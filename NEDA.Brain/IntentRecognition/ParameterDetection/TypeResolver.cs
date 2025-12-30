using Microsoft.Extensions.Logging;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Monitoring.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NEDA.Brain.IntentRecognition.ParameterDetection;
{
    /// <summary>
    /// Doğal dil işleme sırasında çıkarılan parametrelerin türlerini belirler,
    /// tip dönüşümlerini yönetir ve veri validasyonu yapar.
    /// </summary>
    public interface ITypeResolver : IDisposable
    {
        /// <summary>
        /// Ham metin değerinden tür çıkarımı yapar;
        /// </summary>
        Task<TypeResolutionResult> ResolveTypeAsync(string rawValue, string parameterName = null,
            Dictionary<string, object> context = null);

        /// <summary>
        /// Birden fazla değer için toplu tür çözümlemesi yapar;
        /// </summary>
        Task<List<TypeResolutionResult>> ResolveTypesAsync(List<string> rawValues,
            Dictionary<string, object> context = null);

        /// <summary>
        /// Belirli bir türe dönüşüm yapar;
        /// </summary>
        Task<ConversionResult> ConvertToTypeAsync(object value, Type targetType,
            ConversionOptions options = null);

        /// <summary>
        /// Değerin belirli bir türe uygun olup olmadığını kontrol eder;
        /// </summary>
        Task<ValidationResult> ValidateTypeAsync(object value, Type expectedType,
            ValidationOptions options = null);

        /// <summary>
        /// Tür çözümleme için kullanılan pattern'leri günceller;
        /// </summary>
        Task UpdatePatternsAsync(Dictionary<string, TypePattern> newPatterns);

        /// <summary>
        /// Özel tür çözümleyici kaydeder;
        /// </summary>
        Task RegisterCustomResolver(string typeName, ICustomTypeResolver resolver);

        /// <summary>
        /// Tür çözümleme istatistiklerini getirir;
        /// </summary>
        Task<TypeResolutionStatistics> GetStatisticsAsync();

        /// <summary>
        /// Bilinmeyen türleri öğrenir ve pattern'leri günceller;
        /// </summary>
        Task LearnFromUnknownTypesAsync(List<UnknownTypeSample> samples);

        /// <summary>
        /// Tür hiyerarşisini ve ilişkilerini analiz eder;
        /// </summary>
        Task<TypeHierarchyAnalysis> AnalyzeTypeHierarchyAsync(string baseType);

        /// <summary>
        /// Context-aware tür çözümlemesi yapar;
        /// </summary>
        Task<TypeResolutionResult> ResolveWithContextAsync(string rawValue,
            ResolutionContext resolutionContext);

        /// <summary>
        /// Tür çakışmalarını çözer;
        /// </summary>
        Task<TypeConflictResolution> ResolveTypeConflictAsync(List<TypeResolutionResult> candidates);

        /// <summary>
        /// Fuzzy tür eşleştirmesi yapar;
        /// </summary>
        Task<FuzzyTypeMatch> FuzzyMatchTypeAsync(string rawValue, List<Type> targetTypes);

        /// <summary>
        /// Tür çözümleme cache'ini temizler;
        /// </summary>
        Task ClearCacheAsync();
    }

    /// <summary>
    /// Tür çözümleme sonucu;
    /// </summary>
    public class TypeResolutionResult;
    {
        [JsonProperty("resolvedType")]
        public Type ResolvedType { get; set; }

        [JsonProperty("typeName")]
        public string TypeName { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("convertedValue")]
        public object ConvertedValue { get; set; }

        [JsonProperty("originalValue")]
        public string OriginalValue { get; set; }

        [JsonProperty("isAmbiguous")]
        public bool IsAmbiguous { get; set; }

        [JsonProperty("possibleTypes")]
        public List<TypeCandidate> PossibleTypes { get; set; } = new List<TypeCandidate>();

        [JsonProperty("validationErrors")]
        public List<string> ValidationErrors { get; set; } = new List<string>();

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        [JsonProperty("processingTime")]
        public TimeSpan ProcessingTime { get; set; }

        [JsonProperty("patternUsed")]
        public string PatternUsed { get; set; }

        [JsonProperty("contextHints")]
        public List<string> ContextHints { get; set; } = new List<string>();
    }

    /// <summary>
    /// Olası tür adayı;
    /// </summary>
    public class TypeCandidate;
    {
        public Type Type { get; set; }
        public string TypeName { get; set; }
        public double Confidence { get; set; }
        public object ConvertedValue { get; set; }
        public List<string> SupportingPatterns { get; set; } = new List<string>();
        public Dictionary<string, object> Evidence { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dönüşüm seçenekleri;
    /// </summary>
    public class ConversionOptions;
    {
        public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
        public DateTimeStyles DateTimeStyles { get; set; } = DateTimeStyles.None;
        public NumberStyles NumberStyles { get; set; } = NumberStyles.Any;
        public bool AllowNull { get; set; } = true;
        public object DefaultValue { get; set; }
        public bool StrictParsing { get; set; } = false;
        public TimeSpan? Timeout { get; set; }
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Doğrulama seçenekleri;
    /// </summary>
    public class ValidationOptions;
    {
        public bool CheckRange { get; set; } = true;
        public bool CheckFormat { get; set; } = true;
        public bool AllowNull { get; set; } = false;
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public List<string> AllowedValues { get; set; } = new List<string>();
        public Regex FormatPattern { get; set; }
        public int MaxLength { get; set; } = -1;
        public int MinLength { get; set; } = 0;
        public Dictionary<string, object> CustomRules { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dönüşüm sonucu;
    /// </summary>
    public class ConversionResult;
    {
        public bool Success { get; set; }
        public object ConvertedValue { get; set; }
        public Type TargetType { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public TimeSpan ProcessingTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Doğrulama sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public List<string> Warnings { get; set; } = new List<string>();
        public object NormalizedValue { get; set; }
        public Dictionary<string, object> ValidationData { get; set; } = new Dictionary<string, object>();
    }

    public class ValidationError;
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public string Property { get; set; }
        public object AttemptedValue { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
    }

    public enum ValidationSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    /// <summary>
    /// Tür pattern'i;
    /// </summary>
    public class TypePattern;
    {
        public string PatternId { get; set; }
        public Regex Pattern { get; set; }
        public Type TargetType { get; set; }
        public double Confidence { get; set; } = 0.8;
        public List<string> Examples { get; set; } = new List<string>();
        public Dictionary<string, object> ExtractionRules { get; set; } = new Dictionary<string, object>();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public int UsageCount { get; set; }
        public int SuccessCount { get; set; }
        public List<string> ContextHints { get; set; } = new List<string>();
    }

    /// <summary>
    /// Özel tür çözümleyici arayüzü;
    /// </summary>
    public interface ICustomTypeResolver;
    {
        Task<TypeResolutionResult> ResolveAsync(string rawValue, Dictionary<string, object> context);
        Task<bool> CanResolveAsync(string rawValue, Dictionary<string, object> context);
        double GetConfidence(string rawValue, Dictionary<string, object> context);
    }

    /// <summary>
    /// Çözümleme bağlamı;
    /// </summary>
    public class ResolutionContext;
    {
        public string ParameterName { get; set; }
        public string Intent { get; set; }
        public Dictionary<string, object> SessionContext { get; set; } = new Dictionary<string, object>();
        public List<TypeResolutionResult> PreviousResolutions { get; set; } = new List<TypeResolutionResult>();
        public Dictionary<string, object> DomainKnowledge { get; set; } = new Dictionary<string, object>();
        public ResolutionStrategy Strategy { get; set; } = ResolutionStrategy.Standard;
        public bool UseMachineLearning { get; set; } = true;
        public int MaxAlternatives { get; set; } = 3;
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    public enum ResolutionStrategy;
    {
        Standard,
        Strict,
        Lenient,
        Contextual,
        MachineLearning,
        Hybrid;
    }

    /// <summary>
    /// Tür çakışması çözümü;
    /// </summary>
    public class TypeConflictResolution;
    {
        public Type SelectedType { get; set; }
        public double SelectionConfidence { get; set; }
        public List<TypeCandidate> Candidates { get; set; } = new List<TypeCandidate>();
        public ConflictResolutionStrategy Strategy { get; set; }
        public Dictionary<string, object> ResolutionMetrics { get; set; } = new Dictionary<string, object>();
    }

    public enum ConflictResolutionStrategy;
    {
        HighestConfidence,
        ContextBased,
        FrequencyBased,
        UserHistory,
        DomainRules,
        MachineLearning;
    }

    /// <summary>
    /// Fuzzy tür eşleştirme;
    /// </summary>
    public class FuzzyTypeMatch;
    {
        public Type BestMatch { get; set; }
        public double MatchScore { get; set; }
        public List<TypeMatchCandidate> Candidates { get; set; } = new List<TypeMatchCandidate>();
        public Dictionary<string, double> FeatureScores { get; set; } = new Dictionary<string, double>();
    }

    public class TypeMatchCandidate;
    {
        public Type Type { get; set; }
        public double Score { get; set; }
        public Dictionary<string, double> FeatureContributions { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Bilinmeyen tür örneği;
    /// </summary>
    public class UnknownTypeSample;
    {
        public string RawValue { get; set; }
        public Type ActualType { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Tür hiyerarşisi analizi;
    /// </summary>
    public class TypeHierarchyAnalysis;
    {
        public string BaseType { get; set; }
        public List<TypeNode> Hierarchy { get; set; } = new List<TypeNode>();
        public Dictionary<string, List<TypeRelationship>> Relationships { get; set; } = new Dictionary<string, List<TypeRelationship>>();
        public List<TypeConversionPath> ConversionPaths { get; set; } = new List<TypeConversionPath>();
    }

    public class TypeNode;
    {
        public Type Type { get; set; }
        public string Name { get; set; }
        public TypeNode Parent { get; set; }
        public List<TypeNode> Children { get; set; } = new List<TypeNode>();
        public int Depth { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class TypeRelationship;
    {
        public Type FromType { get; set; }
        public Type ToType { get; set; }
        public string RelationshipType { get; set; } // ConvertibleTo, CompatibleWith, etc.
        public double Strength { get; set; }
        public List<string> ConversionMethods { get; set; } = new List<string>();
    }

    public class TypeConversionPath;
    {
        public Type SourceType { get; set; }
        public Type TargetType { get; set; }
        public List<Type> IntermediateTypes { get; set; } = new List<Type>();
        public double TotalConversionCost { get; set; }
        public List<string> ConversionSteps { get; set; } = new List<string>();
    }

    /// <summary>
    /// Tür çözümleme istatistikleri;
    /// </summary>
    public class TypeResolutionStatistics;
    {
        public int TotalResolutions { get; set; }
        public int SuccessfulResolutions { get; set; }
        public double AverageConfidence { get; set; }
        public Dictionary<string, int> TypeDistribution { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, double> PatternEffectiveness { get; set; } = new Dictionary<string, double>();
        public TimeSpan AverageProcessingTime { get; set; }
        public Dictionary<string, int> ErrorDistribution { get; set; } = new Dictionary<string, int>();
        public List<TypeResolutionTrend> RecentTrends { get; set; } = new List<TypeResolutionTrend>();
    }

    public class TypeResolutionTrend;
    {
        public DateTime Period { get; set; }
        public int ResolutionCount { get; set; }
        public double AverageConfidence { get; set; }
        public int ErrorCount { get; set; }
        public Dictionary<string, int> TopTypes { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// TypeResolver implementasyonu;
    /// </summary>
    public class TypeResolver : ITypeResolver;
    {
        private readonly ILogger<TypeResolver> _logger;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly PatternRegistry _patternRegistry
        private readonly TypeCache _typeCache;
        private readonly MLTypePredictor _mlPredictor;
        private readonly Dictionary<string, ICustomTypeResolver> _customResolvers;
        private bool _disposed = false;
        private readonly object _lock = new object();

        // Pattern tanımları;
        private readonly Dictionary<Regex, TypePattern> _builtinPatterns;
        private readonly Dictionary<Type, List<TypeConversionRule>> _conversionRules;

        public TypeResolver(ILogger<TypeResolver> logger, IDiagnosticTool diagnosticTool)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));

            _patternRegistry = new PatternRegistry();
            _typeCache = new TypeCache(TimeSpan.FromHours(1));
            _mlPredictor = new MLTypePredictor();
            _customResolvers = new Dictionary<string, ICustomTypeResolver>();

            InitializeBuiltinPatterns();
            InitializeConversionRules();

            _logger.LogInformation("TypeResolver initialized with {PatternCount} built-in patterns",
                _builtinPatterns.Count);
        }

        private void InitializeBuiltinPatterns()
        {
            _builtinPatterns = new Dictionary<Regex, TypePattern>();

            // Integer pattern'leri;
            AddPattern(@"^-?\d+$", typeof(int), 0.95,
                examples: new[] { "123", "-456", "0" });

            AddPattern(@"^-?\d{1,3}(?:,\d{3})+$", typeof(int), 0.90,
                examples: new[] { "1,000", "10,000", "100,000" });

            // Decimal/Float pattern'leri;
            AddPattern(@"^-?\d+\.\d+$", typeof(decimal), 0.92,
                examples: new[] { "3.14", "-2.5", "0.0" });

            AddPattern(@"^-?\d+\.\d+e[-+]?\d+$", typeof(double), 0.88,
                examples: new[] { "1.23e10", "4.56e-5" });

            // Boolean pattern'leri;
            AddPattern(@"^(true|false|yes|no|on|off|1|0)$", typeof(bool), 0.98,
                examples: new[] { "true", "false", "yes", "no" });

            // DateTime pattern'leri;
            AddPattern(@"^\d{4}-\d{2}-\d{2}$", typeof(DateTime), 0.85,
                examples: new[] { "2024-01-15", "2023-12-31" });

            AddPattern(@"^\d{2}/\d{2}/\d{4}$", typeof(DateTime), 0.85,
                examples: new[] { "15/01/2024", "31/12/2023" });

            AddPattern(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?$", typeof(DateTime), 0.95,
                examples: new[] { "2024-01-15T14:30:00", "2024-01-15T14:30:00.123Z" });

            // TimeSpan pattern'leri;
            AddPattern(@"^\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?$", typeof(TimeSpan), 0.88,
                examples: new[] { "14:30", "14:30:00", "14:30:00.500" });

            AddPattern(@"^-?\d+(?:\.\d+)?\s*(?:hours?|hrs?|minutes?|mins?|seconds?|secs?)$", typeof(TimeSpan), 0.82,
                examples: new[] { "2 hours", "30 minutes", "1.5 hours" });

            // GUID pattern'leri;
            AddPattern(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
                typeof(Guid), 0.99,
                examples: new[] { "123e4567-e89b-12d3-a456-426614174000" });

            // Email pattern'leri;
            AddPattern(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", typeof(string), 0.90,
                contextHints: new[] { "email", "contact" });

            // URL pattern'leri;
            AddPattern(@"^(https?|ftp)://[^\s/$.?#].[^\s]*$", typeof(string), 0.88,
                contextHints: new[] { "url", "link", "website" });

            // IP Address pattern'leri;
            AddPattern(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", typeof(string), 0.92,
                contextHints: new[] { "ip", "address", "network" });

            // Phone number pattern'leri;
            AddPattern(@"^[\+]?[(]?[0-9]{3}[)]?[-\s\.]?[0-9]{3}[-\s\.]?[0-9]{4,6}$", typeof(string), 0.85,
                contextHints: new[] { "phone", "mobile", "telephone" });

            // Currency pattern'leri;
            AddPattern(@"^[$€£¥]\s?\d+(?:\.\d{2})?$", typeof(decimal), 0.87,
                contextHints: new[] { "price", "cost", "amount" });

            AddPattern(@"^\d+(?:\.\d{2})?\s?[$€£¥]$", typeof(decimal), 0.87,
                contextHints: new[] { "price", "cost", "amount" });

            // Percentage pattern'leri;
            AddPattern(@"^-?\d+(?:\.\d+)?%$", typeof(double), 0.90,
                examples: new[] { "50%", "12.5%", "-10%" });

            // File path pattern'leri;
            AddPattern(@"^[a-zA-Z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*$", typeof(string), 0.80,
                contextHints: new[] { "path", "file", "directory" });

            AddPattern(@"^/(?:[^/]+/)*[^/]+$", typeof(string), 0.80,
                contextHints: new[] { "path", "file", "directory" });

            // Enum-like pattern'leri;
            AddPattern(@"^(?:[A-Z][a-z]+)+$", typeof(string), 0.75,
                contextHints: new[] { "enum", "choice", "option" });

            // Array/List pattern'leri;
            AddPattern(@"^\[.*\]$", typeof(string), 0.70,
                contextHints: new[] { "array", "list", "collection" });

            // JSON pattern'leri;
            AddPattern(@"^\{.*\}$", typeof(JObject), 0.85,
                contextHints: new[] { "json", "object", "config" });

            AddPattern(@"^\[.*\]$", typeof(JArray), 0.85,
                contextHints: new[] { "json", "array", "list" });
        }

        private void AddPattern(string regexPattern, Type targetType, double confidence,
            string[] examples = null, string[] contextHints = null)
        {
            var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var pattern = new TypePattern;
            {
                PatternId = $"PAT_{Guid.NewGuid():N}",
                Pattern = regex,
                TargetType = targetType,
                Confidence = confidence,
                Examples = examples?.ToList() ?? new List<string>(),
                ContextHints = contextHints?.ToList() ?? new List<string>()
            };

            _builtinPatterns[regex] = pattern;
            _patternRegistry.Register(pattern);
        }

        private void InitializeConversionRules()
        {
            _conversionRules = new Dictionary<Type, List<TypeConversionRule>>();

            // String -> Other types;
            AddConversionRule(typeof(string), typeof(int),
                value => int.TryParse(value.ToString(), out var result) ? result : null,
                confidence: 0.85);

            AddConversionRule(typeof(string), typeof(decimal),
                value => decimal.TryParse(value.ToString(), out var result) ? result : null,
                confidence: 0.88);

            AddConversionRule(typeof(string), typeof(double),
                value => double.TryParse(value.ToString(), out var result) ? result : null,
                confidence: 0.88);

            AddConversionRule(typeof(string), typeof(bool),
                value => {
                    var str = value.ToString().ToLower();
                    return str switch;
                    {
                        "true" or "yes" or "on" or "1" => true,
                        "false" or "no" or "off" or "0" => false,
                        _ => null;
                    };
                },
                confidence: 0.95);

            AddConversionRule(typeof(string), typeof(DateTime),
                value => DateTime.TryParse(value.ToString(), out var result) ? result : null,
                confidence: 0.80);

            AddConversionRule(typeof(string), typeof(TimeSpan),
                value => TimeSpan.TryParse(value.ToString(), out var result) ? result : null,
                confidence: 0.75);

            AddConversionRule(typeof(string), typeof(Guid),
                value => Guid.TryParse(value.ToString(), out var result) ? result : null,
                confidence: 0.99);

            // Numeric conversions;
            AddConversionRule(typeof(int), typeof(decimal),
                value => Convert.ToDecimal(value),
                confidence: 1.0);

            AddConversionRule(typeof(int), typeof(double),
                value => Convert.ToDouble(value),
                confidence: 1.0);

            AddConversionRule(typeof(decimal), typeof(double),
                value => Convert.ToDouble(value),
                confidence: 0.95);

            AddConversionRule(typeof(double), typeof(decimal),
                value => Convert.ToDecimal(value),
                confidence: 0.90);

            // DateTime conversions;
            AddConversionRule(typeof(DateTime), typeof(string),
                value => value.ToString(),
                confidence: 0.95);

            AddConversionRule(typeof(DateTime), typeof(long),
                value => ((DateTime)value).Ticks,
                confidence: 0.85);

            // Special conversions;
            AddConversionRule(typeof(JObject), typeof(Dictionary<string, object>),
                value => ((JObject)value).ToObject<Dictionary<string, object>>(),
                confidence: 0.90);

            AddConversionRule(typeof(JArray), typeof(List<object>),
                value => ((JArray)value).ToObject<List<object>>(),
                confidence: 0.90);
        }

        private void AddConversionRule(Type fromType, Type toType, Func<object, object> converter, double confidence)
        {
            if (!_conversionRules.ContainsKey(fromType))
            {
                _conversionRules[fromType] = new List<TypeConversionRule>();
            }

            _conversionRules[fromType].Add(new TypeConversionRule;
            {
                FromType = fromType,
                ToType = toType,
                Converter = converter,
                Confidence = confidence;
            });
        }

        public async Task<TypeResolutionResult> ResolveTypeAsync(string rawValue, string parameterName = null,
            Dictionary<string, object> context = null)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new TypeResolutionResult;
                {
                    ResolvedType = typeof(string),
                    TypeName = "string",
                    Confidence = 1.0,
                    ConvertedValue = rawValue,
                    OriginalValue = rawValue,
                    IsAmbiguous = false;
                };
            }

            var cacheKey = GenerateCacheKey(rawValue, parameterName, context);

            return await _typeCache.GetOrCreateAsync(cacheKey, async () =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    _logger.LogDebug("Resolving type for value: '{Value}'", rawValue);

                    var result = new TypeResolutionResult;
                    {
                        OriginalValue = rawValue,
                        Metadata = new Dictionary<string, object>
                        {
                            ["parameterName"] = parameterName,
                            ["timestamp"] = DateTime.UtcNow;
                        }
                    };

                    // 1. Özel çözümleyicileri kontrol et;
                    foreach (var resolver in _customResolvers.Values)
                    {
                        if (await resolver.CanResolveAsync(rawValue, context))
                        {
                            var customResult = await resolver.ResolveAsync(rawValue, context);
                            if (customResult != null && customResult.Confidence > 0.7)
                            {
                                customResult.ProcessingTime = stopwatch.Elapsed;
                                customResult.PatternUsed = "CustomResolver";
                                return customResult;
                            }
                        }
                    }

                    // 2. Built-in pattern'ları kontrol et;
                    var patternMatches = MatchPatterns(rawValue, context);

                    // 3. ML tahmini (eğer etkinse)
                    var mlPrediction = await _mlPredictor.PredictAsync(rawValue, context);

                    // 4. Tüm adayları birleştir;
                    var allCandidates = CombineCandidates(patternMatches, mlPrediction);

                    // 5. En iyi adayı seç;
                    var bestCandidate = SelectBestCandidate(allCandidates, context);

                    // 6. Sonucu hazırla;
                    result.ResolvedType = bestCandidate.Type;
                    result.TypeName = bestCandidate.Type.Name;
                    result.Confidence = bestCandidate.Confidence;
                    result.ConvertedValue = bestCandidate.ConvertedValue;
                    result.PossibleTypes = allCandidates;
                    result.IsAmbiguous = allCandidates.Count > 1 &&
                                        allCandidates[0].Confidence - allCandidates[1].Confidence < 0.2;
                    result.PatternUsed = bestCandidate.SupportingPatterns.FirstOrDefault();
                    result.ProcessingTime = stopwatch.Elapsed;

                    // 7. Validation yap;
                    var validation = await ValidateTypeAsync(result.ConvertedValue, result.ResolvedType);
                    if (!validation.IsValid)
                    {
                        result.ValidationErrors.AddRange(validation.Errors.Select(e => e.Message));
                    }

                    // 8. Pattern kullanımını güncelle;
                    if (!string.IsNullOrEmpty(result.PatternUsed))
                    {
                        _patternRegistry.RecordUsage(result.PatternUsed, result.Confidence > 0.8);
                    }

                    _logger.LogDebug("Type resolved: {Type} with confidence {Confidence}",
                        result.TypeName, result.Confidence);

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resolving type for value: '{Value}'", rawValue);
                    throw new TypeResolutionException(
                        $"Failed to resolve type for value: '{rawValue}'",
                        ex,
                        ErrorCodes.TypeResolutionFailed);
                }
            });
        }

        private List<TypeCandidate> MatchPatterns(string rawValue, Dictionary<string, object> context)
        {
            var candidates = new List<TypeCandidate>();

            foreach (var kvp in _builtinPatterns)
            {
                var pattern = kvp.Key;
                var patternInfo = kvp.Value;

                if (pattern.IsMatch(rawValue))
                {
                    // Context hint kontrolü;
                    double contextBonus = 0.0;
                    if (context != null && patternInfo.ContextHints.Any())
                    {
                        foreach (var hint in patternInfo.ContextHints)
                        {
                            if (context.ContainsKey(hint))
                            {
                                contextBonus += 0.1;
                            }
                        }
                    }

                    // Değeri dönüştürmeyi dene;
                    object convertedValue = null;
                    try
                    {
                        convertedValue = ConvertUsingPattern(rawValue, patternInfo);
                    }
                    catch
                    {
                        // Dönüşüm başarısız oldu, confidence düşür;
                        continue;
                    }

                    candidates.Add(new TypeCandidate;
                    {
                        Type = patternInfo.TargetType,
                        TypeName = patternInfo.TargetType.Name,
                        Confidence = Math.Min(1.0, patternInfo.Confidence + contextBonus),
                        ConvertedValue = convertedValue,
                        SupportingPatterns = new List<string> { patternInfo.PatternId },
                        Evidence = new Dictionary<string, object>
                        {
                            ["pattern"] = patternInfo.Pattern.ToString(),
                            ["contextBonus"] = contextBonus;
                        }
                    });
                }
            }

            return candidates.OrderByDescending(c => c.Confidence).ToList();
        }

        private object ConvertUsingPattern(string rawValue, TypePattern pattern)
        {
            try
            {
                var targetType = pattern.TargetType;

                if (targetType == typeof(int))
                    return int.Parse(rawValue.Replace(",", ""));

                if (targetType == typeof(decimal))
                    return decimal.Parse(rawValue);

                if (targetType == typeof(double))
                    return double.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(bool))
                {
                    var lower = rawValue.ToLower();
                    return lower switch;
                    {
                        "true" or "yes" or "on" or "1" => true,
                        "false" or "no" or "off" or "0" => false,
                        _ => throw new FormatException()
                    };
                }

                if (targetType == typeof(DateTime))
                    return DateTime.Parse(rawValue);

                if (targetType == typeof(TimeSpan))
                    return TimeSpan.Parse(rawValue);

                if (targetType == typeof(Guid))
                    return Guid.Parse(rawValue);

                if (targetType == typeof(JObject))
                    return JObject.Parse(rawValue);

                if (targetType == typeof(JArray))
                    return JArray.Parse(rawValue);

                // Diğer türler için string olarak döndür;
                return rawValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert '{Value}' using pattern", rawValue);
                throw;
            }
        }

        private async Task<List<TypeCandidate>> CombineCandidates(List<TypeCandidate> patternCandidates,
            List<TypeCandidate> mlCandidates)
        {
            var allCandidates = new Dictionary<Type, TypeCandidate>();

            // Pattern adaylarını ekle;
            foreach (var candidate in patternCandidates)
            {
                if (!allCandidates.ContainsKey(candidate.Type))
                {
                    allCandidates[candidate.Type] = candidate;
                }
                else if (candidate.Confidence > allCandidates[candidate.Type].Confidence)
                {
                    allCandidates[candidate.Type] = candidate;
                }
            }

            // ML adaylarını ekle (weighted)
            foreach (var candidate in mlCandidates)
            {
                if (allCandidates.TryGetValue(candidate.Type, out var existing))
                {
                    // ML ve pattern confidence'larını birleştir;
                    existing.Confidence = (existing.Confidence * 0.6) + (candidate.Confidence * 0.4);
                    existing.SupportingPatterns.AddRange(candidate.SupportingPatterns);
                }
                else;
                {
                    allCandidates[candidate.Type] = candidate;
                }
            }

            return allCandidates.Values;
                .OrderByDescending(c => c.Confidence)
                .ToList();
        }

        private TypeCandidate SelectBestCandidate(List<TypeCandidate> candidates, Dictionary<string, object> context)
        {
            if (!candidates.Any())
            {
                // Default olarak string;
                return new TypeCandidate;
                {
                    Type = typeof(string),
                    TypeName = "string",
                    Confidence = 0.5,
                    ConvertedValue = context?.ContainsKey("rawValue")?.ToString() ?? string.Empty;
                };
            }

            if (candidates.Count == 1)
                return candidates[0];

            // Context-aware seçim;
            var contextAwareCandidates = ApplyContextRules(candidates, context);

            // En yüksek confidence olanı seç;
            return contextAwareCandidates.First();
        }

        private List<TypeCandidate> ApplyContextRules(List<TypeCandidate> candidates, Dictionary<string, object> context)
        {
            if (context == null || !context.Any())
                return candidates;

            var scoredCandidates = new List<(TypeCandidate Candidate, double Score)>();

            foreach (var candidate in candidates)
            {
                var score = candidate.Confidence;

                // Parametre ismine göre bonus;
                if (context.TryGetValue("parameterName", out var paramName))
                {
                    var paramStr = paramName.ToString().ToLower();

                    if (paramStr.Contains("id") && candidate.Type == typeof(Guid))
                        score += 0.2;

                    if (paramStr.Contains("date") && candidate.Type == typeof(DateTime))
                        score += 0.15;

                    if (paramStr.Contains("time") && candidate.Type == typeof(TimeSpan))
                        score += 0.15;

                    if (paramStr.Contains("price") && candidate.Type == typeof(decimal))
                        score += 0.1;

                    if (paramStr.Contains("count") && candidate.Type == typeof(int))
                        score += 0.1;

                    if (paramStr.Contains("email") && candidate.Type == typeof(string))
                        score += 0.1;
                }

                // Önceki çözümlemelere göre bonus;
                if (context.TryGetValue("previousTypes", out var prevTypes))
                {
                    if (prevTypes is List<Type> typeList && typeList.Contains(candidate.Type))
                        score += 0.1;
                }

                scoredCandidates.Add((candidate, Math.Min(1.0, score)));
            }

            return scoredCandidates;
                .OrderByDescending(x => x.Score)
                .Select(x =>
                {
                    x.Candidate.Confidence = x.Score;
                    return x.Candidate;
                })
                .ToList();
        }

        public async Task<List<TypeResolutionResult>> ResolveTypesAsync(List<string> rawValues,
            Dictionary<string, object> context = null)
        {
            var results = new List<TypeResolutionResult>();

            // Paralel işleme;
            var tasks = rawValues.Select(value =>
                ResolveTypeAsync(value, null, context));

            var resolvedResults = await Task.WhenAll(tasks);
            results.AddRange(resolvedResults);

            // Grup analizi yap;
            if (results.Count > 1)
            {
                await AnalyzeGroupConsistency(results);
            }

            return results;
        }

        private async Task AnalyzeGroupConsistency(List<TypeResolutionResult> results)
        {
            // Tüm sonuçlar aynı türde mi?
            var typeGroups = results.GroupBy(r => r.ResolvedType);

            if (typeGroups.Count() > 1)
            {
                // Mixed types - context'i kontrol et;
                var dominantType = typeGroups.OrderByDescending(g => g.Count()).First().Key;

                foreach (var result in results.Where(r => r.ResolvedType != dominantType))
                {
                    // Düşük confidence olanları dominant türe dönüştürmeyi dene;
                    if (result.Confidence < 0.7)
                    {
                        var conversion = await ConvertToTypeAsync(
                            result.ConvertedValue,
                            dominantType);

                        if (conversion.Success)
                        {
                            result.ResolvedType = dominantType;
                            result.ConvertedValue = conversion.ConvertedValue;
                            result.Confidence = Math.Max(result.Confidence, 0.6);
                            result.Metadata["groupAdjusted"] = true;
                        }
                    }
                }
            }
        }

        public async Task<ConversionResult> ConvertToTypeAsync(object value, Type targetType,
            ConversionOptions options = null)
        {
            options ??= new ConversionOptions();

            var result = new ConversionResult;
            {
                TargetType = targetType;
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (value == null)
                {
                    if (options.AllowNull)
                    {
                        result.Success = true;
                        result.ConvertedValue = options.DefaultValue;
                    }
                    else;
                    {
                        result.Errors.Add("Null value is not allowed");
                    }
                    return result;
                }

                // Zaten istenen türde mi?
                if (value.GetType() == targetType)
                {
                    result.Success = true;
                    result.ConvertedValue = value;
                    result.ProcessingTime = stopwatch.Elapsed;
                    return result;
                }

                // Doğrudan dönüşüm mümkün mü?
                try
                {
                    result.ConvertedValue = Convert.ChangeType(value, targetType, options.Culture);
                    result.Success = true;
                    result.ProcessingTime = stopwatch.Elapsed;
                    return result;
                }
                catch
                {
                    // Doğrudan dönüşüm başarısız, özel dönüşüm kurallarını dene;
                }

                // Kayıtlı dönüşüm kurallarını kontrol et;
                var sourceType = value.GetType();
                if (_conversionRules.TryGetValue(sourceType, out var rules))
                {
                    var applicableRule = rules.FirstOrDefault(r => r.ToType == targetType);
                    if (applicableRule != null)
                    {
                        try
                        {
                            result.ConvertedValue = applicableRule.Converter(value);
                            if (result.ConvertedValue != null)
                            {
                                result.Success = true;
                                result.Metadata["conversionRule"] = applicableRule.GetType().Name;
                            }
                            else;
                            {
                                result.Errors.Add($"Conversion rule returned null");
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Conversion failed: {ex.Message}");
                        }
                    }
                    else;
                    {
                        result.Errors.Add($"No conversion rule from {sourceType.Name} to {targetType.Name}");
                    }
                }
                else;
                {
                    // Indirect conversion yolu ara;
                    var conversionPath = FindConversionPath(sourceType, targetType);
                    if (conversionPath != null)
                    {
                        result.ConvertedValue = await ConvertThroughPathAsync(value, conversionPath);
                        if (result.ConvertedValue != null)
                        {
                            result.Success = true;
                            result.Metadata["conversionPath"] = conversionPath.IntermediateTypes;
                                .Select(t => t.Name)
                                .ToList();
                        }
                        else;
                        {
                            result.Errors.Add("Indirect conversion failed");
                        }
                    }
                    else;
                    {
                        result.Errors.Add($"No conversion path from {sourceType.Name} to {targetType.Name}");
                    }
                }

                // Dönüşüm başarısız oldu, default değeri kullan;
                if (!result.Success && options.DefaultValue != null)
                {
                    try
                    {
                        result.ConvertedValue = Convert.ChangeType(options.DefaultValue, targetType);
                        result.Success = true;
                        result.Warnings.Add("Used default value after conversion failure");
                    }
                    catch
                    {
                        result.Errors.Add("Failed to use default value");
                    }
                }

                result.ProcessingTime = stopwatch.Elapsed;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting {Value} to {TargetType}", value, targetType.Name);
                result.Errors.Add($"Conversion error: {ex.Message}");
                result.ProcessingTime = stopwatch.Elapsed;
                return result;
            }
        }

        private TypeConversionPath FindConversionPath(Type sourceType, Type targetType)
        {
            // Basit BFS ile dönüşüm yolu bul;
            var visited = new HashSet<Type>();
            var queue = new Queue<(Type Current, List<Type> Path)>();
            queue.Enqueue((sourceType, new List<Type>()));

            while (queue.Count > 0)
            {
                var (current, path) = queue.Dequeue();

                if (current == targetType)
                {
                    return new TypeConversionPath;
                    {
                        SourceType = sourceType,
                        TargetType = targetType,
                        IntermediateTypes = path.Skip(1).ToList(),
                        TotalConversionCost = path.Count * 0.1;
                    };
                }

                if (!visited.Add(current))
                    continue;

                if (_conversionRules.TryGetValue(current, out var rules))
                {
                    foreach (var rule in rules)
                    {
                        var newPath = new List<Type>(path) { current };
                        queue.Enqueue((rule.ToType, newPath));
                    }
                }
            }

            return null;
        }

        private async Task<object> ConvertThroughPathAsync(object value, TypeConversionPath path)
        {
            object current = value;

            foreach (var intermediateType in path.IntermediateTypes)
            {
                var conversion = await ConvertToTypeAsync(current, intermediateType);
                if (!conversion.Success)
                    return null;

                current = conversion.ConvertedValue;
            }

            // Final conversion to target;
            var finalConversion = await ConvertToTypeAsync(current, path.TargetType);
            return finalConversion.Success ? finalConversion.ConvertedValue : null;
        }

        public async Task<ValidationResult> ValidateTypeAsync(object value, Type expectedType,
            ValidationOptions options = null)
        {
            options ??= new ValidationOptions();

            var result = new ValidationResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Null check;
                if (value == null)
                {
                    if (!options.AllowNull)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_NULL",
                            Message = "Value cannot be null",
                            Severity = ValidationSeverity.Error;
                        });
                    }
                    result.IsValid = options.AllowNull;
                    return result;
                }

                // Type check;
                if (!expectedType.IsInstanceOfType(value))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        ErrorCode = "VALIDATION_TYPE_MISMATCH",
                        Message = $"Expected type {expectedType.Name}, got {value.GetType().Name}",
                        AttemptedValue = value,
                        Severity = ValidationSeverity.Error;
                    });
                }

                // Range validation for numeric types;
                if (options.CheckRange)
                {
                    ValidateRange(value, expectedType, options, result);
                }

                // Format validation;
                if (options.CheckFormat && options.FormatPattern != null && value is string stringValue)
                {
                    if (!options.FormatPattern.IsMatch(stringValue))
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_FORMAT",
                            Message = $"Value does not match expected format",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }
                }

                // Length validation for strings;
                if (value is string str)
                {
                    if (str.Length < options.MinLength)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MIN_LENGTH",
                            Message = $"Minimum length is {options.MinLength}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }

                    if (options.MaxLength > 0 && str.Length > options.MaxLength)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MAX_LENGTH",
                            Message = $"Maximum length is {options.MaxLength}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }
                }

                // Allowed values check;
                if (options.AllowedValues.Any() && value != null)
                {
                    var stringValue = value.ToString();
                    if (!options.AllowedValues.Contains(stringValue, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_NOT_ALLOWED",
                            Message = $"Value not in allowed list",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }
                }

                // Custom rules;
                foreach (var rule in options.CustomRules)
                {
                    // Custom validation logic buraya eklenebilir;
                }

                result.IsValid = !result.Errors.Any(e => e.Severity >= ValidationSeverity.Error);

                // Normalize value if valid;
                if (result.IsValid)
                {
                    result.NormalizedValue = NormalizeValue(value, expectedType, options);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating value of type {Type}", expectedType.Name);
                result.Errors.Add(new ValidationError;
                {
                    ErrorCode = "VALIDATION_EXCEPTION",
                    Message = $"Validation error: {ex.Message}",
                    Severity = ValidationSeverity.Critical;
                });
                return result;
            }
        }

        private void ValidateRange(object value, Type type, ValidationOptions options, ValidationResult result)
        {
            try
            {
                if (type == typeof(int) && value is int intValue)
                {
                    if (options.MinValue is int min && intValue < min)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MIN_RANGE",
                            Message = $"Value must be at least {min}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }

                    if (options.MaxValue is int max && intValue > max)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MAX_RANGE",
                            Message = $"Value must be at most {max}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }
                }
                else if (type == typeof(decimal) && value is decimal decimalValue)
                {
                    if (options.MinValue is decimal min && decimalValue < min)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MIN_RANGE",
                            Message = $"Value must be at least {min}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }

                    if (options.MaxValue is decimal max && decimalValue > max)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MAX_RANGE",
                            Message = $"Value must be at most {max}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }
                }
                else if (type == typeof(DateTime) && value is DateTime dateValue)
                {
                    if (options.MinValue is DateTime min && dateValue < min)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MIN_DATE",
                            Message = $"Date must be on or after {min:yyyy-MM-dd}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }

                    if (options.MaxValue is DateTime max && dateValue > max)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorCode = "VALIDATION_MAX_DATE",
                            Message = $"Date must be on or before {max:yyyy-MM-dd}",
                            AttemptedValue = value,
                            Severity = ValidationSeverity.Error;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Range validation failed for type {Type}", type.Name);
            }
        }

        private object NormalizeValue(object value, Type type, ValidationOptions options)
        {
            try
            {
                if (value is string str)
                {
                    // Trim whitespace;
                    str = str.Trim();

                    // Case normalization for enums/options;
                    if (options.AllowedValues.Any())
                    {
                        var matchingValue = options.AllowedValues;
                            .FirstOrDefault(v => v.Equals(str, StringComparison.OrdinalIgnoreCase));
                        if (matchingValue != null)
                        {
                            return matchingValue;
                        }
                    }

                    return str;
                }

                return value;
            }
            catch
            {
                return value;
            }
        }

        public async Task UpdatePatternsAsync(Dictionary<string, TypePattern> newPatterns)
        {
            lock (_lock)
            {
                foreach (var kvp in newPatterns)
                {
                    _patternRegistry.UpdateOrAdd(kvp.Key, kvp.Value);
                }
            }

            await Task.CompletedTask;
        }

        public async Task RegisterCustomResolver(string typeName, ICustomTypeResolver resolver)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("Type name cannot be null or empty", nameof(typeName));

            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));

            lock (_lock)
            {
                _customResolvers[typeName] = resolver;
            }

            _logger.LogInformation("Registered custom resolver for type: {TypeName}", typeName);
            await Task.CompletedTask;
        }

        public async Task<TypeResolutionStatistics> GetStatisticsAsync()
        {
            var stats = new TypeResolutionStatistics;
            {
                TotalResolutions = _patternRegistry.TotalResolutions,
                SuccessfulResolutions = _patternRegistry.SuccessfulResolutions,
                AverageConfidence = _patternRegistry.AverageConfidence,
                TypeDistribution = _patternRegistry.GetTypeDistribution(),
                PatternEffectiveness = _patternRegistry.GetPatternEffectiveness(),
                AverageProcessingTime = TimeSpan.FromMilliseconds(_patternRegistry.AverageProcessingTimeMs),
                ErrorDistribution = _patternRegistry.GetErrorDistribution()
            };

            return await Task.FromResult(stats);
        }

        public async Task LearnFromUnknownTypesAsync(List<UnknownTypeSample> samples)
        {
            if (samples == null || !samples.Any())
                return;

            try
            {
                var newPatterns = new Dictionary<string, TypePattern>();

                foreach (var sample in samples)
                {
                    // Sample'dan pattern çıkar;
                    var pattern = ExtractPatternFromSample(sample);
                    if (pattern != null)
                    {
                        newPatterns[pattern.PatternId] = pattern;
                    }
                }

                // Pattern'leri güncelle;
                if (newPatterns.Any())
                {
                    await UpdatePatternsAsync(newPatterns);
                    _logger.LogInformation("Learned {Count} new patterns from unknown types", newPatterns.Count);
                }

                // ML modelini güncelle;
                await _mlPredictor.TrainAsync(samples);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to learn from unknown types");
            }
        }

        private TypePattern ExtractPatternFromSample(UnknownTypeSample sample)
        {
            try
            {
                var value = sample.RawValue;
                var type = sample.ActualType;

                // Value'ya göre regex pattern oluştur;
                string regexPattern = BuildRegexFromValue(value, type);

                if (regexPattern == null)
                    return null;

                var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

                return new TypePattern;
                {
                    PatternId = $"LEARNED_{Guid.NewGuid():N}",
                    Pattern = regex,
                    TargetType = type,
                    Confidence = 0.7, // Initial confidence for learned patterns;
                    Examples = new List<string> { value },
                    ContextHints = sample.Context?.Keys.ToList() ?? new List<string>(),
                    Created = DateTime.UtcNow;
                };
            }
            catch
            {
                return null;
            }
        }

        private string BuildRegexFromValue(string value, Type type)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            // Basit pattern extraction;
            if (type == typeof(int))
            {
                if (int.TryParse(value, out _))
                    return @"^-?\d+$";
            }
            else if (type == typeof(decimal))
            {
                if (decimal.TryParse(value, out _))
                    return @"^-?\d+(?:\.\d+)?$";
            }
            else if (type == typeof(DateTime))
            {
                if (DateTime.TryParse(value, out _))
                {
                    // Tarih formatına göre regex oluştur;
                    if (value.Contains("-"))
                        return @"^\d{4}-\d{2}-\d{2}$";
                    else if (value.Contains("/"))
                        return @"^\d{2}/\d{2}/\d{4}$";
                }
            }
            else if (type == typeof(bool))
            {
                var lower = value.ToLower();
                if (lower == "true" || lower == "false" || lower == "yes" || lower == "no")
                    return @"^(true|false|yes|no)$";
            }

            return null;
        }

        public async Task<TypeHierarchyAnalysis> AnalyzeTypeHierarchyAsync(string baseType)
        {
            var analysis = new TypeHierarchyAnalysis;
            {
                BaseType = baseType;
            };

            try
            {
                // Built-in type hiyerarşisini analiz et;
                var type = Type.GetType(baseType);
                if (type == null)
                    throw new ArgumentException($"Type not found: {baseType}");

                // Conversion graph'ını oluştur;
                var graph = BuildConversionGraph(type);

                analysis.Hierarchy = BuildTypeHierarchy(type);
                analysis.Relationships = AnalyzeRelationships(graph);
                analysis.ConversionPaths = FindConversionPaths(graph, type);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze type hierarchy for {BaseType}", baseType);
                throw new TypeResolutionException(
                    $"Failed to analyze type hierarchy for {baseType}",
                    ex,
                    ErrorCodes.HierarchyAnalysisFailed);
            }
        }

        private List<TypeNode> BuildTypeHierarchy(Type baseType)
        {
            var nodes = new Dictionary<Type, TypeNode>();

            void BuildNode(Type type, TypeNode parent = null)
            {
                if (type == null || nodes.ContainsKey(type))
                    return;

                var node = new TypeNode;
                {
                    Type = type,
                    Name = type.Name,
                    Parent = parent,
                    Depth = parent?.Depth + 1 ?? 0;
                };

                nodes[type] = node;

                if (parent != null)
                {
                    parent.Children.Add(node);
                }

                // Base type'ı ekle;
                if (type.BaseType != null && type.BaseType != typeof(object))
                {
                    BuildNode(type.BaseType, node);
                }

                // Interface'leri ekle;
                foreach (var interfaceType in type.GetInterfaces())
                {
                    BuildNode(interfaceType, node);
                }
            }

            BuildNode(baseType);
            return nodes.Values.ToList();
        }

        private Dictionary<string, List<TypeRelationship>> AnalyzeRelationships(Dictionary<Type, List<Type>> graph)
        {
            var relationships = new Dictionary<string, List<TypeRelationship>>();

            foreach (var kvp in graph)
            {
                var fromType = kvp.Key;
                var relatedTypes = kvp.Value;

                foreach (var toType in relatedTypes)
                {
                    var relationship = new TypeRelationship;
                    {
                        FromType = fromType,
                        ToType = toType,
                        RelationshipType = "ConvertibleTo",
                        Strength = CalculateConversionStrength(fromType, toType),
                        ConversionMethods = GetConversionMethods(fromType, toType)
                    };

                    var key = $"{fromType.Name}_{toType.Name}";
                    if (!relationships.ContainsKey(key))
                    {
                        relationships[key] = new List<TypeRelationship>();
                    }

                    relationships[key].Add(relationship);
                }
            }

            return relationships;
        }

        private double CalculateConversionStrength(Type fromType, Type toType)
        {
            // Conversion kurallarına göre strength hesapla;
            if (_conversionRules.TryGetValue(fromType, out var rules))
            {
                var rule = rules.FirstOrDefault(r => r.ToType == toType);
                if (rule != null)
                {
                    return rule.Confidence;
                }
            }

            // Implicit conversion var mı?
            try
            {
                // Test conversion;
                var testValue = GetTestValue(fromType);
                var conversion = ConvertToTypeAsync(testValue, toType).Result;
                return conversion.Success ? 0.5 : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private List<string> GetConversionMethods(Type fromType, Type toType)
        {
            var methods = new List<string>();

            // Built-in conversion methods;
            methods.Add("Convert.ChangeType");
            methods.Add("TypeConverter");

            // Custom conversion methods;
            if (_conversionRules.TryGetValue(fromType, out var rules))
            {
                if (rules.Any(r => r.ToType == toType))
                {
                    methods.Add("CustomConverter");
                }
            }

            return methods;
        }

        private object GetTestValue(Type type)
        {
            if (type == typeof(int))
                return 0;
            if (type == typeof(string))
                return "";
            if (type == typeof(DateTime))
                return DateTime.Now;
            if (type == typeof(bool))
                return false;
            if (type == typeof(decimal))
                return 0m;

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private Dictionary<Type, List<Type>> BuildConversionGraph(Type baseType)
        {
            var graph = new Dictionary<Type, List<Type>>();
            var visited = new HashSet<Type>();
            var queue = new Queue<Type>();

            queue.Enqueue(baseType);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (!visited.Add(current))
                    continue;

                if (!graph.ContainsKey(current))
                {
                    graph[current] = new List<Type>();
                }

                // Conversion kurallarından ilişkili türleri bul;
                if (_conversionRules.TryGetValue(current, out var rules))
                {
                    foreach (var rule in rules)
                    {
                        graph[current].Add(rule.ToType);

                        if (!visited.Contains(rule.ToType))
                        {
                            queue.Enqueue(rule.ToType);
                        }
                    }
                }
            }

            return graph;
        }

        private List<TypeConversionPath> FindConversionPaths(Dictionary<Type, List<Type>> graph, Type baseType)
        {
            var paths = new List<TypeConversionPath>();

            foreach (var targetType in graph.Keys.Where(t => t != baseType))
            {
                var path = FindShortestPath(graph, baseType, targetType);
                if (path != null)
                {
                    paths.Add(new TypeConversionPath;
                    {
                        SourceType = baseType,
                        TargetType = targetType,
                        IntermediateTypes = path.Skip(1).Take(path.Count - 2).ToList(),
                        TotalConversionCost = path.Count * 0.1,
                        ConversionSteps = BuildConversionSteps(path)
                    });
                }
            }

            return paths;
        }

        private List<Type> FindShortestPath(Dictionary<Type, List<Type>> graph, Type start, Type end)
        {
            var visited = new HashSet<Type>();
            var queue = new Queue<List<Type>>();

            queue.Enqueue(new List<Type> { start });

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var node = path.Last();

                if (node == end)
                    return path;

                if (!visited.Add(node))
                    continue;

                if (graph.TryGetValue(node, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        if (!visited.Contains(neighbor))
                        {
                            var newPath = new List<Type>(path) { neighbor };
                            queue.Enqueue(newPath);
                        }
                    }
                }
            }

            return null;
        }

        private List<string> BuildConversionSteps(List<Type> path)
        {
            var steps = new List<string>();

            for (int i = 0; i < path.Count - 1; i++)
            {
                steps.Add($"{path[i].Name} -> {path[i + 1].Name}");
            }

            return steps;
        }

        public async Task<TypeResolutionResult> ResolveWithContextAsync(string rawValue,
            ResolutionContext resolutionContext)
        {
            if (resolutionContext == null)
                return await ResolveTypeAsync(rawValue);

            var context = new Dictionary<string, object>
            {
                ["parameterName"] = resolutionContext.ParameterName,
                ["intent"] = resolutionContext.Intent,
                ["domainKnowledge"] = resolutionContext.DomainKnowledge,
                ["previousResolutions"] = resolutionContext.PreviousResolutions;
            };

            // Strategy'ye göre çözümleme;
            return resolutionContext.Strategy switch;
            {
                ResolutionStrategy.Strict => await ResolveStrictAsync(rawValue, context),
                ResolutionStrategy.Lenient => await ResolveLenientAsync(rawValue, context),
                ResolutionStrategy.Contextual => await ResolveContextualAsync(rawValue, resolutionContext),
                ResolutionStrategy.MachineLearning => await ResolveWithMLAsync(rawValue, context),
                ResolutionStrategy.Hybrid => await ResolveHybridAsync(rawValue, resolutionContext),
                _ => await ResolveTypeAsync(rawValue, resolutionContext.ParameterName, context)
            };
        }

        private async Task<TypeResolutionResult> ResolveStrictAsync(string rawValue, Dictionary<string, object> context)
        {
            var result = await ResolveTypeAsync(rawValue, null, context);

            // Strict mode: yüksek confidence gerektir;
            if (result.Confidence < 0.9)
            {
                result.ResolvedType = typeof(string);
                result.Confidence = 0.5;
                result.IsAmbiguous = true;
                result.ValidationErrors.Add("Low confidence in strict mode");
            }

            return result;
        }

        private async Task<TypeResolutionResult> ResolveLenientAsync(string rawValue, Dictionary<string, object> context)
        {
            var result = await ResolveTypeAsync(rawValue, null, context);

            // Lenient mode: düşük confidence bile kabul edilir;
            if (result.Confidence < 0.3)
            {
                // String olarak kabul et;
                result.ResolvedType = typeof(string);
                result.ConvertedValue = rawValue;
                result.Confidence = 0.5;
            }

            return result;
        }

        private async Task<TypeResolutionResult> ResolveContextualAsync(string rawValue, ResolutionContext context)
        {
            // Domain knowledge kullan;
            if (context.DomainKnowledge != null)
            {
                // Domain'e özel pattern'leri kontrol et;
                if (context.DomainKnowledge.TryGetValue("typePatterns", out var patternsObj))
                {
                    if (patternsObj is Dictionary<string, TypePattern> domainPatterns)
                    {
                        foreach (var pattern in domainPatterns.Values)
                        {
                            if (pattern.Pattern.IsMatch(rawValue))
                            {
                                return new TypeResolutionResult;
                                {
                                    ResolvedType = pattern.TargetType,
                                    TypeName = pattern.TargetType.Name,
                                    Confidence = pattern.Confidence,
                                    ConvertedValue = ConvertUsingPattern(rawValue, pattern),
                                    OriginalValue = rawValue,
                                    PatternUsed = pattern.PatternId,
                                    Metadata = new Dictionary<string, object>
                                    {
                                        ["domainPattern"] = true;
                                    }
                                };
                            }
                        }
                    }
                }
            }

            // Previous resolutions'dan context al;
            if (context.PreviousResolutions.Any())
            {
                var lastType = context.PreviousResolutions.Last().ResolvedType;
                // Benzer türleri tercih et;
                var baseResult = await ResolveTypeAsync(rawValue, context.ParameterName);

                if (baseResult.PossibleTypes.Any(c => c.Type == lastType))
                {
                    var candidate = baseResult.PossibleTypes.First(c => c.Type == lastType);
                    baseResult.ResolvedType = candidate.Type;
                    baseResult.Confidence = candidate.Confidence * 1.1; // Context bonus;
                    baseResult.ConvertedValue = candidate.ConvertedValue;
                    baseResult.Metadata["contextBonus"] = true;
                }

                return baseResult;
            }

            return await ResolveTypeAsync(rawValue, context.ParameterName);
        }

        private async Task<TypeResolutionResult> ResolveWithMLAsync(string rawValue, Dictionary<string, object> context)
        {
            var mlResult = await _mlPredictor.PredictBestAsync(rawValue, context);

            if (mlResult != null && mlResult.Confidence > 0.7)
            {
                return new TypeResolutionResult;
                {
                    ResolvedType = mlResult.Type,
                    TypeName = mlResult.Type.Name,
                    Confidence = mlResult.Confidence,
                    ConvertedValue = mlResult.ConvertedValue,
                    OriginalValue = rawValue,
                    PatternUsed = "ML_Prediction",
                    Metadata = new Dictionary<string, object>
                    {
                        ["mlPrediction"] = true,
                        ["mlModelVersion"] = _mlPredictor.ModelVersion;
                    }
                };
            }

            // ML başarısız olursa standart çözümleme;
            return await ResolveTypeAsync(rawValue, null, context);
        }

        private async Task<TypeResolutionResult> ResolveHybridAsync(string rawValue, ResolutionContext context)
        {
            // Multiple strategies'yi birleştir;
            var tasks = new List<Task<TypeResolutionResult>>
            {
                ResolveTypeAsync(rawValue, context.ParameterName),
                ResolveWithMLAsync(rawValue, new Dictionary<string, object>
                {
                    ["parameterName"] = context.ParameterName,
                    ["intent"] = context.Intent;
                })
            };

            var results = await Task.WhenAll(tasks);

            // En yüksek confidence olanı seç;
            var bestResult = results.OrderByDescending(r => r.Confidence).First();

            // Weighted average confidence;
            var weightedConfidence = results.Average(r => r.Confidence);
            bestResult.Confidence = weightedConfidence;
            bestResult.Metadata["hybridResolution"] = true;
            bestResult.Metadata["strategyCount"] = results.Length;

            return bestResult;
        }

        public async Task<TypeConflictResolution> ResolveTypeConflictAsync(List<TypeResolutionResult> candidates)
        {
            if (candidates == null || candidates.Count < 2)
            {
                throw new ArgumentException("At least two candidates required for conflict resolution");
            }

            var resolution = new TypeConflictResolution;
            {
                Candidates = candidates.Select(c => new TypeCandidate;
                {
                    Type = c.ResolvedType,
                    TypeName = c.TypeName,
                    Confidence = c.Confidence,
                    ConvertedValue = c.ConvertedValue;
                }).ToList()
            };

            try
            {
                // Strategy 1: Highest confidence;
                var highestConfidence = resolution.Candidates;
                    .OrderByDescending(c => c.Confidence)
                    .First();

                // Strategy 2: Context-based (eğer context varsa)
                TypeCandidate contextBased = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.ContextHints.Any())
                    {
                        contextBased = resolution.Candidates.First(c => c.Type == candidate.ResolvedType);
                        break;
                    }
                }

                // Strategy 3: Frequency-based (pattern kullanımına göre)
                var frequencyBased = resolution.Candidates;
                    .OrderByDescending(c => _patternRegistry.GetUsageCount(c.Type.Name))
                    .First();

                // Final decision;
                resolution.SelectedType = highestConfidence.Type;
                resolution.SelectionConfidence = highestConfidence.Confidence;
                resolution.Strategy = ConflictResolutionStrategy.HighestConfidence;

                // Metrics;
                resolution.ResolutionMetrics = new Dictionary<string, object>
                {
                    ["candidateCount"] = candidates.Count,
                    ["confidenceRange"] = resolution.Candidates.Max(c => c.Confidence) -
                                         resolution.Candidates.Min(c => c.Confidence),
                    ["selectedConfidence"] = highestConfidence.Confidence,
                    ["contextBasedAlternative"] = contextBased?.Type?.Name,
                    ["frequencyBasedAlternative"] = frequencyBased.Type.Name;
                };

                return resolution;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve type conflict");
                throw new TypeResolutionException(
                    "Failed to resolve type conflict",
                    ex,
                    ErrorCodes.ConflictResolutionFailed);
            }
        }

        public async Task<FuzzyTypeMatch> FuzzyMatchTypeAsync(string rawValue, List<Type> targetTypes)
        {
            var match = new FuzzyTypeMatch();
            var candidates = new List<TypeMatchCandidate>();

            foreach (var targetType in targetTypes)
            {
                var result = await ResolveTypeAsync(rawValue);
                var score = CalculateFuzzyMatchScore(result, targetType);

                candidates.Add(new TypeMatchCandidate;
                {
                    Type = targetType,
                    Score = score,
                    FeatureContributions = new Dictionary<string, double>
                    {
                        ["typeSimilarity"] = result.ResolvedType == targetType ? 1.0 : 0.0,
                        ["confidence"] = result.Confidence,
                        ["compatibility"] = CalculateTypeCompatibility(result.ResolvedType, targetType)
                    }
                });
            }

            match.Candidates = candidates.OrderByDescending(c => c.Score).ToList();
            match.BestMatch = match.Candidates.FirstOrDefault()?.Type;
            match.MatchScore = match.Candidates.FirstOrDefault()?.Score ?? 0.0;

            // Feature scores;
            match.FeatureScores = new Dictionary<string, double>
            {
                ["averageScore"] = candidates.Average(c => c.Score),
                ["maxScore"] = candidates.Max(c => c.Score),
                ["minScore"] = candidates.Min(c => c.Score),
                ["scoreVariance"] = CalculateVariance(candidates.Select(c => c.Score))
            };

            return match;
        }

        private double CalculateFuzzyMatchScore(TypeResolutionResult result, Type targetType)
        {
            double score = 0.0;

            // Exact match;
            if (result.ResolvedType == targetType)
                score += 0.4;

            // Type compatibility;
            score += CalculateTypeCompatibility(result.ResolvedType, targetType) * 0.3;

            // Confidence contribution;
            score += result.Confidence * 0.2;

            // Pattern quality;
            if (!string.IsNullOrEmpty(result.PatternUsed))
                score += 0.1;

            return Math.Min(1.0, score);
        }

        private double CalculateTypeCompatibility(Type sourceType, Type targetType)
        {
            // Type hierarchy check;
            if (targetType.IsAssignableFrom(sourceType))
                return 0.8;

            // Conversion possibility;
            try
            {
                var testValue = GetTestValue(sourceType);
                var conversion = ConvertToTypeAsync(testValue, targetType).Result;
                return conversion.Success ? 0.6 : 0.2;
            }
            catch
            {
                return 0.0;
            }
        }

        private double CalculateVariance(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count < 2)
                return 0.0;

            var mean = list.Average();
            var variance = list.Sum(v => Math.Pow(v - mean, 2)) / list.Count;
            return variance;
        }

        public async Task ClearCacheAsync()
        {
            _typeCache.Clear();
            await Task.CompletedTask;
            _logger.LogInformation("TypeResolver cache cleared");
        }

        private string GenerateCacheKey(string rawValue, string parameterName, Dictionary<string, object> context)
        {
            var keyParts = new List<string>
            {
                $"val:{rawValue?.GetHashCode():X8}",
                $"param:{parameterName?.GetHashCode():X8}"
            };

            if (context != null)
            {
                foreach (var kvp in context.OrderBy(k => k.Key))
                {
                    keyParts.Add($"{kvp.Key}:{kvp.Value?.GetHashCode():X8}");
                }
            }

            return string.Join("|", keyParts);
        }

        #region Helper Classes;
        private class TypeConversionRule;
        {
            public Type FromType { get; set; }
            public Type ToType { get; set; }
            public Func<object, object> Converter { get; set; }
            public double Confidence { get; set; }
        }

        private class PatternRegistry
        {
            private readonly Dictionary<string, TypePattern> _patterns = new Dictionary<string, TypePattern>();
            private readonly Dictionary<string, PatternStatistics> _statistics = new Dictionary<string, PatternStatistics>();
            private readonly object _lock = new object();

            public int TotalResolutions { get; private set; }
            public int SuccessfulResolutions { get; private set; }
            public double AverageConfidence { get; private set; }
            public double AverageProcessingTimeMs { get; private set; }

            public void Register(TypePattern pattern)
            {
                lock (_lock)
                {
                    _patterns[pattern.PatternId] = pattern;
                    _statistics[pattern.PatternId] = new PatternStatistics();
                }
            }

            public void UpdateOrAdd(string patternId, TypePattern pattern)
            {
                lock (_lock)
                {
                    _patterns[patternId] = pattern;
                    if (!_statistics.ContainsKey(patternId))
                    {
                        _statistics[patternId] = new PatternStatistics();
                    }
                }
            }

            public void RecordUsage(string patternId, bool success)
            {
                lock (_lock)
                {
                    if (_statistics.TryGetValue(patternId, out var stats))
                    {
                        stats.UsageCount++;
                        if (success) stats.SuccessCount++;
                        stats.LastUsed = DateTime.UtcNow;

                        TotalResolutions++;
                        if (success) SuccessfulResolutions++;
                    }
                }
            }

            public int GetUsageCount(string typeName)
            {
                lock (_lock)
                {
                    return _patterns.Values;
                        .Where(p => p.TargetType.Name == typeName)
                        .Sum(p => _statistics[p.PatternId].UsageCount);
                }
            }

            public Dictionary<string, int> GetTypeDistribution()
            {
                lock (_lock)
                {
                    return _patterns.Values;
                        .GroupBy(p => p.TargetType.Name)
                        .ToDictionary(g => g.Key, g => g.Sum(p => _statistics[p.PatternId].UsageCount));
                }
            }

            public Dictionary<string, double> GetPatternEffectiveness()
            {
                lock (_lock)
                {
                    return _statistics;
                        .Where(kvp => kvp.Value.UsageCount > 0)
                        .ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value.SuccessCount / (double)kvp.Value.UsageCount);
                }
            }

            public Dictionary<string, int> GetErrorDistribution()
            {
                lock (_lock)
                {
                    return _statistics;
                        .Where(kvp => kvp.Value.UsageCount > 0)
                        .ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value.UsageCount - kvp.Value.SuccessCount);
                }
            }

            private class PatternStatistics;
            {
                public int UsageCount { get; set; }
                public int SuccessCount { get; set; }
                public DateTime LastUsed { get; set; } = DateTime.UtcNow;
            }
        }

        private class TypeCache;
        {
            private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
            private readonly TimeSpan _defaultTtl;
            private readonly object _lock = new object();
            private readonly int _maxSize = 1000;

            public TypeCache(TimeSpan defaultTtl)
            {
                _defaultTtl = defaultTtl;
            }

            public async Task<TypeResolutionResult> GetOrCreateAsync(string key, Func<Task<TypeResolutionResult>> factory)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
                    {
                        entry.LastAccessed = DateTime.UtcNow;
                        return entry.Value;
                    }
                }

                var value = await factory();

                lock (_lock)
                {
                    // Cache size kontrolü;
                    if (_cache.Count >= _maxSize)
                    {
                        var oldest = _cache.OrderBy(e => e.Value.LastAccessed).First();
                        _cache.Remove(oldest.Key);
                    }

                    _cache[key] = new CacheEntry
                    {
                        Value = value,
                        ExpiresAt = DateTime.UtcNow.Add(_defaultTtl),
                        LastAccessed = DateTime.UtcNow;
                    };
                }

                return value;
            }

            public void Clear()
            {
                lock (_lock)
                {
                    _cache.Clear();
                }
            }

            private class CacheEntry
            {
                public TypeResolutionResult Value { get; set; }
                public DateTime ExpiresAt { get; set; }
                public DateTime LastAccessed { get; set; }
                public bool IsExpired => DateTime.UtcNow > ExpiresAt;
            }
        }

        private class MLTypePredictor;
        {
            private readonly Random _random = new Random();

            public string ModelVersion => "1.0.0";

            public async Task<List<TypeCandidate>> PredictAsync(string rawValue, Dictionary<string, object> context)
            {
                // Basit ML simülasyonu;
                await Task.Delay(10); // Simüle edilmiş ML işleme süresi;

                var candidates = new List<TypeCandidate>();

                // Basit kurallar;
                if (rawValue.Contains("@") && rawValue.Contains("."))
                {
                    candidates.Add(new TypeCandidate;
                    {
                        Type = typeof(string),
                        TypeName = "string",
                        Confidence = 0.85,
                        ConvertedValue = rawValue,
                        SupportingPatterns = new List<string> { "ML_Email" }
                    });
                }

                if (rawValue.StartsWith("http"))
                {
                    candidates.Add(new TypeCandidate;
                    {
                        Type = typeof(string),
                        TypeName = "string",
                        Confidence = 0.90,
                        ConvertedValue = rawValue,
                        SupportingPatterns = new List<string> { "ML_Url" }
                    });
                }

                // Rastgele confidence;
                if (_random.NextDouble() > 0.5)
                {
                    candidates.Add(new TypeCandidate;
                    {
                        Type = typeof(int),
                        TypeName = "int",
                        Confidence = 0.7 + (_random.NextDouble() * 0.2),
                        ConvertedValue = _random.Next(1000),
                        SupportingPatterns = new List<string> { "ML_Numeric" }
                    });
                }

                return candidates.OrderByDescending(c => c.Confidence).ToList();
            }

            public async Task<TypeCandidate> PredictBestAsync(string rawValue, Dictionary<string, object> context)
            {
                var predictions = await PredictAsync(rawValue, context);
                return predictions.FirstOrDefault();
            }

            public async Task TrainAsync(List<UnknownTypeSample> samples)
            {
                // ML model training simulation;
                await Task.Delay(100);
                // Gerçek implementasyonda ML model güncellemesi yapılır;
            }
        }
        #endregion;

        #region Error Codes;
        public static class ErrorCodes;
        {
            public const string TypeResolutionFailed = "TYPES_001";
            public const string ConversionFailed = "TYPES_002";
            public const string ValidationFailed = "TYPES_003";
            public const string HierarchyAnalysisFailed = "TYPES_004";
            public const string ConflictResolutionFailed = "TYPES_005";
            public const string PatternExtractionFailed = "TYPES_006";
        }
        #endregion;

        #region Exceptions;
        public class TypeResolutionException : Exception
        {
            public string ErrorCode { get; }

            public TypeResolutionException(string message, Exception innerException, string errorCode)
                : base(message, innerException)
            {
                ErrorCode = errorCode;
            }

            public TypeResolutionException(string message, string errorCode)
                : base(message)
            {
                ErrorCode = errorCode;
            }
        }
        #endregion;

        #region IDisposable;
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
                    ClearCacheAsync().Wait(TimeSpan.FromSeconds(5));
                    _patternRegistry?.GetType()?.GetMethod("Dispose")?.Invoke(_patternRegistry, null);
                }
                _disposed = true;
            }
        }

        ~TypeResolver()
        {
            Dispose(false);
        }
        #endregion;
    }
}
