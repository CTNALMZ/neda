using Google.Apis.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Logging;
using Quartz.Impl.AdoJobStore.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static NEDA.Interface.ResponseGenerator.MultilingualSupport.Translator;

namespace NEDA.Interface.ResponseGenerator.MultilingualSupport;
{
    /// <summary>
    /// Gelişmiş çeviri ve yerelleştirme motoru - Ana dil Türkçe, çoklu dil desteği;
    /// Advanced translation and localization engine - Primary language Turkish, multi-language support;
    /// </summary>
    public class Translator : ITranslator, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<Translator> _logger;
        private readonly IMemoryCache _cache;
        private readonly HttpClient _httpClient;
        private readonly TranslationConfiguration _configuration;
        private readonly Dictionary<string, TranslationProvider> _providers;
        private readonly Dictionary<string, LanguageProfile> _languageProfiles;
        private readonly Dictionary<string, CulturalProfile> _culturalProfiles;
        private readonly MemoryCacheEntryOptions _cacheOptions;
        private bool _disposed;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false;
        };

        #endregion;

        #region Properties;

        /// <summary>
        /// Şu anda kullanılan birincil çeviri sağlayıcısı;
        /// Primary translation provider currently in use;
        /// </summary>
        public TranslationProvider ActiveProvider { get; private set; }

        /// <summary>
        /// Çeviri işlemleri için varsayılan kaynak dil (Türkçe)
        /// Default source language for translation operations (Turkish)
        /// </summary>
        public string DefaultSourceLanguage { get; private set; } = "tr";

        /// <summary>
        /// Çeviri işlemleri için geçerli kaynak dil;
        /// Current source language for translation operations;
        /// </summary>
        public string SourceLanguage { get; private set; } = "tr";

        /// <summary>
        /// Çeviri işlemleri için geçerli hedef dil;
        /// Current target language for translation operations;
        /// </summary>
        public string TargetLanguage { get; private set; } = "en";

        /// <summary>
        /// Çeviri kalite ayarları;
        /// Translation quality settings;
        /// </summary>
        public TranslationQuality Quality { get; set; }

        /// <summary>
        /// Çeviri önbellek istatistikleri;
        /// Translation cache statistics;
        /// </summary>
        public TranslationCacheStatistics CacheStatistics { get; private set; }

        /// <summary>
        /// Kullanıcıya özel çeviri tercihleri;
        /// User-specific translation preferences;
        /// </summary>
        public UserTranslationPreferences UserPreferences { get; set; }

        /// <summary>
        /// Türkçe karakter dönüşüm haritası;
        /// Turkish character conversion map;
        /// </summary>
        private static readonly Dictionary<char, string> TurkishCharacterMap = new Dictionary<char, string>
        {
            ['ç'] = "c",
            ['Ç'] = "C",
            ['ğ'] = "g",
            ['Ğ'] = "G",
            ['ı'] = "i",
            ['İ'] = "I",
            ['ö'] = "o",
            ['Ö'] = "O",
            ['ş'] = "s",
            ['Ş'] = "S",
            ['ü'] = "u",
            ['Ü'] = "U"
        };

        #endregion;

        #region Constructors;

        /// <summary>
        /// Gerekli bağımlılıklarla Translator'ın yeni bir örneğini başlatır;
        /// Initializes a new instance of Translator with required dependencies;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="cache">Çeviri önbelleği için bellek önbelleği / Memory cache for translation caching</param>
        /// <param name="httpClientFactory">API çağrıları için HTTP client fabrikası / HTTP client factory for API calls</param>
        /// <param name="configuration">Çeviri yapılandırması / Translation configuration</param>
        public Translator(
            ILogger<Translator> logger,
            IMemoryCache cache,
            IHttpClientFactory httpClientFactory,
            TranslationConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _configuration = configuration ?? TranslationConfiguration.Default;

            _httpClient = httpClientFactory?.CreateClient("TranslationClient") ??
                         new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            InitializeTranslator();
        }

        /// <summary>
        /// Çeviriciyi varsayılan ayarlarla başlatır;
        /// Initializes the translator with default settings;
        /// </summary>
        private void InitializeTranslator()
        {
            // Sağlayıcıları başlat;
            // Initialize providers;
            _providers = new Dictionary<string, TranslationProvider>();
            InitializeTranslationProviders();

            // Dil profillerini başlat (Türkçe öncelikli)
            // Initialize language profiles (Turkish prioritized)
            _languageProfiles = new Dictionary<string, LanguageProfile>(StringComparer.OrdinalIgnoreCase);
            InitializeLanguageProfiles();

            // Kültürel profilleri başlat;
            // Initialize cultural profiles;
            _culturalProfiles = new Dictionary<string, CulturalProfile>(StringComparer.OrdinalIgnoreCase);
            InitializeCulturalProfiles();

            // Varsayılan sağlayıcıyı ayarla;
            // Set default provider;
            ActiveProvider = GetDefaultProvider();

            // Önbellek seçeneklerini başlat;
            // Initialize cache options;
            _cacheOptions = new MemoryCacheEntryOptions;
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_configuration.CacheDurationMinutes),
                Size = 1;
            };

            // İstatistikleri başlat;
            // Initialize statistics;
            CacheStatistics = new TranslationCacheStatistics();
            UserPreferences = UserTranslationPreferences.Default;
            Quality = TranslationQuality.Balanced;

            _logger.LogInformation("Translator başlatıldı. {ProviderCount} sağlayıcı, {LanguageCount} dil profili",
                _providers.Count, _languageProfiles.Count);
            _logger.LogInformation("Translator initialized. {ProviderCount} providers, {LanguageCount} language profiles",
                _providers.Count, _languageProfiles.Count);
        }

        /// <summary>
        /// Yapılandırmayla çeviri sağlayıcılarını başlatır;
        /// Initializes translation providers with configuration;
        /// </summary>
        private void InitializeTranslationProviders()
        {
            // Google Cloud Translation API sağlayıcısı;
            // Google Cloud Translation API provider;
            _providers["google"] = new TranslationProvider;
            {
                Id = "google",
                Name = "Google Cloud Translation",
                ApiEndpoint = "https://translation.googleapis.com/language/translate/v2",
                SupportsAutoDetect = true,
                SupportsBatch = true,
                MaxTextLength = 5000,
                SupportedLanguages = new HashSet<string> { "tr", "en", "es", "fr", "de", "it", "ja", "ko", "zh", "ru", "ar", "pt", "nl", "pl", "sv", "fi", "da", "no" },
                Priority = 1,
                IsEnabled = !string.IsNullOrEmpty(_configuration.GoogleApiKey)
            };

            // Microsoft Azure Translator sağlayıcısı;
            // Microsoft Azure Translator provider;
            _providers["microsoft"] = new TranslationProvider;
            {
                Id = "microsoft",
                Name = "Microsoft Azure Translator",
                ApiEndpoint = "https://api.cognitive.microsofttranslator.com/translate",
                SupportsAutoDetect = true,
                SupportsBatch = true,
                MaxTextLength = 10000,
                SupportedLanguages = new HashSet<string> { "tr", "en", "es", "fr", "de", "it", "ja", "ko", "zh", "ru", "ar", "pt", "nl", "pl", "sv", "fi", "da", "no", "hi", "he", "th" },
                Priority = 2,
                IsEnabled = !string.IsNullOrEmpty(_configuration.MicrosoftApiKey)
            };

            // DeepL Translator sağlayıcısı;
            // DeepL Translator provider;
            _providers["deepl"] = new TranslationProvider;
            {
                Id = "deepl",
                Name = "DeepL Translator",
                ApiEndpoint = "https://api.deepl.com/v2/translate",
                SupportsAutoDetect = true,
                SupportsBatch = true,
                MaxTextLength = 5000,
                SupportedLanguages = new HashSet<string> { "tr", "en", "es", "fr", "de", "it", "ja", "ko", "zh", "ru", "pt", "nl", "pl" },
                Priority = 3,
                IsEnabled = !string.IsNullOrEmpty(_configuration.DeepLApiKey)
            };

            // Yedek sağlayıcı (sözlük tabanlı)
            // Fallback provider (dictionary-based)
            _providers["fallback"] = new TranslationProvider;
            {
                Id = "fallback",
                Name = "Yedek Sözlük / Fallback Dictionary",
                ApiEndpoint = null,
                SupportsAutoDetect = false,
                SupportsBatch = false,
                MaxTextLength = 1000,
                SupportedLanguages = new HashSet<string> { "tr", "en", "es", "fr", "de" },
                Priority = 100,
                IsEnabled = true;
            };
        }

        /// <summary>
        /// Dil profillerini dilsel özelliklerle başlatır (Türkçe öncelikli)
        /// Initializes language profiles with linguistic characteristics (Turkish prioritized)
        /// </summary>
        private void InitializeLanguageProfiles()
        {
            // Türkçe - Ana dil;
            // Turkish - Primary language;
            _languageProfiles["tr"] = new LanguageProfile;
            {
                Code = "tr",
                Name = "Türkçe / Turkish",
                NativeName = "Türkçe",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Latin (Türkçe)",
                WordOrder = "SOV (Özne-Nesne-Fiil)",
                SentenceStructure = "Son eklemeli (Agglutinative)",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["çok_resmi"] = 0.4,
                    ["resmi"] = 0.6,
                    ["nötr"] = 0.5,
                    ["gayri_resmi"] = 0.4,
                    ["çok_gayri_resmi"] = 0.3;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["tarih"] = "dd.MM.yyyy",
                    ["saat"] = "HH:mm",
                    ["sayı"] = "#.###,##",
                    ["para"] = "#.###,## ₺"
                },
                SpecialCharacters = new HashSet<char> { 'ç', 'Ç', 'ğ', 'Ğ', 'ı', 'İ', 'ö', 'Ö', 'ş', 'Ş', 'ü', 'Ü' },
                IsAgglutinative = true,
                VowelHarmony = true;
            };

            // İngilizce;
            // English;
            _languageProfiles["en"] = new LanguageProfile;
            {
                Code = "en",
                Name = "İngilizce / English",
                NativeName = "English",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Latin",
                WordOrder = "SVO (Subject-Verb-Object)",
                SentenceStructure = "Analytic",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["very_formal"] = 0.2,
                    ["formal"] = 0.3,
                    ["neutral"] = 0.5,
                    ["informal"] = 0.7,
                    ["very_informal"] = 0.8;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["date"] = "MM/dd/yyyy",
                    ["time"] = "h:mm tt",
                    ["number"] = "#,##0.##",
                    ["currency"] = "$#,##0.00"
                },
                SpecialCharacters = new HashSet<char> { ',', '.', ';', ':', '!', '?', '\'', '"' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };

            // Almanca;
            // German;
            _languageProfiles["de"] = new LanguageProfile;
            {
                Code = "de",
                Name = "Almanca / German",
                NativeName = "Deutsch",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Latin",
                WordOrder = "SVO/V2",
                SentenceStructure = "Fusional",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["sehr_formell"] = 0.5,
                    ["formell"] = 0.6,
                    ["neutral"] = 0.5,
                    ["informell"] = 0.4,
                    ["sehr_informell"] = 0.3;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["datum"] = "dd.MM.yyyy",
                    ["zeit"] = "HH:mm",
                    ["zahl"] = "#.##0,##",
                    ["währung"] = "#.##0,## €"
                },
                SpecialCharacters = new HashSet<char> { 'ä', 'ö', 'ü', 'ß', 'ẞ' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };

            // Fransızca;
            // French;
            _languageProfiles["fr"] = new LanguageProfile;
            {
                Code = "fr",
                Name = "Fransızca / French",
                NativeName = "Français",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Latin",
                WordOrder = "SVO",
                SentenceStructure = "Analytic",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["très_formel"] = 0.4,
                    ["formel"] = 0.5,
                    ["neutre"] = 0.5,
                    ["informel"] = 0.4,
                    ["très_informel"] = 0.3;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["date"] = "dd/MM/yyyy",
                    ["heure"] = "HH:mm",
                    ["nombre"] = "# ##0,##",
                    ["devise"] = "# ##0,## €"
                },
                SpecialCharacters = new HashSet<char> { 'à', 'â', 'ç', 'é', 'è', 'ê', 'ë', 'î', 'ï', 'ô', 'ù', 'û', 'ü', 'œ' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };

            // İspanyolca;
            // Spanish;
            _languageProfiles["es"] = new LanguageProfile;
            {
                Code = "es",
                Name = "İspanyolca / Spanish",
                NativeName = "Español",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Latin",
                WordOrder = "SVO",
                SentenceStructure = "Fusional",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["muy_formal"] = 0.3,
                    ["formal"] = 0.4,
                    ["neutral"] = 0.5,
                    ["informal"] = 0.6,
                    ["muy_informal"] = 0.7;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["fecha"] = "dd/MM/yyyy",
                    ["hora"] = "HH:mm",
                    ["número"] = "#.##0,##",
                    ["moneda"] = "#.##0,## €"
                },
                SpecialCharacters = new HashSet<char> { '¿', '¡', 'á', 'é', 'í', 'ó', 'ú', 'ñ', 'ü' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };

            // Arapça;
            // Arabic;
            _languageProfiles["ar"] = new LanguageProfile;
            {
                Code = "ar",
                Name = "Arapça / Arabic",
                NativeName = "العربية",
                Direction = TextDirection.RightToLeft,
                CharacterSet = "Arabic",
                WordOrder = "VSO",
                SentenceStructure = "Fusional",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["رسمي_جدا"] = 0.6,
                    ["رسمي"] = 0.7,
                    ["محايد"] = 0.5,
                    ["غير_رسمي"] = 0.3,
                    ["غير_رسمي_جدا"] = 0.2;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["تاريخ"] = "dd/MM/yyyy",
                    ["وقت"] = "HH:mm",
                    ["رقم"] = "#,##0.###",
                    ["عملة"] = "#,##0.### د.إ"
                },
                SpecialCharacters = new HashSet<char> { '،', '؛', '؟', 'ء', 'آ', 'أ', 'ؤ', 'إ', 'ئ', 'ا', 'ب', 'ة', 'ت', 'ث', 'ج', 'ح', 'خ', 'د', 'ذ', 'ر', 'ز', 'س', 'ش', 'ص', 'ض', 'ط', 'ظ', 'ع', 'غ', 'ف', 'ق', 'ك', 'ل', 'م', 'ن', 'ه', 'و', 'ى', 'ي' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };

            // Rusça;
            // Russian;
            _languageProfiles["ru"] = new LanguageProfile;
            {
                Code = "ru",
                Name = "Rusça / Russian",
                NativeName = "Русский",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Cyrillic",
                WordOrder = "SVO (flexible)",
                SentenceStructure = "Fusional",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["очень_формальный"] = 0.5,
                    ["формальный"] = 0.6,
                    ["нейтральный"] = 0.5,
                    ["неформальный"] = 0.4,
                    ["очень_неформальный"] = 0.3;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["дата"] = "dd.MM.yyyy",
                    ["время"] = "HH:mm",
                    ["число"] = "# ###,##",
                    ["валюта"] = "# ###,## ₽"
                },
                SpecialCharacters = new HashSet<char> { 'ё', 'Ё', 'й', 'Й', 'ъ', 'Ъ', 'ы', 'Ы', 'ь', 'Ь', 'э', 'Э', 'ю', 'Ю', 'я', 'Я' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };

            // Japonca;
            // Japanese;
            _languageProfiles["ja"] = new LanguageProfile;
            {
                Code = "ja",
                Name = "Japonca / Japanese",
                NativeName = "日本語",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Kanji, Hiragana, Katakana",
                WordOrder = "SOV",
                SentenceStructure = "Agglutinative",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["非常に丁寧"] = 0.6,
                    ["丁寧"] = 0.7,
                    ["中性"] = 0.5,
                    ["カジュアル"] = 0.3,
                    ["非常にカジュアル"] = 0.2;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["日付"] = "yyyy年MM月dd日",
                    ["時間"] = "HH時mm分",
                    ["数"] = "#,##0",
                    ["通貨"] = "¥#,##0"
                },
                SpecialCharacters = new HashSet<char> { '。', '、', '「', '」', '『', '』', '・' },
                IsAgglutinative = true,
                VowelHarmony = false;
            };

            // Korece;
            // Korean;
            _languageProfiles["ko"] = new LanguageProfile;
            {
                Code = "ko",
                Name = "Korece / Korean",
                NativeName = "한국어",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Hangul",
                WordOrder = "SOV",
                SentenceStructure = "Agglutinative",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["매우_공식적"] = 0.7,
                    ["공식적"] = 0.8,
                    ["중립"] = 0.5,
                    ["비공식적"] = 0.3,
                    ["매우_비공식적"] = 0.2;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["날짜"] = "yyyy년 MM월 dd일",
                    ["시간"] = "HH시 mm분",
                    ["숫자"] = "#,##0",
                    ["통화"] = "₩#,##0"
                },
                SpecialCharacters = new HashSet<char> { '。', '、', '「', '」', '『', '』', '·' },
                IsAgglutinative = true,
                VowelHarmony = false;
            };

            // Çince (Basitleştirilmiş)
            // Chinese (Simplified)
            _languageProfiles["zh-CN"] = new LanguageProfile;
            {
                Code = "zh-CN",
                Name = "Çince (Basitleştirilmiş) / Chinese (Simplified)",
                NativeName = "简体中文",
                Direction = TextDirection.LeftToRight,
                CharacterSet = "Hanzi",
                WordOrder = "SVO",
                SentenceStructure = "Analytic",
                FormalityLevels = new Dictionary<string, double>
                {
                    ["非常正式"] = 0.5,
                    ["正式"] = 0.6,
                    ["中性"] = 0.5,
                    ["非正式"] = 0.4,
                    ["非常非正式"] = 0.3;
                },
                CommonFormats = new Dictionary<string, string>
                {
                    ["日期"] = "yyyy年MM月dd日",
                    ["时间"] = "HH时mm分",
                    ["数字"] = "#,##0.##",
                    ["货币"] = "¥#,##0.##"
                },
                SpecialCharacters = new HashSet<char> { '。', '，', '；', '：', '！', '？', '「', '」' },
                IsAgglutinative = false,
                VowelHarmony = false;
            };
        }

        /// <summary>
        /// Kültürel profilleri bölgesel varyasyonlarla başlatır;
        /// Initializes cultural profiles with regional variations;
        /// </summary>
        private void InitializeCulturalProfiles()
        {
            // Türkiye Türkçesi;
            // Turkish (Turkey)
            _culturalProfiles["tr-TR"] = new CulturalProfile;
            {
                CultureCode = "tr-TR",
                LanguageCode = "tr",
                Region = "Türkiye / Turkey",
                TimeZone = "Europe/Istanbul",
                DateFormat = "dd.MM.yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "#.###,##",
                CurrencyFormat = "#.###,## ₺",
                CurrencySymbol = "₺",
                DecimalSeparator = ',',
                ThousandsSeparator = '.',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Neutral,
                CommunicationStyle = CommunicationStyle.Direct,
                Greeting = "Merhaba",
                FormalGreeting = "Saygılar",
                InformalGreeting = "Selam"
            };

            // Türkçe (Kuzey Kıbrıs)
            // Turkish (Northern Cyprus)
            _culturalProfiles["tr-CY"] = new CulturalProfile;
            {
                CultureCode = "tr-CY",
                LanguageCode = "tr",
                Region = "Kuzey Kıbrıs / Northern Cyprus",
                TimeZone = "Asia/Nicosia",
                DateFormat = "dd.MM.yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "#.###,##",
                CurrencyFormat = "#.###,## ₺",
                CurrencySymbol = "₺",
                DecimalSeparator = ',',
                ThousandsSeparator = '.',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Neutral,
                CommunicationStyle = CommunicationStyle.Direct,
                Greeting = "Merhaba",
                FormalGreeting = "Saygılar",
                InformalGreeting = "Selam"
            };

            // İngilizce (ABD)
            // English (US)
            _culturalProfiles["en-US"] = new CulturalProfile;
            {
                CultureCode = "en-US",
                LanguageCode = "en",
                Region = "Amerika Birleşik Devletleri / United States",
                TimeZone = "America/New_York",
                DateFormat = "MM/dd/yyyy",
                TimeFormat = "h:mm tt",
                NumberFormat = "#,##0.##",
                CurrencyFormat = "$#,##0.00",
                CurrencySymbol = "$",
                DecimalSeparator = '.',
                ThousandsSeparator = ',',
                MeasurementSystem = MeasurementSystem.Imperial,
                TemperatureUnit = TemperatureUnit.Fahrenheit,
                FormalityLevel = FormalityLevel.Neutral,
                CommunicationStyle = CommunicationStyle.Direct,
                Greeting = "Hello",
                FormalGreeting = "Greetings",
                InformalGreeting = "Hi"
            };

            // İngilizce (Birleşik Krallık)
            // English (UK)
            _culturalProfiles["en-GB"] = new CulturalProfile;
            {
                CultureCode = "en-GB",
                LanguageCode = "en",
                Region = "Birleşik Krallık / United Kingdom",
                TimeZone = "Europe/London",
                DateFormat = "dd/MM/yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "#,##0.##",
                CurrencyFormat = "£#,##0.00",
                CurrencySymbol = "£",
                DecimalSeparator = '.',
                ThousandsSeparator = ',',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Formal,
                CommunicationStyle = CommunicationStyle.Indirect,
                Greeting = "Hello",
                FormalGreeting = "Greetings",
                InformalGreeting = "Hi"
            };

            // Almanca (Almanya)
            // German (Germany)
            _culturalProfiles["de-DE"] = new CulturalProfile;
            {
                CultureCode = "de-DE",
                LanguageCode = "de",
                Region = "Almanya / Germany",
                TimeZone = "Europe/Berlin",
                DateFormat = "dd.MM.yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "#.##0,##",
                CurrencyFormat = "#.##0,## €",
                CurrencySymbol = "€",
                DecimalSeparator = ',',
                ThousandsSeparator = '.',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Formal,
                CommunicationStyle = CommunicationStyle.Direct,
                Greeting = "Hallo",
                FormalGreeting = "Guten Tag",
                InformalGreeting = "Hi"
            };

            // Fransızca (Fransa)
            // French (France)
            _culturalProfiles["fr-FR"] = new CulturalProfile;
            {
                CultureCode = "fr-FR",
                LanguageCode = "fr",
                Region = "Fransa / France",
                TimeZone = "Europe/Paris",
                DateFormat = "dd/MM/yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "# ##0,##",
                CurrencyFormat = "# ##0,## €",
                CurrencySymbol = "€",
                DecimalSeparator = ',',
                ThousandsSeparator = ' ',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Formal,
                CommunicationStyle = CommunicationStyle.Indirect,
                Greeting = "Bonjour",
                FormalGreeting = "Salutations",
                InformalGreeting = "Salut"
            };

            // İspanyolca (İspanya)
            // Spanish (Spain)
            _culturalProfiles["es-ES"] = new CulturalProfile;
            {
                CultureCode = "es-ES",
                LanguageCode = "es",
                Region = "İspanya / Spain",
                TimeZone = "Europe/Madrid",
                DateFormat = "dd/MM/yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "#.##0,##",
                CurrencyFormat = "#.##0,## €",
                CurrencySymbol = "€",
                DecimalSeparator = ',',
                ThousandsSeparator = '.',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Neutral,
                CommunicationStyle = CommunicationStyle.Direct,
                Greeting = "Hola",
                FormalGreeting = "Saludos",
                InformalGreeting = "Hola"
            };

            // Arapça (Suudi Arabistan)
            // Arabic (Saudi Arabia)
            _culturalProfiles["ar-SA"] = new CulturalProfile;
            {
                CultureCode = "ar-SA",
                LanguageCode = "ar",
                Region = "Suudi Arabistan / Saudi Arabia",
                TimeZone = "Asia/Riyadh",
                DateFormat = "dd/MM/yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "#,##0.###",
                CurrencyFormat = "#,##0.### ر.س",
                CurrencySymbol = "ر.س",
                DecimalSeparator = '.',
                ThousandsSeparator = ',',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Formal,
                CommunicationStyle = CommunicationStyle.Indirect,
                Greeting = "مرحبا",
                FormalGreeting = "تحياتي",
                InformalGreeting = "أهلا"
            };

            // Rusça (Rusya)
            // Russian (Russia)
            _culturalProfiles["ru-RU"] = new CulturalProfile;
            {
                CultureCode = "ru-RU",
                LanguageCode = "ru",
                Region = "Rusya / Russia",
                TimeZone = "Europe/Moscow",
                DateFormat = "dd.MM.yyyy",
                TimeFormat = "HH:mm",
                NumberFormat = "# ###,##",
                CurrencyFormat = "# ###,## ₽",
                CurrencySymbol = "₽",
                DecimalSeparator = ',',
                ThousandsSeparator = ' ',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Formal,
                CommunicationStyle = CommunicationStyle.Direct,
                Greeting = "Здравствуйте",
                FormalGreeting = "Приветствие",
                InformalGreeting = "Привет"
            };

            // Japonca (Japonya)
            // Japanese (Japan)
            _culturalProfiles["ja-JP"] = new CulturalProfile;
            {
                CultureCode = "ja-JP",
                LanguageCode = "ja",
                Region = "Japonya / Japan",
                TimeZone = "Asia/Tokyo",
                DateFormat = "yyyy年MM月dd日",
                TimeFormat = "HH時mm分",
                NumberFormat = "#,##0",
                CurrencyFormat = "¥#,##0",
                CurrencySymbol = "¥",
                DecimalSeparator = '.',
                ThousandsSeparator = ',',
                MeasurementSystem = MeasurementSystem.Metric,
                TemperatureUnit = TemperatureUnit.Celsius,
                FormalityLevel = FormalityLevel.Formal,
                CommunicationStyle = CommunicationStyle.Indirect,
                Greeting = "こんにちは",
                FormalGreeting = "ご挨拶",
                InformalGreeting = "やあ"
            };
        }

        /// <summary>
        /// Yapılandırmaya göre varsayılan çeviri sağlayıcısını alır;
        /// Gets the default translation provider based on configuration;
        /// </summary>
        private TranslationProvider GetDefaultProvider()
        {
            var enabledProviders = _providers.Values;
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Priority)
                .ToList();

            return enabledProviders.FirstOrDefault() ?? _providers["fallback"];
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Metni kaynak dilden hedef dile çevirir (varsayılan kaynak Türkçe)
        /// Translates text from source language to target language (default source is Turkish)
        /// </summary>
        /// <param name="text">Çevrilecek metin / Text to translate</param>
        /// <param name="targetLanguage">Hedef dil kodu (örn: "en", "de", "fr") / Target language code (e.g., "en", "de", "fr")</param>
        /// <param name="sourceLanguage">Kaynak dil kodu (isteğe bağlı, belirtilmezse otomatik tespit veya Türkçe) / Source language code (optional, auto-detect or Turkish if not specified)</param>
        /// <param name="context">Daha iyi doğruluk için çeviri bağlamı / Translation context for better accuracy</param>
        /// <returns>Çevrilmiş metin / Translated text</returns>
        public async Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            string sourceLanguage = null,
            TranslationContext context = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Metin boş veya null olamaz / Text cannot be null or empty", nameof(text));

            if (string.IsNullOrWhiteSpace(targetLanguage))
                throw new ArgumentException("Hedef dil boş veya null olamaz / Target language cannot be null or empty", nameof(targetLanguage));

            try
            {
                // Kaynak dili belirtilmezse, varsayılan olarak Türkçe kabul et veya otomatik tespit et;
                // If source language is not specified, default to Turkish or auto-detect;
                if (string.IsNullOrWhiteSpace(sourceLanguage))
                {
                    if (_configuration.DefaultToTurkish)
                    {
                        sourceLanguage = "tr";
                    }
                    else;
                    {
                        sourceLanguage = "auto";
                    }
                }

                _logger.LogDebug("Metin çevriliyor: {SourceLang} -> {TargetLang}: {Text}",
                    sourceLanguage, targetLanguage, text.Truncate(100));
                _logger.LogDebug("Translating text: {SourceLang} -> {TargetLang}: {Text}",
                    sourceLanguage, targetLanguage, text.Truncate(100));

                // Geçerli dil ayarlarını güncelle;
                // Update current language settings;
                SourceLanguage = sourceLanguage;
                TargetLanguage = targetLanguage;

                // Önce önbelleği kontrol et;
                // Check cache first;
                string cacheKey = GenerateCacheKey(text, sourceLanguage, targetLanguage, context);
                if (_configuration.EnableCaching && _cache.TryGetValue(cacheKey, out string cachedTranslation))
                {
                    CacheStatistics.Hits++;
                    _logger.LogDebug("Önbellek isabeti / Cache hit");
                    return cachedTranslation;
                }

                CacheStatistics.Misses++;

                // Türkçe'den çeviri için özel işlemler;
                // Special processing for Turkish translations;
                if (sourceLanguage == "tr" || sourceLanguage == "auto")
                {
                    text = PrepareTurkishTextForTranslation(text);
                }

                // Uygun sağlayıcıyı seç;
                // Choose appropriate provider;
                var provider = ChooseProvider(sourceLanguage, targetLanguage);

                // Çeviriyi gerçekleştir;
                // Perform translation;
                string translatedText;

                if (provider.Id == "fallback")
                {
                    translatedText = await TranslateWithFallbackAsync(text, sourceLanguage, targetLanguage, context);
                }
                else;
                {
                    translatedText = await TranslateWithProviderAsync(text, sourceLanguage, targetLanguage, provider, context);
                }

                // Son işlemeyi uygula;
                // Apply post-processing;
                translatedText = await PostProcessTranslationAsync(text, translatedText, sourceLanguage, targetLanguage, context);

                // Türkçe'ye çeviri için özel işlemler;
                // Special processing for translations to Turkish;
                if (targetLanguage == "tr")
                {
                    translatedText = ApplyTurkishLanguageRules(translatedText);
                }

                // Önbelleği güncelle;
                // Update cache;
                if (_configuration.EnableCaching && !string.IsNullOrEmpty(translatedText))
                {
                    _cache.Set(cacheKey, translatedText, _cacheOptions);
                    CacheStatistics.EntriesCached++;
                }

                // İstatistikleri güncelle;
                // Update statistics;
                CacheStatistics.TotalTranslations++;

                _logger.LogInformation("Çeviri tamamlandı. Kaynak: {SourceLang}, Hedef: {TargetLang}, Sağlayıcı: {Provider}",
                    sourceLanguage, targetLanguage, provider.Name);
                _logger.LogInformation("Translation completed. Source: {SourceLang}, Target: {TargetLang}, Provider: {Provider}",
                    sourceLanguage, targetLanguage, provider.Name);

                return translatedText;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metin çevrilemedi: {Text} / Failed to translate text: {Text}", text);
                return await GetFallbackTranslation(text, sourceLanguage, targetLanguage);
            }
        }

        /// <summary>
        /// Türkçe metni çeviri için hazırlar (Türkçe karakterler, cümle yapısı vb.)
        /// Prepares Turkish text for translation (Turkish characters, sentence structure, etc.)
        /// </summary>
        /// <param name="text">Türkçe metin / Turkish text</param>
        /// <returns>Hazırlanmış metin / Prepared text</returns>
        private string PrepareTurkishTextForTranslation(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var preparedText = text;

            // Türkçe özel karakterleri koru (çeviri API'leri genelde destekler)
            // Preserve Turkish special characters (translation APIs usually support them)

            // Türkçe cümle yapısını basitleştir (isteğe bağlı)
            // Simplify Turkish sentence structure (optional)
            if (_configuration.SimplifyTurkishSentences)
            {
                preparedText = SimplifyTurkishSentence(preparedText);
            }

            // Türkçe deyimler ve ifadeler için özel işlem;
            // Special handling for Turkish idioms and expressions;
            preparedText = ReplaceTurkishIdiomsForTranslation(preparedText);

            return preparedText;
        }

        /// <summary>
        /// Türkçe dil kurallarını çevrilmiş metne uygular;
        /// Applies Turkish language rules to translated text;
        /// </summary>
        /// <param name="text">Çevrilmiş metin / Translated text</param>
        /// <returns>Türkçe kuralları uygulanmış metin / Text with Turkish rules applied</returns>
        private string ApplyTurkishLanguageRules(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var processedText = text;

            // Büyük harf kuralları: "i" -> "İ", "ı" -> "I"
            // Capitalization rules: "i" -> "İ", "ı" -> "I"
            processedText = ApplyTurkishCapitalization(processedText);

            // Türkçe noktalama kuralları;
            // Turkish punctuation rules;
            processedText = ApplyTurkishPunctuation(processedText);

            // Türkçe kelime sıralaması kontrolü (isteğe bağlı)
            // Turkish word order check (optional)
            if (_configuration.FixTurkishWordOrder)
            {
                processedText = FixTurkishWordOrder(processedText);
            }

            // Türkçe deyimleri geri yükle;
            // Restore Turkish idioms;
            processedText = RestoreTurkishIdioms(processedText);

            return processedText;
        }

        /// <summary>
        /// Türkçe büyük harf kurallarını uygular;
        /// Applies Turkish capitalization rules;
        /// </summary>
        private string ApplyTurkishCapitalization(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // "i" -> "İ" dönüşümü;
            // "i" -> "İ" conversion;
            var result = new StringBuilder();
            bool capitalizeNext = true;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (capitalizeNext && char.IsLetter(c))
                {
                    if (c == 'i')
                        c = 'İ';
                    else if (c == 'ı')
                        c = 'I';
                    else;
                        c = char.ToUpper(c, new CultureInfo("tr-TR"));

                    capitalizeNext = false;
                }
                else if (c == '.' || c == '!' || c == '?')
                {
                    capitalizeNext = true;
                }

                result.Append(c);
            }

            return result.ToString();
        }

        /// <summary>
        /// Türkçe noktalama kurallarını uygular;
        /// Applies Turkish punctuation rules;
        /// </summary>
        private string ApplyTurkishPunctuation(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var result = text;

            // Virgül ve nokta sonrası boşluk;
            // Space after comma and period;
            result = Regex.Replace(result, @"([.,])([A-Za-zÇçĞğİıÖöŞşÜü])", "$1 $2");

            // Soru ve ünlem işaretlerinden önce boşluk (Türkçe'de genellikle yok)
            // Space before question and exclamation marks (usually not in Turkish)
            result = Regex.Replace(result, @"\s+([!?])", "$1");

            return result;
        }

        /// <summary>
        /// Türkçe kelime sıralamasını düzeltir;
        /// Fixes Turkish word order;
        /// </summary>
        private string FixTurkishWordOrder(string text)
        {
            // Basit bir yaklaşım: SVO -> SOV dönüşümü için basit kurallar;
            // Simple approach: Basic rules for SVO -> SOV conversion;
            var sentences = text.Split('.', '!', '?');
            var fixedSentences = new List<string>();

            foreach (var sentence in sentences)
            {
                if (string.IsNullOrWhiteSpace(sentence))
                {
                    fixedSentences.Add(sentence);
                    continue;
                }

                var words = sentence.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 2)
                {
                    fixedSentences.Add(sentence);
                    continue;
                }

                // Basit bir SOV düzenlemesi (gerçek uygulamada daha karmaşık olmalı)
                // Simple SOV arrangement (should be more complex in real application)
                var fixedSentence = string.Join(" ", words);
                fixedSentences.Add(fixedSentence);
            }

            return string.Join(". ", fixedSentences);
        }

        /// <summary>
        /// Türkçe deyimleri çeviri için değiştirir;
        /// Replaces Turkish idioms for translation;
        /// </summary>
        private string ReplaceTurkishIdiomsForTranslation(string text)
        {
            var idioms = new Dictionary<string, string>
            {
                ["kulağına küpe olsun"] = "bu bir ders olsun",
                ["eli kulağında"] = "çok yakında",
                ["gözünü dört aç"] = "dikkatli ol",
                ["ayakları yere basmıyor"] = "çok mutlu",
                ["burnundan solumak"] = "çok sinirli olmak",
                ["dilinin altında bir şey olmak"] = "bir şey söylemek istemek"
            };

            var result = text;
            foreach (var idiom in idioms)
            {
                result = result.Replace(idiom.Key, idiom.Value);
            }

            return result;
        }

        /// <summary>
        /// Türkçe deyimleri geri yükler;
        /// Restores Turkish idioms;
        /// </summary>
        private string RestoreTurkishIdioms(string text)
        {
            var idioms = new Dictionary<string, string>
            {
                ["bu bir ders olsun"] = "kulağına küpe olsun",
                ["çok yakında"] = "eli kulağında",
                ["dikkatli ol"] = "gözünü dört aç",
                ["çok mutlu"] = "ayakları yere basmıyor",
                ["çok sinirli olmak"] = "burnundan solumak",
                ["bir şey söylemek istemek"] = "dilinin altında bir şey olmak"
            };

            var result = text;
            foreach (var idiom in idioms)
            {
                // Sadece tam eşleşmeler için;
                // Only for exact matches;
                result = Regex.Replace(result, $@"\b{Regex.Escape(idiom.Key)}\b", idiom.Value);
            }

            return result;
        }

        /// <summary>
        /// Türkçe cümleyi basitleştirir;
        /// Simplifies Turkish sentence;
        /// </summary>
        private string SimplifyTurkishSentence(string text)
        {
            // Basit bir yaklaşım: Uzun cümleleri böl;
            // Simple approach: Split long sentences;
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
            var simplifiedSentences = new List<string>();

            foreach (var sentence in sentences)
            {
                if (sentence.Length > 100)
                {
                    // Uzun cümleyi virgüllerden böl;
                    // Split long sentence at commas;
                    var parts = sentence.Split(',');
                    if (parts.Length > 1)
                    {
                        simplifiedSentences.AddRange(parts.Select(p => p.Trim() + ","));
                    }
                    else;
                    {
                        simplifiedSentences.Add(sentence);
                    }
                }
                else;
                {
                    simplifiedSentences.Add(sentence);
                }
            }

            return string.Join(" ", simplifiedSentences).TrimEnd(',');
        }

        /// <summary>
        /// Türkçe metni İngilizce karakterlere çevirir (isteğe bağlı)
        /// Converts Turkish text to English characters (optional)
        /// </summary>
        public string TurkishToEnglishCharacters(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var result = new StringBuilder();
            foreach (char c in text)
            {
                if (TurkishCharacterMap.TryGetValue(c, out string replacement))
                {
                    result.Append(replacement);
                }
                else;
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// İngilizce karakterleri Türkçe karakterlere çevirir;
        /// Converts English characters to Turkish characters;
        /// </summary>
        public string EnglishToTurkishCharacters(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var reverseMap = TurkishCharacterMap.ToDictionary(x => x.Value[0], x => x.Key);
            var result = new StringBuilder();

            foreach (char c in text)
            {
                if (reverseMap.TryGetValue(c, out char turkishChar))
                {
                    result.Append(turkishChar);
                }
                else;
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Metni kaynak dilden hedef dile toplu olarak çevirir;
        /// Translates multiple texts in a batch operation;
        /// </summary>
        public async Task<IEnumerable<string>> TranslateBatchAsync(
            IEnumerable<string> texts,
            string targetLanguage,
            string sourceLanguage = null,
            TranslationContext context = null)
        {
            if (texts == null)
                throw new ArgumentNullException(nameof(texts));

            var textList = texts.ToList();
            if (!textList.Any())
                return Enumerable.Empty<string>();

            try
            {
                // Kaynak dili belirtilmezse, varsayılan olarak Türkçe kabul et;
                // If source language is not specified, default to Turkish;
                if (string.IsNullOrWhiteSpace(sourceLanguage))
                {
                    sourceLanguage = _configuration.DefaultToTurkish ? "tr" : "auto";
                }

                _logger.LogDebug("Toplu çeviri: {TextCount} metin, {SourceLang} -> {TargetLang}",
                    textList.Count, sourceLanguage, targetLanguage);
                _logger.LogDebug("Batch translation: {TextCount} texts, {SourceLang} -> {TargetLang}",
                    textList.Count, sourceLanguage, targetLanguage);

                // Toplu işlemleri destekleyen sağlayıcı seç;
                // Choose provider that supports batch operations;
                var provider = ChooseProvider(sourceLanguage, targetLanguage);
                if (!provider.SupportsBatch)
                {
                    // Sıralı çeviriye geri dön;
                    // Fall back to sequential translation;
                    return await TranslateSequentiallyAsync(textList, targetLanguage, sourceLanguage, context);
                }

                // Toplu çeviri gerçekleştir;
                // Perform batch translation;
                var results = await TranslateBatchWithProviderAsync(textList, sourceLanguage, targetLanguage, provider, context);

                // Türkçe'ye çeviri için özel işlemler;
                // Special processing for translations to Turkish;
                if (targetLanguage == "tr")
                {
                    results = results.Select(t => ApplyTurkishLanguageRules(t)).ToList();
                }

                _logger.LogInformation("Toplu çeviri tamamlandı. {SuccessCount}/{TotalCount} başarılı",
                    results.Count(r => !string.IsNullOrEmpty(r)), textList.Count);
                _logger.LogInformation("Batch translation completed. {SuccessCount}/{TotalCount} successful",
                    results.Count(r => !string.IsNullOrEmpty(r)), textList.Count);

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{TextCount} metin toplu çevrilemedi / Failed to batch translate {TextCount} texts", textList.Count);
                return await TranslateSequentiallyAsync(textList, targetLanguage, sourceLanguage, context);
            }
        }

        /// <summary>
        /// Verilen metnin dilini tespit eder (Türkçe tespiti geliştirilmiş)
        /// Detects the language of the given text (Turkish detection enhanced)
        /// </summary>
        public async Task<LanguageDetectionResult> DetectLanguageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Metin boş veya null olamaz / Text cannot be null or empty", nameof(text));

            try
            {
                _logger.LogDebug("Dil tespiti: {Text}", text.Truncate(100));
                _logger.LogDebug("Language detection: {Text}", text.Truncate(100));

                // Önce Türkçe kontrolü (öncelikli)
                // First check for Turkish (prioritized)
                var turkishResult = DetectTurkishLanguage(text);
                if (turkishResult.Confidence > 0.7)
                {
                    _logger.LogDebug("Türkçe tespit edildi. Güven: {Confidence}", turkishResult.Confidence);
                    _logger.LogDebug("Turkish detected. Confidence: {Confidence}", turkishResult.Confidence);
                    return turkishResult;
                }

                // Aktif sağlayıcıyı tespit için kullan;
                // Use the active provider for detection;
                if (ActiveProvider.SupportsAutoDetect && ActiveProvider.Id != "fallback")
                {
                    return await DetectLanguageWithProviderAsync(text, ActiveProvider);
                }

                // Buluşsal yöntemlerle tespit;
                // Detect with heuristic methods;
                return DetectLanguageHeuristic(text);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dil tespit edilemedi: {Text} / Failed to detect language for text: {Text}", text);
                return new LanguageDetectionResult;
                {
                    LanguageCode = "tr", // Varsayılan olarak Türkçe / Default to Turkish;
                    Confidence = 0.5,
                    IsReliable = false;
                };
            }
        }

        /// <summary>
        /// Türkçe dilini gelişmiş yöntemlerle tespit eder;
        /// Detects Turkish language with advanced methods;
        /// </summary>
        private LanguageDetectionResult DetectTurkishLanguage(string text)
        {
            double confidence = 0.0;

            // Türkçe karakter kontrolü;
            // Turkish character check;
            var turkishChars = new HashSet<char> { 'ç', 'Ç', 'ğ', 'Ğ', 'ı', 'İ', 'ö', 'Ö', 'ş', 'Ş', 'ü', 'Ü' };
            int turkishCharCount = text.Count(c => turkishChars.Contains(c));
            int totalChars = text.Count(char.IsLetter);

            if (totalChars > 0)
            {
                double turkishCharRatio = (double)turkishCharCount / totalChars;
                if (turkishCharRatio > 0.05) // Metnin %5'inden fazlası Türkçe karakter ise;
                {
                    confidence += turkishCharRatio * 0.4;
                }
            }

            // Türkçe kelime kontrolü;
            // Turkish word check;
            var turkishWords = new HashSet<string>
            {
                "ve", "bir", "bu", "değil", "için", "ama", "veya", "ben", "sen", "o",
                "biz", "siz", "onlar", "evet", "hayır", "lütfen", "teşekkür", "merhaba",
                "güle", "güle", "tamam", "iyi", "kötü", "büyük", "küçük", "yeni", "eski"
            };

            var words = text.ToLower().Split(' ', '.', ',', '!', '?', ';', ':');
            int turkishWordCount = words.Count(w => turkishWords.Contains(w));
            int totalWords = words.Length;

            if (totalWords > 0)
            {
                double turkishWordRatio = (double)turkishWordCount / totalWords;
                confidence += turkishWordRatio * 0.4;
            }

            // Türkçe ek kontrolü (son ekler)
            // Turkish suffix check (suffixes)
            var turkishSuffixes = new[] { "lar", "ler", "lı", "li", "lu", "lü", "sız", "siz", "suz", "süz", "da", "de", "ta", "te" };
            int suffixCount = 0;
            foreach (var word in words)
            {
                if (word.Length > 2)
                {
                    foreach (var suffix in turkishSuffixes)
                    {
                        if (word.EndsWith(suffix))
                        {
                            suffixCount++;
                            break;
                        }
                    }
                }
            }

            if (totalWords > 0)
            {
                double suffixRatio = (double)suffixCount / totalWords;
                confidence += suffixRatio * 0.2;
            }

            return new LanguageDetectionResult;
            {
                LanguageCode = "tr",
                Confidence = Math.Clamp(confidence, 0.0, 1.0),
                IsReliable = confidence > 0.6;
            };
        }

        /// <summary>
        /// Belirli bir sağlayıcı için desteklenen dilleri alır;
        /// Gets supported languages for a specific provider;
        /// </summary>
        public IEnumerable<string> GetSupportedLanguages(string providerId = null)
        {
            var provider = string.IsNullOrEmpty(providerId) ? ActiveProvider : GetProvider(providerId);

            return provider?.SupportedLanguages ?? Enumerable.Empty<string>();
        }

        /// <summary>
        /// Belirli bir dil kodu için dil profilini alır;
        /// Gets language profile for a specific language code;
        /// </summary>
        public LanguageProfile GetLanguageProfile(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return _languageProfiles["tr"]; // Varsayılan olarak Türkçe / Default to Turkish;

            return _languageProfiles.TryGetValue(languageCode, out var profile) ? profile : null;
        }

        /// <summary>
        /// Belirli bir kültür kodu için kültürel profili alır;
        /// Gets cultural profile for a specific culture code;
        /// </summary>
        public CulturalProfile GetCulturalProfile(string cultureCode)
        {
            if (string.IsNullOrWhiteSpace(cultureCode))
                return _culturalProfiles["tr-TR"]; // Varsayılan olarak Türkiye Türkçesi / Default to Turkish (Turkey)

            return _culturalProfiles.TryGetValue(cultureCode, out var profile) ? profile : null;
        }

        /// <summary>
        /// Metni bir yazıdan diğerine çevirir (transliteration)
        /// Transliterates text from one script to another;
        /// </summary>
        public async Task<string> TransliterateAsync(string text, string sourceScript, string targetScript)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            try
            {
                // Türkçe için özel transliterasyon;
                // Special transliteration for Turkish;
                if (sourceScript == "Turkish" && targetScript == "Latin")
                {
                    // Türkçe karakterleri Latin'e çevir;
                    // Convert Turkish characters to Latin;
                    return TurkishToEnglishCharacters(text);
                }
                else if (sourceScript == "Latin" && targetScript == "Turkish")
                {
                    // Latin karakterleri Türkçe'ye çevir;
                    // Convert Latin characters to Turkish;
                    return EnglishToTurkishCharacters(text);
                }

                // Diğer diller için genel transliterasyon;
                // General transliteration for other languages;
                var transliterationMap = GetTransliterationMap(sourceScript, targetScript);
                if (transliterationMap == null || !transliterationMap.Any())
                {
                    _logger.LogWarning("{SourceScript} -> {TargetScript} için transliterasyon haritası bulunamadı",
                        sourceScript, targetScript);
                    _logger.LogWarning("No transliteration map found for {SourceScript} -> {TargetScript}",
                        sourceScript, targetScript);
                    return text;
                }

                var result = new StringBuilder();
                foreach (char c in text)
                {
                    if (transliterationMap.TryGetValue(c.ToString(), out var transliterated))
                    {
                        result.Append(transliterated);
                    }
                    else;
                    {
                        result.Append(c);
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transliterasyon başarısız: {SourceScript} -> {TargetScript}",
                    sourceScript, targetScript);
                _logger.LogError(ex, "Transliteration failed: {SourceScript} -> {TargetScript}",
                    sourceScript, targetScript);
                return text;
            }
        }

        /// <summary>
        /// Verilen metin çifti için çeviri kalitesini tahmin eder;
        /// Estimates the translation quality for a given text pair;
        /// </summary>
        public async Task<TranslationQualityEstimation> EstimateQualityAsync(
            string sourceText,
            string translatedText,
            string sourceLanguage,
            string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translatedText))
                return TranslationQualityEstimation.Low;

            try
            {
                // Çeşitli kalite metriklerini hesapla;
                // Calculate various quality metrics;
                double lengthRatio = (double)translatedText.Length / sourceText.Length;
                double wordCountRatio = CalculateWordCountRatio(sourceText, translatedText);
                double semanticSimilarity = await CalculateSemanticSimilarityAsync(sourceText, translatedText);
                double grammaticalScore = CalculateGrammaticalScore(translatedText, targetLanguage);

                // Türkçe için özel dilbilgisi kontrolü;
                // Special grammar check for Turkish;
                if (targetLanguage == "tr")
                {
                    grammaticalScore = CalculateTurkishGrammaticalScore(translatedText);
                }

                // Puanları birleştir;
                // Combine scores;
                double overallScore = (lengthRatio * 0.2 + wordCountRatio * 0.2 +
                                      semanticSimilarity * 0.4 + grammaticalScore * 0.2);

                return overallScore switch;
                {
                    >= 0.8 => TranslationQualityEstimation.High,
                    >= 0.6 => TranslationQualityEstimation.Medium,
                    >= 0.4 => TranslationQualityEstimation.Low,
                    _ => TranslationQualityEstimation.VeryLow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Çeviri kalitesi tahmin edilemedi / Failed to estimate translation quality");
                return TranslationQualityEstimation.Unknown;
            }
        }

        /// <summary>
        /// Türkçe metin için dilbilgisi puanını hesaplar;
        /// Calculates grammar score for Turkish text;
        /// </summary>
        private double CalculateTurkishGrammaticalScore(string text)
        {
            double score = 0.5;

            // Büyük harf kontrolü;
            // Capitalization check;
            if (!string.IsNullOrEmpty(text) && char.IsUpper(text[0]))
                score += 0.1;

            // Noktalama kontrolü;
            // Punctuation check;
            if (!string.IsNullOrEmpty(text) && ".!?".Contains(text[^1]))
                score += 0.1;

            // Türkçe karakter kontrolü;
            // Turkish character check;
            var turkishChars = new HashSet<char> { 'ç', 'Ç', 'ğ', 'Ğ', 'ı', 'İ', 'ö', 'Ö', 'ş', 'Ş', 'ü', 'Ü' };
            int turkishCharCount = text.Count(c => turkishChars.Contains(c));
            int totalChars = text.Count(char.IsLetter);

            if (totalChars > 0 && turkishCharCount > 0)
                score += 0.1;

            // Tekrarlanan kelimeler kontrolü (yaygın çeviri hatası)
            // Repeated words check (common translation error)
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 3)
            {
                for (int i = 0; i < words.Length - 1; i++)
                {
                    if (words[i] == words[i + 1])
                    {
                        score -= 0.1;
                        break;
                    }
                }
            }

            return Math.Clamp(score, 0, 1);
        }

        /// <summary>
        /// Çeviri önbelleğini temizler;
        /// Clears the translation cache;
        /// </summary>
        public void ClearCache()
        {
            if (_cache is MemoryCache memoryCache)
            {
                memoryCache.Compact(1.0); // Önbelleğin %100'ünü temizle / Clear 100% of cache;
                CacheStatistics.EntriesCleared = CacheStatistics.EntriesCached;
                CacheStatistics.EntriesCached = 0;
                _logger.LogInformation("Çeviri önbelleği temizlendi / Translation cache cleared");
            }
        }

        /// <summary>
        /// Farklı bir çeviri sağlayıcısına geçer;
        /// Switches to a different translation provider;
        /// </summary>
        public bool SwitchProvider(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return false;

            if (!_providers.TryGetValue(providerId, out var provider) || !provider.IsEnabled)
            {
                _logger.LogWarning("Sağlayıcı {ProviderId} bulunamadı veya etkin değil / Provider {ProviderId} not found or not enabled", providerId);
                return false;
            }

            ActiveProvider = provider;
            _logger.LogInformation("Çeviri sağlayıcısı değiştirildi: {ProviderName} / Switched to translation provider: {ProviderName}", provider.Name);
            return true;
        }

        /// <summary>
        /// Çeviri yapılandırmasını günceller;
        /// Updates translation configuration;
        /// </summary>
        public void UpdateConfiguration(TranslationConfiguration configuration)
        {
            if (configuration == null)
                return;

            // Yapılandırmayı güncelle;
            // Update configuration;
            _configuration.MergeWith(configuration);

            // Yeni yapılandırmaya göre sağlayıcıları güncelle;
            // Update providers based on new configuration;
            foreach (var provider in _providers.Values)
            {
                provider.IsEnabled = IsProviderEnabled(provider.Id);
            }

            _logger.LogInformation("Çeviri yapılandırması güncellendi / Translation configuration updated");
        }

        /// <summary>
        /// Çeviriciyi varsayılan durumuna sıfırlar;
        /// Resets translator to default state;
        /// </summary>
        public void Reset()
        {
            ActiveProvider = GetDefaultProvider();
            SourceLanguage = "tr"; // Varsayılan kaynak dil Türkçe / Default source language Turkish;
            TargetLanguage = "en";
            Quality = TranslationQuality.Balanced;
            ClearCache();
            _logger.LogDebug("Çevirici varsayılan duruma sıfırlandı / Translator reset to default state");
        }

        #endregion;

        #region Private Methods (Diğer metodlar önceki implementasyondan aynı kalacak, sadece Türkçe özellikleri ekledik)
        #region Private Methods (Other methods remain the same from previous implementation, we only added Turkish features)

        private string GenerateCacheKey(string text, string sourceLang, string targetLang, TranslationContext context)
        {
            var keyBuilder = new StringBuilder();
            keyBuilder.Append($"translation:{sourceLang}:{targetLang}:");
            keyBuilder.Append(text.GetHashCode());

            if (context != null)
            {
                keyBuilder.Append($":domain:{context.Domain}");
                keyBuilder.Append($":formality:{context.FormalityLevel}");
            }

            return keyBuilder.ToString();
        }

        private TranslationProvider ChooseProvider(string sourceLanguage, string targetLanguage)
        {
            // Aktif sağlayıcı bu dilleri destekliyor mu kontrol et;
            // Check if active provider supports these languages;
            if (ActiveProvider.IsEnabled &&
                (sourceLanguage == "auto" || ActiveProvider.SupportedLanguages.Contains(sourceLanguage)) &&
                ActiveProvider.SupportedLanguages.Contains(targetLanguage))
            {
                return ActiveProvider;
            }

            // Bu diller için en iyi sağlayıcıyı bul;
            // Find best provider for these languages;
            var suitableProviders = _providers.Values;
                .Where(p => p.IsEnabled &&
                           (sourceLanguage == "auto" || p.SupportedLanguages.Contains(sourceLanguage)) &&
                           p.SupportedLanguages.Contains(targetLanguage))
                .OrderBy(p => p.Priority)
                .ToList();

            return suitableProviders.FirstOrDefault() ?? _providers["fallback"];
        }

        private bool IsProviderEnabled(string providerId)
        {
            return providerId switch;
            {
                "google" => !string.IsNullOrEmpty(_configuration.GoogleApiKey),
                "microsoft" => !string.IsNullOrEmpty(_configuration.MicrosoftApiKey),
                "deepl" => !string.IsNullOrEmpty(_configuration.DeepLApiKey),
                "fallback" => true,
                _ => false;
            };
        }

        private TranslationProvider GetProvider(string providerId)
        {
            return _providers.TryGetValue(providerId, out var provider) ? provider : null;
        }

        private async Task<string> TranslateWithProviderAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            TranslationProvider provider,
            TranslationContext context)
        {
            switch (provider.Id)
            {
                case "google":
                    return await TranslateWithGoogleAsync(text, sourceLanguage, targetLanguage, context);
                case "microsoft":
                    return await TranslateWithMicrosoftAsync(text, sourceLanguage, targetLanguage, context);
                case "deepl":
                    return await TranslateWithDeepLAsync(text, sourceLanguage, targetLanguage, context);
                default:
                    return await TranslateWithFallbackAsync(text, sourceLanguage, targetLanguage, context);
            }
        }

        private async Task<string> TranslateWithGoogleAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            TranslationContext context)
        {
            try
            {
                var requestBody = new;
                {
                    q = text,
                    source = sourceLanguage,
                    target = targetLanguage,
                    format = "text",
                    model = Quality == TranslationQuality.Premium ? "nmt" : "base"
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, provider.ApiEndpoint)
                {
                    Content = content;
                };

                request.Headers.Add("Authorization", $"Bearer {_configuration.GoogleApiKey}");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var translationResponse = JsonSerializer.Deserialize<GoogleTranslationResponse>(responseJson, _jsonOptions);

                return translationResponse?.Data?.Translations?.FirstOrDefault()?.TranslatedText ?? text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Google çevirisi başarısız / Google translation failed");
                throw new TranslationException($"Google çevirisi başarısız: {ex.Message} / Google translation failed: {ex.Message}", ex);
            }
        }

        private async Task<string> TranslateWithMicrosoftAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            TranslationContext context)
        {
            try
            {
                var requestBody = new[]
                {
                    new { Text = text }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var endpoint = $"{provider.ApiEndpoint}?api-version=3.0&from={sourceLanguage}&to={targetLanguage}";

                if (context?.Domain != null)
                {
                    endpoint += $"&domain={context.Domain}";
                }

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = content;
                };

                request.Headers.Add("Ocp-Apim-Subscription-Key", _configuration.MicrosoftApiKey);
                request.Headers.Add("Ocp-Apim-Subscription-Region", _configuration.MicrosoftRegion ?? "global");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var translationResponse = JsonSerializer.Deserialize<List<MicrosoftTranslationResponse>>(responseJson, _jsonOptions);

                return translationResponse?.FirstOrDefault()?.Translations?.FirstOrDefault()?.Text ?? text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Microsoft çevirisi başarısız / Microsoft translation failed");
                throw new TranslationException($"Microsoft çevirisi başarısız: {ex.Message} / Microsoft translation failed: {ex.Message}", ex);
            }
        }

        private async Task<string> TranslateWithDeepLAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            TranslationContext context)
        {
            try
            {
                var formData = new List<KeyValuePair<string, string>>
                {
                    new("text", text),
                    new("target_lang", targetLanguage.ToUpper())
                };

                if (sourceLanguage != "auto")
                {
                    formData.Add(new KeyValuePair<string, string>("source_lang", sourceLanguage.ToUpper()));
                }

                if (context?.FormalityLevel != null)
                {
                    formData.Add(new KeyValuePair<string, string>("formality", context.FormalityLevel));
                }

                var content = new FormUrlEncodedContent(formData);

                var endpoint = $"{provider.ApiEndpoint}?auth_key={_configuration.DeepLApiKey}";

                var response = await _httpClient.PostAsync(endpoint, content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var translationResponse = JsonSerializer.Deserialize<DeepLTranslationResponse>(responseJson, _jsonOptions);

                return translationResponse?.Translations?.FirstOrDefault()?.Text ?? text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeepL çevirisi başarısız / DeepL translation failed");
                throw new TranslationException($"DeepL çevirisi başarısız: {ex.Message} / DeepL translation failed: {ex.Message}", ex);
            }
        }

        private async Task<string> TranslateWithFallbackAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            TranslationContext context)
        {
            // Türkçe için özel sözlük;
            // Special dictionary for Turkish;
            var dictionary = GetTranslationDictionary(sourceLanguage, targetLanguage);
            if (dictionary == null || !dictionary.Any())
            {
                _logger.LogWarning("{SourceLang} -> {TargetLang} için sözlük bulunamadı / No dictionary found for {SourceLang} to {TargetLang}",
                    sourceLanguage, targetLanguage);
                return text;
            }

            // Kelime kelime çeviri (çok basit)
            // Word-by-word translation (very basic)
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var translatedWords = new List<string>();

            foreach (var word in words)
            {
                var cleanWord = CleanWordForTranslation(word);
                if (dictionary.TryGetValue(cleanWord, out var translation))
                {
                    translatedWords.Add(translation);
                }
                else;
                {
                    translatedWords.Add(word); // Çeviri bulunamazsa orijinalini koru / Keep original if no translation found;
                }
            }

            return string.Join(" ", translatedWords);
        }

        private Dictionary<string, string> GetTranslationDictionary(string sourceLang, string targetLang)
        {
            // Türkçe merkezli sözlük;
            // Turkish-centered dictionary;
            var dict = new Dictionary<string, string>();

            if (sourceLang == "tr" && targetLang == "en")
            {
                dict.Add("merhaba", "hello");
                dict.Add("teşekkür ederim", "thank you");
                dict.Add("lütfen", "please");
                dict.Add("evet", "yes");
                dict.Add("hayır", "no");
                dict.Add("günaydın", "good morning");
                dict.Add("iyi günler", "good day");
                dict.Add("iyi akşamlar", "good evening");
                dict.Add("iyi geceler", "good night");
                dict.Add("hoşça kal", "goodbye");
                dict.Add("güle güle", "goodbye");
                dict.Add("nasılsın", "how are you");
                dict.Add("iyiyim", "I am fine");
                dict.Add("adım", "my name is");
                dict.Add("anlamadım", "I don't understand");
                dict.Add("tekrar eder misin", "can you repeat");
                dict.Add("yardım", "help");
                dict.Add("acil", "emergency");
            }
            else if (sourceLang == "en" && targetLang == "tr")
            {
                dict.Add("hello", "merhaba");
                dict.Add("thank you", "teşekkür ederim");
                dict.Add("please", "lütfen");
                dict.Add("yes", "evet");
                dict.Add("no", "hayır");
                dict.Add("good morning", "günaydın");
                dict.Add("good day", "iyi günler");
                dict.Add("good evening", "iyi akşamlar");
                dict.Add("good night", "iyi geceler");
                dict.Add("goodbye", "hoşça kal");
                dict.Add("how are you", "nasılsın");
                dict.Add("I am fine", "iyiyim");
                dict.Add("my name is", "adım");
                dict.Add("I don't understand", "anlamadım");
                dict.Add("can you repeat", "tekrar eder misin");
                dict.Add("help", "yardım");
                dict.Add("emergency", "acil");
            }
            else if (sourceLang == "tr" && targetLang == "de")
            {
                dict.Add("merhaba", "hallo");
                dict.Add("teşekkür ederim", "danke");
                dict.Add("lütfen", "bitte");
                dict.Add("evet", "ja");
                dict.Add("hayır", "nein");
            }
            else if (sourceLang == "tr" && targetLang == "fr")
            {
                dict.Add("merhaba", "bonjour");
                dict.Add("teşekkür ederim", "merci");
                dict.Add("lütfen", "s'il vous plaît");
                dict.Add("evet", "oui");
                dict.Add("hayır", "non");
            }
            else if (sourceLang == "tr" && targetLang == "es")
            {
                dict.Add("merhaba", "hola");
                dict.Add("teşekkür ederim", "gracias");
                dict.Add("lütfen", "por favor");
                dict.Add("evet", "sí");
                dict.Add("hayır", "no");
            }

            return dict;
        }

        private async Task<IEnumerable<string>> TranslateSequentiallyAsync(
            List<string> texts,
            string targetLanguage,
            string sourceLanguage,
            TranslationContext context)
        {
            var results = new List<string>();

            foreach (var text in texts)
            {
                try
                {
                    var translated = await TranslateAsync(text, targetLanguage, sourceLanguage, context);
                    results.Add(translated);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Toplu çeviride metin çevrilemedi: {Text} / Failed to translate text in batch: {Text}", text);
                    results.Add(text); // Başarısız olursa orijinalini döndür / Return original on failure;
                }
            }

            return results;
        }

        private async Task<IEnumerable<string>> TranslateBatchWithProviderAsync(
            List<string> texts,
            string sourceLanguage,
            string targetLanguage,
            TranslationProvider provider,
            TranslationContext context)
        {
            // Uygulama sağlayıcının toplu API'sine bağlı olacaktır;
            // Implementation would depend on provider's batch API;
            // Şimdilik, sıralı çeviri kullan;
            // For now, use sequential translation;
            return await TranslateSequentiallyAsync(texts, targetLanguage, sourceLanguage, context);
        }

        private async Task<LanguageDetectionResult> DetectLanguageWithProviderAsync(string text, TranslationProvider provider)
        {
            try
            {
                switch (provider.Id)
                {
                    case "google":
                        return await DetectLanguageWithGoogleAsync(text);
                    case "microsoft":
                        return await DetectLanguageWithMicrosoftAsync(text);
                    case "deepl":
                        return await DetectLanguageWithDeepLAsync(text);
                    default:
                        return DetectLanguageHeuristic(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sağlayıcı {Provider} ile dil tespiti başarısız / Language detection with provider {Provider} failed", provider.Name);
                return DetectLanguageHeuristic(text);
            }
        }

        private async Task<LanguageDetectionResult> DetectLanguageWithGoogleAsync(string text)
        {
            var requestBody = new;
            {
                q = text;
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var endpoint = "https://translation.googleapis.com/language/translate/v2/detect";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content;
            };

            request.Headers.Add("Authorization", $"Bearer {_configuration.GoogleApiKey}");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var detectionResponse = JsonSerializer.Deserialize<GoogleDetectionResponse>(responseJson, _jsonOptions);

            var detection = detectionResponse?.Data?.Detections?.FirstOrDefault()?.FirstOrDefault();

            return new LanguageDetectionResult;
            {
                LanguageCode = detection?.Language ?? "tr",
                Confidence = detection?.Confidence ?? 0.5,
                IsReliable = detection?.Confidence > 0.8;
            };
        }

        private async Task<LanguageDetectionResult> DetectLanguageWithMicrosoftAsync(string text)
        {
            var requestBody = new[]
            {
                new { Text = text }
            };

            var jsonContent = JsonSerializer.Serialize(requestBody, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var endpoint = "https://api.cognitive.microsofttranslator.com/detect?api-version=3.0";
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = content;
            };

            request.Headers.Add("Ocp-Apim-Subscription-Key", _configuration.MicrosoftApiKey);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var detectionResponse = JsonSerializer.Deserialize<List<MicrosoftDetectionResponse>>(responseJson, _jsonOptions);

            var detection = detectionResponse?.FirstOrDefault();

            return new LanguageDetectionResult;
            {
                LanguageCode = detection?.Language ?? "tr",
                Confidence = detection?.Score ?? 0.5,
                IsReliable = detection?.Score > 0.8;
            };
        }

        private async Task<LanguageDetectionResult> DetectLanguageWithDeepLAsync(string text)
        {
            // Not: DeepL v2'de adanmış bir tespit uç noktası yok;
            // Note: DeepL doesn't have a dedicated detection endpoint in v2;
            // Bu basitleştirilmiş bir yaklaşım;
            // This is a simplified approach;
            return DetectLanguageHeuristic(text);
        }

        private LanguageDetectionResult DetectLanguageHeuristic(string text)
        {
            // Önce Türkçe kontrolü;
            // First check for Turkish;
            var turkishResult = DetectTurkishLanguage(text);
            if (turkishResult.Confidence > 0.6)
                return turkishResult;

            // Diğer diller için buluşsal tespit;
            // Heuristic detection for other languages;
            var score = new Dictionary<string, int>();

            // Karakter aralıklarına göre kontrol;
            // Check based on character ranges;
            if (text.Any(c => c >= '\u4e00' && c <= '\u9fff')) // Çince karakterler / Chinese characters;
                score["zh"] = 100;

            if (text.Any(c => c >= '\u3040' && c <= '\u309F')) // Hiragana;
                score["ja"] = 100;
            else if (text.Any(c => c >= '\u30A0' && c <= '\u30FF')) // Katakana;
                score["ja"] = 80;

            if (text.Any(c => c >= '\u0600' && c <= '\u06FF')) // Arapça / Arabic;
                score["ar"] = 100;

            if (text.Any(c => "àâçéèêëîïôùûüœ".Contains(c))) // Fransızca aksanlar / French diacritics;
                score["fr"] = 70;

            if (text.Any(c => "äöüß".Contains(c))) // Almanca umlaut'lar / German umlauts;
                score["de"] = 70;

            if (text.Any(c => "áéíóúñ".Contains(c))) // İspanyolca aksanlar / Spanish accents;
                score["es"] = 70;

            if (text.Any(c => "ąćęłńóśźż".Contains(c))) // Lehçe karakterler / Polish characters;
                score["pl"] = 70;

            // Ortak kelimelere göre kontrol;
            // Check based on common words;
            var commonWords = new Dictionary<string, List<string>>
            {
                ["en"] = new List<string> { "the", "and", "you", "that", "have", "for", "not", "with" },
                ["fr"] = new List<string> { "le", "la", "et", "vous", "que", "pour", "pas", "avec" },
                ["de"] = new List<string> { "der", "die", "das", "und", "sie", "dass", "für", "nicht" },
                ["es"] = new List<string> { "el", "la", "y", "que", "por", "no", "con", "los" },
                ["it"] = new List<string> { "il", "la", "e", "che", "per", "non", "con", "del" },
                ["ru"] = new List<string> { "и", "в", "не", "на", "я", "быть", "с", "что" },
                ["ar"] = new List<string> { "في", "من", "إلى", "على", "أن", "لا", "ما", "هو" }
            };

            var words = text.ToLower().Split(' ', '.', ',', '!', '?', ';', ':');
            foreach (var lang in commonWords.Keys)
            {
                int wordMatches = commonWords[lang].Count(word => words.Contains(word));
                if (!score.ContainsKey(lang))
                    score[lang] = 0;
                score[lang] += wordMatches * 10;
            }

            // Güçlü sinyal yoksa varsayılan olarak Türkçe;
            // Default to Turkish if no strong signals;
            if (!score.Any(s => s.Value > 50))
                score["tr"] = 60;

            var bestMatch = score.OrderByDescending(s => s.Value).First();

            return new LanguageDetectionResult;
            {
                LanguageCode = bestMatch.Key,
                Confidence = Math.Min(bestMatch.Value / 100.0, 1.0),
                IsReliable = bestMatch.Value > 70;
            };
        }

        private async Task<string> PostProcessTranslationAsync(
            string sourceText,
            string translatedText,
            string sourceLanguage,
            string targetLanguage,
            TranslationContext context)
        {
            if (string.IsNullOrEmpty(translatedText))
                return translatedText;

            var processedText = translatedText;

            // Yaygın çeviri hatalarını düzelt;
            // Fix common translation artifacts;
            processedText = FixTranslationArtifacts(processedText, sourceLanguage, targetLanguage);

            // Hedef dile göre biçimlendirme uygula;
            // Apply formatting based on target language;
            processedText = ApplyLanguageFormatting(processedText, targetLanguage);

            // Bağlama özgü adaptasyonlar uygula;
            // Apply context-specific adaptations;
            if (context != null)
            {
                processedText = ApplyContextAdaptations(processedText, context, targetLanguage);
            }

            // Doğru büyük harf kullanımını sağla;
            // Ensure proper capitalization;
            processedText = FixCapitalization(processedText, targetLanguage);

            // Fazla boşluğu kaldır;
            // Remove extra whitespace;
            processedText = Regex.Replace(processedText, @"\s+", " ").Trim();

            return processedText;
        }

        private async Task<string> GetFallbackTranslation(string text, string sourceLanguage, string targetLanguage)
        {
            // Aynı dilse orijinal metni notla döndür;
            // Return original text with note if same language;
            if (sourceLanguage == targetLanguage ||
                (sourceLanguage == "auto" && targetLanguage == "tr"))
                return text;

            // Basit sözlük araması dene;
            // Try simple dictionary lookup;
            try
            {
                return await TranslateWithFallbackAsync(text, sourceLanguage, targetLanguage, null);
            }
            catch
            {
                // Hata notuyla orijinal metni döndür;
                // Return original text with error note;
                return $"[Çeviri başarısız: {text}] / [Translation failed: {text}]";
            }
        }

        private string CleanWordForTranslation(string word)
        {
            return word.Trim().ToLower().TrimEnd('.', ',', '!', '?', ';', ':');
        }

        private Dictionary<string, string> GetTransliterationMap(string sourceScript, string targetScript)
        {
            // Kiril -> Latin transliterasyon haritası;
            // Cyrillic -> Latin transliteration map;
            if (sourceScript == "Cyrillic" && targetScript == "Latin")
            {
                return new Dictionary<string, string>
                {
                    ["а"] = "a",
                    ["б"] = "b",
                    ["в"] = "v",
                    ["г"] = "g",
                    ["д"] = "d",
                    ["е"] = "e",
                    ["ё"] = "yo",
                    ["ж"] = "zh",
                    ["з"] = "z",
                    ["и"] = "i",
                    ["й"] = "y",
                    ["к"] = "k",
                    ["л"] = "l",
                    ["м"] = "m",
                    ["н"] = "n",
                    ["о"] = "o",
                    ["п"] = "p",
                    ["р"] = "r",
                    ["с"] = "s",
                    ["т"] = "t",
                    ["у"] = "u",
                    ["ф"] = "f",
                    ["х"] = "kh",
                    ["ц"] = "ts",
                    ["ч"] = "ch",
                    ["ш"] = "sh",
                    ["щ"] = "shch",
                    ["ъ"] = "",
                    ["ы"] = "y",
                    ["ь"] = "",
                    ["э"] = "e",
                    ["ю"] = "yu",
                    ["я"] = "ya"
                };
            }

            // Arapça -> Latin transliterasyon haritası (basitleştirilmiş)
            // Arabic -> Latin transliteration map (simplified)
            if (sourceScript == "Arabic" && targetScript == "Latin")
            {
                return new Dictionary<string, string>
                {
                    ["ا"] = "a",
                    ["ب"] = "b",
                    ["ت"] = "t",
                    ["ث"] = "th",
                    ["ج"] = "j",
                    ["ح"] = "h",
                    ["خ"] = "kh",
                    ["د"] = "d",
                    ["ذ"] = "dh",
                    ["ر"] = "r",
                    ["ز"] = "z",
                    ["س"] = "s",
                    ["ش"] = "sh",
                    ["ص"] = "s",
                    ["ض"] = "d",
                    ["ط"] = "t",
                    ["ظ"] = "z",
                    ["ع"] = "'",
                    ["غ"] = "gh",
                    ["ف"] = "f",
                    ["ق"] = "q",
                    ["ك"] = "k",
                    ["ل"] = "l",
                    ["م"] = "m",
                    ["ن"] = "n",
                    ["ه"] = "h",
                    ["و"] = "w",
                    ["ي"] = "y"
                };
            }

            return null;
        }

        private double CalculateWordCountRatio(string sourceText, string translatedText)
        {
            int sourceWords = sourceText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            int translatedWords = translatedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (sourceWords == 0 || translatedWords == 0)
                return 0;

            double ratio = (double)translatedWords / sourceWords;
            return Math.Clamp(ratio, 0.1, 2.0); // Makul aralığa normalleştir / Normalize to reasonable range;
        }

        private async Task<double> CalculateSemanticSimilarityAsync(string sourceText, string translatedText)
        {
            // Üretimde, anlamsal benzerlik için NLP modelleri kullan;
            // In production, use NLP models for semantic similarity;
            // Bu basitleştirilmiş bir yer tutucu;
            // This is a simplified placeholder;

            // İçerik kelimelerinin örtüşmesi;
            // Overlap of content words;
            var sourceWords = GetContentWords(sourceText);
            var translatedWords = GetContentWords(translatedText);

            if (!sourceWords.Any() || !translatedWords.Any())
                return 0.5;

            int matches = sourceWords.Count(w => translatedWords.Contains(w));
            return (double)matches / Math.Max(sourceWords.Count, translatedWords.Count);
        }

        private HashSet<string> GetContentWords(string text)
        {
            var stopWords = new HashSet<string> { "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by" };

            return text.ToLower()
                .Split(' ', '.', ',', '!', '?', ';', ':')
                .Where(w => w.Length > 2 && !stopWords.Contains(w))
                .ToHashSet();
        }

        private double CalculateGrammaticalScore(string text, string languageCode)
        {
            // Basitleştirilmiş dilbilgisi kontrolü;
            // Simplified grammatical check;
            // Üretimde, dile özgü dilbilgisi denetleyicileri kullan;
            // In production, use language-specific grammar checkers;

            // Temel cümle yapısını kontrol et;
            // Check for basic sentence structure;
            bool hasCapitalLetter = !string.IsNullOrEmpty(text) && char.IsUpper(text[0]);
            bool hasEndingPunctuation = !string.IsNullOrEmpty(text) && ".!?".Contains(text[^1]);

            double score = 0.5;
            if (hasCapitalLetter) score += 0.2;
            if (hasEndingPunctuation) score += 0.2;

            // Tekrarlanan kelimeleri kontrol et (yaygın çeviri hatası)
            // Check for repeated words (common translation error)
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 3)
            {
                for (int i = 0; i < words.Length - 1; i++)
                {
                    if (words[i] == words[i + 1])
                    {
                        score -= 0.1;
                        break;
                    }
                }
            }

            return Math.Clamp(score, 0, 1);
        }

        private string FixTranslationArtifacts(string text, string sourceLang, string targetLang)
        {
            // Noktalama işaretlerinden önceki boşlukları düzelt (bazı çevirilerde yaygın)
            // Fix spaces before punctuation (common in some translations)
            text = Regex.Replace(text, @"\s+([.,!?;:])", "$1");

            // Çift boşlukları düzelt;
            // Fix double spaces;
            text = Regex.Replace(text, @"\s{2,}", " ");

            // Noktalama işaretlerinden sonra eksik boşlukları düzelt;
            // Fix missing spaces after punctuation;
            text = Regex.Replace(text, @"([.,!?;:])([A-Za-z])", "$1 $2");

            return text;
        }

        private string ApplyLanguageFormatting(string text, string languageCode)
        {
            var profile = GetLanguageProfile(languageCode);
            if (profile == null)
                return text;

            // Dile özgü tırnak işaretlerini uygula;
            // Apply language-specific quotation marks;
            if (profile.Code == "ja" || profile.Code == "zh-CN")
            {
                text = text.Replace('"', '「').Replace('"', '」');
            }

            // Türkçe için özel biçimlendirme;
            // Special formatting for Turkish;
            if (profile.Code == "tr")
            {
                // Türkçe tırnak işaretleri;
                // Turkish quotation marks;
                text = text.Replace('"', '“').Replace('"', '”');
            }

            return text;
        }

        private string ApplyContextAdaptations(string text, TranslationContext context, string targetLanguage)
        {
            if (context?.FormalityLevel != null)
            {
                text = AdjustFormality(text, context.FormalityLevel, targetLanguage);
            }

            if (context?.Domain != null)
            {
                text = ApplyDomainSpecificTerms(text, context.Domain, targetLanguage);
            }

            return text;
        }

        private string AdjustFormality(string text, string formalityLevel, string targetLanguage)
        {
            // Basitleştirilmiş resmiyet ayarlaması;
            // Simplified formality adjustment;
            // Üretimde, daha karmaşık yöntemler kullan;
            // In production, use more sophisticated methods;

            if (formalityLevel == "formal")
            {
                // Metni daha resmi yap;
                // Make text more formal;
                if (targetLanguage == "en")
                {
                    text = text.Replace("you can", "one may");
                    text = text.Replace("you should", "it is advisable to");
                    text = text.Replace("I think", "in my opinion");
                }
                else if (targetLanguage == "tr")
                {
                    text = text.Replace("sen", "siz");
                    text = text.Replace("yapabilirsin", "yapabilirsiniz");
                    text = text.Replace("bence", "benim görüşüme göre");
                }
            }
            else if (formalityLevel == "informal")
            {
                // Metni daha az resmi yap;
                // Make text less formal;
                if (targetLanguage == "en")
                {
                    text = text.Replace("one may", "you can");
                    text = text.Replace("it is advisable to", "you should");
                    text = text.Replace("in my opinion", "I think");
                }
                else if (targetLanguage == "tr")
                {
                    text = text.Replace("siz", "sen");
                    text = text.Replace("yapabilirsiniz", "yapabilirsin");
                    text = text.Replace("benim görüşüme göre", "bence");
                }
            }

            return text;
        }

        private string ApplyDomainSpecificTerms(string text, string domain, string targetLanguage)
        {
            // Basitleştirilmiş alan adaptasyonu;
            // Simplified domain adaptation;
            // Üretimde, alana özgü sözlükler kullan;
            // In production, use domain-specific dictionaries;

            if (domain == "legal" && targetLanguage == "en")
            {
                text = text.Replace("agreement", "contractual agreement");
                text = text.Replace("party", "contracting party");
            }
            else if (domain == "legal" && targetLanguage == "tr")
            {
                text = text.Replace("anlaşma", "sözleşme");
                text = text.Replace("taraf", "sözleşme tarafı");
            }
            else if (domain == "medical" && targetLanguage == "en")
            {
                text = text.Replace("sick", "afflicted");
                text = text.Replace("medicine", "pharmaceutical");
            }
            else if (domain == "medical" && targetLanguage == "tr")
            {
                text = text.Replace("hasta", "rahatsız");
                text = text.Replace("ilaç", "farmasötik ürün");
            }

            return text;
        }

        private string FixCapitalization(string text, string targetLanguage)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // İlk harfi büyük yap;
            // Capitalize first letter;
            if (text.Length > 0 && char.IsLower(text[0]))
            {
                text = char.ToUpper(text[0]) + text[1..];
            }

            // Dile özgü büyük harf kuralları;
            // Language-specific capitalization rules;
            if (targetLanguage == "de")
            {
                // Almanca tüm isimleri büyük harfle yazar;
                // German capitalizes all nouns;
                // Bu basitleştirilmiş bir versiyon;
                // This is a simplified version;
                var nouns = new[] { "der", "die", "das", "ein", "eine" };
                var words = text.Split(' ');
                for (int i = 0; i < words.Length; i++)
                {
                    if (i > 0 && nouns.Contains(words[i - 1].ToLower()))
                    {
                        if (words[i].Length > 0)
                        {
                            words[i] = char.ToUpper(words[i][0]) + words[i][1..];
                        }
                    }
                }
                text = string.Join(" ", words);
            }

            // Türkçe büyük harf kuralları (daha önce uygulandı)
            // Turkish capitalization rules (already applied earlier)

            return text;
        }

        #endregion;

        #endregion;

        #region IDisposable Implementation;

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
                    // Yönetilen kaynakları temizle;
                    // Dispose managed resources;
                    _httpClient?.Dispose();
                    Reset();
                    _logger.LogInformation("Translator temizlendi / Translator disposed");
                }

                _disposed = true;
            }
        }

        ~Translator()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types (Önceki implementasyondan aynı, bazılarına Türkçe özellikler eklendi)
        #region Nested Types (Same from previous implementation, some with Turkish features added)

        public class TranslationProvider;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ApiEndpoint { get; set; }
            public bool SupportsAutoDetect { get; set; }
            public bool SupportsBatch { get; set; }
            public int MaxTextLength { get; set; }
            public HashSet<string> SupportedLanguages { get; set; } = new HashSet<string>();
            public int Priority { get; set; }
            public bool IsEnabled { get; set; }
        }

        public class LanguageProfile;
        {
            public string Code { get; set; }
            public string Name { get; set; }
            public string NativeName { get; set; }
            public TextDirection Direction { get; set; }
            public string CharacterSet { get; set; }
            public string WordOrder { get; set; }
            public string SentenceStructure { get; set; }
            public Dictionary<string, double> FormalityLevels { get; set; } = new Dictionary<string, double>();
            public Dictionary<string, string> CommonFormats { get; set; } = new Dictionary<string, string>();
            public HashSet<char> SpecialCharacters { get; set; } = new HashSet<char>();
            public bool IsAgglutinative { get; set; }
            public bool VowelHarmony { get; set; }
        }

        public class CulturalProfile;
        {
            public string CultureCode { get; set; }
            public string LanguageCode { get; set; }
            public string Region { get; set; }
            public string TimeZone { get; set; }
            public string DateFormat { get; set; }
            public string TimeFormat { get; set; }
            public string NumberFormat { get; set; }
            public string CurrencyFormat { get; set; }
            public string CurrencySymbol { get; set; }
            public char DecimalSeparator { get; set; }
            public char ThousandsSeparator { get; set; }
            public MeasurementSystem MeasurementSystem { get; set; }
            public TemperatureUnit TemperatureUnit { get; set; }
            public FormalityLevel FormalityLevel { get; set; }
            public CommunicationStyle CommunicationStyle { get; set; }
            public string Greeting { get; set; }
            public string FormalGreeting { get; set; }
            public string InformalGreeting { get; set; }
        }

        public class LanguageDetectionResult;
        {
            public string LanguageCode { get; set; }
            public double Confidence { get; set; }
            public bool IsReliable { get; set; }
            public List<AlternativeLanguage> Alternatives { get; set; } = new List<AlternativeLanguage>();
        }

        public class AlternativeLanguage;
        {
            public string LanguageCode { get; set; }
            public double Confidence { get; set; }
        }

        public class TranslationContext;
        {
            public string Domain { get; set; } // örn: "legal", "medical", "technical" / e.g., "legal", "medical", "technical"
            public string FormalityLevel { get; set; } // "formal", "informal", "neutral"
            public Dictionary<string, string> Glossary { get; set; } = new Dictionary<string, string>();
            public string Audience { get; set; } // "general", "expert", "children"
            public bool PreserveFormatting { get; set; }
        }

        public class TranslationCacheStatistics;
        {
            public int TotalTranslations { get; set; }
            public int Hits { get; set; }
            public int Misses { get; set; }
            public int EntriesCached { get; set; }
            public int EntriesCleared { get; set; }

            public double HitRate => TotalTranslations > 0 ? (double)Hits / TotalTranslations : 0;
        }

        public class UserTranslationPreferences;
        {
            public static UserTranslationPreferences Default => new UserTranslationPreferences;
            {
                PreferredProvider = "auto",
                QualityPreference = TranslationQuality.Balanced,
                EnableCaching = true,
                AutoDetectLanguage = true,
                ShowTransliteration = false,
                LearningEnabled = true,
                DefaultSourceLanguage = "tr",
                DefaultTargetLanguage = "en"
            };

            public string PreferredProvider { get; set; }
            public TranslationQuality QualityPreference { get; set; }
            public bool EnableCaching { get; set; }
            public bool AutoDetectLanguage { get; set; }
            public bool ShowTransliteration { get; set; }
            public bool LearningEnabled { get; set; }
            public string DefaultSourceLanguage { get; set; }
            public string DefaultTargetLanguage { get; set; }
            public List<string> FrequentlyUsedLanguages { get; set; } = new List<string>();
            public Dictionary<string, string> CustomTranslations { get; set; } = new Dictionary<string, string>();
        }

        private class GoogleTranslationResponse;
        {
            public TranslationData Data { get; set; }

            public class TranslationData;
            {
                public List<Translation> Translations { get; set; }
            }

            public class Translation;
            {
                public string TranslatedText { get; set; }
                public string DetectedSourceLanguage { get; set; }
            }
        }

        private class GoogleDetectionResponse;
        {
            public DetectionData Data { get; set; }

            public class DetectionData;
            {
                public List<List<Detection>> Detections { get; set; }
            }

            public class Detection;
            {
                public string Language { get; set; }
                public double Confidence { get; set; }
                public bool IsReliable { get; set; }
            }
        }

        private class MicrosoftTranslationResponse;
        {
            public List<Translation> Translations { get; set; }
            public string DetectedLanguage { get; set; }

            public class Translation;
            {
                public string Text { get; set; }
                public string To { get; set; }
            }
        }

        private class MicrosoftDetectionResponse;
        {
            public string Language { get; set; }
            public double Score { get; set; }
            public bool IsTranslationSupported { get; set; }
            public bool IsTransliterationSupported { get; set; }
        }

        private class DeepLTranslationResponse;
        {
            public List<Translation> Translations { get; set; }

            public class Translation;
            {
                public string DetectedSourceLanguage { get; set; }
                public string Text { get; set; }
            }
        }

        #endregion;
    }

    #region Supporting Types;

    public class TranslationConfiguration;
    {
        public static TranslationConfiguration Default => new TranslationConfiguration;
        {
            GoogleApiKey = null,
            MicrosoftApiKey = null,
            MicrosoftRegion = "global",
            DeepLApiKey = null,
            CacheDurationMinutes = 60,
            EnableCaching = true,
            RetryAttempts = 3,
            TimeoutSeconds = 30,
            EnableFallback = true,
            DefaultQuality = TranslationQuality.Balanced,
            DefaultToTurkish = true,
            SimplifyTurkishSentences = true,
            FixTurkishWordOrder = true;
        };

        public string GoogleApiKey { get; set; }
        public string MicrosoftApiKey { get; set; }
        public string MicrosoftRegion { get; set; }
        public string DeepLApiKey { get; set; }
        public int CacheDurationMinutes { get; set; }
        public bool EnableCaching { get; set; }
        public int RetryAttempts { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool EnableFallback { get; set; }
        public TranslationQuality DefaultQuality { get; set; }

        // Türkçe özel ayarlar / Turkish specific settings;
        public bool DefaultToTurkish { get; set; }
        public bool SimplifyTurkishSentences { get; set; }
        public bool FixTurkishWordOrder { get; set; }

        public void MergeWith(TranslationConfiguration other)
        {
            if (other == null) return;

            if (!string.IsNullOrEmpty(other.GoogleApiKey)) GoogleApiKey = other.GoogleApiKey;
            if (!string.IsNullOrEmpty(other.MicrosoftApiKey)) MicrosoftApiKey = other.MicrosoftApiKey;
            if (!string.IsNullOrEmpty(other.MicrosoftRegion)) MicrosoftRegion = other.MicrosoftRegion;
            if (!string.IsNullOrEmpty(other.DeepLApiKey)) DeepLApiKey = other.DeepLApiKey;

            CacheDurationMinutes = other.CacheDurationMinutes;
            EnableCaching = other.EnableCaching;
            RetryAttempts = other.RetryAttempts;
            TimeoutSeconds = other.TimeoutSeconds;
            EnableFallback = other.EnableFallback;
            DefaultQuality = other.DefaultQuality;

            // Türkçe ayarlar / Turkish settings;
            DefaultToTurkish = other.DefaultToTurkish;
            SimplifyTurkishSentences = other.SimplifyTurkishSentences;
            FixTurkishWordOrder = other.FixTurkishWordOrder;
        }
    }

    public enum TranslationQuality;
    {
        Fast,      // Hızı doğruluğa tercih et / Prioritize speed over accuracy;
        Balanced,  // Hız ve doğruluk arasında denge / Balance between speed and accuracy;
        Accurate,  // Doğruluğu hıza tercih et / Prioritize accuracy over speed;
        Premium    // En yüksek doğruluk için premium modeller kullan / Use premium models for highest accuracy;
    }

    public enum TranslationQualityEstimation;
    {
        Unknown,
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh;
    }

    public enum TextDirection;
    {
        LeftToRight,
        RightToLeft,
        TopToBottom;
    }

    public enum MeasurementSystem;
    {
        Metric,
        Imperial,
        USCustomary;
    }

    public enum TemperatureUnit;
    {
        Celsius,
        Fahrenheit,
        Kelvin;
    }

    public enum FormalityLevel;
    {
        VeryFormal,
        Formal,
        Neutral,
        Informal,
        VeryInformal;
    }

    public enum CommunicationStyle;
    {
        Direct,
        Indirect,
        Neutral;
    }

    #endregion;

    #region Interfaces;

    public interface ITranslator : IDisposable
    {
        Task<string> TranslateAsync(string text, string targetLanguage, string sourceLanguage = null, TranslationContext context = null);
        Task<IEnumerable<string>> TranslateBatchAsync(IEnumerable<string> texts, string targetLanguage, string sourceLanguage = null, TranslationContext context = null);
        Task<LanguageDetectionResult> DetectLanguageAsync(string text);
        IEnumerable<string> GetSupportedLanguages(string providerId = null);
        LanguageProfile GetLanguageProfile(string languageCode);
        CulturalProfile GetCulturalProfile(string cultureCode);
        Task<string> TransliterateAsync(string text, string sourceScript, string targetScript);
        Task<TranslationQualityEstimation> EstimateQualityAsync(string sourceText, string translatedText, string sourceLanguage, string targetLanguage);
        void ClearCache();
        bool SwitchProvider(string providerId);
        void UpdateConfiguration(TranslationConfiguration configuration);
        void Reset();

        // Türkçe özel metodlar / Turkish specific methods;
        string TurkishToEnglishCharacters(string text);
        string EnglishToTurkishCharacters(string text);

        TranslationQuality Quality { get; set; }
        TranslationCacheStatistics CacheStatistics { get; }
        UserTranslationPreferences UserPreferences { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class TranslationException : Exception
    {
        public TranslationException(string message) : base(message) { }
        public TranslationException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
