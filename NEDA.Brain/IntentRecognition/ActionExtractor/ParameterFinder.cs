// NEDA.Brain/IntentRecognition/ActionExtractor/ParameterFinder.cs;

using Microsoft.Extensions.Logging;
using NEDA.AI.NaturalLanguage;
using NEDA.Brain.IntentRecognition.ActionExtractor.Models;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.NLP_Engine;
using NEDA.Common.Utilities;
using NEDA.Monitoring.MetricsCollector;
using NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.Brain.IntentRecognition.ActionExtractor;
{
    /// <summary>
    /// Advanced Parameter Finder for extracting and validating parameters from natural language input;
    /// Uses NLP, pattern matching, and context-aware extraction techniques;
    /// </summary>
    public class ParameterFinder : IParameterFinder, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger<ParameterFinder> _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly INLPService _nlpService;
        private readonly IKnowledgeBaseService _knowledgeBase;
        private readonly ParameterExtractionConfiguration _configuration;

        private readonly Dictionary<string, ParameterPattern> _parameterPatterns;
        private readonly Dictionary<string, EntityType> _entityTypes;
        private readonly Dictionary<string, ParameterValidator> _parameterValidators;
        private readonly SemaphoreSlim _extractionLock = new SemaphoreSlim(1, 1);
        private readonly List<ExtractionHistory> _extractionHistory;

        private bool _isInitialized = false;
        private bool _isDisposed = false;

        /// <summary>
        /// Current extraction mode;
        /// </summary>
        public ExtractionMode CurrentExtractionMode { get; private set; }

        /// <summary>
        /// Extraction statistics and performance metrics;
        /// </summary>
        public ParameterExtractionMetrics Metrics { get; private set; }

        /// <summary>
        /// Event raised when parameter extraction completes;
        /// </summary>
        public event EventHandler<ParameterExtractionCompletedEventArgs> ExtractionCompleted;

        /// <summary>
        /// Event raised when ambiguous parameters are detected;
        /// </summary>
        public event EventHandler<AmbiguousParameterDetectedEventArgs> AmbiguousParameterDetected;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of the ParameterFinder;
        /// </summary>
        public ParameterFinder(
            ILogger<ParameterFinder> logger,
            IMetricsCollector metricsCollector,
            INLPService nlpService,
            IKnowledgeBaseService knowledgeBase,
            ParameterExtractionConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _nlpService = nlpService ?? throw new ArgumentNullException(nameof(nlpService));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _configuration = configuration ?? ParameterExtractionConfiguration.Default;

            _parameterPatterns = new Dictionary<string, ParameterPattern>();
            _entityTypes = new Dictionary<string, EntityType>();
            _parameterValidators = new Dictionary<string, ParameterValidator>();
            _extractionHistory = new List<ExtractionHistory>();
            Metrics = new ParameterExtractionMetrics();
            CurrentExtractionMode = ExtractionMode.Standard;

            _logger.LogInformation("ParameterFinder initialized with {ExtractionMode} mode", CurrentExtractionMode);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the parameter finder with patterns and validators;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _extractionLock.WaitAsync(cancellationToken);

                if (_isInitialized)
                {
                    _logger.LogWarning("ParameterFinder is already initialized");
                    return;
                }

                _logger.LogInformation("Initializing Parameter Finder...");

                // Load parameter patterns;
                await LoadParameterPatternsAsync(cancellationToken);

                // Load entity types;
                await LoadEntityTypesAsync(cancellationToken);

                // Load parameter validators;
                await LoadParameterValidatorsAsync(cancellationToken);

                // Initialize NLP service;
                await InitializeNLPServiceAsync(cancellationToken);

                // Load domain-specific extraction rules;
                await LoadDomainRulesAsync(cancellationToken);

                _isInitialized = true;

                _logger.LogInformation("Parameter Finder initialization completed successfully");
                await _metricsCollector.RecordMetricAsync("parameter_finder_initialized", 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Parameter Finder");
                throw new ParameterFinderInitializationException("Failed to initialize parameter extraction engine", ex);
            }
            finally
            {
                _extractionLock.Release();
            }
        }

        /// <summary>
        /// Finds and extracts parameters from natural language text;
        /// </summary>
        public async Task<ParameterExtractionResult> FindParametersAsync(
            ExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();
            ValidateRequest(request);

            try
            {
                var extractionId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogInformation("Starting parameter extraction for: {Text}",
                    request.Text.Length > 50 ? request.Text.Substring(0, 50) + "..." : request.Text);

                // Create extraction context;
                var context = new ExtractionContext;
                {
                    ExtractionId = extractionId,
                    Request = request,
                    Timestamp = DateTime.UtcNow,
                    ExtractionMode = CurrentExtractionMode;
                };

                // Perform multi-stage parameter extraction;
                var extractedParameters = await ExtractParametersMultiStageAsync(context, cancellationToken);

                // Resolve parameter references and dependencies;
                var resolvedParameters = await ResolveParameterReferencesAsync(extractedParameters, context, cancellationToken);

                // Validate extracted parameters;
                var validationResults = await ValidateParametersAsync(resolvedParameters, context, cancellationToken);

                // Handle ambiguous parameters;
                var ambiguityResolution = await HandleAmbiguitiesAsync(resolvedParameters, validationResults, context, cancellationToken);

                // Type conversion and normalization;
                var normalizedParameters = await NormalizeParametersAsync(ambiguityResolution.ResolvedParameters, context, cancellationToken);

                // Create extraction result;
                var result = new ParameterExtractionResult;
                {
                    ExtractionId = extractionId,
                    Request = request,
                    ExtractedParameters = extractedParameters,
                    ResolvedParameters = resolvedParameters,
                    ValidatedParameters = validationResults.ValidParameters,
                    NormalizedParameters = normalizedParameters,
                    AmbiguityResolution = ambiguityResolution,
                    ValidationResults = validationResults,
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow,
                    Confidence = CalculateExtractionConfidence(normalizedParameters, validationResults),
                    Success = validationResults.IsSuccessful;
                };

                // Store extraction in history;
                StoreExtractionHistory(result);

                // Record metrics;
                await RecordExtractionMetricsAsync(result, startTime);

                // Raise completion event;
                OnExtractionCompleted(new ParameterExtractionCompletedEventArgs;
                {
                    Result = result,
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Parameter extraction completed: {ParameterCount} parameters found with confidence {Confidence:F2}",
                    normalizedParameters.Count, result.Confidence);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Parameter extraction was cancelled for request: {RequestId}", request.RequestId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parameter extraction failed for request: {RequestId}", request.RequestId);
                await _metricsCollector.RecordErrorAsync("parameter_extraction_failed", ex);
                throw new ParameterExtractionException($"Parameter extraction failed for request {request.RequestId}", ex);
            }
        }

        /// <summary>
        /// Extracts parameters with semantic understanding and context;
        /// </summary>
        public async Task<SemanticExtractionResult> ExtractParametersSemanticallyAsync(
            SemanticExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var semanticId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting semantic parameter extraction: {SemanticId}", semanticId);

                // Perform deep semantic analysis;
                var semanticAnalysis = await PerformSemanticAnalysisAsync(request, cancellationToken);

                // Extract parameters using semantic roles;
                var semanticParameters = await ExtractSemanticRolesAsync(request, semanticAnalysis, cancellationToken);

                // Apply semantic constraints;
                var constrainedParameters = await ApplySemanticConstraintsAsync(semanticParameters, request, cancellationToken);

                // Resolve semantic ambiguities;
                var resolvedSemantics = await ResolveSemanticAmbiguitiesAsync(constrainedParameters, semanticAnalysis, cancellationToken);

                // Generate semantic representations;
                var semanticRepresentations = await GenerateSemanticRepresentationsAsync(resolvedSemantics, cancellationToken);

                var result = new SemanticExtractionResult;
                {
                    SemanticId = semanticId,
                    Request = request,
                    SemanticAnalysis = semanticAnalysis,
                    SemanticParameters = semanticParameters,
                    ConstrainedParameters = constrainedParameters,
                    ResolvedSemantics = resolvedSemantics,
                    SemanticRepresentations = semanticRepresentations,
                    ExtractionDepth = CalculateExtractionDepth(semanticAnalysis),
                    SemanticCoherence = CalculateSemanticCoherence(resolvedSemantics),
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Semantic extraction completed: Depth = {Depth}, Coherence = {Coherence:F2}",
                    result.ExtractionDepth, result.SemanticCoherence);

                await _metricsCollector.RecordMetricAsync("semantic_extraction_completed", 1);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Semantic parameter extraction failed");
                throw new ParameterExtractionException("Semantic parameter extraction failed", ex);
            }
        }

        /// <summary>
        /// Extracts parameters from structured or semi-structured text;
        /// </summary>
        public async Task<StructuredExtractionResult> ExtractStructuredParametersAsync(
            StructuredExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var structuredId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting structured parameter extraction: {StructuredId}", structuredId);

                // Parse structure based on format;
                var parsedStructure = await ParseStructureAsync(request, cancellationToken);

                // Extract parameters from structure;
                var structuredParameters = await ExtractFromStructureAsync(parsedStructure, request, cancellationToken);

                // Validate structure integrity;
                var integrityCheck = await ValidateStructureIntegrityAsync(structuredParameters, parsedStructure, cancellationToken);

                // Handle missing or incomplete parameters;
                var completedParameters = await HandleMissingParametersAsync(structuredParameters, request, cancellationToken);

                // Normalize structured data;
                var normalizedStructure = await NormalizeStructuredDataAsync(completedParameters, request, cancellationToken);

                var result = new StructuredExtractionResult;
                {
                    StructuredId = structuredId,
                    Request = request,
                    ParsedStructure = parsedStructure,
                    StructuredParameters = structuredParameters,
                    IntegrityCheck = integrityCheck,
                    CompletedParameters = completedParameters,
                    NormalizedStructure = normalizedStructure,
                    StructureType = request.StructureType,
                    ExtractionCompleteness = CalculateExtractionCompleteness(completedParameters, request),
                    StructureIntegrity = integrityCheck.IntegrityScore,
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Structured extraction completed: Completeness = {Completeness:F2}, Integrity = {Integrity:F2}",
                    result.ExtractionCompleteness, result.StructureIntegrity);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Structured parameter extraction failed");
                throw new ParameterExtractionException("Structured parameter extraction failed", ex);
            }
        }

        /// <summary>
        /// Extracts temporal parameters and time expressions;
        /// </summary>
        public async Task<TemporalExtractionResult> ExtractTemporalParametersAsync(
            TemporalExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var temporalId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting temporal parameter extraction: {TemporalId}", temporalId);

                // Parse temporal expressions;
                var temporalExpressions = await ParseTemporalExpressionsAsync(request, cancellationToken);

                // Resolve temporal references;
                var resolvedTemporals = await ResolveTemporalReferencesAsync(temporalExpressions, request, cancellationToken);

                // Extract time intervals and durations;
                var timeIntervals = await ExtractTimeIntervalsAsync(resolvedTemporals, cancellationToken);

                // Handle relative time expressions;
                var relativeTimes = await HandleRelativeTimesAsync(resolvedTemporals, request, cancellationToken);

                // Normalize temporal representations;
                var normalizedTemporals = await NormalizeTemporalDataAsync(timeIntervals, relativeTimes, cancellationToken);

                // Calculate temporal constraints;
                var temporalConstraints = await CalculateTemporalConstraintsAsync(normalizedTemporals, cancellationToken);

                var result = new TemporalExtractionResult;
                {
                    TemporalId = temporalId,
                    Request = request,
                    TemporalExpressions = temporalExpressions,
                    ResolvedTemporals = resolvedTemporals,
                    TimeIntervals = timeIntervals,
                    RelativeTimes = relativeTimes,
                    NormalizedTemporals = normalizedTemporals,
                    TemporalConstraints = temporalConstraints,
                    TemporalResolution = CalculateTemporalResolution(normalizedTemporals),
                    TimeZoneHandling = request.TimeZoneHandling,
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Temporal extraction completed: Resolution = {Resolution}, Constraints = {ConstraintCount}",
                    result.TemporalResolution, result.TemporalConstraints.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Temporal parameter extraction failed");
                throw new ParameterExtractionException("Temporal parameter extraction failed", ex);
            }
        }

        /// <summary>
        /// Extracts numeric parameters and quantitative expressions;
        /// </summary>
        public async Task<NumericExtractionResult> ExtractNumericParametersAsync(
            NumericExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var numericId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting numeric parameter extraction: {NumericId}", numericId);

                // Parse numeric expressions;
                var numericExpressions = await ParseNumericExpressionsAsync(request, cancellationToken);

                // Extract quantities and units;
                var quantities = await ExtractQuantitiesAsync(numericExpressions, cancellationToken);

                // Handle ranges and intervals;
                var ranges = await ExtractRangesAsync(numericExpressions, cancellationToken);

                // Normalize numeric values;
                var normalizedNumerics = await NormalizeNumericValuesAsync(quantities, ranges, cancellationToken);

                // Calculate derived numeric values;
                var derivedValues = await CalculateDerivedValuesAsync(normalizedNumerics, request, cancellationToken);

                // Validate numeric constraints;
                var numericConstraints = await ValidateNumericConstraintsAsync(normalizedNumerics, derivedValues, request, cancellationToken);

                var result = new NumericExtractionResult;
                {
                    NumericId = numericId,
                    Request = request,
                    NumericExpressions = numericExpressions,
                    Quantities = quantities,
                    Ranges = ranges,
                    NormalizedNumerics = normalizedNumerics,
                    DerivedValues = derivedValues,
                    NumericConstraints = numericConstraints,
                    NumberFormat = request.NumberFormat,
                    UnitSystem = request.UnitSystem,
                    Precision = CalculateExtractionPrecision(normalizedNumerics),
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Numeric extraction completed: Precision = {Precision}, Quantities = {QuantityCount}",
                    result.Precision, result.Quantities.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Numeric parameter extraction failed");
                throw new ParameterExtractionException("Numeric parameter extraction failed", ex);
            }
        }

        /// <summary>
        /// Extracts location and spatial parameters;
        /// </summary>
        public async Task<SpatialExtractionResult> ExtractSpatialParametersAsync(
            SpatialExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var spatialId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting spatial parameter extraction: {SpatialId}", spatialId);

                // Parse location expressions;
                var locationExpressions = await ParseLocationExpressionsAsync(request, cancellationToken);

                // Resolve geographic references;
                var resolvedLocations = await ResolveGeographicReferencesAsync(locationExpressions, cancellationToken);

                // Extract coordinates and boundaries;
                var coordinates = await ExtractCoordinatesAsync(resolvedLocations, cancellationToken);

                // Handle relative locations;
                var relativeLocations = await HandleRelativeLocationsAsync(resolvedLocations, request, cancellationToken);

                // Normalize spatial data;
                var normalizedSpatial = await NormalizeSpatialDataAsync(coordinates, relativeLocations, cancellationToken);

                // Calculate spatial relationships;
                var spatialRelationships = await CalculateSpatialRelationshipsAsync(normalizedSpatial, cancellationToken);

                var result = new SpatialExtractionResult;
                {
                    SpatialId = spatialId,
                    Request = request,
                    LocationExpressions = locationExpressions,
                    ResolvedLocations = resolvedLocations,
                    Coordinates = coordinates,
                    RelativeLocations = relativeLocations,
                    NormalizedSpatial = normalizedSpatial,
                    SpatialRelationships = spatialRelationships,
                    CoordinateSystem = request.CoordinateSystem,
                    SpatialPrecision = CalculateSpatialPrecision(normalizedSpatial),
                    ExtractionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Spatial extraction completed: Precision = {Precision}, Locations = {LocationCount}",
                    result.SpatialPrecision, result.ResolvedLocations.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spatial parameter extraction failed");
                throw new ParameterExtractionException("Spatial parameter extraction failed", ex);
            }
        }

        /// <summary>
        /// Validates extracted parameters against constraints and rules;
        /// </summary>
        public async Task<ParameterValidationResult> ValidateExtractedParametersAsync(
            IEnumerable<ExtractedParameter> parameters,
            ValidationContext context = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            try
            {
                var validationId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting parameter validation: {ValidationId}", validationId);

                var parameterList = parameters.ToList();

                // Perform multi-level validation;
                var syntaxValidation = await ValidateSyntaxAsync(parameterList, cancellationToken);
                var semanticValidation = await ValidateSemanticsAsync(parameterList, context, cancellationToken);
                var constraintValidation = await ValidateConstraintsAsync(parameterList, context, cancellationToken);
                var consistencyValidation = await ValidateConsistencyAsync(parameterList, cancellationToken);

                // Calculate overall validation scores;
                var validationScores = CalculateValidationScores(
                    syntaxValidation, semanticValidation, constraintValidation, consistencyValidation);

                // Generate validation recommendations;
                var recommendations = GenerateValidationRecommendations(
                    syntaxValidation, semanticValidation, constraintValidation, consistencyValidation);

                var result = new ParameterValidationResult;
                {
                    ValidationId = validationId,
                    Parameters = parameterList,
                    SyntaxValidation = syntaxValidation,
                    SemanticValidation = semanticValidation,
                    ConstraintValidation = constraintValidation,
                    ConsistencyValidation = consistencyValidation,
                    ValidationScores = validationScores,
                    Recommendations = recommendations,
                    IsSuccessful = validationScores.OverallScore >= _configuration.ValidationThreshold,
                    ValidationDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Parameter validation completed: Overall score = {Score:F2}, Success = {Success}",
                    validationScores.OverallScore, result.IsSuccessful);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parameter validation failed");
                throw new ParameterValidationException("Parameter validation failed", ex);
            }
        }

        /// <summary>
        /// Resolves ambiguous or conflicting parameters;
        /// </summary>
        public async Task<AmbiguityResolutionResult> ResolveParameterAmbiguitiesAsync(
            IEnumerable<ExtractedParameter> parameters,
            AmbiguityContext context,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                var resolutionId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug("Starting ambiguity resolution: {ResolutionId}", resolutionId);

                var parameterList = parameters.ToList();

                // Detect ambiguities;
                var detectedAmbiguities = await DetectAmbiguitiesAsync(parameterList, context, cancellationToken);

                // Classify ambiguity types;
                var ambiguityClassification = await ClassifyAmbiguitiesAsync(detectedAmbiguities, cancellationToken);

                // Apply resolution strategies;
                var resolutionStrategies = await ApplyResolutionStrategiesAsync(detectedAmbiguities, ambiguityClassification, cancellationToken);

                // Generate resolution candidates;
                var resolutionCandidates = await GenerateResolutionCandidatesAsync(
                    parameterList, detectedAmbiguities, resolutionStrategies, cancellationToken);

                // Select optimal resolutions;
                var selectedResolutions = await SelectOptimalResolutionsAsync(resolutionCandidates, context, cancellationToken);

                // Apply resolutions to parameters;
                var resolvedParameters = await ApplyResolutionsAsync(parameterList, selectedResolutions, cancellationToken);

                var result = new AmbiguityResolutionResult;
                {
                    ResolutionId = resolutionId,
                    OriginalParameters = parameterList,
                    DetectedAmbiguities = detectedAmbiguities,
                    AmbiguityClassification = ambiguityClassification,
                    ResolutionStrategies = resolutionStrategies,
                    ResolutionCandidates = resolutionCandidates,
                    SelectedResolutions = selectedResolutions,
                    ResolvedParameters = resolvedParameters,
                    AmbiguityCount = detectedAmbiguities.Count,
                    ResolutionRate = CalculateResolutionRate(detectedAmbiguities, selectedResolutions),
                    ResolutionConfidence = CalculateResolutionConfidence(selectedResolutions),
                    ResolutionDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                // Raise event if ambiguities were detected;
                if (detectedAmbiguities.Any())
                {
                    OnAmbiguousParameterDetected(new AmbiguousParameterDetectedEventArgs;
                    {
                        ResolutionResult = result,
                        AmbiguityCount = detectedAmbiguities.Count,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                _logger.LogInformation("Ambiguity resolution completed: Resolved {ResolvedCount}/{TotalCount} ambiguities",
                    selectedResolutions.Count, detectedAmbiguities.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ambiguity resolution failed");
                throw new ParameterExtractionException("Ambiguity resolution failed", ex);
            }
        }

        /// <summary>
        /// Learns new parameter patterns from extraction results;
        /// </summary>
        public async Task<PatternLearningResult> LearnParameterPatternsAsync(
            PatternLearningRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                await _extractionLock.WaitAsync(cancellationToken);

                var learningId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogInformation("Learning parameter patterns from {ExampleCount} examples",
                    request.Examples?.Count() ?? 0);

                // Extract patterns from examples;
                var extractedPatterns = await ExtractPatternsFromExamplesAsync(request.Examples, cancellationToken);

                // Generalize patterns;
                var generalizedPatterns = await GeneralizePatternsAsync(extractedPatterns, cancellationToken);

                // Validate learned patterns;
                var patternValidation = await ValidateLearnedPatternsAsync(generalizedPatterns, cancellationToken);

                // Integrate patterns into existing knowledge;
                var integrationResult = await IntegratePatternsAsync(generalizedPatterns, patternValidation, cancellationToken);

                // Update extraction performance;
                var performanceUpdate = await UpdateExtractionPerformanceAsync(integrationResult, cancellationToken);

                var result = new PatternLearningResult;
                {
                    LearningId = learningId,
                    Request = request,
                    ExtractedPatterns = extractedPatterns,
                    GeneralizedPatterns = generalizedPatterns,
                    PatternValidation = patternValidation,
                    IntegrationResult = integrationResult,
                    PerformanceUpdate = performanceUpdate,
                    PatternsLearned = generalizedPatterns.Count,
                    PatternQuality = CalculatePatternQuality(generalizedPatterns),
                    LearningEffectiveness = CalculateLearningEffectiveness(integrationResult),
                    LearningDuration = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Pattern learning completed: {PatternCount} patterns learned with quality {Quality:F2}",
                    result.PatternsLearned, result.PatternQuality);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pattern learning failed");
                throw new ParameterLearningException("Pattern learning failed", ex);
            }
            finally
            {
                _extractionLock.Release();
            }
        }

        /// <summary>
        /// Sets the extraction mode;
        /// </summary>
        public void SetExtractionMode(ExtractionMode mode)
        {
            if (!Enum.IsDefined(typeof(ExtractionMode), mode))
            {
                throw new ArgumentException($"Invalid extraction mode: {mode}", nameof(mode));
            }

            if (CurrentExtractionMode != mode)
            {
                _logger.LogInformation("Changing extraction mode from {OldMode} to {NewMode}",
                    CurrentExtractionMode, mode);

                CurrentExtractionMode = mode;

                // Update configuration based on mode;
                UpdateConfigurationForMode(mode);
            }
        }

        /// <summary>
        /// Gets extraction history for analysis;
        /// </summary>
        public async Task<IEnumerable<ExtractionHistory>> GetExtractionHistoryAsync(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            int? maxResults = null)
        {
            await Task.CompletedTask; // Async pattern for future expansion;

            var query = _extractionHistory.AsQueryable();

            if (fromDate.HasValue)
            {
                query = query.Where(h => h.Timestamp >= fromDate.Value);
            }

            if (toDate.HasValue)
            {
                query = query.Where(h => h.Timestamp <= toDate.Value);
            }

            if (maxResults.HasValue)
            {
                query = query.Take(maxResults.Value);
            }

            return query.OrderByDescending(h => h.Timestamp).ToList();
        }

        /// <summary>
        /// Performs health check on the parameter finder;
        /// </summary>
        public async Task<ParameterFinderHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var healthTasks = new List<Task<bool>>
                {
                    CheckPatternsHealthAsync(cancellationToken),
                    CheckValidatorsHealthAsync(cancellationToken),
                    CheckEntityTypesHealthAsync(cancellationToken),
                    CheckNLPServiceHealthAsync(cancellationToken),
                    CheckKnowledgeBaseHealthAsync(cancellationToken)
                };

                await Task.WhenAll(healthTasks);

                var isHealthy = healthTasks.All(t => t.Result);

                var status = new ParameterFinderHealthStatus;
                {
                    IsHealthy = isHealthy,
                    CurrentExtractionMode = CurrentExtractionMode,
                    ActivePatternsCount = _parameterPatterns.Count,
                    ActiveValidatorsCount = _parameterValidators.Count,
                    ActiveEntityTypesCount = _entityTypes.Count,
                    ExtractionHistoryCount = _extractionHistory.Count,
                    Metrics = Metrics,
                    LastHealthCheck = DateTime.UtcNow;
                };

                if (!isHealthy)
                {
                    _logger.LogWarning("Parameter finder health check failed");
                    status.HealthIssues = new List<string> { "One or more components are unhealthy" };
                }

                return status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return new ParameterFinderHealthStatus;
                {
                    IsHealthy = false,
                    HealthIssues = new List<string> { $"Health check failed: {ex.Message}" },
                    LastHealthCheck = DateTime.UtcNow;
                };
            }
        }

        #endregion;

        #region Private Methods;

        private async Task LoadParameterPatternsAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Loading parameter patterns...");

            // Load predefined patterns;
            var patternTypes = new[]
            {
                ParameterPatternType.Numeric,
                ParameterPatternType.Temporal,
                ParameterPatternType.Spatial,
                ParameterPatternType.Textual,
                ParameterPatternType.Boolean,
                ParameterPatternType.Enumerated,
                ParameterPatternType.Composite;
            };

            foreach (var patternType in patternTypes)
            {
                try
                {
                    var pattern = ParameterPattern.CreateDefault(patternType);
                    _parameterPatterns[patternType.ToString()] = pattern;

                    _logger.LogDebug("Loaded parameter pattern: {PatternType}", patternType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load parameter pattern: {PatternType}", patternType);
                }
            }

            // Load custom patterns from knowledge base;
            await LoadCustomPatternsAsync(cancellationToken);
        }

        private async Task LoadCustomPatternsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var customPatterns = await _knowledgeBase.GetParameterPatternsAsync(cancellationToken);

                foreach (var pattern in customPatterns)
                {
                    _parameterPatterns[pattern.Id] = pattern;
                    _logger.LogDebug("Loaded custom parameter pattern: {PatternId}", pattern.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load custom parameter patterns");
            }
        }

        private async Task LoadEntityTypesAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Loading entity types...");

            // Load predefined entity types;
            var entityTypeNames = new[]
            {
                "Person",
                "Organization",
                "Location",
                "Date",
                "Time",
                "Money",
                "Percentage",
                "Email",
                "URL",
                "PhoneNumber",
                "Product",
                "Event"
            };

            foreach (var entityName in entityTypeNames)
            {
                try
                {
                    var entityType = EntityType.CreateDefault(entityName);
                    _entityTypes[entityName] = entityType;

                    _logger.LogDebug("Loaded entity type: {EntityName}", entityName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load entity type: {EntityName}", entityName);
                }
            }
        }

        private async Task LoadParameterValidatorsAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Loading parameter validators...");

            // Load predefined validators;
            var validatorTypes = new[]
            {
                ValidatorType.Range,
                ValidatorType.Format,
                ValidatorType.Type,
                ValidatorType.Custom,
                ValidatorType.Consistency,
                ValidatorType.Completeness;
            };

            foreach (var validatorType in validatorTypes)
            {
                try
                {
                    var validator = ParameterValidator.CreateDefault(validatorType);
                    _parameterValidators[validatorType.ToString()] = validator;

                    _logger.LogDebug("Loaded parameter validator: {ValidatorType}", validatorType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load parameter validator: {ValidatorType}", validatorType);
                }
            }
        }

        private async Task InitializeNLPServiceAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Initializing NLP service...");

            // Ensure NLP service is ready;
            await _nlpService.InitializeAsync(cancellationToken);

            _logger.LogDebug("NLP service initialized");
        }

        private async Task LoadDomainRulesAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Loading domain-specific extraction rules...");

            try
            {
                var domainRules = await _knowledgeBase.GetDomainRulesAsync(cancellationToken);

                // Apply domain rules to patterns and validators;
                foreach (var rule in domainRules)
                {
                    ApplyDomainRule(rule);
                }

                _logger.LogDebug("Loaded {RuleCount} domain rules", domainRules.Count());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load domain rules");
            }
        }

        private void ApplyDomainRule(DomainRule rule)
        {
            // Apply domain rule to appropriate patterns or validators;
            // This would modify extraction behavior based on domain knowledge;
        }

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException(
                    "ParameterFinder must be initialized before use. Call InitializeAsync first.");
            }
        }

        private void ValidateRequest(ExtractionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Text))
                throw new ArgumentException("Text cannot be null or empty", nameof(request.Text));

            if (string.IsNullOrWhiteSpace(request.RequestId))
                throw new ArgumentException("Request ID cannot be null or empty", nameof(request.RequestId));
        }

        private async Task<List<ExtractedParameter>> ExtractParametersMultiStageAsync(
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var extractedParameters = new List<ExtractedParameter>();

            // Stage 1: Basic pattern matching;
            var patternMatches = await ExtractWithPatternMatchingAsync(context, cancellationToken);
            extractedParameters.AddRange(patternMatches);

            // Stage 2: NLP-based extraction;
            var nlpExtractions = await ExtractWithNLPAsync(context, cancellationToken);
            extractedParameters.AddRange(nlpExtractions);

            // Stage 3: Context-aware extraction;
            var contextExtractions = await ExtractWithContextAsync(context, extractedParameters, cancellationToken);
            extractedParameters.AddRange(contextExtractions);

            // Stage 4: Rule-based extraction;
            var ruleExtractions = await ExtractWithRulesAsync(context, cancellationToken);
            extractedParameters.AddRange(ruleExtractions);

            // Remove duplicates;
            extractedParameters = RemoveDuplicateParameters(extractedParameters);

            // Add extraction metadata;
            foreach (var param in extractedParameters)
            {
                param.ExtractionMethod = DetermineExtractionMethod(param);
                param.Confidence = CalculateParameterConfidence(param);
            }

            _logger.LogDebug("Multi-stage extraction completed: {Count} parameters found", extractedParameters.Count);

            return extractedParameters;
        }

        private async Task<List<ExtractedParameter>> ExtractWithPatternMatchingAsync(
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var patternMatches = new List<ExtractedParameter>();

            foreach (var pattern in _parameterPatterns.Values)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var matches = await pattern.MatchAsync(context.Request.Text, context, cancellationToken);
                patternMatches.AddRange(matches);
            }

            return patternMatches;
        }

        private async Task<List<ExtractedParameter>> ExtractWithNLPAsync(
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var nlpExtractions = new List<ExtractedParameter>();

            try
            {
                // Perform NLP analysis;
                var nlpAnalysis = await _nlpService.AnalyzeTextAsync(context.Request.Text, cancellationToken);

                // Extract entities;
                var entities = await _nlpService.ExtractEntitiesAsync(context.Request.Text, cancellationToken);

                // Extract relations;
                var relations = await _nlpService.ExtractRelationsAsync(context.Request.Text, cancellationToken);

                // Convert NLP results to parameters;
                nlpExtractions.AddRange(ConvertEntitiesToParameters(entities, context));
                nlpExtractions.AddRange(ConvertRelationsToParameters(relations, context));

                // Extract using dependency parsing;
                var dependencies = await _nlpService.ParseDependenciesAsync(context.Request.Text, cancellationToken);
                nlpExtractions.AddRange(ExtractFromDependencies(dependencies, context));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NLP extraction failed, continuing with other methods");
            }

            return nlpExtractions;
        }

        private async Task<List<ExtractedParameter>> ExtractWithContextAsync(
            ExtractionContext context,
            List<ExtractedParameter> existingParameters,
            CancellationToken cancellationToken)
        {
            var contextExtractions = new List<ExtractedParameter>();

            // Use existing parameters as context for further extraction;
            foreach (var param in existingParameters)
            {
                // Look for related parameters near this one;
                var related = await FindRelatedParametersAsync(param, context, cancellationToken);
                contextExtractions.AddRange(related);
            }

            // Apply contextual patterns;
            var contextualPatterns = _parameterPatterns.Values.Where(p => p.IsContextual);
            foreach (var pattern in contextualPatterns)
            {
                var matches = await pattern.MatchWithContextAsync(
                    context.Request.Text, existingParameters, context, cancellationToken);
                contextExtractions.AddRange(matches);
            }

            return contextExtractions;
        }

        private async Task<List<ExtractedParameter>> ExtractWithRulesAsync(
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var ruleExtractions = new List<ExtractedParameter>();

            // Apply extraction rules from configuration;
            foreach (var rule in _configuration.ExtractionRules)
            {
                if (rule.IsApplicable(context))
                {
                    var extractions = await rule.ApplyAsync(context, cancellationToken);
                    ruleExtractions.AddRange(extractions);
                }
            }

            return ruleExtractions;
        }

        private List<ExtractedParameter> RemoveDuplicateParameters(List<ExtractedParameter> parameters)
        {
            return parameters;
                .GroupBy(p => new { p.Name, p.Value, p.StartIndex, p.EndIndex })
                .Select(g => g.OrderByDescending(p => p.Confidence).First())
                .ToList();
        }

        private string DetermineExtractionMethod(ExtractedParameter parameter)
        {
            // Determine which method was most likely used for extraction;
            if (parameter.Source == "NLP")
                return "NLP";
            else if (parameter.Source == "Pattern")
                return "PatternMatching";
            else if (parameter.Source == "Rule")
                return "RuleBased";
            else;
                return "Contextual";
        }

        private double CalculateParameterConfidence(ExtractedParameter parameter)
        {
            var confidence = 0.0;

            // Base confidence on extraction method;
            switch (parameter.ExtractionMethod)
            {
                case "NLP":
                    confidence = 0.85;
                    break;
                case "PatternMatching":
                    confidence = 0.90;
                    break;
                case "RuleBased":
                    confidence = 0.80;
                    break;
                case "Contextual":
                    confidence = 0.75;
                    break;
                default:
                    confidence = 0.70;
                    break;
            }

            // Adjust based on parameter quality;
            if (!string.IsNullOrWhiteSpace(parameter.Value))
                confidence += 0.05;

            if (parameter.StartIndex >= 0 && parameter.EndIndex > parameter.StartIndex)
                confidence += 0.05;

            if (!string.IsNullOrWhiteSpace(parameter.Type))
                confidence += 0.05;

            return Math.Min(1.0, confidence);
        }

        private List<ExtractedParameter> ConvertEntitiesToParameters(
            IEnumerable<NLPEntity> entities,
            ExtractionContext context)
        {
            var parameters = new List<ExtractedParameter>();

            foreach (var entity in entities)
            {
                parameters.Add(new ExtractedParameter;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = entity.Type,
                    Value = entity.Text,
                    Type = MapEntityTypeToParameterType(entity.Type),
                    StartIndex = entity.StartIndex,
                    EndIndex = entity.EndIndex,
                    Source = "NLP",
                    Confidence = entity.Confidence,
                    Metadata = new Dictionary<string, object>
                    {
                        ["EntityType"] = entity.Type,
                        ["NLPConfidence"] = entity.Confidence;
                    }
                });
            }

            return parameters;
        }

        private string MapEntityTypeToParameterType(string entityType)
        {
            return entityType.ToLower() switch;
            {
                "person" => "String",
                "organization" => "String",
                "location" => "Location",
                "date" => "DateTime",
                "time" => "DateTime",
                "money" => "Decimal",
                "percentage" => "Double",
                "email" => "String",
                "url" => "String",
                "phonenumber" => "String",
                _ => "String"
            };
        }

        private async Task<List<ExtractedParameter>> ResolveParameterReferencesAsync(
            List<ExtractedParameter> parameters,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var resolvedParameters = new List<ExtractedParameter>();

            foreach (var param in parameters)
            {
                var resolvedParam = await ResolveSingleParameterAsync(param, parameters, context, cancellationToken);
                resolvedParameters.Add(resolvedParam);
            }

            return resolvedParameters;
        }

        private async Task<ExtractedParameter> ResolveSingleParameterAsync(
            ExtractedParameter parameter,
            List<ExtractedParameter> allParameters,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            // Check for references to other parameters;
            var references = await FindParameterReferencesAsync(parameter, allParameters, cancellationToken);

            if (references.Any())
            {
                // Resolve references;
                var resolvedValue = await ResolveReferencesAsync(parameter, references, context, cancellationToken);

                return new ExtractedParameter;
                {
                    Id = parameter.Id,
                    Name = parameter.Name,
                    Value = resolvedValue,
                    OriginalValue = parameter.Value,
                    Type = parameter.Type,
                    StartIndex = parameter.StartIndex,
                    EndIndex = parameter.EndIndex,
                    Source = parameter.Source,
                    Confidence = parameter.Confidence * 0.9, // Slightly lower confidence after resolution;
                    Metadata = parameter.Metadata,
                    References = references,
                    IsResolved = true;
                };
            }

            return parameter;
        }

        private async Task<ParameterValidationResult> ValidateParametersAsync(
            List<ExtractedParameter> parameters,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var validationResult = new ParameterValidationResult;
            {
                ValidationId = Guid.NewGuid().ToString(),
                Parameters = parameters,
                ValidParameters = new List<ValidatedParameter>(),
                InvalidParameters = new List<InvalidParameter>(),
                ValidationRules = new List<ValidationRuleApplied>()
            };

            foreach (var param in parameters)
            {
                var validation = await ValidateSingleParameterAsync(param, context, cancellationToken);

                if (validation.IsValid)
                {
                    validationResult.ValidParameters.Add(new ValidatedParameter;
                    {
                        Parameter = param,
                        ValidationResult = validation,
                        IsCompliant = true;
                    });
                }
                else;
                {
                    validationResult.InvalidParameters.Add(new InvalidParameter;
                    {
                        Parameter = param,
                        ValidationResult = validation,
                        Errors = validation.Errors;
                    });
                }
            }

            validationResult.IsSuccessful = validationResult.InvalidParameters.Count == 0;
            validationResult.SuccessRate = (double)validationResult.ValidParameters.Count / parameters.Count;

            return validationResult;
        }

        private async Task<ParameterValidation> ValidateSingleParameterAsync(
            ExtractedParameter parameter,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var validation = new ParameterValidation;
            {
                ParameterId = parameter.Id,
                IsValid = true,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            // Type validation;
            if (!await ValidateTypeAsync(parameter, cancellationToken))
            {
                validation.IsValid = false;
                validation.Errors.Add($"Invalid type: {parameter.Type}");
            }

            // Format validation;
            if (!await ValidateFormatAsync(parameter, cancellationToken))
            {
                validation.IsValid = false;
                validation.Errors.Add($"Invalid format for type: {parameter.Type}");
            }

            // Range validation if applicable;
            if (IsNumericType(parameter.Type))
            {
                var rangeValidation = await ValidateRangeAsync(parameter, cancellationToken);
                if (!rangeValidation.IsValid)
                {
                    validation.IsValid = false;
                    validation.Errors.AddRange(rangeValidation.Errors);
                }
            }

            // Domain-specific validation;
            var domainValidation = await ValidateDomainRulesAsync(parameter, context, cancellationToken);
            if (!domainValidation.IsValid)
            {
                validation.IsValid = false;
                validation.Errors.AddRange(domainValidation.Errors);
            }

            // Consistency validation with other parameters;
            var consistencyValidation = await ValidateConsistencyAsync(parameter, context, cancellationToken);
            if (!consistencyValidation.IsValid)
            {
                validation.Warnings.AddRange(consistencyValidation.Warnings);
            }

            validation.Confidence = CalculateValidationConfidence(validation);

            return validation;
        }

        private async Task<AmbiguityResolution> HandleAmbiguitiesAsync(
            List<ExtractedParameter> parameters,
            ParameterValidationResult validationResults,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            // Detect ambiguities;
            var ambiguities = await DetectParameterAmbiguitiesAsync(parameters, context, cancellationToken);

            if (!ambiguities.Any())
            {
                return new AmbiguityResolution;
                {
                    HasAmbiguities = false,
                    ResolvedParameters = parameters,
                    ResolutionApplied = ResolutionType.None;
                };
            }

            // Resolve ambiguities;
            var resolvedParameters = await ResolveDetectedAmbiguitiesAsync(
                parameters, ambiguities, validationResults, context, cancellationToken);

            return new AmbiguityResolution;
            {
                HasAmbiguities = true,
                OriginalParameters = parameters,
                DetectedAmbiguities = ambiguities,
                ResolvedParameters = resolvedParameters,
                ResolutionApplied = DetermineResolutionType(ambiguities),
                ResolutionConfidence = CalculateResolutionConfidence(resolvedParameters, parameters)
            };
        }

        private async Task<List<NormalizedParameter>> NormalizeParametersAsync(
            List<ExtractedParameter> parameters,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var normalizedParameters = new List<NormalizedParameter>();

            foreach (var param in parameters)
            {
                var normalizedParam = await NormalizeSingleParameterAsync(param, context, cancellationToken);
                normalizedParameters.Add(normalizedParam);
            }

            return normalizedParameters;
        }

        private async Task<NormalizedParameter> NormalizeSingleParameterAsync(
            ExtractedParameter parameter,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            // Type-specific normalization;
            object normalizedValue = parameter.Type.ToLower() switch;
            {
                "datetime" => await NormalizeDateTimeAsync(parameter.Value, context, cancellationToken),
                "decimal" or "double" => await NormalizeNumericAsync(parameter.Value, context, cancellationToken),
                "string" => await NormalizeStringAsync(parameter.Value, context, cancellationToken),
                "boolean" => await NormalizeBooleanAsync(parameter.Value, context, cancellationToken),
                "location" => await NormalizeLocationAsync(parameter.Value, context, cancellationToken),
                _ => parameter.Value;
            };

            return new NormalizedParameter;
            {
                ParameterId = parameter.Id,
                Name = parameter.Name,
                OriginalValue = parameter.Value,
                NormalizedValue = normalizedValue,
                Type = parameter.Type,
                NormalizationType = DetermineNormalizationType(parameter.Type),
                NormalizationConfidence = CalculateNormalizationConfidence(parameter, normalizedValue),
                NormalizedAt = DateTime.UtcNow;
            };
        }

        private double CalculateExtractionConfidence(
            List<NormalizedParameter> normalizedParameters,
            ParameterValidationResult validationResults)
        {
            if (!normalizedParameters.Any())
                return 0.0;

            var parameterConfidences = normalizedParameters;
                .Select(p => p.NormalizationConfidence)
                .Average();

            var validationConfidence = validationResults.SuccessRate;

            // Weighted combination;
            return (parameterConfidences * 0.6) + (validationConfidence * 0.4);
        }

        private void StoreExtractionHistory(ParameterExtractionResult result)
        {
            var history = new ExtractionHistory;
            {
                ExtractionId = result.ExtractionId,
                RequestId = result.Request.RequestId,
                ParameterCount = result.NormalizedParameters.Count,
                Confidence = result.Confidence,
                Success = result.Success,
                ExtractionDuration = result.ExtractionDuration,
                Timestamp = result.Timestamp;
            };

            _extractionHistory.Add(history);

            // Maintain history size limit;
            if (_extractionHistory.Count > _configuration.MaxHistorySize)
            {
                _extractionHistory.RemoveAt(0);
            }
        }

        private async Task RecordExtractionMetricsAsync(ParameterExtractionResult result, DateTime startTime)
        {
            var duration = DateTime.UtcNow - startTime;

            Metrics.TotalExtractions++;

            if (result.Success)
            {
                Metrics.SuccessfulExtractions++;
            }

            // Update average time;
            if (Metrics.AverageExtractionTime == TimeSpan.Zero)
            {
                Metrics.AverageExtractionTime = duration;
            }
            else;
            {
                Metrics.AverageExtractionTime = TimeSpan.FromMilliseconds(
                    (Metrics.AverageExtractionTime.TotalMilliseconds * (Metrics.TotalExtractions - 1) +
                     duration.TotalMilliseconds) / Metrics.TotalExtractions);
            }

            Metrics.LastExtractionTime = DateTime.UtcNow;
            Metrics.TotalParametersExtracted += result.NormalizedParameters.Count;

            // Update method usage;
            var methodUsage = result.ExtractedParameters;
                .GroupBy(p => p.ExtractionMethod)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var usage in methodUsage)
            {
                if (Metrics.MethodUsage.ContainsKey(usage.Key))
                {
                    Metrics.MethodUsage[usage.Key] += usage.Value;
                }
                else;
                {
                    Metrics.MethodUsage[usage.Key] = usage.Value;
                }
            }

            await _metricsCollector.RecordMetricAsync("parameter_extraction_duration_ms", duration.TotalMilliseconds);
            await _metricsCollector.RecordMetricAsync("parameters_extracted", result.NormalizedParameters.Count);
            await _metricsCollector.RecordMetricAsync("extraction_confidence", result.Confidence);
        }

        private void UpdateConfigurationForMode(ExtractionMode mode)
        {
            // Update configuration based on extraction mode;
            switch (mode)
            {
                case ExtractionMode.Strict:
                    _configuration.ValidationThreshold = 0.9;
                    _configuration.AllowAmbiguities = false;
                    break;

                case ExtractionMode.Standard:
                    _configuration.ValidationThreshold = 0.7;
                    _configuration.AllowAmbiguities = true;
                    break;

                case ExtractionMode.Lenient:
                    _configuration.ValidationThreshold = 0.5;
                    _configuration.AllowAmbiguities = true;
                    break;

                case ExtractionMode.Aggressive:
                    _configuration.ValidationThreshold = 0.3;
                    _configuration.AllowAmbiguities = true;
                    break;
            }
        }

        // Additional helper methods for semantic extraction, structured extraction, etc.
        // Due to length constraints, showing the main structure;

        #endregion;

        #region Event Handlers;

        protected virtual void OnExtractionCompleted(ParameterExtractionCompletedEventArgs e)
        {
            ExtractionCompleted?.Invoke(this, e);
        }

        protected virtual void OnAmbiguousParameterDetected(AmbiguousParameterDetectedEventArgs e)
        {
            AmbiguousParameterDetected?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _extractionLock?.Dispose();

                    foreach (var validator in _parameterValidators.Values)
                    {
                        if (validator is IDisposable disposableValidator)
                        {
                            disposableValidator.Dispose();
                        }
                    }

                    _parameterPatterns.Clear();
                    _entityTypes.Clear();
                    _parameterValidators.Clear();
                    _extractionHistory.Clear();
                }

                _isDisposed = true;
            }
        }

        ~ParameterFinder()
        {
            Dispose(false);
        }

        #endregion;
    }
}
