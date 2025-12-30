using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;

namespace NEDA.AI.NaturalLanguage;
{
    /// <summary>
    /// Ana NLP Motoru - Doğal Dil İşleme ve Anlama;
    /// </summary>
    public class NLPEngine : IDisposable
    {
        private readonly NLPTokenizer _tokenizer;
        private readonly NLPParser _parser;
        private readonly NLPSentiment _sentimentAnalyzer;
        private readonly NEREngine _nerEngine;
        private readonly LanguageDetector _languageDetector;

        private bool _isInitialized;
        private NLPConfig _config;

        public string EngineId { get; private set; }
        public EngineStatus Status { get; private set; }
        public List<string> SupportedLanguages { get; private set; }

        public NLPEngine(string engineId = "NEDA_NLP_Engine", NLPConfig config = null)
        {
            EngineId = engineId;
            _config = config ?? new NLPConfig();

            _tokenizer = new NLPTokenizer();
            _parser = new NLPParser();
            _sentimentAnalyzer = new NLPSentiment();
            _nerEngine = new NEREngine();
            _languageDetector = new LanguageDetector();

            SupportedLanguages = new List<string> { "tr", "en", "de", "fr", "es" };
            Status = EngineStatus.Created;
        }

        /// <summary>
        /// NLP Engine'ı başlat;
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                Status = EngineStatus.Initializing;
                Console.WriteLine($"🚀 Initializing NLP Engine: {EngineId}");

                // Bileşenleri başlat;
                await _tokenizer.InitializeAsync();
                await _parser.InitializeAsync();
                await _sentimentAnalyzer.InitializeAsync();
                await _nerEngine.InitializeAsync();
                await _languageDetector.InitializeAsync();

                // Model yüklemeleri;
                await LoadLanguageModelsAsync();

                _isInitialized = true;
                Status = EngineStatus.Ready;

                Console.WriteLine($"✅ NLP Engine initialized: {EngineId}");
                return true;
            }
            catch (Exception ex)
            {
                Status = EngineStatus.Error;
                Console.WriteLine($"❌ NLP Engine initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Metni tam işle - Tüm NLP pipeline'ı;
        /// </summary>
        public async Task<NLPResult> ProcessTextAsync(string text, Language language = Language.Auto)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("NLP Engine başlatılmamış.");

            var startTime = DateTime.Now;
            Status = EngineStatus.Processing;

            try
            {
                Console.WriteLine($"📝 Processing text: {text.Truncate(50)}...");

                // Dil tespiti;
                var detectedLanguage = language == Language.Auto ?
                    await _languageDetector.DetectAsync(text) : language;

                // Pipeline;
                var tokens = await _tokenizer.TokenizeAsync(text, detectedLanguage);
                var sentences = await _tokenizer.SentenceTokenizeAsync(text, detectedLanguage);
                var sentiment = await _sentimentAnalyzer.AnalyzeAsync(text, detectedLanguage);
                var entities = await _nerEngine.ExtractEntitiesAsync(text, detectedLanguage);
                var syntaxTree = await _parser.ParseAsync(text, detectedLanguage);
                var intent = await ExtractIntentAsync(text, detectedLanguage);

                var result = new NLPResult;
                {
                    OriginalText = text,
                    Language = detectedLanguage,
                    Tokens = tokens,
                    Sentences = sentences,
                    Sentiment = sentiment,
                    Entities = entities,
                    SyntaxTree = syntaxTree,
                    Intent = intent,
                    ProcessingTime = DateTime.Now - startTime,
                    Success = true;
                };

                Status = EngineStatus.Ready;
                LogProcessing(text, result.ProcessingTime, true);

                return result;
            }
            catch (Exception ex)
            {
                Status = EngineStatus.Error;
                LogProcessing(text, DateTime.Now - startTime, false, ex.Message);
                throw new NLPException($"Text processing failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Toplu metin işleme;
        /// </summary>
        public async Task<List<NLPResult>> ProcessBatchAsync(List<string> texts, Language language = Language.Auto)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("NLP Engine başlatılmamış.");

            var results = new List<NLPResult>();
            Status = EngineStatus.Processing;

            try
            {
                Console.WriteLine($"📚 Processing batch: {texts.Count} texts");

                var tasks = texts.Select(text => ProcessTextAsync(text, language)).ToList();
                var batchResults = await Task.WhenAll(tasks);

                results.AddRange(batchResults);
                Status = EngineStatus.Ready;

                Console.WriteLine($"✅ Batch processing completed: {results.Count} texts");
                return results;
            }
            catch (Exception ex)
            {
                Status = EngineStatus.Error;
                throw new NLPException($"Batch processing failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Metin benzerliği hesapla;
        /// </summary>
        public async Task<double> CalculateSimilarityAsync(string text1, string text2, Language language = Language.Auto)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("NLP Engine başlatılmamış.");

            try
            {
                // Vektör tabanlı benzerlik;
                var vector1 = await TextToVectorAsync(text1, language);
                var vector2 = await TextToVectorAsync(text2, language);

                return CalculateCosineSimilarity(vector1, vector2);
            }
            catch (Exception ex)
            {
                throw new NLPException($"Similarity calculation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Metin özetleme;
        /// </summary>
        public async Task<string> SummarizeTextAsync(string text, int maxSentences = 3, Language language = Language.Auto)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("NLP Engine başlatılmamış.");

            try
            {
                var result = await ProcessTextAsync(text, language);

                // Basit özetleme algoritması;
                var importantSentences = result.Sentences;
                    .OrderByDescending(s => s.ImportanceScore)
                    .Take(maxSentences)
                    .OrderBy(s => s.Position)
                    .Select(s => s.Text);

                return string.Join(" ", importantSentences);
            }
            catch (Exception ex)
            {
                throw new NLPException($"Text summarization failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Anahtar kelime çıkarımı;
        /// </summary>
        public async Task<List<Keyword>> ExtractKeywordsAsync(string text, int maxKeywords = 10, Language language = Language.Auto)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("NLP Engine başlatılmamış.");

            try
            {
                var result = await ProcessTextAsync(text, language);

                // TF-IDF benzeri keyword extraction;
                var keywords = result.Tokens;
                    .Where(t => t.Type == TokenType.Word && t.PosTag?.StartsWith("N") == true)
                    .GroupBy(t => t.Lemma.ToLower())
                    .Select(g => new Keyword;
                    {
                        Word = g.Key,
                        Frequency = g.Count(),
                        Score = CalculateKeywordScore(g, result.Tokens.Count)
                    })
                    .OrderByDescending(k => k.Score)
                    .Take(maxKeywords)
                    .ToList();

                return keywords;
            }
            catch (Exception ex)
            {
                throw new NLPException($"Keyword extraction failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Niyet çıkarımı;
        /// </summary>
        private async Task<Intent> ExtractIntentAsync(string text, Language language)
        {
            // Basit kural tabanlı intent recognition;
            var lowerText = text.ToLower();

            var intent = new Intent;
            {
                Text = text,
                Confidence = 0.8 // Varsayılan confidence;
            };

            // Intent pattern'leri;
            var patterns = new Dictionary<string, (string Type, double Confidence)>
            {
                ["create|make|build"] = ("CREATE", 0.9),
                ["delete|remove|destroy"] = ("DELETE", 0.9),
                ["show|display|list"] = ("SHOW", 0.8),
                ["find|search|look for"] = ("SEARCH", 0.8),
                ["help|support|assist"] = ("HELP", 0.7),
                ["stop|exit|quit"] = ("STOP", 0.9),
                ["save|store|backup"] = ("SAVE", 0.8)
            };

            foreach (var pattern in patterns)
            {
                if (Regex.IsMatch(lowerText, pattern.Key))
                {
                    intent.Type = pattern.Value.Type;
                    intent.Confidence = pattern.Value.Confidence;
                    break;
                }
            }

            // Entity-based intent refinement;
            var entities = await _nerEngine.ExtractEntitiesAsync(text, language);
            if (entities.Any(e => e.Type == EntityType.Technology && e.Text.Contains("unreal")))
            {
                intent.Type = "UNREAL_COMMAND";
                intent.Confidence = Math.Max(intent.Confidence, 0.95);
            }

            return intent;
        }

        /// <summary>
        /// Metni vektöre dönüştür;
        /// </summary>
        private async Task<double[]> TextToVectorAsync(string text, Language language)
        {
            var result = await ProcessTextAsync(text, language);

            // Basit vektör temsili (gerçek uygulamada Word2Vec/BERT kullan)
            var vector = new double[100]; // 100 boyutlu vektör;
            var rnd = new Random(text.GetHashCode());

            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = rnd.NextDouble();
            }

            return vector;
        }

        /// <summary>
        /// Cosine similarity hesapla;
        /// </summary>
        private double CalculateCosineSimilarity(double[] vector1, double[] vector2)
        {
            if (vector1.Length != vector2.Length)
                throw new ArgumentException("Vectors must have same length");

            double dotProduct = 0.0, magnitude1 = 0.0, magnitude2 = 0.0;

            for (int i = 0; i < vector1.Length; i++)
            {
                dotProduct += vector1[i] * vector2[i];
                magnitude1 += Math.Pow(vector1[i], 2);
                magnitude2 += Math.Pow(vector2[i], 2);
            }

            magnitude1 = Math.Sqrt(magnitude1);
            magnitude2 = Math.Sqrt(magnitude2);

            return magnitude1 == 0 || magnitude2 == 0 ? 0 : dotProduct / (magnitude1 * magnitude2);
        }

        /// <summary>
        /// Keyword skoru hesapla;
        /// </summary>
        private double CalculateKeywordScore(IGrouping<string, Token> tokenGroup, int totalTokens)
        {
            var frequency = tokenGroup.Count();
            var inverseDocumentFrequency = Math.Log((double)totalTokens / frequency);
            return frequency * inverseDocumentFrequency;
        }

        /// <summary>
        /// Dil modellerini yükle;
        /// </summary>
        private async Task LoadLanguageModelsAsync()
        {
            // Gerçek uygulamada burada dil modelleri yüklenir;
            await Task.Delay(100); // Simülasyon;

            foreach (var lang in SupportedLanguages)
            {
                Console.WriteLine($"📖 Loading language model: {lang}");
                // Model yükleme işlemleri;
            }
        }

        /// <summary>
        /// İşlem geçmişini logla;
        /// </summary>
        private void LogProcessing(string text, TimeSpan duration, bool success, string error = null)
        {
            // Gerçek uygulamada burada logging yapılır;
            var status = success ? "SUCCESS" : "FAILED";
            Console.WriteLine($"📊 NLP Processing: {status} | {duration.TotalMilliseconds:F2}ms | {text.Truncate(30)}");
        }

        public void Dispose()
        {
            _tokenizer?.Dispose();
            _parser?.Dispose();
            _sentimentAnalyzer?.Dispose();
            _nerEngine?.Dispose();
            _languageDetector?.Dispose();

            Status = EngineStatus.Disposed;
            Console.WriteLine("🛑 NLP Engine disposed");
        }
    }

    /// <summary>
    /// NLP Sonuçları;
    /// </summary>
    public class NLPResult;
    {
        public string OriginalText { get; set; }
        public Language Language { get; set; }
        public List<Token> Tokens { get; set; }
        public List<Sentence> Sentences { get; set; }
        public SentimentResult Sentiment { get; set; }
        public List<Entity> Entities { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public Intent Intent { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }

        // Statistics;
        public int WordCount => Tokens?.Count(t => t.Type == TokenType.Word) ?? 0;
        public int SentenceCount => Sentences?.Count ?? 0;
        public int EntityCount => Entities?.Count ?? 0;
        public double ReadabilityScore => CalculateReadability();

        private double CalculateReadability()
        {
            // Basit okunabilirlik skoru;
            if (SentenceCount == 0 || WordCount == 0) return 0;

            var wordsPerSentence = (double)WordCount / SentenceCount;
            var syllablesPerWord = 1.5; // Basit tahmin;

            return 206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord);
        }
    }

    /// <summary>
    /// NLP Konfigürasyonu;
    /// </summary>
    public class NLPConfig;
    {
        public bool EnableSentimentAnalysis { get; set; } = true;
        public bool EnableEntityRecognition { get; set; } = true;
        public bool EnableSyntaxParsing { get; set; } = true;
        public bool EnableIntentDetection { get; set; } = true;
        public int MaxTextLength { get; set; } = 10000;
        public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public string ModelPath { get; set; } = "models/nlp/";
    }

    /// <summary>
    /// Token yapısı;
    /// </summary>
    public class Token;
    {
        public string Text { get; set; }
        public TokenType Type { get; set; }
        public string Lemma { get; set; }
        public string PosTag { get; set; } // Part-of-Speech tag;
        public int Position { get; set; }
        public int Length { get; set; }
        public bool IsStopWord { get; set; }
        public double Weight { get; set; } = 1.0;
    }

    /// <summary>
    /// Cümle yapısı;
    /// </summary>
    public class Sentence;
    {
        public string Text { get; set; }
        public int Position { get; set; }
        public double ImportanceScore { get; set; }
        public List<Token> Tokens { get; set; }
        public Sentiment Sentiment { get; set; }
    }

    /// <summary>
    /// Niyet yapısı;
    /// </summary>
    public class Intent;
    {
        public string Type { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Anahtar kelime;
    /// </summary>
    public class Keyword;
    {
        public string Word { get; set; }
        public int Frequency { get; set; }
        public double Score { get; set; }
        public string Category { get; set; }
    }

    /// <summary>
    /// NLP Exception;
    /// </summary>
    public class NLPException : Exception
    {
        public string ErrorCode { get; set; }
        public string Module { get; set; } = "NEDA.NLP";

        public NLPException(string message) : base(message) { }
        public NLPException(string message, Exception inner) : base(message, inner) { }
        public NLPException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    public enum EngineStatus;
    {
        Created,
        Initializing,
        Ready,
        Processing,
        Error,
        Disposed;
    }

    public enum Language;
    {
        Auto,
        Turkish,
        English,
        German,
        French,
        Spanish;
    }

    public enum TokenType;
    {
        Word,
        Number,
        Punctuation,
        Symbol,
        Space,
        Unknown;
    }

    public enum Sentiment;
    {
        VeryNegative = -2,
        Negative = -1,
        Neutral = 0,
        Positive = 1,
        VeryPositive = 2;
    }
}

// Extension methods;
public static class StringExtensions;
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
    }
}
