// NEDA.Brain/KnowledgeBase/ProcedureLibrary/ProcedureStore.cs;
using Microsoft.Extensions.Logging;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Common.Exceptions;
using NEDA.Common.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.Brain.KnowledgeBase.ProcedureLibrary;
{
    /// <summary>
    /// Represents a stored procedure with metadata and execution logic;
    /// </summary>
    public class ProcedureDefinition;
    {
        /// <summary>
        /// Unique identifier for the procedure;
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Human-readable name of the procedure;
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Detailed description of what the procedure does;
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Procedure category for organization;
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// The actual procedural steps in structured format;
        /// </summary>
        public List<ProcedureStep> Steps { get; set; } = new List<ProcedureStep>();

        /// <summary>
        /// Required parameters for procedure execution;
        /// </summary>
        public Dictionary<string, ParameterDefinition> Parameters { get; set; } = new Dictionary<string, ParameterDefinition>();

        /// <summary>
        /// Expected return value definition;
        /// </summary>
        public ReturnDefinition ReturnValue { get; set; }

        /// <summary>
        /// Prerequisites that must be satisfied before execution;
        /// </summary>
        public List<Prerequisite> Prerequisites { get; set; } = new List<Prerequisite>();

        /// <summary>
        /// Dependencies on other procedures;
        /// </summary>
        public List<string> Dependencies { get; set; } = new List<string>();

        /// <summary>
        /// Performance metrics and statistics;
        /// </summary>
        public ProcedureMetrics Metrics { get; set; } = new ProcedureMetrics();

        /// <summary>
        /// Success criteria for procedure validation;
        /// </summary>
        public List<SuccessCriterion> SuccessCriteria { get; set; } = new List<SuccessCriterion>();

        /// <summary>
        /// Version of the procedure;
        /// </summary>
        public Version Version { get; set; } = new Version(1, 0, 0);

        /// <summary>
        /// Creation timestamp;
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last modification timestamp;
        /// </summary>
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Tags for search and categorization;
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Security level required for execution;
        /// </summary>
        public SecurityLevel RequiredSecurityLevel { get; set; } = SecurityLevel.Standard;

        /// <summary>
        /// Whether this procedure can be modified;
        /// </summary>
        public bool IsLocked { get; set; }

        /// <summary>
        /// Creates a deep copy of the procedure definition;
        /// </summary>
        public ProcedureDefinition Clone()
        {
            return new ProcedureDefinition;
            {
                Id = this.Id,
                Name = this.Name,
                Description = this.Description,
                Category = this.Category,
                Steps = this.Steps.Select(s => s.Clone()).ToList(),
                Parameters = new Dictionary<string, ParameterDefinition>(this.Parameters),
                ReturnValue = this.ReturnValue?.Clone(),
                Prerequisites = this.Prerequisites.Select(p => p.Clone()).ToList(),
                Dependencies = new List<string>(this.Dependencies),
                Metrics = this.Metrics.Clone(),
                SuccessCriteria = this.SuccessCriteria.Select(c => c.Clone()).ToList(),
                Version = new Version(this.Version.ToString()),
                CreatedAt = this.CreatedAt,
                ModifiedAt = this.ModifiedAt,
                Tags = new List<string>(this.Tags),
                RequiredSecurityLevel = this.RequiredSecurityLevel,
                IsLocked = this.IsLocked;
            };
        }
    }

    /// <summary>
    /// Represents an individual step in a procedure;
    /// </summary>
    public class ProcedureStep;
    {
        public int Order { get; set; }
        public string Action { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string Description { get; set; }
        public string ExpectedOutcome { get; set; }
        public TimeSpan? Timeout { get; set; }
        public List<string> RequiredTools { get; set; } = new List<string>();
        public List<ValidationRule> Validations { get; set; } = new List<ValidationRule>();
        public RetryPolicy RetryPolicy { get; set; } = new RetryPolicy();
        public bool IsCritical { get; set; } = true;

        public ProcedureStep Clone()
        {
            return new ProcedureStep;
            {
                Order = this.Order,
                Action = this.Action,
                Parameters = new Dictionary<string, object>(this.Parameters),
                Description = this.Description,
                ExpectedOutcome = this.ExpectedOutcome,
                Timeout = this.Timeout,
                RequiredTools = new List<string>(this.RequiredTools),
                Validations = this.Validations.Select(v => v.Clone()).ToList(),
                RetryPolicy = this.RetryPolicy.Clone(),
                IsCritical = this.IsCritical;
            };
        }
    }

    /// <summary>
    /// Parameter definition for procedure inputs;
    /// </summary>
    public class ParameterDefinition;
    {
        public string Name { get; set; }
        public Type DataType { get; set; }
        public string Description { get; set; }
        public bool IsRequired { get; set; } = true;
        public object DefaultValue { get; set; }
        public List<ValidationRule> Validations { get; set; } = new List<ValidationRule>();
        public string[] AllowedValues { get; set; }

        public ParameterDefinition Clone()
        {
            return new ParameterDefinition;
            {
                Name = this.Name,
                DataType = this.DataType,
                Description = this.Description,
                IsRequired = this.IsRequired,
                DefaultValue = this.DefaultValue,
                Validations = this.Validations.Select(v => v.Clone()).ToList(),
                AllowedValues = this.AllowedValues?.ToArray()
            };
        }
    }

    /// <summary>
    /// Return value definition;
    /// </summary>
    public class ReturnDefinition;
    {
        public Type DataType { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public ReturnDefinition Clone()
        {
            return new ReturnDefinition;
            {
                DataType = this.DataType,
                Description = this.Description,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Prerequisite condition;
    /// </summary>
    public class Prerequisite;
    {
        public string Condition { get; set; }
        public string Description { get; set; }
        public string FailureMessage { get; set; }
        public string[] RequiredResources { get; set; }

        public Prerequisite Clone()
        {
            return new Prerequisite;
            {
                Condition = this.Condition,
                Description = this.Description,
                FailureMessage = this.FailureMessage,
                RequiredResources = this.RequiredResources?.ToArray()
            };
        }
    }

    /// <summary>
    /// Procedure performance metrics;
    /// </summary>
    public class ProcedureMetrics;
    {
        public int ExecutionCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public DateTime LastExecuted { get; set; }
        public Dictionary<string, int> ErrorStatistics { get; set; } = new Dictionary<string, int>();

        public ProcedureMetrics Clone()
        {
            return new ProcedureMetrics;
            {
                ExecutionCount = this.ExecutionCount,
                SuccessCount = this.SuccessCount,
                FailureCount = this.FailureCount,
                AverageExecutionTime = this.AverageExecutionTime,
                TotalExecutionTime = this.TotalExecutionTime,
                LastExecuted = this.LastExecuted,
                ErrorStatistics = new Dictionary<string, int>(this.ErrorStatistics)
            };
        }
    }

    /// <summary>
    /// Success criterion for procedure validation;
    /// </summary>
    public class SuccessCriterion;
    {
        public string Name { get; set; }
        public string ValidationRule { get; set; }
        public string Description { get; set; }
        public bool IsMandatory { get; set; } = true;

        public SuccessCriterion Clone()
        {
            return new SuccessCriterion;
            {
                Name = this.Name,
                ValidationRule = this.ValidationRule,
                Description = this.Description,
                IsMandatory = this.IsMandatory;
            };
        }
    }

    /// <summary>
    /// Validation rule for parameters or steps;
    /// </summary>
    public class ValidationRule;
    {
        public string RuleType { get; set; }
        public string Expression { get; set; }
        public string ErrorMessage { get; set; }
        public Severity Severity { get; set; } = Severity.Error;

        public ValidationRule Clone()
        {
            return new ValidationRule;
            {
                RuleType = this.RuleType,
                Expression = this.Expression,
                ErrorMessage = this.ErrorMessage,
                Severity = this.Severity;
            };
        }
    }

    /// <summary>
    /// Retry policy for procedure steps;
    /// </summary>
    public class RetryPolicy;
    {
        public int MaxRetries { get; set; } = 3;
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);
        public double BackoffMultiplier { get; set; } = 2.0;
        public string[] RetryableErrors { get; set; } = Array.Empty<string>();

        public RetryPolicy Clone()
        {
            return new RetryPolicy;
            {
                MaxRetries = this.MaxRetries,
                InitialDelay = this.InitialDelay,
                MaxDelay = this.MaxDelay,
                BackoffMultiplier = this.BackoffMultiplier,
                RetryableErrors = this.RetryableErrors?.ToArray()
            };
        }
    }

    /// <summary>
    /// Security levels for procedure execution;
    /// </summary>
    public enum SecurityLevel;
    {
        Public,
        Standard,
        Confidential,
        Restricted,
        TopSecret;
    }

    /// <summary>
    /// Validation severity levels;
    /// </summary>
    public enum Severity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    /// <summary>
    /// Main Procedure Store implementation;
    /// </summary>
    public class ProcedureStore : IProcedureStore;
    {
        private readonly ILogger<ProcedureStore> _logger;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly Dictionary<string, ProcedureDefinition> _procedures;
        private readonly Dictionary<string, List<string>> _categoryIndex;
        private readonly Dictionary<string, List<string>> _tagIndex;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerSettings _serializerSettings;

        /// <summary>
        /// Initializes a new instance of the ProcedureStore;
        /// </summary>
        public ProcedureStore(ILogger<ProcedureStore> logger, IKnowledgeGraph knowledgeGraph = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeGraph = knowledgeGraph;
            _procedures = new Dictionary<string, ProcedureDefinition>(StringComparer.OrdinalIgnoreCase);
            _categoryIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _tagIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            _serializerSettings = new JsonSerializerSettings;
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            };

            InitializeDefaultProcedures();
            _logger.LogInformation("ProcedureStore initialized with {Count} default procedures", _procedures.Count);
        }

        /// <summary>
        /// Initializes default system procedures;
        /// </summary>
        private void InitializeDefaultProcedures()
        {
            // Add system diagnostic procedure;
            var diagnosticProcedure = new ProcedureDefinition;
            {
                Id = "SYS_DIAG_001",
                Name = "System Diagnostic Check",
                Description = "Performs comprehensive system diagnostic check",
                Category = "System",
                Steps = new List<ProcedureStep>
                {
                    new ProcedureStep;
                    {
                        Order = 1,
                        Action = "CheckSystemResources",
                        Description = "Check available system resources",
                        ExpectedOutcome = "Resource report generated",
                        Timeout = TimeSpan.FromSeconds(30)
                    },
                    new ProcedureStep;
                    {
                        Order = 2,
                        Action = "ValidateCoreServices",
                        Description = "Validate all core services are running",
                        ExpectedOutcome = "Services status report",
                        Timeout = TimeSpan.FromSeconds(60)
                    }
                },
                Tags = new List<string> { "system", "diagnostic", "maintenance" },
                RequiredSecurityLevel = SecurityLevel.Standard;
            };

            AddProcedure(diagnosticProcedure).Wait();
        }

        /// <summary>
        /// Adds a new procedure to the store;
        /// </summary>
        public async Task<bool> AddProcedure(ProcedureDefinition procedure, CancellationToken cancellationToken = default)
        {
            if (procedure == null)
                throw new ArgumentNullException(nameof(procedure));

            if (string.IsNullOrWhiteSpace(procedure.Id))
                throw new ProcedureStoreException("Procedure ID cannot be null or empty");

            if (string.IsNullOrWhiteSpace(procedure.Name))
                throw new ProcedureStoreException("Procedure name cannot be null or empty");

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_procedures.ContainsKey(procedure.Id))
                {
                    _logger.LogWarning("Procedure with ID {ProcedureId} already exists", procedure.Id);
                    return false;
                }

                // Validate procedure structure;
                ValidateProcedure(procedure);

                // Add to main store;
                _procedures[procedure.Id] = procedure;

                // Update indexes;
                UpdateIndexes(procedure);

                // Register with knowledge graph if available;
                if (_knowledgeGraph != null)
                {
                    await RegisterProcedureInKnowledgeGraph(procedure);
                }

                _logger.LogInformation("Procedure {ProcedureName} ({ProcedureId}) added successfully",
                    procedure.Name, procedure.Id);

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Updates an existing procedure;
        /// </summary>
        public async Task<bool> UpdateProcedure(string procedureId, ProcedureDefinition updatedProcedure, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(procedureId))
                throw new ArgumentNullException(nameof(procedureId));

            if (updatedProcedure == null)
                throw new ArgumentNullException(nameof(updatedProcedure));

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_procedures.TryGetValue(procedureId, out var existingProcedure))
                {
                    _logger.LogWarning("Procedure {ProcedureId} not found for update", procedureId);
                    return false;
                }

                if (existingProcedure.IsLocked)
                {
                    throw new ProcedureStoreException($"Procedure {procedureId} is locked and cannot be modified");
                }

                // Validate updated procedure;
                ValidateProcedure(updatedProcedure);

                // Remove old indexes;
                RemoveFromIndexes(existingProcedure);

                // Update procedure;
                updatedProcedure.ModifiedAt = DateTime.UtcNow;
                _procedures[procedureId] = updatedProcedure;

                // Add new indexes;
                UpdateIndexes(updatedProcedure);

                _logger.LogInformation("Procedure {ProcedureId} updated successfully", procedureId);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Retrieves a procedure by ID;
        /// </summary>
        public async Task<ProcedureDefinition> GetProcedure(string procedureId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(procedureId))
                throw new ArgumentNullException(nameof(procedureId));

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_procedures.TryGetValue(procedureId, out var procedure))
                {
                    return procedure.Clone(); // Return clone to prevent external modification;
                }

                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Removes a procedure from the store;
        /// </summary>
        public async Task<bool> RemoveProcedure(string procedureId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(procedureId))
                throw new ArgumentNullException(nameof(procedureId));

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_procedures.TryGetValue(procedureId, out var procedure))
                {
                    return false;
                }

                if (procedure.IsLocked)
                {
                    throw new ProcedureStoreException($"Procedure {procedureId} is locked and cannot be removed");
                }

                // Check dependencies;
                var dependentProcedures = FindDependentProcedures(procedureId);
                if (dependentProcedures.Any())
                {
                    var dependentNames = string.Join(", ", dependentProcedures.Take(5));
                    throw new ProcedureStoreException(
                        $"Cannot remove procedure {procedureId}. It is used by: {dependentNames}" +
                        (dependentProcedures.Count > 5 ? " and others..." : ""));
                }

                // Remove from main store;
                _procedures.Remove(procedureId);

                // Remove from indexes;
                RemoveFromIndexes(procedure);

                _logger.LogInformation("Procedure {ProcedureId} removed successfully", procedureId);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Searches for procedures based on criteria;
        /// </summary>
        public async Task<List<ProcedureDefinition>> SearchProcedures(ProcedureSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            criteria = criteria ?? new ProcedureSearchCriteria();

            await _lock.WaitAsync(cancellationToken);
            try
            {
                var results = _procedures.Values.AsEnumerable();

                // Apply filters;
                if (!string.IsNullOrWhiteSpace(criteria.Name))
                {
                    results = results.Where(p => p.Name.Contains(criteria.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(criteria.Category))
                {
                    results = results.Where(p => string.Equals(p.Category, criteria.Category, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(criteria.Description))
                {
                    results = results.Where(p =>
                        p.Description.Contains(criteria.Description, StringComparison.OrdinalIgnoreCase) ||
                        p.Tags.Any(t => t.Contains(criteria.Description, StringComparison.OrdinalIgnoreCase)));
                }

                if (criteria.Tags?.Any() == true)
                {
                    results = results.Where(p => criteria.Tags.All(tag => p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
                }

                if (criteria.MinSuccessRate.HasValue)
                {
                    results = results.Where(p =>
                        p.Metrics.ExecutionCount == 0 ||
                        ((double)p.Metrics.SuccessCount / p.Metrics.ExecutionCount) >= criteria.MinSuccessRate.Value);
                }

                if (criteria.SecurityLevel.HasValue)
                {
                    results = results.Where(p => p.RequiredSecurityLevel <= criteria.SecurityLevel.Value);
                }

                if (criteria.CreatedAfter.HasValue)
                {
                    results = results.Where(p => p.CreatedAt >= criteria.CreatedAfter.Value);
                }

                if (criteria.ModifiedAfter.HasValue)
                {
                    results = results.Where(p => p.ModifiedAt >= criteria.ModifiedAfter.Value);
                }

                // Apply ordering;
                results = criteria.SortBy switch;
                {
                    ProcedureSortBy.Name => results.OrderBy(p => p.Name),
                    ProcedureSortBy.Category => results.OrderBy(p => p.Category),
                    ProcedureSortBy.Usage => results.OrderByDescending(p => p.Metrics.ExecutionCount),
                    ProcedureSortBy.SuccessRate => results.OrderByDescending(p =>
                        p.Metrics.ExecutionCount == 0 ? 0 : (double)p.Metrics.SuccessCount / p.Metrics.ExecutionCount),
                    ProcedureSortBy.Recent => results.OrderByDescending(p => p.ModifiedAt),
                    _ => results.OrderBy(p => p.Name)
                };

                // Apply paging;
                if (criteria.PageSize > 0)
                {
                    results = results;
                        .Skip((criteria.PageNumber - 1) * criteria.PageSize)
                        .Take(criteria.PageSize);
                }

                return results.Select(p => p.Clone()).ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets procedures by category;
        /// </summary>
        public async Task<List<ProcedureDefinition>> GetProceduresByCategory(string category, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentNullException(nameof(category));

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_categoryIndex.TryGetValue(category, out var procedureIds))
                {
                    return procedureIds;
                        .Select(id => _procedures[id])
                        .Select(p => p.Clone())
                        .ToList();
                }

                return new List<ProcedureDefinition>();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets procedures by tag;
        /// </summary>
        public async Task<List<ProcedureDefinition>> GetProceduresByTag(string tag, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(tag))
                throw new ArgumentNullException(nameof(tag));

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_tagIndex.TryGetValue(tag, out var procedureIds))
                {
                    return procedureIds;
                        .Select(id => _procedures[id])
                        .Select(p => p.Clone())
                        .ToList();
                }

                return new List<ProcedureDefinition>();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets all available categories;
        /// </summary>
        public async Task<List<string>> GetAllCategories(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _categoryIndex.Keys.OrderBy(k => k).ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets all used tags;
        /// </summary>
        public async Task<List<string>> GetAllTags(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _tagIndex.Keys.OrderBy(k => k).ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Validates if a procedure can be executed with given parameters;
        /// </summary>
        public async Task<ProcedureValidationResult> ValidateProcedureExecution(string procedureId, Dictionary<string, object> parameters, CancellationToken cancellationToken = default)
        {
            var procedure = await GetProcedure(procedureId, cancellationToken);
            if (procedure == null)
            {
                return new ProcedureValidationResult;
                {
                    IsValid = false,
                    Errors = new List<string> { $"Procedure {procedureId} not found" }
                };
            }

            var validationErrors = new List<string>();

            // Check required parameters;
            foreach (var paramDef in procedure.Parameters.Values.Where(p => p.IsRequired))
            {
                if (!parameters.ContainsKey(paramDef.Name))
                {
                    validationErrors.Add($"Required parameter '{paramDef.Name}' is missing");
                }
            }

            // Validate parameter values;
            foreach (var param in parameters)
            {
                if (procedure.Parameters.TryGetValue(param.Key, out var paramDef))
                {
                    // Type validation;
                    if (param.Value != null && param.Value.GetType() != paramDef.DataType)
                    {
                        validationErrors.Add($"Parameter '{param.Key}' expects type {paramDef.DataType.Name}, got {param.Value.GetType().Name}");
                    }

                    // Custom validations;
                    foreach (var validation in paramDef.Validations)
                    {
                        if (!ValidateParameter(param.Value, validation))
                        {
                            validationErrors.Add($"Parameter '{param.Key}' failed validation: {validation.ErrorMessage}");
                        }
                    }
                }
                else;
                {
                    validationErrors.Add($"Unknown parameter '{param.Key}'");
                }
            }

            // Check prerequisites;
            foreach (var prerequisite in procedure.Prerequisites)
            {
                if (!await CheckPrerequisite(prerequisite, cancellationToken))
                {
                    validationErrors.Add($"Prerequisite failed: {prerequisite.FailureMessage}");
                }
            }

            return new ProcedureValidationResult;
            {
                IsValid = validationErrors.Count == 0,
                Errors = validationErrors,
                Procedure = procedure;
            };
        }

        /// <summary>
        /// Records procedure execution metrics;
        /// </summary>
        public async Task RecordProcedureExecution(string procedureId, bool success, TimeSpan executionTime, string errorMessage = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(procedureId))
                throw new ArgumentNullException(nameof(procedureId));

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_procedures.TryGetValue(procedureId, out var procedure))
                {
                    var metrics = procedure.Metrics;
                    metrics.ExecutionCount++;

                    if (success)
                    {
                        metrics.SuccessCount++;
                    }
                    else;
                    {
                        metrics.FailureCount++;

                        if (!string.IsNullOrWhiteSpace(errorMessage))
                        {
                            if (metrics.ErrorStatistics.ContainsKey(errorMessage))
                            {
                                metrics.ErrorStatistics[errorMessage]++;
                            }
                            else;
                            {
                                metrics.ErrorStatistics[errorMessage] = 1;
                            }
                        }
                    }

                    metrics.TotalExecutionTime += executionTime;
                    metrics.AverageExecutionTime = TimeSpan.FromTicks(
                        metrics.TotalExecutionTime.Ticks / metrics.ExecutionCount);
                    metrics.LastExecuted = DateTime.UtcNow;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Exports procedure to JSON format;
        /// </summary>
        public async Task<string> ExportProcedure(string procedureId, CancellationToken cancellationToken = default)
        {
            var procedure = await GetProcedure(procedureId, cancellationToken);
            if (procedure == null)
                throw new ProcedureStoreException($"Procedure {procedureId} not found");

            return JsonConvert.SerializeObject(procedure, _serializerSettings);
        }

        /// <summary>
        /// Imports procedure from JSON format;
        /// </summary>
        public async Task<bool> ImportProcedure(string json, CancellationToken cancellationToken = default)
        {
            try
            {
                var procedure = JsonConvert.DeserializeObject<ProcedureDefinition>(json, _serializerSettings);
                if (procedure == null)
                    throw new ProcedureStoreException("Failed to deserialize procedure from JSON");

                return await AddProcedure(procedure, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new ProcedureStoreException("Invalid JSON format for procedure", ex);
            }
        }

        /// <summary>
        /// Finds procedures that depend on the specified procedure;
        /// </summary>
        public async Task<List<string>> FindDependentProcedures(string procedureId, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _procedures.Values;
                    .Where(p => p.Dependencies.Contains(procedureId, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p.Id)
                    .ToList();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets procedure execution statistics;
        /// </summary>
        public async Task<ProcedureStatistics> GetStatistics(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var stats = new ProcedureStatistics;
                {
                    TotalProcedures = _procedures.Count,
                    TotalCategories = _categoryIndex.Count,
                    TotalTags = _tagIndex.Count,
                    TotalExecutions = _procedures.Values.Sum(p => p.Metrics.ExecutionCount),
                    SuccessfulExecutions = _procedures.Values.Sum(p => p.Metrics.SuccessCount),
                    FailedExecutions = _procedures.Values.Sum(p => p.Metrics.FailureCount)
                };

                if (stats.TotalExecutions > 0)
                {
                    stats.SuccessRate = (double)stats.SuccessfulExecutions / stats.TotalExecutions;
                }

                stats.MostUsedProcedure = _procedures.Values;
                    .OrderByDescending(p => p.Metrics.ExecutionCount)
                    .FirstOrDefault()?.Name;

                return stats;
            }
            finally
            {
                _lock.Release();
            }
        }

        #region Private Helper Methods;

        private void ValidateProcedure(ProcedureDefinition procedure)
        {
            var errors = new List<string>();

            if (procedure.Steps == null || procedure.Steps.Count == 0)
            {
                errors.Add("Procedure must have at least one step");
            }

            if (procedure.Steps != null)
            {
                var stepOrders = procedure.Steps.Select(s => s.Order).ToList();
                if (stepOrders.Distinct().Count() != stepOrders.Count)
                {
                    errors.Add("Procedure steps must have unique order values");
                }

                if (procedure.Steps.Any(s => s.Order < 1))
                {
                    errors.Add("Procedure step order must be positive integer");
                }
            }

            if (procedure.Parameters != null)
            {
                foreach (var param in procedure.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(param.Key))
                    {
                        errors.Add("Parameter name cannot be empty");
                    }

                    if (param.Value == null)
                    {
                        errors.Add($"Parameter definition for '{param.Key}' is null");
                    }
                }
            }

            if (errors.Any())
            {
                throw new ProcedureStoreException($"Procedure validation failed: {string.Join("; ", errors)}");
            }
        }

        private void UpdateIndexes(ProcedureDefinition procedure)
        {
            // Category index;
            if (!string.IsNullOrWhiteSpace(procedure.Category))
            {
                if (!_categoryIndex.TryGetValue(procedure.Category, out var categoryList))
                {
                    categoryList = new List<string>();
                    _categoryIndex[procedure.Category] = categoryList;
                }

                if (!categoryList.Contains(procedure.Id))
                {
                    categoryList.Add(procedure.Id);
                }
            }

            // Tag index;
            foreach (var tag in procedure.Tags ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    if (!_tagIndex.TryGetValue(tag, out var tagList))
                    {
                        tagList = new List<string>();
                        _tagIndex[tag] = tagList;
                    }

                    if (!tagList.Contains(procedure.Id))
                    {
                        tagList.Add(procedure.Id);
                    }
                }
            }
        }

        private void RemoveFromIndexes(ProcedureDefinition procedure)
        {
            // Remove from category index;
            if (!string.IsNullOrWhiteSpace(procedure.Category) &&
                _categoryIndex.TryGetValue(procedure.Category, out var categoryList))
            {
                categoryList.Remove(procedure.Id);
                if (categoryList.Count == 0)
                {
                    _categoryIndex.Remove(procedure.Category);
                }
            }

            // Remove from tag index;
            foreach (var tag in procedure.Tags ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag) &&
                    _tagIndex.TryGetValue(tag, out var tagList))
                {
                    tagList.Remove(procedure.Id);
                    if (tagList.Count == 0)
                    {
                        _tagIndex.Remove(tag);
                    }
                }
            }
        }

        private async Task RegisterProcedureInKnowledgeGraph(ProcedureDefinition procedure)
        {
            try
            {
                var procedureNode = new KnowledgeNode;
                {
                    Id = $"procedure:{procedure.Id}",
                    Type = "Procedure",
                    Properties = new Dictionary<string, object>
                    {
                        ["name"] = procedure.Name,
                        ["description"] = procedure.Description,
                        ["category"] = procedure.Category,
                        ["version"] = procedure.Version.ToString(),
                        ["securityLevel"] = procedure.RequiredSecurityLevel.ToString()
                    }
                };

                await _knowledgeGraph.AddNode(procedureNode);

                // Add relationships for dependencies;
                foreach (var dependency in procedure.Dependencies)
                {
                    await _knowledgeGraph.AddRelationship(
                        $"procedure:{procedure.Id}",
                        $"procedure:{dependency}",
                        "depends_on"
                    );
                }

                // Add relationships for category;
                if (!string.IsNullOrWhiteSpace(procedure.Category))
                {
                    await _knowledgeGraph.AddRelationship(
                        $"procedure:{procedure.Id}",
                        $"category:{procedure.Category}",
                        "belongs_to"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register procedure {ProcedureId} in knowledge graph", procedure.Id);
            }
        }

        private bool ValidateParameter(object value, ValidationRule validation)
        {
            // Implement validation logic based on rule type;
            // This is a simplified implementation;
            switch (validation.RuleType?.ToLower())
            {
                case "notnull":
                    return value != null;
                case "notempty":
                    return value is string str && !string.IsNullOrWhiteSpace(str);
                case "range":
                    // Parse expression like "min:0,max:100"
                    return true; // Simplified;
                case "regex":
                    // Use regex pattern from expression;
                    return true; // Simplified;
                default:
                    return true;
            }
        }

        private async Task<bool> CheckPrerequisite(Prerequisite prerequisite, CancellationToken cancellationToken)
        {
            // Implement prerequisite checking logic;
            // This could involve checking system resources, other procedures, etc.
            return await Task.FromResult(true);
        }

        #endregion;
    }

    /// <summary>
    /// Interface for Procedure Store;
    /// </summary>
    public interface IProcedureStore;
    {
        Task<bool> AddProcedure(ProcedureDefinition procedure, CancellationToken cancellationToken = default);
        Task<bool> UpdateProcedure(string procedureId, ProcedureDefinition updatedProcedure, CancellationToken cancellationToken = default);
        Task<ProcedureDefinition> GetProcedure(string procedureId, CancellationToken cancellationToken = default);
        Task<bool> RemoveProcedure(string procedureId, CancellationToken cancellationToken = default);
        Task<List<ProcedureDefinition>> SearchProcedures(ProcedureSearchCriteria criteria, CancellationToken cancellationToken = default);
        Task<List<ProcedureDefinition>> GetProceduresByCategory(string category, CancellationToken cancellationToken = default);
        Task<List<ProcedureDefinition>> GetProceduresByTag(string tag, CancellationToken cancellationToken = default);
        Task<List<string>> GetAllCategories(CancellationToken cancellationToken = default);
        Task<List<string>> GetAllTags(CancellationToken cancellationToken = default);
        Task<ProcedureValidationResult> ValidateProcedureExecution(string procedureId, Dictionary<string, object> parameters, CancellationToken cancellationToken = default);
        Task RecordProcedureExecution(string procedureId, bool success, TimeSpan executionTime, string errorMessage = null, CancellationToken cancellationToken = default);
        Task<string> ExportProcedure(string procedureId, CancellationToken cancellationToken = default);
        Task<bool> ImportProcedure(string json, CancellationToken cancellationToken = default);
        Task<List<string>> FindDependentProcedures(string procedureId, CancellationToken cancellationToken = default);
        Task<ProcedureStatistics> GetStatistics(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Search criteria for procedures;
    /// </summary>
    public class ProcedureSearchCriteria;
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public double? MinSuccessRate { get; set; }
        public SecurityLevel? SecurityLevel { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? ModifiedAfter { get; set; }
        public ProcedureSortBy SortBy { get; set; } = ProcedureSortBy.Name;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    /// <summary>
    /// Sort options for procedures;
    /// </summary>
    public enum ProcedureSortBy;
    {
        Name,
        Category,
        Usage,
        SuccessRate,
        Recent;
    }

    /// <summary>
    /// Procedure validation result;
    /// </summary>
    public class ProcedureValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public ProcedureDefinition Procedure { get; set; }
    }

    /// <summary>
    /// Procedure store statistics;
    /// </summary>
    public class ProcedureStatistics;
    {
        public int TotalProcedures { get; set; }
        public int TotalCategories { get; set; }
        public int TotalTags { get; set; }
        public int TotalExecutions { get; set; }
        public int SuccessfulExecutions { get; set; }
        public int FailedExecutions { get; set; }
        public double SuccessRate { get; set; }
        public string MostUsedProcedure { get; set; }
    }

    /// <summary>
    /// Custom exception for procedure store operations;
    /// </summary>
    public class ProcedureStoreException : Exception
    {
        public ProcedureStoreException() { }
        public ProcedureStoreException(string message) : base(message) { }
        public ProcedureStoreException(string message, Exception innerException) : base(message, innerException) { }
    }
}
