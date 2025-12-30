using Microsoft.Extensions.Logging;
using NEDA.Core.Common.Extensions;
using NEDA.Core.ExceptionHandling.ErrorReporting;
using NEDA.Core.Logging;
using NEDA.KnowledgeBase.Parsers;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using static NEDA.KnowledgeBase.WebScraper.DataExtractor;

namespace NEDA.KnowledgeBase.WebScraper;
{
    /// <summary>
    /// Gelişmiş veri çıkarıcı - HTML, XML, JSON ve metin verilerinden yapılandırılmış veri çıkarımı;
    /// </summary>
    public class DataExtractor : IDataExtractor;
    {
        #region Private Fields;
        private readonly ILogger<DataExtractor> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly IDataParser _dataParser;
        private readonly RegexOptions _regexOptions;
        private readonly ExtractorConfiguration _configuration;
        private static readonly Regex _htmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex _whitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex _emailRegex = new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b", RegexOptions.Compiled);
        private static readonly Regex _phoneRegex = new Regex(@"\b(?:\+?\d{1,3}[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled);
        private static readonly Regex _urlRegex = new Regex(@"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)", RegexOptions.Compiled);
        private static readonly Regex _dateRegex = new Regex(@"\b\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}\b|\b\d{4}[/\-.]\d{1,2}[/\-.]\d{1,2}\b", RegexOptions.Compiled);
        private static readonly Regex _numberRegex = new Regex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);
        private static readonly Regex _currencyRegex = new Regex(@"\$\s?\d+(?:,\d{3})*(?:\.\d{2})?|\d+(?:,\d{3})*(?:\.\d{2})?\s?\$", RegexOptions.Compiled);
        #endregion;

        #region Properties;
        /// <summary>
        /// Varsayılan encoding;
        /// </summary>
        public Encoding DefaultEncoding { get; set; } = Encoding.UTF8;

        /// <summary>
        /// Kültür bilgisi (tarih, para formatları için)
        /// </summary>
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

        /// <summary>
        /// HTML tag'lerini temizleme ayarı;
        /// </summary>
        public bool CleanHtmlTags { get; set; } = true;

        /// <summary>
        /// Fazla boşlukları temizleme ayarı;
        /// </summary>
        public bool NormalizeWhitespace { get; set; } = true;

        /// <summary>
        /// Metin uzunluğu sınırı;
        /// </summary>
        public int MaxTextLength { get; set; } = 10000;

        /// <summary>
        /// Minimum güven skoru (pattern matching için)
        /// </summary>
        public double MinConfidenceScore { get; set; } = 0.7;

        /// <summary>
        /// Derin çıkarım seviyesi (iç içe yapılar için)
        /// </summary>
        public int ExtractionDepth { get; set; } = 3;
        #endregion;

        #region Events;
        /// <summary>
        /// Çıkarım işlemi başladığında tetiklenir;
        /// </summary>
        public event EventHandler<ExtractionStartedEventArgs> ExtractionStarted;

        /// <summary>
        /// Çıkarım işlemi tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<ExtractionCompletedEventArgs> ExtractionCompleted;

        /// <summary>
        /// Çıkarım hatası oluştuğunda tetiklenir;
        /// </summary>
        public event EventHandler<ExtractionErrorEventArgs> ExtractionError;
        #endregion;

        #region Constructors;
        /// <summary>
        /// DataExtractor sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="errorReporter">Hata raporlama servisi</param>
        /// <param name="dataParser">Veri parser'ı</param>
        public DataExtractor(
            ILogger<DataExtractor> logger,
            IErrorReporter errorReporter,
            IDataParser dataParser)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _dataParser = dataParser ?? throw new ArgumentNullException(nameof(dataParser));

            _regexOptions = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled;
            _configuration = new ExtractorConfiguration();

            InitializeFromConfig();

            _logger.LogInformation("DataExtractor başlatıldı. Pattern sayısı: {PatternCount}", GetPatternCount());
        }

        /// <summary>
        /// Özel konfigürasyon ile DataExtractor sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public DataExtractor(
            ILogger<DataExtractor> logger,
            IErrorReporter errorReporter,
            ExtractorConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _dataParser = new DataParser(logger, errorReporter, new DataParser.ParserConfiguration());
            _regexOptions = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled;

            InitializeFromConfig();
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// HTML içinden CSS selektörü kullanarak veri çıkarır;
        /// </summary>
        /// <param name="html">HTML içeriği</param>
        /// <param name="cssSelector">CSS selektörü</param>
        /// <param name="attribute">Çıkarılacak attribute (null ise inner text)</param>
        /// <returns>Çıkarılan değerler listesi</returns>
        public List<string> ExtractFromHtml(string html, string cssSelector, string attribute = null)
        {
            if (string.IsNullOrWhiteSpace(html))
                throw new ArgumentException("HTML içeriği boş olamaz", nameof(html));
            if (string.IsNullOrWhiteSpace(cssSelector))
                throw new ArgumentException("CSS selektörü boş olamaz", nameof(cssSelector));

            try
            {
                OnExtractionStarted(new ExtractionStartedEventArgs;
                {
                    SourceType = "HTML",
                    Pattern = cssSelector,
                    SourceLength = html.Length;
                });

                _logger.LogDebug("HTML'den CSS selektör ile çıkarım başlatılıyor: {Selector}, Attribute={Attribute}",
                    cssSelector, attribute);

                // HTML'i yükle;
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                // CSS selektörü ile elementleri bul;
                var elements = doc.DocumentNode.SelectNodes(cssSelector);
                if (elements == null)
                {
                    _logger.LogWarning("CSS selektörü ile element bulunamadı: {Selector}", cssSelector);
                    return new List<string>();
                }

                var results = new List<string>();
                foreach (var element in elements)
                {
                    string value;

                    if (!string.IsNullOrEmpty(attribute))
                    {
                        // Attribute değerini al;
                        value = element.GetAttributeValue(attribute, string.Empty);
                    }
                    else;
                    {
                        // Inner text'i al;
                        value = element.InnerText.Trim();
                        if (CleanHtmlTags)
                        {
                            value = CleanHtml(value);
                        }
                        if (NormalizeWhitespace)
                        {
                            value = NormalizeSpaces(value);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        results.Add(value);
                    }
                }

                _logger.LogInformation("HTML'den çıkarım tamamlandı: {Selector}, {Count} öğe bulundu",
                    cssSelector, results.Count);

                OnExtractionCompleted(new ExtractionCompletedEventArgs;
                {
                    SourceType = "HTML",
                    Pattern = cssSelector,
                    ExtractedCount = results.Count,
                    Success = true;
                });

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTML çıkarımı başarısız: {Selector}", cssSelector);
                OnExtractionError(new ExtractionErrorEventArgs;
                {
                    SourceType = "HTML",
                    Pattern = cssSelector,
                    Error = ex,
                    SourceSample = html.Length > 200 ? html.Substring(0, 200) + "..." : html;
                });

                throw new ExtractionException($"HTML'den '{cssSelector}' ile veri çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// XPath kullanarak XML/HTML'den veri çıkarır;
        /// </summary>
        public List<string> ExtractWithXPath(string xml, string xpath, Dictionary<string, string> namespaces = null)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new ArgumentException("XML içeriği boş olamaz", nameof(xml));
            if (string.IsNullOrWhiteSpace(xpath))
                throw new ArgumentException("XPath ifadesi boş olamaz", nameof(xpath));

            try
            {
                OnExtractionStarted(new ExtractionStartedEventArgs;
                {
                    SourceType = "XML/XPath",
                    Pattern = xpath,
                    SourceLength = xml.Length;
                });

                // XML dokümanı oluştur;
                var doc = new XmlDocument();
                doc.LoadXml(xml);

                // Namespace manager oluştur;
                var nsManager = new XmlNamespaceManager(doc.NameTable);
                if (namespaces != null)
                {
                    foreach (var ns in namespaces)
                    {
                        nsManager.AddNamespace(ns.Key, ns.Value);
                    }
                }

                // XPath ile node'ları seç;
                var nodes = doc.SelectNodes(xpath, nsManager);
                if (nodes == null)
                {
                    _logger.LogWarning("XPath ile node bulunamadı: {XPath}", xpath);
                    return new List<string>();
                }

                var results = new List<string>();
                foreach (XmlNode node in nodes)
                {
                    string value = node.InnerText.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        results.Add(value);
                    }
                }

                _logger.LogInformation("XPath çıkarımı tamamlandı: {XPath}, {Count} öğe bulundu",
                    xpath, results.Count);

                OnExtractionCompleted(new ExtractionCompletedEventArgs;
                {
                    SourceType = "XML/XPath",
                    Pattern = xpath,
                    ExtractedCount = results.Count,
                    Success = true;
                });

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XPath çıkarımı başarısız: {XPath}", xpath);
                OnExtractionError(new ExtractionErrorEventArgs;
                {
                    SourceType = "XML/XPath",
                    Pattern = xpath,
                    Error = ex,
                    SourceSample = xml.Length > 200 ? xml.Substring(0, 200) + "..." : xml;
                });

                throw new ExtractionException($"XPath '{xpath}' ile veri çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// Regex pattern'i ile metinden veri çıkarır;
        /// </summary>
        public List<MatchResult> ExtractWithRegex(string text, string pattern, RegexOptions options = RegexOptions.None)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Metin boş olamaz", nameof(text));
            if (string.IsNullOrWhiteSpace(pattern))
                throw new ArgumentException("Regex pattern'i boş olamaz", nameof(pattern));

            try
            {
                OnExtractionStarted(new ExtractionStartedEventArgs;
                {
                    SourceType = "Text/Regex",
                    Pattern = pattern,
                    SourceLength = text.Length;
                });

                options |= _regexOptions;
                var regex = new Regex(pattern, options);
                var matches = regex.Matches(text);

                var results = new List<MatchResult>();
                foreach (Match match in matches)
                {
                    if (match.Success)
                    {
                        var matchResult = new MatchResult;
                        {
                            Value = match.Value,
                            Index = match.Index,
                            Length = match.Length,
                            Groups = new Dictionary<string, string>()
                        };

                        // Grup isimlerini al (eğer varsa)
                        foreach (Group group in match.Groups)
                        {
                            if (!string.IsNullOrEmpty(group.Name) && group.Name != "0")
                            {
                                matchResult.Groups[group.Name] = group.Value;
                            }
                        }

                        // Güven skoru hesapla (basit bir hesaplama)
                        matchResult.Confidence = CalculateMatchConfidence(match, pattern);

                        results.Add(matchResult);
                    }
                }

                _logger.LogInformation("Regex çıkarımı tamamlandı: {Pattern}, {Count} eşleşme bulundu",
                    pattern, results.Count);

                OnExtractionCompleted(new ExtractionCompletedEventArgs;
                {
                    SourceType = "Text/Regex",
                    Pattern = pattern,
                    ExtractedCount = results.Count,
                    Success = true;
                });

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Regex çıkarımı başarısız: {Pattern}", pattern);
                OnExtractionError(new ExtractionErrorEventArgs;
                {
                    SourceType = "Text/Regex",
                    Pattern = pattern,
                    Error = ex,
                    SourceSample = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                });

                throw new ExtractionException($"Regex pattern'i '{pattern}' ile veri çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// JSON'dan belirli bir path ile veri çıkarır;
        /// </summary>
        public List<object> ExtractFromJson(string json, string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON içeriği boş olamaz", nameof(json));
            if (string.IsNullOrWhiteSpace(jsonPath))
                throw new ArgumentException("JSON path boş olamaz", nameof(jsonPath));

            try
            {
                OnExtractionStarted(new ExtractionStartedEventArgs;
                {
                    SourceType = "JSON",
                    Pattern = jsonPath,
                    SourceLength = json.Length;
                });

                // JSON dokümanı oluştur;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var results = new List<object>();

                // JSON path'i işle (basit implementasyon)
                // Daha gelişmiş bir JSON path parser kullanılabilir;
                ExtractJsonPath(doc.RootElement, jsonPath, results);

                _logger.LogInformation("JSON çıkarımı tamamlandı: {Path}, {Count} öğe bulundu",
                    jsonPath, results.Count);

                OnExtractionCompleted(new ExtractionCompletedEventArgs;
                {
                    SourceType = "JSON",
                    Pattern = jsonPath,
                    ExtractedCount = results.Count,
                    Success = true;
                });

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JSON çıkarımı başarısız: {Path}", jsonPath);
                OnExtractionError(new ExtractionErrorEventArgs;
                {
                    SourceType = "JSON",
                    Pattern = jsonPath,
                    Error = ex,
                    SourceSample = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
                });

                throw new ExtractionException($"JSON path '{jsonPath}' ile veri çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// Yapılandırılmış metinden tablo verisi çıkarır;
        /// </summary>
        public List<Dictionary<string, string>> ExtractTable(string text, TableExtractionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Metin boş olamaz", nameof(text));

            options ??= new TableExtractionOptions();

            try
            {
                OnExtractionStarted(new ExtractionStartedEventArgs;
                {
                    SourceType = "Text/Table",
                    Pattern = "Table Extraction",
                    SourceLength = text.Length;
                });

                var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2)
                {
                    return new List<Dictionary<string, string>>();
                }

                var tableData = new List<Dictionary<string, string>>();
                string[] headers = null;

                // Başlık satırını bul;
                int startLine = 0;
                if (options.HasHeaderRow)
                {
                    headers = ParseTableRow(lines[0], options.ColumnSeparator);
                    startLine = 1;
                }
                else;
                {
                    // Başlık yoksa sütun numaralarını kullan;
                    var firstRow = ParseTableRow(lines[0], options.ColumnSeparator);
                    headers = Enumerable.Range(1, firstRow.Length).Select(i => $"Column{i}").ToArray();
                }

                // Veri satırlarını işle;
                for (int i = startLine; i < lines.Length; i++)
                {
                    var rowValues = ParseTableRow(lines[i], options.ColumnSeparator);
                    if (rowValues.Length == headers.Length)
                    {
                        var rowDict = new Dictionary<string, string>();
                        for (int j = 0; j < headers.Length; j++)
                        {
                            rowDict[headers[j]] = CleanCellValue(rowValues[j], options);
                        }
                        tableData.Add(rowDict);
                    }
                }

                _logger.LogInformation("Tablo çıkarımı tamamlandı: {Rows} satır, {Columns} sütun",
                    tableData.Count, headers.Length);

                OnExtractionCompleted(new ExtractionCompletedEventArgs;
                {
                    SourceType = "Text/Table",
                    Pattern = "Table Extraction",
                    ExtractedCount = tableData.Count,
                    Success = true;
                });

                return tableData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tablo çıkarımı başarısız");
                OnExtractionError(new ExtractionErrorEventArgs;
                {
                    SourceType = "Text/Table",
                    Pattern = "Table Extraction",
                    Error = ex,
                    SourceSample = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                });

                throw new ExtractionException("Tablo verisi çıkarılamadı", ex);
            }
        }

        /// <summary>
        /// Metinden otomatik olarak veri tiplerini çıkarır (email, telefon, tarih vb.)
        /// </summary>
        public Dictionary<string, List<ExtractedEntity>> ExtractEntities(string text, EntityExtractionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Metin boş olamaz", nameof(text));

            options ??= new EntityExtractionOptions();
            var entities = new Dictionary<string, List<ExtractedEntity>>();

            try
            {
                OnExtractionStarted(new ExtractionStartedEventArgs;
                {
                    SourceType = "Text/Entities",
                    Pattern = "Entity Extraction",
                    SourceLength = text.Length;
                });

                // E-posta adresleri;
                if (options.ExtractEmails)
                {
                    var emails = ExtractWithRegex(text, _emailRegex.ToString(), RegexOptions.IgnoreCase);
                    entities["Email"] = emails.Select(m => new ExtractedEntity;
                    {
                        Value = m.Value,
                        Type = "Email",
                        Confidence = m.Confidence,
                        Position = m.Index;
                    }).ToList();
                }

                // Telefon numaraları;
                if (options.ExtractPhones)
                {
                    var phones = ExtractWithRegex(text, _phoneRegex.ToString());
                    entities["Phone"] = phones.Select(m => new ExtractedEntity;
                    {
                        Value = m.Value,
                        Type = "Phone",
                        Confidence = m.Confidence,
                        Position = m.Index;
                    }).ToList();
                }

                // URL'ler;
                if (options.ExtractUrls)
                {
                    var urls = ExtractWithRegex(text, _urlRegex.ToString(), RegexOptions.IgnoreCase);
                    entities["URL"] = urls.Select(m => new ExtractedEntity;
                    {
                        Value = m.Value,
                        Type = "URL",
                        Confidence = m.Confidence,
                        Position = m.Index;
                    }).ToList();
                }

                // Tarihler;
                if (options.ExtractDates)
                {
                    var dates = ExtractWithRegex(text, _dateRegex.ToString());
                    entities["Date"] = dates.Select(m => new ExtractedEntity;
                    {
                        Value = m.Value,
                        Type = "Date",
                        Confidence = m.Confidence,
                        Position = m.Index;
                    }).ToList();
                }

                // Para miktarları;
                if (options.ExtractCurrencies)
                {
                    var currencies = ExtractWithRegex(text, _currencyRegex.ToString());
                    entities["Currency"] = currencies.Select(m => new ExtractedEntity;
                    {
                        Value = m.Value,
                        Type = "Currency",
                        Confidence = m.Confidence,
                        Position = m.Index;
                    }).ToList();
                }

                // Sayılar;
                if (options.ExtractNumbers)
                {
                    var numbers = ExtractWithRegex(text, _numberRegex.ToString());
                    entities["Number"] = numbers.Select(m => new ExtractedEntity;
                    {
                        Value = m.Value,
                        Type = "Number",
                        Confidence = m.Confidence,
                        Position = m.Index;
                    }).ToList();
                }

                _logger.LogInformation("Varlık çıkarımı tamamlandı: {EntityTypes} tip bulundu",
                    string.Join(", ", entities.Keys));

                OnExtractionCompleted(new ExtractionCompletedEventArgs;
                {
                    SourceType = "Text/Entities",
                    Pattern = "Entity Extraction",
                    ExtractedCount = entities.Sum(e => e.Value.Count),
                    Success = true;
                });

                return entities;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varlık çıkarımı başarısız");
                OnExtractionError(new ExtractionErrorEventArgs;
                {
                    SourceType = "Text/Entities",
                    Pattern = "Entity Extraction",
                    Error = ex,
                    SourceSample = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                });

                throw new ExtractionException("Varlık çıkarımı başarısız", ex);
            }
        }

        /// <summary>
        /// Metni temizler ve normalize eder;
        /// </summary>
        public string CleanText(string text, TextCleaningOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            options ??= new TextCleaningOptions();
            var cleaned = text;

            try
            {
                // HTML tag'lerini temizle;
                if (options.RemoveHtmlTags && CleanHtmlTags)
                {
                    cleaned = CleanHtml(cleaned);
                }

                // Fazla boşlukları temizle;
                if (options.NormalizeWhitespace && NormalizeWhitespace)
                {
                    cleaned = NormalizeSpaces(cleaned);
                }

                // Satır sonlarını normalize et;
                if (options.NormalizeLineEndings)
                {
                    cleaned = cleaned.Replace("\r\n", "\n").Replace("\r", "\n");
                }

                // Özel karakterleri temizle;
                if (options.RemoveSpecialCharacters)
                {
                    cleaned = RemoveSpecialCharacters(cleaned, options.AllowedCharacters);
                }

                // Unicode karakterleri normalize et;
                if (options.NormalizeUnicode)
                {
                    cleaned = cleaned.Normalize(NormalizationForm.FormC);
                }

                // Trim uygula;
                if (options.Trim)
                {
                    cleaned = cleaned.Trim();
                }

                // Uzunluk sınırı;
                if (options.MaxLength > 0 && cleaned.Length > options.MaxLength)
                {
                    cleaned = cleaned.Substring(0, options.MaxLength);
                    if (options.AddEllipsis)
                    {
                        cleaned += "...";
                    }
                }

                return cleaned;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metin temizleme başarısız");
                return text; // Orijinal metni döndür;
            }
        }

        /// <summary>
        /// Metinden anahtar kelime çıkarır;
        /// </summary>
        public List<KeywordResult> ExtractKeywords(string text, int topN = 10, KeywordExtractionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<KeywordResult>();

            options ??= new KeywordExtractionOptions();
            var cleanedText = CleanText(text, new TextCleaningOptions;
            {
                RemoveHtmlTags = true,
                NormalizeWhitespace = true,
                RemoveSpecialCharacters = true,
                NormalizeUnicode = true,
                Trim = true;
            });

            try
            {
                // Stop words listesi;
                var stopWords = options.StopWords ?? GetDefaultStopWords();

                // Metni kelimelere ayır;
                var words = cleanedText;
                    .ToLowerInvariant()
                    .Split(new[] { ' ', '.', ',', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}' },
                           StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= options.MinWordLength)
                    .Where(w => !stopWords.Contains(w))
                    .Where(w => !options.ExcludePatterns.Any(p => Regex.IsMatch(w, p)))
                    .ToArray();

                // Kelime frekanslarını hesapla;
                var frequency = new Dictionary<string, int>();
                foreach (var word in words)
                {
                    if (frequency.ContainsKey(word))
                        frequency[word]++;
                    else;
                        frequency[word] = 1;
                }

                // TF-IDF benzeri skorlama (basit versiyon)
                var totalWords = words.Length;
                var results = frequency.Select(kvp => new KeywordResult;
                {
                    Keyword = kvp.Key,
                    Frequency = kvp.Value,
                    Score = CalculateKeywordScore(kvp.Key, kvp.Value, totalWords, options)
                })
                .OrderByDescending(k => k.Score)
                .ThenByDescending(k => k.Frequency)
                .Take(topN)
                .ToList();

                _logger.LogInformation("Anahtar kelime çıkarımı tamamlandı: {Count} kelime", results.Count);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anahtar kelime çıkarımı başarısız");
                return new List<KeywordResult>();
            }
        }

        /// <summary>
        /// Asenkron olarak dosyadan veri çıkarır;
        /// </summary>
        public async Task<ExtractionResult> ExtractFromFileAsync(
            string filePath,
            ExtractionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Dosya yolu boş olamaz", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Dosya bulunamadı: {filePath}", filePath);

            try
            {
                var fileInfo = new FileInfo(filePath);
                _logger.LogInformation("Dosyadan çıkarım başlatılıyor: {File}, Size={Size}",
                    filePath, fileInfo.Length.FormatBytes());

                string content;
                using (var reader = new StreamReader(filePath, DefaultEncoding))
                {
                    content = await reader.ReadToEndAsync(cancellationToken);
                }

                // Dosya tipine göre çıkarım yap;
                var result = new ExtractionResult;
                {
                    FileName = Path.GetFileName(filePath),
                    FileSize = fileInfo.Length,
                    ExtractionTime = DateTime.UtcNow;
                };

                if (options.ExtractText)
                {
                    result.TextContent = CleanText(content, options.TextCleaningOptions);
                }

                if (options.ExtractEntities)
                {
                    result.Entities = ExtractEntities(content, options.EntityExtractionOptions);
                }

                if (options.ExtractKeywords)
                {
                    result.Keywords = ExtractKeywords(content, options.KeywordCount, options.KeywordExtractionOptions);
                }

                if (!string.IsNullOrEmpty(options.CustomPattern))
                {
                    result.CustomMatches = ExtractWithRegex(content, options.CustomPattern);
                }

                _logger.LogInformation("Dosyadan çıkarım tamamlandı: {File}, Results={ResultCount}",
                    filePath, result.GetResultCount());

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dosyadan çıkarım başarısız: {FilePath}", filePath);
                throw new ExtractionException($"'{filePath}' dosyasından veri çıkarılamadı", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private void InitializeFromConfig()
        {
            if (_configuration != null)
            {
                DefaultEncoding = _configuration.DefaultEncoding ?? DefaultEncoding;
                Culture = _configuration.Culture ?? Culture;
                CleanHtmlTags = _configuration.CleanHtmlTags;
                NormalizeWhitespace = _configuration.NormalizeWhitespace;
                MaxTextLength = _configuration.MaxTextLength;
                MinConfidenceScore = _configuration.MinConfidenceScore;
                ExtractionDepth = _configuration.ExtractionDepth;
            }
        }

        private int GetPatternCount()
        {
            // Built-in regex pattern'ları say;
            return 7; // email, phone, url, date, number, currency, html tag;
        }

        private string CleanHtml(string html)
        {
            return _htmlTagRegex.Replace(html, " ");
        }

        private string NormalizeSpaces(string text)
        {
            return _whitespaceRegex.Replace(text.Trim(), " ");
        }

        private string RemoveSpecialCharacters(string text, string allowedChars = null)
        {
            if (string.IsNullOrEmpty(allowedChars))
            {
                allowedChars = @"a-zA-Z0-9\s\.\,\-\+\*\?\!@#\$%&\(\)\[\]\{\}\<\>\=\|\/\:;";
            }

            var pattern = $"[^{allowedChars}]";
            return Regex.Replace(text, pattern, " ");
        }

        private string[] ParseTableRow(string line, string separator)
        {
            if (string.IsNullOrEmpty(separator))
            {
                separator = @"\s{2,}"; // Varsayılan: 2 veya daha fazla boşluk;
            }

            return Regex.Split(line, separator)
                       .Select(cell => cell.Trim(' ', '\t', '"', '\''))
                       .ToArray();
        }

        private string CleanCellValue(string value, TableExtractionOptions options)
        {
            var cleaned = value;

            if (options.CleanCellValues)
            {
                cleaned = CleanHtml(cleaned);
                cleaned = NormalizeSpaces(cleaned);
            }

            if (options.ConvertEmptyToNull && string.IsNullOrWhiteSpace(cleaned))
            {
                return null;
            }

            return cleaned;
        }

        private void ExtractJsonPath(System.Text.Json.JsonElement element, string path, List<object> results, int depth = 0)
        {
            if (depth > ExtractionDepth)
                return;

            // Basit JSON path parsing (örnek: "users[0].name")
            if (path.Contains('.'))
            {
                var parts = path.Split('.');
                var currentPart = parts[0];

                if (currentPart.Contains('[') && currentPart.Contains(']'))
                {
                    // Dizi index'i;
                    var match = Regex.Match(currentPart, @"^(.+)\[(\d+)\]$");
                    if (match.Success)
                    {
                        var arrayName = match.Groups[1].Value;
                        var index = int.Parse(match.Groups[2].Value);

                        if (element.TryGetProperty(arrayName, out var arrayElement) && arrayElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var array = arrayElement.EnumerateArray().ToArray();
                            if (index < array.Length)
                            {
                                var remainingPath = string.Join(".", parts.Skip(1));
                                ExtractJsonPath(array[index], remainingPath, results, depth + 1);
                            }
                        }
                    }
                }
                else;
                {
                    // Özellik;
                    if (element.TryGetProperty(currentPart, out var propertyElement))
                    {
                        var remainingPath = string.Join(".", parts.Skip(1));
                        ExtractJsonPath(propertyElement, remainingPath, results, depth + 1);
                    }
                }
            }
            else;
            {
                // Leaf node;
                results.Add(GetJsonValue(element));
            }
        }

        private object GetJsonValue(System.Text.Json.JsonElement element)
        {
            return element.ValueKind switch;
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out long longValue) ? longValue :
                                                         element.TryGetDouble(out double doubleValue) ? doubleValue :
                                                         (object)element.GetRawText(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
                System.Text.Json.JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                    p => p.Name, p => GetJsonValue(p.Value)),
                _ => element.GetRawText()
            };
        }

        private double CalculateMatchConfidence(Match match, string pattern)
        {
            // Basit güven skoru hesaplaması;
            double baseScore = 0.5;

            // Uzunluk faktörü (çok kısa eşleşmeler daha az güvenilir)
            if (match.Length > 5)
                baseScore += 0.2;

            // Grup sayısı faktörü;
            if (match.Groups.Count > 1)
                baseScore += 0.1;

            // Pattern karmaşıklığı (regex meta karakter sayısı)
            var metaCharCount = pattern.Count(c => @"\.*+?^${}()|[]".Contains(c));
            if (metaCharCount > 3)
                baseScore += 0.2;

            return Math.Min(baseScore, 1.0);
        }

        private double CalculateKeywordScore(string word, int frequency, int totalWords, KeywordExtractionOptions options)
        {
            // Basit TF (Term Frequency) skoru;
            double tf = (double)frequency / totalWords;

            // Kelime uzunluğu faktörü;
            double lengthFactor = Math.Min(word.Length / 10.0, 1.0);

            // Özel faktörler;
            double specialFactor = 1.0;
            if (options.PriorityWords?.Contains(word) == true)
                specialFactor *= 1.5;

            return tf * lengthFactor * specialFactor;
        }

        private HashSet<string> GetDefaultStopWords()
        {
            return new HashSet<string>
            {
                "the", "and", "a", "an", "in", "on", "at", "to", "for", "of", "with", "by",
                "is", "are", "was", "were", "be", "been", "being", "have", "has", "had",
                "do", "does", "did", "will", "would", "should", "could", "can", "may",
                "might", "must", "i", "you", "he", "she", "it", "we", "they", "me", "him",
                "her", "us", "them", "my", "your", "his", "its", "our", "their", "this",
                "that", "these", "those", "am", "or", "but", "not", "so", "then", "just"
            };
        }

        private void OnExtractionStarted(ExtractionStartedEventArgs e)
        {
            ExtractionStarted?.Invoke(this, e);
        }

        private void OnExtractionCompleted(ExtractionCompletedEventArgs e)
        {
            ExtractionCompleted?.Invoke(this, e);
        }

        private void OnExtractionError(ExtractionErrorEventArgs e)
        {
            ExtractionError?.Invoke(this, e);
        }
        #endregion;

        #region Nested Types;
        /// <summary>
        /// Çıkarım konfigürasyon sınıfı;
        /// </summary>
        public class ExtractorConfiguration;
        {
            public Encoding DefaultEncoding { get; set; } = Encoding.UTF8;
            public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
            public bool CleanHtmlTags { get; set; } = true;
            public bool NormalizeWhitespace { get; set; } = true;
            public int MaxTextLength { get; set; } = 10000;
            public double MinConfidenceScore { get; set; } = 0.7;
            public int ExtractionDepth { get; set; } = 3;
            public Dictionary<string, object> CustomPatterns { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Regex eşleşme sonucu;
        /// </summary>
        public class MatchResult;
        {
            public string Value { get; set; }
            public int Index { get; set; }
            public int Length { get; set; }
            public Dictionary<string, string> Groups { get; set; }
            public double Confidence { get; set; }
        }

        /// <summary>
        /// Çıkarılan varlık;
        /// </summary>
        public class ExtractedEntity;
        {
            public string Value { get; set; }
            public string Type { get; set; }
            public double Confidence { get; set; }
            public int Position { get; set; }
        }

        /// <summary>
        /// Anahtar kelime sonucu;
        /// </summary>
        public class KeywordResult;
        {
            public string Keyword { get; set; }
            public int Frequency { get; set; }
            public double Score { get; set; }
        }

        /// <summary>
        /// Tablo çıkarımı seçenekleri;
        /// </summary>
        public class TableExtractionOptions;
        {
            public bool HasHeaderRow { get; set; } = true;
            public string ColumnSeparator { get; set; } = @"\s{2,}";
            public bool CleanCellValues { get; set; } = true;
            public bool ConvertEmptyToNull { get; set; } = true;
        }

        /// <summary>
        /// Varlık çıkarımı seçenekleri;
        /// </summary>
        public class EntityExtractionOptions;
        {
            public bool ExtractEmails { get; set; } = true;
            public bool ExtractPhones { get; set; } = true;
            public bool ExtractUrls { get; set; } = true;
            public bool ExtractDates { get; set; } = true;
            public bool ExtractCurrencies { get; set; } = true;
            public bool ExtractNumbers { get; set; } = true;
            public double MinConfidence { get; set; } = 0.7;
        }

        /// <summary>
        /// Metin temizleme seçenekleri;
        /// </summary>
        public class TextCleaningOptions;
        {
            public bool RemoveHtmlTags { get; set; } = true;
            public bool NormalizeWhitespace { get; set; } = true;
            public bool NormalizeLineEndings { get; set; } = true;
            public bool RemoveSpecialCharacters { get; set; } = false;
            public string AllowedCharacters { get; set; }
            public bool NormalizeUnicode { get; set; } = true;
            public bool Trim { get; set; } = true;
            public int MaxLength { get; set; } = 0;
            public bool AddEllipsis { get; set; } = true;
        }

        /// <summary>
        /// Anahtar kelime çıkarımı seçenekleri;
        /// </summary>
        public class KeywordExtractionOptions;
        {
            public int MinWordLength { get; set; } = 3;
            public HashSet<string> StopWords { get; set; }
            public List<string> PriorityWords { get; set; }
            public List<string> ExcludePatterns { get; set; } = new List<string>();
        }

        /// <summary>
        /// Çıkarım seçenekleri;
        /// </summary>
        public class ExtractionOptions;
        {
            public bool ExtractText { get; set; } = true;
            public bool ExtractEntities { get; set; } = true;
            public bool ExtractKeywords { get; set; } = true;
            public int KeywordCount { get; set; } = 10;
            public string CustomPattern { get; set; }
            public TextCleaningOptions TextCleaningOptions { get; set; } = new TextCleaningOptions();
            public EntityExtractionOptions EntityExtractionOptions { get; set; } = new EntityExtractionOptions();
            public KeywordExtractionOptions KeywordExtractionOptions { get; set; } = new KeywordExtractionOptions();
        }

        /// <summary>
        /// Çıkarım sonucu;
        /// </summary>
        public class ExtractionResult;
        {
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public DateTime ExtractionTime { get; set; }
            public string TextContent { get; set; }
            public Dictionary<string, List<ExtractedEntity>> Entities { get; set; }
            public List<KeywordResult> Keywords { get; set; }
            public List<MatchResult> CustomMatches { get; set; }

            public int GetResultCount()
            {
                int count = 0;
                if (!string.IsNullOrEmpty(TextContent)) count++;
                if (Entities != null) count += Entities.Count;
                if (Keywords != null) count += Keywords.Count;
                if (CustomMatches != null) count += CustomMatches.Count;
                return count;
            }
        }

        /// <summary>
        /// Çıkarım başlangıç olayı argümanları;
        /// </summary>
        public class ExtractionStartedEventArgs : EventArgs;
        {
            public string SourceType { get; set; }
            public string Pattern { get; set; }
            public int SourceLength { get; set; }
            public DateTime StartTime { get; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Çıkarım tamamlanma olayı argümanları;
        /// </summary>
        public class ExtractionCompletedEventArgs : EventArgs;
        {
            public string SourceType { get; set; }
            public string Pattern { get; set; }
            public int ExtractedCount { get; set; }
            public bool Success { get; set; }
            public TimeSpan Duration { get; set; }
            public DateTime EndTime { get; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Çıkarım hatası olayı argümanları;
        /// </summary>
        public class ExtractionErrorEventArgs : EventArgs;
        {
            public string SourceType { get; set; }
            public string Pattern { get; set; }
            public Exception Error { get; set; }
            public string SourceSample { get; set; }
            public DateTime ErrorTime { get; } = DateTime.UtcNow;
        }
        #endregion;
    }

    #region Supporting Types and Interfaces;
    /// <summary>
    /// Data extractor interface'i;
    /// </summary>
    public interface IDataExtractor;
    {
        List<string> ExtractFromHtml(string html, string cssSelector, string attribute = null);
        List<string> ExtractWithXPath(string xml, string xpath, Dictionary<string, string> namespaces = null);
        List<MatchResult> ExtractWithRegex(string text, string pattern, RegexOptions options = RegexOptions.None);
        List<object> ExtractFromJson(string json, string jsonPath);
        List<Dictionary<string, string>> ExtractTable(string text, TableExtractionOptions options = null);
        Dictionary<string, List<ExtractedEntity>> ExtractEntities(string text, EntityExtractionOptions options = null);
        string CleanText(string text, TextCleaningOptions options = null);
        List<KeywordResult> ExtractKeywords(string text, int topN = 10, KeywordExtractionOptions options = null);
        Task<ExtractionResult> ExtractFromFileAsync(string filePath, ExtractionOptions options, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Çıkarım istisna sınıfı;
    /// </summary>
    public class ExtractionException : Exception
    {
        public ExtractionException(string message) : base(message) { }
        public ExtractionException(string message, Exception innerException) : base(message, innerException) { }

        public string Pattern { get; set; }
        public string SourceType { get; set; }
    }

    /// <summary>
    /// DataExtractor için extension metotlar;
    /// </summary>
    public static class DataExtractorExtensions;
    {
        /// <summary>
        /// Tek bir değer çıkarır (ilk eşleşen)
        /// </summary>
        public static string ExtractSingle(this IDataExtractor extractor, string html, string cssSelector, string attribute = null)
        {
            var results = extractor.ExtractFromHtml(html, cssSelector, attribute);
            return results.FirstOrDefault();
        }

        /// <summary>
        /// Birden fazla CSS selektörü ile çıkarım yapar;
        /// </summary>
        public static Dictionary<string, List<string>> ExtractMultiple(
            this IDataExtractor extractor,
            string html,
            Dictionary<string, string> selectors)
        {
            var results = new Dictionary<string, List<string>>();
            foreach (var selector in selectors)
            {
                results[selector.Key] = extractor.ExtractFromHtml(html, selector.Value);
            }
            return results;
        }

        /// <summary>
        /// Metinden tüm e-posta adreslerini çıkarır;
        /// </summary>
        public static List<string> ExtractAllEmails(this IDataExtractor extractor, string text)
        {
            var entities = extractor.ExtractEntities(text, new DataExtractor.EntityExtractionOptions;
            {
                ExtractEmails = true,
                ExtractPhones = false,
                ExtractUrls = false,
                ExtractDates = false,
                ExtractCurrencies = false,
                ExtractNumbers = false;
            });

            return entities.ContainsKey("Email")
                ? entities["Email"].Select(e => e.Value).ToList()
                : new List<string>();
        }

        /// <summary>
        /// Metinden tüm telefon numaralarını çıkarır;
        /// </summary>
        public static List<string> ExtractAllPhones(this IDataExtractor extractor, string text)
        {
            var entities = extractor.ExtractEntities(text, new DataExtractor.EntityExtractionOptions;
            {
                ExtractEmails = false,
                ExtractPhones = true,
                ExtractUrls = false,
                ExtractDates = false,
                ExtractCurrencies = false,
                ExtractNumbers = false;
            });

            return entities.ContainsKey("Phone")
                ? entities["Phone"].Select(e => e.Value).ToList()
                : new List<string>();
        }
    }
    #endregion;
}
