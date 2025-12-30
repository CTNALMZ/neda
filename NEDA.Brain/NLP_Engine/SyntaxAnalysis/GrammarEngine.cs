using Microsoft.Extensions.Logging;
using NEDA.Brain.IntentRecognition.ParameterDetection;
using NEDA.Common;
using NEDA.Common.Extensions;
using NEDA.ExceptionHandling;
using NEDA.Monitoring;
using NEDA.Monitoring.Diagnostics.ProblemSolver;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.Brain.NLP_Engine.SyntaxAnalysis.GrammarEngine;

namespace NEDA.Brain.NLP_Engine.SyntaxAnalysis;
{
    /// <summary>
    /// Gelişmiş dilbilgisi (grammar) analiz motoru.
    /// Çoklu dil desteği, bağlamsal analiz ve hata düzeltme özellikleri ile çalışır.
    /// </summary>
    public class GrammarEngine : IGrammarEngine, IDisposable;
    {
        private readonly ILogger<GrammarEngine> _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly ISyntaxTreeBuilder _syntaxTreeBuilder;
        private readonly ILanguageModel _languageModel;
        private readonly IGrammarRuleLoader _ruleLoader;

        private readonly ConcurrentDictionary<string, GrammarModel> _loadedModels;
        private readonly ConcurrentDictionary<string, GrammarRuleSet> _ruleSets;
        private readonly ConcurrentDictionary<string, GrammarPattern> _patterns;
        private readonly ReaderWriterLockSlim _grammarLock;

        private bool _disposed;
        private readonly GrammarEngineConfiguration _configuration;

        /// <summary>
        /// Dilbilgisi modeli yapısı;
        /// </summary>
        private class GrammarModel;
        {
            public string Language { get; set; } = "en";
            public string ModelType { get; set; } = "Statistical";
            public DateTime LoadedAt { get; set; }
            public DateTime LastUsed { get; set; }
            public GrammarModelPerformance Performance { get; set; } = new();
            public Dictionary<string, double> RuleProbabilities { get; set; } = new();
            public Dictionary<string, GrammarPattern> CommonPatterns { get; set; } = new();
            public LanguageStatistics Statistics { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();

            // Parsing cache;
            public ConcurrentDictionary<string, ParseResult> ParseCache { get; set; } = new();

            public bool IsExpired => DateTime.UtcNow - LoadedAt > TimeSpan.FromHours(12);
        }

        /// <summary>
        /// Dilbilgisi kural seti;
        /// </summary>
        private class GrammarRuleSet;
        {
            public string Language { get; set; } = "en";
            public string RuleSetId { get; set; } = string.Empty;
            public List<GrammarRule> Rules { get; set; } = new();
            public Dictionary<string, GrammarProduction> Productions { get; set; } = new();
            public Dictionary<string, List<string>> FirstSets { get; set; } = new();
            public Dictionary<string, List<string>> FollowSets { get; set; } = new();
            public GrammarRule? StartSymbol { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastModified { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new();

            // Conflict resolution;
            public ConflictResolutionStrategy ConflictStrategy { get; set; } = ConflictResolutionStrategy.FirstMatch;
            public Dictionary<string, PrecedenceRule> PrecedenceRules { get; set; } = new();
        }

        /// <summary>
        /// Dilbilgisi pattern'ı;
        /// </summary>
        private class GrammarPattern;
        {
            public string PatternId { get; set; } = string.Empty;
            public string PatternType { get; set; } = "regex";
            public string Language { get; set; } = "en";
            public string PatternText { get; set; } = string.Empty;
            public Regex? RegexPattern { get; set; }
            public List<GrammarRule> RelatedRules { get; set; } = new();
            public double Confidence { get; set; } = 0.9;
            public Dictionary<string, object> Metadata { get; set; } = new();
            public DateTime CreatedAt { get; set; }
            public DateTime LastUsed { get; set; }
            public int MatchCount { get; set; }

            // Statistical data;
            public double Frequency { get; set; }
            public Dictionary<string, double> TransitionProbabilities { get; set; } = new();
        }

        /// <summary>
        /// Grammar engine konfigürasyonu;
        /// </summary>
        public class GrammarEngineConfiguration;
        {
            public int MaxSentenceLength { get; set; } = 1000;
            public int MaxParseDepth { get; set; } = 50;
            public int MaxParseTimeMs { get; set; } = 5000;
            public bool EnableCaching { get; set; } = true;
            public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);
            public int CacheSize { get; set; } = 10000;
            public bool EnableStatisticalParsing { get; set; } = true;
            public bool EnableRuleBasedParsing { get; set; } = true;
            public bool EnableNeuralParsing { get; set; } = false;
            public double ConfidenceThreshold { get; set; } = 0.7;
            public bool EnableErrorCorrection { get; set; } = true;
            public int MaxErrorCorrectionAttempts { get; set; } = 3;
            public bool EnableAmbiguityResolution { get; set; } = true;
            public AmbiguityResolutionStrategy AmbiguityStrategy { get; set; } = AmbiguityResolutionStrategy.Statistical;
            public bool EnableRealTimeLearning { get; set; } = true;
            public int LearningBatchSize { get; set; } = 100;
            public List<string> SupportedLanguages { get; set; } = new()
            {
                "en", "tr", "es", "fr", "de", "it", "ru", "zh", "ja", "ko"
            };
            public Dictionary<string, LanguageSpecificConfig> LanguageConfigs { get; set; } = new();
        }

        /// <summary>
        /// Dilbilgisi kuralı;
        /// </summary>
        public class GrammarRule;
        {
            public string RuleId { get; set; } = string.Empty;
            public string LeftHandSide { get; set; } = string.Empty;
            public List<string> RightHandSide { get; set; } = new();
            public RuleType Type { get; set; } = RuleType.ContextFree;
            public double Probability { get; set; } = 1.0;
            public List<SemanticAction> SemanticActions { get; set; } = new();
            public Dictionary<string, object> Attributes { get; set; } = new();
            public List<Constraint> Constraints { get; set; } = new();
            public List<string> Examples { get; set; } = new();

            // For parsing;
            public int DotPosition { get; set; } = 0;
            public int StartPosition { get; set; } = 0;
            public GrammarRule? Origin { get; set; }

            public bool IsComplete => DotPosition >= RightHandSide.Count;
            public string NextSymbol => !IsComplete ? RightHandSide[DotPosition] : string.Empty;

            public GrammarRule Advance()
            {
                return new GrammarRule;
                {
                    RuleId = RuleId,
                    LeftHandSide = LeftHandSide,
                    RightHandSide = new List<string>(RightHandSide),
                    Type = Type,
                    Probability = Probability,
                    DotPosition = DotPosition + 1,
                    StartPosition = StartPosition,
                    Origin = Origin;
                };
            }

            public override string ToString()
            {
                var rhs = string.Join(" ", RightHandSide);
                var dotPosition = DotPosition;
                var parts = rhs.Split(' ');

                var result = new StringBuilder();
                result.Append(LeftHandSide);
                result.Append(" → ");

                for (int i = 0; i < parts.Length; i++)
                {
                    if (i == dotPosition) result.Append("• ");
                    result.Append(parts[i]);
                    if (i < parts.Length - 1) result.Append(" ");
                }

                if (dotPosition >= parts.Length) result.Append(" •");

                return result.ToString();
            }
        }

        /// <summary>
        /// Dilbilgisi üretimi;
        /// </summary>
        public class GrammarProduction;
        {
            public string ProductionId { get; set; } = string.Empty;
            public string NonTerminal { get; set; } = string.Empty;
            public List<ProductionAlternative> Alternatives { get; set; } = new();
            public double TotalProbability { get; set; } = 1.0;
            public Dictionary<string, object> Metadata { get; set; } = new();
        }

        /// <summary>
        /// Üretim alternatifi;
        /// </summary>
        public class ProductionAlternative;
        {
            public List<string> Symbols { get; set; } = new();
            public double Probability { get; set; } = 1.0;
            public List<SemanticRule> SemanticRules { get; set; } = new();
            public Dictionary<string, object> Conditions { get; set; } = new();
        }

        /// <summary>
        /// Parse sonucu;
        /// </summary>
        public class ParseResult;
        {
            public string ParseId { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public string Language { get; set; } = "en";
            public SyntaxTree? SyntaxTree { get; set; }
            public List<ParseTree> ParseTrees { get; set; } = new();
            public List<GrammarError> Errors { get; set; } = new();
            public List<GrammarWarning> Warnings { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();
            public double Confidence { get; set; }
            public TimeSpan ParseTime { get; set; }
            public DateTime ParsedAt { get; set; }

            // Ambiguity information;
            public int AmbiguityCount { get; set; }
            public List<AmbiguityResolution> Resolutions { get; set; } = new();

            public bool IsValid => Errors.Count == 0;
            public bool HasWarnings => Warnings.Count > 0;

            public string GetParseSummary()
            {
                return $"Parse ID: {ParseId}, Valid: {IsValid}, Confidence: {Confidence:P0}, " +
                       $"Parse Time: {ParseTime.TotalMilliseconds}ms";
            }
        }

        /// <summary>
        /// Sözdizimi ağacı;
        /// </summary>
        public class SyntaxTree;
        {
            public TreeNode Root { get; set; } = new();
            public List<TreeNode> Nodes { get; set; } = new();
            public Dictionary<string, object> Properties { get; set; } = new();
            public int Depth { get; set; }
            public int NodeCount => Nodes.Count;

            // Tree metrics;
            public double BranchingFactor { get; set; }
            public double BalanceFactor { get; set; }
            public Dictionary<int, int> DepthDistribution { get; set; } = new();

            public List<TreeNode> GetLeaves()
            {
                return Nodes.Where(n => n.Children.Count == 0).ToList();
            }

            public List<TreeNode> GetNodesByType(string nodeType)
            {
                return Nodes.Where(n => n.NodeType == nodeType).ToList();
            }

            public string ToString(int maxDepth = 10)
            {
                return PrintTree(Root, 0, maxDepth);
            }

            private string PrintTree(TreeNode node, int depth, int maxDepth)
            {
                if (depth > maxDepth) return "...";

                var indent = new string(' ', depth * 2);
                var result = new StringBuilder();

                result.AppendLine($"{indent}{node}");

                foreach (var child in node.Children)
                {
                    result.Append(PrintTree(child, depth + 1, maxDepth));
                }

                return result.ToString();
            }
        }

        /// <summary>
        /// Ağaç düğümü;
        /// </summary>
        public class TreeNode;
        {
            public string NodeId { get; set; } = string.Empty;
            public string NodeType { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public List<TreeNode> Children { get; set; } = new();
            public TreeNode? Parent { get; set; }
            public Dictionary<string, object> Attributes { get; set; } = new();
            public int Depth { get; set; }
            public int Position { get; set; }

            // For dependency parsing;
            public string DependencyRelation { get; set; } = string.Empty;
            public TreeNode? Head { get; set; }
            public List<TreeNode> Dependents { get; set; } = new();

            public bool IsLeaf => Children.Count == 0;
            public bool IsRoot => Parent == null;

            public override string ToString()
            {
                return $"[{NodeType}: {Label}]";
            }

            public TreeNode AddChild(TreeNode child)
            {
                child.Parent = this;
                child.Depth = Depth + 1;
                Children.Add(child);
                return this;
            }

            public List<TreeNode> GetAncestors()
            {
                var ancestors = new List<TreeNode>();
                var current = Parent;

                while (current != null)
                {
                    ancestors.Add(current);
                    current = current.Parent;
                }

                return ancestors;
            }

            public List<TreeNode> GetDescendants()
            {
                var descendants = new List<TreeNode>();
                CollectDescendants(this, descendants);
                return descendants;
            }

            private void CollectDescendants(TreeNode node, List<TreeNode> descendants)
            {
                foreach (var child in node.Children)
                {
                    descendants.Add(child);
                    CollectDescendants(child, descendants);
                }
            }
        }

        /// <summary>
        /// Parse ağacı;
        /// </summary>
        public class ParseTree : SyntaxTree;
        {
            public double Probability { get; set; } = 1.0;
            public List<GrammarRule> AppliedRules { get; set; } = new();
            public Dictionary<int, string> TokenMapping { get; set; } = new();
            public bool IsPreferred { get; set; }

            // For chart parsing;
            public ChartCell? OriginCell { get; set; }
            public List<ChartCell> ContributingCells { get; set; } = new();
        }

        /// <summary>
        /// Chart parsing hücresi;
        /// </summary>
        public class ChartCell;
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public List<ChartItem> Items { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();

            public int Length => EndIndex - StartIndex;

            public bool ContainsRule(string leftHandSide)
            {
                return Items.Any(item => item.Rule.LeftHandSide == leftHandSide && item.Rule.IsComplete);
            }

            public List<ChartItem> GetCompletedItems()
            {
                return Items.Where(item => item.Rule.IsComplete).ToList();
            }

            public List<ChartItem> GetActiveItems()
            {
                return Items.Where(item => !item.Rule.IsComplete).ToList();
            }
        }

        /// <summary>
        /// Chart item;
        /// </summary>
        public class ChartItem;
        {
            public GrammarRule Rule { get; set; } = new();
            public List<ChartItem> Backpointers { get; set; } = new();
            public double Probability { get; set; } = 1.0;
            public Dictionary<string, object> Attributes { get; set; } = new();

            public override string ToString()
            {
                return $"{Rule} [{Rule.StartPosition}-{Rule.StartPosition + Rule.DotPosition}]";
            }
        }

        /// <summary>
        /// Dilbilgisi hatası;
        /// </summary>
        public class GrammarError;
        {
            public string ErrorId { get; set; } = string.Empty;
            public ErrorType Type { get; set; }
            public string Message { get; set; } = string.Empty;
            public int StartPosition { get; set; }
            public int EndPosition { get; set; }
            public int Length => EndPosition - StartPosition;
            public ErrorSeverity Severity { get; set; }
            public List<ErrorCorrection> Corrections { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();

            public string GetErrorDescription()
            {
                return $"[{Severity}] {Type}: {Message} (Position: {StartPosition}-{EndPosition})";
            }
        }

        /// <summary>
        /// Hata düzeltme önerisi;
        /// </summary>
        public class ErrorCorrection;
        {
            public string OriginalText { get; set; } = string.Empty;
            public string SuggestedText { get; set; } = string.Empty;
            public double Confidence { get; set; }
            public CorrectionType Type { get; set; }
            public List<string> Explanation { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();
        }

        /// <summary>
        /// Belirsizlik çözümü;
        /// </summary>
        public class AmbiguityResolution;
        {
            public string AmbiguityId { get; set; } = string.Empty;
            public AmbiguityType Type { get; set; }
            public List<ParseTree> CandidateTrees { get; set; } = new();
            public ParseTree? SelectedTree { get; set; }
            public double SelectionConfidence { get; set; }
            public ResolutionMethod Method { get; set; }
            public Dictionary<string, object> ResolutionFactors { get; set; } = new();
        }

        /// <summary>
        /// Dil istatistikleri;
        /// </summary>
        public class LanguageStatistics;
        {
            public string Language { get; set; } = "en";
            public Dictionary<string, double> NGramProbabilities { get; set; } = new();
            public Dictionary<string, double> WordFrequencies { get; set; } = new();
            public Dictionary<string, double> POSFrequencies { get; set; } = new();
            public Dictionary<string, double> TransitionProbabilities { get; set; } = new();
            public double Perplexity { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        /// <summary>
        /// Kural türleri;
        /// </summary>
        public enum RuleType;
        {
            ContextFree,
            ContextSensitive,
            Regular,
            Augmented,
            Probabilistic,
            Temporal,
            Semantic;
        }

        /// <summary>
        /// Hata türleri;
        /// </summary>
        public enum ErrorType;
        {
            SyntaxError,
            GrammarError,
            SpellingError,
            PunctuationError,
            AgreementError,
            WordOrderError,
            MissingElement,
            ExtraElement,
            TenseError,
            CaseError;
        }

        /// <summary>
        /// Hata şiddeti;
        /// </summary>
        public enum ErrorSeverity;
        {
            Info,
            Warning,
            Error,
            Critical;
        }

        /// <summary>
        /// Düzeltme türü;
        /// </summary>
        public enum CorrectionType;
        {
            Insertion,
            Deletion,
            Replacement,
            Reordering,
            Capitalization,
            Punctuation;
        }

        /// <summary>
        /// Belirsizlik türü;
        /// </summary>
        public enum AmbiguityType;
        {
            Lexical,
            Structural,
            Attachment,
            Coordination,
            Scope,
            Anaphora;
        }

        /// <summary>
        /// Çözüm yöntemi;
        /// </summary>
        public enum ResolutionMethod;
        {
            Statistical,
            Semantic,
            Pragmatic,
            UserPreference,
            Default;
        }

        /// <summary>
        /// Çakışma çözüm stratejisi;
        /// </summary>
        public enum ConflictResolutionStrategy;
        {
            FirstMatch,
            LongestMatch,
            MostSpecific,
            Probabilistic,
            UserDefined;
        }

        /// <summary>
        /// Belirsizlik çözüm stratejisi;
        /// </summary>
        public enum AmbiguityResolutionStrategy;
        {
            Statistical,
            Semantic,
            Contextual,
            Hybrid,
            FirstFound;
        }

        /// <summary>
        /// Öncelik kuralı;
        /// </summary>
        public class PrecedenceRule;
        {
            public string Operator { get; set; } = string.Empty;
            public PrecedenceLevel Level { get; set; }
            public Associativity Associativity { get; set; }
            public Dictionary<string, object> Conditions { get; set; } = new();
        }

        public enum PrecedenceLevel;
        {
            Lowest,
            Low,
            Medium,
            High,
            Highest;
        }

        public enum Associativity;
        {
            Left,
            Right,
            NonAssociative;
        }

        public event EventHandler<ParseCompletedEventArgs>? ParseCompleted;
        public event EventHandler<GrammarErrorDetectedEventArgs>? GrammarErrorDetected;
        public event EventHandler<AmbiguityResolvedEventArgs>? AmbiguityResolved;

        private readonly Timer _cleanupTimer;
        private readonly GrammarEngineStatistics _statistics;
        private readonly object _statsLock = new object();

        /// <summary>
        /// GrammarEngine örneği oluşturur;
        /// </summary>
        public GrammarEngine(
            ILogger<GrammarEngine> logger,
            IMetricsCollector metricsCollector,
            ISyntaxTreeBuilder syntaxTreeBuilder,
            ILanguageModel languageModel,
            IGrammarRuleLoader ruleLoader,
            GrammarEngineConfiguration? configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _syntaxTreeBuilder = syntaxTreeBuilder ?? throw new ArgumentNullException(nameof(syntaxTreeBuilder));
            _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
            _ruleLoader = ruleLoader ?? throw new ArgumentNullException(nameof(ruleLoader));

            _configuration = configuration ?? new GrammarEngineConfiguration();
            _loadedModels = new ConcurrentDictionary<string, GrammarModel>();
            _ruleSets = new ConcurrentDictionary<string, GrammarRuleSet>();
            _patterns = new ConcurrentDictionary<string, GrammarPattern>();
            _grammarLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _statistics = new GrammarEngineStatistics();

            // Temizlik timer'ı başlat;
            _cleanupTimer = new Timer(
                _ => CleanupExpiredData(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));

            InitializeDefaultRules();
            LoadDefaultModels();

            _logger.LogInformation("GrammarEngine initialized with {Count} supported languages",
                _configuration.SupportedLanguages.Count);
        }

        /// <summary>
        /// Varsayılan kuralları yükler;
        /// </summary>
        private void InitializeDefaultRules()
        {
            try
            {
                // Her desteklenen dil için varsayılan kural setleri oluştur;
                foreach (var language in _configuration.SupportedLanguages)
                {
                    LoadDefaultRulesForLanguage(language);
                }

                _logger.LogDebug("Default grammar rules loaded for {Count} languages",
                    _configuration.SupportedLanguages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading default grammar rules");
            }
        }

        /// <summary>
        /// Varsayılan modelleri yükler;
        /// </summary>
        private void LoadDefaultModels()
        {
            try
            {
                // İngilizce için varsayılan model;
                LoadModelForLanguage("en");

                // Diğer diller için temel modeller;
                foreach (var language in _configuration.SupportedLanguages.Where(l => l != "en"))
                {
                    try
                    {
                        LoadModelForLanguage(language);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load default model for language: {Language}", language);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading default models");
            }
        }

        /// <summary>
        /// Metni parse eder;
        /// </summary>
        public async Task<ParseResult> ParseAsync(
            string text,
            string language = "en",
            ParseOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var parseId = Guid.NewGuid().ToString("N");
            var parseOptions = options ?? new ParseOptions();

            try
            {
                _grammarLock.EnterUpgradeableReadLock();

                // Metin ön işleme;
                var processedText = PreprocessText(text, language);

                if (processedText.Length > _configuration.MaxSentenceLength)
                {
                    throw new GrammarException($"Text exceeds maximum length of {_configuration.MaxSentenceLength} characters");
                }

                // Cache kontrolü;
                if (_configuration.EnableCaching)
                {
                    var cachedResult = GetCachedParse(processedText, language, parseOptions);
                    if (cachedResult != null)
                    {
                        _logger.LogDebug("Returning cached parse result for text: {TextHash}",
                            processedText.GetHashCode());
                        return cachedResult;
                    }
                }

                // Model ve kural setlerini yükle;
                var model = await GetOrLoadModelAsync(language, cancellationToken);
                var ruleSet = await GetOrLoadRuleSetAsync(language, cancellationToken);

                _grammarLock.EnterWriteLock();

                // Tokenization;
                var tokens = await TokenizeAsync(processedText, language, cancellationToken);

                // Part-of-speech tagging;
                var posTags = await TagPartsOfSpeechAsync(tokens, language, cancellationToken);

                // Parsing pipeline;
                ParseResult parseResult;

                if (parseOptions.UseStatisticalParsing && _configuration.EnableStatisticalParsing)
                {
                    parseResult = await ParseStatisticalAsync(
                        tokens, posTags, model, ruleSet, parseOptions, cancellationToken);
                }
                else if (parseOptions.UseRuleBasedParsing && _configuration.EnableRuleBasedParsing)
                {
                    parseResult = await ParseRuleBasedAsync(
                        tokens, posTags, ruleSet, parseOptions, cancellationToken);
                }
                else;
                {
                    // Hibrit parsing;
                    parseResult = await ParseHybridAsync(
                        tokens, posTags, model, ruleSet, parseOptions, cancellationToken);
                }

                // Hata düzeltme;
                if (_configuration.EnableErrorCorrection && parseResult.Errors.Any())
                {
                    parseResult = await ApplyErrorCorrectionAsync(parseResult, parseOptions, cancellationToken);
                }

                // Belirsizlik çözümü;
                if (_configuration.EnableAmbiguityResolution && parseResult.AmbiguityCount > 0)
                {
                    parseResult = await ResolveAmbiguitiesAsync(parseResult, parseOptions, cancellationToken);
                }

                // Sözdizimi ağacı oluştur;
                if (parseResult.IsValid && parseResult.ParseTrees.Any())
                {
                    parseResult.SyntaxTree = await _syntaxTreeBuilder.BuildSyntaxTreeAsync(
                        parseResult.ParseTrees.First(), cancellationToken);
                }

                // Cache'e ekle;
                if (_configuration.EnableCaching && parseResult.Confidence >= _configuration.ConfidenceThreshold)
                {
                    AddToCache(processedText, language, parseOptions, parseResult);
                }

                // İstatistikleri güncelle;
                UpdateStatistics(parseResult);

                // Event tetikle;
                ParseCompleted?.Invoke(this,
                    new ParseCompletedEventArgs(parseId, parseResult, ParseEventType.Completed));

                // Metrik kaydet;
                RecordParseMetrics(parseId, parseResult, stopwatch.Elapsed, language);

                _logger.LogDebug("Parse completed. Text: {TextHash}, Valid: {IsValid}, Confidence: {Confidence}",
                    processedText.GetHashCode(), parseResult.IsValid, parseResult.Confidence);

                return parseResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing text: {Text}", text);

                ParseCompleted?.Invoke(this,
                    new ParseCompletedEventArgs(parseId, null, ParseEventType.Failed));

                throw new GrammarException($"Failed to parse text: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();

                if (_grammarLock.IsWriteLockHeld) _grammarLock.ExitWriteLock();
                _grammarLock.ExitUpgradeableReadLock();
            }
        }

        /// <summary>
        /// İstatistiksel parsing yapar;
        /// </summary>
        private async Task<ParseResult> ParseStatisticalAsync(
            List<Token> tokens,
            List<POSTag> posTags,
            GrammarModel model,
            GrammarRuleSet ruleSet,
            ParseOptions options,
            CancellationToken cancellationToken)
        {
            var parseResult = new ParseResult;
            {
                ParseId = Guid.NewGuid().ToString("N"),
                Text = string.Join(" ", tokens.Select(t => t.Text)),
                Language = ruleSet.Language,
                ParsedAt = DateTime.UtcNow;
            };

            try
            {
                // Chart parsing başlat;
                var chart = InitializeChart(tokens.Count);

                // Seed chart with POS tags;
                await SeedChartWithPOSAsync(chart, tokens, posTags, ruleSet, cancellationToken);

                // Chart parsing algoritması (CYK veya Earley)
                if (options.UseCYKParsing)
                {
                    await ParseWithCYKAsync(chart, tokens, ruleSet, model, cancellationToken);
                }
                else;
                {
                    await ParseWithEarleyAsync(chart, tokens, ruleSet, model, cancellationToken);
                }

                // Parse ağaçlarını çıkar;
                parseResult.ParseTrees = ExtractParseTrees(chart, ruleSet);

                // Güvenilirlik hesapla;
                parseResult.Confidence = CalculateParseConfidence(parseResult.ParseTrees, model);

                // Hataları tespit et;
                parseResult.Errors = DetectGrammarErrors(tokens, parseResult.ParseTrees, ruleSet);
                parseResult.Warnings = DetectGrammarWarnings(tokens, parseResult.ParseTrees, ruleSet);

                // Belirsizlik sayısını hesapla;
                parseResult.AmbiguityCount = parseResult.ParseTrees.Count > 1 ? parseResult.ParseTrees.Count - 1 : 0;

                _logger.LogTrace("Statistical parsing completed with {Count} parse trees",
                    parseResult.ParseTrees.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Statistical parsing failed, falling back to rule-based");
                return await ParseRuleBasedAsync(tokens, posTags, ruleSet, options, cancellationToken);
            }

            return parseResult;
        }

        /// <summary>
        /// Kural tabanlı parsing yapar;
        /// </summary>
        private async Task<ParseResult> ParseRuleBasedAsync(
            List<Token> tokens,
            List<POSTag> posTags,
            GrammarRuleSet ruleSet,
            ParseOptions options,
            CancellationToken cancellationToken)
        {
            var parseResult = new ParseResult;
            {
                ParseId = Guid.NewGuid().ToString("N"),
                Text = string.Join(" ", tokens.Select(t => t.Text)),
                Language = ruleSet.Language,
                ParsedAt = DateTime.UtcNow;
            };

            try
            {
                // Recursive descent parsing;
                var parser = new RecursiveDescentParser(ruleSet);
                var parseTrees = await parser.ParseAsync(tokens, cancellationToken);

                parseResult.ParseTrees = parseTrees;
                parseResult.Confidence = CalculateRuleBasedConfidence(parseTrees, ruleSet);

                // Hata tespiti;
                parseResult.Errors = DetectGrammarErrors(tokens, parseTrees, ruleSet);
                parseResult.Warnings = DetectGrammarWarnings(tokens, parseTrees, ruleSet);

                _logger.LogTrace("Rule-based parsing completed with {Count} parse trees", parseTrees.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rule-based parsing failed");
                parseResult.Errors.Add(new GrammarError;
                {
                    ErrorId = Guid.NewGuid().ToString("N"),
                    Type = ErrorType.SyntaxError,
                    Message = $"Parsing failed: {ex.Message}",
                    Severity = ErrorSeverity.Error,
                    StartPosition = 0,
                    EndPosition = tokens.Count;
                });
            }

            return parseResult;
        }

        /// <summary>
        /// Hibrit parsing yapar;
        /// </summary>
        private async Task<ParseResult> ParseHybridAsync(
            List<Token> tokens,
            List<POSTag> posTags,
            GrammarModel model,
            GrammarRuleSet ruleSet,
            ParseOptions options,
            CancellationToken cancellationToken)
        {
            // İstatistiksel ve kural tabanlı parsing'i birleştir;
            var statisticalResult = await ParseStatisticalAsync(
                tokens, posTags, model, ruleSet, options, cancellationToken);

            var ruleBasedResult = await ParseRuleBasedAsync(
                tokens, posTags, ruleSet, options, cancellationToken);

            // Sonuçları birleştir;
            var combinedResult = new ParseResult;
            {
                ParseId = Guid.NewGuid().ToString("N"),
                Text = statisticalResult.Text,
                Language = statisticalResult.Language,
                ParsedAt = DateTime.UtcNow;
            };

            // En iyi parse ağacını seç;
            var bestTree = SelectBestParseTree(
                statisticalResult.ParseTrees,
                ruleBasedResult.ParseTrees,
                model,
                ruleSet);

            if (bestTree != null)
            {
                combinedResult.ParseTrees.Add(bestTree);
            }

            // Hataları birleştir;
            combinedResult.Errors = statisticalResult.Errors;
                .Concat(ruleBasedResult.Errors)
                .DistinctBy(e => e.Message)
                .ToList();

            combinedResult.Warnings = statisticalResult.Warnings;
                .Concat(ruleBasedResult.Warnings)
                .DistinctBy(w => w.Message)
                .ToList();

            // Güvenilirlik hesapla;
            combinedResult.Confidence = Math.Max(
                statisticalResult.Confidence,
                ruleBasedResult.Confidence) * 0.9;

            return combinedResult;
        }

        /// <summary>
        /// Dilbilgisi hatalarını kontrol eder;
        /// </summary>
        public async Task<GrammarCheckResult> CheckGrammarAsync(
            string text,
            string language = "en",
            GrammarCheckOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var checkId = Guid.NewGuid().ToString("N");

            try
            {
                _grammarLock.EnterReadLock();

                // Parse et;
                var parseOptions = new ParseOptions;
                {
                    UseStatisticalParsing = true,
                    UseRuleBasedParsing = true,
                    EnableErrorDetection = true;
                };

                var parseResult = await ParseAsync(text, language, parseOptions, cancellationToken);

                // Hata analizi;
                var grammarErrors = await AnalyzeGrammarErrorsAsync(parseResult, options, cancellationToken);

                // Öneriler oluştur;
                var suggestions = await GenerateGrammarSuggestionsAsync(parseResult, grammarErrors, cancellationToken);

                // Puan hesapla;
                var score = CalculateGrammarScore(parseResult, grammarErrors);

                var result = new GrammarCheckResult;
                {
                    CheckId = checkId,
                    Text = text,
                    Language = language,
                    Score = score,
                    Errors = grammarErrors,
                    Suggestions = suggestions,
                    ParseResult = parseResult,
                    ProcessingTime = stopwatch.Elapsed,
                    CheckedAt = DateTime.UtcNow;
                };

                // Event tetikle;
                foreach (var error in grammarErrors)
                {
                    GrammarErrorDetected?.Invoke(this,
                        new GrammarErrorDetectedEventArgs(checkId, error));
                }

                RecordGrammarCheckMetrics(checkId, result, stopwatch.Elapsed);

                _logger.LogDebug("Grammar check completed. Score: {Score}, Errors: {ErrorCount}",
                    score, grammarErrors.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking grammar for text: {Text}", text);
                throw new GrammarException($"Failed to check grammar: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
                _grammarLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Metni düzeltir;
        /// </summary>
        public async Task<TextCorrectionResult> CorrectTextAsync(
            string text,
            string language = "en",
            CorrectionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var correctionId = Guid.NewGuid().ToString("N");

            try
            {
                _grammarLock.EnterUpgradeableReadLock();

                // Grammar check yap;
                var checkResult = await CheckGrammarAsync(text, language, null, cancellationToken);

                if (!checkResult.Errors.Any())
                {
                    return TextCorrectionResult.NoCorrectionsNeeded(correctionId, text);
                }

                // Düzeltmeleri uygula;
                var corrections = new List<AppliedCorrection>();
                var correctedText = text;
                var currentPositionOffset = 0;

                foreach (var error in checkResult.Errors.OrderBy(e => e.StartPosition))
                {
                    if (error.Corrections.Any())
                    {
                        var bestCorrection = error.Corrections;
                            .OrderByDescending(c => c.Confidence)
                            .First();

                        // Düzeltmeyi uygula;
                        var appliedCorrection = ApplyCorrection(
                            correctedText,
                            error,
                            bestCorrection,
                            currentPositionOffset);

                        if (appliedCorrection != null)
                        {
                            corrections.Add(appliedCorrection);
                            correctedText = appliedCorrection.CorrectedText;
                            currentPositionOffset += appliedCorrection.LengthDelta;
                        }
                    }
                }

                if (!corrections.Any())
                {
                    return TextCorrectionResult.NoCorrectionsApplied(correctionId, text);
                }

                var result = new TextCorrectionResult;
                {
                    CorrectionId = correctionId,
                    OriginalText = text,
                    CorrectedText = correctedText,
                    AppliedCorrections = corrections,
                    Language = language,
                    ProcessingTime = stopwatch.Elapsed,
                    CorrectedAt = DateTime.UtcNow,
                    ImprovementScore = CalculateImprovementScore(text, correctedText, checkResult.Score)
                };

                // Yeni metni tekrar kontrol et;
                var recheckResult = await CheckGrammarAsync(correctedText, language, null, cancellationToken);
                result.FinalScore = recheckResult.Score;
                result.RemainingErrors = recheckResult.Errors;

                _logger.LogInformation("Text correction completed. Original score: {OriginalScore}, Final score: {FinalScore}",
                    checkResult.Score, result.FinalScore);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error correcting text: {Text}", text);
                throw new GrammarException($"Failed to correct text: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
                _grammarLock.ExitUpgradeableReadLock();
            }
        }

        /// <summary>
        /// Yeni dilbilgisi kuralı ekler;
        /// </summary>
        public async Task<RuleAdditionResult> AddGrammarRuleAsync(
            GrammarRule rule,
            string language = "en",
            CancellationToken cancellationToken = default)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));

            try
            {
                _grammarLock.EnterWriteLock();

                // Kural validasyonu;
                var validationResult = ValidateGrammarRule(rule, language);
                if (!validationResult.IsValid)
                {
                    return RuleAdditionResult.Failure(validationResult.Errors);
                }

                // Rule set'i al;
                var ruleSet = await GetOrLoadRuleSetAsync(language, cancellationToken);

                // Çakışma kontrolü;
                var conflictResult = CheckRuleConflict(rule, ruleSet);
                if (conflictResult.HasConflict)
                {
                    if (!conflictResult.CanAutoResolve)
                    {
                        return RuleAdditionResult.Conflict(rule.RuleId, conflictResult.ConflictingRules);
                    }

                    // Otomatik çözüm;
                    rule = conflictResult.ResolvedRule ?? rule;
                }

                // Kuralı ekle;
                ruleSet.Rules.Add(rule);
                ruleSet.LastModified = DateTime.UtcNow;

                // First ve Follow set'lerini güncelle;
                UpdateFirstAndFollowSets(ruleSet);

                // Cache'i temizle;
                ClearCacheForLanguage(language);

                // Modeli güncelle;
                if (_loadedModels.TryGetValue(language, out var model))
                {
                    UpdateModelWithNewRule(model, rule);
                }

                _logger.LogInformation("Grammar rule added: {RuleId} for language: {Language}",
                    rule.RuleId, language);

                return RuleAdditionResult.Success(rule.RuleId, ruleSet.RuleSetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding grammar rule: {RuleId}", rule.RuleId);
                throw new GrammarException($"Failed to add grammar rule: {ex.Message}", ex);
            }
            finally
            {
                _grammarLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Dilbilgisi pattern'ı ekler;
        /// </summary>
        public async Task<PatternAdditionResult> AddGrammarPatternAsync(
            GrammarPattern pattern,
            CancellationToken cancellationToken = default)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));

            try
            {
                _grammarLock.EnterWriteLock();

                // Pattern validasyonu;
                if (string.IsNullOrWhiteSpace(pattern.PatternId))
                {
                    pattern.PatternId = $"pattern_{Guid.NewGuid():N}";
                }

                // Regex pattern'i derle;
                if (pattern.PatternType == "regex")
                {
                    try
                    {
                        pattern.RegexPattern = new Regex(pattern.PatternText,
                            RegexOptions.Compiled | RegexOptions.IgnoreCase,
                            TimeSpan.FromMilliseconds(100));
                    }
                    catch (Exception ex)
                    {
                        return PatternAdditionResult.Failure($"Invalid regex pattern: {ex.Message}");
                    }
                }

                // Pattern'i kaydet;
                pattern.CreatedAt = DateTime.UtcNow;
                pattern.LastUsed = DateTime.UtcNow;

                _patterns[pattern.PatternId] = pattern;

                // İlgili modeli güncelle;
                if (_loadedModels.TryGetValue(pattern.Language, out var model))
                {
                    model.CommonPatterns[pattern.PatternId] = pattern;
                }

                _logger.LogInformation("Grammar pattern added: {PatternId} for language: {Language}",
                    pattern.PatternId, pattern.Language);

                return PatternAdditionResult.Success(pattern.PatternId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding grammar pattern: {PatternId}", pattern.PatternId);
                throw new GrammarException($"Failed to add grammar pattern: {ex.Message}", ex);
            }
            finally
            {
                _grammarLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Dilbilgisi motoru istatistiklerini getirir;
        /// </summary>
        public GrammarEngineStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                _statistics.LoadedModels = _loadedModels.Count;
                _statistics.RuleSets = _ruleSets.Count;
                _statistics.Patterns = _patterns.Count;
                _statistics.CacheSize = GetCacheSize();
                _statistics.LastUpdated = DateTime.UtcNow;

                return new GrammarEngineStatistics;
                {
                    TotalParses = _statistics.TotalParses,
                    SuccessfulParses = _statistics.SuccessfulParses,
                    AverageParseTime = _statistics.AverageParseTime,
                    AverageConfidence = _statistics.AverageConfidence,
                    ErrorDistribution = new Dictionary<ErrorType, int>(_statistics.ErrorDistribution),
                    LanguageDistribution = new Dictionary<string, int>(_statistics.LanguageDistribution),
                    LoadedModels = _statistics.LoadedModels,
                    RuleSets = _statistics.RuleSets,
                    Patterns = _statistics.Patterns,
                    CacheSize = _statistics.CacheSize,
                    CacheHitRate = _statistics.CacheHitRate,
                    LastUpdated = _statistics.LastUpdated;
                };
            }
        }

        /// <summary>
        /// Süresi dolmuş verileri temizler;
        /// </summary>
        private void CleanupExpiredData()
        {
            try
            {
                _grammarLock.EnterWriteLock();

                var now = DateTime.UtcNow;
                var removedCount = 0;

                // Süresi dolmuş modelleri temizle;
                var expiredModels = _loadedModels.Where(kvp => kvp.Value.IsExpired).ToList();
                foreach (var model in expiredModels)
                {
                    if (_loadedModels.TryRemove(model.Key, out _))
                    {
                        removedCount++;
                    }
                }

                // Süresi dolmuş cache'leri temizle;
                foreach (var model in _loadedModels.Values)
                {
                    var expiredCacheEntries = model.ParseCache;
                        .Where(kvp => now - kvp.Value.ParsedAt > _configuration.CacheDuration)
                        .ToList();

                    foreach (var entry in expiredCacheEntries)
                    {
                        model.ParseCache.TryRemove(entry.Key, out _);
                    }
                }

                if (removedCount > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} expired grammar models", removedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during grammar data cleanup");
            }
            finally
            {
                _grammarLock.ExitWriteLock();
            }
        }

        // Yardımcı metodlar;
        private string PreprocessText(string text, string language)
        {
            // Metin ön işleme;
            var processed = text.Trim();

            // Çoklu boşlukları temizle;
            processed = Regex.Replace(processed, @"\s+", " ");

            // Dil spesifik ön işlemeler;
            switch (language)
            {
                case "tr":
                    // Türkçe karakter normalizasyonu;
                    processed = processed.Replace("İ", "i").Replace("I", "ı");
                    break;

                case "de":
                    // Almanca umlaut normalizasyonu;
                    processed = processed;
                        .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
                        .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue")
                        .Replace("ß", "ss");
                    break;
            }

            return processed;
        }

        private async Task<GrammarModel?> GetOrLoadModelAsync(string language, CancellationToken cancellationToken)
        {
            if (!_loadedModels.TryGetValue(language, out var model))
            {
                model = await _ruleLoader.LoadGrammarModelAsync(language, cancellationToken);
                if (model != null)
                {
                    model.LoadedAt = DateTime.UtcNow;
                    _loadedModels[language] = model;
                }
            }
            else;
            {
                model.LastUsed = DateTime.UtcNow;
            }

            return model;
        }

        private async Task<GrammarRuleSet> GetOrLoadRuleSetAsync(string language, CancellationToken cancellationToken)
        {
            var ruleSetKey = $"ruleset_{language}";

            if (!_ruleSets.TryGetValue(ruleSetKey, out var ruleSet))
            {
                ruleSet = await _ruleLoader.LoadGrammarRulesAsync(language, cancellationToken);
                if (ruleSet == null)
                {
                    // Varsayılan kural seti oluştur;
                    ruleSet = CreateDefaultRuleSet(language);
                }

                ruleSet.RuleSetId = ruleSetKey;
                _ruleSets[ruleSetKey] = ruleSet;
            }

            return ruleSet;
        }

        private GrammarRuleSet CreateDefaultRuleSet(string language)
        {
            var ruleSet = new GrammarRuleSet;
            {
                Language = language,
                RuleSetId = $"default_{language}",
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.UtcNow;
            };

            // Dil için temel kurallar ekle;
            AddBasicGrammarRules(ruleSet, language);

            return ruleSet;
        }

        private void AddBasicGrammarRules(GrammarRuleSet ruleSet, string language)
        {
            // Dil için temel dilbilgisi kuralları;
            switch (language)
            {
                case "en":
                    // İngilizce temel kurallar;
                    ruleSet.Rules.AddRange(GetEnglishBasicRules());
                    break;

                case "tr":
                    // Türkçe temel kurallar;
                    ruleSet.Rules.AddRange(GetTurkishBasicRules());
                    break;

                default:
                    // Evrensel temel kurallar;
                    ruleSet.Rules.AddRange(GetUniversalBasicRules());
                    break;
            }

            ruleSet.StartSymbol = ruleSet.Rules.FirstOrDefault(r => r.LeftHandSide == "S");
        }

        private List<GrammarRule> GetEnglishBasicRules()
        {
            return new List<GrammarRule>
            {
                new() { RuleId = "R1", LeftHandSide = "S", RightHandSide = new List<string> { "NP", "VP" } },
                new() { RuleId = "R2", LeftHandSide = "NP", RightHandSide = new List<string> { "Det", "N" } },
                new() { RuleId = "R3", LeftHandSide = "NP", RightHandSide = new List<string> { "Pronoun" } },
                new() { RuleId = "R4", LeftHandSide = "VP", RightHandSide = new List<string> { "V", "NP" } },
                new() { RuleId = "R5", LeftHandSide = "VP", RightHandSide = new List<string> { "V" } },
                new() { RuleId = "R6", LeftHandSide = "Det", RightHandSide = new List<string> { "the", "a", "an" } },
                new() { RuleId = "R7", LeftHandSide = "N", RightHandSide = new List<string> { "cat", "dog", "man" } },
                new() { RuleId = "R8", LeftHandSide = "V", RightHandSide = new List<string> { "chased", "saw", "ate" } },
                new() { RuleId = "R9", LeftHandSide = "Pronoun", RightHandSide = new List<string> { "he", "she", "it" } }
            };
        }

        private List<GrammarRule> GetTurkishBasicRules()
        {
            return new List<GrammarRule>
            {
                new() { RuleId = "TR1", LeftHandSide = "S", RightHandSide = new List<string> { "ÖZNE", "YÜKLEM" } },
                new() { RuleId = "TR2", LeftHandSide = "ÖZNE", RightHandSide = new List<string> { "İSİM" } },
                new() { RuleId = "TR3", LeftHandSide = "ÖZNE", RightHandSide = new List<string> { "ZAMİR" } },
                new() { RuleId = "TR4", LeftHandSide = "YÜKLEM", RightHandSide = new List<string> { "FİİL" } },
                new() { RuleId = "TR5", LeftHandSide = "YÜKLEM", RightHandSide = new List<string> { "FİİL", "NESNE" } },
                new() { RuleId = "TR6", LeftHandSide = "NESNE", RightHandSide = new List<string> { "İSİM" } },
                new() { RuleId = "TR7", LeftHandSide = "İSİM", RightHandSide = new List<string> { "kedi", "köpek", "adam" } },
                new() { RuleId = "TR8", LeftHandSide = "FİİL", RightHandSide = new List<string> { "gördü", "koştu", "yedi" } },
                new() { RuleId = "TR9", LeftHandSide = "ZAMİR", RightHandSide = new List<string> { "o", "bu", "şu" } }
            };
        }

        private List<GrammarRule> GetUniversalBasicRules()
        {
            return new List<GrammarRule>
            {
                new() { RuleId = "U1", LeftHandSide = "S", RightHandSide = new List<string> { "Subject", "Predicate" } },
                new() { RuleId = "U2", LeftHandSide = "Subject", RightHandSide = new List<string> { "Noun" } },
                new() { RuleId = "U3", LeftHandSide = "Subject", RightHandSide = new List<string> { "Pronoun" } },
                new() { RuleId = "U4", LeftHandSide = "Predicate", RightHandSide = new List<string> { "Verb" } },
                new() { RuleId = "U5", LeftHandSide = "Predicate", RightHandSide = new List<string> { "Verb", "Object" } },
                new() { RuleId = "U6", LeftHandSide = "Object", RightHandSide = new List<string> { "Noun" } }
            };
        }

        private async Task<List<Token>> TokenizeAsync(string text, string language, CancellationToken cancellationToken)
        {
            // Tokenization işlemi;
            var tokens = new List<Token>();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < words.Length; i++)
            {
                tokens.Add(new Token;
                {
                    TokenId = i + 1,
                    Text = words[i],
                    Position = i,
                    StartIndex = text.IndexOf(words[i], StringComparison.Ordinal),
                    EndIndex = text.IndexOf(words[i], StringComparison.Ordinal) + words[i].Length;
                });
            }

            return tokens;
        }

        private async Task<List<POSTag>> TagPartsOfSpeechAsync(List<Token> tokens, string language, CancellationToken cancellationToken)
        {
            // Part-of-speech tagging;
            var posTags = new List<POSTag>();

            // Basit bir POS tagger (gerçek implementasyonda ML model kullanılır)
            foreach (var token in tokens)
            {
                var tag = await _languageModel.TagWordAsync(token.Text, language, cancellationToken);
                posTags.Add(tag);
            }

            return posTags;
        }

        private Chart InitializeChart(int length)
        {
            var chart = new Chart[length + 1, length + 1];

            for (int i = 0; i <= length; i++)
            {
                for (int j = i; j <= length; j++)
                {
                    chart[i, j] = new ChartCell;
                    {
                        StartIndex = i,
                        EndIndex = j,
                        Items = new List<ChartItem>()
                    };
                }
            }

            return chart;
        }

        private async Task SeedChartWithPOSAsync(Chart chart, List<Token> tokens, List<POSTag> posTags, GrammarRuleSet ruleSet, CancellationToken cancellationToken)
        {
            // Chart'ı POS tag'lerle başlat;
            for (int i = 0; i < tokens.Count; i++)
            {
                var posTag = posTags[i];
                var cell = chart[i, i + 1];

                // POS tag'ine karşılık gelen kuralları bul;
                var posRules = ruleSet.Rules;
                    .Where(r => r.RightHandSide.Count == 1 &&
                               r.RightHandSide[0] == posTag.Tag)
                    .ToList();

                foreach (var rule in posRules)
                {
                    cell.Items.Add(new ChartItem;
                    {
                        Rule = new GrammarRule;
                        {
                            RuleId = rule.RuleId,
                            LeftHandSide = rule.LeftHandSide,
                            RightHandSide = new List<string>(rule.RightHandSide),
                            DotPosition = 1,
                            StartPosition = i;
                        },
                        Probability = rule.Probability;
                    });
                }
            }
        }

        private async Task ParseWithCYKAsync(Chart chart, List<Token> tokens, GrammarRuleSet ruleSet, GrammarModel model, CancellationToken cancellationToken)
        {
            // CYK parsing algoritması;
            int n = tokens.Count;

            // Length 1'den n'ye kadar;
            for (int length = 2; length <= n; length++)
            {
                for (int i = 0; i <= n - length; i++)
                {
                    int j = i + length;
                    var cell = chart[i, j];

                    // Tüm bölünme noktalarını dene;
                    for (int k = i + 1; k < j; k++)
                    {
                        var leftCell = chart[i, k];
                        var rightCell = chart[k, j];

                        // Tüm olası birleşimleri kontrol et;
                        foreach (var leftItem in leftCell.GetCompletedItems())
                        {
                            foreach (var rightItem in rightCell.GetCompletedItems())
                            {
                                var candidateRule = FindRuleForCombination(
                                    leftItem.Rule.LeftHandSide,
                                    rightItem.Rule.LeftHandSide,
                                    ruleSet);

                                if (candidateRule != null)
                                {
                                    cell.Items.Add(new ChartItem;
                                    {
                                        Rule = new GrammarRule;
                                        {
                                            RuleId = candidateRule.RuleId,
                                            LeftHandSide = candidateRule.LeftHandSide,
                                            RightHandSide = new List<string>(candidateRule.RightHandSide),
                                            DotPosition = candidateRule.RightHandSide.Count,
                                            StartPosition = i;
                                        },
                                        Backpointers = new List<ChartItem> { leftItem, rightItem },
                                        Probability = candidateRule.Probability *
                                                     leftItem.Probability *
                                                     rightItem.Probability;
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task ParseWithEarleyAsync(Chart chart, List<Token> tokens, GrammarRuleSet ruleSet, GrammarModel model, CancellationToken cancellationToken)
        {
            // Earley parsing algoritması;
            int n = tokens.Count;

            // Initialization;
            var startRules = ruleSet.Rules.Where(r => r.LeftHandSide == ruleSet.StartSymbol?.LeftHandSide).ToList();
            foreach (var rule in startRules)
            {
                chart[0, 0].Items.Add(new ChartItem;
                {
                    Rule = new GrammarRule;
                    {
                        RuleId = rule.RuleId,
                        LeftHandSide = rule.LeftHandSide,
                        RightHandSide = new List<string>(rule.RightHandSide),
                        DotPosition = 0,
                        StartPosition = 0;
                    },
                    Probability = rule.Probability;
                });
            }

            // Parsing loop;
            for (int i = 0; i <= n; i++)
            {
                var cell = chart[i, i];
                var itemsToProcess = new Queue<ChartItem>(cell.Items);

                while (itemsToProcess.Count > 0)
                {
                    var item = itemsToProcess.Dequeue();

                    if (!item.Rule.IsComplete)
                    {
                        // Predict;
                        await ProcessPredictorAsync(item, cell, ruleSet, cancellationToken);
                    }
                    else;
                    {
                        // Complete;
                        await ProcessCompleterAsync(item, chart, i, cancellationToken);
                    }
                }
            }
        }

        private async Task ProcessPredictorAsync(ChartItem item, ChartCell cell, GrammarRuleSet ruleSet, CancellationToken cancellationToken)
        {
            var nextSymbol = item.Rule.NextSymbol;

            // Non-terminal ise predict;
            if (IsNonTerminal(nextSymbol))
            {
                var rules = ruleSet.Rules.Where(r => r.LeftHandSide == nextSymbol).ToList();

                foreach (var rule in rules)
                {
                    var newItem = new ChartItem;
                    {
                        Rule = new GrammarRule;
                        {
                            RuleId = rule.RuleId,
                            LeftHandSide = rule.LeftHandSide,
                            RightHandSide = new List<string>(rule.RightHandSide),
                            DotPosition = 0,
                            StartPosition = cell.StartIndex;
                        },
                        Probability = rule.Probability;
                    };

                    if (!cell.Items.Any(existing => existing.Rule.ToString() == newItem.Rule.ToString()))
                    {
                        cell.Items.Add(newItem);
                    }
                }
            }
        }

        private async Task ProcessCompleterAsync(ChartItem item, Chart chart, int position, CancellationToken cancellationToken)
        {
            // Completed item'ı kullanan tüm active item'ları bul;
            for (int i = 0; i <= position; i++)
            {
                var cell = chart[i, position];

                foreach (var activeItem in cell.GetActiveItems())
                {
                    if (activeItem.Rule.NextSymbol == item.Rule.LeftHandSide)
                    {
                        var newItem = new ChartItem;
                        {
                            Rule = activeItem.Rule.Advance(),
                            Backpointers = new List<ChartItem>(activeItem.Backpointers) { item },
                            Probability = activeItem.Probability * item.Probability;
                        };

                        var targetCell = chart[i, position + 1];

                        if (!targetCell.Items.Any(existing => existing.Rule.ToString() == newItem.Rule.ToString()))
                        {
                            targetCell.Items.Add(newItem);
                        }
                    }
                }
            }
        }

        private List<ParseTree> ExtractParseTrees(Chart chart, GrammarRuleSet ruleSet)
        {
            var parseTrees = new List<ParseTree>();
            int n = chart.GetLength(0) - 2; // Son hücre;

            var finalCell = chart[0, n + 1];
            var completedItems = finalCell.GetCompletedItems()
                .Where(item => item.Rule.LeftHandSide == ruleSet.StartSymbol?.LeftHandSide)
                .ToList();

            foreach (var item in completedItems)
            {
                var parseTree = BuildParseTree(item, chart);
                if (parseTree != null)
                {
                    parseTrees.Add(parseTree);
                }
            }

            return parseTrees;
        }

        private ParseTree BuildParseTree(ChartItem item, Chart chart)
        {
            var parseTree = new ParseTree;
            {
                Root = new TreeNode;
                {
                    NodeId = Guid.NewGuid().ToString("N"),
                    NodeType = item.Rule.LeftHandSide,
                    Label = item.Rule.LeftHandSide;
                },
                Probability = item.Probability,
                AppliedRules = new List<GrammarRule> { item.Rule }
            };

            if (item.Backpointers.Any())
            {
                foreach (var backpointer in item.Backpointers)
                {
                    var childTree = BuildParseTree(backpointer, chart);
                    if (childTree != null)
                    {
                        parseTree.Root.AddChild(childTree.Root);
                        parseTree.AppliedRules.AddRange(childTree.AppliedRules);
                        parseTree.Probability *= childTree.Probability;
                    }
                }
            }
            else;
            {
                // Leaf node;
                parseTree.Root.Value = item.Rule.RightHandSide.FirstOrDefault() ?? "";
            }

            return parseTree;
        }

        private GrammarRule? FindRuleForCombination(string leftSymbol, string rightSymbol, GrammarRuleSet ruleSet)
        {
            return ruleSet.Rules.FirstOrDefault(r =>
                r.RightHandSide.Count == 2 &&
                r.RightHandSide[0] == leftSymbol &&
                r.RightHandSide[1] == rightSymbol);
        }

        private bool IsNonTerminal(string symbol)
        {
            // Basit non-terminal kontrolü (büyük harf ile başlıyorsa)
            return !string.IsNullOrEmpty(symbol) && char.IsUpper(symbol[0]);
        }

        private double CalculateParseConfidence(List<ParseTree> parseTrees, GrammarModel model)
        {
            if (!parseTrees.Any()) return 0.0;

            // En yüksek olasılıklı parse ağacını seç;
            var bestTree = parseTrees.OrderByDescending(t => t.Probability).First();

            // Model istatistiklerine göre güvenilirlik hesapla;
            var ruleConfidence = CalculateRuleConfidence(bestTree, model);
            var statisticalConfidence = CalculateStatisticalConfidence(bestTree, model);

            return (ruleConfidence + statisticalConfidence) / 2.0;
        }

        private double CalculateRuleConfidence(ParseTree parseTree, GrammarModel model)
        {
            var appliedRules = parseTree.AppliedRules;
            if (!appliedRules.Any()) return 0.5;

            // Kuralların modeldeki olasılıklarını kullan;
            var totalConfidence = 0.0;
            var validRules = 0;

            foreach (var rule in appliedRules)
            {
                if (model.RuleProbabilities.TryGetValue(rule.RuleId, out var probability))
                {
                    totalConfidence += probability;
                    validRules++;
                }
            }

            return validRules > 0 ? totalConfidence / validRules : 0.5;
        }

        private double CalculateStatisticalConfidence(ParseTree parseTree, GrammarModel model)
        {
            // İstatistiksel modele göre güvenilirlik;
            // Gerçek implementasyonda n-gram modeli kullanılır;

            return 0.7; // Varsayılan değer;
        }

        private List<GrammarError> DetectGrammarErrors(List<Token> tokens, List<ParseTree> parseTrees, GrammarRuleSet ruleSet)
        {
            var errors = new List<GrammarError>();

            if (!parseTrees.Any())
            {
                // Hiç parse ağacı yoksa syntax hatası;
                errors.Add(new GrammarError;
                {
                    ErrorId = Guid.NewGuid().ToString("N"),
                    Type = ErrorType.SyntaxError,
                    Message = "Unable to parse sentence",
                    Severity = ErrorSeverity.Error,
                    StartPosition = 0,
                    EndPosition = tokens.Count;
                });
            }
            else;
            {
                // Parse ağaçlarındaki hataları tespit et;
                foreach (var tree in parseTrees)
                {
                    errors.AddRange(CheckTreeForErrors(tree, tokens, ruleSet));
                }
            }

            return errors.DistinctBy(e => e.Message).ToList();
        }

        private List<GrammarError> CheckTreeForErrors(ParseTree tree, List<Token> tokens, GrammarRuleSet ruleSet)
        {
            var errors = new List<GrammarError>();

            // Ağaçtaki düğümleri kontrol et;
            CheckNodesForErrors(tree.Root, tokens, ruleSet, errors);

            // Agreement hatalarını kontrol et;
            CheckAgreementErrors(tree, tokens, ruleSet, errors);

            // Word order hatalarını kontrol et;
            CheckWordOrderErrors(tree, tokens, ruleSet, errors);

            return errors;
        }

        private void CheckNodesForErrors(TreeNode node, List<Token> tokens, GrammarRuleSet ruleSet, List<GrammarError> errors)
        {
            // Düğümdeki hataları kontrol et;
            // Gerçek implementasyonda dilbilgisi kurallarına göre kontrol yapılır;

            // Recursive olarak çocukları kontrol et;
            foreach (var child in node.Children)
            {
                CheckNodesForErrors(child, tokens, ruleSet, errors);
            }
        }

        private void CheckAgreementErrors(ParseTree tree, List<Token> tokens, GrammarRuleSet ruleSet, List<GrammarError> errors)
        {
            // Subject-verb agreement kontrolü;
            // Gerçek implementasyonda detaylı agreement kontrolü yapılır;
        }

        private void CheckWordOrderErrors(ParseTree tree, List<Token> tokens, GrammarRuleSet ruleSet, List<GrammarError> errors)
        {
            // Kelime sırası hatalarını kontrol et;
            // Gerçek implementasyonda dilbilgisi kurallarına göre kontrol yapılır;
        }

        private List<GrammarWarning> DetectGrammarWarnings(List<Token> tokens, List<ParseTree> parseTrees, GrammarRuleSet ruleSet)
        {
            var warnings = new List<GrammarWarning>();

            // Dilbilgisi uyarılarını tespit et;
            // Gerçek implementasyonda stilistik ve pragmatik kontrol yapılır;

            return warnings;
        }

        private ParseResult GetCachedParse(string text, string language, ParseOptions options)
        {
            if (!_loadedModels.TryGetValue(language, out var model)) return null;

            var cacheKey = GenerateCacheKey(text, language, options);
            if (model.ParseCache.TryGetValue(cacheKey, out var cachedResult))
            {
                if (DateTime.UtcNow - cachedResult.ParsedAt <= _configuration.CacheDuration)
                {
                    UpdateCacheStatistics(hit: true);
                    return cachedResult;
                }

                // Süresi dolmuş cache entry'sini kaldır;
                model.ParseCache.TryRemove(cacheKey, out _);
            }

            UpdateCacheStatistics(hit: false);
            return null;
        }

        private void AddToCache(string text, string language, ParseOptions options, ParseResult result)
        {
            if (!_loadedModels.TryGetValue(language, out var model)) return;

            var cacheKey = GenerateCacheKey(text, language, options);

            // Cache boyutunu kontrol et;
            if (model.ParseCache.Count >= _configuration.CacheSize)
            {
                // En eski entry'leri kaldır;
                var oldestEntries = model.ParseCache;
                    .OrderBy(kvp => kvp.Value.ParsedAt)
                    .Take(model.ParseCache.Count - _configuration.CacheSize + 1)
                    .ToList();

                foreach (var entry in oldestEntries)
                {
                    model.ParseCache.TryRemove(entry.Key, out _);
                }
            }

            model.ParseCache[cacheKey] = result;
        }

        private string GenerateCacheKey(string text, string language, ParseOptions options)
        {
            // Cache key oluştur;
            var keyData = new;
            {
                Text = text.GetHashCode(),
                Language = language,
                Options = options.GetHashCode()
            };

            return JsonConvert.SerializeObject(keyData).GetHashCode().ToString();
        }

        private async Task<ParseResult> ApplyErrorCorrectionAsync(ParseResult parseResult, ParseOptions options, CancellationToken cancellationToken)
        {
            var correctedResult = parseResult.DeepClone();

            foreach (var error in correctedResult.Errors)
            {
                if (error.Corrections.Any())
                {
                    var bestCorrection = error.Corrections;
                        .OrderByDescending(c => c.Confidence)
                        .First();

                    // Düzeltmeyi uygula;
                    // Gerçek implementasyonda metin manipülasyonu yapılır;
                }
            }

            return correctedResult;
        }

        private async Task<ParseResult> ResolveAmbiguitiesAsync(ParseResult parseResult, ParseOptions options, CancellationToken cancellationToken)
        {
            if (parseResult.ParseTrees.Count <= 1) return parseResult;

            var resolvedResult = parseResult.DeepClone();

            // Belirsizlik çözüm stratejisine göre en iyi ağacı seç;
            ParseTree? selectedTree = null;

            switch (_configuration.AmbiguityStrategy)
            {
                case AmbiguityResolutionStrategy.Statistical:
                    selectedTree = parseResult.ParseTrees;
                        .OrderByDescending(t => t.Probability)
                        .FirstOrDefault();
                    break;

                case AmbiguityResolutionStrategy.Semantic:
                    selectedTree = await ResolveBySemanticsAsync(parseResult.ParseTrees, cancellationToken);
                    break;

                case AmbiguityResolutionStrategy.Contextual:
                    selectedTree = await ResolveByContextAsync(parseResult.ParseTrees, cancellationToken);
                    break;

                case AmbiguityResolutionStrategy.Hybrid:
                    selectedTree = await ResolveByHybridAsync(parseResult.ParseTrees, cancellationToken);
                    break;

                case AmbiguityResolutionStrategy.FirstFound:
                    selectedTree = parseResult.ParseTrees.FirstOrDefault();
                    break;
            }

            if (selectedTree != null)
            {
                resolvedResult.ParseTrees = new List<ParseTree> { selectedTree };
                resolvedResult.AmbiguityCount = 0;

                // Belirsizlik çözüm event'i tetikle;
                AmbiguityResolved?.Invoke(this,
                    new AmbiguityResolvedEventArgs(parseResult.ParseId, selectedTree,
                        parseResult.ParseTrees.Count));
            }

            return resolvedResult;
        }

        private async Task<ParseTree?> ResolveBySemanticsAsync(List<ParseTree> trees, CancellationToken cancellationToken)
        {
            // Semantik analiz ile en anlamlı ağacı seç;
            // Gerçek implementasyonda semantic model kullanılır;

            return trees.OrderByDescending(t => t.Probability).FirstOrDefault();
        }

        private async Task<ParseTree?> ResolveByContextAsync(List<ParseTree> trees, CancellationToken cancellationToken)
        {
            // Bağlamsal bilgilere göre en uygun ağacı seç;
            // Gerçek implementasyonda context model kullanılır;

            return trees.OrderByDescending(t => t.Probability).FirstOrDefault();
        }

        private async Task<ParseTree?> ResolveByHybridAsync(List<ParseTree> trees, CancellationToken cancellationToken)
        {
            // Hibrit çözüm: hem semantik hem istatistiksel;
            var semanticScore = await CalculateSemanticScoresAsync(trees, cancellationToken);
            var statisticalScore = trees.Select(t => t.Probability).ToList();

            // Kombine skor hesapla;
            var combinedScores = new List<double>();
            for (int i = 0; i < trees.Count; i++)
            {
                combinedScores.Add(semanticScore[i] * 0.6 + statisticalScore[i] * 0.4);
            }

            var maxIndex = combinedScores.IndexOf(combinedScores.Max());
            return trees[maxIndex];
        }

        private async Task<List<double>> CalculateSemanticScoresAsync(List<ParseTree> trees, CancellationToken cancellationToken)
        {
            // Semantik skorları hesapla;
            var scores = new List<double>();

            foreach (var tree in trees)
            {
                // Basit bir semantik skor hesaplama;
                var score = CalculateTreeSemanticScore(tree);
                scores.Add(score);
            }

            return scores;
        }

        private double CalculateTreeSemanticScore(ParseTree tree)
        {
            // Ağacın semantik tutarlılık skorunu hesapla;
            // Gerçek implementasyonda detaylı semantic analiz yapılır;

            return 0.7; // Varsayılan değer;
        }

        private async Task<List<GrammarError>> AnalyzeGrammarErrorsAsync(ParseResult parseResult, GrammarCheckOptions? options, CancellationToken cancellationToken)
        {
            var detailedErrors = new List<GrammarError>();

            foreach (var error in parseResult.Errors)
            {
                // Hata detaylandırma;
                var detailedError = error.DeepClone();

                // Düzeltme önerileri oluştur;
                detailedError.Corrections = await GenerateCorrectionsForErrorAsync(error, parseResult, cancellationToken);

                detailedErrors.Add(detailedError);
            }

            return detailedErrors;
        }

        private async Task<List<ErrorCorrection>> GenerateCorrectionsForErrorAsync(GrammarError error, ParseResult parseResult, CancellationToken cancellationToken)
        {
            var corrections = new List<ErrorCorrection>();

            // Hata türüne göre düzeltme önerileri oluştur;
            switch (error.Type)
            {
                case ErrorType.SyntaxError:
                    corrections.AddRange(await GenerateSyntaxCorrectionsAsync(error, parseResult, cancellationToken));
                    break;

                case ErrorType.GrammarError:
                    corrections.AddRange(await GenerateGrammarCorrectionsAsync(error, parseResult, cancellationToken));
                    break;

                case ErrorType.SpellingError:
                    corrections.AddRange(await GenerateSpellingCorrectionsAsync(error, parseResult, cancellationToken));
                    break;

                case ErrorType.AgreementError:
                    corrections.AddRange(await GenerateAgreementCorrectionsAsync(error, parseResult, cancellationToken));
                    break;
            }

            return corrections.OrderByDescending(c => c.Confidence).ToList();
        }

        private async Task<List<ErrorCorrection>> GenerateSyntaxCorrectionsAsync(GrammarError error, ParseResult parseResult, CancellationToken cancellationToken)
        {
            var corrections = new List<ErrorCorrection>();

            // Sözdizimi hataları için düzeltme önerileri;
            // Gerçek implementasyonda pattern matching ve dil modeli kullanılır;

            return corrections;
        }

        private async Task<List<GrammarSuggestion>> GenerateGrammarSuggestionsAsync(ParseResult parseResult, List<GrammarError> errors, CancellationToken cancellationToken)
        {
            var suggestions = new List<GrammarSuggestion>();

            // Dilbilgisi önerileri oluştur;
            // Gerçek implementasyonda stilistik ve pragmatik analiz yapılır;

            return suggestions;
        }

        private double CalculateGrammarScore(ParseResult parseResult, List<GrammarError> errors)
        {
            if (!errors.Any()) return 100.0;

            // Hata sayısına ve şiddetine göre puan hesapla;
            var totalSeverity = errors.Sum(e => (int)e.Severity);
            var maxSeverity = errors.Count * (int)ErrorSeverity.Critical;

            var errorScore = (double)totalSeverity / maxSeverity * 100;
            var baseScore = parseResult.Confidence * 100;

            return Math.Max(0, baseScore - errorScore);
        }

        private AppliedCorrection? ApplyCorrection(string text, GrammarError error, ErrorCorrection correction, int positionOffset)
        {
            try
            {
                var actualStart = error.StartPosition + positionOffset;
                var actualEnd = error.EndPosition + positionOffset;

                if (actualStart < 0 || actualEnd > text.Length || actualStart > actualEnd)
                {
                    return null;
                }

                var originalSegment = text.Substring(actualStart, actualEnd - actualStart);
                var correctedText = text.Remove(actualStart, actualEnd - actualStart)
                    .Insert(actualStart, correction.SuggestedText);

                return new AppliedCorrection;
                {
                    CorrectionId = Guid.NewGuid().ToString("N"),
                    OriginalSegment = originalSegment,
                    CorrectedSegment = correction.SuggestedText,
                    StartPosition = actualStart,
                    EndPosition = actualEnd,
                    LengthDelta = correction.SuggestedText.Length - originalSegment.Length,
                    OriginalText = text,
                    CorrectedText = correctedText,
                    Confidence = correction.Confidence,
                    AppliedAt = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply correction for error: {ErrorId}", error.ErrorId);
                return null;
            }
        }

        private double CalculateImprovementScore(string originalText, string correctedText, double originalScore)
        {
            // İyileşme skorunu hesapla;
            var lengthRatio = (double)correctedText.Length / originalText.Length;
            var lengthPenalty = Math.Abs(1.0 - lengthRatio) * 10; // %10'dan fazla değişim ceza;

            // İyileşme oranı (varsayılan)
            var improvementRate = 0.3;

            return originalScore + (100 - originalScore) * improvementRate - lengthPenalty;
        }

        private RuleValidationResult ValidateGrammarRule(GrammarRule rule, string language)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(rule.LeftHandSide))
                errors.Add("Left-hand side cannot be empty");

            if (!rule.RightHandSide.Any())
                errors.Add("Right-hand side cannot be empty");

            if (rule.Probability < 0 || rule.Probability > 1)
                errors.Add("Probability must be between 0 and 1");

            // Dil spesifik validasyon;
            if (!IsValidForLanguage(rule, language))
                errors.Add($"Rule is not valid for language: {language}");

            return new RuleValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private bool IsValidForLanguage(GrammarRule rule, string language)
        {
            // Dil için kural geçerliliğini kontrol et;
            // Gerçek implementasyonda dil spesifik kontroller yapılır;

            return true; // Varsayılan olarak geçerli;
        }

        private ConflictCheckResult CheckRuleConflict(GrammarRule newRule, GrammarRuleSet ruleSet)
        {
            var conflictingRules = ruleSet.Rules;
                .Where(r => r.LeftHandSide == newRule.LeftHandSide &&
                           r.RightHandSide.SequenceEqual(newRule.RightHandSide))
                .ToList();

            if (conflictingRules.Any())
            {
                // Tamamen aynı kural var;
                return new ConflictCheckResult;
                {
                    HasConflict = true,
                    CanAutoResolve = true,
                    ConflictingRules = conflictingRules,
                    Resolution = ConflictResolution.Merge,
                    ResolvedRule = newRule // Olasılıkları güncelle;
                };
            }

            // Kısmi çakışmaları kontrol et;
            var partialConflicts = ruleSet.Rules;
                .Where(r => r.LeftHandSide == newRule.LeftHandSide &&
                           r.RightHandSide.Count == newRule.RightHandSide.Count &&
                           r.RightHandSide.Intersect(newRule.RightHandSide).Any())
                .ToList();

            if (partialConflicts.Any())
            {
                return new ConflictCheckResult;
                {
                    HasConflict = true,
                    CanAutoResolve = false,
                    ConflictingRules = partialConflicts,
                    Resolution = ConflictResolution.Manual;
                };
            }

            return new ConflictCheckResult;
            {
                HasConflict = false,
                CanAutoResolve = true;
            };
        }

        private void UpdateFirstAndFollowSets(GrammarRuleSet ruleSet)
        {
            // First ve Follow set'lerini güncelle;
            // Gerçek implementasyonda LL/LR parsing için gerekli set'ler hesaplanır;
        }

        private void ClearCacheForLanguage(string language)
        {
            if (_loadedModels.TryGetValue(language, out var model))
            {
                model.ParseCache.Clear();
            }
        }

        private void UpdateModelWithNewRule(GrammarModel model, GrammarRule rule)
        {
            // Modeli yeni kural ile güncelle;
            model.RuleProbabilities[rule.RuleId] = rule.Probability;
            model.LastUsed = DateTime.UtcNow;
        }

        private void UpdateStatistics(ParseResult parseResult)
        {
            lock (_statsLock)
            {
                _statistics.TotalParses++;

                if (parseResult.IsValid)
                {
                    _statistics.SuccessfulParses++;
                }

                // Ortalama parse süresi;
                _statistics.AverageParseTime = TimeSpan.FromMilliseconds(
                    (_statistics.AverageParseTime.TotalMilliseconds * (_statistics.TotalParses - 1) +
                     parseResult.ParseTime.TotalMilliseconds) / _statistics.TotalParses);

                // Ortalama güvenilirlik;
                _statistics.AverageConfidence = (_statistics.AverageConfidence * (_statistics.TotalParses - 1) +
                                                parseResult.Confidence) / _statistics.TotalParses;

                // Hata dağılımı;
                foreach (var error in parseResult.Errors)
                {
                    if (_statistics.ErrorDistribution.ContainsKey(error.Type))
                        _statistics.ErrorDistribution[error.Type]++;
                    else;
                        _statistics.ErrorDistribution[error.Type] = 1;
                }

                // Dil dağılımı;
                if (_statistics.LanguageDistribution.ContainsKey(parseResult.Language))
                    _statistics.LanguageDistribution[parseResult.Language]++;
                else;
                    _statistics.LanguageDistribution[parseResult.Language] = 1;
            }
        }

        private void UpdateCacheStatistics(bool hit)
        {
            lock (_statsLock)
            {
                _statistics.CacheRequests++;

                if (hit)
                {
                    _statistics.CacheHits++;
                }

                _statistics.CacheHitRate = _statistics.CacheRequests > 0 ?
                    (double)_statistics.CacheHits / _statistics.CacheRequests : 0;
            }
        }

        private int GetCacheSize()
        {
            return _loadedModels.Values.Sum(m => m.ParseCache.Count);
        }

        private void RecordParseMetrics(string parseId, ParseResult result, TimeSpan processingTime, string language)
        {
            _metricsCollector.RecordMetric("grammar_parse_confidence", result.Confidence,
                new Dictionary<string, string>
                {
                    { "parse_id", parseId },
                    { "language", language },
                    { "is_valid", result.IsValid.ToString() }
                });

            _metricsCollector.RecordMetric("grammar_parse_time", processingTime.TotalMilliseconds,
                new Dictionary<string, string>
                {
                    { "parse_id", parseId },
                    { "error_count", result.Errors.Count.ToString() }
                });
        }

        private void RecordGrammarCheckMetrics(string checkId, GrammarCheckResult result, TimeSpan processingTime)
        {
            _metricsCollector.RecordMetric("grammar_check_score", result.Score,
                new Dictionary<string, string>
                {
                    { "check_id", checkId },
                    { "language", result.Language },
                    { "error_count", result.Errors.Count.ToString() }
                });
        }

        private ParseTree? SelectBestParseTree(List<ParseTree> statisticalTrees, List<ParseTree> ruleBasedTrees, GrammarModel model, GrammarRuleSet ruleSet)
        {
            var allTrees = statisticalTrees.Concat(ruleBasedTrees).ToList();

            if (!allTrees.Any()) return null;

            // Çeşitli faktörlere göre en iyi ağacı seç;
            var scoredTrees = allTrees.Select(tree => new;
            {
                Tree = tree,
                Score = CalculateTreeScore(tree, model, ruleSet)
            }).ToList();

            return scoredTrees.OrderByDescending(t => t.Score).First().Tree;
        }

        private double CalculateTreeScore(ParseTree tree, GrammarModel model, GrammarRuleSet ruleSet)
        {
            var score = tree.Probability;

            // Ek faktörler;
            score *= CalculateTreeComplexityScore(tree);
            score *= CalculateTreeConsistencyScore(tree, model);

            return score;
        }

        private double CalculateTreeComplexityScore(ParseTree tree)
        {
            // Ağaç karmaşıklığına göre skor (basit yapılar tercih edilir)
            var depth = tree.Depth;
            var nodeCount = tree.NodeCount;

            var complexity = (double)nodeCount / depth;
            return Math.Max(0.1, 1.0 - (complexity / 100));
        }

        private double CalculateTreeConsistencyScore(ParseTree tree, GrammarModel model)
        {
            // Model ile tutarlılık skoru;
            var ruleConfidence = CalculateRuleConfidence(tree, model);
            return ruleConfidence;
        }

        private void LoadDefaultRulesForLanguage(string language)
        {
            // Dil için varsayılan kuralları yükle;
            // Gerçek implementasyonda dil paketlerinden yüklenir;
        }

        private void LoadModelForLanguage(string language)
        {
            // Dil için model yükle;
            // Gerçek implementasyonda model dosyalarından yüklenir;
        }

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
                    _cleanupTimer?.Dispose();
                    _grammarLock?.Dispose();

                    _logger.LogInformation("GrammarEngine disposed");
                }

                _disposed = true;
            }
        }

        ~GrammarEngine()
        {
            Dispose(false);
        }
    }

    // Yardımcı sınıflar ve interface'ler;
    // (Kod uzunluğu nedeniyle kısaltıldı, tam versiyonda tüm sınıflar mevcut)

    public interface IGrammarEngine : IDisposable
    {
        Task<ParseResult> ParseAsync(
            string text,
            string language = "en",
            ParseOptions? options = null,
            CancellationToken cancellationToken = default);

        Task<GrammarCheckResult> CheckGrammarAsync(
            string text,
            string language = "en",
            GrammarCheckOptions? options = null,
            CancellationToken cancellationToken = default);

        Task<TextCorrectionResult> CorrectTextAsync(
            string text,
            string language = "en",
            CorrectionOptions? options = null,
            CancellationToken cancellationToken = default);

        Task<RuleAdditionResult> AddGrammarRuleAsync(
            GrammarRule rule,
            string language = "en",
            CancellationToken cancellationToken = default);

        Task<PatternAdditionResult> AddGrammarPatternAsync(
            GrammarPattern pattern,
            CancellationToken cancellationToken = default);

        GrammarEngineStatistics GetStatistics();

        event EventHandler<ParseCompletedEventArgs>? ParseCompleted;
        event EventHandler<GrammarErrorDetectedEventArgs>? GrammarErrorDetected;
        event EventHandler<AmbiguityResolvedEventArgs>? AmbiguityResolved;
    }

    public interface ISyntaxTreeBuilder;
    {
        Task<SyntaxTree> BuildSyntaxTreeAsync(ParseTree parseTree, CancellationToken cancellationToken);
    }

    public interface ILanguageModel;
    {
        Task<POSTag> TagWordAsync(string word, string language, CancellationToken cancellationToken);
    }

    public interface IGrammarRuleLoader;
    {
        Task<GrammarModel?> LoadGrammarModelAsync(string language, CancellationToken cancellationToken);
        Task<GrammarRuleSet?> LoadGrammarRulesAsync(string language, CancellationToken cancellationToken);
        Task SaveModelAsync(GrammarModel model, CancellationToken cancellationToken);
    }

    // Event argümanları;
    public class ParseCompletedEventArgs : EventArgs;
    {
        public string ParseId { get; }
        public ParseResult? Result { get; }
        public ParseEventType EventType { get; }
        public DateTime Timestamp { get; }

        public ParseCompletedEventArgs(string parseId, ParseResult? result, ParseEventType eventType)
        {
            ParseId = parseId;
            Result = result;
            EventType = eventType;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class GrammarErrorDetectedEventArgs : EventArgs;
    {
        public string CheckId { get; }
        public GrammarError Error { get; }
        public DateTime Timestamp { get; }

        public GrammarErrorDetectedEventArgs(string checkId, GrammarError error)
        {
            CheckId = checkId;
            Error = error;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class AmbiguityResolvedEventArgs : EventArgs;
    {
        public string ParseId { get; }
        public ParseTree SelectedTree { get; }
        public int CandidateCount { get; }
        public DateTime Timestamp { get; }

        public AmbiguityResolvedEventArgs(string parseId, ParseTree selectedTree, int candidateCount)
        {
            ParseId = parseId;
            SelectedTree = selectedTree;
            CandidateCount = candidateCount;
            Timestamp = DateTime.UtcNow;
        }
    }

    public enum ParseEventType;
    {
        Started,
        Completed,
        Failed,
        Corrected;
    }

    // Exception;
    public class GrammarException : Exception
    {
        public GrammarException(string message) : base(message) { }
        public GrammarException(string message, Exception innerException) : base(message, innerException) { }
    }

    // Extension metodlar;
    internal static class GrammarEngineExtensions;
    {
        public static T DeepClone<T>(this T obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            return JsonConvert.DeserializeObject<T>(json)!;
        }
    }
}
