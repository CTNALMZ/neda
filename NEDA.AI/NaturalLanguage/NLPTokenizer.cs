using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;

namespace NEDA.AI.NaturalLanguage;
{
    /// <summary>
    /// Gelişmiş Tokenizer - Metin tokenization ve normalization;
    /// </summary>
    public class NLPTokenizer : IDisposable
    {
        private Dictionary<Language, TokenizerModel> _models;
        private bool _isInitialized;
        private HashSet<string> _stopWords;

        public NLPTokenizer()
        {
            _models = new Dictionary<Language, TokenizerModel>();
            _stopWords = new HashSet<string>();
        }

        /// <summary>
        /// Tokenizer'ı başlat;
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                Console.WriteLine("🔧 Initializing NLP Tokenizer...");

                // Stop words yükle;
                await LoadStopWordsAsync();

                // Dil modellerini yükle;
                await LoadTokenizerModelsAsync();

                _isInitialized = true;
                Console.WriteLine("✅ NLP Tokenizer initialized");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Tokenizer initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Metni token'lara ayır;
        /// </summary>
        public async Task<List<Token>> TokenizeAsync(string text, Language language)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Tokenizer başlatılmamış.");

            return await Task.Run(() =>
            {
                var tokens = new List<Token>();
                var position = 0;

                // Regex pattern for tokenization;
                var pattern = @"(\w+|\d+|[^\w\s]|\s+)";
                var matches = Regex.Matches(text, pattern);

                foreach (Match match in matches)
                {
                    var tokenText = match.Value;
                    var token = CreateToken(tokenText, position, language);

                    if (token != null)
                    {
                        tokens.Add(token);
                    }

                    position += match.Length;
                }

                return tokens;
            });
        }

        /// <summary>
        /// Cümle tokenization;
        /// </summary>
        public async Task<List<Sentence>> SentenceTokenizeAsync(string text, Language language)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Tokenizer başlatılmamış.");

            return await Task.Run(() =>
            {
                var sentences = new List<Sentence>();
                var sentencePattern = GetSentencePattern(language);
                var matches = Regex.Matches(text, sentencePattern);
                var position = 0;

                foreach (Match match in matches)
                {
                    var sentenceText = match.Value.Trim();
                    if (!string.IsNullOrEmpty(sentenceText))
                    {
                        var sentence = new Sentence;
                        {
                            Text = sentenceText,
                            Position = position,
                            ImportanceScore = CalculateSentenceImportance(sentenceText),
                            Tokens = TokenizeAsync(sentenceText, language).Result;
                        };

                        sentences.Add(sentence);
                    }

                    position++;
                }

                return sentences;
            });
        }

        /// <summary>
        /// Token oluştur;
        /// </summary>
        private Token CreateToken(string text, int position, Language language)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var tokenType = DetermineTokenType(text);
            var isStopWord = _stopWords.Contains(text.ToLower());
            var lemma = GetLemma(text, language);

            return new Token;
            {
                Text = text,
                Type = tokenType,
                Lemma = lemma,
                PosTag = GetPosTag(text, tokenType, language),
                Position = position,
                Length = text.Length,
                IsStopWord = isStopWord,
                Weight = isStopWord ? 0.1 : 1.0;
            };
        }

        /// <summary>
        /// Token tipini belirle;
        /// </summary>
        private TokenType DetermineTokenType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return TokenType.Space;

            if (char.IsLetter(text[0]))
                return TokenType.Word;

            if (char.IsDigit(text[0]))
                return TokenType.Number;

            if (char.IsPunctuation(text[0]))
                return TokenType.Punctuation;

            if (char.IsSymbol(text[0]) || text.All(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)))
                return TokenType.Symbol;

            return TokenType.Unknown;
        }

        /// <summary>
        /// Lemma (kök) bul;
        /// </summary>
        private string GetLemma(string word, Language language)
        {
            // Basit lemmatization - gerçek uygulamada ML tabanlı lemmatizer kullan;
            if (language == Language.Turkish)
            {
                return TurkishLemmatizer.Lemmatize(word);
            }

            // İngilizce için basit kurallar;
            if (language == Language.English)
            {
                return EnglishLemmatizer.Lemmatize(word);
            }

            return word.ToLower();
        }

        /// <summary>
        /// Part-of-Speech tag ata;
        /// </summary>
        private string GetPosTag(string word, TokenType type, Language language)
        {
            // Basit POS tagging - gerçek uygulamada ML modeli kullan;
            return type switch;
            {
                TokenType.Word => IsCapitalized(word) ? "PROPN" : "NOUN",
                TokenType.Number => "NUM",
                TokenType.Punctuation => "PUNCT",
                _ => "X"
            };
        }

        /// <summary>
        /// Cümle bölme pattern'i;
        /// </summary>
        private string GetSentencePattern(Language language)
        {
            return language switch;
            {
                Language.Turkish => @"[^.!?]+[.!?]",
                Language.English => @"[^.!?]+[.!?]",
                _ => @"[^.!?]+[.!?]"
            };
        }

        /// <summary>
        /// Cümle önem skoru;
        /// </summary>
        private double CalculateSentenceImportance(string sentence)
        {
            // Basit önem hesaplama;
            var words = sentence.Split(' ').Length;
            var hasNumbers = sentence.Any(char.IsDigit);
            var hasQuestions = sentence.Contains('?');

            var score = 0.0;
            score += Math.Min(words / 10.0, 1.0); // Uzunluk;
            score += hasNumbers ? 0.2 : 0;
            score += hasQuestions ? 0.3 : 0;

            return Math.Min(score, 1.0);
        }

        /// <summary>
        /// Büyük harf kontrolü;
        /// </summary>
        private bool IsCapitalized(string word)
        {
            return !string.IsNullOrEmpty(word) && char.IsUpper(word[0]);
        }

        /// <summary>
        /// Stop words yükle;
        /// </summary>
        private async Task LoadStopWordsAsync()
        {
            // Türkçe stop words;
            var turkishStopWords = new[]
            {
                "acaba", "ama", "aslında", "az", "bazı", "belki", "biri", "birkaç",
                "birşey", "biz", "bu", "çok", "çünkü", "da", "daha", "de", "defa",
                "diye", "eğer", "en", "gibi", "hem", "hep", "hepsi", "her", "hiç",
                "için", "ile", "ise", "kez", "ki", "kim", "mı", "mu", "mü", "nasıl",
                "ne", "neden", "nerde", "nerede", "nereye", "niçin", "niye", "o",
                "sanki", "şey", "siz", "şu", "tüm", "ve", "veya", "ya", "yani"
            };

            // İngilizce stop words;
            var englishStopWords = new[]
            {
                "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
                "of", "with", "by", "from", "up", "about", "into", "through", "during",
                "before", "after", "above", "below", "between", "among", "is", "are",
                "was", "were", "be", "been", "being", "have", "has", "had", "do",
                "does", "did", "will", "would", "could", "should", "may", "might", "must"
            };

            await Task.Run(() =>
            {
                foreach (var word in turkishStopWords)
                    _stopWords.Add(word);

                foreach (var word in englishStopWords)
                    _stopWords.Add(word);
            });
        }

        /// <summary>
        /// Tokenizer modellerini yükle;
        /// </summary>
        private async Task LoadTokenizerModelsAsync()
        {
            await Task.Run(() =>
            {
                // Gerçek uygulamada burada dil modelleri yüklenir;
                foreach (Language lang in Enum.GetValues(typeof(Language)))
                {
                    if (lang == Language.Auto) continue;

                    _models[lang] = new TokenizerModel;
                    {
                        Language = lang,
                        IsLoaded = true,
                        LoadedDate = DateTime.Now;
                    };
                }
            });
        }

        public void Dispose()
        {
            _models?.Clear();
            _stopWords?.Clear();
        }
    }

    /// <summary>
    /// Tokenizer Model;
    /// </summary>
    public class TokenizerModel;
    {
        public Language Language { get; set; }
        public bool IsLoaded { get; set; }
        public DateTime LoadedDate { get; set; }
        public string ModelPath { get; set; }
    }

    /// <summary>
    /// Türkçe Lemmatizer;
    /// </summary>
    public static class TurkishLemmatizer;
    {
        private static readonly Dictionary<string, string> _lemmaMap = new Dictionary<string, string>
        {
            ["gidiyorum"] = "git",
            ["gidiyorsun"] = "git",
            ["gidiyor"] = "git",
            ["gittim"] = "git",
            ["gittin"] = "git",
            ["gitti"] = "git",
            ["yapıyorum"] = "yap",
            ["yapıyorsun"] = "yap",
            ["yapıyor"] = "yap",
            ["yaptım"] = "yap",
            ["yaptın"] = "yap",
            ["yaptı"] = "yap"
        };

        public static string Lemmatize(string word)
        {
            return _lemmaMap.TryGetValue(word.ToLower(), out var lemma) ? lemma : word.ToLower();
        }
    }

    /// <summary>
    /// İngilizce Lemmatizer;
    /// </summary>
    public static class EnglishLemmatizer;
    {
        private static readonly Dictionary<string, string> _lemmaMap = new Dictionary<string, string>
        {
            ["running"] = "run",
            ["ran"] = "run",
            ["goes"] = "go",
            ["went"] = "go",
            ["gone"] = "go",
            ["doing"] = "do",
            ["did"] = "do",
            ["done"] = "do",
            ["making"] = "make",
            ["made"] = "make",
            ["taking"] = "take",
            ["took"] = "take",
            ["taken"] = "take"
        };

        public static string Lemmatize(string word)
        {
            return _lemmaMap.TryGetValue(word.ToLower(), out var lemma) ? lemma : word.ToLower();
        }
    }
}
