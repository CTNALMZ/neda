using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.AI.KnowledgeBase.SkillRepository.SkillLibrary;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.AI.KnowledgeBase.SkillRepository;
{
    /// <summary>
    /// Skill library for storing, retrieving, and managing AI skills and competencies;
    /// </summary>
    public class SkillLibrary : ISkillLibrary, IDisposable;
    {
        private readonly ILogger<SkillLibrary> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly Dictionary<string, SkillDefinition> _skillDefinitions;
        private readonly Dictionary<string, List<SkillInstance>> _skillInstances;
        private readonly SkillDependencyGraph _dependencyGraph;
        private readonly SkillVersionManager _versionManager;
        private readonly SkillPerformanceTracker _performanceTracker;
        private readonly SemaphoreSlim _accessLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        /// <summary>
        /// Event raised when a new skill is added;
        /// </summary>
        public event EventHandler<SkillAddedEventArgs> SkillAdded;

        /// <summary>
        /// Event raised when a skill is updated;
        /// </summary>
        public event EventHandler<SkillUpdatedEventArgs> SkillUpdated;

        /// <summary>
        /// Event raised when a skill is removed;
        /// </summary>
        public event EventHandler<SkillRemovedEventArgs> SkillRemoved;

        /// <summary>
        /// Initializes a new instance of the SkillLibrary class;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="auditLogger">Audit logger instance</param>
        public SkillLibrary(ILogger<SkillLibrary> logger, IAuditLogger auditLogger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

            _skillDefinitions = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
            _skillInstances = new Dictionary<string, List<SkillInstance>>(StringComparer.OrdinalIgnoreCase);
            _dependencyGraph = new SkillDependencyGraph();
            _versionManager = new SkillVersionManager();
            _performanceTracker = new SkillPerformanceTracker();

            _logger.LogInformation("SkillLibrary initialized");
        }

        /// <summary>
        /// Registers a new skill in the library;
        /// </summary>
        /// <param name="definition">Skill definition</param>
        /// <returns>Registration result</returns>
        public async Task<SkillRegistrationResult> RegisterSkillAsync(SkillDefinition definition)
        {
            Guard.ThrowIfNull(definition, nameof(definition));
            ValidateSkillDefinition(definition);

            await _accessLock.WaitAsync();
            try
            {
                _logger.LogDebug("Registering skill: {SkillName} v{Version}",
                    definition.Name, definition.Version);

                // Check if skill already exists;
                if (_skillDefinitions.ContainsKey(definition.Id))
                {
                    var existing = _skillDefinitions[definition.Id];
                    if (existing.Version >= definition.Version)
                    {
                        throw new SkillLibraryException(
                            $"Skill '{definition.Name}' version {definition.Version} is not newer than existing version {existing.Version}",
                            ErrorCodes.SKILL_VERSION_CONFLICT);
                    }
                }

                // Validate dependencies;
                await ValidateDependenciesAsync(definition);

                // Register in dependency graph;
                _dependencyGraph.AddSkill(definition);

                // Store definition;
                _skillDefinitions[definition.Id] = definition;
                _skillInstances[definition.Id] = new List<SkillInstance>();

                // Update version manager;
                _versionManager.RegisterVersion(definition);

                // Initialize performance tracking;
                _performanceTracker.InitializeSkillTracking(definition.Id);

                // Raise event;
                OnSkillAdded(new SkillAddedEventArgs;
                {
                    SkillId = definition.Id,
                    SkillName = definition.Name,
                    Version = definition.Version,
                    Timestamp = DateTime.UtcNow;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillRegistered",
                    $"Skill '{definition.Name}' v{definition.Version} registered",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = definition.Id,
                        ["skillName"] = definition.Name,
                        ["version"] = definition.Version,
                        ["category"] = definition.Category;
                    });

                _logger.LogInformation("Skill registered successfully: {SkillName} v{Version}",
                    definition.Name, definition.Version);

                return new SkillRegistrationResult;
                {
                    Success = true,
                    SkillId = definition.Id,
                    Message = $"Skill '{definition.Name}' v{definition.Version} registered successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register skill: {SkillName}", definition.Name);
                throw new SkillLibraryException($"Failed to register skill '{definition.Name}'",
                    ErrorCodes.SKILL_REGISTRATION_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets a skill by its ID;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <returns>Skill definition</returns>
        public async Task<SkillDefinition> GetSkillAsync(string skillId)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            await _accessLock.WaitAsync();
            try
            {
                if (!_skillDefinitions.TryGetValue(skillId, out var definition))
                {
                    throw new SkillNotFoundException($"Skill with ID '{skillId}' not found", skillId);
                }

                _logger.LogDebug("Retrieved skill: {SkillId}", skillId);
                return definition;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets all skills in a specific category;
        /// </summary>
        /// <param name="category">Skill category</param>
        /// <returns>List of skills</returns>
        public async Task<IReadOnlyList<SkillDefinition>> GetSkillsByCategoryAsync(SkillCategory category)
        {
            await _accessLock.WaitAsync();
            try
            {
                var skills = _skillDefinitions.Values;
                    .Where(s => s.Category == category)
                    .OrderBy(s => s.Name)
                    .ToList();

                _logger.LogDebug("Retrieved {Count} skills in category: {Category}",
                    skills.Count, category);

                return skills.AsReadOnly();
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Creates a new instance of a skill;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="context">Execution context</param>
        /// <returns>Skill instance</returns>
        public async Task<SkillInstance> CreateSkillInstanceAsync(string skillId, SkillExecutionContext context)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));
            Guard.ThrowIfNull(context, nameof(context));

            await _accessLock.WaitAsync();
            try
            {
                if (!_skillDefinitions.TryGetValue(skillId, out var definition))
                {
                    throw new SkillNotFoundException($"Skill with ID '{skillId}' not found", skillId);
                }

                // Check if skill is enabled;
                if (definition.Status != SkillStatus.Enabled)
                {
                    throw new SkillLibraryException(
                        $"Skill '{definition.Name}' is not enabled. Current status: {definition.Status}",
                        ErrorCodes.SKILL_NOT_ENABLED);
                }

                // Create instance;
                var instance = new SkillInstance;
                {
                    InstanceId = Guid.NewGuid().ToString(),
                    SkillId = skillId,
                    Definition = definition,
                    Context = context,
                    CreatedAt = DateTime.UtcNow,
                    Status = SkillInstanceStatus.Initialized;
                };

                // Add to instances;
                _skillInstances[skillId].Add(instance);

                _logger.LogInformation("Created skill instance: {InstanceId} for skill: {SkillName}",
                    instance.InstanceId, definition.Name);

                return instance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create skill instance for skill: {SkillId}", skillId);
                throw new SkillLibraryException($"Failed to create skill instance for '{skillId}'",
                    ErrorCodes.SKILL_INSTANCE_CREATION_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Executes a skill with the given parameters;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="parameters">Execution parameters</param>
        /// <param name="context">Execution context</param>
        /// <returns>Execution result</returns>
        public async Task<SkillExecutionResult> ExecuteSkillAsync(string skillId,
            Dictionary<string, object> parameters, SkillExecutionContext context)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));
            Guard.ThrowIfNull(context, nameof(context));

            var startTime = DateTime.UtcNow;
            SkillInstance instance = null;

            try
            {
                // Create skill instance;
                instance = await CreateSkillInstanceAsync(skillId, context);
                instance.Status = SkillInstanceStatus.Executing;
                instance.Parameters = parameters ?? new Dictionary<string, object>();

                // Get skill definition;
                var definition = await GetSkillAsync(skillId);

                // Validate parameters;
                ValidateParameters(definition, parameters);

                // Execute pre-execution hooks;
                await ExecutePreHooksAsync(definition, instance);

                // Execute the skill;
                _logger.LogDebug("Executing skill: {SkillName} with instance: {InstanceId}",
                    definition.Name, instance.InstanceId);

                var result = await definition.ExecuteAsync(parameters, context);

                // Update instance;
                instance.Status = SkillInstanceStatus.Completed;
                instance.CompletedAt = DateTime.UtcNow;
                instance.Result = result;

                // Execute post-execution hooks;
                await ExecutePostHooksAsync(definition, instance, result);

                // Track performance;
                var executionTime = DateTime.UtcNow - startTime;
                _performanceTracker.RecordExecution(skillId, executionTime, result.Success);

                // Audit successful execution;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillExecuted",
                    $"Skill '{definition.Name}' executed successfully",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = skillId,
                        ["instanceId"] = instance.InstanceId,
                        ["executionTime"] = executionTime.TotalMilliseconds,
                        ["success"] = result.Success;
                    });

                _logger.LogInformation("Skill executed successfully: {SkillName} in {ExecutionTime}ms",
                    definition.Name, executionTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Skill execution failed: {SkillId}", skillId);

                if (instance != null)
                {
                    instance.Status = SkillInstanceStatus.Failed;
                    instance.Error = ex.Message;
                    instance.CompletedAt = DateTime.UtcNow;
                }

                // Track failure;
                _performanceTracker.RecordFailure(skillId, ex);

                // Audit failed execution;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillExecutionFailed",
                    $"Skill execution failed: {ex.Message}",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = skillId,
                        ["error"] = ex.Message,
                        ["errorType"] = ex.GetType().Name;
                    });

                throw new SkillExecutionException($"Failed to execute skill '{skillId}'",
                    ErrorCodes.SKILL_EXECUTION_FAILED, ex);
            }
        }

        /// <summary>
        /// Updates an existing skill;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="updater">Update function</param>
        /// <returns>Update result</returns>
        public async Task<SkillUpdateResult> UpdateSkillAsync(string skillId,
            Func<SkillDefinition, Task<SkillDefinition>> updater)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));
            Guard.ThrowIfNull(updater, nameof(updater));

            await _accessLock.WaitAsync();
            try
            {
                if (!_skillDefinitions.TryGetValue(skillId, out var currentDefinition))
                {
                    throw new SkillNotFoundException($"Skill with ID '{skillId}' not found", skillId);
                }

                // Create updated definition;
                var updatedDefinition = await updater(currentDefinition);

                // Validate updated definition;
                ValidateSkillDefinition(updatedDefinition);

                // Ensure ID remains the same;
                if (updatedDefinition.Id != skillId)
                {
                    throw new SkillLibraryException(
                        "Cannot change skill ID during update",
                        ErrorCodes.SKILL_UPDATE_VALIDATION_FAILED);
                }

                // Update the definition;
                _skillDefinitions[skillId] = updatedDefinition;
                _versionManager.RegisterVersion(updatedDefinition);

                // Update dependency graph;
                _dependencyGraph.UpdateSkill(updatedDefinition);

                // Raise event;
                OnSkillUpdated(new SkillUpdatedEventArgs;
                {
                    SkillId = skillId,
                    SkillName = updatedDefinition.Name,
                    OldVersion = currentDefinition.Version,
                    NewVersion = updatedDefinition.Version,
                    Timestamp = DateTime.UtcNow;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillUpdated",
                    $"Skill '{updatedDefinition.Name}' updated from v{currentDefinition.Version} to v{updatedDefinition.Version}",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = skillId,
                        ["skillName"] = updatedDefinition.Name,
                        ["oldVersion"] = currentDefinition.Version,
                        ["newVersion"] = updatedDefinition.Version;
                    });

                _logger.LogInformation("Skill updated: {SkillName} v{OldVersion} -> v{NewVersion}",
                    updatedDefinition.Name, currentDefinition.Version, updatedDefinition.Version);

                return new SkillUpdateResult;
                {
                    Success = true,
                    SkillId = skillId,
                    OldVersion = currentDefinition.Version,
                    NewVersion = updatedDefinition.Version;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update skill: {SkillId}", skillId);
                throw new SkillLibraryException($"Failed to update skill '{skillId}'",
                    ErrorCodes.SKILL_UPDATE_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Removes a skill from the library;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="force">Force removal even if dependencies exist</param>
        /// <returns>Removal result</returns>
        public async Task<SkillRemovalResult> RemoveSkillAsync(string skillId, bool force = false)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            await _accessLock.WaitAsync();
            try
            {
                if (!_skillDefinitions.TryGetValue(skillId, out var definition))
                {
                    throw new SkillNotFoundException($"Skill with ID '{skillId}' not found", skillId);
                }

                // Check dependencies;
                var dependents = _dependencyGraph.GetDependents(skillId);
                if (dependents.Any() && !force)
                {
                    var dependentNames = string.Join(", ", dependents.Select(d => _skillDefinitions[d].Name));
                    throw new SkillLibraryException(
                        $"Cannot remove skill '{definition.Name}' because it has dependents: {dependentNames}",
                        ErrorCodes.SKILL_HAS_DEPENDENTS);
                }

                // Remove from collections;
                _skillDefinitions.Remove(skillId);
                _skillInstances.Remove(skillId);
                _dependencyGraph.RemoveSkill(skillId);
                _performanceTracker.RemoveSkillTracking(skillId);

                // Raise event;
                OnSkillRemoved(new SkillRemovedEventArgs;
                {
                    SkillId = skillId,
                    SkillName = definition.Name,
                    Version = definition.Version,
                    Timestamp = DateTime.UtcNow,
                    ForceRemoved = force;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillRemoved",
                    $"Skill '{definition.Name}' v{definition.Version} removed {(force ? "(forced)" : "")}",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = skillId,
                        ["skillName"] = definition.Name,
                        ["version"] = definition.Version,
                        ["force"] = force;
                    });

                _logger.LogInformation("Skill removed: {SkillName} v{Version}",
                    definition.Name, definition.Version);

                return new SkillRemovalResult;
                {
                    Success = true,
                    SkillId = skillId,
                    SkillName = definition.Name,
                    Message = $"Skill '{definition.Name}' v{definition.Version} removed successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove skill: {SkillId}", skillId);
                throw new SkillLibraryException($"Failed to remove skill '{skillId}'",
                    ErrorCodes.SKILL_REMOVAL_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Searches for skills based on criteria;
        /// </summary>
        /// <param name="criteria">Search criteria</param>
        /// <returns>Matching skills</returns>
        public async Task<IReadOnlyList<SkillDefinition>> SearchSkillsAsync(SkillSearchCriteria criteria)
        {
            Guard.ThrowIfNull(criteria, nameof(criteria));

            await _accessLock.WaitAsync();
            try
            {
                var query = _skillDefinitions.Values.AsQueryable();

                if (!string.IsNullOrEmpty(criteria.NamePattern))
                {
                    query = query.Where(s => s.Name.Contains(criteria.NamePattern,
                        StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(criteria.DescriptionPattern))
                {
                    query = query.Where(s => s.Description.Contains(criteria.DescriptionPattern,
                        StringComparison.OrdinalIgnoreCase));
                }

                if (criteria.Category.HasValue)
                {
                    query = query.Where(s => s.Category == criteria.Category.Value);
                }

                if (criteria.MinVersion.HasValue)
                {
                    query = query.Where(s => s.Version >= criteria.MinVersion.Value);
                }

                if (criteria.Status.HasValue)
                {
                    query = query.Where(s => s.Status == criteria.Status.Value);
                }

                if (criteria.Tags?.Any() == true)
                {
                    query = query.Where(s => criteria.Tags.All(tag => s.Tags.Contains(tag)));
                }

                var results = query;
                    .OrderBy(s => s.Name)
                    .ThenByDescending(s => s.Version)
                    .ToList();

                _logger.LogDebug("Skill search returned {Count} results", results.Count);
                return results.AsReadOnly();
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets skill performance statistics;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <returns>Performance statistics</returns>
        public async Task<SkillPerformanceStats> GetPerformanceStatsAsync(string skillId)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            await _accessLock.WaitAsync();
            try
            {
                if (!_skillDefinitions.ContainsKey(skillId))
                {
                    throw new SkillNotFoundException($"Skill with ID '{skillId}' not found", skillId);
                }

                var stats = _performanceTracker.GetStatistics(skillId);

                _logger.LogDebug("Retrieved performance stats for skill: {SkillId}", skillId);
                return stats;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets skills that depend on a specific skill;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <returns>Dependent skills</returns>
        public async Task<IReadOnlyList<SkillDefinition>> GetDependentSkillsAsync(string skillId)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            await _accessLock.WaitAsync();
            try
            {
                var dependentIds = _dependencyGraph.GetDependents(skillId);
                var dependents = dependentIds;
                    .Where(id => _skillDefinitions.ContainsKey(id))
                    .Select(id => _skillDefinitions[id])
                    .ToList();

                _logger.LogDebug("Found {Count} dependents for skill: {SkillId}",
                    dependents.Count, skillId);

                return dependents.AsReadOnly();
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Exports skills to a portable format;
        /// </summary>
        /// <param name="skillIds">Skill identifiers to export</param>
        /// <returns>Exported skill package</returns>
        public async Task<SkillPackage> ExportSkillsAsync(IEnumerable<string> skillIds)
        {
            Guard.ThrowIfNull(skillIds, nameof(skillIds));

            await _accessLock.WaitAsync();
            try
            {
                var skillsToExport = new List<SkillDefinition>();
                var idList = skillIds.ToList();

                foreach (var skillId in idList)
                {
                    if (_skillDefinitions.TryGetValue(skillId, out var definition))
                    {
                        skillsToExport.Add(definition);
                    }
                }

                var package = new SkillPackage;
                {
                    PackageId = Guid.NewGuid().ToString(),
                    ExportDate = DateTime.UtcNow,
                    Skills = skillsToExport,
                    Metadata = new SkillPackageMetadata;
                    {
                        Version = "1.0",
                        ExportSource = "NEDA SkillLibrary",
                        TotalSkills = skillsToExport.Count;
                    }
                };

                _logger.LogInformation("Exported {Count} skills to package: {PackageId}",
                    skillsToExport.Count, package.PackageId);

                return package;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Imports skills from a package;
        /// </summary>
        /// <param name="package">Skill package to import</param>
        /// <param name="conflictResolution">Conflict resolution strategy</param>
        /// <returns>Import result</returns>
        public async Task<SkillImportResult> ImportSkillsAsync(SkillPackage package,
            ImportConflictResolution conflictResolution = ImportConflictResolution.Skip)
        {
            Guard.ThrowIfNull(package, nameof(package));
            ValidateSkillPackage(package);

            await _accessLock.WaitAsync();
            try
            {
                var results = new SkillImportResult;
                {
                    PackageId = package.PackageId,
                    ImportDate = DateTime.UtcNow,
                    ImportedSkills = new List<SkillImportDetail>(),
                    SkippedSkills = new List<SkillImportDetail>(),
                    FailedSkills = new List<SkillImportDetail>()
                };

                foreach (var skill in package.Skills)
                {
                    try
                    {
                        // Check for conflicts;
                        bool exists = _skillDefinitions.ContainsKey(skill.Id);

                        if (exists)
                        {
                            var existing = _skillDefinitions[skill.Id];

                            switch (conflictResolution)
                            {
                                case ImportConflictResolution.Skip:
                                    results.SkippedSkills.Add(new SkillImportDetail;
                                    {
                                        SkillId = skill.Id,
                                        SkillName = skill.Name,
                                        Version = skill.Version,
                                        Reason = $"Skill already exists with version {existing.Version}"
                                    });
                                    continue;

                                case ImportConflictResolution.Overwrite:
                                    // Remove existing skill first;
                                    await RemoveSkillAsync(skill.Id, true);
                                    break;

                                case ImportConflictResolution.Merge:
                                    // Update existing skill;
                                    await UpdateSkillAsync(skill.Id, _ => Task.FromResult(skill));
                                    results.ImportedSkills.Add(new SkillImportDetail;
                                    {
                                        SkillId = skill.Id,
                                        SkillName = skill.Name,
                                        Version = skill.Version,
                                        Action = "Merged"
                                    });
                                    continue;
                            }
                        }

                        // Register new skill;
                        var registrationResult = await RegisterSkillAsync(skill);

                        results.ImportedSkills.Add(new SkillImportDetail;
                        {
                            SkillId = skill.Id,
                            SkillName = skill.Name,
                            Version = skill.Version,
                            Action = exists ? "Overwritten" : "Added"
                        });

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to import skill: {SkillName}", skill.Name);

                        results.FailedSkills.Add(new SkillImportDetail;
                        {
                            SkillId = skill.Id,
                            SkillName = skill.Name,
                            Version = skill.Version,
                            Reason = ex.Message;
                        });
                    }
                }

                // Audit import;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillsImported",
                    $"Imported {results.ImportedSkills.Count} skills from package {package.PackageId}",
                    new Dictionary<string, object>
                    {
                        ["packageId"] = package.PackageId,
                        ["importedCount"] = results.ImportedSkills.Count,
                        ["skippedCount"] = results.SkippedSkills.Count,
                        ["failedCount"] = results.FailedSkills.Count;
                    });

                _logger.LogInformation(
                    "Import completed: {Imported} imported, {Skipped} skipped, {Failed} failed",
                    results.ImportedSkills.Count, results.SkippedSkills.Count, results.FailedSkills.Count);

                return results;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Validates a skill definition;
        /// </summary>
        private void ValidateSkillDefinition(SkillDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Id))
                throw new ArgumentException("Skill ID cannot be null or empty", nameof(definition));

            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new ArgumentException("Skill name cannot be null or empty", nameof(definition));

            if (definition.Version <= 0)
                throw new ArgumentException("Skill version must be greater than 0", nameof(definition));

            if (definition.ExecuteAsync == null)
                throw new ArgumentException("Skill execution delegate cannot be null", nameof(definition));
        }

        /// <summary>
        /// Validates skill dependencies;
        /// </summary>
        private async Task ValidateDependenciesAsync(SkillDefinition definition)
        {
            if (definition.Dependencies?.Any() != true)
                return;

            foreach (var dependency in definition.Dependencies)
            {
                if (!_skillDefinitions.TryGetValue(dependency.SkillId, out var depDefinition))
                {
                    throw new SkillLibraryException(
                        $"Dependency skill '{dependency.SkillId}' not found",
                        ErrorCodes.SKILL_DEPENDENCY_NOT_FOUND);
                }

                if (dependency.MinVersion.HasValue && depDefinition.Version < dependency.MinVersion.Value)
                {
                    throw new SkillLibraryException(
                        $"Dependency '{depDefinition.Name}' version {depDefinition.Version} " +
                        $"is lower than required version {dependency.MinVersion.Value}",
                        ErrorCodes.SKILL_DEPENDENCY_VERSION_MISMATCH);
                }
            }
        }

        /// <summary>
        /// Validates execution parameters against skill definition;
        /// </summary>
        private void ValidateParameters(SkillDefinition definition, Dictionary<string, object> parameters)
        {
            if (definition.RequiredParameters?.Any() == true)
            {
                foreach (var requiredParam in definition.RequiredParameters)
                {
                    if (parameters == null || !parameters.ContainsKey(requiredParam))
                    {
                        throw new SkillLibraryException(
                            $"Required parameter '{requiredParam}' is missing",
                            ErrorCodes.SKILL_PARAMETER_MISSING);
                    }
                }
            }
        }

        /// <summary>
        /// Executes pre-execution hooks;
        /// </summary>
        private async Task ExecutePreHooksAsync(SkillDefinition definition, SkillInstance instance)
        {
            if (definition.PreExecutionHooks?.Any() != true)
                return;

            foreach (var hook in definition.PreExecutionHooks)
            {
                try
                {
                    await hook.ExecuteAsync(instance);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pre-execution hook failed for skill: {SkillName}",
                        definition.Name);
                    // Continue execution even if hook fails;
                }
            }
        }

        /// <summary>
        /// Executes post-execution hooks;
        /// </summary>
        private async Task ExecutePostHooksAsync(SkillDefinition definition,
            SkillInstance instance, SkillExecutionResult result)
        {
            if (definition.PostExecutionHooks?.Any() != true)
                return;

            foreach (var hook in definition.PostExecutionHooks)
            {
                try
                {
                    await hook.ExecuteAsync(instance, result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-execution hook failed for skill: {SkillName}",
                        definition.Name);
                    // Don't throw - hooks shouldn't affect main execution;
                }
            }
        }

        /// <summary>
        /// Validates skill package;
        /// </summary>
        private void ValidateSkillPackage(SkillPackage package)
        {
            if (package.Skills == null)
                throw new ArgumentException("Skill package skills cannot be null", nameof(package));

            foreach (var skill in package.Skills)
            {
                ValidateSkillDefinition(skill);
            }
        }

        /// <summary>
        /// Raises SkillAdded event;
        /// </summary>
        protected virtual void OnSkillAdded(SkillAddedEventArgs e)
        {
            SkillAdded?.Invoke(this, e);
        }

        /// <summary>
        /// Raises SkillUpdated event;
        /// </summary>
        protected virtual void OnSkillUpdated(SkillUpdatedEventArgs e)
        {
            SkillUpdated?.Invoke(this, e);
        }

        /// <summary>
        /// Raises SkillRemoved event;
        /// </summary>
        protected virtual void OnSkillRemoved(SkillRemovedEventArgs e)
        {
            SkillRemoved?.Invoke(this, e);
        }

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
                    _performanceTracker?.Dispose();
                }
                _disposed = true;
            }
        }

        #region Supporting Classes;

        /// <summary>
        /// Skill definition interface;
        /// </summary>
        public interface ISkillLibrary;
        {
            Task<SkillRegistrationResult> RegisterSkillAsync(SkillDefinition definition);
            Task<SkillDefinition> GetSkillAsync(string skillId);
            Task<IReadOnlyList<SkillDefinition>> GetSkillsByCategoryAsync(SkillCategory category);
            Task<SkillExecutionResult> ExecuteSkillAsync(string skillId,
                Dictionary<string, object> parameters, SkillExecutionContext context);
            Task<SkillUpdateResult> UpdateSkillAsync(string skillId,
                Func<SkillDefinition, Task<SkillDefinition>> updater);
            Task<SkillRemovalResult> RemoveSkillAsync(string skillId, bool force = false);
            Task<IReadOnlyList<SkillDefinition>> SearchSkillsAsync(SkillSearchCriteria criteria);
            Task<SkillPerformanceStats> GetPerformanceStatsAsync(string skillId);
            Task<IReadOnlyList<SkillDefinition>> GetDependentSkillsAsync(string skillId);
            Task<SkillPackage> ExportSkillsAsync(IEnumerable<string> skillIds);
            Task<SkillImportResult> ImportSkillsAsync(SkillPackage package,
                ImportConflictResolution conflictResolution);
        }

        /// <summary>
        /// Skill definition;
        /// </summary>
        public class SkillDefinition;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public int Version { get; set; }
            public SkillCategory Category { get; set; }
            public SkillStatus Status { get; set; } = SkillStatus.Enabled;
            public List<string> Tags { get; set; } = new List<string>();
            public List<SkillDependency> Dependencies { get; set; } = new List<SkillDependency>();
            public List<string> RequiredParameters { get; set; } = new List<string>();
            public Dictionary<string, object> DefaultParameters { get; set; } = new Dictionary<string, object>();
            public Func<Dictionary<string, object>, SkillExecutionContext, Task<SkillExecutionResult>> ExecuteAsync { get; set; }
            public List<ISkillHook> PreExecutionHooks { get; set; } = new List<ISkillHook>();
            public List<ISkillHook> PostExecutionHooks { get; set; } = new List<ISkillHook>();
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
            public string Author { get; set; }
            public SkillComplexity Complexity { get; set; }
            public TimeSpan EstimatedDuration { get; set; }
            public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        }

        /// <summary>
        /// Skill execution context;
        /// </summary>
        public class SkillExecutionContext;
        {
            public string RequestId { get; set; }
            public string UserId { get; set; }
            public string SessionId { get; set; }
            public Dictionary<string, object> Environment { get; set; } = new Dictionary<string, object>();
            public Dictionary<string, object> ContextData { get; set; } = new Dictionary<string, object>();
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Skill instance;
        /// </summary>
        public class SkillInstance;
        {
            public string InstanceId { get; set; }
            public string SkillId { get; set; }
            public SkillDefinition Definition { get; set; }
            public SkillExecutionContext Context { get; set; }
            public Dictionary<string, object> Parameters { get; set; }
            public SkillExecutionResult Result { get; set; }
            public SkillInstanceStatus Status { get; set; }
            public string Error { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt - CreatedAt : null;
        }

        /// <summary>
        /// Skill execution result;
        /// </summary>
        public class SkillExecutionResult;
        {
            public bool Success { get; set; }
            public object Data { get; set; }
            public string Message { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
            public List<SkillExecutionLog> Logs { get; set; } = new List<SkillExecutionLog>();
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Skill registration result;
        /// </summary>
        public class SkillRegistrationResult;
        {
            public bool Success { get; set; }
            public string SkillId { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Skill update result;
        /// </summary>
        public class SkillUpdateResult;
        {
            public bool Success { get; set; }
            public string SkillId { get; set; }
            public int OldVersion { get; set; }
            public int NewVersion { get; set; }
        }

        /// <summary>
        /// Skill removal result;
        /// </summary>
        public class SkillRemovalResult;
        {
            public bool Success { get; set; }
            public string SkillId { get; set; }
            public string SkillName { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Skill search criteria;
        /// </summary>
        public class SkillSearchCriteria;
        {
            public string NamePattern { get; set; }
            public string DescriptionPattern { get; set; }
            public SkillCategory? Category { get; set; }
            public int? MinVersion { get; set; }
            public SkillStatus? Status { get; set; }
            public List<string> Tags { get; set; }
        }

        /// <summary>
        /// Skill performance statistics;
        /// </summary>
        public class SkillPerformanceStats;
        {
            public string SkillId { get; set; }
            public int TotalExecutions { get; set; }
            public int SuccessfulExecutions { get; set; }
            public int FailedExecutions { get; set; }
            public double SuccessRate { get; set; }
            public TimeSpan AverageDuration { get; set; }
            public TimeSpan MinDuration { get; set; }
            public TimeSpan MaxDuration { get; set; }
            public DateTime LastExecution { get; set; }
            public Dictionary<string, int> ErrorCounts { get; set; } = new Dictionary<string, int>();
        }

        /// <summary>
        /// Skill package for import/export;
        /// </summary>
        public class SkillPackage;
        {
            public string PackageId { get; set; }
            public DateTime ExportDate { get; set; }
            public List<SkillDefinition> Skills { get; set; }
            public SkillPackageMetadata Metadata { get; set; }
        }

        /// <summary>
        /// Skill import result;
        /// </summary>
        public class SkillImportResult;
        {
            public string PackageId { get; set; }
            public DateTime ImportDate { get; set; }
            public List<SkillImportDetail> ImportedSkills { get; set; }
            public List<SkillImportDetail> SkippedSkills { get; set; }
            public List<SkillImportDetail> FailedSkills { get; set; }
        }

        /// <summary>
        /// Skill hook interface;
        /// </summary>
        public interface ISkillHook;
        {
            Task ExecuteAsync(SkillInstance instance, SkillExecutionResult result = null);
        }

        #endregion;

        #region Enums;

        /// <summary>
        /// Skill categories;
        /// </summary>
        public enum SkillCategory;
        {
            General,
            AI_ML,
            ComputerVision,
            NaturalLanguage,
            GameDevelopment,
            ContentCreation,
            Automation,
            Security,
            Monitoring,
            Communication,
            PersonalAssistant,
            Hardware,
            Cloud,
            Database,
            Networking,
            Utilities;
        }

        /// <summary>
        /// Skill status;
        /// </summary>
        public enum SkillStatus;
        {
            Enabled,
            Disabled,
            Deprecated,
            Experimental,
            Maintenance;
        }

        /// <summary>
        /// Skill instance status;
        /// </summary>
        public enum SkillInstanceStatus;
        {
            Initialized,
            Executing,
            Completed,
            Failed,
            Cancelled;
        }

        /// <summary>
        /// Skill complexity levels;
        /// </summary>
        public enum SkillComplexity;
        {
            Simple,
            Moderate,
            Complex,
            Advanced,
            Expert;
        }

        /// <summary>
        /// Import conflict resolution strategies;
        /// </summary>
        public enum ImportConflictResolution;
        {
            Skip,
            Overwrite,
            Merge;
        }

        #endregion;

        #region Events;

        public class SkillAddedEventArgs : EventArgs;
        {
            public string SkillId { get; set; }
            public string SkillName { get; set; }
            public int Version { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class SkillUpdatedEventArgs : EventArgs;
        {
            public string SkillId { get; set; }
            public string SkillName { get; set; }
            public int OldVersion { get; set; }
            public int NewVersion { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class SkillRemovedEventArgs : EventArgs;
        {
            public string SkillId { get; set; }
            public string SkillName { get; set; }
            public int Version { get; set; }
            public DateTime Timestamp { get; set; }
            public bool ForceRemoved { get; set; }
        }

        #endregion;
    }

    #region Exceptions;

    /// <summary>
    /// Base exception for skill library errors;
    /// </summary>
    public class SkillLibraryException : Exception
    {
        public string ErrorCode { get; }

        public SkillLibraryException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public SkillLibraryException(string message, string errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Exception thrown when a skill is not found;
    /// </summary>
    public class SkillNotFoundException : SkillLibraryException;
    {
        public string SkillId { get; }

        public SkillNotFoundException(string message, string skillId)
            : base(message, ErrorCodes.SKILL_NOT_FOUND)
        {
            SkillId = skillId;
        }
    }

    /// <summary>
    /// Exception thrown when skill execution fails;
    /// </summary>
    public class SkillExecutionException : SkillLibraryException;
    {
        public SkillExecutionException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    #endregion;

    #region Supporting Infrastructure;

    /// <summary>
    /// Manages skill versioning;
    /// </summary>
    internal class SkillVersionManager;
    {
        private readonly Dictionary<string, List<int>> _skillVersions = new Dictionary<string, List<int>>();

        public void RegisterVersion(SkillDefinition definition)
        {
            if (!_skillVersions.ContainsKey(definition.Id))
            {
                _skillVersions[definition.Id] = new List<int>();
            }

            _skillVersions[definition.Id].Add(definition.Version);
            _skillVersions[definition.Id].Sort();
        }

        public IReadOnlyList<int> GetVersions(string skillId)
        {
            return _skillVersions.ContainsKey(skillId)
                ? _skillVersions[skillId].AsReadOnly()
                : new List<int>().AsReadOnly();
        }
    }

    /// <summary>
    /// Tracks skill dependencies;
    /// </summary>
    internal class SkillDependencyGraph;
    {
        private readonly Dictionary<string, List<SkillDependency>> _dependencies =
            new Dictionary<string, List<SkillDependency>>();
        private readonly Dictionary<string, List<string>> _dependents =
            new Dictionary<string, List<string>>();

        public void AddSkill(SkillDefinition definition)
        {
            _dependencies[definition.Id] = definition.Dependencies?.ToList() ?? new List<SkillDependency>();

            // Update dependents for each dependency;
            foreach (var dependency in _dependencies[definition.Id])
            {
                if (!_dependents.ContainsKey(dependency.SkillId))
                {
                    _dependents[dependency.SkillId] = new List<string>();
                }
                _dependents[dependency.SkillId].Add(definition.Id);
            }
        }

        public void UpdateSkill(SkillDefinition definition)
        {
            // Remove old dependencies;
            if (_dependencies.ContainsKey(definition.Id))
            {
                foreach (var oldDependency in _dependencies[definition.Id])
                {
                    if (_dependents.ContainsKey(oldDependency.SkillId))
                    {
                        _dependents[oldDependency.SkillId].Remove(definition.Id);
                    }
                }
            }

            // Add new dependencies;
            AddSkill(definition);
        }

        public void RemoveSkill(string skillId)
        {
            // Remove from dependencies of other skills;
            if (_dependents.ContainsKey(skillId))
            {
                foreach (var dependent in _dependents[skillId])
                {
                    if (_dependencies.ContainsKey(dependent))
                    {
                        _dependencies[dependent] = _dependencies[dependent]
                            .Where(d => d.SkillId != skillId)
                            .ToList();
                    }
                }
                _dependents.Remove(skillId);
            }

            // Remove this skill's dependencies;
            if (_dependencies.ContainsKey(skillId))
            {
                foreach (var dependency in _dependencies[skillId])
                {
                    if (_dependents.ContainsKey(dependency.SkillId))
                    {
                        _dependents[dependency.SkillId].Remove(skillId);
                    }
                }
                _dependencies.Remove(skillId);
            }
        }

        public IReadOnlyList<string> GetDependents(string skillId)
        {
            return _dependents.ContainsKey(skillId)
                ? _dependents[skillId].AsReadOnly()
                : new List<string>().AsReadOnly();
        }
    }

    /// <summary>
    /// Tracks skill performance metrics;
    /// </summary>
    internal class SkillPerformanceTracker : IDisposable
    {
        private readonly Dictionary<string, SkillMetrics> _metrics =
            new Dictionary<string, SkillMetrics>();
        private readonly object _lock = new object();
        private bool _disposed;

        public void InitializeSkillTracking(string skillId)
        {
            lock (_lock)
            {
                _metrics[skillId] = new SkillMetrics();
            }
        }

        public void RecordExecution(string skillId, TimeSpan duration, bool success)
        {
            lock (_lock)
            {
                if (!_metrics.ContainsKey(skillId))
                {
                    _metrics[skillId] = new SkillMetrics();
                }

                var metrics = _metrics[skillId];
                metrics.TotalExecutions++;

                if (success)
                {
                    metrics.SuccessfulExecutions++;
                }
                else;
                {
                    metrics.FailedExecutions++;
                }

                metrics.ExecutionTimes.Add(duration);
                metrics.LastExecution = DateTime.UtcNow;
            }
        }

        public void RecordFailure(string skillId, Exception exception)
        {
            lock (_lock)
            {
                if (!_metrics.ContainsKey(skillId))
                {
                    _metrics[skillId] = new SkillMetrics();
                }

                var metrics = _metrics[skillId];
                var errorType = exception.GetType().Name;

                if (metrics.ErrorCounts.ContainsKey(errorType))
                {
                    metrics.ErrorCounts[errorType]++;
                }
                else;
                {
                    metrics.ErrorCounts[errorType] = 1;
                }
            }
        }

        public SkillPerformanceStats GetStatistics(string skillId)
        {
            lock (_lock)
            {
                if (!_metrics.ContainsKey(skillId))
                {
                    return new SkillPerformanceStats { SkillId = skillId };
                }

                var metrics = _metrics[skillId];

                return new SkillPerformanceStats;
                {
                    SkillId = skillId,
                    TotalExecutions = metrics.TotalExecutions,
                    SuccessfulExecutions = metrics.SuccessfulExecutions,
                    FailedExecutions = metrics.FailedExecutions,
                    SuccessRate = metrics.TotalExecutions > 0;
                        ? (double)metrics.SuccessfulExecutions / metrics.TotalExecutions * 100;
                        : 0,
                    AverageDuration = metrics.ExecutionTimes.Any()
                        ? TimeSpan.FromMilliseconds(metrics.ExecutionTimes.Average(t => t.TotalMilliseconds))
                        : TimeSpan.Zero,
                    MinDuration = metrics.ExecutionTimes.Any()
                        ? metrics.ExecutionTimes.Min()
                        : TimeSpan.Zero,
                    MaxDuration = metrics.ExecutionTimes.Any()
                        ? metrics.ExecutionTimes.Max()
                        : TimeSpan.Zero,
                    LastExecution = metrics.LastExecution,
                    ErrorCounts = new Dictionary<string, int>(metrics.ErrorCounts)
                };
            }
        }

        public void RemoveSkillTracking(string skillId)
        {
            lock (_lock)
            {
                _metrics.Remove(skillId);
            }
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
                    lock (_lock)
                    {
                        _metrics.Clear();
                    }
                }
                _disposed = true;
            }
        }

        private class SkillMetrics;
        {
            public int TotalExecutions { get; set; }
            public int SuccessfulExecutions { get; set; }
            public int FailedExecutions { get; set; }
            public List<TimeSpan> ExecutionTimes { get; } = new List<TimeSpan>();
            public DateTime LastExecution { get; set; }
            public Dictionary<string, int> ErrorCounts { get; } = new Dictionary<string, int>();
        }
    }

    #endregion;

    #region Supporting Types;

    public class SkillDependency;
    {
        public string SkillId { get; set; }
        public int? MinVersion { get; set; }
        public bool Optional { get; set; }
    }

    public class SkillExecutionLog;
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    public class SkillPackageMetadata;
    {
        public string Version { get; set; }
        public string ExportSource { get; set; }
        public int TotalSkills { get; set; }
        public Dictionary<string, string> AdditionalInfo { get; set; } = new Dictionary<string, string>();
    }

    public class SkillImportDetail;
    {
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public int Version { get; set; }
        public string Action { get; set; }
        public string Reason { get; set; }
    }

    #endregion;

    #region Error Codes;

    public static class ErrorCodes;
    {
        // Skill Library Errors;
        public const string SKILL_NOT_FOUND = "SL001";
        public const string SKILL_REGISTRATION_FAILED = "SL002";
        public const string SKILL_UPDATE_FAILED = "SL003";
        public const string SKILL_REMOVAL_FAILED = "SL004";
        public const string SKILL_EXECUTION_FAILED = "SL005";

        // Validation Errors;
        public const string SKILL_VERSION_CONFLICT = "SL101";
        public const string SKILL_DEPENDENCY_NOT_FOUND = "SL102";
        public const string SKILL_DEPENDENCY_VERSION_MISMATCH = "SL103";
        public const string SKILL_PARAMETER_MISSING = "SL104";
        public const string SKILL_UPDATE_VALIDATION_FAILED = "SL105";

        // State Errors;
        public const string SKILL_NOT_ENABLED = "SL201";
        public const string SKILL_HAS_DEPENDENTS = "SL202";
        public const string SKILL_INSTANCE_CREATION_FAILED = "SL203";

        // Import/Export Errors;
        public const string SKILL_PACKAGE_INVALID = "SL301";
        public const string SKILL_IMPORT_FAILED = "SL302";
    }

    #endregion;
}
