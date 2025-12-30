using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NEDA.Core.Logging;
using NEDA.Core.Common.Utilities;
using NEDA.Brain.NLP_Engine.Tokenization.LanguageModels;

namespace NEDA.Brain.NLP_Engine.Tokenization;
{
    /// <summary>
    /// Dil tespit motoru - Çoklu dil destekli, yüksek doğruluklu dil tanımlama sistemi;
    /// </summary>
    public class LanguageDetector : ILanguageDetector, IDisposable;
    {
        #region Constants and Fields;

        private const int DefaultNgramSize = 3;
        private const double ConfidenceThreshold = 0.1;
        private const int MaxTextLengthForAnalysis = 10000;

        private readonly ILogger _logger;
        private readonly LanguageModelRepository _modelRepository;
        private readonly Dictionary<string, LanguageProfile> _languageProfiles;
        private readonly object _syncLock = new object();
        private bool _isInitialized;
        private readonly PerformanceMonitor _performanceMonitor;

        #endregion;

        #region Properties;

        /// <summary>
        /// Desteklenen dillerin listesi;
        /// </summary>
        public IReadOnlyList<LanguageInfo> SupportedLanguages { get; private set; }

        /// <summary>
        /// Minimum güven eşiği;
        /// </summary>
        public double MinimumConfidence { get; set; } = 0.15;

        /// <summary>
        /// Maksimum tespit edilecek dil sayısı;
        /// </summary>
        public int MaxLanguages { get; set; } = 5;

        #endregion;

        #region Constructors;

        /// <summary>
        /// LanguageDetector sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="modelRepository">Dil model repository</param>
        public LanguageDetector(ILogger logger, LanguageModelRepository modelRepository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _modelRepository = modelRepository ?? throw new ArgumentNullException(nameof(modelRepository));
            _languageProfiles = new Dictionary<string, LanguageProfile>();
            _performanceMonitor = new PerformanceMonitor("LanguageDetector");

            Initialize();
        }

        /// <summary>
        /// Varsayılan ayarlarla LanguageDetector oluşturur;
        /// </summary>
        public LanguageDetector() : this(
            LogManager.GetLogger(typeof(LanguageDetector)),
            LanguageModelRepository.Default)
        {
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Metnin dilini tespit eder;
        /// </summary>
        /// <param name="text">Analiz edilecek metin</param>
        /// <returns>Dil tespit sonucu</returns>
        public LanguageDetectionResult DetectLanguage(string text)
        {
            return DetectLanguage(text, new LanguageDetectionOptions());
        }

        /// <summary>
        /// Metnin dilini tespit eder (gelişmiş seçeneklerle)
        /// </summary>
        /// <param name="text">Analiz edilecek metin</param>
        /// <param name="options">Dil tespit seçenekleri</param>
        /// <returns>Dil tespit sonucu</returns>
        public LanguageDetectionResult DetectLanguage(string text, LanguageDetectionOptions options)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Warning("Boş metin dil tespiti için gönderildi");
                return LanguageDetectionResult.Empty;
            }

            using (_performanceMonitor.StartOperation("DetectLanguage"))
            {
                try
                {
                    EnsureInitialized();

                    // Metni temizle ve normalize et;
                    string processedText = PreprocessText(text, options);

                    if (string.IsNullOrWhiteSpace(processedText))
                    {
                        _logger.Warning("İşlem sonrası boş metin");
                        return LanguageDetectionResult.Empty;
                    }

                    // Çok kısa metinler için özel işlem;
                    if (processedText.Length < 10)
                    {
                        return HandleShortText(processedText, options);
                    }

                    // Dil tespitini gerçekleştir;
                    var detectionResult = PerformLanguageDetection(processedText, options);

                    // Sonuçları doğrula;
                    detectionResult = ValidateDetectionResult(detectionResult, options);

                    _logger.Debug($"Dil tespit tamamlandı: {detectionResult.PrimaryLanguage?.Name} " +
                                 $"(Güven: {detectionResult.Confidence:P2})");

                    return detectionResult;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Dil tespit sırasında hata: {ex.Message}", ex);
                    return LanguageDetectionResult.CreateErrorResult(ex);
                }
            }
        }

        /// <summary>
        /// Metindeki birden fazla dili tespit eder;
        /// </summary>
        /// <param name="text">Analiz edilecek metin</param>
        /// <returns>Çoklu dil tespit sonuçları</returns>
        public MultiLanguageDetectionResult DetectMultipleLanguages(string text)
        {
            return DetectMultipleLanguages(text, new MultiLanguageDetectionOptions());
        }

        /// <summary>
        /// Metindeki birden fazla dili tespit eder (gelişmiş seçeneklerle)
        /// </summary>
        /// <param name="text">Analiz edilecek metin</param>
        /// <param name="options">Çoklu dil tespit seçenekleri</param>
        /// <returns>Çoklu dil tespit sonuçları</returns>
        public MultiLanguageDetectionResult DetectMultipleLanguages(string text, MultiLanguageDetectionOptions options)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return MultiLanguageDetectionResult.Empty;
            }

            using (_performanceMonitor.StartOperation("DetectMultipleLanguages"))
            {
                try
                {
                    EnsureInitialized();

                    string processedText = PreprocessText(text, options);

                    // N-gram analizi yap;
                    var ngramFrequencies = ExtractNgramFrequencies(processedText, options.NgramSize);

                    // Her dil için skor hesapla;
                    var languageScores = CalculateLanguageScores(ngramFrequencies, options);

                    // En yüksek skorlu dilleri al;
                    var topLanguages = GetTopLanguages(languageScores, options.MaxLanguages);

                    // Dil sınırlarını belirle (eğer isteniyorsa)
                    var languageSegments = options.DetectBoundaries;
                        ? DetectLanguageBoundaries(text, topLanguages, options)
                        : new List<LanguageSegment>();

                    var result = new MultiLanguageDetectionResult;
                    {
                        Languages = topLanguages,
                        LanguageSegments = languageSegments,
                        IsMixedText = topLanguages.Count > 1,
                        DetectionMethod = options.DetectionMethod,
                        Timestamp = DateTime.UtcNow;
                    };

                    _logger.Info($"Çoklu dil tespit tamamlandı: {result.Languages.Count} dil bulundu");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Çoklu dil tespit sırasında hata: {ex.Message}", ex);
                    return MultiLanguageDetectionResult.CreateErrorResult(ex);
                }
            }
        }

        /// <summary>
        /// Dil desteğini kontrol eder;
        /// </summary>
        /// <param name="languageCode">Dil kodu (ISO 639-1)</param>
        /// <returns>Dil destekleniyorsa true</returns>
        public bool IsLanguageSupported(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return false;

            lock (_syncLock)
            {
                return _languageProfiles.ContainsKey(languageCode.ToLowerInvariant());
            }
        }

        /// <summary>
        /// Dil modelini günceller;
        /// </summary>
        /// <param name="languageCode">Dil kodu</param>
        /// <param name="trainingTexts">Eğitim metinleri</param>
        public void UpdateLanguageModel(string languageCode, IEnumerable<string> trainingTexts)
        {
            if (!IsLanguageSupported(languageCode))
            {
                throw new ArgumentException($"Desteklenmeyen dil kodu: {languageCode}", nameof(languageCode));
            }

            lock (_syncLock)
            {
                var profile = _languageProfiles[languageCode.ToLowerInvariant()];
                var newNgrams = BuildNgramProfile(trainingTexts, DefaultNgramSize);
                profile.UpdateProfile(newNgrams);

                _logger.Info($"{languageCode} dil modeli güncellendi");
            }
        }

        /// <summary>
        /// Özel dil profili ekler;
        /// </summary>
        /// <param name="languageInfo">Dil bilgisi</param>
        /// <param name="ngramProfile">N-gram profili</param>
        public void AddCustomLanguageProfile(LanguageInfo languageInfo, Dictionary<string, double> ngramProfile)
        {
            if (languageInfo == null)
                throw new ArgumentNullException(nameof(languageInfo));

            if (ngramProfile == null || ngramProfile.Count == 0)
                throw new ArgumentException("N-gram profili boş olamaz", nameof(ngramProfile));

            lock (_syncLock)
            {
                string key = languageInfo.Code.ToLowerInvariant();

                if (_languageProfiles.ContainsKey(key))
                {
                    _logger.Warning($"{languageInfo.Code} dil profili zaten mevcut, güncelleniyor");
                }

                var profile = new LanguageProfile(languageInfo, ngramProfile);
                _languageProfiles[key] = profile;

                // Desteklenen diller listesini güncelle;
                UpdateSupportedLanguagesList();

                _logger.Info($"Özel dil profili eklendi: {languageInfo.Name} ({languageInfo.Code})");
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Dil tespit motorunu başlatır;
        /// </summary>
        private void Initialize()
        {
            if (_isInitialized)
                return;

            lock (_syncLock)
            {
                if (_isInitialized)
                    return;

                try
                {
                    _logger.Info("LanguageDetector başlatılıyor...");

                    // Dil profillerini yükle;
                    LoadLanguageProfiles();

                    // Desteklenen diller listesini oluştur;
                    UpdateSupportedLanguagesList();

                    _isInitialized = true;
                    _logger.Info($"LanguageDetector başlatıldı. {SupportedLanguages.Count} dil destekleniyor.");
                }
                catch (Exception ex)
                {
                    _logger.Error($"LanguageDetector başlatma hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Dil profillerini yükler;
        /// </summary>
        private void LoadLanguageProfiles()
        {
            var languages = _modelRepository.GetAvailableLanguages();

            foreach (var languageInfo in languages)
            {
                try
                {
                    var ngramProfile = _modelRepository.LoadLanguageProfile(languageInfo.Code);

                    if (ngramProfile != null && ngramProfile.Count > 0)
                    {
                        var profile = new LanguageProfile(languageInfo, ngramProfile);
                        _languageProfiles[languageInfo.Code.ToLowerInvariant()] = profile;

                        _logger.Debug($"{languageInfo.Name} dil profili yüklendi: {ngramProfile.Count} n-gram");
                    }
                    else;
                    {
                        _logger.Warning($"{languageInfo.Name} dil profili boş veya geçersiz");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"{languageInfo.Name} dil profili yüklenirken hata: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Başlatıldığından emin olur;
        /// </summary>
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                Initialize();
            }
        }

        /// <summary>
        /// Desteklenen diller listesini günceller;
        /// </summary>
        private void UpdateSupportedLanguagesList()
        {
            SupportedLanguages = _languageProfiles.Values;
                .Select(p => p.LanguageInfo)
                .OrderBy(l => l.Name)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Metni ön işlemden geçirir;
        /// </summary>
        private string PreprocessText(string text, LanguageDetectionOptions options)
        {
            string processed = text;

            // Uzun metinleri kısalt;
            if (processed.Length > MaxTextLengthForAnalysis && options.TruncateLongText)
            {
                processed = processed.Substring(0, MaxTextLengthForAnalysis);
                _logger.Debug($"Metin {MaxTextLengthForAnalysis} karaktere kısaltıldı");
            }

            // Normalizasyon;
            if (options.NormalizeText)
            {
                processed = TextNormalizer.Normalize(processed, options.NormalizationOptions);
            }

            // Gereksiz boşlukları temizle;
            processed = Regex.Replace(processed, @"\s+", " ").Trim();

            return processed;
        }

        /// <summary>
        /// Çok kısa metinler için özel işlem;
        /// </summary>
        private LanguageDetectionResult HandleShortText(string text, LanguageDetectionOptions options)
        {
            // Kısa metinler için karakter dağılımı analizi;
            var charDistribution = AnalyzeCharacterDistribution(text);

            // Özel kısa metin kuralları;
            var ruleBasedResult = ApplyShortTextRules(text, charDistribution);

            if (ruleBasedResult.Confidence >= MinimumConfidence)
            {
                return ruleBasedResult;
            }

            // Yeterli güven yoksa, varsayılan dil veya belirsiz döndür;
            return options.DefaultLanguage != null;
                ? new LanguageDetectionResult(options.DefaultLanguage, 0.1, "Short text, using default")
                : LanguageDetectionResult.Unknown;
        }

        /// <summary>
        /// Dil tespitini gerçekleştirir;
        /// </summary>
        private LanguageDetectionResult PerformLanguageDetection(string text, LanguageDetectionOptions options)
        {
            // Farklı tespit yöntemlerini kullan;
            var results = new List<LanguageDetectionResult>();

            // Yöntem 1: N-gram analizi;
            if (options.UseNgramAnalysis)
            {
                var ngramResult = DetectByNgramAnalysis(text, options);
                if (ngramResult.IsValid)
                    results.Add(ngramResult);
            }

            // Yöntem 2: Karakter seti analizi;
            if (options.UseCharacterSetAnalysis)
            {
                var charsetResult = DetectByCharacterSet(text, options);
                if (charsetResult.IsValid)
                    results.Add(charsetResult);
            }

            // Yöntem 3: Sözcük dağılımı analizi;
            if (options.UseWordDistribution && text.Length > 50)
            {
                var wordResult = DetectByWordDistribution(text, options);
                if (wordResult.IsValid)
                    results.Add(wordResult);
            }

            // Yöntem 4: Özel kurallar;
            var ruleResult = ApplyDetectionRules(text, options);
            if (ruleResult.IsValid)
                results.Add(ruleResult);

            // Sonuçları birleştir;
            return CombineDetectionResults(results, options);
        }

        /// <summary>
        /// N-gram analizi ile dil tespiti;
        /// </summary>
        private LanguageDetectionResult DetectByNgramAnalysis(string text, LanguageDetectionOptions options)
        {
            var ngramFrequencies = ExtractNgramFrequencies(text, options.NgramSize);

            double bestScore = 0;
            LanguageInfo bestLanguage = null;
            var scores = new Dictionary<string, double>();

            foreach (var profile in _languageProfiles.Values)
            {
                double score = CalculateSimilarityScore(ngramFrequencies, profile.NgramProfile);
                scores[profile.LanguageInfo.Code] = score;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLanguage = profile.LanguageInfo;
                }
            }

            // Skoru normalize et;
            double confidence = NormalizeScore(bestScore, scores.Values);

            return new LanguageDetectionResult(bestLanguage, confidence, "N-gram analysis");
        }

        /// <summary>
        /// Karakter seti analizi ile dil tespiti;
        /// </summary>
        private LanguageDetectionResult DetectByCharacterSet(string text, LanguageDetectionOptions options)
        {
            var charSet = new HashSet<char>();
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
                {
                    charSet.Add(char.ToLowerInvariant(c));
                }
            }

            // Dil karakter setleri ile karşılaştır;
            var matches = new Dictionary<LanguageInfo, int>();

            foreach (var profile in _languageProfiles.Values)
            {
                int matchCount = profile.LanguageInfo.CommonCharacters;
                    .Count(c => charSet.Contains(c));

                double matchRatio = (double)matchCount / Math.Max(1, charSet.Count);
                matches[profile.LanguageInfo] = (int)(matchRatio * 100);
            }

            var bestMatch = matches.OrderByDescending(kv => kv.Value).FirstOrDefault();

            if (bestMatch.Key != null && bestMatch.Value > 30) // %30 eşik değeri;
            {
                double confidence = bestMatch.Value / 100.0;
                return new LanguageDetectionResult(bestMatch.Key, confidence, "Character set analysis");
            }

            return LanguageDetectionResult.Unknown;
        }

        /// <summary>
        /// Sözcük dağılımı analizi ile dil tespiti;
        /// </summary>
        private LanguageDetectionResult DetectByWordDistribution(string text, LanguageDetectionOptions options)
        {
            // Basit kelime ayırma (gerçek tokenizer yerine)
            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                           .Take(100) // İlk 100 kelime;
                           .ToList();

            if (words.Count < 5)
                return LanguageDetectionResult.Unknown;

            // Ortak kelimeleri say;
            var commonWordMatches = new Dictionary<LanguageInfo, int>();

            foreach (var profile in _languageProfiles.Values)
            {
                int matchCount = 0;
                foreach (var word in words)
                {
                    if (profile.LanguageInfo.CommonWords.Contains(word.ToLowerInvariant()))
                    {
                        matchCount++;
                    }
                }

                if (matchCount > 0)
                {
                    commonWordMatches[profile.LanguageInfo] = matchCount;
                }
            }

            if (commonWordMatches.Any())
            {
                var bestMatch = commonWordMatches.OrderByDescending(kv => kv.Value).First();
                double confidence = Math.Min(bestMatch.Value / 10.0, 0.9); // Maksimum %90 güven;

                return new LanguageDetectionResult(bestMatch.Key, confidence, "Word distribution analysis");
            }

            return LanguageDetectionResult.Unknown;
        }

        /// <summary>
        /// Özel tespit kurallarını uygular;
        /// </summary>
        private LanguageDetectionResult ApplyDetectionRules(string text, LanguageDetectionOptions options)
        {
            // Unicode blok analizi;
            var unicodeBlocks = AnalyzeUnicodeBlocks(text);

            // Dil spesifik kurallar;
            foreach (var rule in LanguageDetectionRules.GetAllRules())
            {
                if (rule.Matches(text, unicodeBlocks))
                {
                    return new LanguageDetectionResult(
                        rule.Language,
                        rule.Confidence,
                        $"Rule: {rule.RuleName}"
                    );
                }
            }

            return LanguageDetectionResult.Unknown;
        }

        /// <summary>
        /// Tespit sonuçlarını birleştirir;
        /// </summary>
        private LanguageDetectionResult CombineDetectionResults(
            List<LanguageDetectionResult> results,
            LanguageDetectionOptions options)
        {
            if (!results.Any(r => r.IsValid))
                return options.DefaultLanguage != null;
                    ? new LanguageDetectionResult(options.DefaultLanguage, 0.1, "No detection, using default")
                    : LanguageDetectionResult.Unknown;

            // Geçerli sonuçları filtrele;
            var validResults = results.Where(r => r.IsValid && r.Confidence >= ConfidenceThreshold).ToList();

            if (!validResults.Any())
                return LanguageDetectionResult.Unknown;

            // Aynı dil için sonuçları grupla;
            var groupedResults = validResults;
                .GroupBy(r => r.Language.Code)
                .Select(g => new;
                {
                    Language = g.First().Language,
                    AverageConfidence = g.Average(r => r.Confidence),
                    MaxConfidence = g.Max(r => r.Confidence),
                    Methods = g.Select(r => r.Method).ToList()
                })
                .OrderByDescending(x => x.MaxConfidence)
                .ThenByDescending(x => x.AverageConfidence)
                .ToList();

            var bestResult = groupedResults.First();

            // Güveni artırmak için yöntem sayısını dikkate al;
            double confidenceBoost = Math.Min(0.2, bestResult.Methods.Count * 0.05);
            double finalConfidence = Math.Min(0.99, bestResult.MaxConfidence + confidenceBoost);

            string combinedMethod = string.Join(" + ", bestResult.Methods.Distinct());

            return new LanguageDetectionResult(
                bestResult.Language,
                finalConfidence,
                $"Combined: {combinedMethod}"
            );
        }

        /// <summary>
        /// N-gram frekanslarını çıkarır;
        /// </summary>
        private Dictionary<string, double> ExtractNgramFrequencies(string text, int ngramSize)
        {
            var frequencies = new Dictionary<string, int>();
            int totalNgrams = 0;

            for (int i = 0; i <= text.Length - ngramSize; i++)
            {
                string ngram = text.Substring(i, ngramSize).ToLowerInvariant();

                if (frequencies.ContainsKey(ngram))
                    frequencies[ngram]++;
                else;
                    frequencies[ngram] = 1;

                totalNgrams++;
            }

            // Frekansları normalize et;
            var normalizedFrequencies = new Dictionary<string, double>();
            foreach (var kvp in frequencies)
            {
                normalizedFrequencies[kvp.Key] = (double)kvp.Value / totalNgrams;
            }

            return normalizedFrequencies;
        }

        /// <summary>
        /// Benzerlik skorunu hesaplar;
        /// </summary>
        private double CalculateSimilarityScore(
            Dictionary<string, double> textProfile,
            Dictionary<string, double> languageProfile)
        {
            double score = 0;

            foreach (var ngram in textProfile.Keys)
            {
                if (languageProfile.TryGetValue(ngram, out double languageFreq))
                {
                    // Kosinüs benzerliği;
                    score += textProfile[ngram] * languageFreq;
                }
            }

            // Normalleştirme;
            double textNorm = Math.Sqrt(textProfile.Values.Sum(v => v * v));
            double langNorm = Math.Sqrt(languageProfile.Values.Sum(v => v * v));

            if (textNorm > 0 && langNorm > 0)
            {
                score /= (textNorm * langNorm);
            }

            return score;
        }

        /// <summary>
        /// Skoru normalize eder;
        /// </summary>
        private double NormalizeScore(double score, IEnumerable<double> allScores)
        {
            if (score <= 0)
                return 0;

            var scoresList = allScores.ToList();
            double maxScore = scoresList.Max();
            double minScore = scoresList.Min();

            if (maxScore - minScore == 0)
                return 0.5; // Ortalama güven;

            return (score - minScore) / (maxScore - minScore);
        }

        /// <summary>
        /// Karakter dağılımını analiz eder;
        /// </summary>
        private CharacterDistribution AnalyzeCharacterDistribution(string text)
        {
            var distribution = new CharacterDistribution();

            foreach (char c in text)
            {
                distribution.TotalCharacters++;

                if (char.IsLetter(c))
                {
                    distribution.LetterCount++;
                    distribution.Characters.Add(c);

                    if (IsLatin(c)) distribution.LatinCount++;
                    if (IsCyrillic(c)) distribution.CyrillicCount++;
                    if (IsArabic(c)) distribution.ArabicCount++;
                    if (IsCJK(c)) distribution.CJKCount++;
                }
                else if (char.IsDigit(c))
                {
                    distribution.DigitCount++;
                }
                else if (char.IsPunctuation(c))
                {
                    distribution.PunctuationCount++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    distribution.WhitespaceCount++;
                }
            }

            return distribution;
        }

        /// <summary>
        /// Kısa metin kurallarını uygular;
        /// </summary>
        private LanguageDetectionResult ApplyShortTextRules(string text, CharacterDistribution distribution)
        {
            // URL, e-posta gibi özel formatlar;
            if (Uri.TryCreate(text, UriKind.Absolute, out _))
                return new LanguageDetectionResult(LanguageInfo.English, 0.8, "URL format");

            if (Regex.IsMatch(text, @"^[^@]+@[^@]+\.[^@]+$"))
                return new LanguageDetectionResult(LanguageInfo.English, 0.7, "Email format");

            // Dil spesifik karakter kontrolü;
            if (distribution.CJKCount > 0 && distribution.CJKCount == distribution.LetterCount)
            {
                return new LanguageDetectionResult(LanguageInfo.ChineseSimplified, 0.6, "CJK characters only");
            }

            if (distribution.ArabicCount > 0 && distribution.ArabicCount == distribution.LetterCount)
            {
                return new LanguageDetectionResult(LanguageInfo.Arabic, 0.6, "Arabic characters only");
            }

            return LanguageDetectionResult.Unknown;
        }

        /// <summary>
        /// Unicode bloklarını analiz eder;
        /// </summary>
        private UnicodeBlockAnalysis AnalyzeUnicodeBlocks(string text)
        {
            var analysis = new UnicodeBlockAnalysis();

            foreach (char c in text)
            {
                var block = GetUnicodeBlock(c);
                analysis.AddCharacter(c, block);
            }

            return analysis;
        }

        /// <summary>
        /// Dil sınırlarını tespit eder;
        /// </summary>
        private List<LanguageSegment> DetectLanguageBoundaries(
            string text,
            List<LanguageScore> topLanguages,
            MultiLanguageDetectionOptions options)
        {
            var segments = new List<LanguageSegment>();

            if (text.Length < 50 || topLanguages.Count < 2)
                return segments;

            // Sliding window ile dil analizi;
            int windowSize = Math.Min(100, text.Length / 10);
            int stepSize = windowSize / 2;

            for (int i = 0; i <= text.Length - windowSize; i += stepSize)
            {
                string windowText = text.Substring(i, Math.Min(windowSize, text.Length - i));
                var windowResult = DetectLanguage(windowText, new LanguageDetectionOptions;
                {
                    UseNgramAnalysis = true,
                    UseCharacterSetAnalysis = true;
                });

                if (windowResult.IsValid && windowResult.Confidence > 0.3)
                {
                    segments.Add(new LanguageSegment;
                    {
                        StartIndex = i,
                        EndIndex = i + windowSize,
                        Language = windowResult.Language,
                        Confidence = windowResult.Confidence;
                    });
                }
            }

            // Benzer dil segmentlerini birleştir;
            return MergeLanguageSegments(segments, options.MergeThreshold);
        }

        /// <summary>
        /// Dil segmentlerini birleştirir;
        /// </summary>
        private List<LanguageSegment> MergeLanguageSegments(List<LanguageSegment> segments, double mergeThreshold)
        {
            if (segments.Count < 2)
                return segments;

            var merged = new List<LanguageSegment>();
            var current = segments[0];

            for (int i = 1; i < segments.Count; i++)
            {
                var next = segments[i];

                // Aynı dil ve üst üste biniyorsa/birleşiyorsa;
                if (current.Language.Code == next.Language.Code &&
                    next.StartIndex <= current.EndIndex + 10) // 10 karakter tolerans;
                {
                    // Birleştir;
                    current.EndIndex = Math.Max(current.EndIndex, next.EndIndex);
                    current.Confidence = (current.Confidence + next.Confidence) / 2;
                }
                else;
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged;
        }

        /// <summary>
        /// Dil skorlarını hesaplar;
        /// </summary>
        private List<LanguageScore> CalculateLanguageScores(
            Dictionary<string, double> ngramFrequencies,
            MultiLanguageDetectionOptions options)
        {
            var scores = new List<LanguageScore>();

            foreach (var profile in _languageProfiles.Values)
            {
                double score = CalculateSimilarityScore(ngramFrequencies, profile.NgramProfile);

                scores.Add(new LanguageScore;
                {
                    Language = profile.LanguageInfo,
                    Score = score,
                    Confidence = NormalizeScore(score, scores.Select(s => s.Score))
                });
            }

            return scores.OrderByDescending(s => s.Score).ToList();
        }

        /// <summary>
        /// En yüksek skorlu dilleri alır;
        /// </summary>
        private List<LanguageScore> GetTopLanguages(List<LanguageScore> allScores, int maxCount)
        {
            return allScores;
                .Where(s => s.Confidence > MinimumConfidence)
                .Take(maxCount)
                .ToList();
        }

        /// <summary>
        /// Tespit sonucunu doğrular;
        /// </summary>
        private LanguageDetectionResult ValidateDetectionResult(
            LanguageDetectionResult result,
            LanguageDetectionOptions options)
        {
            if (!result.IsValid)
                return result;

            // Minimum güven kontrolü;
            if (result.Confidence < MinimumConfidence)
            {
                _logger.Debug($"Düşük güven skoru: {result.Confidence:P2}, eşik: {MinimumConfidence:P2}");

                return options.DefaultLanguage != null;
                    ? new LanguageDetectionResult(options.DefaultLanguage, 0.1, "Low confidence, using default")
                    : LanguageDetectionResult.Unknown;
            }

            // Dil destek kontrolü;
            if (options.AllowedLanguages != null && options.AllowedLanguages.Any())
            {
                if (!options.AllowedLanguages.Contains(result.Language.Code))
                {
                    _logger.Debug($"İzin verilmeyen dil: {result.Language.Code}");

                    // İzin verilen dillerden en benzerini bul;
                    var allowedResult = FindBestAllowedLanguage(result, options.AllowedLanguages);
                    return allowedResult ?? LanguageDetectionResult.Unknown;
                }
            }

            return result;
        }

        /// <summary>
        /// İzin verilen dillerden en iyisini bulur;
        /// </summary>
        private LanguageDetectionResult FindBestAllowedLanguage(
            LanguageDetectionResult originalResult,
            IEnumerable<string> allowedLanguages)
        {
            var allowedCodes = new HashSet<string>(allowedLanguages.Select(c => c.ToLowerInvariant()));

            foreach (var code in allowedCodes)
            {
                if (_languageProfiles.TryGetValue(code, out var profile))
                {
                    // Dil aileleri veya yakın diller kontrol edilebilir;
                    // Şimdilik sadece mevcut dil;
                    return new LanguageDetectionResult(
                        profile.LanguageInfo,
                        originalResult.Confidence * 0.7, // Güveni düşür;
                        $"Mapped from {originalResult.Language.Code} to allowed language"
                    );
                }
            }

            return null;
        }

        #endregion;

        #region Helper Methods;

        private bool IsLatin(char c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z';
        private bool IsCyrillic(char c) => c >= 'А' && c <= 'я' || c >= 'Ѐ' && c <= 'џ';
        private bool IsArabic(char c) => c >= 'ء' && c <= 'ي';
        private bool IsCJK(char c) => c >= '\u4E00' && c <= '\u9FFF';

        private UnicodeBlock GetUnicodeBlock(char c)
        {
            // Basit Unicode blok tespiti;
            int codePoint = (int)c;

            if (codePoint >= 0x0000 && codePoint <= 0x007F) return UnicodeBlock.BasicLatin;
            if (codePoint >= 0x0080 && codePoint <= 0x00FF) return UnicodeBlock.Latin1Supplement;
            if (codePoint >= 0x0400 && codePoint <= 0x04FF) return UnicodeBlock.Cyrillic;
            if (codePoint >= 0x0600 && codePoint <= 0x06FF) return UnicodeBlock.Arabic;
            if (codePoint >= 0x4E00 && codePoint <= 0x9FFF) return UnicodeBlock.CJKUnifiedIdeographs;

            return UnicodeBlock.Unknown;
        }

        private Dictionary<string, double> BuildNgramProfile(IEnumerable<string> texts, int ngramSize)
        {
            var combinedFrequencies = new Dictionary<string, int>();
            int totalNgrams = 0;

            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var frequencies = ExtractNgramFrequencies(text, ngramSize);

                foreach (var kvp in frequencies)
                {
                    int count = (int)(kvp.Value * 1000); // Ölçeklendir;

                    if (combinedFrequencies.ContainsKey(kvp.Key))
                        combinedFrequencies[kvp.Key] += count;
                    else;
                        combinedFrequencies[kvp.Key] = count;

                    totalNgrams += count;
                }
            }

            // Normalize et;
            var normalized = new Dictionary<string, double>();
            foreach (var kvp in combinedFrequencies)
            {
                normalized[kvp.Key] = (double)kvp.Value / totalNgrams;
            }

            return normalized;
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Yönetilen kaynakları serbest bırak;
                    _performanceMonitor.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~LanguageDetector()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Dil tespit arayüzü;
    /// </summary>
    public interface ILanguageDetector;
    {
        LanguageDetectionResult DetectLanguage(string text);
        LanguageDetectionResult DetectLanguage(string text, LanguageDetectionOptions options);
        MultiLanguageDetectionResult DetectMultipleLanguages(string text);
        MultiLanguageDetectionResult DetectMultipleLanguages(string text, MultiLanguageDetectionOptions options);
        bool IsLanguageSupported(string languageCode);
    }

    /// <summary>
    /// Dil tespit seçenekleri;
    /// </summary>
    public class LanguageDetectionOptions;
    {
        public bool UseNgramAnalysis { get; set; } = true;
        public bool UseCharacterSetAnalysis { get; set; } = true;
        public bool UseWordDistribution { get; set; } = true;
        public bool NormalizeText { get; set; } = true;
        public bool TruncateLongText { get; set; } = true;
        public int NgramSize { get; set; } = 3;
        public LanguageInfo DefaultLanguage { get; set; }
        public IEnumerable<string> AllowedLanguages { get; set; }
        public TextNormalizationOptions NormalizationOptions { get; set; } = new TextNormalizationOptions();
    }

    /// <summary>
    /// Çoklu dil tespit seçenekleri;
    /// </summary>
    public class MultiLanguageDetectionOptions : LanguageDetectionOptions;
    {
        public int MaxLanguages { get; set; } = 3;
        public bool DetectBoundaries { get; set; } = true;
        public double MergeThreshold { get; set; } = 0.3;
        public DetectionMethod DetectionMethod { get; set; } = DetectionMethod.Hybrid;
    }

    /// <summary>
    /// Dil tespit sonucu;
    /// </summary>
    public class LanguageDetectionResult;
    {
        public LanguageInfo Language { get; }
        public double Confidence { get; }
        public string Method { get; }
        public bool IsValid => Language != null && Confidence > 0;
        public DateTime Timestamp { get; }

        public static LanguageDetectionResult Unknown =>
            new LanguageDetectionResult(null, 0, "Unknown");

        public static LanguageDetectionResult Empty =>
            new LanguageDetectionResult(null, 0, "Empty text");

        public LanguageDetectionResult(LanguageInfo language, double confidence, string method)
        {
            Language = language;
            Confidence = Math.Max(0, Math.Min(1, confidence));
            Method = method ?? "Unknown";
            Timestamp = DateTime.UtcNow;
        }

        public static LanguageDetectionResult CreateErrorResult(Exception exception)
        {
            return new LanguageDetectionResult(null, 0, $"Error: {exception.Message}");
        }

        public override string ToString()
        {
            return Language != null;
                ? $"{Language.Name} ({Language.Code}): {Confidence:P2} via {Method}"
                : $"Unknown language";
        }
    }

    /// <summary>
    /// Çoklu dil tespit sonucu;
    /// </summary>
    public class MultiLanguageDetectionResult;
    {
        public List<LanguageScore> Languages { get; set; } = new List<LanguageScore>();
        public List<LanguageSegment> LanguageSegments { get; set; } = new List<LanguageSegment>();
        public bool IsMixedText { get; set; }
        public DetectionMethod DetectionMethod { get; set; }
        public DateTime Timestamp { get; set; }

        public static MultiLanguageDetectionResult Empty => new MultiLanguageDetectionResult();

        public static MultiLanguageDetectionResult CreateErrorResult(Exception exception)
        {
            return new MultiLanguageDetectionResult;
            {
                Languages = new List<LanguageScore>(),
                DetectionMethod = DetectionMethod.Error;
            };
        }
    }

    /// <summary>
    /// Dil skoru;
    /// </summary>
    public class LanguageScore;
    {
        public LanguageInfo Language { get; set; }
        public double Score { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Dil segmenti;
    /// </summary>
    public class LanguageSegment;
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public LanguageInfo Language { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Dil profili;
    /// </summary>
    internal class LanguageProfile;
    {
        public LanguageInfo LanguageInfo { get; }
        public Dictionary<string, double> NgramProfile { get; private set; }

        public LanguageProfile(LanguageInfo languageInfo, Dictionary<string, double> ngramProfile)
        {
            LanguageInfo = languageInfo ?? throw new ArgumentNullException(nameof(languageInfo));
            NgramProfile = ngramProfile ?? throw new ArgumentNullException(nameof(ngramProfile));
        }

        public void UpdateProfile(Dictionary<string, double> newProfile)
        {
            // Mevcut profili yeni profil ile güncelle (ağırlıklı ortalama)
            foreach (var kvp in newProfile)
            {
                if (NgramProfile.ContainsKey(kvp.Key))
                {
                    NgramProfile[kvp.Key] = (NgramProfile[kvp.Key] + kvp.Value) / 2;
                }
                else;
                {
                    NgramProfile[kvp.Key] = kvp.Value;
                }
            }
        }
    }

    /// <summary>
    /// Dil bilgisi;
    /// </summary>
    public class LanguageInfo;
    {
        public string Code { get; } // ISO 639-1;
        public string Name { get; }
        public string NativeName { get; }
        public HashSet<char> CommonCharacters { get; }
        public HashSet<string> CommonWords { get; }
        public ScriptType Script { get; }

        // Yaygın diller için statik örnekler;
        public static LanguageInfo English => new LanguageInfo("en", "English", "English",
            ScriptType.Latin, GetCommonEnglishCharacters(), GetCommonEnglishWords());

        public static LanguageInfo Turkish => new LanguageInfo("tr", "Turkish", "Türkçe",
            ScriptType.Latin, GetCommonTurkishCharacters(), GetCommonTurkishWords());

        public static LanguageInfo ChineseSimplified => new LanguageInfo("zh", "Chinese (Simplified)", "简体中文",
            ScriptType.CJK, GetCommonChineseCharacters(), GetCommonChineseWords());

        public static LanguageInfo Arabic => new LanguageInfo("ar", "Arabic", "العربية",
            ScriptType.Arabic, GetCommonArabicCharacters(), GetCommonArabicWords());

        public LanguageInfo(string code, string name, string nativeName, ScriptType script,
                           HashSet<char> commonChars, HashSet<string> commonWords)
        {
            Code = code;
            Name = name;
            NativeName = nativeName;
            Script = script;
            CommonCharacters = commonChars;
            CommonWords = commonWords;
        }

        private static HashSet<char> GetCommonEnglishCharacters()
        {
            return new HashSet<char>("abcdefghijklmnopqrstuvwxyz");
        }

        private static HashSet<string> GetCommonEnglishWords()
        {
            return new HashSet<string> { "the", "and", "you", "that", "was", "for", "are", "with", "his", "they" };
        }

        private static HashSet<char> GetCommonTurkishCharacters()
        {
            return new HashSet<char>("abcçdefgğhıijklmnoöprsştuüvyz");
        }

        private static HashSet<string> GetCommonTurkishWords()
        {
            return new HashSet<string> { "ve", "bir", "bu", "ben", "sen", "o", "ile", "için", "ama", "değil" };
        }

        private static HashSet<char> GetCommonChineseCharacters()
        {
            return new HashSet<char>("的一是不了人在有我他这为之来以个中上到大");
        }

        private static HashSet<string> GetCommonChineseWords()
        {
            return new HashSet<string> { "的", "了", "在", "是", "我", "不", "他", "有", "这", "个" };
        }

        private static HashSet<char> GetCommonArabicCharacters()
        {
            return new HashSet<char>("ابتثجحخدذرزسشصضطظعغفقكلمنهوي");
        }

        private static HashSet<string> GetCommonArabicWords()
        {
            return new HashSet<string> { "ال", "في", "من", "على", "أن", "هو", "هي", "مع", "كان", "إلى" };
        }
    }

    /// <summary>
    /// Karakter dağılım analizi;
    /// </summary>
    internal class CharacterDistribution;
    {
        public int TotalCharacters { get; set; }
        public int LetterCount { get; set; }
        public int DigitCount { get; set; }
        public int PunctuationCount { get; set; }
        public int WhitespaceCount { get; set; }
        public int LatinCount { get; set; }
        public int CyrillicCount { get; set; }
        public int ArabicCount { get; set; }
        public int CJKCount { get; set; }
        public HashSet<char> Characters { get; } = new HashSet<char>();
    }

    /// <summary>
    /// Unicode blok analizi;
    /// </summary>
    internal class UnicodeBlockAnalysis;
    {
        public Dictionary<UnicodeBlock, int> BlockCounts { get; } = new Dictionary<UnicodeBlock, int>();
        public HashSet<char> UniqueCharacters { get; } = new HashSet<char>();

        public void AddCharacter(char c, UnicodeBlock block)
        {
            UniqueCharacters.Add(c);

            if (BlockCounts.ContainsKey(block))
                BlockCounts[block]++;
            else;
                BlockCounts[block] = 1;
        }
    }

    /// <summary>
    /// Dil tespit kuralları;
    /// </summary>
    internal static class LanguageDetectionRules;
    {
        private static readonly List<DetectionRule> _rules = new List<DetectionRule>();

        static LanguageDetectionRules()
        {
            InitializeRules();
        }

        public static IEnumerable<DetectionRule> GetAllRules() => _rules.AsReadOnly();

        private static void InitializeRules()
        {
            // Türkçe için özel kurallar;
            _rules.Add(new DetectionRule("TurkishChars", LanguageInfo.Turkish, 0.8,
                text => text.Contains("ç") || text.Contains("ğ") || text.Contains("ı") ||
                       text.Contains("ö") || text.Contains("ş") || text.Contains("ü")));

            // Almanca için özel kurallar;
            _rules.Add(new DetectionRule("GermanChars",
                new LanguageInfo("de", "German", "Deutsch", ScriptType.Latin,
                    new HashSet<char>("abcdefghijklmnopqrstuvwxyzäöüß"),
                    new HashSet<string> { "und", "der", "die", "das", "ein" }),
                0.7,
                text => text.Contains("ß") || text.Contains("ä") || text.Contains("ö") || text.Contains("ü")));

            // Arapça için Unicode aralığı kuralı;
            _rules.Add(new DetectionRule("ArabicRange", LanguageInfo.Arabic, 0.9,
                text => text.Any(c => c >= 'ء' && c <= 'ي')));

            // Japonca için Hiragana/Katakana kontrolü;
            _rules.Add(new DetectionRule("JapaneseKana",
                new LanguageInfo("ja", "Japanese", "日本語", ScriptType.CJK,
                    new HashSet<char>(), new HashSet<string>()),
                0.85,
                text => text.Any(c => (c >= '\u3040' && c <= '\u309F') || // Hiragana;
                                     (c >= '\u30A0' && c <= '\u30FF')))); // Katakana;
        }
    }

    /// <summary>
    /// Tespit kuralı;
    /// </summary>
    internal class DetectionRule;
    {
        public string RuleName { get; }
        public LanguageInfo Language { get; }
        public double Confidence { get; }
        public Func<string, bool> Matcher { get; }

        public DetectionRule(string ruleName, LanguageInfo language, double confidence, Func<string, bool> matcher)
        {
            RuleName = ruleName;
            Language = language;
            Confidence = confidence;
            Matcher = matcher;
        }

        public bool Matches(string text, UnicodeBlockAnalysis unicodeAnalysis = null)
        {
            return Matcher(text);
        }
    }

    /// <summary>
    /// Performans monitörü;
    /// </summary>
    internal class PerformanceMonitor : IDisposable
    {
        private readonly string _componentName;
        private readonly Dictionary<string, TimeSpan> _operationTimes = new Dictionary<string, TimeSpan>();
        private readonly Dictionary<string, int> _operationCounts = new Dictionary<string, int>();

        public PerformanceMonitor(string componentName)
        {
            _componentName = componentName;
        }

        public IDisposable StartOperation(string operationName)
        {
            return new OperationTimer(this, operationName);
        }

        private void RecordOperation(string operationName, TimeSpan duration)
        {
            lock (_operationTimes)
            {
                if (_operationTimes.ContainsKey(operationName))
                {
                    _operationTimes[operationName] += duration;
                    _operationCounts[operationName]++;
                }
                else;
                {
                    _operationTimes[operationName] = duration;
                    _operationCounts[operationName] = 1;
                }
            }
        }

        public void Dispose()
        {
            // Performans istatistiklerini logla;
            if (_operationTimes.Any())
            {
                var logger = LogManager.GetLogger(typeof(PerformanceMonitor));
                logger.Info($"Performance stats for {_componentName}:");

                foreach (var kvp in _operationTimes)
                {
                    int count = _operationCounts[kvp.Key];
                    TimeSpan avg = TimeSpan.FromTicks(kvp.Value.Ticks / count);
                    logger.Info($"  {kvp.Key}: {count} calls, total: {kvp.Value.TotalMilliseconds:F2}ms, avg: {avg.TotalMilliseconds:F2}ms");
                }
            }
        }

        private class OperationTimer : IDisposable
        {
            private readonly PerformanceMonitor _monitor;
            private readonly string _operationName;
            private readonly DateTime _startTime;
            private bool _disposed;

            public OperationTimer(PerformanceMonitor monitor, string operationName)
            {
                _monitor = monitor;
                _operationName = operationName;
                _startTime = DateTime.Now;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    var duration = DateTime.Now - _startTime;
                    _monitor.RecordOperation(_operationName, duration);
                    _disposed = true;
                }
            }
        }
    }

    #region Enums;

    /// <summary>
    /// Yazı tipi;
    /// </summary>
    public enum ScriptType;
    {
        Latin,
        Cyrillic,
        Arabic,
        CJK,
        Devanagari,
        Hebrew,
        Greek,
        Other;
    }

    /// <summary>
    /// Tespit yöntemi;
    /// </summary>
    public enum DetectionMethod;
    {
        Ngram,
        CharacterSet,
        WordDistribution,
        Hybrid,
        RuleBased,
        Error;
    }

    /// <summary>
    /// Unicode blokları;
    /// </summary>
    internal enum UnicodeBlock;
    {
        BasicLatin,
        Latin1Supplement,
        LatinExtended,
        Cyrillic,
        Arabic,
        CJKUnifiedIdeographs,
        Hiragana,
        Katakana,
        Hangul,
        Greek,
        Hebrew,
        Devanagari,
        Unknown;
    }

    #endregion;

    #endregion;
}
