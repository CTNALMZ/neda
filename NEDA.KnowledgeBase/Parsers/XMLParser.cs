using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using System.Xml.Serialization;
using System.IO;
using System.Text.RegularExpressions;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;

namespace NEDA.KnowledgeBase.Parsers;
{
    /// <summary>
    /// XML belgelerini ayrıştırmak ve işlemek için gelişmiş XML parser motoru;
    /// </summary>
    public class XMLParser : IXMLParser, IDisposable;
    {
        #region Fields;
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly XmlReaderSettings _readerSettings;
        private readonly XmlWriterSettings _writerSettings;
        private readonly XmlSchemaSet _schemaSet;
        private readonly Dictionary<string, XDocument> _cachedDocuments;
        private readonly Dictionary<string, XmlNamespaceManager> _namespaceManagers;
        private readonly object _cacheLock = new object();
        private bool _isDisposed;
        #endregion;

        #region Properties;
        public string ParserId { get; private set; }
        public XMLParserStatus Status { get; private set; }
        public ParserConfiguration Configuration { get; private set; }
        public int CachedDocumentsCount => _cachedDocuments.Count;
        public long TotalBytesParsed { get; private set; }
        public long TotalDocumentsParsed { get; private set; }
        public event EventHandler<ParseCompletedEventArgs> OnParseCompleted;
        #endregion;

        #region Constructor;
        public XMLParser(
            ILogger logger,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            ParserId = Guid.NewGuid().ToString();
            Status = XMLParserStatus.Stopped;

            _cachedDocuments = new Dictionary<string, XDocument>();
            _namespaceManagers = new Dictionary<string, XmlNamespaceManager>();

            // XML reader ayarlarını yapılandır;
            _readerSettings = new XmlReaderSettings;
            {
                Async = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore,
                ValidationType = ValidationType.None,
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                MaxCharactersFromEntities = 1024 * 1024, // 1MB;
                MaxCharactersInDocument = 10 * 1024 * 1024 // 10MB;
            };

            // XML writer ayarlarını yapılandır;
            _writerSettings = new XmlWriterSettings;
            {
                Async = true,
                Encoding = Encoding.UTF8,
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                NewLineOnAttributes = false,
                OmitXmlDeclaration = false,
                ConformanceLevel = ConformanceLevel.Document,
                CheckCharacters = true,
                WriteEndDocumentOnClose = true;
            };

            // Schema set oluştur;
            _schemaSet = new XmlSchemaSet();

            _logger.LogInformation($"XML Parser initialized with ID: {ParserId}");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// XML Parser'ı başlatır;
        /// </summary>
        public async Task<OperationResult> InitializeAsync(ParserConfiguration configuration = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (Status == XMLParserStatus.Running)
                {
                    _logger.LogWarning("XML Parser is already initialized");
                    return OperationResult.Failure("XML Parser is already initialized");
                }

                _logger.LogInformation("Initializing XML Parser...");
                Status = XMLParserStatus.Initializing;

                // Konfigürasyonu yükle veya varsayılanı kullan;
                Configuration = configuration ?? GetDefaultConfiguration();

                // Reader ayarlarını güncelle;
                UpdateReaderSettings();

                // Schema'ları yükle;
                await LoadSchemasAsync(cancellationToken);

                // Cache'i temizle;
                ClearCache();

                Status = XMLParserStatus.Running;

                _logger.LogInformation("XML Parser initialized successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize XML Parser");
                Status = XMLParserStatus.Error;
                return OperationResult.Failure($"Initialization failed: {ex.Message}");
            }
        }

        /// <summary>
        /// XML string'ini parse eder;
        /// </summary>
        public async Task<ParseResult> ParseStringAsync(string xmlString, ParseOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlString))
                {
                    return ParseResult.Failure("XML string cannot be null or empty");
                }

                _logger.LogDebug($"Parsing XML string (Length: {xmlString.Length})");

                var bytes = Encoding.UTF8.GetBytes(xmlString);
                using var stream = new MemoryStream(bytes);

                return await ParseStreamAsync(stream, options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse XML string");
                return ParseResult.Failure($"Parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML dosyasını parse eder;
        /// </summary>
        public async Task<ParseResult> ParseFileAsync(string filePath, ParseOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return ParseResult.Failure($"File not found: {filePath}");
                }

                _logger.LogInformation($"Parsing XML file: {filePath}");

                var fileInfo = new FileInfo(filePath);

                // Dosya boyutu kontrolü;
                if (fileInfo.Length > Configuration.MaxFileSize)
                {
                    return ParseResult.Failure($"File size ({fileInfo.Length} bytes) exceeds maximum allowed size ({Configuration.MaxFileSize} bytes)");
                }

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                return await ParseStreamAsync(stream, options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to parse XML file: {filePath}");
                return ParseResult.Failure($"Parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML stream'ini parse eder;
        /// </summary>
        public async Task<ParseResult> ParseStreamAsync(Stream stream, ParseOptions options = null, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            XDocument document = null;

            try
            {
                if (stream == null)
                {
                    return ParseResult.Failure("Stream cannot be null");
                }

                if (!stream.CanRead)
                {
                    return ParseResult.Failure("Stream is not readable");
                }

                _logger.LogDebug("Parsing XML stream");

                var parseOptions = options ?? ParseOptions.Default;
                var readerSettings = GetReaderSettings(parseOptions);

                // Cache kontrolü;
                if (parseOptions.UseCache && parseOptions.CacheKey != null)
                {
                    document = GetCachedDocument(parseOptions.CacheKey);
                    if (document != null)
                    {
                        _logger.LogDebug($"Using cached XML document: {parseOptions.CacheKey}");
                        return CreateSuccessResult(document, startTime, true);
                    }
                }

                // XML belgesini yükle;
                using (var reader = XmlReader.Create(stream, readerSettings))
                {
                    document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
                }

                // Validation yap;
                if (parseOptions.ValidateSchema && _schemaSet.Count > 0)
                {
                    var validationResult = ValidateDocument(document, parseOptions.SchemaName);
                    if (!validationResult.IsValid)
                    {
                        return ParseResult.Failure($"Schema validation failed: {validationResult.ErrorMessage}", validationResult.Errors);
                    }
                }

                // İstatistikleri güncelle;
                UpdateStatistics(stream.Length);

                // Cache'e ekle;
                if (parseOptions.UseCache && parseOptions.CacheKey != null)
                {
                    CacheDocument(parseOptions.CacheKey, document);
                }

                // Event tetikle;
                OnParseCompleted?.Invoke(this, new ParseCompletedEventArgs;
                {
                    ParseTime = DateTime.UtcNow - startTime,
                    DocumentSize = stream.Length,
                    IsCached = false,
                    Timestamp = DateTime.UtcNow;
                });

                return CreateSuccessResult(document, startTime, false);
            }
            catch (XmlException xmlEx)
            {
                _logger.LogError(xmlEx, "XML parsing error");
                return ParseResult.Failure($"XML parsing error: {xmlEx.Message}", xmlEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error parsing XML stream");
                return ParseResult.Failure($"Unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// XPath sorgusu çalıştırır;
        /// </summary>
        public async Task<XPathResult> ExecuteXPathAsync(string xmlContent, string xpath, XPathOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return XPathResult.Failure("XML content cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(xpath))
                {
                    return XPathResult.Failure("XPath expression cannot be null or empty");
                }

                _logger.LogDebug($"Executing XPath: {xpath}");

                var parseResult = await ParseStringAsync(xmlContent, new ParseOptions { UseCache = false }, cancellationToken);
                if (!parseResult.Success)
                {
                    return XPathResult.Failure($"Failed to parse XML: {parseResult.ErrorMessage}");
                }

                return ExecuteXPath(parseResult.Document, xpath, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing XPath: {xpath}");
                return XPathResult.Failure($"XPath execution error: {ex.Message}");
            }
        }

        /// <summary>
        /// XDocument üzerinde XPath sorgusu çalıştırır;
        /// </summary>
        public XPathResult ExecuteXPath(XDocument document, string xpath, XPathOptions options = null)
        {
            try
            {
                if (document == null)
                {
                    return XPathResult.Failure("Document cannot be null");
                }

                if (string.IsNullOrWhiteSpace(xpath))
                {
                    return XPathResult.Failure("XPath expression cannot be null or empty");
                }

                var xpathOptions = options ?? XPathOptions.Default;
                var navigator = document.CreateNavigator();

                if (navigator == null)
                {
                    return XPathResult.Failure("Failed to create XPath navigator");
                }

                // Namespace manager oluştur;
                var namespaceManager = GetNamespaceManager(document, xpathOptions.Namespaces);

                // XPath'i compile et (performans için)
                var expression = navigator.Compile(xpath);
                expression.SetContext(namespaceManager);

                // Sorguyu çalıştır;
                var result = navigator.Evaluate(expression);

                return ProcessXPathResult(result, xpath);
            }
            catch (XPathException xpathEx)
            {
                _logger.LogError(xpathEx, $"XPath error: {xpath}");
                return XPathResult.Failure($"XPath error: {xpathEx.Message}", xpathEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing XPath: {xpath}");
                return XPathResult.Failure($"Unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML'i başka bir formata dönüştürür;
        /// </summary>
        public async Task<TransformResult> TransformAsync(string xmlContent, string xsltContent, TransformOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return TransformResult.Failure("XML content cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(xsltContent))
                {
                    return TransformResult.Failure("XSLT content cannot be null or empty");
                }

                _logger.LogDebug("Transforming XML with XSLT");

                // XSLT yükleyici oluştur;
                var xsltSettings = new System.Xml.Xsl.XsltSettings(true, true);
                var resolver = new XmlUrlResolver();

                using (var xsltReader = XmlReader.Create(new StringReader(xsltContent)))
                using (var xmlReader = XmlReader.Create(new StringReader(xmlContent)))
                {
                    var transform = new System.Xml.Xsl.XslCompiledTransform();

                    try
                    {
                        // XSLT'yi compile et;
                        transform.Load(xsltReader, xsltSettings, resolver);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to compile XSLT");
                        return TransformResult.Failure($"XSLT compilation failed: {ex.Message}");
                    }

                    // Transformasyonu uygula;
                    using (var output = new StringWriter())
                    {
                        var writerSettings = transform.OutputSettings?.Clone() ?? new XmlWriterSettings();

                        if (options?.OutputSettings != null)
                        {
                            ApplyOutputSettings(writerSettings, options.OutputSettings);
                        }

                        using (var writer = XmlWriter.Create(output, writerSettings))
                        {
                            transform.Transform(xmlReader, null, writer);
                        }

                        var result = output.ToString();

                        _logger.LogDebug($"Transformation completed, output length: {result.Length}");

                        return TransformResult.Success(result);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XML transformation failed");
                return TransformResult.Failure($"Transformation error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML'i C# objesine dönüştürür (Deserialization)
        /// </summary>
        public async Task<DeserializationResult<T>> DeserializeAsync<T>(string xmlContent, DeserializationOptions options = null, CancellationToken cancellationToken = default) where T : class, new()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return DeserializationResult<T>.Failure("XML content cannot be null or empty");
                }

                _logger.LogDebug($"Deserializing XML to type: {typeof(T).Name}");

                var serializer = new XmlSerializer(typeof(T));

                using (var reader = new StringReader(xmlContent))
                {
                    var result = (T)serializer.Deserialize(reader);

                    _logger.LogDebug($"Deserialization successful for type: {typeof(T).Name}");

                    return DeserializationResult<T>.Success(result);
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, $"Deserialization failed for type: {typeof(T).Name}");
                return DeserializationResult<T>.Failure($"Deserialization error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error during deserialization: {typeof(T).Name}");
                return DeserializationResult<T>.Failure($"Unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// C# objesini XML'e dönüştürür (Serialization)
        /// </summary>
        public async Task<SerializationResult> SerializeAsync<T>(T obj, SerializationOptions options = null, CancellationToken cancellationToken = default) where T : class;
        {
            try
            {
                if (obj == null)
                {
                    return SerializationResult.Failure("Object cannot be null");
                }

                _logger.LogDebug($"Serializing object of type: {typeof(T).Name}");

                var serializer = new XmlSerializer(typeof(T));
                var settings = options?.WriterSettings ?? _writerSettings;

                using (var writer = new StringWriter())
                using (var xmlWriter = XmlWriter.Create(writer, settings))
                {
                    // Namespace'leri ekle;
                    if (options?.Namespaces != null)
                    {
                        var ns = new XmlSerializerNamespaces();
                        foreach (var nsPair in options.Namespaces)
                        {
                            ns.Add(nsPair.Key, nsPair.Value);
                        }
                        serializer.Serialize(xmlWriter, obj, ns);
                    }
                    else;
                    {
                        serializer.Serialize(xmlWriter, obj);
                    }

                    var result = writer.ToString();

                    _logger.LogDebug($"Serialization completed, XML length: {result.Length}");

                    return SerializationResult.Success(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Serialization failed for type: {typeof(T).Name}");
                return SerializationResult.Failure($"Serialization error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML'i doğrular (Schema validation)
        /// </summary>
        public async Task<ValidationResult> ValidateAsync(string xmlContent, string schemaName = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return ValidationResult.Failure("XML content cannot be null or empty");
                }

                _logger.LogDebug("Validating XML content");

                var parseResult = await ParseStringAsync(xmlContent, new ParseOptions { UseCache = false }, cancellationToken);
                if (!parseResult.Success)
                {
                    return ValidationResult.Failure($"Failed to parse XML: {parseResult.ErrorMessage}");
                }

                return ValidateDocument(parseResult.Document, schemaName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XML validation failed");
                return ValidationResult.Failure($"Validation error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML'den belirli bir değeri çıkarır;
        /// </summary>
        public async Task<ExtractionResult> ExtractValueAsync(string xmlContent, string xpath, ExtractionOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return ExtractionResult.Failure("XML content cannot be null or empty");
                }

                if (string.IsNullOrWhiteSpace(xpath))
                {
                    return ExtractionResult.Failure("XPath expression cannot be null or empty");
                }

                _logger.LogDebug($"Extracting value with XPath: {xpath}");

                var xpathResult = await ExecuteXPathAsync(xmlContent, xpath, new XPathOptions { ReturnType = XPathResultType.Value }, cancellationToken);
                if (!xpathResult.Success)
                {
                    return ExtractionResult.Failure($"XPath execution failed: {xpathResult.ErrorMessage}");
                }

                var value = xpathResult.Result?.ToString();

                if (string.IsNullOrWhiteSpace(value))
                {
                    return ExtractionResult.Failure("No value found at specified XPath");
                }

                // Value'yu işle;
                var processedValue = ProcessExtractedValue(value, options);

                return ExtractionResult.Success(processedValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Value extraction failed for XPath: {xpath}");
                return ExtractionResult.Failure($"Extraction error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML'den birden fazla değer çıkarır;
        /// </summary>
        public async Task<BatchExtractionResult> ExtractValuesAsync(string xmlContent, Dictionary<string, string> xpathMappings, ExtractionOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return BatchExtractionResult.Failure("XML content cannot be null or empty");
                }

                if (xpathMappings == null || xpathMappings.Count == 0)
                {
                    return BatchExtractionResult.Failure("XPath mappings cannot be null or empty");
                }

                _logger.LogDebug($"Extracting {xpathMappings.Count} values from XML");

                var parseResult = await ParseStringAsync(xmlContent, new ParseOptions { UseCache = true }, cancellationToken);
                if (!parseResult.Success)
                {
                    return BatchExtractionResult.Failure($"Failed to parse XML: {parseResult.ErrorMessage}");
                }

                var results = new Dictionary<string, object>();
                var errors = new Dictionary<string, string>();

                foreach (var mapping in xpathMappings)
                {
                    try
                    {
                        var xpathResult = ExecuteXPath(parseResult.Document, mapping.Value, new XPathOptions { ReturnType = XPathResultType.Value });

                        if (xpathResult.Success && xpathResult.Result != null)
                        {
                            var value = ProcessExtractedValue(xpathResult.Result.ToString(), options);
                            results[mapping.Key] = value;
                        }
                        else;
                        {
                            errors[mapping.Key] = xpathResult.ErrorMessage ?? "No value found";
                        }
                    }
                    catch (Exception ex)
                    {
                        errors[mapping.Key] = $"Extraction error: {ex.Message}";
                    }
                }

                return BatchExtractionResult.Success(results, errors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch value extraction failed");
                return BatchExtractionResult.Failure($"Batch extraction error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML ağacını dolaşır ve işler;
        /// </summary>
        public async Task<TraversalResult> TraverseAsync(string xmlContent, Func<XElement, bool> nodeProcessor, TraversalOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return TraversalResult.Failure("XML content cannot be null or empty");
                }

                if (nodeProcessor == null)
                {
                    return TraversalResult.Failure("Node processor cannot be null");
                }

                _logger.LogDebug("Traversing XML tree");

                var parseResult = await ParseStringAsync(xmlContent, new ParseOptions { UseCache = false }, cancellationToken);
                if (!parseResult.Success)
                {
                    return TraversalResult.Failure($"Failed to parse XML: {parseResult.ErrorMessage}");
                }

                var traversalOptions = options ?? TraversalOptions.Default;
                var statistics = new TraversalStatistics();

                // Ağacı dolaş;
                TraverseElement(parseResult.Document.Root, nodeProcessor, traversalOptions, statistics, 0);

                _logger.LogDebug($"Traversal completed: {statistics.NodesProcessed} nodes processed, {statistics.NodesSkipped} nodes skipped");

                return TraversalResult.Success(statistics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XML tree traversal failed");
                return TraversalResult.Failure($"Traversal error: {ex.Message}");
            }
        }

        /// <summary>
        /// XML'i normalize eder (format, encoding, vb.)
        /// </summary>
        public async Task<NormalizationResult> NormalizeAsync(string xmlContent, NormalizationOptions options = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    return NormalizationResult.Failure("XML content cannot be null or empty");
                }

                _logger.LogDebug("Normalizing XML content");

                var parseResult = await ParseStringAsync(xmlContent, new ParseOptions { UseCache = false }, cancellationToken);
                if (!parseResult.Success)
                {
                    return NormalizationResult.Failure($"Failed to parse XML: {parseResult.ErrorMessage}");
                }

                var normalizationOptions = options ?? NormalizationOptions.Default;
                var document = parseResult.Document;

                // Normalizasyon işlemlerini uygula;
                if (normalizationOptions.RemoveComments)
                {
                    RemoveComments(document);
                }

                if (normalizationOptions.RemoveProcessingInstructions)
                {
                    RemoveProcessingInstructions(document);
                }

                if (normalizationOptions.NormalizeWhitespace)
                {
                    NormalizeWhitespace(document);
                }

                if (normalizationOptions.SortAttributes)
                {
                    SortAttributes(document);
                }

                if (normalizationOptions.StandardizeEncoding)
                {
                    StandardizeEncoding(document);
                }

                // Normalize edilmiş XML'i string olarak döndür;
                var normalizedXml = document.ToString();

                _logger.LogDebug($"Normalization completed, output length: {normalizedXml.Length}");

                return NormalizationResult.Success(normalizedXml);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XML normalization failed");
                return NormalizationResult.Failure($"Normalization error: {ex.Message}");
            }
        }

        /// <summary>
        /// Parser metriklerini alır;
        /// </summary>
        public ParserMetrics GetMetrics()
        {
            return new ParserMetrics;
            {
                ParserId = ParserId,
                Status = Status,
                CachedDocumentsCount = CachedDocumentsCount,
                TotalBytesParsed = TotalBytesParsed,
                TotalDocumentsParsed = TotalDocumentsParsed,
                CacheHitRate = CalculateCacheHitRate(),
                AverageParseTime = CalculateAverageParseTime(),
                SchemaCount = _schemaSet.Count,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Cache'i temizler;
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachedDocuments.Clear();
                _namespaceManagers.Clear();
                _logger.LogInformation("XML Parser cache cleared");
            }
        }
        #endregion;

        #region Private Methods;
        private ParserConfiguration GetDefaultConfiguration()
        {
            return new ParserConfiguration;
            {
                MaxFileSize = 50 * 1024 * 1024, // 50MB;
                EnableCaching = true,
                CacheDuration = TimeSpan.FromMinutes(30),
                MaxCacheSize = 100 * 1024 * 1024, // 100MB;
                ValidationEnabled = true,
                SchemaValidationEnabled = true,
                EntityExpansionLimit = 1000,
                MaxDepth = 100,
                MaxCharacters = 10 * 1024 * 1024, // 10MB;
                DefaultEncoding = Encoding.UTF8,
                PreserveWhitespace = false;
            };
        }

        private void UpdateReaderSettings()
        {
            if (Configuration == null) return;

            _readerSettings.IgnoreWhitespace = !Configuration.PreserveWhitespace;
            _readerSettings.MaxCharactersInDocument = Configuration.MaxCharacters;
            _readerSettings.MaxCharactersFromEntities = Configuration.EntityExpansionLimit;

            if (Configuration.ValidationEnabled)
            {
                _readerSettings.ValidationType = Configuration.SchemaValidationEnabled ?
                    ValidationType.Schema : ValidationType.DTD;
                _readerSettings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;

                if (Configuration.Schemas != null)
                {
                    foreach (var schema in Configuration.Schemas)
                    {
                        using (var reader = XmlReader.Create(new StringReader(schema)))
                        {
                            _schemaSet.Add(null, reader);
                        }
                    }
                }
            }
        }

        private async Task LoadSchemasAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Configuration?.SchemaFiles == null) return;

                foreach (var schemaFile in Configuration.SchemaFiles)
                {
                    if (File.Exists(schemaFile))
                    {
                        using (var reader = XmlReader.Create(schemaFile))
                        {
                            var schema = XmlSchema.Read(reader, null);
                            _schemaSet.Add(schema);
                        }

                        _logger.LogDebug($"Loaded schema: {schemaFile}");
                    }
                }

                _schemaSet.Compile();
                _logger.LogInformation($"Loaded and compiled {Configuration.SchemaFiles.Count} schemas");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load schemas");
                throw;
            }
        }

        private XmlReaderSettings GetReaderSettings(ParseOptions options)
        {
            var settings = _readerSettings.Clone();

            if (options != null)
            {
                settings.IgnoreComments = options.IgnoreComments;
                settings.IgnoreProcessingInstructions = options.IgnoreProcessingInstructions;
                settings.IgnoreWhitespace = !options.PreserveWhitespace;
                settings.ValidationType = options.ValidateSchema ? ValidationType.Schema : ValidationType.None;

                if (options.ValidationEventHandler != null)
                {
                    settings.ValidationEventHandler += options.ValidationEventHandler;
                }
            }

            return settings;
        }

        private ParseResult CreateSuccessResult(XDocument document, DateTime startTime, bool isCached)
        {
            return new ParseResult;
            {
                Success = true,
                Document = document,
                ParseTime = DateTime.UtcNow - startTime,
                IsCached = isCached,
                DocumentSize = document.ToString().Length,
                RootElementName = document.Root?.Name.LocalName,
                Namespaces = GetDocumentNamespaces(document)
            };
        }

        private Dictionary<string, string> GetDocumentNamespaces(XDocument document)
        {
            var namespaces = new Dictionary<string, string>();

            if (document?.Root == null) return namespaces;

            foreach (var attr in document.Root.Attributes())
            {
                if (attr.IsNamespaceDeclaration)
                {
                    namespaces[attr.Name.LocalName] = attr.Value;
                }
            }

            return namespaces;
        }

        private ValidationResult ValidateDocument(XDocument document, string schemaName = null)
        {
            var errors = new List<ValidationError>();
            var isValid = true;

            try
            {
                if (_schemaSet.Count == 0) return ValidationResult.Success();

                document.Validate(_schemaSet, (sender, args) =>
                {
                    isValid = false;
                    errors.Add(new ValidationError;
                    {
                        Severity = args.Severity,
                        Message = args.Message,
                        LineNumber = args.Exception?.LineNumber ?? 0,
                        LinePosition = args.Exception?.LinePosition ?? 0;
                    });
                }, schemaName);

                if (isValid)
                {
                    return ValidationResult.Success();
                }
                else;
                {
                    return ValidationResult.Failure("Schema validation failed", errors);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Document validation error");
                return ValidationResult.Failure($"Validation error: {ex.Message}");
            }
        }

        private XDocument GetCachedDocument(string cacheKey)
        {
            lock (_cacheLock)
            {
                return _cachedDocuments.GetValueOrDefault(cacheKey);
            }
        }

        private void CacheDocument(string cacheKey, XDocument document)
        {
            try
            {
                lock (_cacheLock)
                {
                    // Cache boyutu kontrolü;
                    if (_cachedDocuments.Count >= Configuration.MaxCacheItems)
                    {
                        var oldestKey = _cachedDocuments.Keys.First();
                        _cachedDocuments.Remove(oldestKey);
                        _logger.LogDebug($"Removed oldest cache entry: {oldestKey}");
                    }

                    _cachedDocuments[cacheKey] = document;
                    _logger.LogDebug($"Cached document: {cacheKey}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache document");
            }
        }

        private XmlNamespaceManager GetNamespaceManager(XDocument document, Dictionary<string, string> additionalNamespaces = null)
        {
            var docHash = document.GetHashCode().ToString();
            var nsKey = additionalNamespaces != null ?
                $"{docHash}_{string.Join(",", additionalNamespaces)}" :
                docHash.ToString();

            lock (_cacheLock)
            {
                if (_namespaceManagers.TryGetValue(nsKey, out var manager))
                {
                    return manager;
                }

                var nameTable = new NameTable();
                manager = new XmlNamespaceManager(nameTable);

                // Document namespaces'ını ekle;
                var docNamespaces = GetDocumentNamespaces(document);
                foreach (var ns in docNamespaces)
                {
                    manager.AddNamespace(ns.Key, ns.Value);
                }

                // Ek namespaces'ları ekle;
                if (additionalNamespaces != null)
                {
                    foreach (var ns in additionalNamespaces)
                    {
                        if (!manager.HasNamespace(ns.Key))
                        {
                            manager.AddNamespace(ns.Key, ns.Value);
                        }
                    }
                }

                // Cache'e ekle;
                _namespaceManagers[nsKey] = manager;

                return manager;
            }
        }

        private XPathResult ProcessXPathResult(object result, string xpath)
        {
            if (result == null)
            {
                return XPathResult.Failure($"No result for XPath: {xpath}");
            }

            var resultType = result.GetType();

            if (resultType == typeof(string))
            {
                return XPathResult.Success(result.ToString(), XPathResultType.Value);
            }
            else if (resultType == typeof(double))
            {
                return XPathResult.Success(result, XPathResultType.Number);
            }
            else if (resultType == typeof(bool))
            {
                return XPathResult.Success(result, XPathResultType.Boolean);
            }
            else if (result is XPathNodeIterator iterator)
            {
                var nodes = new List<XPathNavigator>();
                while (iterator.MoveNext())
                {
                    nodes.Add(iterator.Current.Clone());
                }

                return XPathResult.Success(nodes, XPathResultType.NodeSet);
            }
            else;
            {
                return XPathResult.Success(result, XPathResultType.Any);
            }
        }

        private object ProcessExtractedValue(string value, ExtractionOptions options)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            var extractionOptions = options ?? ExtractionOptions.Default;
            var processedValue = value;

            // Trim yap;
            if (extractionOptions.TrimWhitespace)
            {
                processedValue = processedValue.Trim();
            }

            // Normalize whitespace;
            if (extractionOptions.NormalizeWhitespace)
            {
                processedValue = Regex.Replace(processedValue, @"\s+", " ");
            }

            // Type conversion;
            if (extractionOptions.TargetType != null)
            {
                try
                {
                    if (extractionOptions.TargetType == typeof(int))
                        return int.Parse(processedValue);
                    else if (extractionOptions.TargetType == typeof(long))
                        return long.Parse(processedValue);
                    else if (extractionOptions.TargetType == typeof(double))
                        return double.Parse(processedValue);
                    else if (extractionOptions.TargetType == typeof(bool))
                        return bool.Parse(processedValue);
                    else if (extractionOptions.TargetType == typeof(DateTime))
                        return DateTime.Parse(processedValue);
                }
                catch
                {
                    // Conversion failed, return string;
                }
            }

            return processedValue;
        }

        private void TraverseElement(XElement element, Func<XElement, bool> processor, TraversalOptions options, TraversalStatistics statistics, int depth)
        {
            if (element == null) return;

            // Maximum depth kontrolü;
            if (options.MaxDepth > 0 && depth > options.MaxDepth)
            {
                statistics.NodesSkipped++;
                return;
            }

            // Node'u işle;
            var shouldContinue = processor(element);
            statistics.NodesProcessed++;

            if (!shouldContinue && options.StopOnFalseReturn)
            {
                return;
            }

            // Child'ları dolaş;
            if (options.TraverseChildren)
            {
                foreach (var child in element.Elements())
                {
                    TraverseElement(child, processor, options, statistics, depth + 1);
                }
            }
        }

        private void RemoveComments(XDocument document)
        {
            var comments = document.DescendantNodes().OfType<XComment>().ToList();
            foreach (var comment in comments)
            {
                comment.Remove();
            }
        }

        private void RemoveProcessingInstructions(XDocument document)
        {
            var instructions = document.DescendantNodes().OfType<XProcessingInstruction>().ToList();
            foreach (var instruction in instructions)
            {
                instruction.Remove();
            }
        }

        private void NormalizeWhitespace(XDocument document)
        {
            // Text node'lardaki whitespace'leri normalize et;
            var textNodes = document.DescendantNodes().OfType<XText>().ToList();
            foreach (var textNode in textNodes)
            {
                if (textNode.Parent != null && textNode.Parent.Name.LocalName != "pre")
                {
                    var normalized = Regex.Replace(textNode.Value, @"\s+", " ");
                    textNode.Value = normalized.Trim();
                }
            }
        }

        private void SortAttributes(XDocument document)
        {
            var elements = document.Descendants().ToList();
            foreach (var element in elements)
            {
                var sortedAttributes = element.Attributes().OrderBy(a => a.Name.LocalName).ToList();
                element.RemoveAttributes();
                element.Add(sortedAttributes);
            }
        }

        private void StandardizeEncoding(XDocument document)
        {
            // Encoding declaration'ını standart hale getir;
            if (document.Declaration == null)
            {
                document.Declaration = new XDeclaration("1.0", "UTF-8", "yes");
            }
            else;
            {
                document.Declaration.Encoding = "UTF-8";
            }
        }

        private void ApplyOutputSettings(XmlWriterSettings writerSettings, OutputSettings outputSettings)
        {
            if (outputSettings.Indent.HasValue)
                writerSettings.Indent = outputSettings.Indent.Value;

            if (outputSettings.IndentChars != null)
                writerSettings.IndentChars = outputSettings.IndentChars;

            if (outputSettings.NewLineChars != null)
                writerSettings.NewLineChars = outputSettings.NewLineChars;

            if (outputSettings.OmitXmlDeclaration.HasValue)
                writerSettings.OmitXmlDeclaration = outputSettings.OmitXmlDeclaration.Value;

            if (outputSettings.Encoding != null)
                writerSettings.Encoding = outputSettings.Encoding;
        }

        private void UpdateStatistics(long bytesParsed)
        {
            TotalBytesParsed += bytesParsed;
            TotalDocumentsParsed++;
        }

        private double CalculateCacheHitRate()
        {
            if (TotalDocumentsParsed == 0) return 0;

            // Örnek cache hit rate hesaplama;
            // Gerçek implementasyonda daha detaylı tracking gerekir;
            var cacheHits = TotalDocumentsParsed - (TotalDocumentsParsed - CachedDocumentsCount);
            return (double)cacheHits / TotalDocumentsParsed;
        }

        private TimeSpan CalculateAverageParseTime()
        {
            // Örnek ortalama parse süresi;
            // Gerçek implementasyonda timing verisi toplanmalı;
            return TimeSpan.FromMilliseconds(50);
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
                    ClearCache();
                    _schemaSet?.Clear();
                    Status = XMLParserStatus.Disposed;
                    _logger.LogInformation("XML Parser disposed");
                }

                _isDisposed = true;
            }
        }

        ~XMLParser()
        {
            Dispose(false);
        }
        #endregion;

        #region Supporting Classes and Enums;
        public enum XMLParserStatus;
        {
            Stopped,
            Initializing,
            Running,
            Error,
            Disposed;
        }

        public enum XPathResultType;
        {
            Value,
            NodeSet,
            Boolean,
            Number,
            Any;
        }

        public class ParserConfiguration;
        {
            public long MaxFileSize { get; set; }
            public bool EnableCaching { get; set; }
            public TimeSpan CacheDuration { get; set; }
            public long MaxCacheSize { get; set; }
            public int MaxCacheItems { get; set; } = 1000;
            public bool ValidationEnabled { get; set; }
            public bool SchemaValidationEnabled { get; set; }
            public List<string> Schemas { get; set; } = new List<string>();
            public List<string> SchemaFiles { get; set; } = new List<string>();
            public int EntityExpansionLimit { get; set; }
            public int MaxDepth { get; set; }
            public long MaxCharacters { get; set; }
            public Encoding DefaultEncoding { get; set; }
            public bool PreserveWhitespace { get; set; }
        }

        public class ParseOptions;
        {
            public static ParseOptions Default => new ParseOptions();

            public bool UseCache { get; set; } = true;
            public string CacheKey { get; set; }
            public bool ValidateSchema { get; set; } = true;
            public string SchemaName { get; set; }
            public bool IgnoreComments { get; set; } = true;
            public bool IgnoreProcessingInstructions { get; set; } = true;
            public bool PreserveWhitespace { get; set; } = false;
            public ValidationEventHandler ValidationEventHandler { get; set; }
        }

        public class XPathOptions;
        {
            public static XPathOptions Default => new XPathOptions();

            public Dictionary<string, string> Namespaces { get; set; } = new Dictionary<string, string>();
            public XPathResultType ReturnType { get; set; } = XPathResultType.NodeSet;
        }

        public class ExtractionOptions;
        {
            public static ExtractionOptions Default => new ExtractionOptions();

            public bool TrimWhitespace { get; set; } = true;
            public bool NormalizeWhitespace { get; set; } = true;
            public Type TargetType { get; set; }
            public string DefaultValue { get; set; }
        }

        public class TraversalOptions;
        {
            public static TraversalOptions Default => new TraversalOptions();

            public bool TraverseChildren { get; set; } = true;
            public bool StopOnFalseReturn { get; set; } = false;
            public int MaxDepth { get; set; } = 0; // 0 = unlimited;
        }

        public class NormalizationOptions;
        {
            public static NormalizationOptions Default => new NormalizationOptions();

            public bool RemoveComments { get; set; } = true;
            public bool RemoveProcessingInstructions { get; set; } = true;
            public bool NormalizeWhitespace { get; set; } = true;
            public bool SortAttributes { get; set; } = true;
            public bool StandardizeEncoding { get; set; } = true;
        }

        public class OutputSettings;
        {
            public bool? Indent { get; set; }
            public string IndentChars { get; set; }
            public string NewLineChars { get; set; }
            public bool? OmitXmlDeclaration { get; set; }
            public Encoding Encoding { get; set; }
        }

        public class ValidationError;
        {
            public XmlSeverityType Severity { get; set; }
            public string Message { get; set; }
            public int LineNumber { get; set; }
            public int LinePosition { get; set; }
        }

        public class TraversalStatistics;
        {
            public int NodesProcessed { get; set; }
            public int NodesSkipped { get; set; }
            public int MaxDepthReached { get; set; }
            public TimeSpan TraversalTime { get; set; }
        }

        public class ParserMetrics;
        {
            public string ParserId { get; set; }
            public XMLParserStatus Status { get; set; }
            public int CachedDocumentsCount { get; set; }
            public long TotalBytesParsed { get; set; }
            public long TotalDocumentsParsed { get; set; }
            public double CacheHitRate { get; set; }
            public TimeSpan AverageParseTime { get; set; }
            public int SchemaCount { get; set; }
            public DateTime Timestamp { get; set; }
        }
        #endregion;
    }

    #region Interfaces;
    public interface IXMLParser : IDisposable
    {
        Task<OperationResult> InitializeAsync(ParserConfiguration configuration = null, CancellationToken cancellationToken = default);
        Task<ParseResult> ParseStringAsync(string xmlString, ParseOptions options = null, CancellationToken cancellationToken = default);
        Task<ParseResult> ParseFileAsync(string filePath, ParseOptions options = null, CancellationToken cancellationToken = default);
        Task<ParseResult> ParseStreamAsync(Stream stream, ParseOptions options = null, CancellationToken cancellationToken = default);
        Task<XPathResult> ExecuteXPathAsync(string xmlContent, string xpath, XPathOptions options = null, CancellationToken cancellationToken = default);
        XPathResult ExecuteXPath(XDocument document, string xpath, XPathOptions options = null);
        Task<TransformResult> TransformAsync(string xmlContent, string xsltContent, TransformOptions options = null, CancellationToken cancellationToken = default);
        Task<DeserializationResult<T>> DeserializeAsync<T>(string xmlContent, DeserializationOptions options = null, CancellationToken cancellationToken = default) where T : class, new();
        Task<SerializationResult> SerializeAsync<T>(T obj, SerializationOptions options = null, CancellationToken cancellationToken = default) where T : class;
        Task<ValidationResult> ValidateAsync(string xmlContent, string schemaName = null, CancellationToken cancellationToken = default);
        Task<ExtractionResult> ExtractValueAsync(string xmlContent, string xpath, ExtractionOptions options = null, CancellationToken cancellationToken = default);
        Task<BatchExtractionResult> ExtractValuesAsync(string xmlContent, Dictionary<string, string> xpathMappings, ExtractionOptions options = null, CancellationToken cancellationToken = default);
        Task<TraversalResult> TraverseAsync(string xmlContent, Func<XElement, bool> nodeProcessor, TraversalOptions options = null, CancellationToken cancellationToken = default);
        Task<NormalizationResult> NormalizeAsync(string xmlContent, NormalizationOptions options = null, CancellationToken cancellationToken = default);
        ParserMetrics GetMetrics();
        void ClearCache();

        event EventHandler<ParseCompletedEventArgs> OnParseCompleted;
    }
    #endregion;
}
