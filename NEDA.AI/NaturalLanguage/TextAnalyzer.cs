using NEDA.AI.NaturalLanguage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NEDA.AI.NaturalLanguage;
{
    /// <summary>
    /// Gelişmiş Metin Analiz Motoru - Kapsamlı metin analizi ve özellik çıkarımı;
    /// </summary>
    public class TextAnalyzer : IDisposable
    {
        private readonly NLPTokenizer _tokenizer;
        private readonly TextStatistics _statistics;
        private readonly TextFeatures _features;
        private readonly PatternMatcher _patternMatcher;
        private readonly ContentClassifier _classifier;

        private bool _isInitialized;
        private TextAnalyzerConfig _config;

        public string AnalyzerId { get; private set; }
        public AnalyzerStatus Status { get; private set; }
        public List<AnalysisType> SupportedAnalyses { get; private set; }

        // Cache for performance;
        private readonly AnalysisCache _cache;

        public TextAnalyzer(string analyzerId = "NEDA-Text-Analyzer", TextAnalyzerConfig config = null)
        {
            AnalyzerId = analyzerId;
            _config = config ?? new TextAnalyzerConfig();

            _tokenizer = new NLPTokenizer();
            _statistics = new TextStatistics();
            _features = new TextFeatures();
            _patternMatcher = new PatternMatcher();
            _classifier = new ContentClassifier();

            _cache = new AnalysisCache(_config.CacheSize);
            SupportedAnalyses = GetAllAnalysisTypes();
            Status = AnalyzerStatus.Created;
        }

        /// <summary>
        /// Text Analyzer'ı başlat;
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                Status = AnalyzerStatus.Initializing;
                Console.WriteLine($"?? Initializing Text Analyzer: {AnalyzerId}");

                // Bileşenleri başlat;
                await _tokenizer.InitializeAsync();
                await _patternMatcher.LoadPatternsAsync();
                await _classifier.LoadModelsAsync();

                _isInitialized = true;
                Status = AnalyzerStatus.Ready;

                Console.WriteLine($"? Text Analyzer initialized: {AnalyzerId}");
                return true;
            }
            catch (Exception ex)
            {
                Status = AnalyzerStatus.Error;
                Console.WriteLine($"? Text Analyzer initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Kapsamlı metin analizi - Tüm analiz türlerini çalıştır;
        /// </summary>
        public async Task<ComprehensiveAnalysis> AnalyzeComprehensiveAsync(string text, AnalysisOptions options = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Text Analyzer başlatılmamış.");

            var startTime = DateTime.Now;
            options ??= new AnalysisOptions();

            try
            {
                // Cache kontrolü;
                var cacheKey = $"comprehensive_{text.GetHashCode()}";
                if (_config.EnableCaching && _cache.TryGet(cacheKey, out ComprehensiveAnalysis cachedResult))
                {
                    return cachedResult;
                }

                Console.WriteLine($"?? Comprehensive analysis: {text.Truncate(60)}...");

                // Paralel analizler;
                var analysisTasks = new Dictionary<string, Task<object>>
                {
                    ["statistics"] = Task.Run(() => AnalyzeStatisticsAsync(text, options)),
                    ["features"] = Task.Run(() => ExtractFeaturesAsync(text, options)),
                    ["sentiment"] = Task.Run(() => AnalyzeSentimentAsync(text, options)),
                    ["entities"] = Task.Run(() => ExtractEntitiesAsync(text, options)),
                    ["topics"] = Task.Run(() => ExtractTopicsAsync(text, options)),
                    ["readability"] = Task.Run(() => AnalyzeReadabilityAsync(text, options)),
                    ["patterns"] = Task.Run(() => MatchPatternsAsync(text, options)),
                    ["classification"] = Task.Run(() => ClassifyContentAsync(text, options))
                };

                // Tüm analizleri bekleyerek sonuçları topla;
                await Task.WhenAll(analysisTasks.Values);

                var analysis = new ComprehensiveAnalysis;
                {
                    Text = text,
                    Statistics = (TextStatisticsResult)await analysisTasks["statistics"],
                    Features = (TextFeaturesResult)await analysisTasks["features"],
                    Sentiment = (SentimentAnalysis)await analysisTasks["sentiment"],
                    Entities = (List<TextEntity>)await analysisTasks["entities"],
                    Topics = (List<Topic>)await analysisTasks["topics"],
                    Readability = (ReadabilityScore)await analysisTasks["readability"],
                    Patterns = (List<PatternMatch>)await analysisTasks["patterns"],
                    Classification = (ContentClassification)await analysisTasks["classification"],
                    ProcessingTime = DateTime.Now - startTime,
                    Timestamp = DateTime.Now;
                };

                // Özet skor hesapla;
                analysis.OverallScore = CalculateOverallScore(analysis);

                // Cache'e kaydet;
                if (_config.EnableCaching)
                {
                    _cache.Set(cacheKey, analysis, TimeSpan.FromMinutes(_config.CacheDurationMinutes));
                }

                LogAnalysis(analysis);
                return analysis;
            }
            catch (Exception ex)
            {
                Status = AnalyzerStatus.Error;
                throw new TextAnalysisException($"Comprehensive analysis failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Metin istatistikleri analizi;
        /// </summary>
        public async Task<TextStatisticsResult> AnalyzeStatisticsAsync(string text, AnalysisOptions options = null)
        {
            return await Task.Run(() =>
            {
                var stats = new TextStatisticsResult;
                {
                    CharacterCount = text.Length,
                    CharacterCountNoSpaces = text.Replace(" ", "").Length,
                    WordCount = CountWords(text),
                    SentenceCount = CountSentences(text),
                    ParagraphCount = CountParagraphs(text),
                    LineCount = CountLines(text),
                    AverageWordLength = CalculateAverageWordLength(text),
                    AverageSentenceLength = CalculateAverageSentenceLength(text),
                    UniqueWordCount = CountUniqueWords(text),
                    WordFrequency = CalculateWordFrequency(text),
                    CharacterFrequency = CalculateCharacterFrequency(text)
                };

                // Dil bazlı istatistikler;
                stats.LanguageComplexity = CalculateLanguageComplexity(stats);
                stats.TextDensity = CalculateTextDensity(stats);

                return stats;
            });
        }

        /// <summary>
        /// Metin özellikleri çıkarımı;
        /// </summary>
        public async Task<TextFeaturesResult> ExtractFeaturesAsync(string text, AnalysisOptions options = null)
        {
            return await Task.Run(() =>
            {
                var features = new TextFeaturesResult();

                // Yapısal özellikler;
                features.HasNumbers = text.Any(char.IsDigit);
                features.HasPunctuation = text.Any(char.IsPunctuation);
                features.HasUpperCase = text.Any(char.IsUpper);
                features.HasLowerCase = text.Any(char.IsLower);
                features.HasMixedCase = features.HasUpperCase && features.HasLowerCase;
                features.HasSpecialChars = text.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));

                // İçerik özellikleri;
                features.ContainsQuestions = text.Contains('?');
                features.ContainsExclamations = text.Contains('!');
                features.ContainsQuotes = text.Contains('"') || text.Contains(''');
                features.ContainsParentheses = text.Contains('(') || text.Contains(')');
                features.ContainsURLs = ContainsUrls(text);
                features.ContainsEmails = ContainsEmails(text);
                features.ContainsPhoneNumbers = ContainsPhoneNumbers(text);

                // Dil özellikleri;
                features.LanguagePatterns = DetectLanguagePatterns(text);
                features.WritingStyle = DetectWritingStyle(text);
                features.FormalityLevel = DetectFormalityLevel(text);

                // Özel pattern'ler;
                features.TechnicalTerms = ExtractTechnicalTerms(text);
                features.DomainSpecificTerms = ExtractDomainSpecificTerms(text, options?.Domain);

                return features;
            });
        }

        /// <summary>
        /// Duygu analizi;
        /// </summary>
        public async Task<SentimentAnalysis> AnalyzeSentimentAsync(string text, AnalysisOptions options = null)
        {
            return await Task.Run(() =>
            {
                var sentiment = new SentimentAnalysis();

                // Basit sözlük tabanlı duygu analizi;
                var positiveWords = new[] { "iyi", "güzel", "harika", "mükemmel", "sev", "mutlu", "başarı" };
                var negativeWords = new[] { "kötü", "berbat", "korkunç", "nefret", "üzgün", "başarısız", "sorun" };

                var words = text.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var positiveCount = words.Count(w => positiveWords.Any(p => w.Contains(p)));
                var negativeCount = words.Count(w => negativeWords.Any(n => w.Contains(n)));
                var totalRelevant = positiveCount + negativeCount;

                if (totalRelevant > 0)
                {
                    sentiment.Score = (positiveCount - negativeCount) / (double)totalRelevant;
                }

                // Duygu sınıflandırması;
                sentiment.Polarity = sentiment.Score switch;
                {
                    > 0.3 => SentimentPolarity.VeryPositive,
                    > 0.1 => SentimentPolarity.Positive,
                    < -0.3 => SentimentPolarity.VeryNegative,
                    < -0.1 => SentimentPolarity.Negative,
                    _ => SentimentPolarity.Neutral;
                };

                // Duygu yoğunluğu;
                sentiment.Intensity = Math.Abs(sentiment.Score);
                sentiment.Confidence = CalculateSentimentConfidence(positiveCount, negativeCount, words.Length);

                return sentiment;
            });
        }

        /// <summary>
        /// Varlık çıkarımı (Entity Recognition)
        /// </summary>
        public async Task<List<TextEntity>> ExtractEntitiesAsync(string text, AnalysisOptions options = null)
        {
            return await Task.Run(() =>
            {
                var entities = new List<TextEntity>();

                // İsim varlıkları (basit regex tabanlı)
                var namePattern = @"\b[A-Z][a-z]+ [A-Z][a-z]+\b";
                var nameMatches = Regex.Matches(text, namePattern);
                entities.AddRange(nameMatches.Select(m => new TextEntity;
                {
                    Text = m.Value,
                    Type = EntityType.Person,
                    Confidence = 0.7,
                    Position = m.Index;
                }));

                // Yer varlıkları;
                var locationPattern = @"\b[A-Z][a-z]+(?: [A-Z][a-z]+)*\b";
                var locationMatches = Regex.Matches(text, locationPattern);
                entities.AddRange(locationMatches.Select(m => new TextEntity;
                {
                    Text = m.Value,
                    Type = EntityType.Location,
                    Confidence = 0.6,
                    Position = m.Index;
                }));

                // Organizasyon varlıkları;
                var orgWords = new[] { "şirket", "firma", "kurum", "üniversite", "hastane" };
                var orgMatches = Regex.Matches(text, @"\b(" + string.Join("|", orgWords) + @")\b", RegexOptions.IgnoreCase);
                entities.AddRange(orgMatches.Select(m => new TextEntity;
                {
                    Text = m.Value,
                    Type = EntityType.Organization,
                    Confidence = 0.8,
                    Position = m.Index;
                }));

                // Tarih varlıkları;
                var datePattern = @"\b\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b";
                var dateMatches = Regex.Matches(text, datePattern);
                entities.AddRange(dateMatches.Select(m => new TextEntity;
                {
                    Text = m.Value,
                    Type = EntityType.Date,
                    Confidence = 0.9,
                    Position = m.Index;
                }));

                return entities.DistinctBy(e => e.Text).ToList();
            });
        }

        /// <summary>
        /// Konu çıkarımı;
        /// </summary>
        public async Task<List<Topic>> ExtractTopicsAsync(string text, AnalysisOptions options = null)
        {
            return await Task.Run(() =>
            {
                var topics = new List<Topic>();
                var words = text.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // Önceden tanımlanmış konu kategorileri;
                var topicCategories = new Dictionary<string, string[]>
                {
                    ["teknoloji"] = new[] { "bilgisayar", "yazılım", "program", "kod", "internet", "teknoloji" },
                    ["oyun"] = new[] { "oyun", "grafik", "karakter", "seviye", "kontrol", "unity", "unreal" },
                    ["iş"] = new[] { "proje", "yönetim", "ekip", "toplantı", "rapor", "bütçe" },
                    ["eğitim"] = new[] { "öğrenme", "eğitim", "kurs", "ders", "sınav", "öğretmen" }
                };

                foreach (var category in topicCategories)
                {
                    var matches = words.Count(w => category.Value.Any(keyword => w.Contains(keyword)));
                    if (matches > 0)
                    {
                        topics.Add(new Topic;
                        {
                            Name = category.Key,
                            Score = (double)matches / words.Length,
                            Keywords = category.Value.Where(kw => words.Any(w => w.Contains(kw))).ToList()
                        });
                    }
                }

                return topics.OrderByDescending(t => t.Score).ToList();
            });
        }

        /// <summary>
        /// Okunabilirlik analizi;
        /// </summary>
        public async Task<ReadabilityScore> AnalyzeReadabilityAsync(string text, AnalysisOptions options = null)
        {
            return await Task.Run(() =>
            {
                var stats = AnalyzeStatisticsAsync(text).Result;

                // Flesch Reading Ease (Türkçe uyarlaması)
                var wordsPerSentence = stats.AverageSentenceLength;
                var syllablesPerWord = CalculateAverageSyllables(text);

                var fleschScore = 206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord);

                // Okunabilirlik seviyesi;
                var level = fleschScore switch;
                {
                    > 90 => ReadabilityLevel.VeryEasy,
                    > 80 => ReadabilityLevel.Easy,
                    > 70 => ReadabilityLevel.FairlyEasy,
                    > 60 => ReadabilityLevel.Standard,
                    > 50 => ReadabilityLevel.FairlyDifficult,
                    > 30 => ReadabilityLevel.Difficult,
                    _ => ReadabilityLevel.VeryDifficult;
                };

                return new ReadabilityScore;
                {
                    FleschReadingEase = fleschScore,
                    Level = level,
                    WordsPerSentence = wordsPerSentence,
                    SyllablesPerWord = syllablesPerWord,
                    ComplexWordPercentage = CalculateComplexWordPercentage(text)
                };
            });
        }

        /// <summary>
        /// Pattern eşleştirme;
        /// </summary>
        public async Task<List<PatternMatch>> MatchPatternsAsync(string text, AnalysisOptions options = null)
        {
            return await _patternMatcher.MatchAsync(text, options?.CustomPatterns);
        }

        /// <summary>
        /// İçerik sınıflandırma;
        /// </summary>
        public async Task<ContentClassification> ClassifyContentAsync(string text, AnalysisOptions options = null)
        {
            return await _classifier.ClassifyAsync(text, options?.Domain);
        }

        /// <summary>
        /// Özet skor hesapla;
        /// </summary>
        private double CalculateOverallScore(ComprehensiveAnalysis analysis)
        {
            var scores = new List<double>();

            // İstatistik skoru;
            if (analysis.Statistics != null)
            {
                var statScore = Math.Min(analysis.Statistics.WordCount / 100.0, 1.0);
                scores.Add(statScore);
            }

            // Duygu skoru;
            if (analysis.Sentiment != null)
            {
                var sentimentScore = (analysis.Sentiment.Score + 1) / 2; // -1..1 to 0..1;
                scores.Add(sentimentScore);
            }

            // Varlık skoru;
            if (analysis.Entities != null)
            {
                var entityScore = Math.Min(analysis.Entities.Count / 10.0, 1.0);
                scores.Add(entityScore);
            }

            // Okunabilirlik skoru;
            if (analysis.Readability != null)
            {
                var readabilityScore = analysis.Readability.FleschReadingEase / 100.0;
                scores.Add(readabilityScore);
            }

            return scores.Any() ? scores.Average() : 0.5;
        }

        // Yardımcı metodlar;
        private int CountWords(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        private int CountSentences(string text) => text.Split('.', '!', '?').Length;

        private int CountParagraphs(string text) => text.Split('\n').Count(p => !string.IsNullOrWhiteSpace(p));

        private int CountLines(string text) => text.Split('\n').Length;

        private double CalculateAverageWordLength(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Any() ? words.Average(w => w.Length) : 0;
        }

        private double CalculateAverageSentenceLength(string text)
        {
            var sentences = text.Split('.', '!', '?');
            var validSentences = sentences.Where(s => !string.IsNullOrWhiteSpace(s));
            return validSentences.Any() ? validSentences.Average(s => s.Split(' ').Length) : 0;
        }

        private int CountUniqueWords(string text)
        {
            var words = text.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Distinct().Count();
        }

        private Dictionary<string, int> CalculateWordFrequency(string text)
        {
            var words = text.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        }

        private Dictionary<char, int> CalculateCharacterFrequency(string text)
        {
            return text.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        }

        private double CalculateLanguageComplexity(TextStatisticsResult stats)
        {
            return Math.Min((stats.UniqueWordCount / (double)stats.WordCount) * 10, 1.0);
        }

        private double CalculateTextDensity(TextStatisticsResult stats)
        {
            return stats.WordCount > 0 ? (double)stats.CharacterCountNoSpaces / stats.WordCount : 0;
        }

        private bool ContainsUrls(string text) => Regex.IsMatch(text, @"https?://[^\s]+");

        private bool ContainsEmails(string text) => Regex.IsMatch(text, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b");

        private bool ContainsPhoneNumbers(string text) => Regex.IsMatch(text, @"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b");

        private List<string> DetectLanguagePatterns(string text)
        {
            var patterns = new List<string>();

            if (text.Contains("nasıl") || text.Contains("neden")) patterns.Add("soru");
            if (text.Contains("çünkü") || text.Contains("bu yüzden")) patterns.Add("açıklama");
            if (text.Contains("örneğin") || text.Contains("mesela")) patterns.Add("örnekleme");
            if (text.Contains("ancak") || text.Contains("fakat")) patterns.Add("karşıtlık");

            return patterns;
        }

        private WritingStyle DetectWritingStyle(string text)
        {
            if (text.Contains("!") || text.Contains("?")) return WritingStyle.Conversational;
            if (text.Length > 200) return WritingStyle.Formal;
            return WritingStyle.Neutral;
        }

        private FormalityLevel DetectFormalityLevel(string text)
        {
            var informalWords = new[] { "slm", "merhaba", "hey", "ya", "falan" };
            var formalWords = new[] { "saygılarımla", "bilgilerinize", "arz ederim" };

            if (formalWords.Any(w => text.Contains(w))) return FormalityLevel.VeryFormal;
            if (informalWords.Any(w => text.Contains(w))) return FormalityLevel.Informal;
            return FormalityLevel.Neutral;
        }

        private List<string> ExtractTechnicalTerms(string text)
        {
            var technicalTerms = new[] { "algorithm", "database", "framework", "interface", "protocol" };
            return technicalTerms.Where(term => text.ToLower().Contains(term)).ToList();
        }

        private List<string> ExtractDomainSpecificTerms(string text, string domain)
        {
            // Domain'e özel terimler;
            var terms = new List<string>();

            if (domain == "programming")
            {
                var programmingTerms = new[] { "function", "class", "object", "variable", "loop" };
                terms.AddRange(programmingTerms.Where(term => text.ToLower().Contains(term)));
            }

            return terms;
        }

        private double CalculateAverageSyllables(string text)
        {
            // Basit hece sayısı tahmini (Türkçe için)
            var vowels = new[] { 'a', 'e', 'ı', 'i', 'o', 'ö', 'u', 'ü' };
            var vowelCount = text.Count(c => vowels.Contains(char.ToLower(c)));
            return text.Length > 0 ? (double)vowelCount / CountWords(text) : 0;
        }

        private double CalculateComplexWordPercentage(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var complexWords = words.Count(w => w.Length > 6); // 6+ harfli kelimeler kompleks sayılıyor;
            return words.Length > 0 ? (double)complexWords / words.Length : 0;
        }

        private double CalculateSentimentConfidence(int positive, int negative, int totalWords)
        {
            var totalRelevant = positive + negative;
            return totalWords > 0 ? (double)totalRelevant / totalWords : 0;
        }

        private List<AnalysisType> GetAllAnalysisTypes()
        {
            return Enum.GetValues(typeof(AnalysisType)).Cast<AnalysisType>().ToList();
        }

        private void LogAnalysis(ComprehensiveAnalysis analysis)
        {
            Console.WriteLine($"?? Analysis completed: {analysis.ProcessingTime.TotalMilliseconds:F2}ms | " +
                            $"Score: {analysis.OverallScore:P2} | Words: {analysis.Statistics.WordCount}");
        }

        public void Dispose()
        {
            _tokenizer?.Dispose();
            _patternMatcher?.Dispose();
            _classifier?.Dispose();
            _cache?.Clear();

            Status = AnalyzerStatus.Disposed;
            Console.WriteLine("?? Text Analyzer disposed");
        }
    }

    /// <summary>
    /// Kapsamlı Analiz Sonuçları;
    /// </summary>
    public class ComprehensiveAnalysis;
    {
        public string Text { get; set; }
        public TextStatisticsResult Statistics { get; set; }
        public TextFeaturesResult Features { get; set; }
        public SentimentAnalysis Sentiment { get; set; }
        public List<TextEntity> Entities { get; set; }
        public List<Topic> Topics { get; set; }
        public ReadabilityScore Readability { get; set; }
        public List<PatternMatch> Patterns { get; set; }
        public ContentClassification Classification { get; set; }
        public double OverallScore { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Metin İstatistikleri;
    /// </summary>
    public class TextStatisticsResult;
    {
        public int CharacterCount { get; set; }
        public int CharacterCountNoSpaces { get; set; }
        public int WordCount { get; set; }
        public int SentenceCount { get; set; }
        public int ParagraphCount { get; set; }
        public int LineCount { get; set; }
        public double AverageWordLength { get; set; }
        public double AverageSentenceLength { get; set; }
        public int UniqueWordCount { get; set; }
        public Dictionary<string, int> WordFrequency { get; set; }
        public Dictionary<char, int> CharacterFrequency { get; set; }
        public double LanguageComplexity { get; set; }
        public double TextDensity { get; set; }
    }

    /// <summary>
    /// Metin Özellikleri;
    /// </summary>
    public class TextFeaturesResult;
    {
        // Yapısal özellikler;
        public bool HasNumbers { get; set; }
        public bool HasPunctuation { get; set; }
        public bool HasUpperCase { get; set; }
        public bool HasLowerCase { get; set; }
        public bool HasMixedCase { get; set; }
        public bool HasSpecialChars { get; set; }

        // İçerik özellikler;
        public bool ContainsQuestions { get; set; }
        public bool ContainsExclamations { get; set; }
        public bool ContainsQuotes { get; set; }
        public bool ContainsParentheses { get; set; }
        public bool ContainsURLs { get; set; }
        public bool ContainsEmails { get; set; }
        public bool ContainsPhoneNumbers { get; set; }

        // Dil özellikleri;
        public List<string> LanguagePatterns { get; set; } = new List<string>();
        public WritingStyle WritingStyle { get; set; }
        public FormalityLevel FormalityLevel { get; set; }

        // Özel terimler;
        public List<string> TechnicalTerms { get; set; } = new List<string>();
        public List<string> DomainSpecificTerms { get; set; } = new List<string>();
    }

    /// <summary>
    /// Duygu Analizi;
    /// </summary>
    public class SentimentAnalysis;
    {
        public double Score { get; set; } // -1 (olumsuz) to +1 (olumlu)
        public SentimentPolarity Polarity { get; set; }
        public double Intensity { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> EmotionScores { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Metin Varlığı;
    /// </summary>
    public class TextEntity;
    {
        public string Text { get; set; }
        public EntityType Type { get; set; }
        public double Confidence { get; set; }
        public int Position { get; set; }
        public int Length => Text?.Length ?? 0;
    }

    /// <summary>
    /// Konu;
    /// </summary>
    public class Topic;
    {
        public string Name { get; set; }
        public double Score { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
    }

    /// <summary>
    /// Okunabilirlik Skoru;
    /// </summary>
    public class ReadabilityScore;
    {
        public double FleschReadingEase { get; set; }
        public ReadabilityLevel Level { get; set; }
        public double WordsPerSentence { get; set; }
        public double SyllablesPerWord { get; set; }
        public double ComplexWordPercentage { get; set; }
    }

    // Enum'lar ve diğer yardımcı sınıflar...
    public enum AnalysisType;
    {
        Statistics,
        Features,
        Sentiment,
        Entities,
        Topics,
        Readability,
        Patterns,
        Classification;
    }

    public enum AnalyzerStatus;
    {
        Created,
        Initializing,
        Ready,
        Analyzing,
        Error,
        Disposed;
    }

    public enum SentimentPolarity;
    {
        VeryNegative = -2,
        Negative = -1,
        Neutral = 0,
        Positive = 1,
        VeryPositive = 2;
    }

    public enum EntityType;
    {
        Person,
        Location,
        Organization,
        Date,
        Time,
        Money,
        Percent,
        Product,
        Event,
        Unknown;
    }

    public enum ReadabilityLevel;
    {
        VeryEasy,
        Easy,
        FairlyEasy,
        Standard,
        FairlyDifficult,
        Difficult,
        VeryDifficult;
    }

    public enum WritingStyle;
    {
        Formal,
        Informal,
        Conversational,
        Technical,
        Academic,
        Neutral;
    }

    public enum FormalityLevel;
    {
        VeryFormal,
        Formal,
        Neutral,
        Informal,
        VeryInformal;
    }
}

// Yardımcı sınıflar;
public class TextAnalyzerConfig;
{
    public bool EnableCaching { get; set; } = true;
    public int CacheSize { get; set; } = 1000;
    public int CacheDurationMinutes { get; set; } = 60;
    public int MaxTextLength { get; set; } = 10000;
    public bool EnableParallelProcessing { get; set; } = true;
}

public class AnalysisOptions;
{
    public string Domain { get; set; }
    public List<CustomPattern> CustomPatterns { get; set; } = new List<CustomPattern>();
    public bool IncludeDetailedStatistics { get; set; } = true;
    public Language Language { get; set; } = Language.Turkish;
}

// Diğer gerekli sınıfların basit implementasyonları;
public class PatternMatcher : IDisposable
{
    public Task LoadPatternsAsync() => Task.CompletedTask;
    public Task<List<PatternMatch>> MatchAsync(string text, List<CustomPattern> customPatterns = null)
        => Task.FromResult(new List<PatternMatch>());
    public void Dispose() { }
}

public class ContentClassifier : IDisposable
{
    public Task LoadModelsAsync() => Task.CompletedTask;
    public Task<ContentClassification> ClassifyAsync(string text, string domain = null)
        => Task.FromResult(new ContentClassification());
    public void Dispose() { }
}

public class PatternMatch;
{
    public string PatternName { get; set; }
    public string MatchedText { get; set; }
    public int Position { get; set; }
    public double Confidence { get; set; }
}

public class ContentClassification;
{
    public string PrimaryCategory { get; set; }
    public Dictionary<string, double> CategoryScores { get; set; } = new Dictionary<string, double>();
    public double Confidence { get; set; }
}

public class CustomPattern;
{
    public string Name { get; set; }
    public string RegexPattern { get; set; }
    public string Description { get; set; }
}

public class AnalysisCache;
{
    private readonly Dictionary<string, (object Value, DateTime Expiry)> _cache;
    private readonly int _maxSize;

    public AnalysisCache(int maxSize = 1000)
    {
        _cache = new Dictionary<string, (object, DateTime)>();
        _maxSize = maxSize;
    }

    public bool TryGet<T>(string key, out T value)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.Now)
        {
            value = (T)entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Set(string key, object value, TimeSpan duration)
    {
        if (_cache.Count >= _maxSize)
        {
            // LRU eviction;
            var oldest = _cache.OrderBy(x => x.Value.Expiry).First();
            _cache.Remove(oldest.Key);
        }

        _cache[key] = (value, DateTime.Now + duration);
    }

    public void Clear() => _cache.Clear();
}

public class TextAnalysisException : Exception
{
    public string ErrorCode { get; set; }
    public string Module { get; set; } = "NEDA.TextAnalyzer";

    public TextAnalysisException(string message) : base(message) { }
    public TextAnalysisException(string message, Exception inner) : base(message, inner) { }
    public TextAnalysisException(string message, string errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }
}
