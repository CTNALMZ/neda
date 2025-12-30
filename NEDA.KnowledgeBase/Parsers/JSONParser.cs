using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.Common.Constants;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.ExceptionHandling;
using NEDA.KnowledgeBase.DataManagement;

namespace NEDA.KnowledgeBase.Parsers;
{
    /// <summary>
    /// Advanced JSON parser with schema validation, transformation, querying capabilities,
    /// and comprehensive error handling.
    /// </summary>
    public class JSONParser : IJSONParser, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger<JSONParser> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IDataRepository _dataRepository;
        private readonly JsonSerializerOptions _defaultSerializerOptions;

        private bool _isDisposed;
        private readonly object _parseLock = new object();
        private readonly Dictionary<string, JsonSchema> _schemaCache;
        private readonly SemaphoreSlim _concurrentParsingSemaphore;

        private const int MAX_CONCURRENT_PARSING = 10;
        private const int DEFAULT_MAX_DEPTH = 64;
        private const int DEFAULT_BUFFER_SIZE = 16384; // 16KB;

        private static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles;
        };

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when JSON parsing starts;
        /// </summary>
        public event EventHandler<JsonParseStartedEventArgs> ParseStarted;

        /// <summary>
        /// Event raised when JSON parsing completes;
        /// </summary>
        public event EventHandler<JsonParseCompletedEventArgs> ParseCompleted;

        /// <summary>
        /// Event raised when JSON parsing fails;
        /// </summary>
        public event EventHandler<JsonParseFailedEventArgs> ParseFailed;

        /// <summary>
        /// Event raised when JSON validation is performed;
        /// </summary>
        public event EventHandler<JsonValidationEventArgs> ValidationPerformed;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of JSONParser;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="securityManager">Security manager instance</param>
        /// <param name="dataRepository">Data repository for storing parsed data</param>
        /// <param name="serializerOptions">Custom JSON serializer options</param>
        public JSONParser(
            ILogger<JSONParser> logger,
            ISecurityManager securityManager,
            IDataRepository dataRepository,
            JsonSerializerOptions serializerOptions = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _dataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));

            _defaultSerializerOptions = serializerOptions?.Clone() ?? DefaultOptions.Clone();
            _schemaCache = new Dictionary<string, JsonSchema>(StringComparer.OrdinalIgnoreCase);
            _concurrentParsingSemaphore = new SemaphoreSlim(MAX_CONCURRENT_PARSING, MAX_CONCURRENT_PARSING);

            _logger.LogInformation(LogEvents.JSONParserCreated,
                "JSONParser initialized with default options: CaseInsensitive={CaseInsensitive}, MaxDepth={MaxDepth}",
                _defaultSerializerOptions.PropertyNameCaseInsensitive,
                _defaultSerializerOptions.MaxDepth);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Parses JSON string into specified type;
        /// </summary>
        /// <typeparam name="T">Target type</typeparam>
        /// <param name="json">JSON string</param>
        /// <param name="options">Parsing options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Parsed object</returns>
        public async Task<T> ParseAsync<T>(
            string json,
            JsonParserOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON string cannot be null or empty", nameof(json));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataParse);

            var operationId = Guid.NewGuid().ToString();
            var parserOptions = options ?? new JsonParserOptions();

            _logger.LogDebug(LogEvents.JSONParseStarted,
                "Starting JSON parsing to type {Type}, Operation: {OperationId}, Length: {Length}",
                typeof(T).Name, operationId, json.Length);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                OnParseStarted(new JsonParseStartedEventArgs(
                    operationId,
                    typeof(T),
                    json.Length,
                    parserOptions));

                var result = await ExecuteParseAsync<T>(
                    json,
                    parserOptions,
                    operationId,
                    cancellationToken);

                OnParseCompleted(new JsonParseCompletedEventArgs(
                    operationId,
                    typeof(T),
                    result,
                    TimeSpan.Zero, // Would need timing;
                    true));

                _logger.LogInformation(LogEvents.JSONParseCompleted,
                    "JSON parsing completed: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONParseCancelled,
                    "JSON parsing cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONParseFailed, ex,
                    "JSON parsing failed: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);

                OnParseFailed(new JsonParseFailedEventArgs(
                    operationId,
                    typeof(T),
                    ex,
                    parserOptions));

                throw new JsonParseException(
                    $"Failed to parse JSON to type {typeof(T).Name}",
                    ex,
                    operationId,
                    typeof(T));
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Parses JSON string into dynamic object;
        /// </summary>
        /// <param name="json">JSON string</param>
        /// <param name="options">Parsing options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Dynamic object representing JSON data</returns>
        public async Task<dynamic> ParseDynamicAsync(
            string json,
            JsonParserOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON string cannot be null or empty", nameof(json));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataParse);

            var operationId = Guid.NewGuid().ToString();
            var parserOptions = options ?? new JsonParserOptions();

            _logger.LogDebug(LogEvents.JSONParseStarted,
                "Starting dynamic JSON parsing, Operation: {OperationId}, Length: {Length}",
                operationId, json.Length);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                OnParseStarted(new JsonParseStartedEventArgs(
                    operationId,
                    typeof(ExpandoObject),
                    json.Length,
                    parserOptions));

                var result = await ExecuteParseDynamicAsync(
                    json,
                    parserOptions,
                    operationId,
                    cancellationToken);

                OnParseCompleted(new JsonParseCompletedEventArgs(
                    operationId,
                    typeof(ExpandoObject),
                    result,
                    TimeSpan.Zero,
                    true));

                _logger.LogInformation(LogEvents.JSONParseCompleted,
                    "Dynamic JSON parsing completed: Operation {OperationId}",
                    operationId);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONParseCancelled,
                    "JSON parsing cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONParseFailed, ex,
                    "Dynamic JSON parsing failed: Operation {OperationId}",
                    operationId);

                OnParseFailed(new JsonParseFailedEventArgs(
                    operationId,
                    typeof(ExpandoObject),
                    ex,
                    parserOptions));

                throw new JsonParseException(
                    "Failed to parse JSON to dynamic object",
                    ex,
                    operationId,
                    typeof(ExpandoObject));
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Parses JSON from stream into specified type;
        /// </summary>
        /// <typeparam name="T">Target type</typeparam>
        /// <param name="stream">Stream containing JSON data</param>
        /// <param name="options">Parsing options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Parsed object</returns>
        public async Task<T> ParseFromStreamAsync<T>(
            Stream stream,
            JsonParserOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("Stream must be readable", nameof(stream));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataParse);

            var operationId = Guid.NewGuid().ToString();
            var parserOptions = options ?? new JsonParserOptions();

            _logger.LogDebug(LogEvents.JSONParseStarted,
                "Starting JSON parsing from stream to type {Type}, Operation: {OperationId}",
                typeof(T).Name, operationId);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                OnParseStarted(new JsonParseStartedEventArgs(
                    operationId,
                    typeof(T),
                    (int?)stream.Length,
                    parserOptions));

                var result = await ExecuteParseFromStreamAsync<T>(
                    stream,
                    parserOptions,
                    operationId,
                    cancellationToken);

                OnParseCompleted(new JsonParseCompletedEventArgs(
                    operationId,
                    typeof(T),
                    result,
                    TimeSpan.Zero,
                    true));

                _logger.LogInformation(LogEvents.JSONParseCompleted,
                    "JSON parsing from stream completed: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONParseCancelled,
                    "JSON parsing cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONParseFailed, ex,
                    "JSON parsing from stream failed: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);

                OnParseFailed(new JsonParseFailedEventArgs(
                    operationId,
                    typeof(T),
                    ex,
                    parserOptions));

                throw new JsonParseException(
                    $"Failed to parse JSON from stream to type {typeof(T).Name}",
                    ex,
                    operationId,
                    typeof(T));
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Serializes object to JSON string;
        /// </summary>
        /// <typeparam name="T">Source type</typeparam>
        /// <param name="obj">Object to serialize</param>
        /// <param name="options">Serialization options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>JSON string</returns>
        public async Task<string> SerializeAsync<T>(
            T obj,
            JsonSerializerOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataSerialize);

            var operationId = Guid.NewGuid().ToString();
            var serializerOptions = options ?? _defaultSerializerOptions;

            _logger.LogDebug(LogEvents.JSONSerializeStarted,
                "Starting JSON serialization from type {Type}, Operation: {OperationId}",
                typeof(T).Name, operationId);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                var result = await Task.Run(() =>
                {
                    return JsonSerializer.Serialize(obj, serializerOptions);
                }, cancellationToken);

                _logger.LogInformation(LogEvents.JSONSerializeCompleted,
                    "JSON serialization completed: Operation {OperationId}, Type: {Type}, Length: {Length}",
                    operationId, typeof(T).Name, result.Length);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONSerializeCancelled,
                    "JSON serialization cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONSerializeFailed, ex,
                    "JSON serialization failed: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);

                throw new JsonSerializeException(
                    $"Failed to serialize object of type {typeof(T).Name} to JSON",
                    ex,
                    operationId,
                    typeof(T));
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Validates JSON against schema;
        /// </summary>
        /// <param name="json">JSON string to validate</param>
        /// <param name="schema">JSON schema</param>
        /// <param name="options">Validation options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Validation result</returns>
        public async Task<JsonValidationResult> ValidateAsync(
            string json,
            JsonSchema schema,
            JsonValidationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON string cannot be null or empty", nameof(json));

            if (schema == null)
                throw new ArgumentNullException(nameof(schema));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataValidation);

            var operationId = Guid.NewGuid().ToString();
            var validationOptions = options ?? new JsonValidationOptions();

            _logger.LogDebug(LogEvents.JSONValidationStarted,
                "Starting JSON validation, Operation: {OperationId}, Schema: {SchemaId}",
                operationId, schema.Id);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                var result = await ExecuteValidationAsync(
                    json,
                    schema,
                    validationOptions,
                    operationId,
                    cancellationToken);

                OnValidationPerformed(new JsonValidationEventArgs(
                    operationId,
                    schema.Id,
                    result.IsValid,
                    result.Errors,
                    validationOptions));

                _logger.LogInformation(LogEvents.JSONValidationCompleted,
                    "JSON validation completed: Operation {OperationId}, Valid: {IsValid}, ErrorCount: {ErrorCount}",
                    operationId, result.IsValid, result.Errors?.Count ?? 0);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONValidationCancelled,
                    "JSON validation cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONValidationFailed, ex,
                    "JSON validation failed: Operation {OperationId}, Schema: {SchemaId}",
                    operationId, schema.Id);

                throw new JsonValidationException(
                    $"Failed to validate JSON against schema {schema.Id}",
                    ex,
                    operationId,
                    schema.Id);
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Queries JSON using JSONPath expression;
        /// </summary>
        /// <param name="json">JSON string</param>
        /// <param name="jsonPath">JSONPath expression</param>
        /// <param name="options">Query options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Query results</returns>
        public async Task<JsonQueryResult> QueryAsync(
            string json,
            string jsonPath,
            JsonQueryOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON string cannot be null or empty", nameof(json));

            if (string.IsNullOrWhiteSpace(jsonPath))
                throw new ArgumentException("JSONPath expression cannot be null or empty", nameof(jsonPath));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataQuery);

            var operationId = Guid.NewGuid().ToString();
            var queryOptions = options ?? new JsonQueryOptions();

            _logger.LogDebug(LogEvents.JSONQueryStarted,
                "Starting JSON query, Operation: {OperationId}, Path: {JsonPath}",
                operationId, jsonPath);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                var result = await ExecuteQueryAsync(
                    json,
                    jsonPath,
                    queryOptions,
                    operationId,
                    cancellationToken);

                _logger.LogInformation(LogEvents.JSONQueryCompleted,
                    "JSON query completed: Operation {OperationId}, Path: {JsonPath}, ResultCount: {ResultCount}",
                    operationId, jsonPath, result.Results?.Count ?? 0);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONQueryCancelled,
                    "JSON query cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONQueryFailed, ex,
                    "JSON query failed: Operation {OperationId}, Path: {JsonPath}",
                    operationId, jsonPath);

                throw new JsonQueryException(
                    $"Failed to query JSON with path {jsonPath}",
                    ex,
                    operationId,
                    jsonPath);
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Transforms JSON using transformation rules;
        /// </summary>
        /// <param name="json">Input JSON</param>
        /// <param name="transformation">Transformation rules</param>
        /// <param name="options">Transformation options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Transformed JSON</returns>
        public async Task<string> TransformAsync(
            string json,
            JsonTransformation transformation,
            JsonTransformOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON string cannot be null or empty", nameof(json));

            if (transformation == null)
                throw new ArgumentNullException(nameof(transformation));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataTransform);

            var operationId = Guid.NewGuid().ToString();
            var transformOptions = options ?? new JsonTransformOptions();

            _logger.LogDebug(LogEvents.JSONTransformStarted,
                "Starting JSON transformation, Operation: {OperationId}, Transformation: {TransformationId}",
                operationId, transformation.Id);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                var result = await ExecuteTransformationAsync(
                    json,
                    transformation,
                    transformOptions,
                    operationId,
                    cancellationToken);

                _logger.LogInformation(LogEvents.JSONTransformCompleted,
                    "JSON transformation completed: Operation {OperationId}, Transformation: {TransformationId}",
                    operationId, transformation.Id);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONTransformCancelled,
                    "JSON transformation cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONTransformFailed, ex,
                    "JSON transformation failed: Operation {OperationId}, Transformation: {TransformationId}",
                    operationId, transformation.Id);

                throw new JsonTransformException(
                    $"Failed to transform JSON with transformation {transformation.Id}",
                    ex,
                    operationId,
                    transformation.Id);
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Merges multiple JSON documents;
        /// </summary>
        /// <param name="jsonDocuments">JSON documents to merge</param>
        /// <param name="mergeStrategy">Merge strategy</param>
        /// <param name="options">Merge options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Merged JSON</returns>
        public async Task<string> MergeAsync(
            IEnumerable<string> jsonDocuments,
            JsonMergeStrategy mergeStrategy,
            JsonMergeOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (jsonDocuments == null)
                throw new ArgumentNullException(nameof(jsonDocuments));

            var documents = jsonDocuments.ToList();
            if (!documents.Any())
                throw new ArgumentException("At least one JSON document is required", nameof(jsonDocuments));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataMerge);

            var operationId = Guid.NewGuid().ToString();
            var mergeOptions = options ?? new JsonMergeOptions();

            _logger.LogDebug(LogEvents.JSONMergeStarted,
                "Starting JSON merge, Operation: {OperationId}, DocumentCount: {DocumentCount}, Strategy: {Strategy}",
                operationId, documents.Count, mergeStrategy);

            try
            {
                await _concurrentParsingSemaphore.WaitAsync(cancellationToken);

                var result = await ExecuteMergeAsync(
                    documents,
                    mergeStrategy,
                    mergeOptions,
                    operationId,
                    cancellationToken);

                _logger.LogInformation(LogEvents.JSONMergeCompleted,
                    "JSON merge completed: Operation {OperationId}, DocumentCount: {DocumentCount}",
                    operationId, documents.Count);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.JSONMergeCancelled,
                    "JSON merge cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONMergeFailed, ex,
                    "JSON merge failed: Operation {OperationId}, DocumentCount: {DocumentCount}",
                    operationId, documents.Count);

                throw new JsonMergeException(
                    $"Failed to merge {documents.Count} JSON documents",
                    ex,
                    operationId,
                    documents.Count);
            }
            finally
            {
                _concurrentParsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Creates JSON schema from type;
        /// </summary>
        /// <typeparam name="T">Type to generate schema from</typeparam>
        /// <param name="options">Schema generation options</param>
        /// <returns>JSON schema</returns>
        public JsonSchema GenerateSchema<T>(JsonSchemaOptions options = null)
        {
            var type = typeof(T);
            var schemaOptions = options ?? new JsonSchemaOptions();

            _logger.LogDebug(LogEvents.JSONSchemaGenerationStarted,
                "Starting JSON schema generation for type {Type}", type.Name);

            try
            {
                var schema = GenerateSchemaFromType(type, schemaOptions);

                _logger.LogInformation(LogEvents.JSONSchemaGenerationCompleted,
                    "JSON schema generation completed for type {Type}, Properties: {PropertyCount}",
                    type.Name, schema.Properties?.Count ?? 0);

                return schema;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.JSONSchemaGenerationFailed, ex,
                    "JSON schema generation failed for type {Type}", type.Name);

                throw new JsonSchemaException(
                    $"Failed to generate JSON schema for type {type.Name}",
                    ex,
                    type);
            }
        }

        #endregion;

        #region Private Methods;

        private async Task<T> ExecuteParseAsync<T>(
            string json,
            JsonParserOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Pre-process JSON if needed;
                var processedJson = await PreProcessJsonAsync(json, options);

                // Validate if required;
                if (options.ValidateBeforeParse && options.Schema != null)
                {
                    var validationResult = await ExecuteValidationAsync(
                        processedJson,
                        options.Schema,
                        new JsonValidationOptions(),
                        operationId,
                        cancellationToken);

                    if (!validationResult.IsValid)
                    {
                        throw new JsonValidationException(
                            "JSON validation failed before parsing",
                            null,
                            operationId,
                            options.Schema.Id,
                            validationResult.Errors);
                    }
                }

                // Configure serializer options;
                var serializerOptions = ConfigureSerializerOptions(options);

                // Parse JSON;
                var result = await Task.Run(() =>
                {
                    return JsonSerializer.Deserialize<T>(processedJson, serializerOptions);
                }, cancellationToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Store parsed data if requested;
                if (options.StoreParsedData && result != null)
                {
                    await StoreParsedDataAsync(operationId, json, result, duration);
                }

                return result;
            }
            catch (JsonException ex)
            {
                throw new JsonParseException(
                    $"JSON parsing error at position {ex.BytePositionInLine}:{ex.LineNumber}",
                    ex,
                    operationId,
                    typeof(T));
            }
        }

        private async Task<dynamic> ExecuteParseDynamicAsync(
            string json,
            JsonParserOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Parse to JsonDocument for dynamic access;
                using (var document = await Task.Run(() =>
                {
                    return JsonDocument.Parse(json, new JsonDocumentOptions;
                    {
                        AllowTrailingCommas = options.AllowTrailingCommas,
                        CommentHandling = options.CommentHandling,
                        MaxDepth = options.MaxDepth ?? DEFAULT_MAX_DEPTH;
                    });
                }, cancellationToken))
                {
                    var result = ConvertJsonElementToDynamic(document.RootElement);

                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    // Store parsed data if requested;
                    if (options.StoreParsedData)
                    {
                        await StoreParsedDataAsync(operationId, json, result, duration);
                    }

                    return result;
                }
            }
            catch (JsonException ex)
            {
                throw new JsonParseException(
                    $"JSON parsing error at position {ex.BytePositionInLine}:{ex.LineNumber}",
                    ex,
                    operationId,
                    typeof(ExpandoObject));
            }
        }

        private async Task<T> ExecuteParseFromStreamAsync<T>(
            Stream stream,
            JsonParserOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Configure serializer options;
                var serializerOptions = ConfigureSerializerOptions(options);

                // Read from stream;
                var result = await JsonSerializer.DeserializeAsync<T>(
                    stream,
                    serializerOptions,
                    cancellationToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Store parsed data if requested;
                if (options.StoreParsedData && result != null)
                {
                    await StoreParsedDataAsync(operationId, "Stream", result, duration);
                }

                return result;
            }
            catch (JsonException ex)
            {
                throw new JsonParseException(
                    $"JSON parsing error from stream",
                    ex,
                    operationId,
                    typeof(T));
            }
        }

        private async Task<JsonValidationResult> ExecuteValidationAsync(
            string json,
            JsonSchema schema,
            JsonValidationOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Parse JSON to validate structure;
                using (var document = JsonDocument.Parse(json, new JsonDocumentOptions;
                {
                    MaxDepth = options.MaxDepth ?? DEFAULT_MAX_DEPTH;
                }))
                {
                    var validationErrors = new List<JsonValidationError>();

                    // Perform validation based on schema;
                    await ValidateJsonDocumentAsync(
                        document.RootElement,
                        schema,
                        "$",
                        validationErrors,
                        options);

                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    return new JsonValidationResult;
                    {
                        IsValid = validationErrors.Count == 0,
                        Errors = validationErrors,
                        ValidationTime = duration,
                        SchemaId = schema.Id,
                        OperationId = operationId;
                    };
                }
            }
            catch (JsonException ex)
            {
                // If we can't even parse the JSON, it's invalid;
                return new JsonValidationResult;
                {
                    IsValid = false,
                    Errors = new List<JsonValidationError>
                    {
                        new JsonValidationError;
                        {
                            Path = "$",
                            Message = $"Invalid JSON: {ex.Message}",
                            ErrorCode = "INVALID_JSON",
                            Severity = ValidationErrorSeverity.Error;
                        }
                    },
                    ValidationTime = TimeSpan.Zero,
                    SchemaId = schema.Id,
                    OperationId = operationId;
                };
            }
        }

        private async Task<JsonQueryResult> ExecuteQueryAsync(
            string json,
            string jsonPath,
            JsonQueryOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                using (var document = await Task.Run(() =>
                {
                    return JsonDocument.Parse(json);
                }, cancellationToken))
                {
                    var results = new List<JsonQueryMatch>();
                    var pathEvaluator = new JsonPathEvaluator(options);

                    // Evaluate JSONPath expression;
                    await EvaluateJsonPathAsync(
                        document.RootElement,
                        jsonPath,
                        "$",
                        results,
                        pathEvaluator,
                        cancellationToken);

                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    return new JsonQueryResult;
                    {
                        Success = true,
                        Results = results,
                        QueryTime = duration,
                        JsonPath = jsonPath,
                        MatchCount = results.Count,
                        OperationId = operationId;
                    };
                }
            }
            catch (JsonException ex)
            {
                throw new JsonQueryException(
                    $"Invalid JSON for query",
                    ex,
                    operationId,
                    jsonPath);
            }
        }

        private async Task<string> ExecuteTransformationAsync(
            string json,
            JsonTransformation transformation,
            JsonTransformOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                using (var document = await Task.Run(() =>
                {
                    return JsonDocument.Parse(json);
                }, cancellationToken))
                {
                    // Apply transformations;
                    var transformedElement = await ApplyTransformationsAsync(
                        document.RootElement,
                        transformation.Rules,
                        options,
                        cancellationToken);

                    // Serialize transformed element;
                    var result = JsonSerializer.Serialize(transformedElement, _defaultSerializerOptions);

                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    return result;
                }
            }
            catch (JsonException ex)
            {
                throw new JsonTransformException(
                    $"Invalid JSON for transformation",
                    ex,
                    operationId,
                    transformation.Id);
            }
        }

        private async Task<string> ExecuteMergeAsync(
            List<string> jsonDocuments,
            JsonMergeStrategy mergeStrategy,
            JsonMergeOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Parse all documents;
                var documents = new List<JsonDocument>();
                try
                {
                    foreach (var json in jsonDocuments)
                    {
                        var document = await Task.Run(() =>
                        {
                            return JsonDocument.Parse(json);
                        }, cancellationToken);
                        documents.Add(document);
                    }

                    // Merge documents according to strategy;
                    var mergedElement = MergeJsonDocuments(documents, mergeStrategy, options);

                    // Serialize merged result;
                    var result = JsonSerializer.Serialize(mergedElement, _defaultSerializerOptions);

                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    return result;
                }
                finally
                {
                    // Clean up documents;
                    foreach (var document in documents)
                    {
                        document.Dispose();
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new JsonMergeException(
                    $"Invalid JSON document in merge operation",
                    ex,
                    operationId,
                    jsonDocuments.Count);
            }
        }

        private async Task<string> PreProcessJsonAsync(string json, JsonParserOptions options)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            var processedJson = json;

            // Remove BOM if present;
            if (processedJson.StartsWith("\uFEFF", StringComparison.Ordinal))
            {
                processedJson = processedJson.Substring(1);
            }

            // Trim if requested;
            if (options.TrimWhitespace)
            {
                processedJson = processedJson.Trim();
            }

            // Fix common JSON issues if requested;
            if (options.FixCommonErrors)
            {
                processedJson = await FixCommonJsonErrorsAsync(processedJson);
            }

            return processedJson;
        }

        private async Task<string> FixCommonJsonErrorsAsync(string json)
        {
            return await Task.Run(() =>
            {
                var result = new StringBuilder(json);

                // Fix trailing commas;
                if (result.ToString().Contains(",]"))
                {
                    result.Replace(",]", "]");
                }
                if (result.ToString().Contains(",}"))
                {
                    result.Replace(",}", "}");
                }

                // Fix missing quotes on property names;
                // This is a simplified fix - real implementation would need a proper parser;
                var regex = new System.Text.RegularExpressions.Regex(
                    @"(\{|\,\s*)([a-zA-Z_][a-zA-Z0-9_]*)\s*:",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                result = new StringBuilder(regex.Replace(result.ToString(), "$1\"$2\":"));

                return result.ToString();
            });
        }

        private JsonSerializerOptions ConfigureSerializerOptions(JsonParserOptions options)
        {
            var serializerOptions = _defaultSerializerOptions.Clone();

            if (options.PropertyNameCaseInsensitive.HasValue)
            {
                serializerOptions.PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive.Value;
            }

            if (options.WriteIndented.HasValue)
            {
                serializerOptions.WriteIndented = options.WriteIndented.Value;
            }

            if (options.MaxDepth.HasValue)
            {
                serializerOptions.MaxDepth = options.MaxDepth.Value;
            }

            if (options.Converters != null && options.Converters.Any())
            {
                foreach (var converter in options.Converters)
                {
                    serializerOptions.Converters.Add(converter);
                }
            }

            return serializerOptions;
        }

        private async Task StoreParsedDataAsync<T>(
            string operationId,
            string source,
            T data,
            TimeSpan duration)
        {
            try
            {
                await _dataRepository.SaveParsedDataAsync(new ParsedDataRecord<T>
                {
                    OperationId = operationId,
                    Source = source,
                    Data = data,
                    DataType = typeof(T).FullName,
                    ParseTime = duration,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.JSONStorageFailed, ex,
                    "Failed to store parsed data: Operation {OperationId}",
                    operationId);
            }
        }

        private dynamic ConvertJsonElementToDynamic(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var expando = new ExpandoObject();
                    var dict = expando as IDictionary<string, object>;

                    foreach (var property in element.EnumerateObject())
                    {
                        dict[property.Name] = ConvertJsonElementToDynamic(property.Value);
                    }
                    return expando;

                case JsonValueKind.Array:
                    var list = new List<dynamic>();
                    foreach (var item in element.EnumerateArray())
                    {
                        list.Add(ConvertJsonElementToDynamic(item));
                    }
                    return list;

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                        return intValue;
                    if (element.TryGetInt64(out long longValue))
                        return longValue;
                    if (element.TryGetDecimal(out decimal decimalValue))
                        return decimalValue;
                    if (element.TryGetDouble(out double doubleValue))
                        return doubleValue;
                    return element.GetRawText();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                    return null;

                default:
                    return null;
            }
        }

        private async Task ValidateJsonDocumentAsync(
            JsonElement element,
            JsonSchema schema,
            string path,
            List<JsonValidationError> errors,
            JsonValidationOptions options)
        {
            await Task.Run(() =>
            {
                // Basic type validation;
                if (schema.Type.HasValue)
                {
                    var expectedType = schema.Type.Value;
                    var actualType = GetJsonValueKindType(element.ValueKind);

                    if (expectedType != actualType)
                    {
                        errors.Add(new JsonValidationError;
                        {
                            Path = path,
                            Message = $"Expected type {expectedType}, found {actualType}",
                            ErrorCode = "TYPE_MISMATCH",
                            Severity = ValidationErrorSeverity.Error;
                        });
                        return;
                    }
                }

                // Object validation;
                if (element.ValueKind == JsonValueKind.Object && schema.Properties != null)
                {
                    ValidateJsonObject(element, schema, path, errors, options);
                }

                // Array validation;
                else if (element.ValueKind == JsonValueKind.Array && schema.Items != null)
                {
                    ValidateJsonArray(element, schema, path, errors, options);
                }

                // String validation;
                else if (element.ValueKind == JsonValueKind.String && schema.StringConstraints != null)
                {
                    ValidateJsonString(element, schema, path, errors);
                }

                // Number validation;
                else if ((element.ValueKind == JsonValueKind.Number ||
                         element.ValueKind == JsonValueKind.String) && schema.NumberConstraints != null)
                {
                    ValidateJsonNumber(element, schema, path, errors);
                }
            });
        }

        private void ValidateJsonObject(
            JsonElement element,
            JsonSchema schema,
            string path,
            List<JsonValidationError> errors,
            JsonValidationOptions options)
        {
            var properties = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

            // Check required properties;
            if (schema.RequiredProperties != null)
            {
                foreach (var requiredProp in schema.RequiredProperties)
                {
                    if (!properties.ContainsKey(requiredProp))
                    {
                        errors.Add(new JsonValidationError;
                        {
                            Path = path,
                            Message = $"Missing required property: {requiredProp}",
                            ErrorCode = "MISSING_REQUIRED_PROPERTY",
                            Severity = ValidationErrorSeverity.Error;
                        });
                    }
                }
            }

            // Validate each property;
            foreach (var property in properties)
            {
                var propertyPath = $"{path}.{property.Key}";

                if (schema.Properties.TryGetValue(property.Key, out var propertySchema))
                {
                    // Recursive validation;
                    ValidateJsonDocumentAsync(
                        property.Value,
                        propertySchema,
                        propertyPath,
                        errors,
                        options).Wait();
                }
                else if (!schema.AllowAdditionalProperties)
                {
                    errors.Add(new JsonValidationError;
                    {
                        Path = propertyPath,
                        Message = $"Additional property not allowed: {property.Key}",
                        ErrorCode = "ADDITIONAL_PROPERTY",
                        Severity = options.Strict ? ValidationErrorSeverity.Error : ValidationErrorSeverity.Warning;
                    });
                }
            }
        }

        private void ValidateJsonArray(
            JsonElement element,
            JsonSchema schema,
            string path,
            List<JsonValidationError> errors,
            JsonValidationOptions options)
        {
            var items = element.EnumerateArray().ToList();

            // Check min/max items;
            if (schema.MinItems.HasValue && items.Count < schema.MinItems.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Array must have at least {schema.MinItems} items, found {items.Count}",
                    ErrorCode = "MIN_ITEMS",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (schema.MaxItems.HasValue && items.Count > schema.MaxItems.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Array must have at most {schema.MaxItems} items, found {items.Count}",
                    ErrorCode = "MAX_ITEMS",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            // Validate each item;
            for (int i = 0; i < items.Count; i++)
            {
                var itemPath = $"{path}[{i}]";
                ValidateJsonDocumentAsync(
                    items[i],
                    schema.Items,
                    itemPath,
                    errors,
                    options).Wait();
            }
        }

        private void ValidateJsonString(
            JsonElement element,
            JsonSchema schema,
            string path,
            List<JsonValidationError> errors)
        {
            var value = element.GetString();
            var constraints = schema.StringConstraints;

            if (constraints.MinLength.HasValue && value.Length < constraints.MinLength.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"String must be at least {constraints.MinLength} characters, found {value.Length}",
                    ErrorCode = "MIN_LENGTH",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (constraints.MaxLength.HasValue && value.Length > constraints.MaxLength.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"String must be at most {constraints.MaxLength} characters, found {value.Length}",
                    ErrorCode = "MAX_LENGTH",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (!string.IsNullOrEmpty(constraints.Pattern))
            {
                var regex = new System.Text.RegularExpressions.Regex(constraints.Pattern);
                if (!regex.IsMatch(value))
                {
                    errors.Add(new JsonValidationError;
                    {
                        Path = path,
                        Message = $"String does not match pattern: {constraints.Pattern}",
                        ErrorCode = "PATTERN",
                        Severity = ValidationErrorSeverity.Error;
                    });
                }
            }

            if (constraints.EnumValues != null && constraints.EnumValues.Any())
            {
                if (!constraints.EnumValues.Contains(value))
                {
                    errors.Add(new JsonValidationError;
                    {
                        Path = path,
                        Message = $"String must be one of: {string.Join(", ", constraints.EnumValues)}",
                        ErrorCode = "ENUM",
                        Severity = ValidationErrorSeverity.Error;
                    });
                }
            }
        }

        private void ValidateJsonNumber(
            JsonElement element,
            JsonSchema schema,
            string path,
            List<JsonValidationError> errors)
        {
            decimal value;
            if (element.ValueKind == JsonValueKind.Number)
            {
                value = element.GetDecimal();
            }
            else if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), out decimal parsedValue))
            {
                value = parsedValue;
            }
            else;
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = "Value is not a valid number",
                    ErrorCode = "INVALID_NUMBER",
                    Severity = ValidationErrorSeverity.Error;
                });
                return;
            }

            var constraints = schema.NumberConstraints;

            if (constraints.Minimum.HasValue && value < constraints.Minimum.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Number must be at least {constraints.Minimum}, found {value}",
                    ErrorCode = "MINIMUM",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (constraints.Maximum.HasValue && value > constraints.Maximum.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Number must be at most {constraints.Maximum}, found {value}",
                    ErrorCode = "MAXIMUM",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (constraints.ExclusiveMinimum.HasValue && value <= constraints.ExclusiveMinimum.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Number must be greater than {constraints.ExclusiveMinimum}, found {value}",
                    ErrorCode = "EXCLUSIVE_MINIMUM",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (constraints.ExclusiveMaximum.HasValue && value >= constraints.ExclusiveMaximum.Value)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Number must be less than {constraints.ExclusiveMaximum}, found {value}",
                    ErrorCode = "EXCLUSIVE_MAXIMUM",
                    Severity = ValidationErrorSeverity.Error;
                });
            }

            if (constraints.MultipleOf.HasValue && value % constraints.MultipleOf.Value != 0)
            {
                errors.Add(new JsonValidationError;
                {
                    Path = path,
                    Message = $"Number must be a multiple of {constraints.MultipleOf}, found {value}",
                    ErrorCode = "MULTIPLE_OF",
                    Severity = ValidationErrorSeverity.Error;
                });
            }
        }

        private async Task EvaluateJsonPathAsync(
            JsonElement element,
            string jsonPath,
            string currentPath,
            List<JsonQueryMatch> results,
            JsonPathEvaluator evaluator,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Simplified JSONPath evaluation;
                // In a real implementation, this would use a proper JSONPath parser;

                // Handle root selector;
                if (jsonPath == "$" || jsonPath == "$.*")
                {
                    results.Add(new JsonQueryMatch;
                    {
                        Path = currentPath,
                        Value = element,
                        ValueType = element.ValueKind;
                    });
                    return;
                }

                // Handle child selector (simplified)
                if (jsonPath.StartsWith("$."))
                {
                    var propertyName = jsonPath.Substring(2);

                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        if (element.TryGetProperty(propertyName, out var propertyValue))
                        {
                            results.Add(new JsonQueryMatch;
                            {
                                Path = $"{currentPath}.{propertyName}",
                                Value = propertyValue,
                                ValueType = propertyValue.ValueKind;
                            });
                        }
                    }
                }

                // Handle array index selector (simplified)
                if (jsonPath.Contains("[") && jsonPath.Contains("]"))
                {
                    // Extract index;
                    var startIndex = jsonPath.IndexOf('[');
                    var endIndex = jsonPath.IndexOf(']');
                    var indexText = jsonPath.Substring(startIndex + 1, endIndex - startIndex - 1);

                    if (int.TryParse(indexText, out int index) &&
                        element.ValueKind == JsonValueKind.Array)
                    {
                        var array = element.EnumerateArray().ToList();
                        if (index >= 0 && index < array.Count)
                        {
                            results.Add(new JsonQueryMatch;
                            {
                                Path = $"{currentPath}[{index}]",
                                Value = array[index],
                                ValueType = array[index].ValueKind;
                            });
                        }
                    }
                }
            }, cancellationToken);
        }

        private async Task<JsonElement> ApplyTransformationsAsync(
            JsonElement element,
            List<TransformationRule> rules,
            JsonTransformOptions options,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Clone the element (simplified - real implementation would need to rebuild)
                using (var stream = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        WriteTransformedElement(writer, element, rules, options);
                    }

                    stream.Position = 0;
                    using (var document = JsonDocument.Parse(stream))
                    {
                        return document.RootElement.Clone();
                    }
                }
            }, cancellationToken);
        }

        private void WriteTransformedElement(
            Utf8JsonWriter writer,
            JsonElement element,
            List<TransformationRule> rules,
            JsonTransformOptions options)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (var property in element.EnumerateObject())
                    {
                        // Check if property should be transformed;
                        var transformationRule = rules.FirstOrDefault(r =>
                            r.SourcePath == property.Name ||
                            r.SourcePath == "$." + property.Name);

                        if (transformationRule != null)
                        {
                            // Apply transformation;
                            writer.WritePropertyName(transformationRule.TargetPath ?? property.Name);

                            if (transformationRule.TransformationType == TransformationType.Rename)
                            {
                                // Just write the value;
                                WriteJsonElement(writer, property.Value);
                            }
                            else if (transformationRule.TransformationType == TransformationType.Convert)
                            {
                                // Apply conversion;
                                ApplyConversion(writer, property.Value, transformationRule);
                            }
                        }
                        else;
                        {
                            // Write property as-is;
                            writer.WritePropertyName(property.Name);
                            WriteJsonElement(writer, property.Value);
                        }
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    foreach (var item in element.EnumerateArray())
                    {
                        WriteJsonElement(writer, item);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    WriteJsonElement(writer, element);
                    break;
            }
        }

        private void WriteJsonElement(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteJsonElement(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteJsonElement(writer, item);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;

                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intValue))
                        writer.WriteNumberValue(intValue);
                    else if (element.TryGetInt64(out long longValue))
                        writer.WriteNumberValue(longValue);
                    else if (element.TryGetDecimal(out decimal decimalValue))
                        writer.WriteNumberValue(decimalValue);
                    else if (element.TryGetDouble(out double doubleValue))
                        writer.WriteNumberValue(doubleValue);
                    else;
                        writer.WriteNumberValue(element.GetRawText());
                    break;

                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;

                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;

                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
            }
        }

        private void ApplyConversion(
            Utf8JsonWriter writer,
            JsonElement element,
            TransformationRule rule)
        {
            switch (rule.TargetType)
            {
                case "string":
                    writer.WriteStringValue(element.ToString());
                    break;

                case "number":
                    if (element.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(element.GetString(), out decimal decimalValue))
                    {
                        writer.WriteNumberValue(decimalValue);
                    }
                    else if (element.ValueKind == JsonValueKind.Number)
                    {
                        WriteJsonElement(writer, element);
                    }
                    else;
                    {
                        writer.WriteNullValue();
                    }
                    break;

                case "boolean":
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var stringValue = element.GetString().ToLower();
                        writer.WriteBooleanValue(stringValue == "true" || stringValue == "1" || stringValue == "yes");
                    }
                    else if (element.ValueKind == JsonValueKind.Number)
                    {
                        writer.WriteBooleanValue(element.GetDecimal() != 0);
                    }
                    else;
                    {
                        writer.WriteBooleanValue(element.ValueKind == JsonValueKind.True);
                    }
                    break;

                default:
                    WriteJsonElement(writer, element);
                    break;
            }
        }

        private JsonElement MergeJsonDocuments(
            List<JsonDocument> documents,
            JsonMergeStrategy strategy,
            JsonMergeOptions options)
        {
            if (!documents.Any())
                throw new ArgumentException("No documents to merge");

            // Start with first document;
            var resultDoc = documents[0];

            // Merge remaining documents;
            for (int i = 1; i < documents.Count; i++)
            {
                resultDoc = MergeTwoDocuments(resultDoc, documents[i], strategy, options);
            }

            return resultDoc.RootElement.Clone();
        }

        private JsonDocument MergeTwoDocuments(
            JsonDocument doc1,
            JsonDocument doc2,
            JsonMergeStrategy strategy,
            JsonMergeOptions options)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    MergeJsonElements(writer, doc1.RootElement, doc2.RootElement, strategy, options);
                }

                stream.Position = 0;
                return JsonDocument.Parse(stream);
            }
        }

        private void MergeJsonElements(
            Utf8JsonWriter writer,
            JsonElement element1,
            JsonElement element2,
            JsonMergeStrategy strategy,
            JsonMergeOptions options)
        {
            if (element1.ValueKind != element2.ValueKind)
            {
                // Type mismatch - use strategy;
                if (strategy == JsonMergeStrategy.PreferFirst)
                {
                    WriteJsonElement(writer, element1);
                }
                else;
                {
                    WriteJsonElement(writer, element2);
                }
                return;
            }

            switch (element1.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    // Get all property names;
                    var properties1 = element1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                    var properties2 = element2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                    // Combine property names;
                    var allPropertyNames = properties1.Keys.Union(properties2.Keys).Distinct();

                    foreach (var propertyName in allPropertyNames)
                    {
                        writer.WritePropertyName(propertyName);

                        if (properties1.TryGetValue(propertyName, out var value1) &&
                            properties2.TryGetValue(propertyName, out var value2))
                        {
                            // Property exists in both - merge recursively;
                            MergeJsonElements(writer, value1, value2, strategy, options);
                        }
                        else if (properties1.TryGetValue(propertyName, out value1))
                        {
                            // Property only in first;
                            WriteJsonElement(writer, value1);
                        }
                        else;
                        {
                            // Property only in second;
                            WriteJsonElement(writer, properties2[propertyName]);
                        }
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    // Merge arrays based on strategy;
                    if (strategy == JsonMergeStrategy.Concat)
                    {
                        // Concatenate arrays;
                        foreach (var item in element1.EnumerateArray())
                        {
                            WriteJsonElement(writer, item);
                        }
                        foreach (var item in element2.EnumerateArray())
                        {
                            WriteJsonElement(writer, item);
                        }
                    }
                    else if (strategy == JsonMergeStrategy.Union)
                    {
                        // Union of arrays (remove duplicates)
                        var items = new HashSet<string>();

                        foreach (var item in element1.EnumerateArray())
                        {
                            var itemJson = item.ToString();
                            if (items.Add(itemJson))
                            {
                                WriteJsonElement(writer, item);
                            }
                        }

                        foreach (var item in element2.EnumerateArray())
                        {
                            var itemJson = item.ToString();
                            if (items.Add(itemJson))
                            {
                                WriteJsonElement(writer, item);
                            }
                        }
                    }
                    else;
                    {
                        // Prefer first or second;
                        WriteJsonElement(writer, strategy == JsonMergeStrategy.PreferFirst ? element1 : element2);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    // For primitive values, use strategy;
                    if (strategy == JsonMergeStrategy.PreferFirst)
                    {
                        WriteJsonElement(writer, element1);
                    }
                    else;
                    {
                        WriteJsonElement(writer, element2);
                    }
                    break;
            }
        }

        private JsonSchema GenerateSchemaFromType(Type type, JsonSchemaOptions options)
        {
            var schema = new JsonSchema;
            {
                Id = $"{type.Namespace}.{type.Name}",
                Title = type.Name,
                Description = type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description,
                Type = GetTypeJsonType(type),
                Properties = new Dictionary<string, JsonSchema>(),
                RequiredProperties = new List<string>()
            };

            // Get properties;
            var properties = type.GetProperties(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly);

            foreach (var property in properties)
            {
                // Skip if marked with JsonIgnore;
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                    continue;

                var propertySchema = new JsonSchema;
                {
                    Id = $"{schema.Id}.{property.Name}",
                    Title = property.Name,
                    Description = property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description,
                    Type = GetTypeJsonType(property.PropertyType)
                };

                // Add string constraints for string properties;
                if (property.PropertyType == typeof(string))
                {
                    propertySchema.StringConstraints = new StringConstraints();

                    // Check for StringLength attribute;
                    var stringLengthAttr = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.StringLengthAttribute>();
                    if (stringLengthAttr != null)
                    {
                        propertySchema.StringConstraints.MaxLength = stringLengthAttr.MaximumLength;
                        if (stringLengthAttr.MinimumLength > 0)
                        {
                            propertySchema.StringConstraints.MinLength = stringLengthAttr.MinimumLength;
                        }
                    }

                    // Check for RegularExpression attribute;
                    var regexAttr = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RegularExpressionAttribute>();
                    if (regexAttr != null)
                    {
                        propertySchema.StringConstraints.Pattern = regexAttr.Pattern;
                    }
                }

                // Add number constraints for numeric properties;
                else if (IsNumericType(property.PropertyType))
                {
                    propertySchema.NumberConstraints = new NumberConstraints();

                    // Check for Range attribute;
                    var rangeAttr = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RangeAttribute>();
                    if (rangeAttr != null)
                    {
                        if (decimal.TryParse(rangeAttr.Minimum.ToString(), out decimal min))
                            propertySchema.NumberConstraints.Minimum = min;
                        if (decimal.TryParse(rangeAttr.Maximum.ToString(), out decimal max))
                            propertySchema.NumberConstraints.Maximum = max;
                    }
                }

                schema.Properties[GetPropertyName(property, options)] = propertySchema;

                // Check if required;
                if (property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() != null ||
                    (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) != null))
                {
                    schema.RequiredProperties.Add(property.Name);
                }
            }

            return schema;
        }

        private JsonType GetTypeJsonType(Type type)
        {
            if (type == typeof(string))
                return JsonType.String;
            if (type == typeof(bool) || type == typeof(bool?))
                return JsonType.Boolean;
            if (IsNumericType(type))
                return JsonType.Number;
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
                return JsonType.Array;
            if (type.IsClass && type != typeof(string))
                return JsonType.Object;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return JsonType.String; // ISO date string;

            return JsonType.Object; // Default;
        }

        private bool IsNumericType(Type type)
        {
            var numericTypes = new[]
            {
                typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
                typeof(int), typeof(uint), typeof(long), typeof(ulong),
                typeof(float), typeof(double), typeof(decimal),
                typeof(byte?), typeof(sbyte?), typeof(short?), typeof(ushort?),
                typeof(int?), typeof(uint?), typeof(long?), typeof(ulong?),
                typeof(float?), typeof(double?), typeof(decimal?)
            };

            return numericTypes.Contains(type);
        }

        private JsonType GetJsonValueKindType(JsonValueKind valueKind)
        {
            return valueKind switch;
            {
                JsonValueKind.Object => JsonType.Object,
                JsonValueKind.Array => JsonType.Array,
                JsonValueKind.String => JsonType.String,
                JsonValueKind.Number => JsonType.Number,
                JsonValueKind.True or JsonValueKind.False => JsonType.Boolean,
                JsonValueKind.Null => JsonType.Null,
                _ => JsonType.Null;
            };
        }

        private string GetPropertyName(PropertyInfo property, JsonSchemaOptions options)
        {
            // Check for JsonPropertyName attribute;
            var jsonPropertyAttr = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (jsonPropertyAttr != null)
                return jsonPropertyAttr.Name;

            // Apply naming policy;
            if (options.NamingPolicy == JsonNamingPolicy.CamelCase)
                return char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
            if (options.NamingPolicy == JsonNamingPolicy.SnakeCaseLower)
                return string.Concat(property.Name.Select((c, i) =>
                    i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));

            return property.Name;
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnParseStarted(JsonParseStartedEventArgs e)
        {
            ParseStarted?.Invoke(this, e);
        }

        protected virtual void OnParseCompleted(JsonParseCompletedEventArgs e)
        {
            ParseCompleted?.Invoke(this, e);
        }

        protected virtual void OnParseFailed(JsonParseFailedEventArgs e)
        {
            ParseFailed?.Invoke(this, e);
        }

        protected virtual void OnValidationPerformed(JsonValidationEventArgs e)
        {
            ValidationPerformed?.Invoke(this, e);
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
                    _concurrentParsingSemaphore?.Dispose();
                    _schemaCache.Clear();
                }

                _isDisposed = true;
            }
        }

        ~JSONParser()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes;

        private class JsonPathEvaluator;
        {
            public JsonQueryOptions Options { get; }

            public JsonPathEvaluator(JsonQueryOptions options)
            {
                Options = options;
            }

            // JSONPath evaluation logic would be implemented here;
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Interface for JSON parser functionality;
    /// </summary>
    public interface IJSONParser : IDisposable
    {
        /// <summary>
        /// Parses JSON string into specified type;
        /// </summary>
        Task<T> ParseAsync<T>(
            string json,
            JsonParserOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses JSON string into dynamic object;
        /// </summary>
        Task<dynamic> ParseDynamicAsync(
            string json,
            JsonParserOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses JSON from stream into specified type;
        /// </summary>
        Task<T> ParseFromStreamAsync<T>(
            Stream stream,
            JsonParserOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Serializes object to JSON string;
        /// </summary>
        Task<string> SerializeAsync<T>(
            T obj,
            JsonSerializerOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates JSON against schema;
        /// </summary>
        Task<JsonValidationResult> ValidateAsync(
            string json,
            JsonSchema schema,
            JsonValidationOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries JSON using JSONPath expression;
        /// </summary>
        Task<JsonQueryResult> QueryAsync(
            string json,
            string jsonPath,
            JsonQueryOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Transforms JSON using transformation rules;
        /// </summary>
        Task<string> TransformAsync(
            string json,
            JsonTransformation transformation,
            JsonTransformOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Merges multiple JSON documents;
        /// </summary>
        Task<string> MergeAsync(
            IEnumerable<string> jsonDocuments,
            JsonMergeStrategy mergeStrategy,
            JsonMergeOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates JSON schema from type;
        /// </summary>
        JsonSchema GenerateSchema<T>(JsonSchemaOptions options = null);

        /// <summary>
        /// Event raised when JSON parsing starts;
        /// </summary>
        event EventHandler<JsonParseStartedEventArgs> ParseStarted;

        /// <summary>
        /// Event raised when JSON parsing completes;
        /// </summary>
        event EventHandler<JsonParseCompletedEventArgs> ParseCompleted;

        /// <summary>
        /// Event raised when JSON parsing fails;
        /// </summary>
        event EventHandler<JsonParseFailedEventArgs> ParseFailed;

        /// <summary>
        /// Event raised when JSON validation is performed;
        /// </summary>
        event EventHandler<JsonValidationEventArgs> ValidationPerformed;
    }

    /// <summary>
    /// JSON parser options;
    /// </summary>
    public class JsonParserOptions;
    {
        public bool ValidateBeforeParse { get; set; } = false;
        public JsonSchema Schema { get; set; }
        public bool? PropertyNameCaseInsensitive { get; set; }
        public bool? WriteIndented { get; set; }
        public int? MaxDepth { get; set; }
        public bool AllowTrailingCommas { get; set; } = true;
        public JsonCommentHandling CommentHandling { get; set; } = JsonCommentHandling.Skip;
        public bool TrimWhitespace { get; set; } = false;
        public bool FixCommonErrors { get; set; } = false;
        public bool StoreParsedData { get; set; } = false;
        public List<JsonConverter> Converters { get; set; }

        public JsonParserOptions Clone()
        {
            return (JsonParserOptions)MemberwiseClone();
        }
    }

    /// <summary>
    /// JSON schema for validation;
    /// </summary>
    public class JsonSchema;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public JsonType? Type { get; set; }
        public Dictionary<string, JsonSchema> Properties { get; set; }
        public List<string> RequiredProperties { get; set; }
        public JsonSchema Items { get; set; }
        public int? MinItems { get; set; }
        public int? MaxItems { get; set; }
        public bool AllowAdditionalProperties { get; set; } = true;
        public StringConstraints StringConstraints { get; set; }
        public NumberConstraints NumberConstraints { get; set; }
        public List<object> EnumValues { get; set; }
    }

    /// <summary>
    /// JSON type enumeration;
    /// </summary>
    public enum JsonType;
    {
        Object,
        Array,
        String,
        Number,
        Boolean,
        Null;
    }

    /// <summary>
    /// String constraints for schema validation;
    /// </summary>
    public class StringConstraints;
    {
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public string Pattern { get; set; }
        public List<string> EnumValues { get; set; }
    }

    /// <summary>
    /// Number constraints for schema validation;
    /// </summary>
    public class NumberConstraints;
    {
        public decimal? Minimum { get; set; }
        public decimal? Maximum { get; set; }
        public decimal? ExclusiveMinimum { get; set; }
        public decimal? ExclusiveMaximum { get; set; }
        public decimal? MultipleOf { get; set; }
    }

    /// <summary>
    /// JSON validation options;
    /// </summary>
    public class JsonValidationOptions;
    {
        public bool Strict { get; set; } = false;
        public int? MaxDepth { get; set; }
        public bool AllowMultipleErrors { get; set; } = true;
    }

    /// <summary>
    /// JSON validation result;
    /// </summary>
    public class JsonValidationResult;
    {
        public bool IsValid { get; set; }
        public List<JsonValidationError> Errors { get; set; }
        public TimeSpan ValidationTime { get; set; }
        public string SchemaId { get; set; }
        public string OperationId { get; set; }
    }

    /// <summary>
    /// JSON validation error;
    /// </summary>
    public class JsonValidationError;
    {
        public string Path { get; set; }
        public string Message { get; set; }
        public string ErrorCode { get; set; }
        public ValidationErrorSeverity Severity { get; set; }
    }

    /// <summary>
    /// Validation error severity;
    /// </summary>
    public enum ValidationErrorSeverity;
    {
        Warning,
        Error,
        Critical;
    }

    /// <summary>
    /// JSON query options;
    /// </summary>
    public class JsonQueryOptions;
    {
        public bool CaseSensitive { get; set; } = false;
        public bool ReturnFirstMatchOnly { get; set; } = false;
    }

    /// <summary>
    /// JSON query result;
    /// </summary>
    public class JsonQueryResult;
    {
        public bool Success { get; set; }
        public List<JsonQueryMatch> Results { get; set; }
        public TimeSpan QueryTime { get; set; }
        public string JsonPath { get; set; }
        public int MatchCount { get; set; }
        public string OperationId { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// JSON query match;
    /// </summary>
    public class JsonQueryMatch;
    {
        public string Path { get; set; }
        public JsonElement Value { get; set; }
        public JsonValueKind ValueType { get; set; }
    }

    /// <summary>
    /// JSON transformation;
    /// </summary>
    public class JsonTransformation;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<TransformationRule> Rules { get; set; }
    }

    /// <summary>
    /// Transformation rule;
    /// </summary>
    public class TransformationRule;
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public TransformationType TransformationType { get; set; }
        public string TargetType { get; set; }
        public object DefaultValue { get; set; }
    }

    /// <summary>
    /// Transformation type;
    /// </summary>
    public enum TransformationType;
    {
        Rename,
        Convert,
        Filter,
        Map,
        Default;
    }

    /// <summary>
    /// JSON transform options;
    /// </summary>
    public class JsonTransformOptions;
    {
        public bool RemoveNullProperties { get; set; } = false;
        public bool RemoveEmptyArrays { get; set; } = false;
        public bool RemoveEmptyObjects { get; set; } = false;
    }

    /// <summary>
    /// JSON merge strategy;
    /// </summary>
    public enum JsonMergeStrategy;
    {
        PreferFirst,
        PreferSecond,
        Concat,
        Union,
        DeepMerge;
    }

    /// <summary>
    /// JSON merge options;
    /// </summary>
    public class JsonMergeOptions;
    {
        public bool RemoveDuplicates { get; set; } = true;
        public bool SortProperties { get; set; } = false;
    }

    /// <summary>
    /// JSON schema options;
    /// </summary>
    public class JsonSchemaOptions;
    {
        public JsonNamingPolicy NamingPolicy { get; set; } = JsonNamingPolicy.CamelCase;
        public bool IncludeInheritedProperties { get; set; } = true;
        public bool IncludePrivateProperties { get; set; } = false;
    }

    /// <summary>
    /// Parsed data record for storage;
    /// </summary>
    public class ParsedDataRecord<T>
    {
        public string OperationId { get; set; }
        public string Source { get; set; }
        public T Data { get; set; }
        public string DataType { get; set; }
        public TimeSpan ParseTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for JSON parse start;
    /// </summary>
    public class JsonParseStartedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Type TargetType { get; }
        public int? InputLength { get; }
        public JsonParserOptions Options { get; }

        public JsonParseStartedEventArgs(
            string operationId,
            Type targetType,
            int? inputLength,
            JsonParserOptions options)
        {
            OperationId = operationId;
            TargetType = targetType;
            InputLength = inputLength;
            Options = options;
        }
    }

    /// <summary>
    /// Event arguments for JSON parse completion;
    /// </summary>
    public class JsonParseCompletedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Type TargetType { get; }
        public object Result { get; }
        public TimeSpan ParseTime { get; }
        public bool Success { get; }

        public JsonParseCompletedEventArgs(
            string operationId,
            Type targetType,
            object result,
            TimeSpan parseTime,
            bool success)
        {
            OperationId = operationId;
            TargetType = targetType;
            Result = result;
            ParseTime = parseTime;
            Success = success;
        }
    }

    /// <summary>
    /// Event arguments for JSON parse failure;
    /// </summary>
    public class JsonParseFailedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Type TargetType { get; }
        public Exception Exception { get; }
        public JsonParserOptions Options { get; }

        public JsonParseFailedEventArgs(
            string operationId,
            Type targetType,
            Exception exception,
            JsonParserOptions options)
        {
            OperationId = operationId;
            TargetType = targetType;
            Exception = exception;
            Options = options;
        }
    }

    /// <summary>
    /// Event arguments for JSON validation;
    /// </summary>
    public class JsonValidationEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public string SchemaId { get; }
        public bool IsValid { get; }
        public List<JsonValidationError> Errors { get; }
        public JsonValidationOptions Options { get; }

        public JsonValidationEventArgs(
            string operationId,
            string schemaId,
            bool isValid,
            List<JsonValidationError> errors,
            JsonValidationOptions options)
        {
            OperationId = operationId;
            SchemaId = schemaId;
            IsValid = isValid;
            Errors = errors;
            Options = options;
        }
    }

    /// <summary>
    /// Custom exception for JSON parsing errors;
    /// </summary>
    public class JsonParseException : Exception
    {
        public string OperationId { get; }
        public Type TargetType { get; }

        public JsonParseException(
            string message,
            Exception innerException,
            string operationId,
            Type targetType)
            : base(message, innerException)
        {
            OperationId = operationId;
            TargetType = targetType;
        }
    }

    /// <summary>
    /// Custom exception for JSON serialization errors;
    /// </summary>
    public class JsonSerializeException : Exception
    {
        public string OperationId { get; }
        public Type SourceType { get; }

        public JsonSerializeException(
            string message,
            Exception innerException,
            string operationId,
            Type sourceType)
            : base(message, innerException)
        {
            OperationId = operationId;
            SourceType = sourceType;
        }
    }

    /// <summary>
    /// Custom exception for JSON validation errors;
    /// </summary>
    public class JsonValidationException : Exception
    {
        public string OperationId { get; }
        public string SchemaId { get; }
        public List<JsonValidationError> ValidationErrors { get; }

        public JsonValidationException(
            string message,
            Exception innerException,
            string operationId,
            string schemaId,
            List<JsonValidationError> validationErrors = null)
            : base(message, innerException)
        {
            OperationId = operationId;
            SchemaId = schemaId;
            ValidationErrors = validationErrors;
        }
    }

    /// <summary>
    /// Custom exception for JSON query errors;
    /// </summary>
    public class JsonQueryException : Exception
    {
        public string OperationId { get; }
        public string JsonPath { get; }

        public JsonQueryException(
            string message,
            Exception innerException,
            string operationId,
            string jsonPath)
            : base(message, innerException)
        {
            OperationId = operationId;
            JsonPath = jsonPath;
        }
    }

    /// <summary>
    /// Custom exception for JSON transformation errors;
    /// </summary>
    public class JsonTransformException : Exception
    {
        public string OperationId { get; }
        public string TransformationId { get; }

        public JsonTransformException(
            string message,
            Exception innerException,
            string operationId,
            string transformationId)
            : base(message, innerException)
        {
            OperationId = operationId;
            TransformationId = transformationId;
        }
    }

    /// <summary>
    /// Custom exception for JSON merge errors;
    /// </summary>
    public class JsonMergeException : Exception
    {
        public string OperationId { get; }
        public int DocumentCount { get; }

        public JsonMergeException(
            string message,
            Exception innerException,
            string operationId,
            int documentCount)
            : base(message, innerException)
        {
            OperationId = operationId;
            DocumentCount = documentCount;
        }
    }

    /// <summary>
    /// Custom exception for JSON schema errors;
    /// </summary>
    public class JsonSchemaException : Exception
    {
        public Type TargetType { get; }

        public JsonSchemaException(
            string message,
            Exception innerException,
            Type targetType)
            : base(message, innerException)
        {
            TargetType = targetType;
        }
    }

    #endregion;
}
