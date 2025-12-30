using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.AI.KnowledgeBase.SkillRepository;

namespace NEDA.AI.SkillBuilder;
{
    /// <summary>
    /// Advanced skill builder for constructing complex skills with composition, validation, and optimization;
    /// </summary>
    public class SkillBuilder : ISkillBuilder, IDisposable;
    {
        private readonly ILogger<SkillBuilder> _logger;
        private readonly ISkillLibrary _skillLibrary;
        private readonly IAuditLogger _auditLogger;
        private readonly IServiceProvider _serviceProvider;
        private readonly SkillValidationEngine _validationEngine;
        private readonly SkillOptimizationEngine _optimizationEngine;
        private readonly SkillCompositionEngine _compositionEngine;
        private readonly SkillTemplateRepository _templateRepository;
        private readonly Dictionary<string, SkillConstructionContext> _constructionContexts;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the SkillBuilder class;
        /// </summary>
        public SkillBuilder(
            ILogger<SkillBuilder> logger,
            ISkillLibrary skillLibrary,
            IAuditLogger auditLogger,
            IServiceProvider serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _skillLibrary = skillLibrary ?? throw new ArgumentNullException(nameof(skillLibrary));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            _validationEngine = new SkillValidationEngine(logger);
            _optimizationEngine = new SkillOptimizationEngine(logger);
            _compositionEngine = new SkillCompositionEngine(logger, skillLibrary);
            _templateRepository = new SkillTemplateRepository();
            _constructionContexts = new Dictionary<string, SkillConstructionContext>();

            _logger.LogInformation("SkillBuilder initialized");
        }

        /// <summary>
        /// Creates a new skill from scratch with fluent API;
        /// </summary>
        /// <param name="name">Skill name</param>
        /// <returns>Skill builder instance for fluent configuration</returns>
        public ISkillBuilder CreateSkill(string name)
        {
            Guard.ThrowIfNullOrEmpty(name, nameof(name));

            var contextId = Guid.NewGuid().ToString();
            var context = new SkillConstructionContext;
            {
                ContextId = contextId,
                SkillName = name,
                Status = ConstructionStatus.Initialized;
            };

            _constructionContexts[contextId] = context;

            _logger.LogDebug("Created new skill construction context: {ContextId} for skill: {SkillName}",
                contextId, name);

            return new FluentSkillBuilder(this, contextId);
        }

        /// <summary>
        /// Builds a skill from a template;
        /// </summary>
        /// <param name="templateName">Template name</param>
        /// <param name="parameters">Template parameters</param>
        /// <returns>Built skill definition</returns>
        public async Task<SkillDefinition> BuildFromTemplateAsync(string templateName,
            Dictionary<string, object> parameters)
        {
            Guard.ThrowIfNullOrEmpty(templateName, nameof(templateName));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Building skill from template: {TemplateName}", templateName);

            try
            {
                // Get template;
                var template = await _templateRepository.GetTemplateAsync(templateName);
                if (template == null)
                {
                    throw new SkillBuilderException(
                        $"Template '{templateName}' not found",
                        ErrorCodes.SKILL_TEMPLATE_NOT_FOUND);
                }

                // Apply parameters to template;
                var skillDefinition = await template.ApplyParametersAsync(parameters);

                // Validate the built skill;
                var validationResult = await _validationEngine.ValidateAsync(skillDefinition);
                if (!validationResult.IsValid)
                {
                    throw new SkillBuilderException(
                        $"Template validation failed: {string.Join(", ", validationResult.Errors)}",
                        ErrorCodes.SKILL_TEMPLATE_VALIDATION_FAILED);
                }

                // Optimize the skill;
                var optimizedSkill = await _optimizationEngine.OptimizeAsync(skillDefinition);

                // Register in skill library;
                var registrationResult = await _skillLibrary.RegisterSkillAsync(optimizedSkill);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillBuiltFromTemplate",
                    $"Skill built from template '{templateName}'",
                    new Dictionary<string, object>
                    {
                        ["templateName"] = templateName,
                        ["skillId"] = registrationResult.SkillId,
                        ["skillName"] = optimizedSkill.Name,
                        ["buildTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Skill built from template successfully: {SkillName} in {BuildTime}ms",
                    optimizedSkill.Name, (DateTime.UtcNow - startTime).TotalMilliseconds);

                return optimizedSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build skill from template: {TemplateName}", templateName);
                throw new SkillBuilderException(
                    $"Failed to build skill from template '{templateName}'",
                    ErrorCodes.SKILL_BUILD_FROM_TEMPLATE_FAILED, ex);
            }
        }

        /// <summary>
        /// Composes multiple skills into a complex skill;
        /// </summary>
        /// <param name="compositionPlan">Composition plan</param>
        /// <returns>Composed skill definition</returns>
        public async Task<SkillDefinition> ComposeSkillsAsync(SkillCompositionPlan compositionPlan)
        {
            Guard.ThrowIfNull(compositionPlan, nameof(compositionPlan));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Composing skills: {CompositionName}", compositionPlan.Name);

            try
            {
                // Validate composition plan;
                var validationResult = await _compositionEngine.ValidateCompositionPlanAsync(compositionPlan);
                if (!validationResult.IsValid)
                {
                    throw new SkillBuilderException(
                        $"Composition plan validation failed: {string.Join(", ", validationResult.Errors)}",
                        ErrorCodes.SKILL_COMPOSITION_VALIDATION_FAILED);
                }

                // Build composed skill;
                var composedSkill = await _compositionEngine.ComposeAsync(compositionPlan);

                // Optimize composed skill;
                var optimizedSkill = await _optimizationEngine.OptimizeAsync(composedSkill);

                // Register in skill library;
                var registrationResult = await _skillLibrary.RegisterSkillAsync(optimizedSkill);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillsComposed",
                    $"Composed {compositionPlan.ComponentSkills.Count} skills into '{compositionPlan.Name}'",
                    new Dictionary<string, object>
                    {
                        ["compositionName"] = compositionPlan.Name,
                        ["skillId"] = registrationResult.SkillId,
                        ["componentCount"] = compositionPlan.ComponentSkills.Count,
                        ["compositionTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Skill composition completed: {SkillName} with {ComponentCount} components in {CompositionTime}ms",
                    optimizedSkill.Name, compositionPlan.ComponentSkills.Count,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);

                return optimizedSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compose skills: {CompositionName}", compositionPlan.Name);
                throw new SkillBuilderException(
                    $"Failed to compose skills for '{compositionPlan.Name}'",
                    ErrorCodes.SKILL_COMPOSITION_FAILED, ex);
            }
        }

        /// <summary>
        /// Refactors an existing skill for better performance or structure;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="refactoringOptions">Refactoring options</param>
        /// <returns>Refactored skill definition</returns>
        public async Task<SkillDefinition> RefactorSkillAsync(string skillId,
            SkillRefactoringOptions refactoringOptions)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));
            Guard.ThrowIfNull(refactoringOptions, nameof(refactoringOptions));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Refactoring skill: {SkillId}", skillId);

            try
            {
                // Get existing skill;
                var existingSkill = await _skillLibrary.GetSkillAsync(skillId);

                // Analyze skill for refactoring opportunities;
                var analysis = await AnalyzeSkillForRefactoringAsync(existingSkill, refactoringOptions);

                if (!analysis.HasRefactoringOpportunities)
                {
                    _logger.LogWarning("No refactoring opportunities found for skill: {SkillName}",
                        existingSkill.Name);
                    return existingSkill;
                }

                // Apply refactoring;
                var refactoredSkill = await ApplyRefactoringAsync(existingSkill, analysis, refactoringOptions);

                // Validate refactored skill;
                var validationResult = await _validationEngine.ValidateAsync(refactoredSkill);
                if (!validationResult.IsValid)
                {
                    throw new SkillBuilderException(
                        $"Refactored skill validation failed: {string.Join(", ", validationResult.Errors)}",
                        ErrorCodes.SKILL_REFACTORING_VALIDATION_FAILED);
                }

                // Optimize refactored skill;
                var optimizedSkill = await _optimizationEngine.OptimizeAsync(refactoredSkill);

                // Update skill in library;
                var updateResult = await _skillLibrary.UpdateSkillAsync(skillId, _ => Task.FromResult(optimizedSkill));

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillRefactored",
                    $"Refactored skill '{existingSkill.Name}'",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = skillId,
                        ["skillName"] = existingSkill.Name,
                        ["oldVersion"] = updateResult.OldVersion,
                        ["newVersion"] = updateResult.NewVersion,
                        ["refactoringType"] = refactoringOptions.RefactoringType.ToString(),
                        ["improvementMetrics"] = analysis.ImprovementMetrics,
                        ["refactoringTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Skill refactoring completed: {SkillName} v{OldVersion} -> v{NewVersion} in {RefactoringTime}ms",
                    existingSkill.Name, updateResult.OldVersion, updateResult.NewVersion,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);

                return optimizedSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refactor skill: {SkillId}", skillId);
                throw new SkillBuilderException(
                    $"Failed to refactor skill '{skillId}'",
                    ErrorCodes.SKILL_REFACTORING_FAILED, ex);
            }
        }

        /// <summary>
        /// Generates a skill from natural language description;
        /// </summary>
        /// <param name="description">Natural language description</param>
        /// <param name="context">Generation context</param>
        /// <returns>Generated skill definition</returns>
        public async Task<SkillDefinition> GenerateFromDescriptionAsync(string description,
            SkillGenerationContext context)
        {
            Guard.ThrowIfNullOrEmpty(description, nameof(description));
            Guard.ThrowIfNull(context, nameof(context));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Generating skill from description: {Description}",
                description.Truncate(100));

            try
            {
                // Parse description using NLP;
                var parsedDescription = await ParseSkillDescriptionAsync(description, context);

                // Extract skill components from parsed description;
                var components = await ExtractSkillComponentsAsync(parsedDescription);

                // Generate skill structure;
                var skillStructure = await GenerateSkillStructureAsync(components, context);

                // Build skill definition;
                var skillDefinition = await BuildSkillFromStructureAsync(skillStructure, context);

                // Validate generated skill;
                var validationResult = await _validationEngine.ValidateAsync(skillDefinition);
                if (!validationResult.IsValid)
                {
                    // Try to fix validation issues;
                    skillDefinition = await FixValidationIssuesAsync(skillDefinition, validationResult);

                    // Re-validate;
                    validationResult = await _validationEngine.ValidateAsync(skillDefinition);
                    if (!validationResult.IsValid)
                    {
                        throw new SkillBuilderException(
                            $"Generated skill validation failed: {string.Join(", ", validationResult.Errors)}",
                            ErrorCodes.SKILL_GENERATION_VALIDATION_FAILED);
                    }
                }

                // Optimize generated skill;
                var optimizedSkill = await _optimizationEngine.OptimizeAsync(skillDefinition);

                // Register in skill library;
                var registrationResult = await _skillLibrary.RegisterSkillAsync(optimizedSkill);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillGeneratedFromDescription",
                    $"Skill generated from natural language description",
                    new Dictionary<string, object>
                    {
                        ["descriptionLength"] = description.Length,
                        ["skillId"] = registrationResult.SkillId,
                        ["skillName"] = optimizedSkill.Name,
                        ["generationTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds,
                        ["confidenceScore"] = parsedDescription.ConfidenceScore;
                    });

                _logger.LogInformation(
                    "Skill generated from description successfully: {SkillName} in {GenerationTime}ms",
                    optimizedSkill.Name, (DateTime.UtcNow - startTime).TotalMilliseconds);

                return optimizedSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate skill from description");
                throw new SkillBuilderException(
                    "Failed to generate skill from description",
                    ErrorCodes.SKILL_GENERATION_FAILED, ex);
            }
        }

        /// <summary>
        /// Creates a skill by modifying an existing skill;
        /// </summary>
        /// <param name="baseSkillId">Base skill identifier</param>
        /// <param name="modifications">Modifications to apply</param>
        /// <returns>Modified skill definition</returns>
        public async Task<SkillDefinition> CreateVariantAsync(string baseSkillId,
            SkillModificationSet modifications)
        {
            Guard.ThrowIfNullOrEmpty(baseSkillId, nameof(baseSkillId));
            Guard.ThrowIfNull(modifications, nameof(modifications));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Creating variant of skill: {SkillId}", baseSkillId);

            try
            {
                // Get base skill;
                var baseSkill = await _skillLibrary.GetSkillAsync(baseSkillId);

                // Apply modifications;
                var modifiedSkill = await ApplyModificationsAsync(baseSkill, modifications);

                // Generate new skill ID and name;
                modifiedSkill.Id = GenerateVariantId(baseSkillId, modifications);
                modifiedSkill.Name = $"{baseSkill.Name} - {modifications.VariantName}";
                modifiedSkill.Version = 1; // New variant starts at version 1;

                // Validate modified skill;
                var validationResult = await _validationEngine.ValidateAsync(modifiedSkill);
                if (!validationResult.IsValid)
                {
                    throw new SkillBuilderException(
                        $"Skill variant validation failed: {string.Join(", ", validationResult.Errors)}",
                        ErrorCodes.SKILL_VARIANT_VALIDATION_FAILED);
                }

                // Register as new skill;
                var registrationResult = await _skillLibrary.RegisterSkillAsync(modifiedSkill);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillVariantCreated",
                    $"Created variant of skill '{baseSkill.Name}'",
                    new Dictionary<string, object>
                    {
                        ["baseSkillId"] = baseSkillId,
                        ["variantSkillId"] = registrationResult.SkillId,
                        ["variantName"] = modifications.VariantName,
                        ["modificationCount"] = modifications.Modifications.Count,
                        ["creationTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Skill variant created successfully: {VariantName} based on {BaseSkillName}",
                    modifiedSkill.Name, baseSkill.Name);

                return modifiedSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create skill variant: {BaseSkillId}", baseSkillId);
                throw new SkillBuilderException(
                    $"Failed to create variant of skill '{baseSkillId}'",
                    ErrorCodes.SKILL_VARIANT_CREATION_FAILED, ex);
            }
        }

        /// <summary>
        /// Builds a skill pipeline (sequential execution)
        /// </summary>
        /// <param name="pipelineDefinition">Pipeline definition</param>
        /// <returns>Pipeline skill definition</returns>
        public async Task<SkillDefinition> BuildPipelineAsync(SkillPipelineDefinition pipelineDefinition)
        {
            Guard.ThrowIfNull(pipelineDefinition, nameof(pipelineDefinition));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Building skill pipeline: {PipelineName}", pipelineDefinition.Name);

            try
            {
                // Validate pipeline definition;
                var validationResult = await ValidatePipelineDefinitionAsync(pipelineDefinition);
                if (!validationResult.IsValid)
                {
                    throw new SkillBuilderException(
                        $"Pipeline validation failed: {string.Join(", ", validationResult.Errors)}",
                        ErrorCodes.SKILL_PIPELINE_VALIDATION_FAILED);
                }

                // Build pipeline skill;
                var pipelineSkill = await BuildPipelineSkillAsync(pipelineDefinition);

                // Register pipeline skill;
                var registrationResult = await _skillLibrary.RegisterSkillAsync(pipelineSkill);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillPipelineBuilt",
                    $"Built skill pipeline '{pipelineDefinition.Name}' with {pipelineDefinition.Stages.Count} stages",
                    new Dictionary<string, object>
                    {
                        ["pipelineName"] = pipelineDefinition.Name,
                        ["skillId"] = registrationResult.SkillId,
                        ["stageCount"] = pipelineDefinition.Stages.Count,
                        ["buildTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Skill pipeline built successfully: {PipelineName} with {StageCount} stages",
                    pipelineDefinition.Name, pipelineDefinition.Stages.Count);

                return pipelineSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build skill pipeline: {PipelineName}", pipelineDefinition.Name);
                throw new SkillBuilderException(
                    $"Failed to build skill pipeline '{pipelineDefinition.Name}'",
                    ErrorCodes.SKILL_PIPELINE_BUILD_FAILED, ex);
            }
        }

        /// <summary>
        /// Optimizes an existing skill;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <param name="optimizationLevel">Optimization level</param>
        /// <returns>Optimized skill definition</returns>
        public async Task<SkillDefinition> OptimizeSkillAsync(string skillId,
            OptimizationLevel optimizationLevel = OptimizationLevel.Balanced)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Optimizing skill: {SkillId} at level: {OptimizationLevel}",
                skillId, optimizationLevel);

            try
            {
                // Get existing skill;
                var existingSkill = await _skillLibrary.GetSkillAsync(skillId);

                // Optimize skill;
                var optimizedSkill = await _optimizationEngine.OptimizeAsync(existingSkill, optimizationLevel);

                // Update skill in library;
                var updateResult = await _skillLibrary.UpdateSkillAsync(skillId, _ => Task.FromResult(optimizedSkill));

                // Get performance comparison;
                var performanceComparison = await ComparePerformanceAsync(existingSkill, optimizedSkill);

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "SkillOptimized",
                    $"Optimized skill '{existingSkill.Name}'",
                    new Dictionary<string, object>
                    {
                        ["skillId"] = skillId,
                        ["skillName"] = existingSkill.Name,
                        ["optimizationLevel"] = optimizationLevel.ToString(),
                        ["performanceImprovement"] = performanceImprovement,
                        ["optimizationTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Skill optimization completed: {SkillName} with {Improvement}% improvement",
                    existingSkill.Name, performanceComparison.ImprovementPercentage);

                return optimizedSkill;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to optimize skill: {SkillId}", skillId);
                throw new SkillBuilderException(
                    $"Failed to optimize skill '{skillId}'",
                    ErrorCodes.SKILL_OPTIMIZATION_FAILED, ex);
            }
        }

        /// <summary>
        /// Analyzes a skill for quality metrics;
        /// </summary>
        /// <param name="skillId">Skill identifier</param>
        /// <returns>Skill quality analysis</returns>
        public async Task<SkillQualityAnalysis> AnalyzeSkillQualityAsync(string skillId)
        {
            Guard.ThrowIfNullOrEmpty(skillId, nameof(skillId));

            _logger.LogDebug("Analyzing skill quality: {SkillId}", skillId);

            try
            {
                // Get skill;
                var skill = await _skillLibrary.GetSkillAsync(skillId);

                // Analyze complexity;
                var complexityAnalysis = await AnalyzeComplexityAsync(skill);

                // Analyze dependencies;
                var dependencyAnalysis = await AnalyzeDependenciesAsync(skill);

                // Analyze performance metrics;
                var performanceAnalysis = await _skillLibrary.GetPerformanceStatsAsync(skillId);

                // Calculate quality score;
                var qualityScore = CalculateQualityScore(complexityAnalysis, dependencyAnalysis, performanceAnalysis);

                var analysis = new SkillQualityAnalysis;
                {
                    SkillId = skillId,
                    SkillName = skill.Name,
                    QualityScore = qualityScore,
                    ComplexityAnalysis = complexityAnalysis,
                    DependencyAnalysis = dependencyAnalysis,
                    PerformanceAnalysis = performanceAnalysis,
                    Recommendations = GenerateRecommendations(complexityAnalysis, dependencyAnalysis, performanceAnalysis),
                    AnalyzedAt = DateTime.UtcNow;
                };

                _logger.LogDebug("Skill quality analysis completed: {SkillName} - Score: {QualityScore}",
                    skill.Name, qualityScore);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze skill quality: {SkillId}", skillId);
                throw new SkillBuilderException(
                    $"Failed to analyze skill quality for '{skillId}'",
                    ErrorCodes.SKILL_QUALITY_ANALYSIS_FAILED, ex);
            }
        }

        /// <summary>
        /// Finalizes skill construction and registers it;
        /// </summary>
        /// <param name="contextId">Construction context identifier</param>
        /// <returns>Registered skill definition</returns>
        internal async Task<SkillDefinition> FinalizeConstructionAsync(string contextId)
        {
            Guard.ThrowIfNullOrEmpty(contextId, nameof(contextId));

            if (!_constructionContexts.TryGetValue(contextId, out var context))
            {
                throw new SkillBuilderException(
                    $"Construction context '{contextId}' not found",
                    ErrorCodes.CONSTRUCTION_CONTEXT_NOT_FOUND);
            }

            try
            {
                // Validate constructed skill;
                var validationResult = await _validationEngine.ValidateAsync(context.SkillDefinition);
                if (!validationResult.IsValid)
                {
                    throw new SkillBuilderException(
                        $"Skill validation failed: {string.Join(", ", validationResult.Errors)}",
                        ErrorCodes.SKILL_CONSTRUCTION_VALIDATION_FAILED);
                }

                // Optimize skill;
                var optimizedSkill = await _optimizationEngine.OptimizeAsync(context.SkillDefinition);

                // Register skill;
                var registrationResult = await _skillLibrary.RegisterSkillAsync(optimizedSkill);

                // Update context;
                context.Status = ConstructionStatus.Completed;
                context.CompletedAt = DateTime.UtcNow;
                context.RegisteredSkillId = registrationResult.SkillId;

                // Clean up context;
                _constructionContexts.Remove(contextId);

                _logger.LogInformation(
                    "Skill construction finalized: {SkillName} registered as {SkillId}",
                    optimizedSkill.Name, registrationResult.SkillId);

                return optimizedSkill;
            }
            catch (Exception ex)
            {
                context.Status = ConstructionStatus.Failed;
                context.Error = ex.Message;

                _logger.LogError(ex, "Failed to finalize skill construction: {ContextId}", contextId);
                throw new SkillBuilderException(
                    $"Failed to finalize skill construction for context '{contextId}'",
                    ErrorCodes.SKILL_CONSTRUCTION_FAILED, ex);
            }
        }

        /// <summary>
        /// Updates construction context with skill definition;
        /// </summary>
        internal void UpdateConstructionContext(string contextId, SkillDefinition definition)
        {
            if (_constructionContexts.TryGetValue(contextId, out var context))
            {
                context.SkillDefinition = definition;
                context.Status = ConstructionStatus.Building;
                context.UpdatedAt = DateTime.UtcNow;
            }
        }

        #region Private Helper Methods;

        private async Task<SkillRefactoringAnalysis> AnalyzeSkillForRefactoringAsync(
            SkillDefinition skill, SkillRefactoringOptions options)
        {
            var analysis = new SkillRefactoringAnalysis;
            {
                SkillId = skill.Id,
                SkillName = skill.Name,
                AnalyzedAt = DateTime.UtcNow;
            };

            // Analyze complexity;
            analysis.ComplexityScore = CalculateComplexityScore(skill);

            // Analyze dependencies;
            analysis.DependencyCount = skill.Dependencies?.Count ?? 0;

            // Analyze performance patterns;
            var performanceStats = await _skillLibrary.GetPerformanceStatsAsync(skill.Id);
            analysis.PerformanceScore = CalculatePerformanceScore(performanceStats);

            // Identify refactoring opportunities;
            analysis.Opportunities = IdentifyRefactoringOpportunities(skill, options);

            analysis.HasRefactoringOpportunities = analysis.Opportunities.Any();
            analysis.ImprovementMetrics = CalculateImprovementMetrics(analysis);

            return analysis;
        }

        private async Task<SkillDefinition> ApplyRefactoringAsync(
            SkillDefinition skill, SkillRefactoringAnalysis analysis, SkillRefactoringOptions options)
        {
            var refactoredSkill = skill.Clone();

            foreach (var opportunity in analysis.Opportunities)
            {
                switch (opportunity.Type)
                {
                    case RefactoringType.ExtractMethod:
                        refactoredSkill = await ExtractMethodAsync(refactoredSkill, opportunity);
                        break;
                    case RefactoringType.SimplifyLogic:
                        refactoredSkill = await SimplifyLogicAsync(refactoredSkill, opportunity);
                        break;
                    case RefactoringType.OptimizeDependencies:
                        refactoredSkill = await OptimizeDependenciesAsync(refactoredSkill, opportunity);
                        break;
                    case RefactoringType.ImproveErrorHandling:
                        refactoredSkill = await ImproveErrorHandlingAsync(refactoredSkill, opportunity);
                        break;
                    case RefactoringType.AddCaching:
                        refactoredSkill = await AddCachingAsync(refactoredSkill, opportunity);
                        break;
                }
            }

            // Update version;
            refactoredSkill.Version++;
            refactoredSkill.UpdatedAt = DateTime.UtcNow;

            return refactoredSkill;
        }

        private async Task<ParsedSkillDescription> ParseSkillDescriptionAsync(
            string description, SkillGenerationContext context)
        {
            // This would integrate with NEDA's NLP engine;
            // For now, implementing a simplified version;

            var parsed = new ParsedSkillDescription;
            {
                OriginalText = description,
                ParsedAt = DateTime.UtcNow;
            };

            // Extract action verbs;
            parsed.ActionVerbs = ExtractVerbs(description);

            // Extract objects;
            parsed.Objects = ExtractObjects(description);

            // Extract parameters;
            parsed.Parameters = ExtractParameters(description);

            // Extract conditions;
            parsed.Conditions = ExtractConditions(description);

            // Determine skill category;
            parsed.Category = DetermineCategory(description, context);

            // Calculate confidence score;
            parsed.ConfidenceScore = CalculateParsingConfidence(parsed);

            return parsed;
        }

        private async Task<SkillComponents> ExtractSkillComponentsAsync(ParsedSkillDescription parsedDescription)
        {
            var components = new SkillComponents;
            {
                PrimaryAction = parsedDescription.ActionVerbs.FirstOrDefault(),
                TargetObjects = parsedDescription.Objects,
                RequiredParameters = parsedDescription.Parameters;
                    .Where(p => p.Required)
                    .Select(p => p.Name)
                    .ToList(),
                OptionalParameters = parsedDescription.Parameters;
                    .Where(p => !p.Required)
                    .Select(p => p.Name)
                    .ToList(),
                PreConditions = parsedDescription.Conditions;
                    .Where(c => c.Type == ConditionType.PreCondition)
                    .ToList(),
                PostConditions = parsedDescription.Conditions;
                    .Where(c => c.Type == ConditionType.PostCondition)
                    .ToList()
            };

            // Find similar existing skills for reference;
            components.SimilarSkills = await FindSimilarSkillsAsync(components);

            return components;
        }

        private async Task<SkillStructure> GenerateSkillStructureAsync(
            SkillComponents components, SkillGenerationContext context)
        {
            var structure = new SkillStructure;
            {
                Name = GenerateSkillName(components),
                Description = GenerateSkillDescription(components),
                Category = components.Category ?? SkillCategory.General,
                Complexity = EstimateComplexity(components)
            };

            // Generate execution logic;
            structure.ExecutionLogic = await GenerateExecutionLogicAsync(components, context);

            // Generate parameters;
            structure.Parameters = GenerateParameters(components);

            // Generate dependencies;
            structure.Dependencies = await GenerateDependenciesAsync(components, context);

            // Generate error handling;
            structure.ErrorHandling = GenerateErrorHandling(components);

            return structure;
        }

        private async Task<SkillDefinition> BuildSkillFromStructureAsync(
            SkillStructure structure, SkillGenerationContext context)
        {
            var definition = new SkillDefinition;
            {
                Id = GenerateSkillId(structure.Name),
                Name = structure.Name,
                Description = structure.Description,
                Version = 1,
                Category = structure.Category,
                Status = SkillStatus.Experimental,
                RequiredParameters = structure.Parameters.Required,
                DefaultParameters = structure.Parameters.Defaults,
                Complexity = structure.Complexity,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Author = context.Author;
            };

            // Build execution delegate;
            definition.ExecuteAsync = await BuildExecutionDelegateAsync(structure.ExecutionLogic, context);

            // Add dependencies;
            definition.Dependencies = structure.Dependencies;

            // Add error handling hooks;
            definition.PreExecutionHooks.AddRange(structure.ErrorHandling.PreHooks);
            definition.PostExecutionHooks.AddRange(structure.ErrorHandling.PostHooks);

            // Add metadata;
            definition.Metadata["generated"] = "true";
            definition.Metadata["generationContext"] = context.ContextId;
            definition.Metadata["generationTimestamp"] = DateTime.UtcNow.ToString("O");

            return definition;
        }

        private async Task<SkillDefinition> FixValidationIssuesAsync(
            SkillDefinition skill, SkillValidationResult validationResult)
        {
            var fixedSkill = skill.Clone();

            foreach (var error in validationResult.Errors)
            {
                switch (error.Code)
                {
                    case "MISSING_EXECUTION_LOGIC":
                        fixedSkill.ExecuteAsync = await CreateDefaultExecutionLogicAsync(fixedSkill);
                        break;
                    case "MISSING_REQUIRED_PARAMETERS":
                        fixedSkill.RequiredParameters = await InferRequiredParametersAsync(fixedSkill);
                        break;
                    case "INVALID_DEPENDENCIES":
                        fixedSkill.Dependencies = await FixDependenciesAsync(fixedSkill);
                        break;
                    case "INCOMPLETE_METADATA":
                        fixedSkill.Metadata = await CompleteMetadataAsync(fixedSkill);
                        break;
                }
            }

            return fixedSkill;
        }

        private async Task<SkillDefinition> ApplyModificationsAsync(
            SkillDefinition baseSkill, SkillModificationSet modifications)
        {
            var modifiedSkill = baseSkill.Clone();

            foreach (var modification in modifications.Modifications)
            {
                switch (modification.Type)
                {
                    case ModificationType.AddParameter:
                        modifiedSkill = await AddParameterAsync(modifiedSkill, modification);
                        break;
                    case ModificationType.RemoveParameter:
                        modifiedSkill = await RemoveParameterAsync(modifiedSkill, modification);
                        break;
                    case ModificationType.ModifyLogic:
                        modifiedSkill = await ModifyLogicAsync(modifiedSkill, modification);
                        break;
                    case ModificationType.AddDependency:
                        modifiedSkill = await AddDependencyAsync(modifiedSkill, modification);
                        break;
                    case ModificationType.RemoveDependency:
                        modifiedSkill = await RemoveDependencyAsync(modifiedSkill, modification);
                        break;
                    case ModificationType.ChangeCategory:
                        modifiedSkill = await ChangeCategoryAsync(modifiedSkill, modification);
                        break;
                }
            }

            // Update metadata;
            modifiedSkill.Metadata["isVariant"] = "true";
            modifiedSkill.Metadata["baseSkillId"] = baseSkill.Id;
            modifiedSkill.Metadata["variantCreatedAt"] = DateTime.UtcNow.ToString("O");

            return modifiedSkill;
        }

        private async Task<SkillDefinition> BuildPipelineSkillAsync(SkillPipelineDefinition pipelineDefinition)
        {
            var pipelineSkill = new SkillDefinition;
            {
                Id = GenerateSkillId(pipelineDefinition.Name),
                Name = pipelineDefinition.Name,
                Description = pipelineDefinition.Description,
                Version = 1,
                Category = DeterminePipelineCategory(pipelineDefinition),
                Status = SkillStatus.Enabled,
                Complexity = EstimatePipelineComplexity(pipelineDefinition),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow;
            };

            // Build pipeline execution logic;
            pipelineSkill.ExecuteAsync = await BuildPipelineExecutionLogicAsync(pipelineDefinition);

            // Collect all parameters from stages;
            pipelineSkill.RequiredParameters = CollectPipelineParameters(pipelineDefinition);

            // Collect all dependencies from stages;
            pipelineSkill.Dependencies = await CollectPipelineDependenciesAsync(pipelineDefinition);

            // Add pipeline-specific metadata;
            pipelineSkill.Metadata["pipeline"] = "true";
            pipelineSkill.Metadata["stageCount"] = pipelineDefinition.Stages.Count.ToString();
            pipelineSkill.Metadata["executionOrder"] = string.Join(",",
                pipelineDefinition.Stages.Select(s => s.SkillId));

            return pipelineSkill;
        }

        private async Task<PerformanceComparison> ComparePerformanceAsync(
            SkillDefinition before, SkillDefinition after)
        {
            // This would require actual execution and benchmarking;
            // For now, return estimated comparison;

            var comparison = new PerformanceComparison;
            {
                BeforeSkill = before,
                AfterSkill = after,
                ComparedAt = DateTime.UtcNow;
            };

            // Estimate improvements based on complexity reduction;
            var beforeComplexity = CalculateComplexityScore(before);
            var afterComplexity = CalculateComplexityScore(after);

            comparison.ComplexityReduction = beforeComplexity - afterComplexity;
            comparison.EstimatedPerformanceImprovement =
                (beforeComplexity - afterComplexity) / beforeComplexity * 100;

            // Analyze dependency improvements;
            var beforeDeps = before.Dependencies?.Count ?? 0;
            var afterDeps = after.Dependencies?.Count ?? 0;
            comparison.DependencyReduction = beforeDeps - afterDeps;

            return comparison;
        }

        private string GenerateVariantId(string baseSkillId, SkillModificationSet modifications)
        {
            var hash = modifications.GetHashCode().ToString("X8");
            return $"{baseSkillId}_variant_{hash}";
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
                    _optimizationEngine?.Dispose();
                    _compositionEngine?.Dispose();
                    _templateRepository?.Dispose();

                    // Clean up construction contexts;
                    foreach (var context in _constructionContexts.Values)
                    {
                        if (context.Status == ConstructionStatus.Building)
                        {
                            _logger.LogWarning(
                                "Cleaning up unfinished construction context: {ContextId}",
                                context.ContextId);
                        }
                    }
                    _constructionContexts.Clear();
                }
                _disposed = true;
            }
        }

        #region Supporting Classes;

        /// <summary>
        /// Skill builder interface;
        /// </summary>
        public interface ISkillBuilder;
        {
            Task<SkillDefinition> BuildFromTemplateAsync(string templateName,
                Dictionary<string, object> parameters);
            Task<SkillDefinition> ComposeSkillsAsync(SkillCompositionPlan compositionPlan);
            Task<SkillDefinition> RefactorSkillAsync(string skillId, SkillRefactoringOptions refactoringOptions);
            Task<SkillDefinition> GenerateFromDescriptionAsync(string description,
                SkillGenerationContext context);
            Task<SkillDefinition> CreateVariantAsync(string baseSkillId, SkillModificationSet modifications);
            Task<SkillDefinition> BuildPipelineAsync(SkillPipelineDefinition pipelineDefinition);
            Task<SkillDefinition> OptimizeSkillAsync(string skillId, OptimizationLevel optimizationLevel);
            Task<SkillQualityAnalysis> AnalyzeSkillQualityAsync(string skillId);
        }

        /// <summary>
        /// Fluent skill builder for chainable configuration;
        /// </summary>
        public interface IFluentSkillBuilder;
        {
            IFluentSkillBuilder WithDescription(string description);
            IFluentSkillBuilder InCategory(SkillCategory category);
            IFluentSkillBuilder WithParameter(string name, Type type, bool required = true,
                object defaultValue = null);
            IFluentSkillBuilder WithDependency(string skillId, int? minVersion = null, bool optional = false);
            IFluentSkillBuilder WithExecutionLogic(Func<Dictionary<string, object>,
                SkillExecutionContext, Task<SkillExecutionResult>> executionLogic);
            IFluentSkillBuilder WithPreHook(ISkillHook hook);
            IFluentSkillBuilder WithPostHook(ISkillHook hook);
            IFluentSkillBuilder WithTag(string tag);
            IFluentSkillBuilder WithMetadata(string key, string value);
            Task<SkillDefinition> BuildAsync();
        }

        /// <summary>
        /// Skill composition plan;
        /// </summary>
        public class SkillCompositionPlan;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public List<SkillComponent> ComponentSkills { get; set; } = new List<SkillComponent>();
            public CompositionStrategy Strategy { get; set; }
            public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
            public SkillCategory Category { get; set; } = SkillCategory.General;
            public string Author { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Skill refactoring options;
        /// </summary>
        public class SkillRefactoringOptions;
        {
            public RefactoringType RefactoringType { get; set; }
            public RefactoringScope Scope { get; set; }
            public bool PreserveBehavior { get; set; } = true;
            public int MaxComplexityReduction { get; set; } = 30; // Percentage;
            public List<string> TargetAreas { get; set; } = new List<string>();
            public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Skill generation context;
        /// </summary>
        public class SkillGenerationContext;
        {
            public string ContextId { get; set; } = Guid.NewGuid().ToString();
            public string Author { get; set; }
            public SkillCategory PreferredCategory { get; set; }
            public List<string> AvailableSkillIds { get; set; } = new List<string>();
            public Dictionary<string, object> Constraints { get; set; } = new Dictionary<string, object>();
            public GenerationQuality QualityTarget { get; set; } = GenerationQuality.Standard;
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Skill modification set;
        /// </summary>
        public class SkillModificationSet;
        {
            public string VariantName { get; set; }
            public List<SkillModification> Modifications { get; set; } = new List<SkillModification>();
            public string Description { get; set; }
            public bool InheritMetadata { get; set; } = true;
            public Dictionary<string, object> AdditionalConfiguration { get; set; } =
                new Dictionary<string, object>();
        }

        /// <summary>
        /// Skill pipeline definition;
        /// </summary>
        public class SkillPipelineDefinition;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public List<PipelineStage> Stages { get; set; } = new List<PipelineStage>();
            public PipelineExecutionMode ExecutionMode { get; set; } = PipelineExecutionMode.Sequential;
            public ErrorHandlingStrategy ErrorHandling { get; set; } = ErrorHandlingStrategy.ContinueOnError;
            public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Skill quality analysis;
        /// </summary>
        public class SkillQualityAnalysis;
        {
            public string SkillId { get; set; }
            public string SkillName { get; set; }
            public double QualityScore { get; set; }
            public SkillComplexityAnalysis ComplexityAnalysis { get; set; }
            public SkillDependencyAnalysis DependencyAnalysis { get; set; }
            public SkillPerformanceStats PerformanceAnalysis { get; set; }
            public List<SkillRecommendation> Recommendations { get; set; } = new List<SkillRecommendation>();
            public DateTime AnalyzedAt { get; set; }
        }

        /// <summary>
        /// Skill construction context;
        /// </summary>
        internal class SkillConstructionContext;
        {
            public string ContextId { get; set; }
            public string SkillName { get; set; }
            public SkillDefinition SkillDefinition { get; set; }
            public ConstructionStatus Status { get; set; }
            public string RegisteredSkillId { get; set; }
            public string Error { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
            public DateTime? CompletedAt { get; set; }
        }

        #endregion;

        #region Enums;

        /// <summary>
        /// Refactoring types;
        /// </summary>
        public enum RefactoringType;
        {
            ExtractMethod,
            SimplifyLogic,
            OptimizeDependencies,
            ImproveErrorHandling,
            AddCaching,
            ParallelizeExecution,
            AddValidation,
            RemoveRedundancy;
        }

        /// <summary>
        /// Refactoring scope;
        /// </summary>
        public enum RefactoringScope;
        {
            Local,
            Component,
            Global;
        }

        /// <summary>
        /// Composition strategies;
        /// </summary>
        public enum CompositionStrategy;
        {
            Sequential,
            Parallel,
            Conditional,
            Loop,
            Pipeline,
            Workflow;
        }

        /// <summary>
        /// Generation quality levels;
        /// </summary>
        public enum GenerationQuality;
        {
            Basic,
            Standard,
            Advanced,
            Production;
        }

        /// <summary>
        /// Modification types;
        /// </summary>
        public enum ModificationType;
        {
            AddParameter,
            RemoveParameter,
            ModifyParameter,
            ModifyLogic,
            AddDependency,
            RemoveDependency,
            ChangeCategory,
            ChangeComplexity,
            AddValidation,
            AddHook;
        }

        /// <summary>
        /// Pipeline execution modes;
        /// </summary>
        public enum PipelineExecutionMode;
        {
            Sequential,
            Parallel,
            Conditional,
            ForkJoin;
        }

        /// <summary>
        /// Error handling strategies for pipelines;
        /// </summary>
        public enum ErrorHandlingStrategy;
        {
            StopOnError,
            ContinueOnError,
            RetryOnError,
            FallbackOnError;
        }

        /// <summary>
        /// Optimization levels;
        /// </summary>
        public enum OptimizationLevel;
        {
            None,
            Basic,
            Balanced,
            Aggressive,
            Maximum;
        }

        /// <summary>
        /// Construction status;
        /// </summary>
        internal enum ConstructionStatus;
        {
            Initialized,
            Building,
            Validating,
            Optimizing,
            Completed,
            Failed;
        }

        #endregion;
    }

    #region Exceptions;

    /// <summary>
    /// Base exception for skill builder errors;
    /// </summary>
    public class SkillBuilderException : Exception
    {
        public string ErrorCode { get; }

        public SkillBuilderException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public SkillBuilderException(string message, string errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    #endregion;

    #region Supporting Infrastructure;

    /// <summary>
    /// Fluent skill builder implementation;
    /// </summary>
    internal class FluentSkillBuilder : ISkillBuilder.IFluentSkillBuilder;
    {
        private readonly SkillBuilder _skillBuilder;
        private readonly string _contextId;
        private readonly SkillDefinition _skillDefinition;

        public FluentSkillBuilder(SkillBuilder skillBuilder, string contextId)
        {
            _skillBuilder = skillBuilder;
            _contextId = contextId;
            _skillDefinition = new SkillDefinition;
            {
                Id = Guid.NewGuid().ToString(),
                Version = 1,
                Status = SkillStatus.Experimental,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow;
            };
        }

        public ISkillBuilder.IFluentSkillBuilder WithDescription(string description)
        {
            _skillDefinition.Description = description;
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder InCategory(SkillCategory category)
        {
            _skillDefinition.Category = category;
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithParameter(string name, Type type,
            bool required = true, object defaultValue = null)
        {
            if (required)
            {
                _skillDefinition.RequiredParameters.Add(name);
            }

            if (defaultValue != null)
            {
                _skillDefinition.DefaultParameters[name] = defaultValue;
            }

            // Store type information in metadata;
            _skillDefinition.Metadata[$"param_type_{name}"] = type.FullName;

            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithDependency(string skillId, int? minVersion = null,
            bool optional = false)
        {
            _skillDefinition.Dependencies.Add(new SkillDependency;
            {
                SkillId = skillId,
                MinVersion = minVersion,
                Optional = optional;
            });
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithExecutionLogic(
            Func<Dictionary<string, object>, SkillExecutionContext, Task<SkillExecutionResult>> executionLogic)
        {
            _skillDefinition.ExecuteAsync = executionLogic;
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithPreHook(ISkillHook hook)
        {
            _skillDefinition.PreExecutionHooks.Add(hook);
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithPostHook(ISkillHook hook)
        {
            _skillDefinition.PostExecutionHooks.Add(hook);
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithTag(string tag)
        {
            _skillDefinition.Tags.Add(tag);
            return this;
        }

        public ISkillBuilder.IFluentSkillBuilder WithMetadata(string key, string value)
        {
            _skillDefinition.Metadata[key] = value;
            return this;
        }

        public async Task<SkillDefinition> BuildAsync()
        {
            // Update the construction context;
            _skillBuilder.UpdateConstructionContext(_contextId, _skillDefinition);

            // Finalize construction;
            return await _skillBuilder.FinalizeConstructionAsync(_contextId);
        }
    }

    /// <summary>
    /// Skill validation engine;
    /// </summary>
    internal class SkillValidationEngine : IDisposable
    {
        private readonly ILogger<SkillValidationEngine> _logger;
        private readonly List<IValidationRule> _validationRules;
        private bool _disposed;

        public SkillValidationEngine(ILogger<SkillValidationEngine> logger)
        {
            _logger = logger;
            _validationRules = LoadValidationRules();
        }

        public async Task<SkillValidationResult> ValidateAsync(SkillDefinition skill)
        {
            var result = new SkillValidationResult;
            {
                SkillId = skill.Id,
                SkillName = skill.Name,
                ValidatedAt = DateTime.UtcNow;
            };

            foreach (var rule in _validationRules)
            {
                try
                {
                    var ruleResult = await rule.ValidateAsync(skill);
                    result.RuleResults.Add(ruleResult);

                    if (!ruleResult.IsValid)
                    {
                        result.Errors.AddRange(ruleResult.Errors);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Validation rule failed: {RuleName}", rule.Name);
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "VALIDATION_RULE_FAILED",
                        Message = $"Validation rule '{rule.Name}' failed: {ex.Message}",
                        Severity = ValidationSeverity.Warning;
                    });
                }
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);

            _logger.LogDebug("Skill validation completed: {SkillName} - Valid: {IsValid}",
                skill.Name, result.IsValid);

            return result;
        }

        private List<IValidationRule> LoadValidationRules()
        {
            var rules = new List<IValidationRule>
            {
                new RequiredFieldsValidationRule(),
                new ExecutionLogicValidationRule(),
                new DependencyValidationRule(),
                new ParameterValidationRule(),
                new ComplexityValidationRule(),
                new SecurityValidationRule(),
                new PerformanceValidationRule()
            };

            return rules;
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
                    // Clean up rules if needed;
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Skill optimization engine;
    /// </summary>
    internal class SkillOptimizationEngine : IDisposable
    {
        private readonly ILogger<SkillOptimizationEngine> _logger;
        private readonly List<IOptimizationStrategy> _optimizationStrategies;
        private bool _disposed;

        public SkillOptimizationEngine(ILogger<SkillOptimizationEngine> logger)
        {
            _logger = logger;
            _optimizationStrategies = LoadOptimizationStrategies();
        }

        public async Task<SkillDefinition> OptimizeAsync(SkillDefinition skill,
            OptimizationLevel level = OptimizationLevel.Balanced)
        {
            var optimizedSkill = skill.Clone();
            var optimizationReport = new OptimizationReport;
            {
                SkillId = skill.Id,
                SkillName = skill.Name,
                OptimizationLevel = level,
                StartedAt = DateTime.UtcNow;
            };

            foreach (var strategy in _optimizationStrategies)
            {
                if (strategy.ApplicableLevel <= level)
                {
                    try
                    {
                        var strategyResult = await strategy.OptimizeAsync(optimizedSkill);
                        optimizedSkill = strategyResult.OptimizedSkill;
                        optimizationReport.StrategyResults.Add(strategyResult);

                        _logger.LogDebug(
                            "Applied optimization strategy: {StrategyName} for skill: {SkillName}",
                            strategy.Name, skill.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Optimization strategy failed: {StrategyName} for skill: {SkillName}",
                            strategy.Name, skill.Name);
                    }
                }
            }

            optimizationReport.CompletedAt = DateTime.UtcNow;
            optimizationReport.OptimizationDuration = optimizationReport.CompletedAt - optimizationReport.StartedAt;

            // Store optimization report in metadata;
            optimizedSkill.Metadata["optimizationReport"] = System.Text.Json.JsonSerializer.Serialize(optimizationReport);
            optimizedSkill.Metadata["lastOptimized"] = DateTime.UtcNow.ToString("O");

            _logger.LogInformation(
                "Skill optimization completed: {SkillName} with {StrategyCount} strategies in {Duration}ms",
                skill.Name, optimizationReport.StrategyResults.Count,
                optimizationReport.OptimizationDuration.TotalMilliseconds);

            return optimizedSkill;
        }

        private List<IOptimizationStrategy> LoadOptimizationStrategies()
        {
            var strategies = new List<IOptimizationStrategy>
            {
                new DependencyOptimizationStrategy(OptimizationLevel.Basic),
                new ParameterOptimizationStrategy(OptimizationLevel.Basic),
                new LogicSimplificationStrategy(OptimizationLevel.Balanced),
                new PerformanceOptimizationStrategy(OptimizationLevel.Balanced),
                new MemoryOptimizationStrategy(OptimizationLevel.Aggressive),
                new ParallelizationStrategy(OptimizationLevel.Aggressive),
                new CachingStrategy(OptimizationLevel.Maximum)
            };

            return strategies;
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
                    // Clean up strategies if needed;
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Skill composition engine;
    /// </summary>
    internal class SkillCompositionEngine : IDisposable
    {
        private readonly ILogger<SkillCompositionEngine> _logger;
        private readonly ISkillLibrary _skillLibrary;
        private bool _disposed;

        public SkillCompositionEngine(ILogger<SkillCompositionEngine> logger, ISkillLibrary skillLibrary)
        {
            _logger = logger;
            _skillLibrary = skillLibrary;
        }

        public async Task<SkillValidationResult> ValidateCompositionPlanAsync(SkillCompositionPlan plan)
        {
            var result = new SkillValidationResult;
            {
                ValidatedAt = DateTime.UtcNow;
            };

            // Validate component skills exist;
            foreach (var component in plan.ComponentSkills)
            {
                try
                {
                    await _skillLibrary.GetSkillAsync(component.SkillId);
                }
                catch (SkillNotFoundException)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "COMPONENT_SKILL_NOT_FOUND",
                        Message = $"Component skill '{component.SkillId}' not found",
                        Severity = ValidationSeverity.Error;
                    });
                }
            }

            // Validate dependencies don't create cycles;
            var cycleDetectionResult = await DetectCyclesAsync(plan);
            if (cycleDetectionResult.HasCycles)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "DEPENDENCY_CYCLE_DETECTED",
                    Message = $"Dependency cycle detected: {string.Join(" -> ", cycleDetectionResult.Cycle)}",
                    Severity = ValidationSeverity.Error;
                });
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);
            return result;
        }

        public async Task<SkillDefinition> ComposeAsync(SkillCompositionPlan plan)
        {
            var composedSkill = new SkillDefinition;
            {
                Id = GenerateComposedSkillId(plan),
                Name = plan.Name,
                Description = plan.Description,
                Version = 1,
                Category = plan.Category,
                Status = SkillStatus.Experimental,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Author = plan.Author;
            };

            // Build execution logic based on composition strategy;
            composedSkill.ExecuteAsync = await BuildComposedExecutionLogicAsync(plan);

            // Collect all parameters from components;
            composedSkill.RequiredParameters = await CollectRequiredParametersAsync(plan);

            // Collect all dependencies;
            composedSkill.Dependencies = await CollectDependenciesAsync(plan);

            // Add composition metadata;
            composedSkill.Metadata["composed"] = "true";
            composedSkill.Metadata["compositionStrategy"] = plan.Strategy.ToString();
            composedSkill.Metadata["componentCount"] = plan.ComponentSkills.Count.ToString();
            composedSkill.Metadata["compositionPlanId"] = Guid.NewGuid().ToString();

            return composedSkill;
        }

        private async Task<CycleDetectionResult> DetectCyclesAsync(SkillCompositionPlan plan)
        {
            // Implement cycle detection using graph algorithms;
            // This is a simplified version;

            var result = new CycleDetectionResult();
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            foreach (var component in plan.ComponentSkills)
            {
                if (await HasCycleAsync(component.SkillId, visited, recursionStack, plan))
                {
                    result.HasCycles = true;
                    break;
                }
            }

            return result;
        }

        private async Task<bool> HasCycleAsync(string skillId, HashSet<string> visited,
            HashSet<string> recursionStack, SkillCompositionPlan plan)
        {
            if (recursionStack.Contains(skillId))
                return true;

            if (visited.Contains(skillId))
                return false;

            visited.Add(skillId);
            recursionStack.Add(skillId);

            // Get skill dependencies;
            var skill = await _skillLibrary.GetSkillAsync(skillId);
            if (skill.Dependencies != null)
            {
                foreach (var dependency in skill.Dependencies)
                {
                    if (await HasCycleAsync(dependency.SkillId, visited, recursionStack, plan))
                        return true;
                }
            }

            recursionStack.Remove(skillId);
            return false;
        }

        private async Task<Func<Dictionary<string, object>, SkillExecutionContext,
            Task<SkillExecutionResult>>> BuildComposedExecutionLogicAsync(SkillCompositionPlan plan)
        {
            return async (parameters, context) =>
            {
                var results = new List<SkillExecutionResult>();
                var executionContext = context;

                switch (plan.Strategy)
                {
                    case CompositionStrategy.Sequential:
                        foreach (var component in plan.ComponentSkills)
                        {
                            var result = await _skillLibrary.ExecuteSkillAsync(
                                component.SkillId,
                                MergeParameters(parameters, component.Parameters),
                                executionContext);
                            results.Add(result);

                            if (!result.Success && component.BreakOnFailure)
                                break;

                            // Pass result to next component if configured;
                            if (component.PassResultToNext)
                            {
                                executionContext.ContextData["previousResult"] = result.Data;
                            }
                        }
                        break;

                    case CompositionStrategy.Parallel:
                        var tasks = plan.ComponentSkills.Select(component =>
                            _skillLibrary.ExecuteSkillAsync(
                                component.SkillId,
                                MergeParameters(parameters, component.Parameters),
                                executionContext));
                        results.AddRange(await Task.WhenAll(tasks));
                        break;

                    case CompositionStrategy.Conditional:
                        // Implement conditional execution based on parameters;
                        var conditionResult = EvaluateCondition(plan.Configuration, parameters);
                        if (conditionResult.ShouldExecute)
                        {
                            var component = plan.ComponentSkills.FirstOrDefault(
                                c => c.Id == conditionResult.ComponentId);
                            if (component != null)
                            {
                                var result = await _skillLibrary.ExecuteSkillAsync(
                                    component.SkillId,
                                    MergeParameters(parameters, component.Parameters),
                                    executionContext);
                                results.Add(result);
                            }
                        }
                        break;

                    case CompositionStrategy.Pipeline:
                        object pipelineData = null;
                        foreach (var component in plan.ComponentSkills)
                        {
                            var componentParams = MergeParameters(parameters, component.Parameters);
                            if (pipelineData != null)
                            {
                                componentParams["pipelineInput"] = pipelineData;
                            }

                            var result = await _skillLibrary.ExecuteSkillAsync(
                                component.SkillId, componentParams, executionContext);
                            results.Add(result);

                            if (result.Success)
                            {
                                pipelineData = result.Data;
                            }
                            else;
                            {
                                break;
                            }
                        }
                        break;
                }

                return CombineResults(results, plan);
            };
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
    /// Skill template repository;
    /// </summary>
    internal class SkillTemplateRepository : IDisposable
    {
        private readonly Dictionary<string, SkillTemplate> _templates;
        private bool _disposed;

        public SkillTemplateRepository()
        {
            _templates = LoadTemplates();
        }

        public async Task<SkillTemplate> GetTemplateAsync(string templateName)
        {
            if (_templates.TryGetValue(templateName, out var template))
            {
                return template;
            }

            // Try to load template dynamically;
            var dynamicTemplate = await LoadDynamicTemplateAsync(templateName);
            if (dynamicTemplate != null)
            {
                _templates[templateName] = dynamicTemplate;
                return dynamicTemplate;
            }

            return null;
        }

        private Dictionary<string, SkillTemplate> LoadTemplates()
        {
            // Load built-in templates;
            var templates = new Dictionary<string, SkillTemplate>
            {
                ["DataProcessor"] = CreateDataProcessorTemplate(),
                ["APIClient"] = CreateAPIClientTemplate(),
                ["FileHandler"] = CreateFileHandlerTemplate(),
                ["ValidationSkill"] = CreateValidationSkillTemplate(),
                ["Transformer"] = CreateTransformerTemplate(),
                ["Analyzer"] = CreateAnalyzerTemplate(),
                ["Generator"] = CreateGeneratorTemplate(),
                ["Validator"] = CreateValidatorTemplate()
            };

            return templates;
        }

        private async Task<SkillTemplate> LoadDynamicTemplateAsync(string templateName)
        {
            // Implement dynamic template loading from database or files;
            return null;
        }

        private SkillTemplate CreateDataProcessorTemplate()
        {
            return new SkillTemplate;
            {
                Name = "DataProcessor",
                Description = "Template for data processing skills",
                Category = SkillCategory.General,
                Parameters = new Dictionary<string, TemplateParameter>
                {
                    ["inputType"] = new TemplateParameter { Type = typeof(string), Required = true },
                    ["outputType"] = new TemplateParameter { Type = typeof(string), Required = true },
                    ["processingLogic"] = new TemplateParameter { Type = typeof(string), Required = true }
                },
                Builder = async (parameters) =>
                {
                    var skill = new SkillDefinition;
                    {
                        Name = $"DataProcessor_{parameters["inputType"]}_to_{parameters["outputType"]}",
                        Description = $"Processes {parameters["inputType"]} into {parameters["outputType"]}",
                        Category = SkillCategory.General,
                        Version = 1;
                    };

                    // Build execution logic based on parameters;
                    skill.ExecuteAsync = await BuildDataProcessorLogicAsync(parameters);

                    return skill;
                }
            };
        }

        private async Task<Func<Dictionary<string, object>, SkillExecutionContext,
            Task<SkillExecutionResult>>> BuildDataProcessorLogicAsync(
            Dictionary<string, object> templateParameters)
        {
            return async (parameters, context) =>
            {
                // Implementation would vary based on template parameters;
                await Task.Delay(1); // Placeholder;
                return new SkillExecutionResult;
                {
                    Success = true,
                    Message = "Data processing completed"
                };
            };
        }

        // Other template creation methods...

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
                    _templates.Clear();
                }
                _disposed = true;
            }
        }
    }

    #endregion;

    #region Supporting Types;

    public class SkillComponent;
    {
        public string Id { get; set; }
        public string SkillId { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public bool BreakOnFailure { get; set; } = true;
        public bool PassResultToNext { get; set; } = false;
        public int Order { get; set; }
    }

    public class SkillModification;
    {
        public ModificationType Type { get; set; }
        public string Target { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public string Description { get; set; }
    }

    public class PipelineStage;
    {
        public string StageId { get; set; }
        public string SkillId { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> ParameterMapping { get; set; } = new Dictionary<string, string>();
        public int Order { get; set; }
        public TimeSpan? Timeout { get; set; }
        public int RetryCount { get; set; } = 0;
    }

    public class SkillComplexityAnalysis;
    {
        public int CyclomaticComplexity { get; set; }
        public int CognitiveComplexity { get; set; }
        public int ParameterCount { get; set; }
        public int DependencyCount { get; set; }
        public int LogicDepth { get; set; }
        public double OverallScore { get; set; }
        public ComplexityLevel Level { get; set; }
    }

    public class SkillDependencyAnalysis;
    {
        public List<SkillDependencyInfo> Dependencies { get; set; } = new List<SkillDependencyInfo>();
        public List<SkillDependencyInfo> Dependents { get; set; } = new List<SkillDependencyInfo>();
        public bool HasCircularDependencies { get; set; }
        public int Depth { get; set; }
        public int Breadth { get; set; }
        public DependencyHealth Health { get; set; }
    }

    public class SkillRecommendation;
    {
        public RecommendationType Type { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public PriorityLevel Priority { get; set; }
        public List<string> Actions { get; set; } = new List<string>();
        public double EstimatedEffort { get; set; } // In hours;
        public double ExpectedImpact { get; set; } // Percentage improvement;
    }

    public class SkillValidationResult;
    {
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public List<ValidationRuleResult> RuleResults { get; set; } = new List<ValidationRuleResult>();
        public DateTime ValidatedAt { get; set; }
    }

    public class SkillRefactoringAnalysis;
    {
        public string SkillId { get; set; }
        public string SkillName { get; set; }
        public double ComplexityScore { get; set; }
        public int DependencyCount { get; set; }
        public double PerformanceScore { get; set; }
        public List<RefactoringOpportunity> Opportunities { get; set; } = new List<RefactoringOpportunity>();
        public bool HasRefactoringOpportunities { get; set; }
        public Dictionary<string, double> ImprovementMetrics { get; set; } = new Dictionary<string, double>();
        public DateTime AnalyzedAt { get; set; }
    }

    public class ParsedSkillDescription;
    {
        public string OriginalText { get; set; }
        public List<string> ActionVerbs { get; set; } = new List<string>();
        public List<string> Objects { get; set; } = new List<string>();
        public List<ParameterInfo> Parameters { get; set; } = new List<ParameterInfo>();
        public List<ConditionInfo> Conditions { get; set; } = new List<ConditionInfo>();
        public SkillCategory Category { get; set; }
        public double ConfidenceScore { get; set; }
        public DateTime ParsedAt { get; set; }
    }

    public class SkillStructure;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public SkillCategory Category { get; set; }
        public SkillComplexity Complexity { get; set; }
        public SkillExecutionLogic ExecutionLogic { get; set; }
        public SkillParameters Parameters { get; set; }
        public List<SkillDependency> Dependencies { get; set; } = new List<SkillDependency>();
        public ErrorHandlingConfiguration ErrorHandling { get; set; }
    }

    public class PerformanceComparison;
    {
        public SkillDefinition BeforeSkill { get; set; }
        public SkillDefinition AfterSkill { get; set; }
        public double ComplexityReduction { get; set; }
        public int DependencyReduction { get; set; }
        public double EstimatedPerformanceImprovement { get; set; }
        public DateTime ComparedAt { get; set; }
    }

    public class SkillTemplate;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public SkillCategory Category { get; set; }
        public Dictionary<string, TemplateParameter> Parameters { get; set; } =
            new Dictionary<string, TemplateParameter>();
        public Func<Dictionary<string, object>, Task<SkillDefinition>> Builder { get; set; }

        public async Task<SkillDefinition> ApplyParametersAsync(Dictionary<string, object> parameters)
        {
            // Validate parameters;
            foreach (var requiredParam in Parameters.Where(p => p.Value.Required))
            {
                if (!parameters.ContainsKey(requiredParam.Key))
                {
                    throw new ArgumentException($"Required parameter '{requiredParam.Key}' is missing");
                }
            }

            return await Builder(parameters);
        }
    }

    #endregion;

    #region Enums;

    public enum ComplexityLevel;
    {
        VerySimple,
        Simple,
        Moderate,
        Complex,
        VeryComplex;
    }

    public enum DependencyHealth;
    {
        Excellent,
        Good,
        Fair,
        Poor,
        Critical;
    }

    public enum RecommendationType;
    {
        Refactor,
        Optimize,
        Simplify,
        AddValidation,
        ImproveErrorHandling,
        AddDocumentation,
        UpdateDependencies,
        SecurityImprovement;
    }

    public enum PriorityLevel;
    {
        Critical,
        High,
        Medium,
        Low,
        Informational;
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
        // Skill Builder Errors;
        public const string SKILL_TEMPLATE_NOT_FOUND = "SB001";
        public const string SKILL_TEMPLATE_VALIDATION_FAILED = "SB002";
        public const string SKILL_BUILD_FROM_TEMPLATE_FAILED = "SB003";
        public const string SKILL_COMPOSITION_VALIDATION_FAILED = "SB004";
        public const string SKILL_COMPOSITION_FAILED = "SB005";
        public const string SKILL_REFACTORING_VALIDATION_FAILED = "SB006";
        public const string SKILL_REFACTORING_FAILED = "SB007";
        public const string SKILL_GENERATION_VALIDATION_FAILED = "SB008";
        public const string SKILL_GENERATION_FAILED = "SB009";
        public const string SKILL_VARIANT_VALIDATION_FAILED = "SB010";
        public const string SKILL_VARIANT_CREATION_FAILED = "SB011";
        public const string SKILL_PIPELINE_VALIDATION_FAILED = "SB012";
        public const string SKILL_PIPELINE_BUILD_FAILED = "SB013";
        public const string SKILL_OPTIMIZATION_FAILED = "SB014";
        public const string SKILL_QUALITY_ANALYSIS_FAILED = "SB015";

        // Construction Errors;
        public const string CONSTRUCTION_CONTEXT_NOT_FOUND = "SB101";
        public const string SKILL_CONSTRUCTION_VALIDATION_FAILED = "SB102";
        public const string SKILL_CONSTRUCTION_FAILED = "SB103";
    }

    #endregion;
}
