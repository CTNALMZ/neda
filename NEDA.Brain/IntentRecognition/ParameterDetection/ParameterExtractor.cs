using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.AI.NaturalLanguage;
using NEDA.Brain.NLP_Engine.EntityRecognition;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis;
using NEDA.Common.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NEDA.Brain.IntentRecognition.ParameterDetection;
{
    /// <summary>
    /// Service for extracting and parsing parameters from natural language input;
    /// </summary>
    public interface IParameterExtractor : IDisposable
    {
        /// <summary>
        /// Extracts parameters from text based on intent schema;
        /// </summary>
        Task<ParameterExtractionResult> ExtractParametersAsync(
            string text,
            IntentSchema intentSchema,
            ExtractionContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates extracted parameters against schema;
        /// </summary>
        Task<ParameterValidationResult> ValidateParametersAsync(
            Dictionary<string, object> parameters,
            IntentSchema intentSchema,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Transforms parameters to target types;
        /// </summary>
        Task<Dictionary<string, object>> TransformParametersAsync(
            Dictionary<string, object> parameters,
            ParameterTransformationOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts parameters from structured data (JSON, XML, etc.)
        /// </summary>
        Task<Dictionary<string, object>> ExtractFromStructuredDataAsync(
            string structuredData,
            DataFormat format,
            IntentSchema schema = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Merges parameters from multiple sources;
        /// </summary>
        Task<Dictionary<string, object>> MergeParametersAsync(
            params ParameterSource[] sources,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts temporal parameters (dates, times, durations)
        /// </summary>
        Task<TemporalParameters> ExtractTemporalParametersAsync(
            string text,
            DateTime? referenceTime = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts numeric parameters (quantities, measurements, ranges)
        /// </summary>
        Task<NumericParameters> ExtractNumericParametersAsync(
            string text,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts entity parameters (people, places, organizations)
        /// </summary>
        Task<EntityParameters> ExtractEntityParametersAsync(
            string text,
            ExtractionContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts location parameters (addresses, coordinates, places)
        /// </summary>
        Task<LocationParameters> ExtractLocationParametersAsync(
            string text,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implementation of parameter extraction service;
    /// </summary>
    public class ParameterExtractor : IParameterExtractor;
    {
        private readonly ILogger<ParameterExtractor> _logger;
        private readonly IOptions<ParameterExtractionOptions> _options;
        private readonly ISyntaxParser _syntaxParser;
        private readonly IEntityExtractor _entityExtractor;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IParameterResolver _parameterResolver;
        private readonly RegexCache _regexCache;
        private readonly SemaphoreSlim _extractionLock = new(1, 1);
        private bool _disposed;

        public ParameterExtractor(
            ILogger<ParameterExtractor> logger,
            IOptions<ParameterExtractionOptions> options,
            ISyntaxParser syntaxParser,
            IEntityExtractor entityExtractor,
            ISemanticAnalyzer semanticAnalyzer,
            IParameterResolver parameterResolver)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _syntaxParser = syntaxParser ?? throw new ArgumentNullException(nameof(syntaxParser));
            _entityExtractor = entityExtractor ?? throw new ArgumentNullException(nameof(entityExtractor));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _parameterResolver = parameterResolver ?? throw new ArgumentNullException(nameof(parameterResolver));
            _regexCache = new RegexCache();

            InitializePatterns();
            _logger.LogInformation("ParameterExtractor initialized");
        }

        /// <summary>
        /// Extracts parameters from natural language text;
        /// </summary>
        public async Task<ParameterExtractionResult> ExtractParametersAsync(
            string text,
            IntentSchema intentSchema,
            ExtractionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            if (intentSchema == null)
                throw new ArgumentNullException(nameof(intentSchema));

            await _extractionLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("Extracting parameters from text: {Text}", text.Truncate(100));

                var result = new ParameterExtractionResult;
                {
                    OriginalText = text,
                    IntentSchema = intentSchema,
                    ExtractionTime = DateTime.UtcNow;
                };

                // Parse syntax tree;
                var syntaxTree = await _syntaxParser.ParseAsync(text, cancellationToken);
                result.SyntaxTree = syntaxTree;

                // Perform semantic analysis;
                var semanticAnalysis = await _semanticAnalyzer.AnalyzeAsync(text, context, cancellationToken);
                result.SemanticAnalysis = semanticAnalysis;

                // Extract entities;
                var entities = await _entityExtractor.ExtractEntitiesAsync(text, cancellationToken);
                result.Entities = entities;

                // Extract parameters based on schema;
                var extractedParams = new Dictionary<string, object>();
                var confidenceScores = new Dictionary<string, float>();
                var extractionSources = new Dictionary<string, ExtractionSource>();

                foreach (var parameter in intentSchema.Parameters)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var extractionResult = await ExtractParameterAsync(
                            text,
                            parameter,
                            syntaxTree,
                            semanticAnalysis,
                            entities,
                            context,
                            cancellationToken);

                        if (extractionResult != null && extractionResult.IsSuccess)
                        {
                            extractedParams[parameter.Name] = extractionResult.Value;
                            confidenceScores[parameter.Name] = extractionResult.Confidence;
                            extractionSources[parameter.Name] = extractionResult.Source;

                            _logger.LogDebug("Extracted parameter {Parameter}: {Value} (confidence: {Confidence})",
                                parameter.Name, extractionResult.Value, extractionResult.Confidence);
                        }
                        else;
                        {
                            // Try to use default value if extraction failed;
                            if (parameter.HasDefaultValue)
                            {
                                extractedParams[parameter.Name] = parameter.DefaultValue;
                                confidenceScores[parameter.Name] = 0.3f; // Low confidence for default values;
                                extractionSources[parameter.Name] = ExtractionSource.DefaultValue;

                                _logger.LogDebug("Using default value for parameter {Parameter}: {Value}",
                                    parameter.Name, parameter.DefaultValue);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error extracting parameter {Parameter} from text", parameter.Name);
                    }
                }

                // Extract additional context parameters;
                var contextParams = await ExtractContextParametersAsync(text, context, cancellationToken);
                foreach (var param in contextParams)
                {
                    if (!extractedParams.ContainsKey(param.Key))
                    {
                        extractedParams[param.Key] = param.Value;
                        confidenceScores[param.Key] = 0.7f; // Moderate confidence for context;
                        extractionSources[param.Key] = ExtractionSource.Context;
                    }
                }

                // Resolve parameter references and dependencies;
                var resolvedParams = await ResolveParameterDependenciesAsync(
                    extractedParams,
                    intentSchema,
                    cancellationToken);

                // Validate extracted parameters;
                var validationResult = await ValidateParametersAsync(resolvedParams, intentSchema, cancellationToken);

                // Build final result;
                result.Parameters = resolvedParams;
                result.ConfidenceScores = confidenceScores;
                result.ExtractionSources = extractionSources;
                result.IsComplete = validationResult.IsValid &&
                    intentSchema.RequiredParameters.All(p => resolvedParams.ContainsKey(p));
                result.ValidationResult = validationResult;
                result.QualityScore = CalculateExtractionQuality(result);

                _logger.LogInformation("Extracted {Count} parameters from text with quality score {Quality}",
                    result.Parameters.Count, result.QualityScore);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Parameter extraction cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting parameters from text: {Text}", text);
                throw new ParameterExtractionException($"Failed to extract parameters: {ex.Message}", ex);
            }
            finally
            {
                _extractionLock.Release();
            }
        }

        /// <summary>
        /// Validates extracted parameters;
        /// </summary>
        public async Task<ParameterValidationResult> ValidateParametersAsync(
            Dictionary<string, object> parameters,
            IntentSchema intentSchema,
            CancellationToken cancellationToken = default)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            if (intentSchema == null)
                throw new ArgumentNullException(nameof(intentSchema));

            var result = new ParameterValidationResult;
            {
                ValidationTime = DateTime.UtcNow,
                ValidatedParameters = new Dictionary<string, object>(parameters)
            };

            foreach (var paramDef in intentSchema.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!parameters.TryGetValue(paramDef.Name, out var value))
                {
                    if (paramDef.IsRequired)
                    {
                        result.Errors.Add(new ParameterError;
                        {
                            ParameterName = paramDef.Name,
                            ErrorCode = ParameterErrorCode.MissingRequired,
                            Message = $"Required parameter '{paramDef.Name}' is missing"
                        });
                    }
                    continue;
                }

                // Type validation;
                if (!IsTypeCompatible(value, paramDef.DataType))
                {
                    result.Errors.Add(new ParameterError;
                    {
                        ParameterName = paramDef.Name,
                        ErrorCode = ParameterErrorCode.TypeMismatch,
                        Message = $"Parameter '{paramDef.Name}' has incorrect type. Expected: {paramDef.DataType}, Actual: {value?.GetType().Name}"
                    });
                    continue;
                }

                // Range validation for numeric types;
                if (paramDef.HasRangeConstraint && value is IComparable comparable)
                {
                    if (paramDef.MinValue != null && comparable.CompareTo(paramDef.MinValue) < 0)
                    {
                        result.Errors.Add(new ParameterError;
                        {
                            ParameterName = paramDef.Name,
                            ErrorCode = ParameterErrorCode.ValueOutOfRange,
                            Message = $"Parameter '{paramDef.Name}' value is below minimum: {paramDef.MinValue}"
                        });
                    }

                    if (paramDef.MaxValue != null && comparable.CompareTo(paramDef.MaxValue) > 0)
                    {
                        result.Errors.Add(new ParameterError;
                        {
                            ParameterName = paramDef.Name,
                            ErrorCode = ParameterErrorCode.ValueOutOfRange,
                            Message = $"Parameter '{paramDef.Name}' value is above maximum: {paramDef.MaxValue}"
                        });
                    }
                }

                // Pattern validation for strings;
                if (paramDef.HasPatternConstraint && value is string stringValue)
                {
                    if (!string.IsNullOrEmpty(paramDef.Pattern))
                    {
                        var regex = _regexCache.GetRegex(paramDef.Pattern);
                        if (!regex.IsMatch(stringValue))
                        {
                            result.Errors.Add(new ParameterError;
                            {
                                ParameterName = paramDef.Name,
                                ErrorCode = ParameterErrorCode.PatternMismatch,
                                Message = $"Parameter '{paramDef.Name}' does not match pattern: {paramDef.Pattern}"
                            });
                        }
                    }
                }

                // Enum validation;
                if (paramDef.HasEnumConstraint && paramDef.AllowedValues != null)
                {
                    if (!paramDef.AllowedValues.Contains(value))
                    {
                        result.Errors.Add(new ParameterError;
                        {
                            ParameterName = paramDef.Name,
                            ErrorCode = ParameterErrorCode.InvalidEnumValue,
                            Message = $"Parameter '{paramDef.Name}' value is not in allowed values: {string.Join(", ", paramDef.AllowedValues)}"
                        });
                    }
                }

                // Custom validation;
                if (paramDef.CustomValidator != null)
                {
                    try
                    {
                        var validationResult = await paramDef.CustomValidator.ValidateAsync(value, cancellationToken);
                        if (!validationResult.IsValid)
                        {
                            result.Errors.Add(new ParameterError;
                            {
                                ParameterName = paramDef.Name,
                                ErrorCode = ParameterErrorCode.CustomValidationFailed,
                                Message = $"Parameter '{paramDef.Name}' failed custom validation: {validationResult.ErrorMessage}"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in custom validator for parameter {Parameter}", paramDef.Name);
                    }
                }
            }

            // Check for unknown parameters;
            var knownParamNames = intentSchema.Parameters.Select(p => p.Name).ToHashSet();
            foreach (var paramName in parameters.Keys)
            {
                if (!knownParamNames.Contains(paramName))
                {
                    result.Warnings.Add(new ParameterWarning;
                    {
                        ParameterName = paramName,
                        WarningCode = ParameterWarningCode.UnknownParameter,
                        Message = $"Parameter '{paramName}' is not defined in schema"
                    });
                }
            }

            result.IsValid = result.Errors.Count == 0;
            result.Severity = result.Errors.Any(e => e.IsCritical) ?
                ValidationSeverity.Error :
                result.Warnings.Any() ? ValidationSeverity.Warning : ValidationSeverity.Success;

            return result;
        }

        /// <summary>
        /// Transforms parameters to target types;
        /// </summary>
        public async Task<Dictionary<string, object>> TransformParametersAsync(
            Dictionary<string, object> parameters,
            ParameterTransformationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            options ??= new ParameterTransformationOptions();

            var transformed = new Dictionary<string, object>();

            foreach (var kvp in parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    object transformedValue;

                    if (options.TargetTypes.TryGetValue(kvp.Key, out var targetType))
                    {
                        transformedValue = await TransformValueAsync(kvp.Value, targetType, cancellationToken);
                    }
                    else if (options.DefaultTargetType != null)
                    {
                        transformedValue = await TransformValueAsync(kvp.Value, options.DefaultTargetType, cancellationToken);
                    }
                    else;
                    {
                        transformedValue = kvp.Value;
                    }

                    // Apply transformations;
                    if (options.ValueTransformers.TryGetValue(kvp.Key, out var transformer))
                    {
                        transformedValue = await transformer.TransformAsync(transformedValue, cancellationToken);
                    }

                    // Apply formatting;
                    if (options.Formatters.TryGetValue(kvp.Key, out var formatter))
                    {
                        transformedValue = formatter.Format(transformedValue);
                    }

                    transformed[kvp.Key] = transformedValue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error transforming parameter {Parameter}", kvp.Key);

                    if (options.FailOnTransformationError)
                        throw new ParameterTransformationException($"Failed to transform parameter '{kvp.Key}': {ex.Message}", ex);

                    // Keep original value on transformation failure;
                    transformed[kvp.Key] = kvp.Value;
                }
            }

            return transformed;
        }

        /// <summary>
        /// Extracts parameters from structured data;
        /// </summary>
        public async Task<Dictionary<string, object>> ExtractFromStructuredDataAsync(
            string structuredData,
            DataFormat format,
            IntentSchema schema = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(structuredData))
                throw new ArgumentException("Structured data cannot be null or empty", nameof(structuredData));

            try
            {
                Dictionary<string, object> extracted = new();

                switch (format)
                {
                    case DataFormat.Json:
                        extracted = await ExtractFromJsonAsync(structuredData, schema, cancellationToken);
                        break;

                    case DataFormat.Xml:
                        extracted = await ExtractFromXmlAsync(structuredData, schema, cancellationToken);
                        break;

                    case DataFormat.Csv:
                        extracted = await ExtractFromCsvAsync(structuredData, schema, cancellationToken);
                        break;

                    case DataFormat.KeyValuePairs:
                        extracted = await ExtractFromKeyValuePairsAsync(structuredData, cancellationToken);
                        break;

                    default:
                        throw new NotSupportedException($"Data format {format} is not supported");
                }

                // Filter by schema if provided;
                if (schema != null)
                {
                    extracted = extracted;
                        .Where(kvp => schema.Parameters.Any(p => p.Name == kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }

                return extracted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting parameters from {Format} data", format);
                throw new StructuredDataExtractionException($"Failed to extract from {format}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Merges parameters from multiple sources;
        /// </summary>
        public async Task<Dictionary<string, object>> MergeParametersAsync(
            params ParameterSource[] sources,
            CancellationToken cancellationToken = default)
        {
            if (sources == null || sources.Length == 0)
                return new Dictionary<string, object>();

            if (sources.Length == 1)
                return new Dictionary<string, object>(sources[0].Parameters);

            var merged = new Dictionary<string, object>();
            var sourcePriorities = new Dictionary<string, List<ParameterSource>>();

            // Group parameters by name with their sources;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var param in source.Parameters)
                {
                    if (!sourcePriorities.ContainsKey(param.Key))
                        sourcePriorities[param.Key] = new List<ParameterSource>();

                    sourcePriorities[param.Key].Add(source);
                }
            }

            // Merge based on priority rules;
            foreach (var kvp in sourcePriorities)
            {
                var paramName = kvp.Key;
                var paramSources = kvp.Value;

                // Sort sources by priority;
                var sortedSources = paramSources;
                    .OrderByDescending(s => s.Priority)
                    .ThenByDescending(s => s.Confidence)
                    .ToList();

                // Take value from highest priority source;
                var primarySource = sortedSources.First();
                merged[paramName] = primarySource.Parameters[paramName];

                // Log merge decision;
                if (sortedSources.Count > 1)
                {
                    _logger.LogDebug("Merged parameter {Parameter} from {Source} (priority: {Priority})",
                        paramName, primarySource.Name, primarySource.Priority);
                }
            }

            // Resolve conflicts;
            merged = await ResolveParameterConflictsAsync(merged, sources, cancellationToken);

            return merged;
        }

        /// <summary>
        /// Extracts temporal parameters;
        /// </summary>
        public async Task<TemporalParameters> ExtractTemporalParametersAsync(
            string text,
            DateTime? referenceTime = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new TemporalParameters();

            referenceTime ??= DateTime.UtcNow;

            var result = new TemporalParameters;
            {
                ReferenceTime = referenceTime.Value,
                ExtractedText = text;
            };

            try
            {
                // Extract dates;
                var dateMatches = _regexCache.GetRegex(Patterns.DatePattern).Matches(text);
                foreach (Match match in dateMatches)
                {
                    if (DateTime.TryParse(match.Value, out var date))
                    {
                        result.Dates.Add(date);
                    }
                }

                // Extract times;
                var timeMatches = _regexCache.GetRegex(Patterns.TimePattern).Matches(text);
                foreach (Match match in timeMatches)
                {
                    if (TimeSpan.TryParse(match.Value, out var time))
                    {
                        result.Times.Add(time);
                    }
                }

                // Extract durations;
                var durationMatches = _regexCache.GetRegex(Patterns.DurationPattern).Matches(text);
                foreach (Match match in durationMatches)
                {
                    if (TryParseDuration(match.Value, out var duration))
                    {
                        result.Durations.Add(duration);
                    }
                }

                // Extract date ranges;
                var rangeMatches = _regexCache.GetRegex(Patterns.DateRangePattern).Matches(text);
                foreach (Match match in rangeMatches)
                {
                    var range = ParseDateRange(match.Value, referenceTime.Value);
                    if (range != null)
                    {
                        result.DateRanges.Add(range);
                    }
                }

                // Extract relative times;
                var relativeMatches = _regexCache.GetRegex(Patterns.RelativeTimePattern).Matches(text);
                foreach (Match match in relativeMatches)
                {
                    var relativeTime = ParseRelativeTime(match.Value, referenceTime.Value);
                    if (relativeTime != null)
                    {
                        result.RelativeTimes.Add(relativeTime.Value);
                    }
                }

                // Calculate primary date/time if multiple found;
                if (result.Dates.Any() || result.Times.Any())
                {
                    result.PrimaryDateTime = CalculatePrimaryDateTime(
                        result.Dates,
                        result.Times,
                        referenceTime.Value);
                }

                result.IsSuccess = result.Dates.Any() || result.Times.Any() || result.Durations.Any();
                result.Confidence = CalculateTemporalConfidence(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting temporal parameters from text: {Text}", text);
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Extracts numeric parameters;
        /// </summary>
        public async Task<NumericParameters> ExtractNumericParametersAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new NumericParameters();

            var result = new NumericParameters;
            {
                ExtractedText = text;
            };

            try
            {
                // Extract numbers;
                var numberMatches = _regexCache.GetRegex(Patterns.NumberPattern).Matches(text);
                foreach (Match match in numberMatches)
                {
                    if (double.TryParse(match.Value, out var number))
                    {
                        result.Numbers.Add(number);
                        result.NumberPositions.Add(match.Index);
                    }
                }

                // Extract currencies;
                var currencyMatches = _regexCache.GetRegex(Patterns.CurrencyPattern).Matches(text);
                foreach (Match match in currencyMatches)
                {
                    var currency = ParseCurrency(match.Value);
                    if (currency != null)
                    {
                        result.Currencies.Add(currency);
                    }
                }

                // Extract percentages;
                var percentMatches = _regexCache.GetRegex(Patterns.PercentagePattern).Matches(text);
                foreach (Match match in percentMatches)
                {
                    if (TryParsePercentage(match.Value, out var percentage))
                    {
                        result.Percentages.Add(percentage);
                    }
                }

                // Extract measurements;
                var measurementMatches = _regexCache.GetRegex(Patterns.MeasurementPattern).Matches(text);
                foreach (Match match in measurementMatches)
                {
                    var measurement = ParseMeasurement(match.Value);
                    if (measurement != null)
                    {
                        result.Measurements.Add(measurement);
                    }
                }

                // Extract ranges;
                var rangeMatches = _regexCache.GetRegex(Patterns.NumericRangePattern).Matches(text);
                foreach (Match match in rangeMatches)
                {
                    var range = ParseNumericRange(match.Value);
                    if (range != null)
                    {
                        result.Ranges.Add(range);
                    }
                }

                // Extract units;
                foreach (var number in result.Numbers)
                {
                    var unit = ExtractUnit(text, result.Numbers.IndexOf(number));
                    if (!string.IsNullOrEmpty(unit))
                    {
                        result.Units.Add(unit);
                    }
                }

                result.IsSuccess = result.Numbers.Any() || result.Currencies.Any() || result.Measurements.Any();
                result.Confidence = CalculateNumericConfidence(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting numeric parameters from text: {Text}", text);
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Extracts entity parameters;
        /// </summary>
        public async Task<EntityParameters> ExtractEntityParametersAsync(
            string text,
            ExtractionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new EntityParameters();

            var result = new EntityParameters;
            {
                ExtractedText = text,
                ExtractionTime = DateTime.UtcNow;
            };

            try
            {
                // Extract named entities;
                var entities = await _entityExtractor.ExtractEntitiesAsync(text, cancellationToken);

                foreach (var entity in entities)
                {
                    switch (entity.Type)
                    {
                        case EntityType.Person:
                            result.Persons.Add(new PersonEntity;
                            {
                                Name = entity.Value,
                                Confidence = entity.Confidence,
                                Position = entity.Position;
                            });
                            break;

                        case EntityType.Location:
                            result.Locations.Add(new LocationEntity;
                            {
                                Name = entity.Value,
                                Confidence = entity.Confidence,
                                Position = entity.Position;
                            });
                            break;

                        case EntityType.Organization:
                            result.Organizations.Add(new OrganizationEntity;
                            {
                                Name = entity.Value,
                                Confidence = entity.Confidence,
                                Position = entity.Position;
                            });
                            break;

                        case EntityType.Product:
                            result.Products.Add(new ProductEntity;
                            {
                                Name = entity.Value,
                                Confidence = entity.Confidence,
                                Position = entity.Position;
                            });
                            break;

                        case EntityType.Event:
                            result.Events.Add(new EventEntity;
                            {
                                Name = entity.Value,
                                Confidence = entity.Confidence,
                                Position = entity.Position;
                            });
                            break;
                    }
                }

                // Extract relationships between entities;
                if (entities.Count >= 2)
                {
                    result.Relationships = await ExtractEntityRelationshipsAsync(entities, text, cancellationToken);
                }

                // Extract entity attributes;
                result.Attributes = await ExtractEntityAttributesAsync(entities, text, context, cancellationToken);

                result.IsSuccess = entities.Any();
                result.Confidence = entities.Any() ? entities.Average(e => e.Confidence) : 0;
                result.EntityCount = entities.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting entity parameters from text: {Text}", text);
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Extracts location parameters;
        /// </summary>
        public async Task<LocationParameters> ExtractLocationParametersAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new LocationParameters();

            var result = new LocationParameters;
            {
                ExtractedText = text;
            };

            try
            {
                // Extract addresses;
                var addressMatches = _regexCache.GetRegex(Patterns.AddressPattern).Matches(text);
                foreach (Match match in addressMatches)
                {
                    result.Addresses.Add(match.Value);
                }

                // Extract coordinates;
                var coordMatches = _regexCache.GetRegex(Patterns.CoordinatePattern).Matches(text);
                foreach (Match match in coordMatches)
                {
                    var coords = ParseCoordinates(match.Value);
                    if (coords != null)
                    {
                        result.Coordinates.Add(coords);
                    }
                }

                // Extract place names using entity extraction;
                var entities = await _entityExtractor.ExtractEntitiesAsync(text, cancellationToken);
                var locationEntities = entities.Where(e => e.Type == EntityType.Location);

                foreach (var entity in locationEntities)
                {
                    result.Places.Add(new Place;
                    {
                        Name = entity.Value,
                        Type = InferPlaceType(entity.Value),
                        Confidence = entity.Confidence;
                    });
                }

                // Extract relative locations;
                var relativeMatches = _regexCache.GetRegex(Patterns.RelativeLocationPattern).Matches(text);
                foreach (Match match in relativeMatches)
                {
                    result.RelativeLocations.Add(match.Value);
                }

                // Extract location types (city, country, etc.)
                foreach (var place in result.Places)
                {
                    place.Type ??= ClassifyLocationType(place.Name, text);
                }

                result.IsSuccess = result.Addresses.Any() || result.Coordinates.Any() || result.Places.Any();
                result.Confidence = CalculateLocationConfidence(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting location parameters from text: {Text}", text);
                result.Error = ex.Message;
            }

            return result;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _extractionLock?.Dispose();
            _regexCache?.Dispose();

            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private void InitializePatterns()
        {
            // Precompile commonly used regex patterns;
            _regexCache.AddPattern("email", Patterns.EmailPattern);
            _regexCache.AddPattern("phone", Patterns.PhonePattern);
            _regexCache.AddPattern("url", Patterns.UrlPattern);
            _regexCache.AddPattern("ip", Patterns.IpAddressPattern);
            _regexCache.AddPattern("date", Patterns.DatePattern);
            _regexCache.AddPattern("time", Patterns.TimePattern);
            _regexCache.AddPattern("number", Patterns.NumberPattern);
            _regexCache.AddPattern("currency", Patterns.CurrencyPattern);
        }

        private async Task<ParameterExtraction> ExtractParameterAsync(
            string text,
            ParameterDefinition parameterDef,
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            List<ExtractedEntity> entities,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var extraction = new ParameterExtraction;
            {
                ParameterName = parameterDef.Name,
                DataType = parameterDef.DataType;
            };

            try
            {
                // Try different extraction methods in order of preference;
                var extractionMethods = new List<Func<Task<ParameterExtraction>>>
                {
                    // 1. Try pattern matching;
                    () => ExtractByPatternAsync(text, parameterDef, cancellationToken),
                    
                    // 2. Try entity extraction;
                    () => ExtractFromEntitiesAsync(text, parameterDef, entities, cancellationToken),
                    
                    // 3. Try semantic extraction;
                    () => ExtractSemanticallyAsync(text, parameterDef, semanticAnalysis, cancellationToken),
                    
                    // 4. Try syntax-based extraction;
                    () => ExtractFromSyntaxAsync(text, parameterDef, syntaxTree, cancellationToken),
                    
                    // 5. Try context-based extraction;
                    () => ExtractFromContextAsync(parameterDef, context, cancellationToken)
                };

                foreach (var method in extractionMethods)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var result = await method();
                    if (result != null && result.IsSuccess && result.Confidence > 0.5f)
                    {
                        extraction.Value = result.Value;
                        extraction.Confidence = result.Confidence;
                        extraction.Source = result.Source;
                        extraction.IsSuccess = true;
                        break;
                    }
                }

                // If still not found, try fuzzy matching;
                if (!extraction.IsSuccess)
                {
                    var fuzzyResult = await ExtractByFuzzyMatchingAsync(text, parameterDef, cancellationToken);
                    if (fuzzyResult != null && fuzzyResult.IsSuccess)
                    {
                        extraction.Value = fuzzyResult.Value;
                        extraction.Confidence = fuzzyResult.Confidence * 0.7f; // Reduce confidence for fuzzy matches;
                        extraction.Source = ExtractionSource.FuzzyMatch;
                        extraction.IsSuccess = true;
                    }
                }

                // Transform value to target type;
                if (extraction.IsSuccess)
                {
                    extraction.Value = await TransformToDataTypeAsync(
                        extraction.Value,
                        parameterDef.DataType,
                        cancellationToken);

                    // Apply constraints;
                    extraction.Value = ApplyConstraints(extraction.Value, parameterDef);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in parameter extraction for {Parameter}", parameterDef.Name);
                extraction.Error = ex.Message;
            }

            return extraction;
        }

        private async Task<ParameterExtraction> ExtractByPatternAsync(
            string text,
            ParameterDefinition parameterDef,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(parameterDef.Pattern))
                return null;

            var regex = _regexCache.GetRegex(parameterDef.Pattern);
            var match = regex.Match(text);

            if (match.Success)
            {
                return new ParameterExtraction;
                {
                    Value = match.Value,
                    Confidence = 0.9f,
                    Source = ExtractionSource.PatternMatch,
                    IsSuccess = true;
                };
            }

            return null;
        }

        private async Task<ParameterExtraction> ExtractFromEntitiesAsync(
            string text,
            ParameterDefinition parameterDef,
            List<ExtractedEntity> entities,
            CancellationToken cancellationToken)
        {
            // Find entities that match the parameter type;
            var matchingEntities = entities;
                .Where(e => EntityTypeMatchesParameter(e.Type, parameterDef.DataType))
                .OrderByDescending(e => e.Confidence)
                .ToList();

            if (matchingEntities.Any())
            {
                var bestEntity = matchingEntities.First();
                return new ParameterExtraction;
                {
                    Value = bestEntity.Value,
                    Confidence = bestEntity.Confidence,
                    Source = ExtractionSource.EntityRecognition,
                    IsSuccess = true;
                };
            }

            return null;
        }

        private async Task<ParameterExtraction> ExtractSemanticallyAsync(
            string text,
            ParameterDefinition parameterDef,
            SemanticAnalysis semanticAnalysis,
            CancellationToken cancellationToken)
        {
            // Use semantic analysis to find parameter values;
            // This is a simplified version - actual implementation would be more sophisticated;
            var relevantTokens = semanticAnalysis.Tokens;
                .Where(t => t.RelevanceToParameter(parameterDef.Name) > 0.5)
                .OrderByDescending(t => t.Confidence)
                .ToList();

            if (relevantTokens.Any())
            {
                var bestToken = relevantTokens.First();
                return new ParameterExtraction;
                {
                    Value = bestToken.Value,
                    Confidence = bestToken.Confidence * 0.8f,
                    Source = ExtractionSource.SemanticAnalysis,
                    IsSuccess = true;
                };
            }

            return null;
        }

        private async Task<ParameterExtraction> ExtractFromSyntaxAsync(
            string text,
            ParameterDefinition parameterDef,
            SyntaxTree syntaxTree,
            CancellationToken cancellationToken)
        {
            // Extract based on syntactic patterns;
            // For example, for a "recipient" parameter, look for nouns in indirect object position;
            var candidates = syntaxTree.FindNodesByRole(parameterDef.SyntacticRole);

            if (candidates.Any())
            {
                var bestCandidate = candidates.OrderByDescending(c => c.Confidence).First();
                return new ParameterExtraction;
                {
                    Value = bestCandidate.Text,
                    Confidence = bestCandidate.Confidence * 0.7f,
                    Source = ExtractionSource.SyntacticAnalysis,
                    IsSuccess = true;
                };
            }

            return null;
        }

        private async Task<ParameterExtraction> ExtractFromContextAsync(
            ParameterDefinition parameterDef,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            if (context == null || context.Parameters == null)
                return null;

            if (context.Parameters.TryGetValue(parameterDef.Name, out var value))
            {
                return new ParameterExtraction;
                {
                    Value = value,
                    Confidence = 0.6f,
                    Source = ExtractionSource.Context,
                    IsSuccess = true;
                };
            }

            return null;
        }

        private async Task<ParameterExtraction> ExtractByFuzzyMatchingAsync(
            string text,
            ParameterDefinition parameterDef,
            CancellationToken cancellationToken)
        {
            // Use fuzzy matching for parameter names in text;
            // For example, if parameter is "filename", look for words that look like filenames;
            var words = text.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                if (LooksLikeParameterValue(word, parameterDef.DataType))
                {
                    return new ParameterExtraction;
                    {
                        Value = word,
                        Confidence = 0.5f,
                        Source = ExtractionSource.FuzzyMatch,
                        IsSuccess = true;
                    };
                }
            }

            return null;
        }

        private async Task<Dictionary<string, object>> ExtractContextParametersAsync(
            string text,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, object>();

            if (context == null)
                return parameters;

            // Extract implicit parameters from context;
            if (context.ConversationHistory?.Any() == true)
            {
                // Get last user message for reference;
                var lastUserMessage = context.ConversationHistory;
                    .LastOrDefault(m => m.Role == "user");

                if (lastUserMessage != null)
                {
                    parameters["previous_message"] = lastUserMessage.Content;
                    parameters["conversation_topic"] = ExtractTopic(lastUserMessage.Content);
                }
            }

            // Add system context;
            if (context.SystemContext != null)
            {
                parameters["current_time"] = DateTime.UtcNow;
                parameters["timezone"] = context.SystemContext.TimeZone;
                parameters["language"] = context.SystemContext.Language;
            }

            // Add user context;
            if (context.UserContext != null)
            {
                parameters["user_id"] = context.UserContext.UserId;
                parameters["user_role"] = context.UserContext.Role;

                if (context.UserContext.Preferences != null)
                {
                    foreach (var pref in context.UserContext.Preferences)
                    {
                        parameters[$"pref_{pref.Key}"] = pref.Value;
                    }
                }
            }

            return parameters;
        }

        private async Task<Dictionary<string, object>> ResolveParameterDependenciesAsync(
            Dictionary<string, object> parameters,
            IntentSchema schema,
            CancellationToken cancellationToken)
        {
            var resolved = new Dictionary<string, object>(parameters);

            // Resolve references (e.g., $otherParameter)
            foreach (var param in parameters)
            {
                if (param.Value is string stringValue && stringValue.StartsWith("$"))
                {
                    var referencedParam = stringValue.TrimStart('$');
                    if (parameters.ContainsKey(referencedParam))
                    {
                        resolved[param.Key] = parameters[referencedParam];
                    }
                }
            }

            // Apply dependencies defined in schema;
            foreach (var paramDef in schema.Parameters)
            {
                if (paramDef.DependsOn != null && resolved.ContainsKey(paramDef.Name))
                {
                    foreach (var dependency in paramDef.DependsOn)
                    {
                        if (!resolved.ContainsKey(dependency))
                        {
                            // Dependency not satisfied, remove parameter;
                            resolved.Remove(paramDef.Name);
                            break;
                        }
                    }
                }
            }

            // Compute derived parameters;
            foreach (var paramDef in schema.Parameters.Where(p => p.IsDerived))
            {
                if (paramDef.DerivationFunction != null)
                {
                    try
                    {
                        var derivedValue = await paramDef.DerivationFunction.ComputeAsync(resolved, cancellationToken);
                        resolved[paramDef.Name] = derivedValue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error computing derived parameter {Parameter}", paramDef.Name);
                    }
                }
            }

            return resolved;
        }

        private async Task<Dictionary<string, object>> ResolveParameterConflictsAsync(
            Dictionary<string, object> merged,
            ParameterSource[] sources,
            CancellationToken cancellationToken)
        {
            var resolved = new Dictionary<string, object>(merged);

            // Identify conflicts;
            var conflicts = new Dictionary<string, List<object>>();

            foreach (var source in sources)
            {
                foreach (var param in source.Parameters)
                {
                    if (!conflicts.ContainsKey(param.Key))
                        conflicts[param.Key] = new List<object>();

                    if (!conflicts[param.Key].Contains(param.Value))
                        conflicts[param.Key].Add(param.Value);
                }
            }

            // Resolve conflicts;
            foreach (var conflict in conflicts.Where(c => c.Value.Count > 1))
            {
                var paramName = conflict.Key;
                var values = conflict.Value;

                // Use conflict resolution strategy;
                var resolvedValue = await ResolveConflictAsync(paramName, values, sources, cancellationToken);
                resolved[paramName] = resolvedValue;

                _logger.LogDebug("Resolved conflict for parameter {Parameter}: {Values} -> {Resolved}",
                    paramName, string.Join(", ", values), resolvedValue);
            }

            return resolved;
        }

        private async Task<object> ResolveConflictAsync(
            string paramName,
            List<object> values,
            ParameterSource[] sources,
            CancellationToken cancellationToken)
        {
            // Simple conflict resolution - take most frequent value;
            var frequency = values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
            var mostFrequent = frequency.OrderByDescending(kvp => kvp.Value).First().Key;

            return mostFrequent;
        }

        private async Task<Dictionary<string, object>> ExtractFromJsonAsync(
            string json,
            IntentSchema schema,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, object>();

            try
            {
                var jsonObject = JObject.Parse(json);

                if (schema == null)
                {
                    // Extract all properties;
                    foreach (var property in jsonObject.Properties())
                    {
                        parameters[property.Name] = ConvertToken(property.Value);
                    }
                }
                else;
                {
                    // Extract only schema-defined parameters;
                    foreach (var paramDef in schema.Parameters)
                    {
                        var token = jsonObject.SelectToken(paramDef.JsonPath ?? paramDef.Name);
                        if (token != null)
                        {
                            parameters[paramDef.Name] = ConvertToken(token);
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new StructuredDataExtractionException("Invalid JSON format", ex);
            }

            return parameters;
        }

        private async Task<Dictionary<string, object>> ExtractFromXmlAsync(
            string xml,
            IntentSchema schema,
            CancellationToken cancellationToken)
        {
            // XML parsing implementation;
            // This is a simplified version;
            var parameters = new Dictionary<string, object>();

            // Implementation would use XmlDocument or XmlReader;
            // For brevity, returning empty dictionary;
            return parameters;
        }

        private async Task<Dictionary<string, object>> ExtractFromCsvAsync(
            string csv,
            IntentSchema schema,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, object>();
            var lines = csv.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0)
                return parameters;

            var headers = lines[0].Split(',');

            for (int i = 1; i < lines.Length && i < 2; i++) // Take first data row only;
            {
                var values = lines[i].Split(',');
                for (int j = 0; j < Math.Min(headers.Length, values.Length); j++)
                {
                    parameters[headers[j].Trim()] = values[j].Trim();
                }
            }

            return parameters;
        }

        private async Task<Dictionary<string, object>> ExtractFromKeyValuePairsAsync(
            string pairs,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, object>();
            var lines = pairs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '=', ':' }, 2);
                if (parts.Length == 2)
                {
                    parameters[parts[0].Trim()] = parts[1].Trim();
                }
            }

            return parameters;
        }

        private bool IsTypeCompatible(object value, ParameterDataType dataType)
        {
            if (value == null)
                return !dataType.IsValueType || dataType.IsNullable;

            var actualType = value.GetType();

            return dataType.ClrType.IsAssignableFrom(actualType);
        }

        private async Task<object> TransformValueAsync(object value, Type targetType, CancellationToken cancellationToken)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsAssignableFrom(value.GetType()))
                return value;

            // Handle common conversions;
            try
            {
                if (targetType == typeof(string))
                    return value.ToString();

                if (targetType == typeof(int) || targetType == typeof(int?))
                    return Convert.ToInt32(value);

                if (targetType == typeof(double) || targetType == typeof(double?))
                    return Convert.ToDouble(value);

                if (targetType == typeof(bool) || targetType == typeof(bool?))
                    return Convert.ToBoolean(value);

                if (targetType == typeof(DateTime) || targetType == typeof(DateTime?))
                    return Convert.ToDateTime(value);

                // Try type converter;
                var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
                if (converter.CanConvertFrom(value.GetType()))
                {
                    return converter.ConvertFrom(value);
                }

                // Try JSON serialization for complex objects;
                if (value is string jsonString)
                {
                    return JsonConvert.DeserializeObject(jsonString, targetType);
                }
            }
            catch
            {
                // Conversion failed;
            }

            throw new InvalidCastException($"Cannot convert {value.GetType().Name} to {targetType.Name}");
        }

        private object ApplyConstraints(object value, ParameterDefinition parameterDef)
        {
            if (value == null)
                return null;

            // Apply string constraints;
            if (value is string stringValue)
            {
                if (parameterDef.MaxLength > 0 && stringValue.Length > parameterDef.MaxLength)
                {
                    stringValue = stringValue.Substring(0, parameterDef.MaxLength);
                }

                if (parameterDef.MinLength > 0 && stringValue.Length < parameterDef.MinLength)
                {
                    // Pad or return null based on configuration;
                    if (parameterDef.PadToMinLength)
                    {
                        stringValue = stringValue.PadRight(parameterDef.MinLength);
                    }
                    else;
                    {
                        return null;
                    }
                }

                if (!string.IsNullOrEmpty(parameterDef.AllowedCharacters))
                {
                    stringValue = new string(stringValue;
                        .Where(c => parameterDef.AllowedCharacters.Contains(c))
                        .ToArray());
                }

                return stringValue;
            }

            // Apply numeric constraints;
            if (value is IComparable comparable)
            {
                if (parameterDef.MinValue != null && comparable.CompareTo(parameterDef.MinValue) < 0)
                {
                    value = parameterDef.MinValue;
                }

                if (parameterDef.MaxValue != null && comparable.CompareTo(parameterDef.MaxValue) > 0)
                {
                    value = parameterDef.MaxValue;
                }
            }

            return value;
        }

        private float CalculateExtractionQuality(ParameterExtractionResult result)
        {
            if (result.Parameters.Count == 0)
                return 0;

            var factors = new List<float>();

            // Factor 1: Completeness;
            var completeness = result.IntentSchema.RequiredParameters;
                .Count(p => result.Parameters.ContainsKey(p)) /
                (float)result.IntentSchema.RequiredParameters.Count;
            factors.Add(completeness * 0.4f);

            // Factor 2: Confidence;
            var avgConfidence = result.ConfidenceScores.Values.Any() ?
                result.ConfidenceScores.Values.Average() : 0;
            factors.Add(avgConfidence * 0.3f);

            // Factor 3: Validation;
            var validationScore = result.ValidationResult?.IsValid == true ? 1.0f : 0.5f;
            factors.Add(validationScore * 0.2f);

            // Factor 4: Source quality;
            var sourceScore = CalculateSourceQuality(result.ExtractionSources);
            factors.Add(sourceScore * 0.1f);

            return factors.Sum();
        }

        private float CalculateSourceQuality(Dictionary<string, ExtractionSource> sources)
        {
            if (sources.Count == 0)
                return 0;

            var sourceWeights = new Dictionary<ExtractionSource, float>
            {
                [ExtractionSource.PatternMatch] = 1.0f,
                [ExtractionSource.EntityRecognition] = 0.9f,
                [ExtractionSource.SemanticAnalysis] = 0.8f,
                [ExtractionSource.SyntacticAnalysis] = 0.7f,
                [ExtractionSource.Context] = 0.6f,
                [ExtractionSource.FuzzyMatch] = 0.5f,
                [ExtractionSource.DefaultValue] = 0.3f;
            };

            return sources.Values.Average(s => sourceWeights.GetValueOrDefault(s, 0.5f));
        }

        private async Task<object> TransformToDataTypeAsync(object value, ParameterDataType dataType, CancellationToken cancellationToken)
        {
            // Implementation for transforming to specific data types;
            // This would handle custom data type conversions;
            return value;
        }

        private bool EntityTypeMatchesParameter(EntityType entityType, ParameterDataType dataType)
        {
            // Map entity types to parameter data types;
            var mapping = new Dictionary<EntityType, List<ParameterDataType>>
            {
                [EntityType.Person] = new List<ParameterDataType> {
                    ParameterDataType.String,
                    ParameterDataType.PersonName;
                },
                [EntityType.Location] = new List<ParameterDataType> {
                    ParameterDataType.String,
                    ParameterDataType.Location;
                },
                [EntityType.Organization] = new List<ParameterDataType> {
                    ParameterDataType.String,
                    ParameterDataType.Organization;
                },
                [EntityType.Date] = new List<ParameterDataType> {
                    ParameterDataType.DateTime,
                    ParameterDataType.Date;
                },
                [EntityType.Time] = new List<ParameterDataType> {
                    ParameterDataType.TimeSpan,
                    ParameterDataType.Time;
                },
                [EntityType.Money] = new List<ParameterDataType> {
                    ParameterDataType.Decimal,
                    ParameterDataType.Currency;
                },
                [EntityType.Percent] = new List<ParameterDataType> {
                    ParameterDataType.Double,
                    ParameterDataType.Percentage;
                }
            };

            return mapping.ContainsKey(entityType) &&
                   mapping[entityType].Contains(dataType);
        }

        private bool LooksLikeParameterValue(string text, ParameterDataType dataType)
        {
            // Simple heuristic for fuzzy matching;
            switch (dataType)
            {
                case ParameterDataType.Email:
                    return text.Contains("@") && text.Contains(".");

                case ParameterDataType.PhoneNumber:
                    return text.All(char.IsDigit) && text.Length >= 7;

                case ParameterDataType.Url:
                    return text.StartsWith("http") || text.Contains(".com") || text.Contains(".org");

                case ParameterDataType.FilePath:
                    return text.Contains("\\") || text.Contains("/") || text.Contains(".");

                default:
                    return false;
            }
        }

        private string ExtractTopic(string text)
        {
            // Simple topic extraction;
            var keywords = new[] { "weather", "news", "email", "file", "schedule", "meeting", "reminder" };
            return keywords.FirstOrDefault(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)) ?? "general";
        }

        #region Temporal Parameter Helpers;

        private bool TryParseDuration(string text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            // Simple duration parsing;
            var match = _regexCache.GetRegex(@"(\d+)\s*(hours?|hrs?|minutes?|mins?|seconds?|secs?)").Match(text);
            if (match.Success)
            {
                var value = int.Parse(match.Groups[1].Value);
                var unit = match.Groups[2].Value.ToLower();

                duration = unit.StartsWith("hour") ? TimeSpan.FromHours(value) :
                          unit.StartsWith("min") ? TimeSpan.FromMinutes(value) :
                          TimeSpan.FromSeconds(value);

                return true;
            }

            return false;
        }

        private DateRange ParseDateRange(string text, DateTime referenceTime)
        {
            // Simplified date range parsing;
            return null;
        }

        private DateTime? ParseRelativeTime(string text, DateTime referenceTime)
        {
            // Simplified relative time parsing;
            if (text.Contains("tomorrow"))
                return referenceTime.AddDays(1);

            if (text.Contains("yesterday"))
                return referenceTime.AddDays(-1);

            if (text.Contains("next week"))
                return referenceTime.AddDays(7);

            return null;
        }

        private DateTime? CalculatePrimaryDateTime(List<DateTime> dates, List<TimeSpan> times, DateTime referenceTime)
        {
            if (!dates.Any() && !times.Any())
                return null;

            var date = dates.FirstOrDefault();
            var time = times.FirstOrDefault();

            if (date == default && time == default)
                return null;

            if (date != default && time != default)
                return date.Date.Add(time);

            if (date != default)
                return date;

            // Time only, use reference date;
            return referenceTime.Date.Add(time);
        }

        private float CalculateTemporalConfidence(TemporalParameters parameters)
        {
            var factors = new List<float>();

            if (parameters.Dates.Any()) factors.Add(0.3f);
            if (parameters.Times.Any()) factors.Add(0.3f);
            if (parameters.Durations.Any()) factors.Add(0.2f);
            if (parameters.DateRanges.Any()) factors.Add(0.2f);

            return factors.Sum();
        }

        #endregion;

        #region Numeric Parameter Helpers;

        private Currency ParseCurrency(string text)
        {
            // Simplified currency parsing;
            return null;
        }

        private bool TryParsePercentage(string text, out double percentage)
        {
            percentage = 0;

            var match = _regexCache.GetRegex(@"(\d+(?:\.\d+)?)\s*%").Match(text);
            if (match.Success)
            {
                return double.TryParse(match.Groups[1].Value, out percentage);
            }

            return false;
        }

        private Measurement ParseMeasurement(string text)
        {
            // Simplified measurement parsing;
            return null;
        }

        private NumericRange ParseNumericRange(string text)
        {
            // Simplified range parsing;
            return null;
        }

        private string ExtractUnit(string text, int numberIndex)
        {
            // Simple unit extraction;
            var words = text.Split(' ');
            if (numberIndex >= 0 && numberIndex < words.Length - 1)
            {
                var nextWord = words[numberIndex + 1];
                var units = new[] { "cm", "m", "km", "g", "kg", "ml", "l", "px", "em", "rem" };

                if (units.Contains(nextWord.ToLower()))
                    return nextWord;
            }

            return null;
        }

        private float CalculateNumericConfidence(NumericParameters parameters)
        {
            var factors = new List<float>();

            if (parameters.Numbers.Any()) factors.Add(0.3f);
            if (parameters.Currencies.Any()) factors.Add(0.3f);
            if (parameters.Measurements.Any()) factors.Add(0.2f);
            if (parameters.Ranges.Any()) factors.Add(0.1f);
            if (parameters.Percentages.Any()) factors.Add(0.1f);

            return factors.Sum();
        }

        #endregion;

        #region Entity Parameter Helpers;

        private async Task<List<EntityRelationship>> ExtractEntityRelationshipsAsync(
            List<ExtractedEntity> entities,
            string text,
            CancellationToken cancellationToken)
        {
            var relationships = new List<EntityRelationship>();

            // Simple relationship extraction based on proximity;
            for (int i = 0; i < entities.Count - 1; i++)
            {
                for (int j = i + 1; j < entities.Count; j++)
                {
                    var distance = Math.Abs(entities[i].Position - entities[j].Position);
                    if (distance < 50) // Words within 50 characters;
                    {
                        relationships.Add(new EntityRelationship;
                        {
                            SourceEntity = entities[i],
                            TargetEntity = entities[j],
                            RelationshipType = "proximity",
                            Confidence = 1.0f - (distance / 100.0f)
                        });
                    }
                }
            }

            return relationships;
        }

        private async Task<Dictionary<string, List<EntityAttribute>>> ExtractEntityAttributesAsync(
            List<ExtractedEntity> entities,
            string text,
            ExtractionContext context,
            CancellationToken cancellationToken)
        {
            var attributes = new Dictionary<string, List<EntityAttribute>>();

            // Simple attribute extraction;
            foreach (var entity in entities)
            {
                var entityAttributes = new List<EntityAttribute>();

                // Extract adjectives before the entity;
                var words = text.Split(' ');
                if (entity.Position > 0)
                {
                    var precedingWord = words.ElementAtOrDefault(entity.Position - 1);
                    if (!string.IsNullOrEmpty(precedingWord) &&
                        precedingWord.EndsWith("ly") || precedingWord.EndsWith("ful"))
                    {
                        entityAttributes.Add(new EntityAttribute;
                        {
                            Name = "modifier",
                            Value = precedingWord,
                            Confidence = 0.7f;
                        });
                    }
                }

                attributes[entity.Value] = entityAttributes;
            }

            return attributes;
        }

        #endregion;

        #region Location Parameter Helpers;

        private Coordinates ParseCoordinates(string text)
        {
            // Simplified coordinate parsing;
            return null;
        }

        private string InferPlaceType(string placeName)
        {
            // Simple place type inference;
            var suffixes = new Dictionary<string, string>
            {
                ["city"] = "city",
                ["town"] = "town",
                ["village"] = "village",
                ["country"] = "country",
                ["state"] = "state",
                ["street"] = "street",
                ["avenue"] = "avenue",
                ["road"] = "road",
                ["park"] = "park",
                ["airport"] = "airport",
                ["station"] = "station"
            };

            foreach (var suffix in suffixes)
            {
                if (placeName.EndsWith(suffix.Key, StringComparison.OrdinalIgnoreCase))
                    return suffix.Value;
            }

            return "place";
        }

        private string ClassifyLocationType(string placeName, string context)
        {
            // Context-aware location type classification;
            if (context.Contains("airport") || context.Contains("flight"))
                return "airport";

            if (context.Contains("hotel") || context.Contains("stay"))
                return "hotel";

            if (context.Contains("restaurant") || context.Contains("eat") || context.Contains("food"))
                return "restaurant";

            return "location";
        }

        private float CalculateLocationConfidence(LocationParameters parameters)
        {
            var factors = new List<float>();

            if (parameters.Addresses.Any()) factors.Add(0.4f);
            if (parameters.Coordinates.Any()) factors.Add(0.3f);
            if (parameters.Places.Any()) factors.Add(0.3f);

            return factors.Sum();
        }

        #endregion;

        #endregion;
    }

    #region Supporting Classes and Enums;

    public class ParameterExtractionOptions;
    {
        public float MinimumConfidence { get; set; } = 0.5f;
        public bool EnableFuzzyMatching { get; set; } = true;
        public bool UseContextParameters { get; set; } = true;
        public int MaxExtractionTimeMs { get; set; } = 5000;
        public List<ExtractionStrategy> Strategies { get; set; } = new();
    }

    public class ParameterExtractionResult;
    {
        public string OriginalText { get; set; }
        public IntentSchema IntentSchema { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public Dictionary<string, float> ConfidenceScores { get; set; } = new();
        public Dictionary<string, ExtractionSource> ExtractionSources { get; set; } = new();
        public bool IsComplete { get; set; }
        public float QualityScore { get; set; }
        public DateTime ExtractionTime { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public SemanticAnalysis SemanticAnalysis { get; set; }
        public List<ExtractedEntity> Entities { get; set; } = new();
        public ParameterValidationResult ValidationResult { get; set; }
    }

    public class ParameterExtraction;
    {
        public string ParameterName { get; set; }
        public ParameterDataType DataType { get; set; }
        public object Value { get; set; }
        public float Confidence { get; set; }
        public ExtractionSource Source { get; set; }
        public bool IsSuccess { get; set; }
        public string Error { get; set; }
    }

    public class ParameterValidationResult;
    {
        public bool IsValid { get; set; }
        public ValidationSeverity Severity { get; set; }
        public DateTime ValidationTime { get; set; }
        public Dictionary<string, object> ValidatedParameters { get; set; } = new();
        public List<ParameterError> Errors { get; set; } = new();
        public List<ParameterWarning> Warnings { get; set; } = new();
    }

    public class ParameterTransformationOptions;
    {
        public Dictionary<string, Type> TargetTypes { get; set; } = new();
        public Type DefaultTargetType { get; set; }
        public Dictionary<string, IValueTransformer> ValueTransformers { get; set; } = new();
        public Dictionary<string, IValueFormatter> Formatters { get; set; } = new();
        public bool FailOnTransformationError { get; set; } = false;
    }

    public class TemporalParameters;
    {
        public string ExtractedText { get; set; }
        public DateTime ReferenceTime { get; set; }
        public List<DateTime> Dates { get; set; } = new();
        public List<TimeSpan> Times { get; set; } = new();
        public List<TimeSpan> Durations { get; set; } = new();
        public List<DateRange> DateRanges { get; set; } = new();
        public List<DateTime> RelativeTimes { get; set; } = new();
        public DateTime? PrimaryDateTime { get; set; }
        public bool IsSuccess { get; set; }
        public float Confidence { get; set; }
        public string Error { get; set; }
    }

    public class NumericParameters;
    {
        public string ExtractedText { get; set; }
        public List<double> Numbers { get; set; } = new();
        public List<int> NumberPositions { get; set; } = new();
        public List<Currency> Currencies { get; set; } = new();
        public List<double> Percentages { get; set; } = new();
        public List<Measurement> Measurements { get; set; } = new();
        public List<NumericRange> Ranges { get; set; } = new();
        public List<string> Units { get; set; } = new();
        public bool IsSuccess { get; set; }
        public float Confidence { get; set; }
        public string Error { get; set; }
    }

    public class EntityParameters;
    {
        public string ExtractedText { get; set; }
        public DateTime ExtractionTime { get; set; }
        public List<PersonEntity> Persons { get; set; } = new();
        public List<LocationEntity> Locations { get; set; } = new();
        public List<OrganizationEntity> Organizations { get; set; } = new();
        public List<ProductEntity> Products { get; set; } = new();
        public List<EventEntity> Events { get; set; } = new();
        public List<EntityRelationship> Relationships { get; set; } = new();
        public Dictionary<string, List<EntityAttribute>> Attributes { get; set; } = new();
        public bool IsSuccess { get; set; }
        public float Confidence { get; set; }
        public int EntityCount { get; set; }
        public string Error { get; set; }
    }

    public class LocationParameters;
    {
        public string ExtractedText { get; set; }
        public List<string> Addresses { get; set; } = new();
        public List<Coordinates> Coordinates { get; set; } = new();
        public List<Place> Places { get; set; } = new();
        public List<string> RelativeLocations { get; set; } = new();
        public bool IsSuccess { get; set; }
        public float Confidence { get; set; }
        public string Error { get; set; }
    }

    public class ExtractionContext;
    {
        public Dictionary<string, object> Parameters { get; set; } = new();
        public List<ConversationMessage> ConversationHistory { get; set; }
        public SystemContext SystemContext { get; set; }
        public UserContext UserContext { get; set; }
        public Dictionary<string, object> CustomProperties { get; set; } = new();
    }

    public class ParameterSource;
    {
        public string Name { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public float Priority { get; set; } = 1.0f;
        public float Confidence { get; set; } = 1.0f;
        public DateTime Timestamp { get; set; }
    }

    public enum ExtractionSource;
    {
        PatternMatch,
        EntityRecognition,
        SemanticAnalysis,
        SyntacticAnalysis,
        Context,
        FuzzyMatch,
        DefaultValue,
        UserInput;
    }

    public enum ParameterErrorCode;
    {
        MissingRequired,
        TypeMismatch,
        ValueOutOfRange,
        PatternMismatch,
        InvalidEnumValue,
        CustomValidationFailed,
        DependencyMissing,
        ResolutionFailed;
    }

    public enum ParameterWarningCode;
    {
        UnknownParameter,
        LowConfidence,
        AmbiguousValue,
        PossibleTypo,
        UnusualValue;
    }

    public enum ValidationSeverity;
    {
        Success,
        Warning,
        Error;
    }

    public enum DataFormat;
    {
        Json,
        Xml,
        Csv,
        KeyValuePairs,
        Yaml,
        Custom;
    }

    public class ParameterError;
    {
        public string ParameterName { get; set; }
        public ParameterErrorCode ErrorCode { get; set; }
        public string Message { get; set; }
        public bool IsCritical { get; set; } = true;
        public object ExpectedValue { get; set; }
        public object ActualValue { get; set; }
    }

    public class ParameterWarning;
    {
        public string ParameterName { get; set; }
        public ParameterWarningCode WarningCode { get; set; }
        public string Message { get; set; }
        public float Severity { get; set; } = 0.5f;
    }

    // Exception classes;
    public class ParameterExtractionException : Exception
    {
        public ParameterExtractionException(string message) : base(message) { }
        public ParameterExtractionException(string message, Exception inner) : base(message, inner) { }
    }

    public class ParameterValidationException : Exception
    {
        public ParameterValidationException(string message) : base(message) { }
        public ParameterValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ParameterTransformationException : Exception
    {
        public ParameterTransformationException(string message) : base(message) { }
        public ParameterTransformationException(string message, Exception inner) : base(message, inner) { }
    }

    public class StructuredDataExtractionException : Exception
    {
        public StructuredDataExtractionException(string message) : base(message) { }
        public StructuredDataExtractionException(string message, Exception inner) : base(message, inner) { }
    }

    // Additional supporting classes (simplified for brevity)
    public class IntentSchema { }
    public class ParameterDefinition { }
    public class ParameterDataType { }
    public class SyntaxTree { }
    public class SemanticAnalysis { }
    public class ExtractedEntity { }
    public class ConversationMessage { }
    public class SystemContext { }
    public class UserContext { }
    public class DateRange { }
    public class Currency { }
    public class Measurement { }
    public class NumericRange { }
    public class PersonEntity { }
    public class LocationEntity { }
    public class OrganizationEntity { }
    public class ProductEntity { }
    public class EventEntity { }
    public class EntityRelationship { }
    public class EntityAttribute { }
    public class Coordinates { }
    public class Place { }
    public class ExtractionStrategy { }
    public interface IValueTransformer { }
    public interface IValueFormatter { }

    public static class Patterns;
    {
        public const string EmailPattern = @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b";
        public const string PhonePattern = @"\b(\+\d{1,3}[-.]?)?\(?\d{3}\)?[-.]?\d{3}[-.]?\d{4}\b";
        public const string UrlPattern = @"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)";
        public const string IpAddressPattern = @"\b(?:\d{1,3}\.){3}\d{1,3}\b";
        public const string DatePattern = @"\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b|\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]* \d{1,2},? \d{4}\b";
        public const string TimePattern = @"\b\d{1,2}:\d{2}(?::\d{2})?\s*(?:AM|PM)?\b";
        public const string DurationPattern = @"\b\d+\s*(?:hours?|hrs?|minutes?|mins?|seconds?|secs?|days?|weeks?|months?|years?)\b";
        public const string DateRangePattern = @"\b(?:from|between)\s+.+\s+(?:to|and)\s+.+\b";
        public const string RelativeTimePattern = @"\b(?:tomorrow|yesterday|today|now|next week|last week|next month|last month)\b";
        public const string NumberPattern = @"\b\d+(?:\.\d+)?\b";
        public const string CurrencyPattern = @"\$\d+(?:\.\d{2})?|\b\d+(?:\.\d{2})?\s*(?:USD|EUR|GBP|JPY)\b";
        public const string PercentagePattern = @"\b\d+(?:\.\d+)?%\b";
        public const string MeasurementPattern = @"\b\d+(?:\.\d+)?\s*(?:cm|m|km|g|kg|ml|l|px|em|rem|inch|foot|feet|yard|mile)\b";
        public const string NumericRangePattern = @"\b\d+\s*-\s*\d+\b";
        public const string AddressPattern = @"\b\d+\s+[A-Za-z\s]+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr|Lane|Ln)\b";
        public const string CoordinatePattern = @"\b-?\d+(?:\.\d+)?[°\s]*[NS]\s*-?\d+(?:\.\d+)?[°\s]*[EW]\b";
        public const string RelativeLocationPattern = @"\b(?:near|next to|beside|opposite|across from|in front of|behind)\s+.+\b";
    }

    #endregion;
}
