using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.AI.KnowledgeBase.SkillRepository;
using NEDA.AI.NaturalLanguage;
using NEDA.API.ClientSDK;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.AI.KnowledgeBase.TemplateStorage.TemplateStorage;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.AI.KnowledgeBase.TemplateStorage;
{
    /// <summary>
    /// Advanced template storage system for managing skill templates, workflow templates, and design patterns;
    /// Supports hierarchical organization, versioning, and intelligent template matching;
    /// </summary>
    public class TemplateStorage : ITemplateStorage, IDisposable;
    {
        private readonly ILogger<TemplateStorage> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly IMemoryCache _memoryCache;
        private readonly AppConfig _appConfig;
        private readonly TemplateRepository _repository;
        private readonly TemplateIndexEngine _indexEngine;
        private readonly TemplateValidationEngine _validationEngine;
        private readonly TemplateRenderer _templateRenderer;
        private readonly TemplateVersionManager _versionManager;
        private readonly SemaphoreSlim _accessLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, TemplateAccessPattern> _accessPatterns;
        private TemplateMetrics _metrics;
        private bool _disposed;
        private bool _isInitialized;

        /// <summary>
        /// Event raised when a new template is stored;
        /// </summary>
        public event EventHandler<TemplateStoredEventArgs> TemplateStored;

        /// <summary>
        /// Event raised when a template is retrieved;
        /// </summary>
        public event EventHandler<TemplateRetrievedEventArgs> TemplateRetrieved;

        /// <summary>
        /// Event raised when a template is versioned;
        /// </summary>
        public event EventHandler<TemplateVersionedEventArgs> TemplateVersioned;

        /// <summary>
        /// Event raised when templates are synchronized;
        /// </summary>
        public event EventHandler<TemplatesSyncedEventArgs> TemplatesSynced;

        /// <summary>
        /// Initializes a new instance of TemplateStorage;
        /// </summary>
        public TemplateStorage(
            ILogger<TemplateStorage> logger,
            IAuditLogger auditLogger,
            IMemoryCache memoryCache,
            IOptions<AppConfig> appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _appConfig = appConfig?.Value ?? throw new ArgumentNullException(nameof(appConfig));

            _repository = new TemplateRepository(logger, _appConfig);
            _indexEngine = new TemplateIndexEngine(logger);
            _validationEngine = new TemplateValidationEngine(logger);
            _templateRenderer = new TemplateRenderer(logger);
            _versionManager = new TemplateVersionManager(logger);

            _accessPatterns = new Dictionary<string, TemplateAccessPattern>();
            _metrics = new TemplateMetrics();

            _logger.LogInformation("TemplateStorage initialized");
        }

        /// <summary>
        /// Initializes the template storage system;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("TemplateStorage already initialized");
                return;
            }

            await _accessLock.WaitAsync();
            try
            {
                _logger.LogInformation("Initializing TemplateStorage system...");

                // Initialize repository;
                await _repository.InitializeAsync();

                // Load system templates;
                await LoadSystemTemplatesAsync();

                // Build indexes;
                await BuildIndexesAsync();

                // Calculate initial metrics;
                _metrics = await CalculateMetricsAsync();

                _isInitialized = true;

                _logger.LogInformation(
                    "TemplateStorage initialized successfully. Loaded {TemplateCount} templates, {CategoryCount} categories",
                    _metrics.TotalTemplates, _metrics.CategoryDistribution.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize TemplateStorage");
                throw new TemplateStorageException("Failed to initialize TemplateStorage",
                    ErrorCodes.TEMPLATE_STORAGE_INIT_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Stores a new template with hierarchical organization;
        /// </summary>
        /// <param name="template">Template to store</param>
        /// <param name="options">Storage options</param>
        /// <returns>Storage result</returns>
        public async Task<TemplateStorageResult> StoreAsync(Template template, TemplateStorageOptions options = null)
        {
            Guard.ThrowIfNull(template, nameof(template));
            options ??= new TemplateStorageOptions();

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            TemplateStorageResult result = null;

            try
            {
                _logger.LogDebug("Storing template: {TemplateId} of type {TemplateType}",
                    template.Id, template.Type);

                // Validate template;
                var validationResult = await ValidateTemplateAsync(template, options);
                if (!validationResult.IsValid)
                {
                    throw new TemplateValidationException(
                        $"Template validation failed: {string.Join(", ", validationResult.Errors)}",
                        template.Id, validationResult.Errors);
                }

                // Check for existing template with same ID;
                var existingTemplate = await _repository.GetAsync(template.Id);
                if (existingTemplate != null)
                {
                    switch (options.ConflictResolution)
                    {
                        case TemplateConflictResolution.Throw:
                            throw new TemplateAlreadyExistsException(
                                $"Template '{template.Id}' already exists", template.Id);

                        case TemplateConflictResolution.Overwrite:
                            await _repository.DeleteAsync(template.Id);
                            break;

                        case TemplateConflictResolution.CreateVersion:
                            template = await CreateNewVersionAsync(existingTemplate, template, options);
                            break;

                        case TemplateConflictResolution.Rename:
                            template.Id = GenerateUniqueTemplateId(template.Id);
                            break;
                    }
                }

                // Enhance template with metadata;
                var enhancedTemplate = await EnhanceTemplateAsync(template, options);

                // Store in repository;
                var storageResult = await _repository.StoreAsync(enhancedTemplate);
                if (!storageResult.Success)
                {
                    throw new TemplateStorageException(
                        $"Failed to store template: {storageResult.ErrorMessage}",
                        template.Id, ErrorCodes.TEMPLATE_STORE_FAILED);
                }

                // Index the template;
                var indexResult = await _indexEngine.IndexAsync(enhancedTemplate);
                if (!indexResult.Success)
                {
                    // Rollback storage if indexing fails;
                    await _repository.DeleteAsync(template.Id);
                    throw new TemplateIndexException(
                        $"Failed to index template: {indexResult.ErrorMessage}",
                        template.Id, ErrorCodes.TEMPLATE_INDEX_FAILED);
                }

                // Update cache;
                UpdateCache(enhancedTemplate);

                // Update version manager;
                _versionManager.RegisterTemplate(enhancedTemplate);

                // Build result;
                result = new TemplateStorageResult;
                {
                    Success = true,
                    TemplateId = enhancedTemplate.Id,
                    StorageId = storageResult.StorageId,
                    Version = enhancedTemplate.Version,
                    Indexed = true,
                    StorageTime = DateTime.UtcNow - startTime,
                    FinalTemplateId = enhancedTemplate.Id;
                };

                // Raise event;
                OnTemplateStored(new TemplateStoredEventArgs;
                {
                    TemplateId = enhancedTemplate.Id,
                    TemplateType = enhancedTemplate.Type,
                    Version = enhancedTemplate.Version,
                    Timestamp = DateTime.UtcNow,
                    Options = options,
                    StorageResult = result;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplateStored",
                    $"Stored template '{enhancedTemplate.Id}' v{enhancedTemplate.Version}",
                    new Dictionary<string, object>
                    {
                        ["templateId"] = enhancedTemplate.Id,
                        ["templateType"] = enhancedTemplate.Type.ToString(),
                        ["templateName"] = enhancedTemplate.Name,
                        ["category"] = enhancedTemplate.Category,
                        ["version"] = enhancedTemplate.Version,
                        ["storageTime"] = result.StorageTime.TotalMilliseconds,
                        ["conflictResolution"] = options.ConflictResolution.ToString()
                    });

                // Update metrics;
                Interlocked.Increment(ref _metrics.TotalTemplates);
                _metrics.LastStorageTime = DateTime.UtcNow;

                _logger.LogInformation(
                    "Template stored successfully: {TemplateId} v{Version} in {StorageTime}ms",
                    enhancedTemplate.Id, enhancedTemplate.Version, result.StorageTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store template: {TemplateId}", template.Id);

                result = new TemplateStorageResult;
                {
                    Success = false,
                    TemplateId = template.Id,
                    ErrorMessage = ex.Message,
                    ErrorCode = ex is TemplateStorageException tse ? tse.ErrorCode : ErrorCodes.TEMPLATE_STORE_FAILED,
                    StorageTime = DateTime.UtcNow - startTime;
                };

                throw new TemplateStorageException($"Failed to store template '{template.Id}'",
                    template.Id, ErrorCodes.TEMPLATE_STORE_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Retrieves a template by ID with optional version;
        /// </summary>
        /// <param name="templateId">Template identifier</param>
        /// <param name="version">Specific version (null for latest)</param>
        /// <param name="options">Retrieval options</param>
        /// <returns>Template retrieval result</returns>
        public async Task<TemplateRetrievalResult> GetAsync(string templateId, int? version = null,
            TemplateRetrievalOptions options = null)
        {
            Guard.ThrowIfNullOrEmpty(templateId, nameof(templateId));
            options ??= new TemplateRetrievalOptions();

            await EnsureInitializedAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogDebug("Retrieving template: {TemplateId} v{Version}", templateId, version);

            try
            {
                // Check cache first;
                if (options.UseCache)
                {
                    var cachedTemplate = await TryGetFromCacheAsync(templateId, version, options);
                    if (cachedTemplate != null)
                    {
                        _logger.LogDebug("Cache hit for template: {TemplateId}", templateId);
                        UpdateAccessPattern(templateId, TemplateOperation.Retrieve);

                        return new TemplateRetrievalResult;
                        {
                            Success = true,
                            Template = cachedTemplate,
                            RetrievedFromCache = true,
                            RetrievalTime = DateTime.UtcNow - startTime;
                        };
                    }
                }

                // Retrieve from repository;
                Template template;
                if (version.HasValue)
                {
                    template = await _repository.GetVersionAsync(templateId, version.Value);
                }
                else;
                {
                    template = await _repository.GetLatestAsync(templateId);
                }

                if (template == null)
                {
                    throw new TemplateNotFoundException($"Template '{templateId}' not found", templateId);
                }

                // Apply transformations if requested;
                if (options.Transformations?.Any() == true)
                {
                    template = await ApplyTransformationsAsync(template, options.Transformations);
                }

                // Validate template if requested;
                if (options.ValidateOnRetrieval)
                {
                    var validationResult = await _validationEngine.ValidateAsync(template);
                    if (!validationResult.IsValid)
                    {
                        _logger.LogWarning("Retrieved template failed validation: {TemplateId}", templateId);

                        if (options.ThrowOnInvalid)
                        {
                            throw new TemplateValidationException(
                                $"Retrieved template validation failed: {string.Join(", ", validationResult.Errors)}",
                                templateId, validationResult.Errors);
                        }
                    }
                }

                // Update cache;
                if (options.CacheResult)
                {
                    UpdateCache(template);
                }

                // Update access patterns;
                UpdateAccessPattern(templateId, TemplateOperation.Retrieve);

                // Update template access statistics;
                template.AccessCount++;
                template.LastAccessed = DateTime.UtcNow;
                await _repository.UpdateAccessInfoAsync(templateId, template.AccessCount, template.LastAccessed);

                // Build result;
                var result = new TemplateRetrievalResult;
                {
                    Success = true,
                    Template = template,
                    RetrievedFromCache = false,
                    RetrievalTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnTemplateRetrieved(new TemplateRetrievedEventArgs;
                {
                    TemplateId = templateId,
                    Version = template.Version,
                    Timestamp = DateTime.UtcNow,
                    Options = options,
                    RetrievalResult = result;
                });

                // Update metrics;
                Interlocked.Increment(ref _metrics.TotalRetrievals);
                _metrics.LastRetrievalTime = DateTime.UtcNow;
                _metrics.AverageRetrievalTime = CalculateMovingAverage(
                    _metrics.AverageRetrievalTime, _metrics.TotalRetrievals, result.RetrievalTime.TotalMilliseconds);

                _logger.LogDebug(
                    "Template retrieved successfully: {TemplateId} v{Version} in {RetrievalTime}ms",
                    templateId, template.Version, result.RetrievalTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve template: {TemplateId}", templateId);
                throw new TemplateRetrievalException(
                    $"Failed to retrieve template '{templateId}'",
                    templateId, ErrorCodes.TEMPLATE_RETRIEVAL_FAILED, ex);
            }
        }

        /// <summary>
        /// Searches for templates based on criteria;
        /// </summary>
        /// <param name="criteria">Search criteria</param>
        /// <param name="options">Search options</param>
        /// <returns>Search results</returns>
        public async Task<TemplateSearchResult> SearchAsync(TemplateSearchCriteria criteria,
            TemplateSearchOptions options = null)
        {
            Guard.ThrowIfNull(criteria, nameof(criteria));
            options ??= new TemplateSearchOptions();

            await EnsureInitializedAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogDebug("Searching templates with criteria: {CriteriaType}", criteria.Type);

            try
            {
                // Check cache for search results;
                if (options.UseCache)
                {
                    var cachedResults = await TryGetSearchFromCacheAsync(criteria, options);
                    if (cachedResults != null)
                    {
                        _logger.LogDebug("Cache hit for search: {CriteriaType}", criteria.Type);
                        return cachedResults;
                    }
                }

                List<Template> templates;

                switch (criteria.Type)
                {
                    case TemplateSearchType.Semantic:
                        templates = await SearchSemanticAsync(criteria, options);
                        break;

                    case TemplateSearchType.Category:
                        templates = await SearchByCategoryAsync(criteria, options);
                        break;

                    case TemplateSearchType.Tag:
                        templates = await SearchByTagAsync(criteria, options);
                        break;

                    case TemplateSearchType.Pattern:
                        templates = await SearchByPatternAsync(criteria, options);
                        break;

                    case TemplateSearchType.Complexity:
                        templates = await SearchByComplexityAsync(criteria, options);
                        break;

                    case TemplateSearchType.Hybrid:
                        templates = await SearchHybridAsync(criteria, options);
                        break;

                    default:
                        templates = await SearchDefaultAsync(criteria, options);
                        break;
                }

                // Apply filters;
                templates = await ApplySearchFiltersAsync(templates, criteria, options);

                // Calculate relevance scores;
                var scoredTemplates = await CalculateSearchRelevanceAsync(templates, criteria, options);

                // Sort results;
                scoredTemplates = SortSearchResults(scoredTemplates, options);

                // Apply pagination;
                var paginatedResults = ApplyPagination(scoredTemplates, options);

                // Build result;
                var result = new TemplateSearchResult;
                {
                    Success = true,
                    Criteria = criteria,
                    Options = options,
                    TotalMatches = scoredTemplates.Count,
                    ReturnedCount = paginatedResults.Count,
                    Templates = paginatedResults,
                    SearchTime = DateTime.UtcNow - startTime;
                };

                // Cache search results;
                if (options.CacheResults)
                {
                    await CacheSearchResultsAsync(criteria, options, result);
                }

                // Update metrics;
                Interlocked.Increment(ref _metrics.TotalSearches);
                _metrics.LastSearchTime = DateTime.UtcNow;

                _logger.LogDebug(
                    "Template search completed: {ResultCount} results in {SearchTime}ms",
                    result.ReturnedCount, result.SearchTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template search failed");
                throw new TemplateSearchException(
                    "Template search failed",
                    ErrorCodes.TEMPLATE_SEARCH_FAILED, ex);
            }
        }

        /// <summary>
        /// Creates a new version of an existing template;
        /// </summary>
        /// <param name="templateId">Template identifier</param>
        /// <param name="newTemplate">New template version</param>
        /// <param name="options">Versioning options</param>
        /// <returns>Versioning result</returns>
        public async Task<TemplateVersionResult> CreateVersionAsync(string templateId, Template newTemplate,
            TemplateVersionOptions options = null)
        {
            Guard.ThrowIfNullOrEmpty(templateId, nameof(templateId));
            Guard.ThrowIfNull(newTemplate, nameof(newTemplate));

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Creating new version for template: {TemplateId}", templateId);

            try
            {
                options ??= new TemplateVersionOptions();

                // Get latest version;
                var latestTemplate = await _repository.GetLatestAsync(templateId);
                if (latestTemplate == null)
                {
                    throw new TemplateNotFoundException($"Template '{templateId}' not found", templateId);
                }

                // Validate version increment;
                if (newTemplate.Version <= latestTemplate.Version)
                {
                    throw new TemplateVersionException(
                        $"New version {newTemplate.Version} must be greater than current version {latestTemplate.Version}",
                        templateId, ErrorCodes.TEMPLATE_VERSION_INVALID);
                }

                // Ensure template ID matches;
                newTemplate.Id = templateId;

                // Copy metadata from latest version;
                newTemplate.CreatedBy = latestTemplate.CreatedBy;
                newTemplate.CreatedAt = latestTemplate.CreatedAt;
                newTemplate.OriginalTemplateId = latestTemplate.OriginalTemplateId ?? templateId;

                // Set version-specific metadata;
                newTemplate.VersionMetadata = new TemplateVersionMetadata;
                {
                    Version = newTemplate.Version,
                    PreviousVersion = latestTemplate.Version,
                    VersionNotes = options.VersionNotes,
                    Changes = await CalculateTemplateChangesAsync(latestTemplate, newTemplate),
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = options.CreatedBy;
                };

                // Store new version;
                var storageResult = await StoreAsync(newTemplate, new TemplateStorageOptions;
                {
                    ConflictResolution = TemplateConflictResolution.CreateVersion,
                    StoreAsNewVersion = true;
                });

                // Update version manager;
                _versionManager.AddVersion(templateId, newTemplate.Version);

                // Build result;
                var result = new TemplateVersionResult;
                {
                    Success = true,
                    TemplateId = templateId,
                    OldVersion = latestTemplate.Version,
                    NewVersion = newTemplate.Version,
                    Changes = newTemplate.VersionMetadata.Changes,
                    VersioningTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnTemplateVersioned(new TemplateVersionedEventArgs;
                {
                    TemplateId = templateId,
                    OldVersion = latestTemplate.Version,
                    NewVersion = newTemplate.Version,
                    Timestamp = DateTime.UtcNow,
                    Options = options,
                    VersionResult = result;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplateVersionCreated",
                    $"Created version {newTemplate.Version} for template '{templateId}'",
                    new Dictionary<string, object>
                    {
                        ["templateId"] = templateId,
                        ["oldVersion"] = latestTemplate.Version,
                        ["newVersion"] = newTemplate.Version,
                        ["changeCount"] = result.Changes.Count,
                        ["versioningTime"] = result.VersioningTime.TotalMilliseconds,
                        ["versionNotes"] = options.VersionNotes;
                    });

                _logger.LogInformation(
                    "Template version created: {TemplateId} v{OldVersion} -> v{NewVersion}",
                    templateId, latestTemplate.Version, newTemplate.Version);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create template version: {TemplateId}", templateId);
                throw new TemplateVersionException(
                    $"Failed to create version for template '{templateId}'",
                    templateId, ErrorCodes.TEMPLATE_VERSION_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Renders a template with provided data;
        /// </summary>
        /// <param name="templateId">Template identifier</param>
        /// <param name="data">Template data</param>
        /// <param name="options">Rendering options</param>
        /// <returns>Rendered template</returns>
        public async Task<TemplateRenderResult> RenderAsync(string templateId, Dictionary<string, object> data,
            TemplateRenderOptions options = null)
        {
            Guard.ThrowIfNullOrEmpty(templateId, nameof(templateId));
            options ??= new TemplateRenderOptions();

            await EnsureInitializedAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogDebug("Rendering template: {TemplateId}", templateId);

            try
            {
                // Get template;
                var templateResult = await GetAsync(templateId, options.Version, new TemplateRetrievalOptions;
                {
                    ValidateOnRetrieval = true,
                    ThrowOnInvalid = true;
                });

                if (!templateResult.Success)
                {
                    throw new TemplateRenderException(
                        $"Failed to retrieve template '{templateId}' for rendering",
                        templateId, ErrorCodes.TEMPLATE_RENDER_RETRIEVAL_FAILED);
                }

                var template = templateResult.Template;

                // Validate data against template schema;
                if (template.Schema != null && options.ValidateData)
                {
                    var validationResult = await ValidateDataAgainstSchemaAsync(data, template.Schema);
                    if (!validationResult.IsValid)
                    {
                        throw new TemplateDataValidationException(
                            $"Template data validation failed: {string.Join(", ", validationResult.Errors)}",
                            templateId, validationResult.Errors);
                    }
                }

                // Apply data transformations;
                var transformedData = await ApplyDataTransformationsAsync(data, template, options);

                // Render template;
                var renderResult = await _templateRenderer.RenderAsync(template, transformedData, options);

                // Post-process rendered content;
                if (options.PostProcessors?.Any() == true)
                {
                    foreach (var processor in options.PostProcessors)
                    {
                        renderResult = await processor.ProcessAsync(renderResult);
                    }
                }

                // Update template usage statistics;
                template.UsageCount++;
                template.LastUsed = DateTime.UtcNow;
                await _repository.UpdateUsageInfoAsync(templateId, template.UsageCount, template.LastUsed);

                // Build result;
                var result = new TemplateRenderResult;
                {
                    Success = true,
                    TemplateId = templateId,
                    TemplateVersion = template.Version,
                    RenderedContent = renderResult.RenderedContent,
                    Metadata = renderResult.Metadata,
                    Warnings = renderResult.Warnings,
                    RenderTime = DateTime.UtcNow - startTime;
                };

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplateRendered",
                    $"Rendered template '{templateId}' v{template.Version}",
                    new Dictionary<string, object>
                    {
                        ["templateId"] = templateId,
                        ["templateVersion"] = template.Version,
                        ["dataFieldCount"] = data.Count,
                        ["renderTime"] = result.RenderTime.TotalMilliseconds,
                        ["contentSize"] = result.RenderedContent?.Length ?? 0;
                    });

                _logger.LogDebug(
                    "Template rendered successfully: {TemplateId} in {RenderTime}ms",
                    templateId, result.RenderTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render template: {TemplateId}", templateId);
                throw new TemplateRenderException(
                    $"Failed to render template '{templateId}'",
                    templateId, ErrorCodes.TEMPLATE_RENDER_FAILED, ex);
            }
        }

        /// <summary>
        /// Exports templates to a portable format;
        /// </summary>
        /// <param name="criteria">Export criteria</param>
        /// <param name="options">Export options</param>
        /// <returns>Exported template package</returns>
        public async Task<TemplateExportPackage> ExportAsync(TemplateExportCriteria criteria,
            TemplateExportOptions options = null)
        {
            Guard.ThrowIfNull(criteria, nameof(criteria));
            options ??= new TemplateExportOptions();

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Exporting templates...");

            try
            {
                // Get templates to export;
                List<Template> templates;
                if (criteria.TemplateIds?.Any() == true)
                {
                    templates = new List<Template>();
                    foreach (var templateId in criteria.TemplateIds)
                    {
                        var template = await _repository.GetLatestAsync(templateId);
                        if (template != null)
                        {
                            templates.Add(template);
                        }
                    }
                }
                else;
                {
                    // Search for templates based on criteria;
                    var searchResult = await SearchAsync(new TemplateSearchCriteria;
                    {
                        Type = criteria.SearchCriteria?.Type ?? TemplateSearchType.Hybrid,
                        Categories = criteria.Categories,
                        Tags = criteria.Tags,
                        MinComplexity = criteria.MinComplexity,
                        MaxComplexity = criteria.MaxComplexity;
                    }, new TemplateSearchOptions;
                    {
                        MaxResults = criteria.MaxResults ?? int.MaxValue;
                    });

                    templates = searchResult.Templates.Select(t => t.Template).ToList();
                }

                // Apply export filters;
                templates = await ApplyExportFiltersAsync(templates, criteria, options);

                // Create export package;
                var package = new TemplateExportPackage;
                {
                    PackageId = Guid.NewGuid().ToString(),
                    ExportTimestamp = DateTime.UtcNow,
                    ExportCriteria = criteria,
                    ExportOptions = options,
                    Templates = templates,
                    Metadata = new ExportMetadata;
                    {
                        TotalTemplates = templates.Count,
                        TotalSize = templates.Sum(t => t.SizeInBytes),
                        ExportFormat = options.Format,
                        CompressionLevel = options.CompressionLevel,
                        IncludeDependencies = options.IncludeDependencies,
                        IncludeMetadata = options.IncludeMetadata,
                        Version = "1.0"
                    }
                };

                // Add dependencies if requested;
                if (options.IncludeDependencies)
                {
                    package.Dependencies = await CollectTemplateDependenciesAsync(templates);
                }

                // Compress if requested;
                if (options.CompressionLevel > 0)
                {
                    package = await CompressPackageAsync(package, options.CompressionLevel);
                }

                // Encrypt if requested;
                if (!string.IsNullOrEmpty(options.EncryptionKey))
                {
                    package = await EncryptPackageAsync(package, options.EncryptionKey);
                }

                // Create manifest;
                package.Manifest = await CreateExportManifestAsync(package);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplatesExported",
                    $"Exported {templates.Count} templates",
                    new Dictionary<string, object>
                    {
                        ["packageId"] = package.PackageId,
                        ["templateCount"] = templates.Count,
                        ["totalSize"] = package.Metadata.TotalSize,
                        ["exportTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds,
                        ["compressed"] = options.CompressionLevel > 0,
                        ["encrypted"] = !string.IsNullOrEmpty(options.EncryptionKey),
                        ["includeDependencies"] = options.IncludeDependencies;
                    });

                _logger.LogInformation(
                    "Template export completed: {TemplateCount} templates exported in {ExportTime}ms",
                    templates.Count, (DateTime.UtcNow - startTime).TotalMilliseconds);

                return package;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template export failed");
                throw new TemplateExportException(
                    "Template export failed",
                    ErrorCodes.TEMPLATE_EXPORT_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Imports templates from a package;
        /// </summary>
        /// <param name="package">Template package to import</param>
        /// <param name="options">Import options</param>
        /// <returns>Import result</returns>
        public async Task<TemplateImportResult> ImportAsync(TemplateExportPackage package,
            TemplateImportOptions options = null)
        {
            Guard.ThrowIfNull(package, nameof(package));
            options ??= new TemplateImportOptions();

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Importing templates from package: {PackageId}", package.PackageId);

            try
            {
                // Validate package;
                var validationResult = await ValidateImportPackageAsync(package);
                if (!validationResult.IsValid)
                {
                    throw new TemplateImportValidationException(
                        $"Import package validation failed: {string.Join(", ", validationResult.Errors)}",
                        validationResult.Errors);
                }

                // Decrypt if needed;
                if (!string.IsNullOrEmpty(options.DecryptionKey))
                {
                    package = await DecryptPackageAsync(package, options.DecryptionKey);
                }

                // Decompress if needed;
                if (package.Metadata.CompressionLevel > 0)
                {
                    package = await DecompressPackageAsync(package);
                }

                var importResults = new List<TemplateImportDetail>();
                var importedCount = 0;
                var skippedCount = 0;
                var failedCount = 0;

                // Import dependencies first;
                if (package.Dependencies?.Any() == true && options.ImportDependencies)
                {
                    foreach (var dependency in package.Dependencies)
                    {
                        try
                        {
                            var dependencyResult = await ImportTemplateAsync(dependency, options);
                            importResults.Add(dependencyResult);

                            if (dependencyResult.Status == TemplateImportStatus.Imported)
                                importedCount++;
                            else if (dependencyResult.Status == TemplateImportStatus.Skipped)
                                skippedCount++;
                            else;
                                failedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to import dependency: {TemplateId}", dependency.Id);
                            failedCount++;
                        }
                    }
                }

                // Import main templates;
                foreach (var template in package.Templates)
                {
                    try
                    {
                        var importResult = await ImportTemplateAsync(template, options);
                        importResults.Add(importResult);

                        if (importResult.Status == TemplateImportStatus.Imported)
                            importedCount++;
                        else if (importResult.Status == TemplateImportStatus.Skipped)
                            skippedCount++;
                        else;
                            failedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to import template: {TemplateId}", template.Id);

                        importResults.Add(new TemplateImportDetail;
                        {
                            TemplateId = template.Id,
                            Status = TemplateImportStatus.Failed,
                            ErrorMessage = ex.Message,
                            Error = ex;
                        });
                        failedCount++;
                    }
                }

                // Rebuild indexes;
                await RebuildIndexesAsync();

                // Build result;
                var result = new TemplateImportResult;
                {
                    Success = importedCount > 0 || skippedCount > 0,
                    PackageId = package.PackageId,
                    ImportedCount = importedCount,
                    SkippedCount = skippedCount,
                    FailedCount = failedCount,
                    ImportDetails = importResults,
                    ImportTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnTemplatesSynced(new TemplatesSyncedEventArgs;
                {
                    PackageId = package.PackageId,
                    ImportResult = result,
                    Timestamp = DateTime.UtcNow,
                    Operation = SyncOperation.Import;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplatesImported",
                    $"Imported {importedCount} templates, skipped {skippedCount}, failed {failedCount}",
                    new Dictionary<string, object>
                    {
                        ["packageId"] = package.PackageId,
                        ["importedCount"] = importedCount,
                        ["skippedCount"] = skippedCount,
                        ["failedCount"] = failedCount,
                        ["importTime"] = result.ImportTime.TotalMilliseconds,
                        ["importDependencies"] = options.ImportDependencies;
                    });

                _logger.LogInformation(
                    "Template import completed: {Imported} imported, {Skipped} skipped, {Failed} failed in {Time}ms",
                    importedCount, skippedCount, failedCount, result.ImportTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template import failed");
                throw new TemplateImportException(
                    "Template import failed",
                    ErrorCodes.TEMPLATE_IMPORT_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Synchronizes templates with external source;
        /// </summary>
        /// <param name="source">Synchronization source</param>
        /// <param name="options">Sync options</param>
        /// <returns>Synchronization result</returns>
        public async Task<TemplateSyncResult> SyncAsync(ITemplateSource source, TemplateSyncOptions options = null)
        {
            Guard.ThrowIfNull(source, nameof(source));
            options ??= new TemplateSyncOptions();

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Synchronizing templates with source: {SourceName}", source.Name);

            try
            {
                // Get templates from source;
                var sourceTemplates = await source.GetTemplatesAsync(options.SyncCriteria);

                var syncResults = new List<TemplateSyncDetail>();
                var addedCount = 0;
                var updatedCount = 0;
                var deletedCount = 0;
                var unchangedCount = 0;
                var failedCount = 0;

                // Sync each template;
                foreach (var sourceTemplate in sourceTemplates)
                {
                    try
                    {
                        var syncResult = await SyncTemplateAsync(sourceTemplate, source, options);
                        syncResults.Add(syncResult);

                        switch (syncResult.Operation)
                        {
                            case SyncOperation.Added:
                                addedCount++;
                                break;
                            case SyncOperation.Updated:
                                updatedCount++;
                                break;
                            case SyncOperation.Deleted:
                                deletedCount++;
                                break;
                            case SyncOperation.Unchanged:
                                unchangedCount++;
                                break;
                            case SyncOperation.Failed:
                                failedCount++;
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync template: {TemplateId}", sourceTemplate.Id);
                        failedCount++;
                    }
                }

                // Handle deletions if configured;
                if (options.SyncDeletions)
                {
                    var deletionResults = await SyncDeletionsAsync(sourceTemplates, source, options);
                    syncResults.AddRange(deletionResults);
                    deletedCount += deletionResults.Count(r => r.Operation == SyncOperation.Deleted);
                }

                // Rebuild indexes if needed;
                if (addedCount > 0 || updatedCount > 0 || deletedCount > 0)
                {
                    await RebuildIndexesAsync();
                }

                // Build result;
                var result = new TemplateSyncResult;
                {
                    Success = failedCount == 0,
                    Source = source.Name,
                    AddedCount = addedCount,
                    UpdatedCount = updatedCount,
                    DeletedCount = deletedCount,
                    UnchangedCount = unchangedCount,
                    FailedCount = failedCount,
                    SyncDetails = syncResults,
                    SyncTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnTemplatesSynced(new TemplatesSyncedEventArgs;
                {
                    Source = source.Name,
                    SyncResult = result,
                    Timestamp = DateTime.UtcNow,
                    Operation = SyncOperation.Sync;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplatesSynced",
                    $"Synced templates with source '{source.Name}'",
                    new Dictionary<string, object>
                    {
                        ["source"] = source.Name,
                        ["added"] = addedCount,
                        ["updated"] = updatedCount,
                        ["deleted"] = deletedCount,
                        ["unchanged"] = unchangedCount,
                        ["failed"] = failedCount,
                        ["syncTime"] = result.SyncTime.TotalMilliseconds,
                        ["syncDeletions"] = options.SyncDeletions;
                    });

                _logger.LogInformation(
                    "Template sync completed: {Added} added, {Updated} updated, {Deleted} deleted, {Failed} failed in {Time}ms",
                    addedCount, updatedCount, deletedCount, failedCount, result.SyncTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template sync failed");
                throw new TemplateSyncException(
                    $"Template sync with source '{source.Name}' failed",
                    ErrorCodes.TEMPLATE_SYNC_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets template metrics and statistics;
        /// </summary>
        /// <returns>Template metrics</returns>
        public async Task<TemplateMetrics> GetMetricsAsync()
        {
            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            try
            {
                // Update metrics with current data;
                _metrics = await CalculateMetricsAsync();
                return _metrics;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Cleans up old or unused templates;
        /// </summary>
        /// <param name="options">Cleanup options</param>
        /// <returns>Cleanup result</returns>
        public async Task<TemplateCleanupResult> CleanupAsync(TemplateCleanupOptions options = null)
        {
            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting template cleanup...");

            try
            {
                options ??= new TemplateCleanupOptions();

                // Get templates eligible for cleanup;
                var eligibleTemplates = await GetTemplatesForCleanupAsync(options);

                if (!eligibleTemplates.Any())
                {
                    _logger.LogInformation("No templates eligible for cleanup");
                    return new TemplateCleanupResult;
                    {
                        Success = true,
                        CleanedCount = 0,
                        TotalSizeFreed = 0,
                        CleanupTime = DateTime.UtcNow - startTime,
                        Message = "No templates eligible for cleanup"
                    };
                }

                var cleanedTemplates = new List<Template>();
                var totalSizeFreed = 0L;

                // Apply cleanup strategies;
                foreach (var template in eligibleTemplates)
                {
                    var shouldCleanup = await ShouldCleanupTemplateAsync(template, options);
                    if (shouldCleanup)
                    {
                        // Delete template;
                        await _repository.DeleteAsync(template.Id);
                        await _indexEngine.RemoveFromIndexAsync(template.Id);

                        // Remove from cache;
                        RemoveFromCache(template.Id);

                        // Remove from version manager;
                        _versionManager.RemoveTemplate(template.Id);

                        cleanedTemplates.Add(template);
                        totalSizeFreed += template.SizeInBytes;

                        _logger.LogDebug("Cleaned up template: {TemplateId}", template.Id);
                    }
                }

                // Build result;
                var result = new TemplateCleanupResult;
                {
                    Success = true,
                    CleanedCount = cleanedTemplates.Count,
                    CleanedTemplates = cleanedTemplates,
                    TotalSizeFreed = totalSizeFreed,
                    CleanupTime = DateTime.UtcNow - startTime;
                };

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "TemplatesCleanedUp",
                    $"Cleaned up {cleanedTemplates.Count} templates, freed {totalSizeFreed} bytes",
                    new Dictionary<string, object>
                    {
                        ["cleanedCount"] = cleanedTemplates.Count,
                        ["sizeFreed"] = totalSizeFreed,
                        ["cleanupTime"] = result.CleanupTime.TotalMilliseconds,
                        ["cleanupStrategy"] = options.Strategy.ToString()
                    });

                // Update metrics;
                Interlocked.Add(ref _metrics.TotalCleanedTemplates, cleanedTemplates.Count);
                _metrics.TotalSizeFreed += totalSizeFreed;
                _metrics.LastCleanupTime = DateTime.UtcNow;

                _logger.LogInformation(
                    "Template cleanup completed: {CleanedCount} templates cleaned, {SizeFreed} bytes freed in {Time}ms",
                    cleanedTemplates.Count, totalSizeFreed, result.CleanupTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template cleanup failed");
                throw new TemplateCleanupException(
                    "Template cleanup failed",
                    ErrorCodes.TEMPLATE_CLEANUP_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        #region Private Helper Methods;

        private async Task EnsureInitializedAsync()
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }
        }

        private async Task LoadSystemTemplatesAsync()
        {
            try
            {
                _logger.LogInformation("Loading system templates...");

                // Load built-in system templates;
                var systemTemplates = new List<Template>
                {
                    CreateSkillTemplate(),
                    CreateWorkflowTemplate(),
                    CreateDataProcessorTemplate(),
                    CreateAPIClientTemplate(),
                    CreateValidationTemplate(),
                    CreateReportTemplate(),
                    CreateNotificationTemplate(),
                    CreateScheduledTaskTemplate()
                };

                foreach (var template in systemTemplates)
                {
                    try
                    {
                        // Check if template already exists;
                        var existing = await _repository.GetAsync(template.Id);
                        if (existing == null)
                        {
                            await StoreAsync(template, new TemplateStorageOptions;
                            {
                                ConflictResolution = TemplateConflictResolution.Throw,
                                IsSystemTemplate = true;
                            });
                            _logger.LogDebug("Loaded system template: {TemplateId}", template.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load system template: {TemplateId}", template.Id);
                    }
                }

                _logger.LogInformation("Loaded {Count} system templates", systemTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load system templates");
                throw;
            }
        }

        private async Task BuildIndexesAsync()
        {
            var allTemplates = await _repository.GetAllAsync();
            await _indexEngine.BuildIndexesAsync(allTemplates);
        }

        private async Task RebuildIndexesAsync()
        {
            var allTemplates = await _repository.GetAllAsync();
            await _indexEngine.RebuildIndexesAsync(allTemplates);
        }

        private async Task<TemplateValidationResult> ValidateTemplateAsync(Template template, TemplateStorageOptions options)
        {
            var result = new TemplateValidationResult;
            {
                TemplateId = template.Id,
                ValidatedAt = DateTime.UtcNow;
            };

            // Required fields validation;
            if (string.IsNullOrEmpty(template.Id))
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "TEMPLATE_ID_REQUIRED",
                    Message = "Template ID is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            if (string.IsNullOrEmpty(template.Name))
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "TEMPLATE_NAME_REQUIRED",
                    Message = "Template name is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            if (template.Type == TemplateType.Unknown)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "TEMPLATE_TYPE_REQUIRED",
                    Message = "Template type is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Content validation;
            if (template.Content == null || !template.Content.Any())
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "TEMPLATE_CONTENT_REQUIRED",
                    Message = "Template content is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Schema validation if present;
            if (template.Schema != null)
            {
                var schemaValidation = await _validationEngine.ValidateSchemaAsync(template.Schema);
                if (!schemaValidation.IsValid)
                {
                    result.Errors.AddRange(schemaValidation.Errors);
                }
            }

            // Complexity validation;
            if (template.Complexity < 1 || template.Complexity > 10)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "TEMPLATE_COMPLEXITY_INVALID",
                    Message = "Template complexity must be between 1 and 10",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Custom validation rules;
            if (options.ValidationRules?.Any() == true)
            {
                foreach (var rule in options.ValidationRules)
                {
                    var ruleResult = await rule.ValidateAsync(template);
                    if (!ruleResult.IsValid)
                    {
                        result.Errors.AddRange(ruleResult.Errors);
                    }
                }
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);
            return result;
        }

        private async Task<Template> EnhanceTemplateAsync(Template template, TemplateStorageOptions options)
        {
            var enhancedTemplate = template.Clone();

            // Add metadata;
            enhancedTemplate.CreatedAt = DateTime.UtcNow;
            enhancedTemplate.UpdatedAt = DateTime.UtcNow;
            enhancedTemplate.CreatedBy = options.CreatedBy ?? "system";
            enhancedTemplate.IsSystemTemplate = options.IsSystemTemplate;
            enhancedTemplate.StorageOptions = options;

            // Set version if not provided;
            if (enhancedTemplate.Version <= 0)
            {
                enhancedTemplate.Version = 1;
            }

            // Generate original template ID for versioning;
            if (string.IsNullOrEmpty(enhancedTemplate.OriginalTemplateId))
            {
                enhancedTemplate.OriginalTemplateId = enhancedTemplate.Id;
            }

            // Calculate size;
            enhancedTemplate.SizeInBytes = CalculateTemplateSize(enhancedTemplate);

            // Generate embeddings for semantic search;
            enhancedTemplate.Embeddings = await GenerateTemplateEmbeddingsAsync(enhancedTemplate);

            // Extract key features;
            enhancedTemplate.KeyFeatures = await ExtractKeyFeaturesAsync(enhancedTemplate);

            // Calculate default values;
            if (enhancedTemplate.DefaultValues == null)
            {
                enhancedTemplate.DefaultValues = await CalculateDefaultValuesAsync(enhancedTemplate);
            }

            return enhancedTemplate;
        }

        private async Task<Template> CreateNewVersionAsync(Template existingTemplate, Template newTemplate,
            TemplateStorageOptions options)
        {
            // Calculate new version number;
            var newVersion = existingTemplate.Version + 1;

            // Create versioned template;
            var versionedTemplate = newTemplate.Clone();
            versionedTemplate.Id = $"{existingTemplate.Id}_v{newVersion}";
            versionedTemplate.Version = newVersion;
            versionedTemplate.OriginalTemplateId = existingTemplate.OriginalTemplateId;
            versionedTemplate.PreviousVersionId = existingTemplate.Id;

            // Copy metadata from existing template;
            versionedTemplate.CreatedAt = existingTemplate.CreatedAt;
            versionedTemplate.CreatedBy = existingTemplate.CreatedBy;
            versionedTemplate.IsSystemTemplate = existingTemplate.IsSystemTemplate;

            // Add version metadata;
            versionedTemplate.VersionMetadata = new TemplateVersionMetadata;
            {
                Version = newVersion,
                PreviousVersion = existingTemplate.Version,
                VersionNotes = options.VersionNotes,
                Changes = await CalculateTemplateChangesAsync(existingTemplate, newTemplate),
                CreatedAt = DateTime.UtcNow,
                CreatedBy = options.CreatedBy;
            };

            return versionedTemplate;
        }

        private string GenerateUniqueTemplateId(string baseId)
        {
            var counter = 1;
            var newId = $"{baseId}_{counter}";

            while (_repository.Exists(newId))
            {
                counter++;
                newId = $"{baseId}_{counter}";
            }

            return newId;
        }

        private void UpdateCache(Template template)
        {
            var cacheKey = GetCacheKey(template.Id, template.Version);
            var cacheOptions = new MemoryCacheEntryOptions;
            {
                SlidingExpiration = TimeSpan.FromMinutes(_appConfig.TemplateSettings.CacheDurationMinutes),
                Size = template.SizeInBytes;
            };

            _memoryCache.Set(cacheKey, template, cacheOptions);
        }

        private void RemoveFromCache(string templateId)
        {
            // Remove all versions from cache;
            var cacheKeys = GetCacheKeysForTemplate(templateId);
            foreach (var cacheKey in cacheKeys)
            {
                _memoryCache.Remove(cacheKey);
            }
        }

        private async Task<Template> TryGetFromCacheAsync(string templateId, int? version, TemplateRetrievalOptions options)
        {
            if (version.HasValue)
            {
                var cacheKey = GetCacheKey(templateId, version.Value);
                if (_memoryCache.TryGetValue(cacheKey, out Template cachedTemplate))
                {
                    return cachedTemplate;
                }
            }
            else;
            {
                // Try to get latest version from cache;
                var versions = _versionManager.GetVersions(templateId);
                if (versions.Any())
                {
                    var latestVersion = versions.Max();
                    var cacheKey = GetCacheKey(templateId, latestVersion);
                    if (_memoryCache.TryGetValue(cacheKey, out Template cachedTemplate))
                    {
                        return cachedTemplate;
                    }
                }
            }

            return null;
        }

        private async Task<TemplateSearchResult> TryGetSearchFromCacheAsync(TemplateSearchCriteria criteria,
            TemplateSearchOptions options)
        {
            var cacheKey = GenerateSearchCacheKey(criteria, options);
            if (_memoryCache.TryGetValue(cacheKey, out TemplateSearchResult cachedResults))
            {
                return cachedResults;
            }

            return null;
        }

        private async Task CacheSearchResultsAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options,
            TemplateSearchResult results)
        {
            var cacheKey = GenerateSearchCacheKey(criteria, options);
            var cacheOptions = new MemoryCacheEntryOptions;
            {
                SlidingExpiration = TimeSpan.FromMinutes(_appConfig.TemplateSettings.SearchCacheDurationMinutes),
                Size = results.Templates.Sum(t => t.Template.SizeInBytes)
            };

            _memoryCache.Set(cacheKey, results, cacheOptions);
        }

        private string GetCacheKey(string templateId, int version)
        {
            return $"template_{templateId}_v{version}";
        }

        private List<string> GetCacheKeysForTemplate(string templateId)
        {
            var versions = _versionManager.GetVersions(templateId);
            return versions.Select(v => GetCacheKey(templateId, v)).ToList();
        }

        private string GenerateSearchCacheKey(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            return $"search_{criteria.GetHashCode()}_{options.GetHashCode()}";
        }

        private void UpdateAccessPattern(string templateId, TemplateOperation operation)
        {
            if (!_accessPatterns.ContainsKey(templateId))
            {
                _accessPatterns[templateId] = new TemplateAccessPattern;
                {
                    TemplateId = templateId,
                    Operation = operation;
                };
            }

            var pattern = _accessPatterns[templateId];
            pattern.AccessCount++;
            pattern.LastAccessed = DateTime.UtcNow;
            pattern.AverageAccessInterval = CalculateMovingAverage(
                pattern.AverageAccessInterval, pattern.AccessCount,
                (DateTime.UtcNow - pattern.LastAccessed).TotalSeconds);
        }

        private async Task<List<Template>> SearchSemanticAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            // Use embeddings for semantic search;
            var queryEmbedding = await GenerateEmbeddingForQueryAsync(criteria);
            return await _indexEngine.SearchBySimilarityAsync(queryEmbedding, options.MaxResults);
        }

        private async Task<List<Template>> SearchByCategoryAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            return await _indexEngine.SearchByCategoryAsync(criteria.Categories, options.MaxResults);
        }

        private async Task<List<Template>> SearchByTagAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            return await _indexEngine.SearchByTagsAsync(criteria.Tags, options.MaxResults);
        }

        private async Task<List<Template>> SearchByPatternAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            var pattern = criteria.Parameters?.GetValueOrDefault("pattern") as TemplatePattern;
            if (pattern != null)
            {
                return await _indexEngine.SearchByPatternAsync(pattern, options.MaxResults);
            }

            return new List<Template>();
        }

        private async Task<List<Template>> SearchByComplexityAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            return await _indexEngine.SearchByComplexityAsync(
                criteria.MinComplexity ?? 1,
                criteria.MaxComplexity ?? 10,
                options.MaxResults);
        }

        private async Task<List<Template>> SearchHybridAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            // Combine multiple search strategies;
            var results = new List<Template>();

            if (criteria.Keywords?.Any() == true)
            {
                var keywordResults = await _indexEngine.SearchByKeywordsAsync(criteria.Keywords, options.MaxResults / 2);
                results.AddRange(keywordResults);
            }

            if (criteria.Categories?.Any() == true)
            {
                var categoryResults = await _indexEngine.SearchByCategoryAsync(criteria.Categories, options.MaxResults / 2);
                results.AddRange(categoryResults);
            }

            // Remove duplicates;
            return results.DistinctBy(t => t.Id).ToList();
        }

        private async Task<List<Template>> SearchDefaultAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            // Default search: use keyword search;
            if (criteria.Keywords?.Any() == true)
            {
                return await _indexEngine.SearchByKeywordsAsync(criteria.Keywords, options.MaxResults);
            }

            // If no keywords, return recent templates;
            return await _indexEngine.SearchRecentAsync(options.MaxResults);
        }

        private async Task<List<Template>> ApplySearchFiltersAsync(List<Template> templates,
            TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            var filteredTemplates = templates;

            // Apply date filters;
            if (criteria.MinDate.HasValue)
            {
                filteredTemplates = filteredTemplates;
                    .Where(t => t.UpdatedAt >= criteria.MinDate.Value)
                    .ToList();
            }

            if (criteria.MaxDate.HasValue)
            {
                filteredTemplates = filteredTemplates;
                    .Where(t => t.UpdatedAt <= criteria.MaxDate.Value)
                    .ToList();
            }

            // Apply complexity filters;
            if (criteria.MinComplexity.HasValue)
            {
                filteredTemplates = filteredTemplates;
                    .Where(t => t.Complexity >= criteria.MinComplexity.Value)
                    .ToList();
            }

            if (criteria.MaxComplexity.HasValue)
            {
                filteredTemplates = filteredTemplates;
                    .Where(t => t.Complexity <= criteria.MaxComplexity.Value)
                    .ToList();
            }

            // Apply custom filters;
            if (criteria.CustomFilters?.Any() == true)
            {
                foreach (var filter in criteria.CustomFilters)
                {
                    filteredTemplates = await filter.ApplyAsync(filteredTemplates);
                }
            }

            return filteredTemplates;
        }

        private async Task<List<ScoredTemplate>> CalculateSearchRelevanceAsync(List<Template> templates,
            TemplateSearchCriteria criteria, TemplateSearchOptions options)
        {
            var scoredTemplates = new List<ScoredTemplate>();

            foreach (var template in templates)
            {
                var score = await CalculateRelevanceScoreAsync(template, criteria, options);

                scoredTemplates.Add(new ScoredTemplate;
                {
                    Template = template,
                    RelevanceScore = score,
                    ScoreBreakdown = await CalculateScoreBreakdownAsync(template, criteria, score)
                });
            }

            return scoredTemplates;
        }

        private List<ScoredTemplate> SortSearchResults(List<ScoredTemplate> templates, TemplateSearchOptions options)
        {
            switch (options.SortOrder)
            {
                case TemplateSortOrder.Relevance:
                    return templates.OrderByDescending(t => t.RelevanceScore).ToList();

                case TemplateSortOrder.Name:
                    return templates.OrderBy(t => t.Template.Name).ToList();

                case TemplateSortOrder.Date:
                    return templates.OrderByDescending(t => t.Template.UpdatedAt).ToList();

                case TemplateSortOrder.Complexity:
                    return templates.OrderBy(t => t.Template.Complexity).ToList();

                case TemplateSortOrder.Popularity:
                    return templates.OrderByDescending(t => t.Template.UsageCount).ToList();

                default:
                    return templates;
            }
        }

        private List<ScoredTemplate> ApplyPagination(List<ScoredTemplate> templates, TemplateSearchOptions options)
        {
            if (options.PageSize <= 0)
                return templates;

            var startIndex = (options.PageNumber - 1) * options.PageSize;
            if (startIndex >= templates.Count)
                return new List<ScoredTemplate>();

            return templates;
                .Skip(startIndex)
                .Take(options.PageSize)
                .ToList();
        }

        private async Task<TemplateImportDetail> ImportTemplateAsync(Template template, TemplateImportOptions options)
        {
            try
            {
                // Check if template already exists;
                var existingTemplate = await _repository.GetAsync(template.Id);

                if (existingTemplate != null)
                {
                    switch (options.ConflictResolution)
                    {
                        case ImportConflictResolution.Skip:
                            return new TemplateImportDetail;
                            {
                                TemplateId = template.Id,
                                Status = TemplateImportStatus.Skipped,
                                Reason = "Template already exists",
                                ExistingVersion = existingTemplate.Version,
                                ImportedVersion = template.Version;
                            };

                        case ImportConflictResolution.Overwrite:
                            // Delete existing template;
                            await _repository.DeleteAsync(template.Id);
                            await _indexEngine.RemoveFromIndexAsync(template.Id);
                            RemoveFromCache(template.Id);
                            break;

                        case ImportConflictResolution.Merge:
                            // Merge templates;
                            template = await MergeTemplatesAsync(existingTemplate, template);
                            break;

                        case ImportConflictResolution.CreateVersion:
                            // Create new version;
                            template = await CreateNewVersionAsync(existingTemplate, template,
                                new TemplateStorageOptions { ConflictResolution = TemplateConflictResolution.CreateVersion });
                            break;
                    }
                }

                // Import template;
                var storageResult = await StoreAsync(template, new TemplateStorageOptions;
                {
                    ConflictResolution = TemplateConflictResolution.Overwrite,
                    CreatedBy = options.CreatedBy ?? "import",
                    IsSystemTemplate = template.IsSystemTemplate;
                });

                return new TemplateImportDetail;
                {
                    TemplateId = template.Id,
                    Status = TemplateImportStatus.Imported,
                    Version = template.Version,
                    Action = existingTemplate != null ? "Overwritten" : "Added",
                    StorageResult = storageResult;
                };
            }
            catch (Exception ex)
            {
                return new TemplateImportDetail;
                {
                    TemplateId = template.Id,
                    Status = TemplateImportStatus.Failed,
                    ErrorMessage = ex.Message,
                    Error = ex;
                };
            }
        }

        private async Task<TemplateSyncDetail> SyncTemplateAsync(Template sourceTemplate, ITemplateSource source,
            TemplateSyncOptions options)
        {
            var existingTemplate = await _repository.GetAsync(sourceTemplate.Id);

            if (existingTemplate == null)
            {
                // Add new template;
                await StoreAsync(sourceTemplate, new TemplateStorageOptions;
                {
                    ConflictResolution = TemplateConflictResolution.Throw,
                    CreatedBy = source.Name,
                    IsSystemTemplate = false;
                });

                return new TemplateSyncDetail;
                {
                    TemplateId = sourceTemplate.Id,
                    Operation = SyncOperation.Added,
                    Timestamp = DateTime.UtcNow;
                };
            }
            else;
            {
                // Check if template needs update;
                var needsUpdate = await ShouldUpdateTemplateAsync(existingTemplate, sourceTemplate, options);

                if (needsUpdate)
                {
                    // Update template;
                    await StoreAsync(sourceTemplate, new TemplateStorageOptions;
                    {
                        ConflictResolution = TemplateConflictResolution.Overwrite,
                        CreatedBy = source.Name;
                    });

                    return new TemplateSyncDetail;
                    {
                        TemplateId = sourceTemplate.Id,
                        Operation = SyncOperation.Updated,
                        Timestamp = DateTime.UtcNow,
                        Changes = await CalculateTemplateChangesAsync(existingTemplate, sourceTemplate)
                    };
                }
                else;
                {
                    return new TemplateSyncDetail;
                    {
                        TemplateId = sourceTemplate.Id,
                        Operation = SyncOperation.Unchanged,
                        Timestamp = DateTime.UtcNow;
                    };
                }
            }
        }

        private async Task<List<TemplateSyncDetail>> SyncDeletionsAsync(List<Template> sourceTemplates,
            ITemplateSource source, TemplateSyncOptions options)
        {
            var sourceTemplateIds = sourceTemplates.Select(t => t.Id).ToHashSet();
            var allTemplateIds = (await _repository.GetAllAsync()).Select(t => t.Id).ToList();

            var deletionDetails = new List<TemplateSyncDetail>();

            foreach (var templateId in allTemplateIds)
            {
                // Check if template exists in source;
                if (!sourceTemplateIds.Contains(templateId))
                {
                    // Check if template should be deleted;
                    var template = await _repository.GetAsync(templateId);
                    if (template != null && await ShouldDeleteTemplateAsync(template, source, options))
                    {
                        // Delete template;
                        await _repository.DeleteAsync(templateId);
                        await _indexEngine.RemoveFromIndexAsync(templateId);
                        RemoveFromCache(templateId);

                        deletionDetails.Add(new TemplateSyncDetail;
                        {
                            TemplateId = templateId,
                            Operation = SyncOperation.Deleted,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }
            }

            return deletionDetails;
        }

        private async Task<bool> ShouldCleanupTemplateAsync(Template template, TemplateCleanupOptions options)
        {
            switch (options.Strategy)
            {
                case CleanupStrategy.AgeBased:
                    return (DateTime.UtcNow - template.LastAccessed).TotalDays > options.MaxAgeDays;

                case CleanupStrategy.UsageBased:
                    return template.UsageCount < options.MinUsageCount;

                case CleanupStrategy.SizeBased:
                    return template.SizeInBytes > options.MaxTemplateSize;

                case CleanupStrategy.ComplexityBased:
                    return template.Complexity < options.MinComplexity;

                case CleanupStrategy.Combined:
                    var ageScore = (DateTime.UtcNow - template.LastAccessed).TotalDays / options.MaxAgeDays;
                    var usageScore = 1 - ((double)template.UsageCount / options.MinUsageCount);
                    var sizeScore = (double)template.SizeInBytes / options.MaxTemplateSize;
                    var complexityScore = 1 - ((double)template.Complexity / options.MinComplexity);

                    var combinedScore = (ageScore + usageScore + sizeScore + complexityScore) / 4;
                    return combinedScore > options.CleanupThreshold;

                default:
                    return false;
            }
        }

        private async Task<TemplateMetrics> CalculateMetricsAsync()
        {
            var allTemplates = await _repository.GetAllAsync();

            return new TemplateMetrics;
            {
                TotalTemplates = allTemplates.Count,
                TotalSizeBytes = allTemplates.Sum(t => t.SizeInBytes),
                AverageTemplateSize = allTemplates.Average(t => t.SizeInBytes),
                TemplateTypeDistribution = allTemplates.GroupBy(t => t.Type)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                CategoryDistribution = allTemplates.GroupBy(t => t.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ComplexityDistribution = allTemplates.GroupBy(t => Math.Round(t.Complexity))
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                AverageComplexity = allTemplates.Average(t => t.Complexity),
                TotalRetrievals = _metrics.TotalRetrievals,
                TotalSearches = _metrics.TotalSearches,
                TotalRenders = _metrics.TotalRenders,
                TotalCleanedTemplates = _metrics.TotalCleanedTemplates,
                TotalSizeFreed = _metrics.TotalSizeFreed,
                CacheHitRate = await CalculateCacheHitRateAsync(),
                AverageRetrievalTime = _metrics.AverageRetrievalTime,
                AverageSearchTime = _metrics.AverageSearchTime,
                AverageRenderTime = _metrics.AverageRenderTime,
                LastStorageTime = _metrics.LastStorageTime,
                LastRetrievalTime = _metrics.LastRetrievalTime,
                LastSearchTime = _metrics.LastSearchTime,
                LastRenderTime = _metrics.LastRenderTime,
                LastCleanupTime = _metrics.LastCleanupTime,
                IndexCount = _indexEngine.GetIndexCount(),
                CalculatedAt = DateTime.UtcNow;
            };
        }

        private double CalculateMovingAverage(double currentAverage, long count, double newValue)
        {
            return (currentAverage * (count - 1) + newValue) / count;
        }

        private Template CreateSkillTemplate()
        {
            return new Template;
            {
                Id = "system_skill_template",
                Name = "Skill Template",
                Description = "Base template for creating AI skills",
                Type = TemplateType.Skill,
                Category = "AI/ML",
                Complexity = 5,
                Content = new Dictionary<string, object>
                {
                    ["name"] = "{{skill_name}}",
                    ["description"] = "{{skill_description}}",
                    ["category"] = "{{skill_category}}",
                    ["parameters"] = "{{skill_parameters}}",
                    ["execution_logic"] = "{{execution_logic}}"
                },
                Schema = new TemplateSchema;
                {
                    Properties = new Dictionary<string, TemplateSchemaProperty>
                    {
                        ["skill_name"] = new() { Type = SchemaPropertyType.String, Required = true },
                        ["skill_description"] = new() { Type = SchemaPropertyType.String, Required = true },
                        ["skill_category"] = new() { Type = SchemaPropertyType.String, Required = true },
                        ["skill_parameters"] = new() { Type = SchemaPropertyType.Array, Required = false },
                        ["execution_logic"] = new() { Type = SchemaPropertyType.Object, Required = true }
                    }
                },
                Tags = new List<string> { "skill", "ai", "template", "system" },
                IsSystemTemplate = true;
            };
        }

        // Other system template creation methods...

        #endregion;

        #region Event Handlers;

        protected virtual void OnTemplateStored(TemplateStoredEventArgs e)
        {
            TemplateStored?.Invoke(this, e);
        }

        protected virtual void OnTemplateRetrieved(TemplateRetrievedEventArgs e)
        {
            TemplateRetrieved?.Invoke(this, e);
        }

        protected virtual void OnTemplateVersioned(TemplateVersionedEventArgs e)
        {
            TemplateVersioned?.Invoke(this, e);
        }

        protected virtual void OnTemplatesSynced(TemplatesSyncedEventArgs e)
        {
            TemplatesSynced?.Invoke(this, e);
        }

        #endregion;

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose implementation;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _accessLock?.Dispose();
                    _repository?.Dispose();
                    _indexEngine?.Dispose();
                    _validationEngine?.Dispose();
                    _templateRenderer?.Dispose();
                    _versionManager?.Dispose();
                }
                _disposed = true;
            }
        }

        #region Supporting Classes;

        /// <summary>
        /// Template storage interface;
        /// </summary>
        public interface ITemplateStorage : IDisposable
        {
            Task InitializeAsync();
            Task<TemplateStorageResult> StoreAsync(Template template, TemplateStorageOptions options);
            Task<TemplateRetrievalResult> GetAsync(string templateId, int? version, TemplateRetrievalOptions options);
            Task<TemplateSearchResult> SearchAsync(TemplateSearchCriteria criteria, TemplateSearchOptions options);
            Task<TemplateVersionResult> CreateVersionAsync(string templateId, Template newTemplate, TemplateVersionOptions options);
            Task<TemplateRenderResult> RenderAsync(string templateId, Dictionary<string, object> data, TemplateRenderOptions options);
            Task<TemplateExportPackage> ExportAsync(TemplateExportCriteria criteria, TemplateExportOptions options);
            Task<TemplateImportResult> ImportAsync(TemplateExportPackage package, TemplateImportOptions options);
            Task<TemplateSyncResult> SyncAsync(ITemplateSource source, TemplateSyncOptions options);
            Task<TemplateMetrics> GetMetricsAsync();
            Task<TemplateCleanupResult> CleanupAsync(TemplateCleanupOptions options);

            event EventHandler<TemplateStoredEventArgs> TemplateStored;
            event EventHandler<TemplateRetrievedEventArgs> TemplateRetrieved;
            event EventHandler<TemplateVersionedEventArgs> TemplateVersioned;
            event EventHandler<TemplatesSyncedEventArgs> TemplatesSynced;
        }

        /// <summary>
        /// Template representing a reusable pattern or structure;
        /// </summary>
        public class Template : ICloneable;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public TemplateType Type { get; set; }
            public string Category { get; set; }
            public int Complexity { get; set; } // 1-10 scale;
            public Dictionary<string, object> Content { get; set; } = new Dictionary<string, object>();
            public TemplateSchema Schema { get; set; }
            public Dictionary<string, object> DefaultValues { get; set; } = new Dictionary<string, object>();
            public List<string> Tags { get; set; } = new List<string>();
            public TemplateEmbedding Embeddings { get; set; }
            public List<string> KeyFeatures { get; set; } = new List<string>();
            public List<TemplateDependency> Dependencies { get; set; } = new List<TemplateDependency>();
            public TemplateVersionMetadata VersionMetadata { get; set; }
            public string CreatedBy { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public DateTime LastUsed { get; set; }
            public int AccessCount { get; set; }
            public int UsageCount { get; set; }
            public int Version { get; set; }
            public string OriginalTemplateId { get; set; }
            public string PreviousVersionId { get; set; }
            public bool IsSystemTemplate { get; set; }
            public TemplateStorageOptions StorageOptions { get; set; }
            public long SizeInBytes { get; set; }

            public object Clone()
            {
                return new Template;
                {
                    Id = Id,
                    Name = Name,
                    Description = Description,
                    Type = Type,
                    Category = Category,
                    Complexity = Complexity,
                    Content = new Dictionary<string, object>(Content),
                    Schema = (TemplateSchema)Schema?.Clone(),
                    DefaultValues = new Dictionary<string, object>(DefaultValues),
                    Tags = new List<string>(Tags),
                    Embeddings = (TemplateEmbedding)Embeddings?.Clone(),
                    KeyFeatures = new List<string>(KeyFeatures),
                    Dependencies = Dependencies.Select(d => (TemplateDependency)d.Clone()).ToList(),
                    VersionMetadata = (TemplateVersionMetadata)VersionMetadata?.Clone(),
                    CreatedBy = CreatedBy,
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                    LastAccessed = LastAccessed,
                    LastUsed = LastUsed,
                    AccessCount = AccessCount,
                    UsageCount = UsageCount,
                    Version = Version,
                    OriginalTemplateId = OriginalTemplateId,
                    PreviousVersionId = PreviousVersionId,
                    IsSystemTemplate = IsSystemTemplate,
                    StorageOptions = (TemplateStorageOptions)StorageOptions?.Clone(),
                    SizeInBytes = SizeInBytes;
                };
            }
        }

        /// <summary>
        /// Template schema for validation;
        /// </summary>
        public class TemplateSchema : ICloneable;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public Dictionary<string, TemplateSchemaProperty> Properties { get; set; } = new Dictionary<string, TemplateSchemaProperty>();
            public List<TemplateValidationRule> ValidationRules { get; set; } = new List<TemplateValidationRule>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

            public object Clone()
            {
                return new TemplateSchema;
                {
                    Name = Name,
                    Description = Description,
                    Properties = Properties.ToDictionary(p => p.Key, p => (TemplateSchemaProperty)p.Value.Clone()),
                    ValidationRules = ValidationRules.Select(r => (TemplateValidationRule)r.Clone()).ToList(),
                    Metadata = new Dictionary<string, object>(Metadata)
                };
            }
        }

        /// <summary>
        /// Template storage options;
        /// </summary>
        public class TemplateStorageOptions : ICloneable;
        {
            public TemplateConflictResolution ConflictResolution { get; set; } = TemplateConflictResolution.Throw;
            public bool StoreAsNewVersion { get; set; } = false;
            public bool ValidateBeforeStore { get; set; } = true;
            public bool IndexAfterStore { get; set; } = true;
            public bool CacheAfterStore { get; set; } = true;
            public string CreatedBy { get; set; } = "system";
            public bool IsSystemTemplate { get; set; } = false;
            public string VersionNotes { get; set; }
            public List<ITemplateValidationRule> ValidationRules { get; set; } = new List<ITemplateValidationRule>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

            public object Clone()
            {
                return new TemplateStorageOptions;
                {
                    ConflictResolution = ConflictResolution,
                    StoreAsNewVersion = StoreAsNewVersion,
                    ValidateBeforeStore = ValidateBeforeStore,
                    IndexAfterStore = IndexAfterStore,
                    CacheAfterStore = CacheAfterStore,
                    CreatedBy = CreatedBy,
                    IsSystemTemplate = IsSystemTemplate,
                    VersionNotes = VersionNotes,
                    ValidationRules = new List<ITemplateValidationRule>(ValidationRules),
                    Metadata = new Dictionary<string, object>(Metadata)
                };
            }
        }

        /// <summary>
        /// Template retrieval options;
        /// </summary>
        public class TemplateRetrievalOptions;
        {
            public bool UseCache { get; set; } = true;
            public bool CacheResult { get; set; } = true;
            public bool ValidateOnRetrieval { get; set; } = false;
            public bool ThrowOnInvalid { get; set; } = false;
            public List<ITemplateTransformation> Transformations { get; set; } = new List<ITemplateTransformation>();
            public Dictionary<string, object> AdditionalMetadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template search criteria;
        /// </summary>
        public class TemplateSearchCriteria;
        {
            public TemplateSearchType Type { get; set; } = TemplateSearchType.Hybrid;
            public List<string> Keywords { get; set; } = new List<string>();
            public List<string> Categories { get; set; } = new List<string>();
            public List<string> Tags { get; set; } = new List<string>();
            public int? MinComplexity { get; set; }
            public int? MaxComplexity { get; set; }
            public DateTime? MinDate { get; set; }
            public DateTime? MaxDate { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public List<ITemplateFilter> CustomFilters { get; set; } = new List<ITemplateFilter>();
        }

        /// <summary>
        /// Template search options;
        /// </summary>
        public class TemplateSearchOptions;
        {
            public int MaxResults { get; set; } = 50;
            public int PageNumber { get; set; } = 1;
            public int PageSize { get; set; } = 10;
            public bool UseCache { get; set; } = true;
            public bool CacheResults { get; set; } = true;
            public TemplateSortOrder SortOrder { get; set; } = TemplateSortOrder.Relevance;
            public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template version options;
        /// </summary>
        public class TemplateVersionOptions;
        {
            public string VersionNotes { get; set; }
            public string CreatedBy { get; set; } = "system";
            public bool ValidateBeforeVersion { get; set; } = true;
            public bool PreserveMetadata { get; set; } = true;
            public Dictionary<string, object> AdditionalMetadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template render options;
        /// </summary>
        public class TemplateRenderOptions;
        {
            public int? Version { get; set; }
            public bool ValidateData { get; set; } = true;
            public List<ITemplateDataTransformer> DataTransformers { get; set; } = new List<ITemplateDataTransformer>();
            public List<ITemplatePostProcessor> PostProcessors { get; set; } = new List<ITemplatePostProcessor>();
            public Dictionary<string, object> RenderContext { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template export criteria;
        /// </summary>
        public class TemplateExportCriteria;
        {
            public List<string> TemplateIds { get; set; }
            public List<string> Categories { get; set; }
            public List<string> Tags { get; set; }
            public int? MinComplexity { get; set; }
            public int? MaxComplexity { get; set; }
            public int? MaxResults { get; set; }
            public TemplateSearchCriteria SearchCriteria { get; set; }
        }

        /// <summary>
        /// Template export options;
        /// </summary>
        public class TemplateExportOptions;
        {
            public ExportFormat Format { get; set; } = ExportFormat.Json;
            public int CompressionLevel { get; set; } = 0;
            public string EncryptionKey { get; set; }
            public bool IncludeDependencies { get; set; } = true;
            public bool IncludeMetadata { get; set; } = true;
            public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template import options;
        /// </summary>
        public class TemplateImportOptions;
        {
            public ImportConflictResolution ConflictResolution { get; set; } = ImportConflictResolution.Skip;
            public string DecryptionKey { get; set; }
            public bool ImportDependencies { get; set; } = true;
            public bool ValidateBeforeImport { get; set; } = true;
            public string CreatedBy { get; set; } = "import";
            public Dictionary<string, object> AdditionalMetadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template sync options;
        /// </summary>
        public class TemplateSyncOptions;
        {
            public bool SyncDeletions { get; set; } = false;
            public TemplateSyncCriteria SyncCriteria { get; set; } = new TemplateSyncCriteria();
            public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Template cleanup options;
        /// </summary>
        public class TemplateCleanupOptions;
        {
            public CleanupStrategy Strategy { get; set; } = CleanupStrategy.Combined;
            public int MaxAgeDays { get; set; } = 90;
            public int MinUsageCount { get; set; } = 1;
            public long MaxTemplateSize { get; set; } = 10 * 1024 * 1024; // 10MB;
            public int MinComplexity { get; set; } = 3;
            public double CleanupThreshold { get; set; } = 0.7;
            public List<string> ProtectedTemplateIds { get; set; } = new List<string>();
        }

        /// <summary>
        /// Template storage result;
        /// </summary>
        public class TemplateStorageResult;
        {
            public bool Success { get; set; }
            public string TemplateId { get; set; }
            public string StorageId { get; set; }
            public int Version { get; set; }
            public bool Indexed { get; set; }
            public TimeSpan StorageTime { get; set; }
            public string FinalTemplateId { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorCode { get; set; }
        }

        /// <summary>
        /// Template retrieval result;
        /// </summary>
        public class TemplateRetrievalResult;
        {
            public bool Success { get; set; }
            public Template Template { get; set; }
            public bool RetrievedFromCache { get; set; }
            public TimeSpan RetrievalTime { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Template search result;
        /// </summary>
        public class TemplateSearchResult;
        {
            public bool Success { get; set; }
            public TemplateSearchCriteria Criteria { get; set; }
            public TemplateSearchOptions Options { get; set; }
            public List<ScoredTemplate> Templates { get; set; } = new List<ScoredTemplate>();
            public int TotalMatches { get; set; }
            public int ReturnedCount { get; set; }
            public TimeSpan SearchTime { get; set; }
        }

        /// <summary>
        /// Scored template with relevance information;
        /// </summary>
        public class ScoredTemplate;
        {
            public Template Template { get; set; }
            public double RelevanceScore { get; set; }
            public Dictionary<string, double> ScoreBreakdown { get; set; } = new Dictionary<string, double>();
        }

        /// <summary>
        /// Template version result;
        /// </summary>
        public class TemplateVersionResult;
        {
            public bool Success { get; set; }
            public string TemplateId { get; set; }
            public int OldVersion { get; set; }
            public int NewVersion { get; set; }
            public List<TemplateChange> Changes { get; set; } = new List<TemplateChange>();
            public TimeSpan VersioningTime { get; set; }
        }

        /// <summary>
        /// Template render result;
        /// </summary>
        public class TemplateRenderResult;
        {
            public bool Success { get; set; }
            public string TemplateId { get; set; }
            public int TemplateVersion { get; set; }
            public Dictionary<string, object> RenderedContent { get; set; } = new Dictionary<string, object>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
            public List<string> Warnings { get; set; } = new List<string>();
            public TimeSpan RenderTime { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Template export package;
        /// </summary>
        public class TemplateExportPackage;
        {
            public string PackageId { get; set; }
            public DateTime ExportTimestamp { get; set; }
            public TemplateExportCriteria ExportCriteria { get; set; }
            public TemplateExportOptions ExportOptions { get; set; }
            public List<Template> Templates { get; set; } = new List<Template>();
            public List<Template> Dependencies { get; set; } = new List<Template>();
            public ExportMetadata Metadata { get; set; }
            public ExportManifest Manifest { get; set; }
        }

        /// <summary>
        /// Template import result;
        /// </summary>
        public class TemplateImportResult;
        {
            public bool Success { get; set; }
            public string PackageId { get; set; }
            public int ImportedCount { get; set; }
            public int SkippedCount { get; set; }
            public int FailedCount { get; set; }
            public List<TemplateImportDetail> ImportDetails { get; set; } = new List<TemplateImportDetail>();
            public TimeSpan ImportTime { get; set; }
        }

        /// <summary>
        /// Template sync result;
        /// </summary>
        public class TemplateSyncResult;
        {
            public bool Success { get; set; }
            public string Source { get; set; }
            public int AddedCount { get; set; }
            public int UpdatedCount { get; set; }
            public int DeletedCount { get; set; }
            public int UnchangedCount { get; set; }
            public int FailedCount { get; set; }
            public List<TemplateSyncDetail> SyncDetails { get; set; } = new List<TemplateSyncDetail>();
            public TimeSpan SyncTime { get; set; }
        }

        /// <summary>
        /// Template cleanup result;
        /// </summary>
        public class TemplateCleanupResult;
        {
            public bool Success { get; set; }
            public int CleanedCount { get; set; }
            public List<Template> CleanedTemplates { get; set; } = new List<Template>();
            public long TotalSizeFreed { get; set; }
            public TimeSpan CleanupTime { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Template metrics and statistics;
        /// </summary>
        public class TemplateMetrics;
        {
            public int TotalTemplates { get; set; }
            public long TotalSizeBytes { get; set; }
            public double AverageTemplateSize { get; set; }
            public Dictionary<string, int> TemplateTypeDistribution { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> CategoryDistribution { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> ComplexityDistribution { get; set; } = new Dictionary<string, int>();
            public double AverageComplexity { get; set; }
            public long TotalRetrievals { get; set; }
            public long TotalSearches { get; set; }
            public long TotalRenders { get; set; }
            public long TotalCleanedTemplates { get; set; }
            public long TotalSizeFreed { get; set; }
            public double CacheHitRate { get; set; }
            public double AverageRetrievalTime { get; set; }
            public double AverageSearchTime { get; set; }
            public double AverageRenderTime { get; set; }
            public DateTime LastStorageTime { get; set; }
            public DateTime LastRetrievalTime { get; set; }
            public DateTime LastSearchTime { get; set; }
            public DateTime LastRenderTime { get; set; }
            public DateTime LastCleanupTime { get; set; }
            public int IndexCount { get; set; }
            public DateTime CalculatedAt { get; set; }
        }

        #endregion;

        #region Enums;

        /// <summary>
        /// Template types;
        /// </summary>
        public enum TemplateType;
        {
            Unknown,
            Skill,
            Workflow,
            DataProcessor,
            APIClient,
            Report,
            Notification,
            UIComponent,
            Database,
            Test,
            Deployment,
            Documentation,
            Custom;
        }

        /// <summary>
        /// Template search types;
        /// </summary>
        public enum TemplateSearchType;
        {
            Semantic,
            Category,
            Tag,
            Pattern,
            Complexity,
            Hybrid,
            Keyword;
        }

        /// <summary>
        /// Template sort orders;
        /// </summary>
        public enum TemplateSortOrder;
        {
            Relevance,
            Name,
            Date,
            Complexity,
            Popularity,
            Size;
        }

        /// <summary>
        /// Template conflict resolution strategies;
        /// </summary>
        public enum TemplateConflictResolution;
        {
            Throw,
            Overwrite,
            CreateVersion,
            Rename;
        }

        /// <summary>
        /// Import conflict resolution strategies;
        /// </summary>
        public enum ImportConflictResolution;
        {
            Skip,
            Overwrite,
            Merge,
            CreateVersion;
        }

        /// <summary>
        /// Template import status;
        /// </summary>
        public enum TemplateImportStatus;
        {
            Pending,
            Imported,
            Skipped,
            Failed;
        }

        /// <summary>
        /// Sync operations;
        /// </summary>
        public enum SyncOperation;
        {
            Added,
            Updated,
            Deleted,
            Unchanged,
            Failed,
            Import,
            Sync;
        }

        /// <summary>
        /// Cleanup strategies;
        /// </summary>
        public enum CleanupStrategy;
        {
            AgeBased,
            UsageBased,
            SizeBased,
            ComplexityBased,
            Combined;
        }

        /// <summary>
        /// Export formats;
        /// </summary>
        public enum ExportFormat;
        {
            Json,
            Xml,
            Binary,
            Compressed;
        }

        /// <summary>
        /// Template operations;
        /// </summary>
        public enum TemplateOperation;
        {
            Store,
            Retrieve,
            Search,
            Render,
            Version,
            Export,
            Import,
            Sync,
            Cleanup;
        }

        /// <summary>
        /// Schema property types;
        /// </summary>
        public enum SchemaPropertyType;
        {
            String,
            Number,
            Boolean,
            Object,
            Array,
            Null,
            Any;
        }

        #endregion;
    }

    #region Exceptions;

    /// <summary>
    /// Base exception for template storage errors;
    /// </summary>
    public class TemplateStorageException : Exception
    {
        public string ErrorCode { get; }
        public string TemplateId { get; }

        public TemplateStorageException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public TemplateStorageException(string message, string templateId, string errorCode)
            : base(message)
        {
            TemplateId = templateId;
            ErrorCode = errorCode;
        }

        public TemplateStorageException(string message, string templateId, string errorCode, Exception innerException)
            : base(message, innerException)
        {
            TemplateId = templateId;
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Template not found exception;
    /// </summary>
    public class TemplateNotFoundException : TemplateStorageException;
    {
        public TemplateNotFoundException(string message, string templateId)
            : base(message, templateId, ErrorCodes.TEMPLATE_NOT_FOUND)
        {
        }
    }

    /// <summary>
    /// Template already exists exception;
    /// </summary>
    public class TemplateAlreadyExistsException : TemplateStorageException;
    {
        public TemplateAlreadyExistsException(string message, string templateId)
            : base(message, templateId, ErrorCodes.TEMPLATE_ALREADY_EXISTS)
        {
        }
    }

    /// <summary>
    /// Template validation exception;
    /// </summary>
    public class TemplateValidationException : TemplateStorageException;
    {
        public List<ValidationError> Errors { get; }

        public TemplateValidationException(string message, string templateId, List<ValidationError> errors)
            : base(message, templateId, ErrorCodes.TEMPLATE_VALIDATION_FAILED)
        {
            Errors = errors;
        }
    }

    /// <summary>
    /// Template retrieval exception;
    /// </summary>
    public class TemplateRetrievalException : TemplateStorageException;
    {
        public TemplateRetrievalException(string message, string templateId, string errorCode, Exception innerException)
            : base(message, templateId, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template search exception;
    /// </summary>
    public class TemplateSearchException : TemplateStorageException;
    {
        public TemplateSearchException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template version exception;
    /// </summary>
    public class TemplateVersionException : TemplateStorageException;
    {
        public TemplateVersionException(string message, string templateId, string errorCode)
            : base(message, templateId, errorCode)
        {
        }
    }

    /// <summary>
    /// Template render exception;
    /// </summary>
    public class TemplateRenderException : TemplateStorageException;
    {
        public TemplateRenderException(string message, string templateId, string errorCode)
            : base(message, templateId, errorCode)
        {
        }

        public TemplateRenderException(string message, string templateId, string errorCode, Exception innerException)
            : base(message, templateId, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template export exception;
    /// </summary>
    public class TemplateExportException : TemplateStorageException;
    {
        public TemplateExportException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template import exception;
    /// </summary>
    public class TemplateImportException : TemplateStorageException;
    {
        public TemplateImportException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template import validation exception;
    /// </summary>
    public class TemplateImportValidationException : TemplateStorageException;
    {
        public List<ValidationError> Errors { get; }

        public TemplateImportValidationException(string message, List<ValidationError> errors)
            : base(message, ErrorCodes.TEMPLATE_IMPORT_VALIDATION_FAILED)
        {
            Errors = errors;
        }
    }

    /// <summary>
    /// Template data validation exception;
    /// </summary>
    public class TemplateDataValidationException : TemplateStorageException;
    {
        public List<ValidationError> Errors { get; }

        public TemplateDataValidationException(string message, string templateId, List<ValidationError> errors)
            : base(message, templateId, ErrorCodes.TEMPLATE_DATA_VALIDATION_FAILED)
        {
            Errors = errors;
        }
    }

    /// <summary>
    /// Template sync exception;
    /// </summary>
    public class TemplateSyncException : TemplateStorageException;
    {
        public TemplateSyncException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template cleanup exception;
    /// </summary>
    public class TemplateCleanupException : TemplateStorageException;
    {
        public TemplateCleanupException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Template index exception;
    /// </summary>
    public class TemplateIndexException : TemplateStorageException;
    {
        public TemplateIndexException(string message, string templateId, string errorCode)
            : base(message, templateId, errorCode)
        {
        }
    }

    #endregion;

    #region Supporting Infrastructure;

    /// <summary>
    /// Template repository for persistent storage;
    /// </summary>
    internal class TemplateRepository : IDisposable
    {
        private readonly ILogger<TemplateRepository> _logger;
        private readonly AppConfig _appConfig;
        private readonly ITemplateStore _store;
        private bool _disposed;

        public TemplateRepository(ILogger<TemplateRepository> logger, AppConfig appConfig)
        {
            _logger = logger;
            _appConfig = appConfig;
            _store = CreateTemplateStore();
        }

        public async Task InitializeAsync()
        {
            await _store.InitializeAsync();
            _logger.LogInformation("TemplateRepository initialized");
        }

        public async Task<TemplateStorageResult> StoreAsync(Template template)
        {
            return await _store.StoreAsync(template);
        }

        public async Task<Template> GetAsync(string templateId)
        {
            return await _store.GetAsync(templateId);
        }

        public async Task<Template> GetLatestAsync(string templateId)
        {
            return await _store.GetLatestAsync(templateId);
        }

        public async Task<Template> GetVersionAsync(string templateId, int version)
        {
            return await _store.GetVersionAsync(templateId, version);
        }

        public async Task<bool> DeleteAsync(string templateId)
        {
            return await _store.DeleteAsync(templateId);
        }

        public async Task UpdateAccessInfoAsync(string templateId, int accessCount, DateTime lastAccessed)
        {
            await _store.UpdateAccessInfoAsync(templateId, accessCount, lastAccessed);
        }

        public async Task UpdateUsageInfoAsync(string templateId, int usageCount, DateTime lastUsed)
        {
            await _store.UpdateUsageInfoAsync(templateId, usageCount, lastUsed);
        }

        public List<Template> GetAll()
        {
            return _store.GetAll();
        }

        public async Task<List<Template>> GetAllAsync()
        {
            return await _store.GetAllAsync();
        }

        public bool Exists(string templateId)
        {
            return _store.Exists(templateId);
        }

        private ITemplateStore CreateTemplateStore()
        {
            // Create appropriate store based on configuration;
            return new FileTemplateStore(_logger, _appConfig);
        }

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
                    _store?.Dispose();
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Template index engine for fast search;
    /// </summary>
    internal class TemplateIndexEngine : IDisposable
    {
        private readonly ILogger<TemplateIndexEngine> _logger;
        private readonly Dictionary<string, ITemplateIndex> _indexes;
        private bool _disposed;

        public TemplateIndexEngine(ILogger<TemplateIndexEngine> logger)
        {
            _logger = logger;
            _indexes = new Dictionary<string, ITemplateIndex>();
        }

        public async Task<IndexResult> IndexAsync(Template template)
        {
            // Add to all relevant indexes;
            await AddToSemanticIndexAsync(template);
            await AddToCategoryIndexAsync(template);
            await AddToTagIndexAsync(template);
            await AddToComplexityIndexAsync(template);

            return new IndexResult { Success = true };
        }

        public async Task<bool> RemoveFromIndexAsync(string templateId)
        {
            // Remove from all indexes;
            foreach (var index in _indexes.Values)
            {
                await index.RemoveAsync(templateId);
            }

            return true;
        }

        public async Task BuildIndexesAsync(List<Template> templates)
        {
            foreach (var template in templates)
            {
                await IndexAsync(template);
            }
        }

        public async Task RebuildIndexesAsync(List<Template> templates)
        {
            // Clear all indexes;
            foreach (var index in _indexes.Values)
            {
                await index.ClearAsync();
            }

            // Rebuild indexes;
            await BuildIndexesAsync(templates);
        }

        public async Task<List<Template>> SearchBySimilarityAsync(TemplateEmbedding queryEmbedding, int maxResults)
        {
            if (_indexes.TryGetValue("semantic", out var semanticIndex))
            {
                return await semanticIndex.SearchAsync(queryEmbedding, maxResults);
            }

            return new List<Template>();
        }

        public async Task<List<Template>> SearchByCategoryAsync(List<string> categories, int maxResults)
        {
            if (_indexes.TryGetValue("category", out var categoryIndex))
            {
                return await categoryIndex.SearchByCategoriesAsync(categories, maxResults);
            }

            return new List<Template>();
        }

        public async Task<List<Template>> SearchByTagsAsync(List<string> tags, int maxResults)
        {
            if (_indexes.TryGetValue("tag", out var tagIndex))
            {
                return await tagIndex.SearchByTagsAsync(tags, maxResults);
            }

            return new List<Template>();
        }

        public async Task<List<Template>> SearchByPatternAsync(TemplatePattern pattern, int maxResults)
        {
            // Implementation for pattern-based search;
            await Task.Delay(1);
            return new List<Template>();
        }

        public async Task<List<Template>> SearchByComplexityAsync(int minComplexity, int maxComplexity, int maxResults)
        {
            if (_indexes.TryGetValue("complexity", out var complexityIndex))
            {
                return await complexityIndex.SearchByRangeAsync(minComplexity, maxComplexity, maxResults);
            }

            return new List<Template>();
        }

        public async Task<List<Template>> SearchByKeywordsAsync(List<string> keywords, int maxResults)
        {
            if (_indexes.TryGetValue("keyword", out var keywordIndex))
            {
                return await keywordIndex.SearchByKeywordsAsync(keywords, maxResults);
            }

            return new List<Template>();
        }

        public async Task<List<Template>> SearchRecentAsync(int maxResults)
        {
            // Implementation for recent templates search;
            await Task.Delay(1);
            return new List<Template>();
        }

        public int GetIndexCount()
        {
            return _indexes.Count;
        }

        private async Task AddToSemanticIndexAsync(Template template)
        {
            if (!_indexes.ContainsKey("semantic"))
            {
                _indexes["semantic"] = new SemanticTemplateIndex();
            }

            await _indexes["semantic"].AddAsync(template);
        }

        private async Task AddToCategoryIndexAsync(Template template)
        {
            if (!_indexes.ContainsKey("category"))
            {
                _indexes["category"] = new CategoryTemplateIndex();
            }

            await _indexes["category"].AddAsync(template);
        }

        private async Task AddToTagIndexAsync(Template template)
        {
            if (!_indexes.ContainsKey("tag"))
            {
                _indexes["tag"] = new TagTemplateIndex();
            }

            await _indexes["tag"].AddAsync(template);
        }

        private async Task AddToComplexityIndexAsync(Template template)
        {
            if (!_indexes.ContainsKey("complexity"))
            {
                _indexes["complexity"] = new ComplexityTemplateIndex();
            }

            await _indexes["complexity"].AddAsync(template);
        }

        private async Task AddToKeywordIndexAsync(Template template)
        {
            if (!_indexes.ContainsKey("keyword"))
            {
                _indexes["keyword"] = new KeywordTemplateIndex();
            }

            await _indexes["keyword"].AddAsync(template);
        }

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
                    foreach (var index in _indexes.Values)
                    {
                        index.Dispose();
                    }
                    _indexes.Clear();
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Template validation engine;
    /// </summary>
    internal class TemplateValidationEngine : IDisposable
    {
        private readonly ILogger<TemplateValidationEngine> _logger;
        private bool _disposed;

        public TemplateValidationEngine(ILogger<TemplateValidationEngine> logger)
        {
            _logger = logger;
        }

        public async Task<TemplateValidationResult> ValidateAsync(Template template)
        {
            var result = new TemplateValidationResult;
            {
                TemplateId = template.Id,
                ValidatedAt = DateTime.UtcNow;
            };

            // Basic validation;
            if (string.IsNullOrEmpty(template.Id))
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "TEMPLATE_ID_REQUIRED",
                    Message = "Template ID is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Schema validation;
            if (template.Schema != null)
            {
                var schemaResult = await ValidateSchemaAsync(template.Schema);
                if (!schemaResult.IsValid)
                {
                    result.Errors.AddRange(schemaResult.Errors);
                }
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);
            return result;
        }

        public async Task<SchemaValidationResult> ValidateSchemaAsync(TemplateSchema schema)
        {
            var result = new SchemaValidationResult;
            {
                ValidatedAt = DateTime.UtcNow;
            };

            // Validate schema properties;
            if (schema.Properties != null)
            {
                foreach (var property in schema.Properties)
                {
                    var propertyResult = await ValidateSchemaPropertyAsync(property.Value);
                    if (!propertyResult.IsValid)
                    {
                        result.Errors.AddRange(propertyResult.Errors.Select(e => new ValidationError;
                        {
                            Code = $"SCHEMA_PROPERTY_{e.Code}",
                            Message = $"Property '{property.Key}': {e.Message}",
                            Severity = e.Severity,
                            Field = property.Key;
                        }));
                    }
                }
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);
            return result;
        }

        private async Task<PropertyValidationResult> ValidateSchemaPropertyAsync(TemplateSchemaProperty property)
        {
            var result = new PropertyValidationResult;
            {
                ValidatedAt = DateTime.UtcNow;
            };

            // Validate property type;
            if (!Enum.IsDefined(typeof(SchemaPropertyType), property.Type))
            {
                result.Errors.Add(new PropertyValidationError;
                {
                    Code = "INVALID_PROPERTY_TYPE",
                    Message = $"Invalid property type: {property.Type}",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Validate constraints;
            if (property.MinLength.HasValue && property.MaxLength.HasValue &&
                property.MinLength > property.MaxLength)
            {
                result.Errors.Add(new PropertyValidationError;
                {
                    Code = "INVALID_LENGTH_CONSTRAINTS",
                    Message = "MinLength cannot be greater than MaxLength",
                    Severity = ValidationSeverity.Error;
                });
            }

            if (property.Minimum.HasValue && property.Maximum.HasValue &&
                property.Minimum > property.Maximum)
            {
                result.Errors.Add(new PropertyValidationError;
                {
                    Code = "INVALID_RANGE_CONSTRAINTS",
                    Message = "Minimum cannot be greater than Maximum",
                    Severity = ValidationSeverity.Error;
                });
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);
            return result;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Template renderer;
    /// </summary>
    internal class TemplateRenderer : IDisposable
    {
        private readonly ILogger<TemplateRenderer> _logger;
        private bool _disposed;

        public TemplateRenderer(ILogger<TemplateRenderer> logger)
        {
            _logger = logger;
        }

        public async Task<TemplateRenderResult> RenderAsync(Template template, Dictionary<string, object> data,
            TemplateRenderOptions options)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Apply template content with data;
                var renderedContent = await ApplyTemplateAsync(template.Content, data, template.DefaultValues);

                // Build metadata;
                var metadata = new Dictionary<string, object>
                {
                    ["templateId"] = template.Id,
                    ["templateVersion"] = template.Version,
                    ["renderTimestamp"] = DateTime.UtcNow,
                    ["dataFieldCount"] = data.Count,
                    ["options"] = options;
                };

                return new TemplateRenderResult;
                {
                    Success = true,
                    RenderedContent = renderedContent,
                    Metadata = metadata,
                    RenderTime = DateTime.UtcNow - startTime;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render template: {TemplateId}", template.Id);
                throw new TemplateRenderException(
                    $"Failed to render template '{template.Id}'",
                    template.Id, ErrorCodes.TEMPLATE_RENDER_FAILED, ex);
            }
        }

        private async Task<Dictionary<string, object>> ApplyTemplateAsync(Dictionary<string, object> templateContent,
            Dictionary<string, object> data, Dictionary<string, object> defaultValues)
        {
            var result = new Dictionary<string, object>();

            foreach (var item in templateContent)
            {
                var renderedValue = await RenderValueAsync(item.Value, data, defaultValues);
                result[item.Key] = renderedValue;
            }

            return result;
        }

        private async Task<object> RenderValueAsync(object value, Dictionary<string, object> data,
            Dictionary<string, object> defaultValues)
        {
            if (value is string stringValue)
            {
                // Handle template strings with placeholders;
                return await RenderStringAsync(stringValue, data, defaultValues);
            }
            else if (value is Dictionary<string, object> dictValue)
            {
                // Recursively render dictionaries;
                var renderedDict = new Dictionary<string, object>();
                foreach (var item in dictValue)
                {
                    renderedDict[item.Key] = await RenderValueAsync(item.Value, data, defaultValues);
                }
                return renderedDict;
            }
            else if (value is List<object> listValue)
            {
                // Recursively render lists;
                var renderedList = new List<object>();
                foreach (var item in listValue)
                {
                    renderedList.Add(await RenderValueAsync(item, data, defaultValues));
                }
                return renderedList;
            }
            else;
            {
                // Return primitive values as-is;
                return value;
            }
        }

        private async Task<string> RenderStringAsync(string template, Dictionary<string, object> data,
            Dictionary<string, object> defaultValues)
        {
            // Simple template rendering with {{placeholder}} syntax;
            var rendered = template;

            foreach (var item in data)
            {
                var placeholder = $"{{{{{item.Key}}}}}";
                rendered = rendered.Replace(placeholder, item.Value?.ToString() ?? string.Empty);
            }

            // Fill remaining placeholders with default values;
            foreach (var item in defaultValues)
            {
                var placeholder = $"{{{{{item.Key}}}}}";
                if (rendered.Contains(placeholder))
                {
                    rendered = rendered.Replace(placeholder, item.Value?.ToString() ?? string.Empty);
                }
            }

            return rendered;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Template version manager;
    /// </summary>
    internal class TemplateVersionManager : IDisposable
    {
        private readonly ILogger<TemplateVersionManager> _logger;
        private readonly Dictionary<string, List<int>> _templateVersions;
        private bool _disposed;

        public TemplateVersionManager(ILogger<TemplateVersionManager> logger)
        {
            _logger = logger;
            _templateVersions = new Dictionary<string, List<int>>();
        }

        public void RegisterTemplate(Template template)
        {
            if (!_templateVersions.ContainsKey(template.OriginalTemplateId))
            {
                _templateVersions[template.OriginalTemplateId] = new List<int>();
            }

            if (!_templateVersions[template.OriginalTemplateId].Contains(template.Version))
            {
                _templateVersions[template.OriginalTemplateId].Add(template.Version);
                _templateVersions[template.OriginalTemplateId].Sort();
            }
        }

        public void AddVersion(string templateId, int version)
        {
            if (!_templateVersions.ContainsKey(templateId))
            {
                _templateVersions[templateId] = new List<int>();
            }

            if (!_templateVersions[templateId].Contains(version))
            {
                _templateVersions[templateId].Add(version);
                _templateVersions[templateId].Sort();
            }
        }

        public void RemoveTemplate(string templateId)
        {
            _templateVersions.Remove(templateId);
        }

        public List<int> GetVersions(string templateId)
        {
            return _templateVersions.ContainsKey(templateId)
                ? new List<int>(_templateVersions[templateId])
                : new List<int>();
        }

        public int GetLatestVersion(string templateId)
        {
            return _templateVersions.ContainsKey(templateId) && _templateVersions[templateId].Any()
                ? _templateVersions[templateId].Max()
                : 0;
        }

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
                    _templateVersions.Clear();
                }
                _disposed = true;
            }
        }
    }

    #endregion;

    #region Supporting Types;

    public class TemplateSchemaProperty : ICloneable;
    {
        public SchemaPropertyType Type { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; } = false;
        public object DefaultValue { get; set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public string Pattern { get; set; }
        public List<object> Enum { get; set; } = new List<object>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new TemplateSchemaProperty;
            {
                Type = Type,
                Description = Description,
                Required = Required,
                DefaultValue = DefaultValue,
                MinLength = MinLength,
                MaxLength = MaxLength,
                Minimum = Minimum,
                Maximum = Maximum,
                Pattern = Pattern,
                Enum = new List<object>(Enum),
                Metadata = new Dictionary<string, object>(Metadata)
            };
        }
    }

    public class TemplateValidationRule : ICloneable;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Condition { get; set; }
        public string Message { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new TemplateValidationRule;
            {
                Name = Name,
                Description = Description,
                Condition = Condition,
                Message = Message,
                Severity = Severity,
                Parameters = new Dictionary<string, object>(Parameters)
            };
        }
    }

    public class TemplateEmbedding : ICloneable;
    {
        public List<float> Vector { get; set; } = new List<float>();
        public string Model { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int Dimension { get; set; }

        public object Clone()
        {
            return new TemplateEmbedding;
            {
                Vector = new List<float>(Vector),
                Model = Model,
                GeneratedAt = GeneratedAt,
                Dimension = Dimension;
            };
        }
    }

    public class TemplateDependency : ICloneable;
    {
        public string TemplateId { get; set; }
        public string VersionConstraint { get; set; }
        public bool Required { get; set; } = true;
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new TemplateDependency;
            {
                TemplateId = TemplateId,
                VersionConstraint = VersionConstraint,
                Required = Required,
                Configuration = new Dictionary<string, object>(Configuration)
            };
        }
    }

    public class TemplateVersionMetadata : ICloneable;
    {
        public int Version { get; set; }
        public int PreviousVersion { get; set; }
        public string VersionNotes { get; set; }
        public List<TemplateChange> Changes { get; set; } = new List<TemplateChange>();
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }

        public object Clone()
        {
            return new TemplateVersionMetadata;
            {
                Version = Version,
                PreviousVersion = PreviousVersion,
                VersionNotes = VersionNotes,
                Changes = Changes.Select(c => (TemplateChange)c.Clone()).ToList(),
                CreatedAt = CreatedAt,
                CreatedBy = CreatedBy;
            };
        }
    }

    public class TemplateChange : ICloneable;
    {
        public string Field { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public ChangeType Type { get; set; }
        public string Description { get; set; }

        public object Clone()
        {
            return new TemplateChange;
            {
                Field = Field,
                OldValue = OldValue,
                NewValue = NewValue,
                Type = Type,
                Description = Description;
            };
        }
    }

    public class TemplateImportDetail;
    {
        public string TemplateId { get; set; }
        public TemplateImportStatus Status { get; set; }
        public int? Version { get; set; }
        public int? ExistingVersion { get; set; }
        public int? ImportedVersion { get; set; }
        public string Action { get; set; }
        public string Reason { get; set; }
        public TemplateStorageResult StorageResult { get; set; }
        public Exception Error { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class TemplateSyncDetail;
    {
        public string TemplateId { get; set; }
        public SyncOperation Operation { get; set; }
        public List<TemplateChange> Changes { get; set; } = new List<TemplateChange>();
        public DateTime Timestamp { get; set; }
    }

    public class ExportMetadata;
    {
        public int TotalTemplates { get; set; }
        public long TotalSize { get; set; }
        public ExportFormat ExportFormat { get; set; }
        public int CompressionLevel { get; set; }
        public bool IncludeDependencies { get; set; }
        public bool IncludeMetadata { get; set; }
        public string Version { get; set; }
        public DateTime ExportTimestamp { get; set; }
    }

    public class ExportManifest;
    {
        public string PackageId { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<ManifestItem> Items { get; set; } = new List<ManifestItem>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class TemplateAccessPattern;
    {
        public string TemplateId { get; set; }
        public TemplateOperation Operation { get; set; }
        public long AccessCount { get; set; }
        public DateTime LastAccessed { get; set; }
        public double AverageAccessInterval { get; set; }
    }

    public class TemplateValidationResult;
    {
        public string TemplateId { get; set; }
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public DateTime ValidatedAt { get; set; }
    }

    public class SchemaValidationResult;
    {
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public DateTime ValidatedAt { get; set; }
    }

    public class PropertyValidationResult;
    {
        public bool IsValid { get; set; }
        public List<PropertyValidationError> Errors { get; set; } = new List<PropertyValidationError>();
        public DateTime ValidatedAt { get; set; }
    }

    public class IndexResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int IndexedCount { get; set; }
    }

    #endregion;

    #region Interfaces;

    public interface ITemplateSource;
    {
        string Name { get; }
        Task<List<Template>> GetTemplatesAsync(TemplateSyncCriteria criteria);
        Task<Template> GetTemplateAsync(string templateId);
    }

    public interface ITemplateStore : IDisposable
    {
        Task InitializeAsync();
        Task<TemplateStorageResult> StoreAsync(Template template);
        Task<Template> GetAsync(string templateId);
        Task<Template> GetLatestAsync(string templateId);
        Task<Template> GetVersionAsync(string templateId, int version);
        Task<bool> DeleteAsync(string templateId);
        Task UpdateAccessInfoAsync(string templateId, int accessCount, DateTime lastAccessed);
        Task UpdateUsageInfoAsync(string templateId, int usageCount, DateTime lastUsed);
        List<Template> GetAll();
        Task<List<Template>> GetAllAsync();
        bool Exists(string templateId);
    }

    public interface ITemplateIndex : IDisposable
    {
        Task AddAsync(Template template);
        Task RemoveAsync(string templateId);
        Task ClearAsync();
        Task<List<Template>> SearchAsync(TemplateEmbedding embedding, int maxResults);
        Task<List<Template>> SearchByCategoriesAsync(List<string> categories, int maxResults);
        Task<List<Template>> SearchByTagsAsync(List<string> tags, int maxResults);
        Task<List<Template>> SearchByRangeAsync(int minValue, int maxValue, int maxResults);
        Task<List<Template>> SearchByKeywordsAsync(List<string> keywords, int maxResults);
    }

    public interface ITemplateValidationRule;
    {
        Task<ValidationRuleResult> ValidateAsync(Template template);
    }

    public interface ITemplateTransformation;
    {
        Task<Template> TransformAsync(Template template);
    }

    public interface ITemplateFilter;
    {
        Task<List<Template>> ApplyAsync(List<Template> templates);
    }

    public interface ITemplateDataTransformer;
    {
        Task<Dictionary<string, object>> TransformAsync(Dictionary<string, object> data, Template template);
    }

    public interface ITemplatePostProcessor;
    {
        Task<TemplateRenderResult> ProcessAsync(TemplateRenderResult renderResult);
    }

    #endregion;

    #region Enums;

    public enum ChangeType;
    {
        Added,
        Modified,
        Removed;
    }

    public enum ValidationSeverity;
    {
        Information,
        Warning,
        Error,
        Critical;
    }

    #endregion;

    #region Error Codes;

    public static class ErrorCodes;
    {
        // Template Storage Errors;
        public const string TEMPLATE_STORAGE_INIT_FAILED = "TS001";
        public const string TEMPLATE_NOT_FOUND = "TS002";
        public const string TEMPLATE_ALREADY_EXISTS = "TS003";
        public const string TEMPLATE_VALIDATION_FAILED = "TS004";
        public const string TEMPLATE_STORE_FAILED = "TS005";
        public const string TEMPLATE_RETRIEVAL_FAILED = "TS006";
        public const string TEMPLATE_SEARCH_FAILED = "TS007";
        public const string TEMPLATE_VERSION_FAILED = "TS008";
        public const string TEMPLATE_VERSION_INVALID = "TS009";
        public const string TEMPLATE_RENDER_FAILED = "TS010";
        public const string TEMPLATE_RENDER_RETRIEVAL_FAILED = "TS011";
        public const string TEMPLATE_EXPORT_FAILED = "TS012";
        public const string TEMPLATE_IMPORT_FAILED = "TS013";
        public const string TEMPLATE_IMPORT_VALIDATION_FAILED = "TS014";
        public const string TEMPLATE_DATA_VALIDATION_FAILED = "TS015";
        public const string TEMPLATE_SYNC_FAILED = "TS016";
        public const string TEMPLATE_CLEANUP_FAILED = "TS017";

        // Index Errors;
        public const string TEMPLATE_INDEX_FAILED = "TS101";

        // Cache Errors;
        public const string TEMPLATE_CACHE_FAILED = "TS201";
    }

    #endregion;
}
