using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NEDA.Core.Common;
using NEDA.Core.Common.Constants;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.ExceptionHandling;
using NEDA.KnowledgeBase.DataManagement;
using NEDA.KnowledgeBase.Parsers;
using HtmlAgilityPack;

namespace NEDA.KnowledgeBase.WebScraper;
{
    /// <summary>
    /// Advanced parsing engine with support for multiple formats, 
    /// intelligent content detection, and extensible parser architecture.
    /// </summary>
    public class ParserEngine : IParserEngine, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger<ParserEngine> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IDataRepository _dataRepository;
        private readonly IServiceProvider _serviceProvider;
        private readonly ParserConfiguration _configuration;

        private bool _isDisposed;
        private readonly ConcurrentDictionary<string, IParser> _registeredParsers;
        private readonly ConcurrentDictionary<string, ParserCacheEntry> _parserCache;
        private readonly SemaphoreSlim _parsingSemaphore;
        private readonly object _parserRegistryLock = new object();

        private const int MAX_CONCURRENT_PARSING = 15;
        private const int DEFAULT_CACHE_SIZE = 1000;
        private const int DEFAULT_TIMEOUT_SECONDS = 300;
        private static readonly TimeSpan CACHE_EXPIRATION = TimeSpan.FromMinutes(30);

        private static readonly HashSet<string> HTML_CONTENT_TYPES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text/html",
            "application/xhtml+xml",
            "application/xhtml"
        };

        private static readonly HashSet<string> XML_CONTENT_TYPES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/xml",
            "text/xml",
            "application/atom+xml",
            "application/rss+xml",
            "application/soap+xml"
        };

        private static readonly HashSet<string> JSON_CONTENT_TYPES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/json",
            "text/json",
            "application/javascript",
            "application/x-javascript",
            "text/javascript"
        };

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when parsing starts;
        /// </summary>
        public event EventHandler<ParserStartedEventArgs> ParserStarted;

        /// <summary>
        /// Event raised when parsing completes;
        /// </summary>
        public event EventHandler<ParserCompletedEventArgs> ParserCompleted;

        /// <summary>
        /// Event raised when parsing fails;
        /// </summary>
        public event EventHandler<ParserFailedEventArgs> ParserFailed;

        /// <summary>
        /// Event raised when content type is detected;
        /// </summary>
        public event EventHandler<ContentTypeDetectedEventArgs> ContentTypeDetected;

        /// <summary>
        /// Event raised when parser is registered;
        /// </summary>
        public event EventHandler<ParserRegisteredEventArgs> ParserRegistered;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of ParserEngine;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="securityManager">Security manager instance</param>
        /// <param name="dataRepository">Data repository for storing parsed data</param>
        /// <param name="serviceProvider">Service provider for dependency injection</param>
        /// <param name="configuration">Parser configuration</param>
        public ParserEngine(
            ILogger<ParserEngine> logger,
            ISecurityManager securityManager,
            IDataRepository dataRepository,
            IServiceProvider serviceProvider,
            ParserConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _dataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            _configuration = configuration ?? new ParserConfiguration();
            _registeredParsers = new ConcurrentDictionary<string, IParser>(StringComparer.OrdinalIgnoreCase);
            _parserCache = new ConcurrentDictionary<string, ParserCacheEntry>();
            _parsingSemaphore = new SemaphoreSlim(
                _configuration.MaxConcurrentParsing ?? MAX_CONCURRENT_PARSING,
                _configuration.MaxConcurrentParsing ?? MAX_CONCURRENT_PARSING);

            // Register default parsers;
            RegisterDefaultParsers();

            _logger.LogInformation(LogEvents.ParserEngineCreated,
                "ParserEngine initialized with {MaxConcurrent} concurrent parsing slots",
                _configuration.MaxConcurrentParsing ?? MAX_CONCURRENT_PARSING);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Parses content with automatic format detection;
        /// </summary>
        /// <param name="content">Content to parse</param>
        /// <param name="context">Parsing context</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Parsing result</returns>
        public async Task<ParsingResult> ParseAsync(
            string content,
            ParsingContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Content cannot be null or empty", nameof(content));

            await _securityManager.ValidateOperationAsync(SecurityOperation.ContentParse);

            var operationId = Guid.NewGuid().ToString();
            var parsingContext = context ?? new ParsingContext();

            _logger.LogDebug(LogEvents.ParsingStarted,
                "Starting parsing operation {OperationId}, Content length: {Length}, Context: {@Context}",
                operationId, content.Length, parsingContext);

            try
            {
                await _parsingSemaphore.WaitAsync(cancellationToken);

                OnParserStarted(new ParserStartedEventArgs(
                    operationId,
                    content.Length,
                    parsingContext));

                // Detect content type;
                var contentType = await DetectContentTypeAsync(content, parsingContext);
                OnContentTypeDetected(new ContentTypeDetectedEventArgs(
                    operationId,
                    contentType,
                    content.Length));

                // Get appropriate parser;
                var parser = await GetParserForContentAsync(content, contentType, parsingContext);

                // Check cache if enabled;
                if (_configuration.EnableCaching)
                {
                    var cacheKey = GenerateCacheKey(content, contentType, parsingContext);
                    if (_parserCache.TryGetValue(cacheKey, out var cacheEntry) &&
                        cacheEntry.ExpirationTime > DateTime.UtcNow)
                    {
                        _logger.LogDebug(LogEvents.CacheHit,
                            "Cache hit for parsing operation {OperationId}, Key: {CacheKey}",
                            operationId, cacheKey);

                        var cachedResult = cacheEntry.Result.Clone();
                        cachedResult.OperationId = operationId;
                        cachedResult.FromCache = true;

                        OnParserCompleted(new ParserCompletedEventArgs(
                            operationId,
                            cachedResult,
                            TimeSpan.Zero,
                            true));

                        return cachedResult;
                    }
                }

                // Execute parsing;
                var result = await ExecuteParsingAsync(
                    parser,
                    content,
                    contentType,
                    parsingContext,
                    operationId,
                    cancellationToken);

                // Cache result if enabled;
                if (_configuration.EnableCaching && result.Success)
                {
                    var cacheKey = GenerateCacheKey(content, contentType, parsingContext);
                    var cacheEntry = new ParserCacheEntry
                    {
                        Result = result.Clone(),
                        ExpirationTime = DateTime.UtcNow.Add(_configuration.CacheExpiration ?? CACHE_EXPIRATION)
                    };

                    // Manage cache size;
                    ManageCacheSize();

                    _parserCache[cacheKey] = cacheEntry

                    _logger.LogDebug(LogEvents.CacheStored,
                        "Parsing result cached for operation {OperationId}, Key: {CacheKey}",
                        operationId, cacheKey);
                }

                OnParserCompleted(new ParserCompletedEventArgs(
                    operationId,
                    result,
                    result.ParsingTime,
                    result.Success));

                _logger.LogInformation(LogEvents.ParsingCompleted,
                    "Parsing completed: Operation {OperationId}, Type: {ContentType}, Success: {Success}, Duration: {Duration}",
                    operationId, contentType, result.Success, result.ParsingTime);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.ParsingCancelled,
                    "Parsing cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.ParsingFailed, ex,
                    "Parsing failed: Operation {OperationId}",
                    operationId);

                OnParserFailed(new ParserFailedEventArgs(
                    operationId,
                    ex,
                    parsingContext));

                throw new ParsingEngineException(
                    $"Parsing failed for operation {operationId}",
                    ex,
                    operationId,
                    content?.Length ?? 0);
            }
            finally
            {
                _parsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Parses content from stream;
        /// </summary>
        /// <param name="stream">Stream containing content</param>
        /// <param name="context">Parsing context</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Parsing result</returns>
        public async Task<ParsingResult> ParseFromStreamAsync(
            Stream stream,
            ParsingContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("Stream must be readable", nameof(stream));

            await _securityManager.ValidateOperationAsync(SecurityOperation.ContentParse);

            var operationId = Guid.NewGuid().ToString();
            var parsingContext = context ?? new ParsingContext();

            _logger.LogDebug(LogEvents.ParsingStarted,
                "Starting stream parsing operation {OperationId}, Context: {@Context}",
                operationId, parsingContext);

            try
            {
                await _parsingSemaphore.WaitAsync(cancellationToken);

                OnParserStarted(new ParserStartedEventArgs(
                    operationId,
                    (int?)stream.Length,
                    parsingContext));

                // Read and detect content type;
                var (content, contentType) = await ReadAndDetectContentAsync(stream, parsingContext);
                OnContentTypeDetected(new ContentTypeDetectedEventArgs(
                    operationId,
                    contentType,
                    content.Length));

                // Get appropriate parser;
                var parser = await GetParserForContentAsync(content, contentType, parsingContext);

                // Execute parsing;
                var result = await ExecuteParsingAsync(
                    parser,
                    content,
                    contentType,
                    parsingContext,
                    operationId,
                    cancellationToken);

                OnParserCompleted(new ParserCompletedEventArgs(
                    operationId,
                    result,
                    result.ParsingTime,
                    result.Success));

                _logger.LogInformation(LogEvents.ParsingCompleted,
                    "Stream parsing completed: Operation {OperationId}, Type: {ContentType}, Success: {Success}",
                    operationId, contentType, result.Success);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.ParsingCancelled,
                    "Stream parsing cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.ParsingFailed, ex,
                    "Stream parsing failed: Operation {OperationId}",
                    operationId);

                OnParserFailed(new ParserFailedEventArgs(
                    operationId,
                    ex,
                    parsingContext));

                throw new ParsingEngineException(
                    $"Stream parsing failed for operation {operationId}",
                    ex,
                    operationId,
                    0);
            }
            finally
            {
                _parsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Parses content with specified parser;
        /// </summary>
        /// <param name="content">Content to parse</param>
        /// <param name="parserName">Name of parser to use</param>
        /// <param name="options">Parser-specific options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Parsing result</returns>
        public async Task<ParsingResult> ParseWithParserAsync(
            string content,
            string parserName,
            ParserOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Content cannot be null or empty", nameof(content));

            if (string.IsNullOrWhiteSpace(parserName))
                throw new ArgumentException("Parser name cannot be null or empty", nameof(parserName));

            await _securityManager.ValidateOperationAsync(SecurityOperation.ContentParse);

            var operationId = Guid.NewGuid().ToString();
            var parserOptions = options ?? new ParserOptions();

            _logger.LogDebug(LogEvents.ParsingStarted,
                "Starting parsing with specific parser: {ParserName}, Operation: {OperationId}, Content length: {Length}",
                parserName, operationId, content.Length);

            try
            {
                await _parsingSemaphore.WaitAsync(cancellationToken);

                // Get specified parser;
                if (!_registeredParsers.TryGetValue(parserName, out var parser))
                {
                    throw new ParserNotFoundException($"Parser '{parserName}' is not registered");
                }

                OnParserStarted(new ParserStartedEventArgs(
                    operationId,
                    content.Length,
                    new ParsingContext { ParserName = parserName }));

                // Detect content type for logging;
                var contentType = await DetectContentTypeAsync(content, null);

                // Execute parsing;
                var result = await ExecuteParsingWithParserAsync(
                    parser,
                    content,
                    parserOptions,
                    operationId,
                    cancellationToken);

                OnParserCompleted(new ParserCompletedEventArgs(
                    operationId,
                    result,
                    result.ParsingTime,
                    result.Success));

                _logger.LogInformation(LogEvents.ParsingCompleted,
                    "Parser-specific parsing completed: Operation {OperationId}, Parser: {ParserName}, Success: {Success}",
                    operationId, parserName, result.Success);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.ParsingCancelled,
                    "Parser-specific parsing cancelled: Operation {OperationId}, Parser: {ParserName}",
                    operationId, parserName);
                throw;
            }
            catch (ParserNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.ParsingFailed, ex,
                    "Parser-specific parsing failed: Operation {OperationId}, Parser: {ParserName}",
                    operationId, parserName);

                OnParserFailed(new ParserFailedEventArgs(
                    operationId,
                    ex,
                    new ParsingContext { ParserName = parserName }));

                throw new ParsingEngineException(
                    $"Parsing with parser '{parserName}' failed for operation {operationId}",
                    ex,
                    operationId,
                    content.Length);
            }
            finally
            {
                _parsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Batch parses multiple content items;
        /// </summary>
        /// <param name="contentItems">Content items to parse</param>
        /// <param name="context">Parsing context</param>
        /// <param name="parallelism">Maximum parallel parsing operations</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Collection of parsing results</returns>
        public async Task<IEnumerable<ParsingResult>> ParseBatchAsync(
            IEnumerable<ContentItem> contentItems,
            ParsingContext context = null,
            int? parallelism = null,
            CancellationToken cancellationToken = default)
        {
            if (contentItems == null)
                throw new ArgumentNullException(nameof(contentItems));

            var items = contentItems.ToList();
            if (!items.Any())
                return Enumerable.Empty<ParsingResult>();

            await _securityManager.ValidateOperationAsync(SecurityOperation.ContentParse);

            var batchId = Guid.NewGuid().ToString();
            var parsingContext = context ?? new ParsingContext();
            var maxParallelism = parallelism ?? (_configuration.MaxConcurrentParsing ?? MAX_CONCURRENT_PARSING);

            _logger.LogInformation(LogEvents.BatchParsingStarted,
                "Starting batch parsing: Batch {BatchId}, Item count: {ItemCount}, Max parallelism: {MaxParallelism}",
                batchId, items.Count, maxParallelism);

            var semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);
            var tasks = new List<Task<ParsingResult>>();
            var results = new List<ParsingResult>();

            foreach (var item in items)
            {
                tasks.Add(ProcessBatchItemAsync(item, parsingContext, semaphore, batchId, cancellationToken));
            }

            try
            {
                var batchResults = await Task.WhenAll(tasks);
                results.AddRange(batchResults);

                var successCount = batchResults.Count(r => r.Success);
                var failedCount = batchResults.Count(r => !r.Success);

                _logger.LogInformation(LogEvents.BatchParsingCompleted,
                    "Batch parsing completed: Batch {BatchId}, Success: {SuccessCount}, Failed: {FailedCount}",
                    batchId, successCount, failedCount);

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.BatchParsingFailed, ex,
                    "Batch parsing failed: Batch {BatchId}",
                    batchId);
                throw;
            }
        }

        /// <summary>
        /// Extracts structured data from content;
        /// </summary>
        /// <typeparam name="T">Type of data to extract</typeparam>
        /// <param name="content">Content to extract from</param>
        /// <param name="extractionRules">Extraction rules</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Extracted data</returns>
        public async Task<DataExtractionResult<T>> ExtractDataAsync<T>(
            string content,
            ExtractionRules extractionRules,
            CancellationToken cancellationToken = default)
            where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Content cannot be null or empty", nameof(content));

            if (extractionRules == null)
                throw new ArgumentNullException(nameof(extractionRules));

            await _securityManager.ValidateOperationAsync(SecurityOperation.DataExtraction);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogDebug(LogEvents.DataExtractionStarted,
                "Starting data extraction: Operation {OperationId}, Content length: {Length}, Type: {Type}",
                operationId, content.Length, typeof(T).Name);

            try
            {
                await _parsingSemaphore.WaitAsync(cancellationToken);

                var startTime = DateTime.UtcNow;

                // Parse content first;
                var parsingResult = await ParseAsync(
                    content,
                    new ParsingContext { ExtractionMode = true },
                    cancellationToken);

                if (!parsingResult.Success)
                {
                    throw new DataExtractionException(
                        $"Failed to parse content before extraction: {parsingResult.ErrorMessage}",
                        operationId,
                        typeof(T));
                }

                // Extract data based on parsing result and rules;
                var extractedData = await ExecuteDataExtractionAsync<T>(
                    parsingResult,
                    extractionRules,
                    operationId,
                    cancellationToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                var result = new DataExtractionResult<T>
                {
                    Success = true,
                    OperationId = operationId,
                    ExtractedData = extractedData,
                    ExtractionTime = duration,
                    SourceContentLength = content.Length,
                    ExtractionRules = extractionRules;
                };

                // Store extracted data;
                await StoreExtractedDataAsync(operationId, content, result);

                _logger.LogInformation(LogEvents.DataExtractionCompleted,
                    "Data extraction completed: Operation {OperationId}, Type: {Type}, Items: {ItemCount}, Duration: {Duration}",
                    operationId, typeof(T).Name, extractedData?.Count() ?? 0, duration);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.DataExtractionCancelled,
                    "Data extraction cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.DataExtractionFailed, ex,
                    "Data extraction failed: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);

                throw new DataExtractionException(
                    $"Failed to extract data of type {typeof(T).Name}",
                    ex,
                    operationId,
                    typeof(T));
            }
            finally
            {
                _parsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Validates content against schema or rules;
        /// </summary>
        /// <param name="content">Content to validate</param>
        /// <param name="validationRules">Validation rules</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Validation result</returns>
        public async Task<ContentValidationResult> ValidateContentAsync(
            string content,
            ValidationRules validationRules,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Content cannot be null or empty", nameof(content));

            if (validationRules == null)
                throw new ArgumentNullException(nameof(validationRules));

            await _securityManager.ValidateOperationAsync(SecurityOperation.ContentValidation);

            var operationId = Guid.NewGuid().ToString();

            _logger.LogDebug(LogEvents.ContentValidationStarted,
                "Starting content validation: Operation {OperationId}, Content length: {Length}",
                operationId, content.Length);

            try
            {
                await _parsingSemaphore.WaitAsync(cancellationToken);

                var startTime = DateTime.UtcNow;

                // Detect content type;
                var contentType = await DetectContentTypeAsync(content, null);

                // Get appropriate validator;
                var validator = GetValidatorForContentType(contentType, validationRules);

                // Execute validation;
                var result = await ExecuteValidationAsync(
                    content,
                    contentType,
                    validator,
                    validationRules,
                    operationId,
                    cancellationToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                result.ValidationTime = duration;
                result.OperationId = operationId;

                _logger.LogInformation(LogEvents.ContentValidationCompleted,
                    "Content validation completed: Operation {OperationId}, Valid: {IsValid}, ErrorCount: {ErrorCount}",
                    operationId, result.IsValid, result.Errors?.Count ?? 0);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(LogEvents.ContentValidationCancelled,
                    "Content validation cancelled: Operation {OperationId}",
                    operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.ContentValidationFailed, ex,
                    "Content validation failed: Operation {OperationId}",
                    operationId);

                throw new ContentValidationException(
                    $"Failed to validate content for operation {operationId}",
                    ex,
                    operationId);
            }
            finally
            {
                _parsingSemaphore.Release();
            }
        }

        /// <summary>
        /// Registers a custom parser;
        /// </summary>
        /// <param name="parser">Parser to register</param>
        /// <returns>Registration result</returns>
        public bool RegisterParser(IParser parser)
        {
            if (parser == null)
                throw new ArgumentNullException(nameof(parser));

            if (string.IsNullOrWhiteSpace(parser.Name))
                throw new ArgumentException("Parser must have a name", nameof(parser));

            lock (_parserRegistryLock)
            {
                if (_registeredParsers.ContainsKey(parser.Name))
                {
                    _logger.LogWarning(LogEvents.ParserRegistrationFailed,
                        "Parser registration failed: Parser '{ParserName}' is already registered",
                        parser.Name);
                    return false;
                }

                if (!_registeredParsers.TryAdd(parser.Name, parser))
                {
                    _logger.LogError(LogEvents.ParserRegistrationFailed,
                        "Parser registration failed: Could not add parser '{ParserName}'",
                        parser.Name);
                    return false;
                }

                // Register content types;
                if (parser.SupportedContentTypes != null)
                {
                    foreach (var contentType in parser.SupportedContentTypes)
                    {
                        if (!_registeredParsers.ContainsKey($"contenttype:{contentType}"))
                        {
                            _registeredParsers.TryAdd($"contenttype:{contentType}", parser);
                        }
                    }
                }

                // Register file extensions;
                if (parser.SupportedFileExtensions != null)
                {
                    foreach (var extension in parser.SupportedFileExtensions)
                    {
                        var normalizedExtension = extension.StartsWith(".") ? extension : $".{extension}";
                        if (!_registeredParsers.ContainsKey($"extension:{normalizedExtension}"))
                        {
                            _registeredParsers.TryAdd($"extension:{normalizedExtension}", parser);
                        }
                    }
                }

                OnParserRegistered(new ParserRegisteredEventArgs(
                    parser.Name,
                    parser.SupportedContentTypes,
                    parser.SupportedFileExtensions));

                _logger.LogInformation(LogEvents.ParserRegistered,
                    "Parser registered: {ParserName}, Supported types: {ContentTypes}, Extensions: {Extensions}",
                    parser.Name,
                    string.Join(", ", parser.SupportedContentTypes ?? Array.Empty<string>()),
                    string.Join(", ", parser.SupportedFileExtensions ?? Array.Empty<string>()));

                return true;
            }
        }

        /// <summary>
        /// Unregisters a parser;
        /// </summary>
        /// <param name="parserName">Name of parser to unregister</param>
        /// <returns>True if parser was unregistered</returns>
        public bool UnregisterParser(string parserName)
        {
            if (string.IsNullOrWhiteSpace(parserName))
                throw new ArgumentException("Parser name cannot be null or empty", nameof(parserName));

            lock (_parserRegistryLock)
            {
                if (!_registeredParsers.TryRemove(parserName, out var parser))
                {
                    _logger.LogWarning(LogEvents.ParserUnregistrationFailed,
                        "Parser unregistration failed: Parser '{ParserName}' not found",
                        parserName);
                    return false;
                }

                // Remove content type mappings;
                if (parser.SupportedContentTypes != null)
                {
                    foreach (var contentType in parser.SupportedContentTypes)
                    {
                        _registeredParsers.TryRemove($"contenttype:{contentType}", out _);
                    }
                }

                // Remove file extension mappings;
                if (parser.SupportedFileExtensions != null)
                {
                    foreach (var extension in parser.SupportedFileExtensions)
                    {
                        var normalizedExtension = extension.StartsWith(".") ? extension : $".{extension}";
                        _registeredParsers.TryRemove($"extension:{normalizedExtension}", out _);
                    }
                }

                _logger.LogInformation(LogEvents.ParserUnregistered,
                    "Parser unregistered: {ParserName}", parserName);

                return true;
            }
        }

        /// <summary>
        /// Gets list of registered parsers;
        /// </summary>
        /// <returns>Collection of parser information</returns>
        public IEnumerable<ParserInfo> GetRegisteredParsers()
        {
            return _registeredParsers;
                .Where(kvp => !kvp.Key.StartsWith("contenttype:") && !kvp.Key.StartsWith("extension:"))
                .Select(kvp => new ParserInfo;
                {
                    Name = kvp.Value.Name,
                    Description = kvp.Value.Description,
                    SupportedContentTypes = kvp.Value.SupportedContentTypes,
                    SupportedFileExtensions = kvp.Value.SupportedFileExtensions,
                    Version = kvp.Value.Version;
                })
                .ToList();
        }

        /// <summary>
        /// Clears the parser cache;
        /// </summary>
        public void ClearCache()
        {
            var count = _parserCache.Count;
            _parserCache.Clear();

            _logger.LogInformation(LogEvents.CacheCleared,
                "Parser cache cleared: {Count} entries removed", count);
        }

        /// <summary>
        /// Gets cache statistics;
        /// </summary>
        /// <returns>Cache statistics</returns>
        public CacheStatistics GetCacheStatistics()
        {
            var now = DateTime.UtcNow;
            var expiredEntries = _parserCache.Count(kvp => kvp.Value.ExpirationTime <= now);
            var validEntries = _parserCache.Count - expiredEntries;

            return new CacheStatistics;
            {
                TotalEntries = _parserCache.Count,
                ValidEntries = validEntries,
                ExpiredEntries = expiredEntries,
                MaxSize = _configuration.CacheSize ?? DEFAULT_CACHE_SIZE,
                MemoryUsage = CalculateCacheMemoryUsage()
            };
        }

        #endregion;

        #region Private Methods;

        private void RegisterDefaultParsers()
        {
            // Register HTML parser;
            RegisterParser(new HtmlParser());

            // Register XML parser;
            RegisterParser(new XmlParser());

            // Register JSON parser (uses the JSONParser service)
            var jsonParser = _serviceProvider.GetService<IJSONParser>();
            if (jsonParser != null)
            {
                RegisterParser(new JsonParserAdapter(jsonParser));
            }

            // Register plain text parser;
            RegisterParser(new TextParser());

            // Register CSV parser;
            RegisterParser(new CsvParser());

            // Register markdown parser;
            RegisterParser(new MarkdownParser());

            _logger.LogInformation(LogEvents.DefaultParsersRegistered,
                "Default parsers registered: HTML, XML, JSON, Text, CSV, Markdown");
        }

        private async Task<ParsingResult> ExecuteParsingAsync(
            IParser parser,
            string content,
            string contentType,
            ParsingContext context,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Configure parser options;
                var parserOptions = CreateParserOptions(contentType, context);

                // Execute parsing;
                var result = await parser.ParseAsync(
                    content,
                    parserOptions,
                    cancellationToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Enhance result with additional information;
                result.OperationId = operationId;
                result.ParsingTime = duration;
                result.ContentType = contentType;
                result.ParserName = parser.Name;
                result.SourceLength = content.Length;

                // Store parsed data if requested;
                if (context.StoreParsedData && result.Success && result.ParsedData != null)
                {
                    await StoreParsedDataAsync(operationId, content, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(LogEvents.ParserExecutionFailed, ex,
                    "Parser execution failed: Operation {OperationId}, Parser: {ParserName}",
                    operationId, parser.Name);

                return new ParsingResult;
                {
                    Success = false,
                    OperationId = operationId,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ContentType = contentType,
                    ParserName = parser.Name,
                    ParsingTime = DateTime.UtcNow - startTime;
                };
            }
        }

        private async Task<ParsingResult> ExecuteParsingWithParserAsync(
            IParser parser,
            string content,
            ParserOptions options,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                var result = await parser.ParseAsync(content, options, cancellationToken);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                result.OperationId = operationId;
                result.ParsingTime = duration;
                result.ParserName = parser.Name;
                result.SourceLength = content.Length;

                return result;
            }
            catch (Exception ex)
            {
                return new ParsingResult;
                {
                    Success = false,
                    OperationId = operationId,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ParserName = parser.Name,
                    ParsingTime = DateTime.UtcNow - startTime;
                };
            }
        }

        private async Task<string> DetectContentTypeAsync(string content, ParsingContext context)
        {
            if (context?.ContentType != null)
            {
                _logger.LogDebug(LogEvents.ContentTypeDetected,
                    "Content type from context: {ContentType}", context.ContentType);
                return context.ContentType;
            }

            // Check for HTML/XML/JSON patterns;
            var trimmedContent = content.TrimStart();

            // Check for JSON (starts with { or [)
            if (trimmedContent.StartsWith("{") || trimmedContent.StartsWith("["))
            {
                // Try to parse as JSON to confirm;
                try
                {
                    // Quick JSON validation;
                    if (IsValidJson(trimmedContent))
                    {
                        _logger.LogDebug(LogEvents.ContentTypeDetected,
                            "Content type detected as JSON");
                        return "application/json";
                    }
                }
                catch
                {
                    // Not valid JSON;
                }
            }

            // Check for XML (starts with <?xml or <root)
            if (trimmedContent.StartsWith("<?xml") ||
                (trimmedContent.StartsWith("<") && trimmedContent.Contains("?>")) ||
                (trimmedContent.StartsWith("<") && Regex.IsMatch(trimmedContent, @"^<[a-zA-Z_][a-zA-Z0-9_]*[>\\s]")))
            {
                // Try to parse as XML to confirm;
                try
                {
                    // Quick XML validation;
                    if (IsValidXml(trimmedContent))
                    {
                        _logger.LogDebug(LogEvents.ContentTypeDetected,
                            "Content type detected as XML");
                        return "application/xml";
                    }
                }
                catch
                {
                    // Not valid XML;
                }
            }

            // Check for HTML (contains HTML tags)
            if (Regex.IsMatch(content, @"<(html|head|body|div|span|p|a|img|table|tr|td)[\s>]", RegexOptions.IgnoreCase))
            {
                _logger.LogDebug(LogEvents.ContentTypeDetected,
                    "Content type detected as HTML");
                return "text/html";
            }

            // Check for CSV (contains commas and newlines in pattern)
            if (IsLikelyCsv(content))
            {
                _logger.LogDebug(LogEvents.ContentTypeDetected,
                    "Content type detected as CSV");
                return "text/csv";
            }

            // Default to plain text;
            _logger.LogDebug(LogEvents.ContentTypeDetected,
                "Content type detected as plain text");
            return "text/plain";
        }

        private async Task<IParser> GetParserForContentAsync(
            string content,
            string contentType,
            ParsingContext context)
        {
            // Check for specific parser in context;
            if (!string.IsNullOrWhiteSpace(context.ParserName))
            {
                if (_registeredParsers.TryGetValue(context.ParserName, out var specifiedParser))
                {
                    _logger.LogDebug(LogEvents.ParserSelected,
                        "Using specified parser: {ParserName}", context.ParserName);
                    return specifiedParser;
                }
                else;
                {
                    _logger.LogWarning(LogEvents.ParserNotFound,
                        "Specified parser not found: {ParserName}, falling back to auto-selection",
                        context.ParserName);
                }
            }

            // Try to get parser by content type;
            var contentTypeKey = $"contenttype:{contentType}";
            if (_registeredParsers.TryGetValue(contentTypeKey, out var contentTypeParser))
            {
                _logger.LogDebug(LogEvents.ParserSelected,
                    "Using content-type parser for: {ContentType}", contentType);
                return contentTypeParser;
            }

            // Try to get parser by MIME type category;
            if (HTML_CONTENT_TYPES.Contains(contentType))
            {
                if (_registeredParsers.TryGetValue("contenttype:text/html", out var htmlParser))
                {
                    return htmlParser;
                }
            }
            else if (XML_CONTENT_TYPES.Contains(contentType))
            {
                if (_registeredParsers.TryGetValue("contenttype:application/xml", out var xmlParser))
                {
                    return xmlParser;
                }
            }
            else if (JSON_CONTENT_TYPES.Contains(contentType))
            {
                if (_registeredParsers.TryGetValue("contenttype:application/json", out var jsonParser))
                {
                    return jsonParser;
                }
            }

            // Try to detect parser from content;
            var detectedParser = await DetectParserFromContentAsync(content, context);
            if (detectedParser != null)
            {
                return detectedParser;
            }

            // Fall back to plain text parser;
            if (_registeredParsers.TryGetValue("TextParser", out var textParser))
            {
                _logger.LogDebug(LogEvents.ParserSelected,
                    "Falling back to text parser for content type: {ContentType}", contentType);
                return textParser;
            }

            throw new ParserNotFoundException($"No suitable parser found for content type: {contentType}");
        }

        private async Task<IParser> DetectParserFromContentAsync(string content, ParsingContext context)
        {
            // Check file extension if provided in context;
            if (!string.IsNullOrWhiteSpace(context.FileExtension))
            {
                var extensionKey = $"extension:{context.FileExtension}";
                if (_registeredParsers.TryGetValue(extensionKey, out var extensionParser))
                {
                    _logger.LogDebug(LogEvents.ParserSelected,
                        "Using file extension parser for: {Extension}", context.FileExtension);
                    return extensionParser;
                }
            }

            // Analyze content structure;
            var analysis = await AnalyzeContentStructureAsync(content);

            // Based on analysis, select appropriate parser;
            if (analysis.IsHtml)
            {
                return GetParserByName("HtmlParser");
            }
            else if (analysis.IsXml)
            {
                return GetParserByName("XmlParser");
            }
            else if (analysis.IsJson)
            {
                return GetParserByName("JsonParser");
            }
            else if (analysis.IsCsv)
            {
                return GetParserByName("CsvParser");
            }
            else if (analysis.IsMarkdown)
            {
                return GetParserByName("MarkdownParser");
            }

            return null;
        }

        private IParser GetParserByName(string parserName)
        {
            return _registeredParsers.TryGetValue(parserName, out var parser) ? parser : null;
        }

        private async Task<(string Content, string ContentType)> ReadAndDetectContentAsync(
            Stream stream,
            ParsingContext context)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            {
                // Read first chunk for detection;
                var buffer = new char[4096];
                var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                var firstChunk = new string(buffer, 0, bytesRead);

                // Detect content type from first chunk;
                var contentType = await DetectContentTypeAsync(firstChunk, context);

                // Read remaining content;
                var remainingContent = await reader.ReadToEndAsync();
                var fullContent = firstChunk + remainingContent;

                return (fullContent, contentType);
            }
        }

        private async Task<ContentStructureAnalysis> AnalyzeContentStructureAsync(string content)
        {
            return await Task.Run(() =>
            {
                var analysis = new ContentStructureAnalysis();

                // Check for HTML;
                if (content.Contains("<html") || content.Contains("<div") || content.Contains("<span") ||
                    content.Contains("<p") || content.Contains("<a href"))
                {
                    analysis.IsHtml = true;
                    analysis.Confidence = 0.9;
                }

                // Check for XML;
                if (content.Contains("<?xml") ||
                    (content.Contains("<") && content.Contains(">") &&
                     Regex.IsMatch(content, @"<[a-zA-Z_][a-zA-Z0-9_]*>[^<]*</[a-zA-Z_][a-zA-Z0-9_]*>")))
                {
                    analysis.IsXml = true;
                    analysis.Confidence = Math.Max(analysis.Confidence, 0.8);
                }

                // Check for JSON;
                if ((content.TrimStart().StartsWith("{") && content.TrimEnd().EndsWith("}")) ||
                    (content.TrimStart().StartsWith("[") && content.TrimEnd().EndsWith("]")))
                {
                    analysis.IsJson = true;
                    analysis.Confidence = Math.Max(analysis.Confidence, 0.85);
                }

                // Check for CSV;
                if (IsLikelyCsv(content))
                {
                    analysis.IsCsv = true;
                    analysis.Confidence = Math.Max(analysis.Confidence, 0.7);
                }

                // Check for Markdown;
                if (content.Contains("# ") || content.Contains("## ") || content.Contains("### ") ||
                    content.Contains("* ") || content.Contains("- ") || content.Contains("1. ") ||
                    content.Contains("```") || content.Contains("`") || content.Contains("![") ||
                    content.Contains("]("))
                {
                    analysis.IsMarkdown = true;
                    analysis.Confidence = Math.Max(analysis.Confidence, 0.6);
                }

                return analysis;
            });
        }

        private bool IsValidJson(string content)
        {
            try
            {
                // Quick validation by checking braces/brackets balance;
                var stack = new Stack<char>();
                var inString = false;
                var escape = false;

                for (int i = 0; i < content.Length; i++)
                {
                    var c = content[i];

                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (c == '"' && !escape)
                    {
                        inString = !inString;
                        continue;
                    }

                    if (!inString)
                    {
                        if (c == '{' || c == '[')
                        {
                            stack.Push(c);
                        }
                        else if (c == '}')
                        {
                            if (stack.Count == 0 || stack.Pop() != '{')
                                return false;
                        }
                        else if (c == ']')
                        {
                            if (stack.Count == 0 || stack.Pop() != '[')
                                return false;
                        }
                    }
                }

                return stack.Count == 0 && !inString;
            }
            catch
            {
                return false;
            }
        }

        private bool IsValidXml(string content)
        {
            try
            {
                // Quick validation by checking tag balance;
                var regex = new Regex(@"<([^>]+)>");
                var matches = regex.Matches(content);
                var stack = new Stack<string>();

                foreach (Match match in matches)
                {
                    var tag = match.Groups[1].Value;

                    // Skip processing instructions and comments;
                    if (tag.StartsWith("?") || tag.StartsWith("!"))
                        continue;

                    // Check if it's a closing tag;
                    if (tag.StartsWith("/"))
                    {
                        var tagName = tag.Substring(1).Split(' ')[0];
                        if (stack.Count == 0 || stack.Pop() != tagName)
                            return false;
                    }
                    // Check if it's a self-closing tag;
                    else if (tag.EndsWith("/"))
                    {
                        // Self-closing, no need to push;
                    }
                    // Opening tag;
                    else;
                    {
                        var tagName = tag.Split(' ')[0];
                        stack.Push(tagName);
                    }
                }

                return stack.Count == 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsLikelyCsv(string content)
        {
            var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
                return false;

            // Check if first line looks like headers;
            var firstLine = lines[0];
            var commaCount = firstLine.Count(c => c == ',');

            // Need at least one comma;
            if (commaCount == 0)
                return false;

            // Check if subsequent lines have similar comma count;
            for (int i = 1; i < Math.Min(5, lines.Length); i++)
            {
                var lineCommaCount = lines[i].Count(c => c == ',');
                if (Math.Abs(lineCommaCount - commaCount) > 1)
                    return false;
            }

            return true;
        }

        private ParserOptions CreateParserOptions(string contentType, ParsingContext context)
        {
            var options = new ParserOptions();

            // Set options based on content type;
            if (HTML_CONTENT_TYPES.Contains(contentType))
            {
                options.HtmlParsingOptions = new HtmlParsingOptions;
                {
                    FixHtmlErrors = true,
                    RemoveScripts = context.RemoveScripts,
                    RemoveStyles = context.RemoveStyles,
                    ExtractTextOnly = context.ExtractTextOnly,
                    PreserveWhitespace = context.PreserveWhitespace;
                };
            }
            else if (XML_CONTENT_TYPES.Contains(contentType))
            {
                options.XmlParsingOptions = new XmlParsingOptions;
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = !context.PreserveWhitespace,
                    LoadSchema = context.ValidateSchema;
                };
            }
            else if (JSON_CONTENT_TYPES.Contains(contentType))
            {
                options.JsonParsingOptions = new JsonParsingOptions;
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = context.MaxDepth ?? 64;
                };
            }

            // Apply context options;
            options.ValidationOptions = context.ValidationOptions;
            options.TransformationRules = context.TransformationRules;
            options.ExtractionRules = context.ExtractionRules;
            options.Timeout = context.Timeout ?? TimeSpan.FromSeconds(DEFAULT_TIMEOUT_SECONDS);

            return options;
        }

        private async Task<IEnumerable<T>> ExecuteDataExtractionAsync<T>(
            ParsingResult parsingResult,
            ExtractionRules rules,
            string operationId,
            CancellationToken cancellationToken)
            where T : class, new()
        {
            return await Task.Run(() =>
            {
                var results = new List<T>();

                if (!parsingResult.Success || parsingResult.ParsedData == null)
                    return results;

                // Extract data based on parsing result type;
                if (parsingResult.ParsedData is HtmlDocument htmlDoc)
                {
                    results.AddRange(ExtractFromHtml<T>(htmlDoc, rules));
                }
                else if (parsingResult.ParsedData is XDocument xmlDoc)
                {
                    results.AddRange(ExtractFromXml<T>(xmlDoc, rules));
                }
                else if (parsingResult.ParsedData is System.Text.Json.JsonElement jsonElement)
                {
                    results.AddRange(ExtractFromJson<T>(jsonElement, rules));
                }
                else if (parsingResult.ParsedData is string text)
                {
                    results.AddRange(ExtractFromText<T>(text, rules));
                }

                return results;
            }, cancellationToken);
        }

        private IEnumerable<T> ExtractFromHtml<T>(HtmlDocument htmlDoc, ExtractionRules rules)
            where T : class, new()
        {
            var results = new List<T>();

            // Implement HTML extraction logic based on rules;
            // This is a simplified example;
            if (rules.Selectors != null)
            {
                foreach (var selector in rules.Selectors)
                {
                    var nodes = htmlDoc.DocumentNode.SelectNodes(selector.XPath ?? selector.CssSelector);
                    if (nodes != null)
                    {
                        foreach (var node in nodes)
                        {
                            var item = new T();
                            // Map node data to object properties;
                            // Implementation depends on rules mapping;
                            results.Add(item);
                        }
                    }
                }
            }

            return results;
        }

        private IEnumerable<T> ExtractFromXml<T>(XDocument xmlDoc, ExtractionRules rules)
            where T : class, new()
        {
            var results = new List<T>();

            // Implement XML extraction logic based on rules;
            if (rules.XPathQueries != null)
            {
                foreach (var xpath in rules.XPathQueries)
                {
                    var elements = xmlDoc.XPathSelectElements(xpath);
                    foreach (var element in elements)
                    {
                        var item = new T();
                        // Map element data to object properties;
                        results.Add(item);
                    }
                }
            }

            return results;
        }

        private IEnumerable<T> ExtractFromJson<T>(System.Text.Json.JsonElement jsonElement, ExtractionRules rules)
            where T : class, new()
        {
            var results = new List<T>();

            // Implement JSON extraction logic based on rules;
            if (rules.JsonPaths != null)
            {
                foreach (var jsonPath in rules.JsonPaths)
                {
                    // Extract using JSONPath;
                    // This would require a JSONPath implementation;
                    var items = ExtractByJsonPath<T>(jsonElement, jsonPath);
                    results.AddRange(items);
                }
            }

            return results;
        }

        private IEnumerable<T> ExtractFromText<T>(string text, ExtractionRules rules)
            where T : class, new()
        {
            var results = new List<T>();

            // Implement text extraction using regex patterns;
            if (rules.RegexPatterns != null)
            {
                foreach (var pattern in rules.RegexPatterns)
                {
                    var matches = Regex.Matches(text, pattern.Pattern, pattern.Options);
                    foreach (Match match in matches)
                    {
                        var item = new T();
                        // Map match groups to object properties;
                        results.Add(item);
                    }
                }
            }

            return results;
        }

        private IEnumerable<T> ExtractByJsonPath<T>(System.Text.Json.JsonElement element, string jsonPath)
            where T : class, new()
        {
            // Simplified JSONPath extraction;
            // In a real implementation, use a proper JSONPath library;
            return new List<T>();
        }

        private IContentValidator GetValidatorForContentType(string contentType, ValidationRules rules)
        {
            // Return appropriate validator based on content type;
            if (HTML_CONTENT_TYPES.Contains(contentType))
            {
                return new HtmlValidator(rules);
            }
            else if (XML_CONTENT_TYPES.Contains(contentType))
            {
                return new XmlValidator(rules);
            }
            else if (JSON_CONTENT_TYPES.Contains(contentType))
            {
                return new JsonValidator(rules);
            }

            return new TextValidator(rules);
        }

        private async Task<ContentValidationResult> ExecuteValidationAsync(
            string content,
            string contentType,
            IContentValidator validator,
            ValidationRules rules,
            string operationId,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                var result = await validator.ValidateAsync(content, cancellationToken);
                result.OperationId = operationId;
                result.ContentType = contentType;

                return result;
            }
            catch (Exception ex)
            {
                return new ContentValidationResult;
                {
                    OperationId = operationId,
                    ContentType = contentType,
                    IsValid = false,
                    Errors = new List<ValidationError>
                    {
                        new ValidationError;
                        {
                            Code = "VALIDATION_ERROR",
                            Message = $"Validation failed: {ex.Message}",
                            Severity = ValidationSeverity.Error,
                            Location = "Global"
                        }
                    },
                    ValidationTime = DateTime.UtcNow - startTime;
                };
            }
        }

        private async Task<ParsingResult> ProcessBatchItemAsync(
            ContentItem item,
            ParsingContext context,
            SemaphoreSlim semaphore,
            string batchId,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var itemContext = context.Clone();
                itemContext.ContentType = item.ContentType;
                itemContext.FileExtension = item.FileExtension;
                itemContext.SourceUrl = item.SourceUrl;

                var result = await ParseAsync(item.Content, itemContext, cancellationToken);
                result.BatchId = batchId;
                result.ItemId = item.Id;

                return result;
            }
            catch (Exception ex)
            {
                return new ParsingResult;
                {
                    Success = false,
                    BatchId = batchId,
                    ItemId = item.Id,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ParsingTime = TimeSpan.Zero;
                };
            }
            finally
            {
                semaphore.Release();
            }
        }

        private string GenerateCacheKey(string content, string contentType, ParsingContext context)
        {
            // Create a hash-based key for caching;
            var keyData = $"{contentType}:{context.ParserName}:{context.GetHashCode()}:{content.GetHashCode()}";
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyData));
                return Convert.ToBase64String(hash);
            }
        }

        private void ManageCacheSize()
        {
            var maxSize = _configuration.CacheSize ?? DEFAULT_CACHE_SIZE;
            if (_parserCache.Count <= maxSize)
                return;

            // Remove expired entries first;
            var now = DateTime.UtcNow;
            var expiredKeys = _parserCache;
                .Where(kvp => kvp.Value.ExpirationTime <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _parserCache.TryRemove(key, out _);
            }

            // If still over limit, remove oldest entries;
            if (_parserCache.Count > maxSize)
            {
                var oldestKeys = _parserCache;
                    .OrderBy(kvp => kvp.Value.ExpirationTime)
                    .Take(_parserCache.Count - maxSize)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldestKeys)
                {
                    _parserCache.TryRemove(key, out _);
                }
            }
        }

        private long CalculateCacheMemoryUsage()
        {
            // Estimate memory usage (simplified)
            long totalSize = 0;
            foreach (var entry in _parserCache.Values)
            {
                if (entry.Result?.ParsedData is string str)
                {
                    totalSize += str.Length * 2; // Approximate for UTF-16;
                }
                // Add estimation for other data types;
                totalSize += 100; // Overhead per entry
            }
            return totalSize;
        }

        private async Task StoreParsedDataAsync(string operationId, string sourceContent, ParsingResult result)
        {
            try
            {
                await _dataRepository.SaveParsedContentAsync(new ParsedContentRecord;
                {
                    OperationId = operationId,
                    SourceContent = sourceContent,
                    ParsedData = result.ParsedData,
                    ContentType = result.ContentType,
                    ParserName = result.ParserName,
                    ParseTime = result.ParsingTime,
                    Success = result.Success,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        { "ContentLength", sourceContent.Length },
                        { "ResultType", result.ParsedData?.GetType().Name }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.DataStorageFailed, ex,
                    "Failed to store parsed data: Operation {OperationId}",
                    operationId);
            }
        }

        private async Task StoreExtractedDataAsync<T>(
            string operationId,
            string sourceContent,
            DataExtractionResult<T> result)
            where T : class, new()
        {
            try
            {
                await _dataRepository.SaveExtractedDataAsync(new ExtractedDataRecord<T>
                {
                    OperationId = operationId,
                    SourceContent = sourceContent,
                    ExtractedData = result.ExtractedData.ToList(),
                    DataType = typeof(T).FullName,
                    ExtractionTime = result.ExtractionTime,
                    ExtractionRules = result.ExtractionRules,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.DataStorageFailed, ex,
                    "Failed to store extracted data: Operation {OperationId}, Type: {Type}",
                    operationId, typeof(T).Name);
            }
        }

        #endregion;

        #region Event Methods;

        protected virtual void OnParserStarted(ParserStartedEventArgs e)
        {
            ParserStarted?.Invoke(this, e);
        }

        protected virtual void OnParserCompleted(ParserCompletedEventArgs e)
        {
            ParserCompleted?.Invoke(this, e);
        }

        protected virtual void OnParserFailed(ParserFailedEventArgs e)
        {
            ParserFailed?.Invoke(this, e);
        }

        protected virtual void OnContentTypeDetected(ContentTypeDetectedEventArgs e)
        {
            ContentTypeDetected?.Invoke(this, e);
        }

        protected virtual void OnParserRegistered(ParserRegisteredEventArgs e)
        {
            ParserRegistered?.Invoke(this, e);
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
                    _parsingSemaphore?.Dispose();
                    _parserCache.Clear();
                    _registeredParsers.Clear();
                }

                _isDisposed = true;
            }
        }

        ~ParserEngine()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes;

        private class ParserCacheEntry
        {
            public ParsingResult Result { get; set; }
            public DateTime ExpirationTime { get; set; }
        }

        private class ContentStructureAnalysis;
        {
            public bool IsHtml { get; set; }
            public bool IsXml { get; set; }
            public bool IsJson { get; set; }
            public bool IsCsv { get; set; }
            public bool IsMarkdown { get; set; }
            public double Confidence { get; set; }
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Interface for parser engine functionality;
    /// </summary>
    public interface IParserEngine : IDisposable
    {
        /// <summary>
        /// Parses content with automatic format detection;
        /// </summary>
        Task<ParsingResult> ParseAsync(
            string content,
            ParsingContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses content from stream;
        /// </summary>
        Task<ParsingResult> ParseFromStreamAsync(
            Stream stream,
            ParsingContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses content with specified parser;
        /// </summary>
        Task<ParsingResult> ParseWithParserAsync(
            string content,
            string parserName,
            ParserOptions options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Batch parses multiple content items;
        /// </summary>
        Task<IEnumerable<ParsingResult>> ParseBatchAsync(
            IEnumerable<ContentItem> contentItems,
            ParsingContext context = null,
            int? parallelism = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts structured data from content;
        /// </summary>
        Task<DataExtractionResult<T>> ExtractDataAsync<T>(
            string content,
            ExtractionRules extractionRules,
            CancellationToken cancellationToken = default)
            where T : class, new();

        /// <summary>
        /// Validates content against schema or rules;
        /// </summary>
        Task<ContentValidationResult> ValidateContentAsync(
            string content,
            ValidationRules validationRules,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Registers a custom parser;
        /// </summary>
        bool RegisterParser(IParser parser);

        /// <summary>
        /// Unregisters a parser;
        /// </summary>
        bool UnregisterParser(string parserName);

        /// <summary>
        /// Gets list of registered parsers;
        /// </summary>
        IEnumerable<ParserInfo> GetRegisteredParsers();

        /// <summary>
        /// Clears the parser cache;
        /// </summary>
        void ClearCache();

        /// <summary>
        /// Gets cache statistics;
        /// </summary>
        CacheStatistics GetCacheStatistics();

        /// <summary>
        /// Event raised when parsing starts;
        /// </summary>
        event EventHandler<ParserStartedEventArgs> ParserStarted;

        /// <summary>
        /// Event raised when parsing completes;
        /// </summary>
        event EventHandler<ParserCompletedEventArgs> ParserCompleted;

        /// <summary>
        /// Event raised when parsing fails;
        /// </summary>
        event EventHandler<ParserFailedEventArgs> ParserFailed;

        /// <summary>
        /// Event raised when content type is detected;
        /// </summary>
        event EventHandler<ContentTypeDetectedEventArgs> ContentTypeDetected;

        /// <summary>
        /// Event raised when parser is registered;
        /// </summary>
        event EventHandler<ParserRegisteredEventArgs> ParserRegistered;
    }

    /// <summary>
    /// Base interface for parsers;
    /// </summary>
    public interface IParser;
    {
        /// <summary>
        /// Parser name;
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Parser description;
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Parser version;
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Supported content types;
        /// </summary>
        IEnumerable<string> SupportedContentTypes { get; }

        /// <summary>
        /// Supported file extensions;
        /// </summary>
        IEnumerable<string> SupportedFileExtensions { get; }

        /// <summary>
        /// Parses content;
        /// </summary>
        Task<ParsingResult> ParseAsync(
            string content,
            ParserOptions options = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Interface for content validators;
    /// </summary>
    public interface IContentValidator;
    {
        /// <summary>
        /// Validates content;
        /// </summary>
        Task<ContentValidationResult> ValidateAsync(
            string content,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Parser configuration;
    /// </summary>
    public class ParserConfiguration;
    {
        public int? MaxConcurrentParsing { get; set; }
        public bool EnableCaching { get; set; } = true;
        public int? CacheSize { get; set; }
        public TimeSpan? CacheExpiration { get; set; }
        public bool AutoDetectContentType { get; set; } = true;
        public bool FallbackToTextParser { get; set; } = true;
        public TimeSpan? DefaultTimeout { get; set; }
        public bool EnablePerformanceLogging { get; set; } = true;
        public bool StoreParsedData { get; set; } = false;
    }

    /// <summary>
    /// Parsing context;
    /// </summary>
    public class ParsingContext : ICloneable;
    {
        public string ContentType { get; set; }
        public string FileExtension { get; set; }
        public string ParserName { get; set; }
        public string SourceUrl { get; set; }
        public string SourceEncoding { get; set; } = "UTF-8";
        public bool ExtractTextOnly { get; set; }
        public bool RemoveScripts { get; set; } = true;
        public bool RemoveStyles { get; set; } = false;
        public bool PreserveWhitespace { get; set; } = false;
        public bool ValidateSchema { get; set; } = false;
        public bool StoreParsedData { get; set; } = false;
        public bool ExtractionMode { get; set; } = false;
        public int? MaxDepth { get; set; }
        public TimeSpan? Timeout { get; set; }
        public ValidationOptions ValidationOptions { get; set; }
        public TransformationRules TransformationRules { get; set; }
        public ExtractionRules ExtractionRules { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// Parser options;
    /// </summary>
    public class ParserOptions;
    {
        public HtmlParsingOptions HtmlParsingOptions { get; set; }
        public XmlParsingOptions XmlParsingOptions { get; set; }
        public JsonParsingOptions JsonParsingOptions { get; set; }
        public TextParsingOptions TextParsingOptions { get; set; }
        public ValidationOptions ValidationOptions { get; set; }
        public TransformationRules TransformationRules { get; set; }
        public ExtractionRules ExtractionRules { get; set; }
        public TimeSpan? Timeout { get; set; }
        public bool ReturnDetailedErrors { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
    }

    /// <summary>
    /// HTML parsing options;
    /// </summary>
    public class HtmlParsingOptions;
    {
        public bool FixHtmlErrors { get; set; } = true;
        public bool RemoveScripts { get; set; } = true;
        public bool RemoveStyles { get; set; } = false;
        public bool RemoveComments { get; set; } = false;
        public bool ExtractTextOnly { get; set; } = false;
        public bool PreserveWhitespace { get; set; } = false;
        public bool LowercaseTags { get; set; } = false;
        public string[] PreservedTags { get; set; } = Array.Empty<string>();
        public string[] RemovedTags { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// XML parsing options;
    /// </summary>
    public class XmlParsingOptions;
    {
        public bool IgnoreComments { get; set; } = true;
        public bool IgnoreWhitespace { get; set; } = true;
        public bool LoadSchema { get; set; } = false;
        public bool ResolveEntities { get; set; } = true;
        public bool ValidateSchema { get; set; } = false;
        public bool PreserveNamespaces { get; set; } = true;
    }

    /// <summary>
    /// JSON parsing options;
    /// </summary>
    public class JsonParsingOptions;
    {
        public bool AllowTrailingCommas { get; set; } = true;
        public JsonCommentHandling CommentHandling { get; set; } = JsonCommentHandling.Skip;
        public int MaxDepth { get; set; } = 64;
        public bool PropertyNameCaseInsensitive { get; set; } = true;
        public bool WriteIndented { get; set; } = false;
    }

    /// <summary>
    /// Text parsing options;
    /// </summary>
    public class TextParsingOptions;
    {
        public Encoding Encoding { get; set; } = Encoding.UTF8;
        public bool DetectEncoding { get; set; } = true;
        public string LineEnding { get; set; } = "\n";
        public bool TrimLines { get; set; } = false;
        public bool RemoveEmptyLines { get; set; } = false;
        public int MaxLineLength { get; set; } = int.MaxValue;
    }

    /// <summary>
    /// Validation options;
    /// </summary>
    public class ValidationOptions;
    {
        public bool ValidateStructure { get; set; } = true;
        public bool ValidateSchema { get; set; } = false;
        public bool ValidateContent { get; set; } = false;
        public string SchemaPath { get; set; }
        public List<ValidationRule> Rules { get; set; } = new List<ValidationRule>();
    }

    /// <summary>
    /// Transformation rules;
    /// </summary>
    public class TransformationRules;
    {
        public List<TransformationRule> Rules { get; set; } = new List<TransformationRule>();
        public bool ApplyRecursively { get; set; } = true;
        public bool StopOnFirstMatch { get; set; } = false;
    }

    /// <summary>
    /// Extraction rules;
    /// </summary>
    public class ExtractionRules;
    {
        public List<Selector> Selectors { get; set; }
        public List<string> XPathQueries { get; set; }
        public List<string> JsonPaths { get; set; }
        public List<RegexPattern> RegexPatterns { get; set; }
        public Dictionary<string, string> FieldMappings { get; set; }
        public bool ExtractMultiple { get; set; } = true;
        public bool ReturnFirstMatchOnly { get; set; } = false;
    }

    /// <summary>
    /// Validation rules;
    /// </summary>
    public class ValidationRules;
    {
        public List<ValidationRule> Rules { get; set; } = new List<ValidationRule>();
        public string Schema { get; set; }
        public bool Strict { get; set; } = false;
        public bool AllowMultipleErrors { get; set; } = true;
    }

    /// <summary>
    /// Selector for content extraction;
    /// </summary>
    public class Selector;
    {
        public string CssSelector { get; set; }
        public string XPath { get; set; }
        public string Attribute { get; set; }
        public SelectorType Type { get; set; } = SelectorType.Text;
        public string TargetProperty { get; set; }
    }

    /// <summary>
    /// Selector type;
    /// </summary>
    public enum SelectorType;
    {
        Text,
        Html,
        Attribute,
        InnerHtml,
        OuterHtml;
    }

    /// <summary>
    /// Regex pattern for extraction;
    /// </summary>
    public class RegexPattern;
    {
        public string Pattern { get; set; }
        public RegexOptions Options { get; set; } = RegexOptions.None;
        public Dictionary<int, string> GroupMappings { get; set; }
    }

    /// <summary>
    /// Validation rule;
    /// </summary>
    public class ValidationRule;
    {
        public string Name { get; set; }
        public string Condition { get; set; }
        public string ErrorMessage { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
    }

    /// <summary>
    /// Transformation rule;
    /// </summary>
    public class TransformationRule;
    {
        public string SourcePattern { get; set; }
        public string TargetPattern { get; set; }
        public TransformationType Type { get; set; } = TransformationType.Replace;
        public bool CaseSensitive { get; set; } = false;
        public bool Global { get; set; } = true;
    }

    /// <summary>
    /// Transformation type;
    /// </summary>
    public enum TransformationType;
    {
        Replace,
        Remove,
        Append,
        Prepend,
        Wrap,
        Unwrap;
    }

    /// <summary>
    /// Validation severity;
    /// </summary>
    public enum ValidationSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    /// <summary>
    /// Parsing result;
    /// </summary>
    public class ParsingResult : ICloneable;
    {
        public bool Success { get; set; }
        public string OperationId { get; set; }
        public string BatchId { get; set; }
        public string ItemId { get; set; }
        public object ParsedData { get; set; }
        public string ContentType { get; set; }
        public string ParserName { get; set; }
        public TimeSpan ParsingTime { get; set; }
        public int SourceLength { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public bool FromCache { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// Content item for batch parsing;
    /// </summary>
    public class ContentItem;
    {
        public string Id { get; set; }
        public string Content { get; set; }
        public string ContentType { get; set; }
        public string FileExtension { get; set; }
        public string SourceUrl { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Data extraction result;
    /// </summary>
    public class DataExtractionResult<T> where T : class, new()
    {
        public bool Success { get; set; }
        public string OperationId { get; set; }
        public IEnumerable<T> ExtractedData { get; set; }
        public TimeSpan ExtractionTime { get; set; }
        public int SourceContentLength { get; set; }
        public ExtractionRules ExtractionRules { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Content validation result;
    /// </summary>
    public class ContentValidationResult;
    {
        public bool IsValid { get; set; }
        public string OperationId { get; set; }
        public string ContentType { get; set; }
        public List<ValidationError> Errors { get; set; }
        public TimeSpan ValidationTime { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Validation error;
    /// </summary>
    public class ValidationError;
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public ValidationSeverity Severity { get; set; }
        public string Location { get; set; }
        public int? LineNumber { get; set; }
        public int? ColumnNumber { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Parser information;
    /// </summary>
    public class ParserInfo;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public IEnumerable<string> SupportedContentTypes { get; set; }
        public IEnumerable<string> SupportedFileExtensions { get; set; }
        public string Version { get; set; }
    }

    /// <summary>
    /// Cache statistics;
    /// </summary>
    public class CacheStatistics;
    {
        public int TotalEntries { get; set; }
        public int ValidEntries { get; set; }
        public int ExpiredEntries { get; set; }
        public int MaxSize { get; set; }
        public long MemoryUsage { get; set; } // in bytes;
        public double HitRate { get; set; }
    }

    /// <summary>
    /// Parsed content record for storage;
    /// </summary>
    public class ParsedContentRecord;
    {
        public string OperationId { get; set; }
        public string SourceContent { get; set; }
        public object ParsedData { get; set; }
        public string ContentType { get; set; }
        public string ParserName { get; set; }
        public TimeSpan ParseTime { get; set; }
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Extracted data record for storage;
    /// </summary>
    public class ExtractedDataRecord<T> where T : class, new()
    {
        public string OperationId { get; set; }
        public string SourceContent { get; set; }
        public List<T> ExtractedData { get; set; }
        public string DataType { get; set; }
        public TimeSpan ExtractionTime { get; set; }
        public ExtractionRules ExtractionRules { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for parser start;
    /// </summary>
    public class ParserStartedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public int? ContentLength { get; }
        public ParsingContext Context { get; }

        public ParserStartedEventArgs(
            string operationId,
            int? contentLength,
            ParsingContext context)
        {
            OperationId = operationId;
            ContentLength = contentLength;
            Context = context;
        }
    }

    /// <summary>
    /// Event arguments for parser completion;
    /// </summary>
    public class ParserCompletedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public ParsingResult Result { get; }
        public TimeSpan Duration { get; }
        public bool Success { get; }

        public ParserCompletedEventArgs(
            string operationId,
            ParsingResult result,
            TimeSpan duration,
            bool success)
        {
            OperationId = operationId;
            Result = result;
            Duration = duration;
            Success = success;
        }
    }

    /// <summary>
    /// Event arguments for parser failure;
    /// </summary>
    public class ParserFailedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public Exception Exception { get; }
        public ParsingContext Context { get; }

        public ParserFailedEventArgs(
            string operationId,
            Exception exception,
            ParsingContext context)
        {
            OperationId = operationId;
            Exception = exception;
            Context = context;
        }
    }

    /// <summary>
    /// Event arguments for content type detection;
    /// </summary>
    public class ContentTypeDetectedEventArgs : EventArgs;
    {
        public string OperationId { get; }
        public string ContentType { get; }
        public int ContentLength { get; }

        public ContentTypeDetectedEventArgs(
            string operationId,
            string contentType,
            int contentLength)
        {
            OperationId = operationId;
            ContentType = contentType;
            ContentLength = contentLength;
        }
    }

    /// <summary>
    /// Event arguments for parser registration;
    /// </summary>
    public class ParserRegisteredEventArgs : EventArgs;
    {
        public string ParserName { get; }
        public IEnumerable<string> SupportedContentTypes { get; }
        public IEnumerable<string> SupportedFileExtensions { get; }

        public ParserRegisteredEventArgs(
            string parserName,
            IEnumerable<string> supportedContentTypes,
            IEnumerable<string> supportedFileExtensions)
        {
            ParserName = parserName;
            SupportedContentTypes = supportedContentTypes;
            SupportedFileExtensions = supportedFileExtensions;
        }
    }

    /// <summary>
    /// Custom exception for parsing engine errors;
    /// </summary>
    public class ParsingEngineException : Exception
    {
        public string OperationId { get; }
        public int ContentLength { get; }

        public ParsingEngineException(
            string message,
            Exception innerException,
            string operationId,
            int contentLength)
            : base(message, innerException)
        {
            OperationId = operationId;
            ContentLength = contentLength;
        }
    }

    /// <summary>
    /// Custom exception for parser not found;
    /// </summary>
    public class ParserNotFoundException : Exception
    {
        public string ParserName { get; }

        public ParserNotFoundException(string message)
            : base(message)
        {
        }

        public ParserNotFoundException(string message, string parserName)
            : base(message)
        {
            ParserName = parserName;
        }
    }

    /// <summary>
    /// Custom exception for data extraction errors;
    /// </summary>
    public class DataExtractionException : Exception
    {
        public string OperationId { get; }
        public Type TargetType { get; }

        public DataExtractionException(
            string message,
            string operationId,
            Type targetType)
            : base(message)
        {
            OperationId = operationId;
            TargetType = targetType;
        }

        public DataExtractionException(
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
    /// Custom exception for content validation errors;
    /// </summary>
    public class ContentValidationException : Exception
    {
        public string OperationId { get; }

        public ContentValidationException(
            string message,
            Exception innerException,
            string operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    #endregion;
}
