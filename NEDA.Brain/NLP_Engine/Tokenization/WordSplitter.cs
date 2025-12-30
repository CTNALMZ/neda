// NEDA.Brain/NLP_Engine/Tokenization/WordSplitter.cs;

using NEDA.Brain.IntentRecognition.ParameterDetection;
using NEDA.Brain.NLP_Engine.Tokenization.Configuration;
using NEDA.Brain.NLP_Engine.Tokenization.Contracts;
using NEDA.Brain.NLP_Engine.Tokenization.Exceptions;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NEDA.Brain.NLP_Engine.Tokenization;
{
    /// <summary>
    /// Advanced word splitting engine with support for multiple languages,
    /// compound words, contractions, and special character handling.
    /// Implements sophisticated text segmentation algorithms.
    /// </summary>
    public class WordSplitter : IWordSplitter, IDisposable;
    {
        #region Constants and Static Fields;

        private const int DEFAULT_MAX_TOKEN_LENGTH = 100;
        private const int DEFAULT_BUFFER_SIZE = 4096;
        private const string CONTRACTION_PATTERN = @"(?i)\b(i'm|you're|he's|she's|it's|we're|they're|i've|you've|we've|they've|i'd|you'd|he'd|she'd|we'd|they'd|i'll|you'll|he'll|she'll|we'll|they'll|isn't|aren't|wasn't|weren't|haven't|hasn't|hadn't|won't|wouldn't|shouldn't|couldn't|mustn't|can't|cannot|don't|doesn't|didn't)";
        private const string PUNCTUATION_PATTERN = @"[!\"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~]";
        private const string WHITESPACE_PATTERN = @"\s+";

        private static readonly Regex EmailPattern = new Regex(
            @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UrlPattern = new Regex(
            @"\b(?:https?://|www\.)\S+\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex IpAddressPattern = new Regex(
            @"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b",
            RegexOptions.Compiled);

        private static readonly Regex HashtagPattern = new Regex(
            @"#\w+",
            RegexOptions.Compiled);

        private static readonly Regex MentionPattern = new Regex(
            @"@\w+",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, string[]> LanguageSpecificRules = new()
        {
            ["en"] = new[] { "'s", "'ll", "'re", "'ve", "'d", "'m", "n't" },
            ["fr"] = new[] { "l'", "d'", "qu'", "s'", "n'", "c'", "j'", "m'", "t'" },
            ["de"] = new[] { "ge", "be", "er", "en", "te", "st" },
            ["it"] = new[] { "l'", "gl'", "dell'", "nell'", "sull'", "dall'", "all'" },
            ["es"] = new[] { "del", "al", "con", "por", "para" }
        };

        #endregion;

        #region Private Fields;

        private readonly WordSplitterConfiguration _configuration;
        private readonly HashSet<string> _stopWords;
        private readonly Dictionary<string, List<string>> _compoundWordDictionary;
        private readonly Dictionary<string, string[]> _contractionExpansions;
        private readonly CultureInfo _cultureInfo;
        private readonly Regex _contractionRegex;
        private readonly Regex _punctuationRegex;
        private readonly Regex _whitespaceRegex;
        private readonly Regex _wordBoundaryRegex;
        private bool _disposed;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the configuration used by this word splitter.
        /// </summary>
        public WordSplitterConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the current language code.
        /// </summary>
        public string LanguageCode => _configuration.LanguageCode;

        /// <summary>
        /// Gets whether the splitter preserves case.
        /// </summary>
        public bool PreserveCase => _configuration.PreserveCase;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of the WordSplitter class with default configuration.
        /// </summary>
        public WordSplitter() : this(new WordSplitterConfiguration())
        {
        }

        /// <summary>
        /// Initializes a new instance of the WordSplitter class with specified configuration.
        /// </summary>
        /// <param name="configuration">The configuration to use.</param>
        public WordSplitter(WordSplitterConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _cultureInfo = CultureInfo.GetCultureInfo(configuration.LanguageCode);

            InitializeRegexPatterns();
            LoadStopWords();
            LoadCompoundWords();
            LoadContractionExpansions();

            // Initialize buffers and caches;
            InitializeCaches();
        }

        /// <summary>
        /// Initializes a new instance of the WordSplitter class with specified language.
        /// </summary>
        /// <param name="languageCode">The language code (e.g., "en-US", "tr-TR").</param>
        public WordSplitter(string languageCode)
            : this(new WordSplitterConfiguration { LanguageCode = languageCode })
        {
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Splits text into words using advanced segmentation algorithms.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>An array of split words.</returns>
        public string[] Split(string text)
        {
            ValidateInput(text);

            try
            {
                return InternalSplit(text);
            }
            catch (Exception ex)
            {
                throw new WordSplittingException(
                    $"Error splitting text: {ex.Message}",
                    text,
                    ex);
            }
        }

        /// <summary>
        /// Splits text into words asynchronously.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<string[]> SplitAsync(string text)
        {
            ValidateInput(text);

            return await Task.Run(() => Split(text))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Splits text into words along with their positions in the original text.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>An array of WordInfo objects containing word and position.</returns>
        public WordInfo[] SplitWithPositions(string text)
        {
            ValidateInput(text);

            var words = new List<WordInfo>();
            var buffer = new StringBuilder();
            int position = 0;
            int wordStart = -1;

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];

                if (IsWordBoundary(current))
                {
                    if (buffer.Length > 0)
                    {
                        words.Add(new WordInfo;
                        {
                            Word = ProcessToken(buffer.ToString()),
                            StartPosition = wordStart,
                            EndPosition = i - 1,
                            Length = buffer.Length;
                        });

                        buffer.Clear();
                        wordStart = -1;
                    }

                    // Handle standalone punctuation if configured;
                    if (_configuration.HandlePunctuationSeparately && IsPunctuation(current))
                    {
                        words.Add(new WordInfo;
                        {
                            Word = current.ToString(),
                            StartPosition = i,
                            EndPosition = i,
                            Length = 1;
                        });
                    }
                }
                else;
                {
                    if (wordStart == -1)
                    {
                        wordStart = i;
                    }
                    buffer.Append(current);
                }

                position++;
            }

            // Handle last word;
            if (buffer.Length > 0)
            {
                words.Add(new WordInfo;
                {
                    Word = ProcessToken(buffer.ToString()),
                    StartPosition = wordStart,
                    EndPosition = text.Length - 1,
                    Length = buffer.Length;
                });
            }

            return words.ToArray();
        }

        /// <summary>
        /// Splits text into sentences first, then words in each sentence.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>A dictionary of sentence index to words.</returns>
        public Dictionary<int, string[]> SplitIntoSentenceWords(string text)
        {
            ValidateInput(text);

            var sentences = SplitIntoSentences(text);
            var result = new Dictionary<int, string[]>();

            for (int i = 0; i < sentences.Length; i++)
            {
                result[i] = Split(sentences[i]);
            }

            return result;
        }

        /// <summary>
        /// Splits compound words into their components based on language rules.
        /// </summary>
        /// <param name="compoundWord">The compound word to split.</param>
        /// <returns>An array of word components.</returns>
        public string[] SplitCompoundWord(string compoundWord)
        {
            if (string.IsNullOrWhiteSpace(compoundWord))
                return Array.Empty<string>();

            // Check dictionary first;
            if (_compoundWordDictionary.TryGetValue(compoundWord.ToLower(_cultureInfo), out var components))
            {
                return components.ToArray();
            }

            // Apply language-specific splitting rules;
            return ApplyCompoundSplittingRules(compoundWord);
        }

        /// <summary>
        /// Expands contractions in text (e.g., "don't" -> "do not").
        /// </summary>
        /// <param name="text">The text with contractions.</param>
        /// <returns>The text with expanded contractions.</returns>
        public string ExpandContractions(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            return _contractionRegex.Replace(text, match =>
            {
                var contraction = match.Value.ToLower(_cultureInfo);
                if (_contractionExpansions.TryGetValue(contraction, out var expansion))
                {
                    return string.Join(" ", expansion);
                }
                return match.Value;
            });
        }

        /// <summary>
        /// Checks if a word is a stop word.
        /// </summary>
        /// <param name="word">The word to check.</param>
        /// <returns>True if the word is a stop word.</returns>
        public bool IsStopWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return false;

            return _stopWords.Contains(word.ToLower(_cultureInfo));
        }

        /// <summary>
        /// Normalizes words by applying case normalization, diacritic removal, etc.
        /// </summary>
        /// <param name="word">The word to normalize.</param>
        /// <returns>The normalized word.</returns>
        public string NormalizeWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return word;

            var normalized = word;

            // Remove diacritics if configured;
            if (_configuration.RemoveDiacritics)
            {
                normalized = RemoveDiacritics(normalized);
            }

            // Apply case normalization;
            if (!_configuration.PreserveCase)
            {
                normalized = _configuration.CaseNormalization == CaseNormalization.Lowercase;
                    ? normalized.ToLower(_cultureInfo)
                    : normalized.ToUpper(_cultureInfo);
            }

            // Trim extra spaces;
            normalized = normalized.Trim();

            return normalized;
        }

        /// <summary>
        /// Updates the language configuration dynamically.
        /// </summary>
        /// <param name="languageCode">The new language code.</param>
        public void UpdateLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                throw new ArgumentException("Language code cannot be null or empty", nameof(languageCode));

            _configuration.LanguageCode = languageCode;
            ReloadLanguageResources();
        }

        #endregion;

        #region Private Methods;

        private string[] InternalSplit(string text)
        {
            // Step 1: Pre-process text;
            var processedText = PreprocessText(text);

            // Step 2: Handle special patterns (URLs, emails, etc.)
            var tokens = ExtractSpecialPatterns(processedText, out var remainingText);

            // Step 3: Split remaining text;
            var wordTokens = SplitRemainingText(remainingText);

            // Step 4: Merge and filter tokens;
            var allTokens = MergeTokens(tokens, wordTokens);

            // Step 5: Post-process tokens;
            return PostProcessTokens(allTokens);
        }

        private string PreprocessText(string text)
        {
            var processed = text;

            // Expand contractions if configured;
            if (_configuration.ExpandContractions)
            {
                processed = ExpandContractions(processed);
            }

            // Normalize whitespace;
            processed = _whitespaceRegex.Replace(processed, " ");

            // Handle special characters;
            if (_configuration.NormalizeSpecialCharacters)
            {
                processed = NormalizeSpecialCharacters(processed);
            }

            return processed.Trim();
        }

        private List<string> ExtractSpecialPatterns(string text, out string remainingText)
        {
            var tokens = new List<string>();
            remainingText = text;

            if (_configuration.PreserveSpecialPatterns)
            {
                // Extract and remove special patterns;
                var matches = new List<PatternMatch>();

                // Find all special patterns;
                FindPatternMatches(text, EmailPattern, PatternType.Email, matches);
                FindPatternMatches(text, UrlPattern, PatternType.Url, matches);
                FindPatternMatches(text, IpAddressPattern, PatternType.IpAddress, matches);
                FindPatternMatches(text, HashtagPattern, PatternType.Hashtag, matches);
                FindPatternMatches(text, MentionPattern, PatternType.Mention, matches);

                // Sort by position;
                matches.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));

                // Reconstruct remaining text;
                var builder = new StringBuilder();
                int lastIndex = 0;

                foreach (var match in matches)
                {
                    // Add text before match;
                    if (match.StartIndex > lastIndex)
                    {
                        builder.Append(text, lastIndex, match.StartIndex - lastIndex);
                        builder.Append(' '); // Replace with space;
                    }

                    // Add token;
                    tokens.Add(match.Value);

                    lastIndex = match.EndIndex;
                }

                // Add remaining text;
                if (lastIndex < text.Length)
                {
                    builder.Append(text, lastIndex, text.Length - lastIndex);
                }

                remainingText = builder.ToString();
            }

            return tokens;
        }

        private string[] SplitRemainingText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            // Use word boundary detection;
            var words = _wordBoundaryRegex.Split(text)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .ToArray();

            // Handle compound words;
            if (_configuration.SplitCompoundWords)
            {
                words = words.SelectMany(w => SplitCompoundWordIfNeeded(w))
                    .ToArray();
            }

            return words;
        }

        private string[] SplitCompoundWordIfNeeded(string word)
        {
            // Check if word should be split as compound;
            if (word.Length < _configuration.MinCompoundWordLength)
                return new[] { word };

            var components = SplitCompoundWord(word);
            if (components.Length > 1)
            {
                return components;
            }

            return new[] { word };
        }

        private string[] MergeTokens(List<string> specialTokens, string[] wordTokens)
        {
            var allTokens = new List<string>(specialTokens.Count + wordTokens.Length);
            allTokens.AddRange(specialTokens);
            allTokens.AddRange(wordTokens);

            return allTokens.ToArray();
        }

        private string[] PostProcessTokens(string[] tokens)
        {
            var processedTokens = tokens;
                .Select(ProcessToken)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToArray();

            // Remove stop words if configured;
            if (_configuration.RemoveStopWords)
            {
                processedTokens = processedTokens;
                    .Where(token => !IsStopWord(token))
                    .ToArray();
            }

            // Apply length filtering;
            if (_configuration.MinWordLength > 1 || _configuration.MaxWordLength < int.MaxValue)
            {
                processedTokens = processedTokens;
                    .Where(token => token.Length >= _configuration.MinWordLength &&
                                   token.Length <= _configuration.MaxWordLength)
                    .ToArray();
            }

            return processedTokens;
        }

        private string ProcessToken(string token)
        {
            var processed = token;

            // Normalize;
            processed = NormalizeWord(processed);

            // Clean token;
            processed = CleanToken(processed);

            return processed;
        }

        private string CleanToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return token;

            // Remove leading/trailing punctuation;
            if (_configuration.TrimPunctuation)
            {
                token = token.Trim(PUNCTUATION_PATTERN.ToCharArray());
            }

            return token;
        }

        private string[] ApplyCompoundSplittingRules(string word)
        {
            var components = new List<string>();
            var current = word;

            // Apply language-specific splitting;
            if (LanguageSpecificRules.TryGetValue(_configuration.LanguageCode.Substring(0, 2), out var rules))
            {
                foreach (var rule in rules)
                {
                    if (current.EndsWith(rule, StringComparison.OrdinalIgnoreCase))
                    {
                        components.Add(current.Substring(0, current.Length - rule.Length));
                        components.Add(rule);
                        return components.ToArray();
                    }
                }
            }

            // Try morphological analysis for long words;
            if (current.Length > 8)
            {
                var splitPoints = FindMorphologicalSplitPoints(current);
                if (splitPoints.Count > 0)
                {
                    return SplitAtPoints(current, splitPoints);
                }
            }

            return new[] { word };
        }

        private List<int> FindMorphologicalSplitPoints(string word)
        {
            var splitPoints = new List<int>();

            // Common prefixes and suffixes for English;
            var prefixes = new[] { "anti", "auto", "bi", "co", "de", "dis", "ex", "hyper", "in", "inter", "micro", "mid", "mis", "multi", "non", "over", "post", "pre", "re", "semi", "sub", "super", "trans", "tri", "un", "under" };
            var suffixes = new[] { "able", "al", "ance", "ant", "ary", "ate", "ation", "ence", "ent", "er", "est", "ful", "ible", "ic", "ical", "ify", "ing", "ion", "ish", "ism", "ist", "ity", "ive", "ize", "less", "ly", "ment", "ness", "or", "ous", "s", "ship", "tion", "ty", "ure", "y" };

            // Check for prefixes;
            foreach (var prefix in prefixes)
            {
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    word.Length > prefix.Length + 2)
                {
                    splitPoints.Add(prefix.Length);
                }
            }

            // Check for suffixes;
            foreach (var suffix in suffixes)
            {
                if (word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    word.Length > suffix.Length + 2)
                {
                    splitPoints.Add(word.Length - suffix.Length);
                }
            }

            // Look for vowel-consonant boundaries (common in compound words)
            for (int i = 1; i < word.Length - 1; i++)
            {
                if (IsVowel(word[i - 1]) && IsConsonant(word[i]))
                {
                    splitPoints.Add(i);
                }
            }

            return splitPoints.Distinct().OrderBy(p => p).ToList();
        }

        private string[] SplitAtPoints(string word, List<int> splitPoints)
        {
            var components = new List<string>();
            int start = 0;

            foreach (var point in splitPoints)
            {
                if (point > start && point < word.Length)
                {
                    components.Add(word.Substring(start, point - start));
                    start = point;
                }
            }

            if (start < word.Length)
            {
                components.Add(word.Substring(start));
            }

            return components.ToArray();
        }

        private bool IsVowel(char c)
        {
            c = char.ToLower(c, _cultureInfo);
            return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u';
        }

        private bool IsConsonant(char c)
        {
            c = char.ToLower(c, _cultureInfo);
            return c >= 'a' && c <= 'z' && !IsVowel(c);
        }

        private bool IsWordBoundary(char c)
        {
            return char.IsWhiteSpace(c) ||
                   (_configuration.HandlePunctuationSeparately && IsPunctuation(c));
        }

        private bool IsPunctuation(char c)
        {
            return char.IsPunctuation(c) || PUNCTUATION_PATTERN.Contains(c);
        }

        private string NormalizeSpecialCharacters(string text)
        {
            var builder = new StringBuilder(text.Length);

            foreach (char c in text)
            {
                switch (c)
                {
                    case '«':
                    case '»':
                    case '‹':
                    case '›':
                        builder.Append('"');
                        break;
                    case '–':
                    case '—':
                        builder.Append('-');
                        break;
                    case '…':
                        builder.Append("...");
                        break;
                    case '‘':
                    case '’':
                    case '‚':
                        builder.Append('\'');
                        break;
                    case '“':
                    case '”':
                    case '„':
                        builder.Append('"');
                        break;
                    case '•':
                        builder.Append('*');
                        break;
                    case '¦':
                        builder.Append('|');
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        private string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();

            foreach (char c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private string[] SplitIntoSentences(string text)
        {
            // Basic sentence splitting - can be enhanced with ML model;
            var sentences = new List<string>();
            var builder = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                builder.Append(current);

                if (IsSentenceEnding(current, i, text))
                {
                    sentences.Add(builder.ToString().Trim());
                    builder.Clear();
                }
            }

            if (builder.Length > 0)
            {
                sentences.Add(builder.ToString().Trim());
            }

            return sentences.ToArray();
        }

        private bool IsSentenceEnding(char c, int position, string text)
        {
            if (c == '.' || c == '!' || c == '?')
            {
                // Check for ellipsis;
                if (c == '.' && position + 2 < text.Length &&
                    text[position + 1] == '.' && text[position + 2] == '.')
                {
                    return false;
                }

                // Check if next character starts a new sentence;
                if (position + 1 < text.Length)
                {
                    char next = text[position + 1];
                    if (char.IsWhiteSpace(next) && position + 2 < text.Length)
                    {
                        char nextAfterSpace = text[position + 2];
                        return char.IsUpper(nextAfterSpace);
                    }
                }
            }

            return false;
        }

        private void FindPatternMatches(string text, Regex pattern, PatternType type, List<PatternMatch> matches)
        {
            foreach (Match match in pattern.Matches(text))
            {
                matches.Add(new PatternMatch;
                {
                    Value = match.Value,
                    StartIndex = match.Index,
                    EndIndex = match.Index + match.Length,
                    Type = type;
                });
            }
        }

        #endregion;

        #region Initialization Methods;

        private void InitializeRegexPatterns()
        {
            _contractionRegex = new Regex(
                CONTRACTION_PATTERN,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            _punctuationRegex = new Regex(
                PUNCTUATION_PATTERN,
                RegexOptions.Compiled);

            _whitespaceRegex = new Regex(
                WHITESPACE_PATTERN,
                RegexOptions.Compiled);

            // Complex word boundary detection;
            var wordBoundaryPattern = BuildWordBoundaryPattern();
            _wordBoundaryRegex = new Regex(
                wordBoundaryPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        private string BuildWordBoundaryPattern()
        {
            // Build pattern based on configuration;
            var patterns = new List<string>();

            if (_configuration.SplitOnPunctuation)
            {
                patterns.Add(PUNCTUATION_PATTERN);
            }

            if (_configuration.SplitOnWhitespace)
            {
                patterns.Add(WHITESPACE_PATTERN);
            }

            if (_configuration.SplitOnNumbers)
            {
                patterns.Add(@"\d+");
            }

            if (patterns.Count == 0)
            {
                patterns.Add(WHITESPACE_PATTERN);
            }

            return $"[{string.Join("", patterns)}]+";
        }

        private void LoadStopWords()
        {
            _stopWords = new HashSet<string>(StringComparer.Create(_cultureInfo, true));

            // Load language-specific stop words;
            var stopWords = StopWordLoader.LoadStopWords(_configuration.LanguageCode);
            foreach (var word in stopWords)
            {
                _stopWords.Add(word.ToLower(_cultureInfo));
            }

            // Add custom stop words;
            if (_configuration.CustomStopWords != null)
            {
                foreach (var word in _configuration.CustomStopWords)
                {
                    _stopWords.Add(word.ToLower(_cultureInfo));
                }
            }
        }

        private void LoadCompoundWords()
        {
            _compoundWordDictionary = new Dictionary<string, List<string>>(StringComparer.Create(_cultureInfo, true));

            // Load language-specific compound words;
            var compounds = CompoundWordLoader.LoadCompoundWords(_configuration.LanguageCode);
            foreach (var compound in compounds)
            {
                _compoundWordDictionary[compound.Key.ToLower(_cultureInfo)] =
                    compound.Value.Select(w => w.ToLower(_cultureInfo)).ToList();
            }
        }

        private void LoadContractionExpansions()
        {
            _contractionExpansions = new Dictionary<string, string[]>(StringComparer.Create(_cultureInfo, true));

            // English contractions;
            _contractionExpansions["i'm"] = new[] { "I", "am" };
            _contractionExpansions["you're"] = new[] { "you", "are" };
            _contractionExpansions["he's"] = new[] { "he", "is" };
            _contractionExpansions["she's"] = new[] { "she", "is" };
            _contractionExpansions["it's"] = new[] { "it", "is" };
            _contractionExpansions["we're"] = new[] { "we", "are" };
            _contractionExpansions["they're"] = new[] { "they", "are" };
            _contractionExpansions["i've"] = new[] { "I", "have" };
            _contractionExpansions["you've"] = new[] { "you", "have" };
            _contractionExpansions["we've"] = new[] { "we", "have" };
            _contractionExpansions["they've"] = new[] { "they", "have" };
            _contractionExpansions["i'd"] = new[] { "I", "would" };
            _contractionExpansions["you'd"] = new[] { "you", "would" };
            _contractionExpansions["he'd"] = new[] { "he", "would" };
            _contractionExpansions["she'd"] = new[] { "she", "would" };
            _contractionExpansions["we'd"] = new[] { "we", "would" };
            _contractionExpansions["they'd"] = new[] { "they", "would" };
            _contractionExpansions["i'll"] = new[] { "I", "will" };
            _contractionExpansions["you'll"] = new[] { "you", "will" };
            _contractionExpansions["he'll"] = new[] { "he", "will" };
            _contractionExpansions["she'll"] = new[] { "she", "will" };
            _contractionExpansions["we'll"] = new[] { "we", "will" };
            _contractionExpansions["they'll"] = new[] { "they", "will" };
            _contractionExpansions["isn't"] = new[] { "is", "not" };
            _contractionExpansions["aren't"] = new[] { "are", "not" };
            _contractionExpansions["wasn't"] = new[] { "was", "not" };
            _contractionExpansions["weren't"] = new[] { "were", "not" };
            _contractionExpansions["haven't"] = new[] { "have", "not" };
            _contractionExpansions["hasn't"] = new[] { "has", "not" };
            _contractionExpansions["hadn't"] = new[] { "had", "not" };
            _contractionExpansions["won't"] = new[] { "will", "not" };
            _contractionExpansions["wouldn't"] = new[] { "would", "not" };
            _contractionExpansions["shouldn't"] = new[] { "should", "not" };
            _contractionExpansions["couldn't"] = new[] { "could", "not" };
            _contractionExpansions["mustn't"] = new[] { "must", "not" };
            _contractionExpansions["can't"] = new[] { "cannot" };
            _contractionExpansions["cannot"] = new[] { "can", "not" };
            _contractionExpansions["don't"] = new[] { "do", "not" };
            _contractionExpansions["doesn't"] = new[] { "does", "not" };
            _contractionExpansions["didn't"] = new[] { "did", "not" };

            // Add language-specific contractions;
            LoadLanguageSpecificContractions();
        }

        private void LoadLanguageSpecificContractions()
        {
            // Add other language contractions based on language code;
            if (_configuration.LanguageCode.StartsWith("fr"))
            {
                _contractionExpansions["l'"] = new[] { "le", "la" };
                _contractionExpansions["d'"] = new[] { "de" };
                _contractionExpansions["qu'"] = new[] { "que" };
            }
        }

        private void InitializeCaches()
        {
            // Initialize any LRU caches or buffers here;
            // Can be expanded based on performance requirements;
        }

        private void ReloadLanguageResources()
        {
            LoadStopWords();
            LoadCompoundWords();
            LoadContractionExpansions();
        }

        #endregion;

        #region Validation Methods;

        private void ValidateInput(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text), "Text cannot be null");

            if (text.Length > _configuration.MaxInputLength)
                throw new WordSplittingException(
                    $"Input text exceeds maximum length of {_configuration.MaxInputLength} characters",
                    text);
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    _stopWords?.Clear();
                    _compoundWordDictionary?.Clear();
                    _contractionExpansions?.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~WordSplitter()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Represents a word with its position information.
        /// </summary>
        public struct WordInfo;
        {
            public string Word { get; set; }
            public int StartPosition { get; set; }
            public int EndPosition { get; set; }
            public int Length { get; set; }

            public override string ToString()
            {
                return $"{Word} [{StartPosition}-{EndPosition}]";
            }
        }

        private enum PatternType;
        {
            Email,
            Url,
            IpAddress,
            Hashtag,
            Mention;
        }

        private struct PatternMatch;
        {
            public string Value { get; set; }
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public PatternType Type { get; set; }
        }

        #endregion;
    }
}
