// NEDA.Brain/IntentRecognition/ActionExtractor/ActionParser.cs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Brain.IntentRecognition.ActionExtractor;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NLP_Engine.EntityRecognition;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.MetricsCollector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Brain.IntentRecognition.ActionExtractor;
{
    /// <summary>
    /// NEDA sisteminin eylem ayrıştırma ve komut analiz motoru.
    /// Doğal dil ifadelerinden eylemleri, parametreleri ve niyetleri çıkarır.
    /// </summary>
    public class ActionParser : IActionParser, IDisposable;
    {
        private readonly ILogger<ActionParser> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly ISyntaxAnalyzer _syntaxAnalyzer;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IEntityExtractor _entityExtractor;
        private readonly IMemoryRecall _memoryRecall;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IMetricsCollector _metricsCollector;

        // Action parsing models and caches;
        private readonly Dictionary<string, ActionPattern> _actionPatterns;
        private readonly Dictionary<string, VerbTemplate> _verbTemplates;
        private readonly Dictionary<string, ActionGrammar> _actionGrammars;
        private readonly LRUCache<string, ParsedAction> _parsingCache;

        // Configuration;
        private ActionParserConfig _config;
        private readonly ActionParserOptions _options;
        private bool _isInitialized;
        private readonly object _lockObject = new object();
        private readonly SemaphoreSlim _parsingSemaphore;

        // Grammar and syntax components;
        private readonly Dictionary<Language, GrammarRules> _languageGrammars;
        private readonly Dictionary<string, List<VerbConjugation>> _verbConjugations;

        // Events;
        public event EventHandler<ActionParsingStartedEventArgs> OnParsingStarted;
        public event EventHandler<ActionParsingProgressEventArgs> OnParsingProgress;
        public event EventHandler<ActionParsingCompletedEventArgs> OnParsingCompleted;
        public event EventHandler<AmbiguityDetectedEventArgs> OnAmbiguityDetected;
        public event EventHandler<ActionPatternRecognizedEventArgs> OnActionPatternRecognized;

        /// <summary>
        /// ActionParser constructor;
        /// </summary>
        public ActionParser(
            ILogger<ActionParser> logger,
            IServiceProvider serviceProvider,
            IKnowledgeBase knowledgeBase,
            ISyntaxAnalyzer syntaxAnalyzer,
            ISemanticAnalyzer semanticAnalyzer,
            IEntityExtractor entityExtractor,
            IMemoryRecall memoryRecall,
            IDiagnosticTool diagnosticTool,
            IMetricsCollector metricsCollector,
            IOptions<ActionParserOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _syntaxAnalyzer = syntaxAnalyzer ?? throw new ArgumentNullException(nameof(syntaxAnalyzer));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _entityExtractor = entityExtractor ?? throw new ArgumentNullException(nameof(entityExtractor));
            _memoryRecall = memoryRecall ?? throw new ArgumentNullException(nameof(memoryRecall));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _options = options?.Value ?? new ActionParserOptions();

            // Initialize storage;
            _actionPatterns = new Dictionary<string, ActionPattern>();
            _verbTemplates = new Dictionary<string, VerbTemplate>();
            _actionGrammars = new Dictionary<string, ActionGrammar>();
            _parsingCache = new LRUCache<string, ParsedAction>(capacity: 1000);

            // Default configuration;
            _config = new ActionParserConfig();
            _isInitialized = false;

            // Initialize language grammars;
            _languageGrammars = new Dictionary<Language, GrammarRules>();
            _verbConjugations = new Dictionary<string, List<VerbConjugation>>();

            // Concurrency control;
            _parsingSemaphore = new SemaphoreSlim(
                _config.MaxConcurrentParsings,
                _config.MaxConcurrentParsings);

            _logger.LogInformation("ActionParser initialized successfully");
        }

        /// <summary>
        /// ActionParser'ı belirtilen konfigürasyon ile başlatır;
        /// </summary>
        public async Task InitializeAsync(ActionParserConfig config = null)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("ActionParser already initialized");
                    return;
                }

                _logger.LogInformation("Initializing ActionParser...");

                _config = config ?? new ActionParserConfig();

                // Load action patterns;
                await LoadActionPatternsAsync();

                // Load verb templates;
                await LoadVerbTemplatesAsync();

                // Load action grammars;
                await LoadActionGrammarsAsync();

                // Load language grammars;
                await LoadLanguageGrammarsAsync();

                // Load verb conjugations;
                await LoadVerbConjugationsAsync();

                // Initialize NLP components;
                await _syntaxAnalyzer.InitializeAsync();
                await _semanticAnalyzer.InitializeAsync();
                await _entityExtractor.InitializeAsync();

                // Load domain-specific action models;
                await LoadDomainActionModelsAsync();

                // Warm up parsing models;
                await WarmUpModelsAsync();

                _isInitialized = true;

                _logger.LogInformation("ActionParser initialized successfully with {PatternCount} patterns and {VerbCount} verb templates",
                    _actionPatterns.Count, _verbTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ActionParser");
                throw new ActionParserException("ActionParser initialization failed", ex);
            }
        }

        /// <summary>
        /// Eylem pattern'lerini yükler;
        /// </summary>
        private async Task LoadActionPatternsAsync()
        {
            try
            {
                // Load default action patterns;
                var defaultPatterns = GetDefaultActionPatterns();
                foreach (var pattern in defaultPatterns)
                {
                    _actionPatterns[pattern.Id] = pattern;
                }

                // Load patterns from knowledge base;
                var kbPatterns = await _knowledgeBase.GetActionPatternsAsync();
                foreach (var pattern in kbPatterns)
                {
                    if (!_actionPatterns.ContainsKey(pattern.Id))
                    {
                        _actionPatterns[pattern.Id] = pattern;
                    }
                }

                // Load learned patterns from memory;
                var learnedPatterns = await _memoryRecall.GetActionPatternsAsync();
                foreach (var pattern in learnedPatterns)
                {
                    var patternId = $"LEARNED_{pattern.Id}";
                    _actionPatterns[patternId] = pattern;
                }

                _logger.LogDebug("Loaded {PatternCount} action patterns", _actionPatterns.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load action patterns");
                throw;
            }
        }

        /// <summary>
        /// Varsayılan eylem pattern'lerini döndürür;
        /// </summary>
        private IEnumerable<ActionPattern> GetDefaultActionPatterns()
        {
            return new List<ActionPattern>
            {
                new ActionPattern;
                {
                    Id = "PATTERN_IMPERATIVE_COMMAND",
                    Name = "Imperative Command Pattern",
                    Description = "Pattern for imperative commands (Verb + Object + Modifiers)",
                    Template = "[VERB] [OBJECT] [MODIFIERS?]",
                    Example = "Open the file quickly",
                    Language = Language.English,
                    Priority = PatternPriority.High,
                    Confidence = 0.9,
                    IsActive = true;
                },
                new ActionPattern;
                {
                    Id = "PATTERN_REQUEST_ACTION",
                    Name = "Request Action Pattern",
                    Description = "Pattern for polite requests (Could you + Verb + Object)",
                    Template = "[COULD/YOU] [VERB] [OBJECT] [PLEASE?]",
                    Example = "Could you send me the report please",
                    Language = Language.English,
                    Priority = PatternPriority.Medium,
                    Confidence = 0.8,
                    IsActive = true;
                },
                new ActionPattern;
                {
                    Id = "PATTERN_QUESTION_ACTION",
                    Name = "Question Action Pattern",
                    Description = "Pattern for action questions (What/How + to + Verb)",
                    Template = "[WHAT/HOW] [TO] [VERB] [OBJECT?]",
                    Example = "How to create a new project",
                    Language = Language.English,
                    Priority = PatternPriority.Medium,
                    Confidence = 0.85,
                    IsActive = true;
                },
                new ActionPattern;
                {
                    Id = "PATTERN_CONDITIONAL_ACTION",
                    Name = "Conditional Action Pattern",
                    Description = "Pattern for conditional actions (If + Condition + then + Action)",
                    Template = "[IF] [CONDITION] [THEN] [VERB] [OBJECT]",
                    Example = "If the file exists then delete it",
                    Language = Language.English,
                    Priority = PatternPriority.Medium,
                    Confidence = 0.75,
                    IsActive = true;
                },
                new ActionPattern;
                {
                    Id = "PATTERN_SEQUENTIAL_ACTION",
                    Name = "Sequential Action Pattern",
                    Description = "Pattern for sequential actions (First + Action1 + then + Action2)",
                    Template = "[FIRST/THEN] [VERB1] [OBJECT1] [THEN/AND] [VERB2] [OBJECT2]",
                    Example = "First compile the code then run the tests",
                    Language = Language.English,
                    Priority = PatternPriority.Medium,
                    Confidence = 0.8,
                    IsActive = true;
                },
                new ActionPattern;
                {
                    Id = "PATTERN_COMPOUND_ACTION",
                    Name = "Compound Action Pattern",
                    Description = "Pattern for compound actions (Action1 + and/or + Action2)",
                    Template = "[VERB1] [OBJECT1] [AND/OR] [VERB2] [OBJECT2]",
                    Example = "Save the document and close the window",
                    Language = Language.English,
                    Priority = PatternPriority.Low,
                    Confidence = 0.7,
                    IsActive = true;
                }
            };
        }

        /// <summary>
        /// Fiil şablonlarını yükler;
        /// </summary>
        private async Task LoadVerbTemplatesAsync()
        {
            try
            {
                // Load default verb templates;
                var defaultTemplates = GetDefaultVerbTemplates();
                foreach (var template in defaultTemplates)
                {
                    _verbTemplates[template.Verb] = template;
                }

                // Load templates from knowledge base;
                var kbTemplates = await _knowledgeBase.GetVerbTemplatesAsync();
                foreach (var template in kbTemplates)
                {
                    if (!_verbTemplates.ContainsKey(template.Verb))
                    {
                        _verbTemplates[template.Verb] = template;
                    }
                }

                _logger.LogDebug("Loaded {TemplateCount} verb templates", _verbTemplates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load verb templates");
                throw;
            }
        }

        /// <summary>
        /// Varsayılan fiil şablonlarını döndürür;
        /// </summary>
        private IEnumerable<VerbTemplate> GetDefaultVerbTemplates()
        {
            return new List<VerbTemplate>
            {
                new VerbTemplate;
                {
                    Verb = "create",
                    BaseForm = "create",
                    PastTense = "created",
                    PresentParticiple = "creating",
                    ThirdPerson = "creates",
                    ActionType = ActionType.Creation,
                    RequiredObjects = new List<string> { "object" },
                    OptionalObjects = new List<string> { "location", "properties" },
                    Prepositions = new List<string> { "in", "with", "as" },
                    Example = "create a new file in the documents folder",
                    Confidence = 0.9;
                },
                new VerbTemplate;
                {
                    Verb = "delete",
                    BaseForm = "delete",
                    PastTense = "deleted",
                    PresentParticiple = "deleting",
                    ThirdPerson = "deletes",
                    ActionType = ActionType.Deletion,
                    RequiredObjects = new List<string> { "object" },
                    OptionalObjects = new List<string> { "permanently", "from" },
                    Prepositions = new List<string> { "from", "in" },
                    Example = "delete the temporary file from the system",
                    Confidence = 0.95;
                },
                new VerbTemplate;
                {
                    Verb = "open",
                    BaseForm = "open",
                    PastTense = "opened",
                    PresentParticiple = "opening",
                    ThirdPerson = "opens",
                    ActionType = ActionType.Access,
                    RequiredObjects = new List<string> { "object" },
                    OptionalObjects = new List<string> { "in", "with", "for" },
                    Prepositions = new List<string> { "in", "with", "for" },
                    Example = "open the document with the editor",
                    Confidence = 0.9;
                },
                new VerbTemplate;
                {
                    Verb = "save",
                    BaseForm = "save",
                    PastTense = "saved",
                    PresentParticiple = "saving",
                    ThirdPerson = "saves",
                    ActionType = ActionType.Storage,
                    RequiredObjects = new List<string> { "object" },
                    OptionalObjects = new List<string> { "as", "to", "in" },
                    Prepositions = new List<string> { "as", "to", "in" },
                    Example = "save the file as backup.txt",
                    Confidence = 0.85;
                },
                new VerbTemplate;
                {
                    Verb = "send",
                    BaseForm = "send",
                    PastTense = "sent",
                    PresentParticiple = "sending",
                    ThirdPerson = "sends",
                    ActionType = ActionType.Communication,
                    RequiredObjects = new List<string> { "object", "recipient" },
                    OptionalObjects = new List<string> { "via", "with", "attachments" },
                    Prepositions = new List<string> { "to", "via", "with" },
                    Example = "send the email to the team with attachments",
                    Confidence = 0.8;
                },
                new VerbTemplate;
                {
                    Verb = "analyze",
                    BaseForm = "analyze",
                    PastTense = "analyzed",
                    PresentParticiple = "analyzing",
                    ThirdPerson = "analyzes",
                    ActionType = ActionType.Analysis,
                    RequiredObjects = new List<string> { "object" },
                    OptionalObjects = new List<string> { "for", "using", "with" },
                    Prepositions = new List<string> { "for", "using", "with" },
                    Example = "analyze the data for patterns using AI",
                    Confidence = 0.75;
                }
            };
        }

        /// <summary>
        /// Eylem dilbilgisi kurallarını yükler;
        /// </summary>
        private async Task LoadActionGrammarsAsync()
        {
            try
            {
                var grammars = new List<ActionGrammar>
                {
                    new ActionGrammar;
                    {
                        Id = "GRAMMAR_ENGLISH_ACTION",
                        Name = "English Action Grammar",
                        Language = Language.English,
                        Rules = new List<GrammarRule>
                        {
                            new GrammarRule;
                            {
                                Name = "Imperative Sentence",
                                Pattern = "VERB [DETERMINER?] NOUN [PREPOSITIONAL_PHRASE?] [ADVERB?]",
                                Description = "Standard imperative command structure",
                                Priority = 1;
                            },
                            new GrammarRule;
                            {
                                Name = "Request Sentence",
                                Pattern = "[MODAL] [PRONOUN] VERB [DETERMINER?] NOUN [PLEASE?]",
                                Description = "Polite request structure",
                                Priority = 2;
                            },
                            new GrammarRule;
                            {
                                Name = "Question Sentence",
                                Pattern = "[QUESTION_WORD] [TO] VERB [DETERMINER?] NOUN",
                                Description = "Question-based action structure",
                                Priority = 3;
                            }
                        },
                        IsActive = true;
                    }
                };

                foreach (var grammar in grammars)
                {
                    _actionGrammars[grammar.Id] = grammar;
                }

                _logger.LogDebug("Loaded {GrammarCount} action grammars", _actionGrammars.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load action grammars");
                throw;
            }
        }

        /// <summary>
        /// Dil dilbilgisi kurallarını yükler;
        /// </summary>
        private async Task LoadLanguageGrammarsAsync()
        {
            try
            {
                // English grammar rules;
                var englishGrammar = new GrammarRules;
                {
                    Language = Language.English,
                    SentenceStructures = new List<SentenceStructure>
                    {
                        new SentenceStructure { Pattern = "SVO", Description = "Subject-Verb-Object" },
                        new SentenceStructure { Pattern = "SVO+", Description = "Subject-Verb-Object with modifiers" },
                        new SentenceStructure { Pattern = "VSO", Description = "Verb-Subject-Object (questions)" }
                    },
                    VerbPositions = new List<int> { 0, 1, 2 },
                    ObjectPositions = new List<int> { 2, 3, 4 },
                    ModifierPositions = new List<int> { 1, 3, 5 }
                };

                _languageGrammars[Language.English] = englishGrammar;

                _logger.LogDebug("Loaded language grammars for {LanguageCount} languages", _languageGrammars.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load language grammars");
                throw;
            }
        }

        /// <summary>
        /// Fiil çekimlerini yükler;
        /// </summary>
        private async Task LoadVerbConjugationsAsync()
        {
            try
            {
                // Common verb conjugations for English;
                var conjugations = new Dictionary<string, List<VerbConjugation>>
                {
                    ["create"] = new List<VerbConjugation>
                    {
                        new VerbConjugation { Tense = VerbTense.Present, Person = VerbPerson.FirstSingular, Form = "create" },
                        new VerbConjugation { Tense = VerbTense.Present, Person = VerbPerson.ThirdSingular, Form = "creates" },
                        new VerbConjugation { Tense = VerbTense.Past, Person = VerbPerson.Any, Form = "created" },
                        new VerbConjugation { Tense = VerbTense.PresentParticiple, Person = VerbPerson.Any, Form = "creating" }
                    },
                    ["open"] = new List<VerbConjugation>
                    {
                        new VerbConjugation { Tense = VerbTense.Present, Person = VerbPerson.FirstSingular, Form = "open" },
                        new VerbConjugation { Tense = VerbTense.Present, Person = VerbPerson.ThirdSingular, Form = "opens" },
                        new VerbConjugation { Tense = VerbTense.Past, Person = VerbPerson.Any, Form = "opened" },
                        new VerbConjugation { Tense = VerbTense.PresentParticiple, Person = VerbPerson.Any, Form = "opening" }
                    },
                    ["delete"] = new List<VerbConjugation>
                    {
                        new VerbConjugation { Tense = VerbTense.Present, Person = VerbPerson.FirstSingular, Form = "delete" },
                        new VerbConjugation { Tense = VerbTense.Present, Person = VerbPerson.ThirdSingular, Form = "deletes" },
                        new VerbConjugation { Tense = VerbTense.Past, Person = VerbPerson.Any, Form = "deleted" },
                        new VerbConjugation { Tense = VerbTense.PresentParticiple, Person = VerbPerson.Any, Form = "deleting" }
                    }
                };

                foreach (var kvp in conjugations)
                {
                    _verbConjugations[kvp.Key] = kvp.Value;
                }

                _logger.LogDebug("Loaded conjugations for {VerbCount} verbs", _verbConjugations.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load verb conjugations");
                throw;
            }
        }

        /// <summary>
        /// Domain-specific eylem modellerini yükler;
        /// </summary>
        private async Task LoadDomainActionModelsAsync()
        {
            try
            {
                var domainModels = new List<DomainActionModel>
                {
                    new DomainActionModel;
                    {
                        Domain = "FileSystem",
                        Verbs = new List<string> { "create", "delete", "open", "save", "copy", "move", "rename" },
                        Objects = new List<string> { "file", "folder", "directory", "document", "archive" },
                        Patterns = new List<string> { "PATTERN_IMPERATIVE_COMMAND", "PATTERN_REQUEST_ACTION" }
                    },
                    new DomainActionModel;
                    {
                        Domain = "SystemControl",
                        Verbs = new List<string> { "start", "stop", "restart", "shutdown", "install", "update" },
                        Objects = new List<string> { "service", "process", "application", "system", "update" },
                        Patterns = new List<string> { "PATTERN_IMPERATIVE_COMMAND", "PATTERN_CONDITIONAL_ACTION" }
                    },
                    new DomainActionModel;
                    {
                        Domain = "DataAnalysis",
                        Verbs = new List<string> { "analyze", "process", "extract", "transform", "load", "query" },
                        Objects = new List<string> { "data", "dataset", "report", "analysis", "results" },
                        Patterns = new List<string> { "PATTERN_QUESTION_ACTION", "PATTERN_SEQUENTIAL_ACTION" }
                    }
                };

                foreach (var model in domainModels)
                {
                    await _knowledgeBase.StoreDomainActionModelAsync(model);
                }

                _logger.LogDebug("Loaded {ModelCount} domain action models", domainModels.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load domain action models");
                throw;
            }
        }

        /// <summary>
        /// Modelleri ısıtır;
        /// </summary>
        private async Task WarmUpModelsAsync()
        {
            try
            {
                _logger.LogDebug("Warming up action parsing models...");

                var warmupTasks = new List<Task>
                {
                    WarmUpParsingCacheAsync(),
                    WarmUpVerbRecognitionAsync(),
                    WarmUpPatternMatchingAsync()
                };

                await Task.WhenAll(warmupTasks);

                _logger.LogDebug("Action parsing models warmed up successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warm up models");
                // Non-critical, continue;
            }
        }

        /// <summary>
        /// Eylemleri ayrıştırır;
        /// </summary>
        public async Task<ParsedAction> ParseActionAsync(
            ActionParsingRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                // Check cache first;
                var cacheKey = GenerateCacheKey(request);
                if (_config.EnableCaching && _parsingCache.TryGet(cacheKey, out var cachedAction))
                {
                    _logger.LogDebug("Returning cached parsed action for request: {RequestId}", request.Id);
                    return cachedAction;
                }

                // Acquire semaphore for concurrency control;
                await _parsingSemaphore.WaitAsync(cancellationToken);

                try
                {
                    var parsingId = Guid.NewGuid().ToString();

                    // Event: Parsing started;
                    OnParsingStarted?.Invoke(this, new ActionParsingStartedEventArgs;
                    {
                        ParsingId = parsingId,
                        Request = request,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Starting action parsing {ParsingId} for request: {RequestId}",
                        parsingId, request.Id);

                    var startTime = DateTime.UtcNow;

                    // Progress report;
                    await ReportProgressAsync(parsingId, 0, "Initializing action parsing", request.Context);

                    // 1. Text preprocessing;
                    var preprocessedText = await PreprocessTextAsync(request.Text, request.Context, cancellationToken);
                    await ReportProgressAsync(parsingId, 10, "Text preprocessing completed", request.Context);

                    // 2. Syntax analysis;
                    var syntaxTree = await _syntaxAnalyzer.AnalyzeAsync(preprocessedText, cancellationToken);
                    await ReportProgressAsync(parsingId, 20, "Syntax analysis completed", request.Context);

                    // 3. Semantic analysis;
                    var semanticAnalysis = await _semanticAnalyzer.AnalyzeAsync(preprocessedText, cancellationToken);
                    await ReportProgressAsync(parsingId, 30, "Semantic analysis completed", request.Context);

                    // 4. Entity extraction;
                    var entities = await _entityExtractor.ExtractEntitiesAsync(preprocessedText, cancellationToken);
                    await ReportProgressAsync(parsingId, 40, "Entity extraction completed", request.Context);

                    // 5. Verb extraction;
                    var verbExtraction = await ExtractVerbsAsync(syntaxTree, semanticAnalysis, cancellationToken);
                    await ReportProgressAsync(parsingId, 50, "Verb extraction completed", request.Context);

                    // 6. Object extraction;
                    var objectExtraction = await ExtractObjectsAsync(syntaxTree, semanticAnalysis, entities, cancellationToken);
                    await ReportProgressAsync(parsingId, 60, "Object extraction completed", request.Context);

                    // 7. Parameter extraction;
                    var parameterExtraction = await ExtractParametersAsync(
                        syntaxTree,
                        semanticAnalysis,
                        entities,
                        verbExtraction,
                        objectExtraction,
                        cancellationToken);
                    await ReportProgressAsync(parsingId, 70, "Parameter extraction completed", request.Context);

                    // 8. Pattern matching;
                    var patternMatching = await MatchActionPatternsAsync(
                        preprocessedText,
                        syntaxTree,
                        semanticAnalysis,
                        verbExtraction,
                        objectExtraction,
                        cancellationToken);
                    await ReportProgressAsync(parsingId, 80, "Pattern matching completed", request.Context);

                    // 9. Action composition;
                    var composedAction = await ComposeActionAsync(
                        verbExtraction,
                        objectExtraction,
                        parameterExtraction,
                        patternMatching,
                        request.Context,
                        cancellationToken);
                    await ReportProgressAsync(parsingId, 90, "Action composition completed", request.Context);

                    // 10. Build parsed action;
                    var parsedAction = await BuildParsedActionAsync(
                        parsingId,
                        request,
                        preprocessedText,
                        syntaxTree,
                        semanticAnalysis,
                        verbExtraction,
                        objectExtraction,
                        parameterExtraction,
                        patternMatching,
                        composedAction,
                        startTime,
                        cancellationToken);

                    // Update cache;
                    if (_config.EnableCaching && parsedAction.Confidence >= _config.CacheThreshold)
                    {
                        _parsingCache.Put(cacheKey, parsedAction);
                    }

                    // Check for pattern recognition;
                    await CheckPatternRecognitionAsync(patternMatching, parsedAction, cancellationToken);

                    // Event: Parsing completed;
                    OnParsingCompleted?.Invoke(this, new ActionParsingCompletedEventArgs;
                    {
                        ParsingId = parsingId,
                        Request = request,
                        ParsedAction = parsedAction,
                        Timestamp = DateTime.UtcNow;
                    });

                    await ReportProgressAsync(parsingId, 100, "Action parsing completed", request.Context);

                    _logger.LogInformation(
                        "Completed action parsing {ParsingId} in {ParsingTime}ms. Confidence: {Confidence}",
                        parsingId, parsedAction.ParsingTime.TotalMilliseconds, parsedAction.Confidence);

                    return parsedAction;
                }
                finally
                {
                    _parsingSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Action parsing cancelled for request: {RequestId}", request.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in action parsing for request: {RequestId}", request.Id);
                throw new ActionParsingException($"Action parsing failed for request: {request.Id}", ex);
            }
        }

        /// <summary>
        /// Metni ön işler;
        /// </summary>
        private async Task<string> PreprocessTextAsync(
            string text,
            ParsingContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                    return string.Empty;

                var preprocessed = text;

                // 1. Trim and normalize whitespace;
                preprocessed = preprocessed.Trim();
                preprocessed = Regex.Replace(preprocessed, @"\s+", " ");

                // 2. Lowercase (for case-insensitive matching)
                if (!_options.CaseSensitive)
                {
                    preprocessed = preprocessed.ToLowerInvariant();
                }

                // 3. Remove extra punctuation (keep sentence structure)
                preprocessed = Regex.Replace(preprocessed, @"[^\w\s.,!?;:()\[\]{}""'-]", " ");

                // 4. Expand contractions;
                preprocessed = await ExpandContractionsAsync(preprocessed, cancellationToken);

                // 5. Correct common spelling errors;
                if (_options.EnableSpellCheck)
                {
                    preprocessed = await CorrectSpellingAsync(preprocessed, cancellationToken);
                }

                // 6. Normalize numbers and dates;
                preprocessed = await NormalizeNumbersAndDatesAsync(preprocessed, cancellationToken);

                // 7. Remove stop words if configured;
                if (_options.RemoveStopWords)
                {
                    preprocessed = await RemoveStopWordsAsync(preprocessed, context?.Language ?? Language.English, cancellationToken);
                }

                return preprocessed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preprocessing text");
                return text; // Return original text on error;
            }
        }

        /// <summary>
        /// Kısaltmaları genişletir;
        /// </summary>
        private async Task<string> ExpandContractionsAsync(string text, CancellationToken cancellationToken)
        {
            var contractions = new Dictionary<string, string>
            {
                ["can't"] = "cannot",
                ["won't"] = "will not",
                ["don't"] = "do not",
                ["doesn't"] = "does not",
                ["isn't"] = "is not",
                ["aren't"] = "are not",
                ["wasn't"] = "was not",
                ["weren't"] = "were not",
                ["haven't"] = "have not",
                ["hasn't"] = "has not",
                ["hadn't"] = "had not",
                ["wouldn't"] = "would not",
                ["couldn't"] = "could not",
                ["shouldn't"] = "should not",
                ["i'm"] = "i am",
                ["you're"] = "you are",
                ["he's"] = "he is",
                ["she's"] = "she is",
                ["it's"] = "it is",
                ["we're"] = "we are",
                ["they're"] = "they are",
                ["i've"] = "i have",
                ["you've"] = "you have",
                ["we've"] = "we have",
                ["they've"] = "they have",
                ["i'd"] = "i would",
                ["you'd"] = "you would",
                ["he'd"] = "he would",
                ["she'd"] = "she would",
                ["we'd"] = "we would",
                ["they'd"] = "they would",
                ["i'll"] = "i will",
                ["you'll"] = "you will",
                ["he'll"] = "he will",
                ["she'll"] = "she will",
                ["we'll"] = "we will",
                ["they'll"] = "they will"
            };

            var result = text;
            foreach (var contraction in contractions)
            {
                result = Regex.Replace(result, $@"\b{contraction.Key}\b", contraction.Value, RegexOptions.IgnoreCase);
            }

            await Task.CompletedTask;
            return result;
        }

        /// <summary>
        /// Fiilleri çıkarır;
        /// </summary>
        private async Task<VerbExtractionResult> ExtractVerbsAsync(
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            CancellationToken cancellationToken)
        {
            var result = new VerbExtractionResult;
            {
                Verbs = new List<ExtractedVerb>(),
                PrimaryVerb = null,
                AuxiliaryVerbs = new List<ExtractedVerb>(),
                VerbPhrases = new List<VerbPhrase>()
            };

            try
            {
                // Extract verbs from syntax tree;
                var verbs = await ExtractVerbsFromSyntaxTreeAsync(syntaxTree, cancellationToken);

                // Extract verbs from semantic analysis;
                var semanticVerbs = await ExtractVerbsFromSemanticAnalysisAsync(semanticAnalysis, cancellationToken);

                // Merge and deduplicate verbs;
                var allVerbs = verbs.Union(semanticVerbs).DistinctBy(v => v.BaseForm).ToList();

                foreach (var verb in allVerbs)
                {
                    // Get verb template if available;
                    if (_verbTemplates.TryGetValue(verb.BaseForm, out var template))
                    {
                        verb.Template = template;
                        verb.ActionType = template.ActionType;
                        verb.Confidence *= template.Confidence;
                    }

                    // Determine if verb is primary or auxiliary;
                    if (IsPrimaryVerb(verb, syntaxTree))
                    {
                        result.PrimaryVerb = verb;
                    }
                    else if (IsAuxiliaryVerb(verb))
                    {
                        result.AuxiliaryVerbs.Add(verb);
                    }

                    result.Verbs.Add(verb);
                }

                // Identify verb phrases;
                result.VerbPhrases = await IdentifyVerbPhrasesAsync(result.Verbs, syntaxTree, cancellationToken);

                // Calculate overall confidence;
                result.Confidence = CalculateVerbExtractionConfidence(result.Verbs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting verbs");
                return result;
            }
        }

        /// <summary>
        /// Syntax ağacından fiilleri çıkarır;
        /// </summary>
        private async Task<List<ExtractedVerb>> ExtractVerbsFromSyntaxTreeAsync(
            SyntaxTree syntaxTree,
            CancellationToken cancellationToken)
        {
            var verbs = new List<ExtractedVerb>();

            try
            {
                // Traverse syntax tree to find verb nodes;
                var verbNodes = FindVerbNodes(syntaxTree.Root);

                foreach (var node in verbNodes)
                {
                    var verb = new ExtractedVerb;
                    {
                        Text = node.Text,
                        BaseForm = await GetBaseFormAsync(node.Text, cancellationToken),
                        Tense = DetermineVerbTense(node),
                        Person = DetermineVerbPerson(node),
                        IsNegated = IsVerbNegated(node),
                        Position = node.Position,
                        Confidence = CalculateVerbConfidence(node)
                    };

                    verbs.Add(verb);
                }

                return verbs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting verbs from syntax tree");
                return verbs;
            }
        }

        /// <summary>
        /// Fiilin kök formunu alır;
        /// </summary>
        private async Task<string> GetBaseFormAsync(string verb, CancellationToken cancellationToken)
        {
            try
            {
                // Check verb conjugations first;
                foreach (var kvp in _verbConjugations)
                {
                    var conjugation = kvp.Value.FirstOrDefault(c => c.Form.Equals(verb, StringComparison.OrdinalIgnoreCase));
                    if (conjugation != null)
                    {
                        return kvp.Key; // Return base form;
                    }
                }

                // Use stemming algorithm for unknown verbs;
                var stemmed = await StemVerbAsync(verb, cancellationToken);
                return stemmed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting base form for verb: {Verb}", verb);
                return verb.ToLowerInvariant();
            }
        }

        /// <summary>
        /// Nesneleri çıkarır;
        /// </summary>
        private async Task<ObjectExtractionResult> ExtractObjectsAsync(
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            List<ExtractedEntity> entities,
            CancellationToken cancellationToken)
        {
            var result = new ObjectExtractionResult;
            {
                DirectObjects = new List<ExtractedObject>(),
                IndirectObjects = new List<ExtractedObject>(),
                PrepositionalObjects = new List<PrepositionalObject>(),
                ObjectRelationships = new Dictionary<string, List<string>>()
            };

            try
            {
                // Extract objects from syntax tree;
                var syntaxObjects = await ExtractObjectsFromSyntaxTreeAsync(syntaxTree, cancellationToken);

                // Extract objects from entities;
                var entityObjects = await ExtractObjectsFromEntitiesAsync(entities, cancellationToken);

                // Extract objects from semantic analysis;
                var semanticObjects = await ExtractObjectsFromSemanticAnalysisAsync(semanticAnalysis, cancellationToken);

                // Merge all objects;
                var allObjects = syntaxObjects;
                    .Union(entityObjects)
                    .Union(semanticObjects)
                    .DistinctBy(o => o.Text)
                    .ToList();

                // Classify objects;
                foreach (var obj in allObjects)
                {
                    if (IsDirectObject(obj, syntaxTree))
                    {
                        result.DirectObjects.Add(obj);
                    }
                    else if (IsIndirectObject(obj, syntaxTree))
                    {
                        result.IndirectObjects.Add(obj);
                    }
                    else if (IsPrepositionalObject(obj, syntaxTree))
                    {
                        var prepObj = await ConvertToPrepositionalObjectAsync(obj, syntaxTree, cancellationToken);
                        if (prepObj != null)
                        {
                            result.PrepositionalObjects.Add(prepObj);
                        }
                    }
                }

                // Analyze object relationships;
                result.ObjectRelationships = await AnalyzeObjectRelationshipsAsync(
                    result.DirectObjects,
                    result.IndirectObjects,
                    result.PrepositionalObjects,
                    cancellationToken);

                // Calculate overall confidence;
                result.Confidence = CalculateObjectExtractionConfidence(allObjects);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting objects");
                return result;
            }
        }

        /// <summary>
        /// Parametreleri çıkarır;
        /// </summary>
        private async Task<ParameterExtractionResult> ExtractParametersAsync(
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            List<ExtractedEntity> entities,
            VerbExtractionResult verbExtraction,
            ObjectExtractionResult objectExtraction,
            CancellationToken cancellationToken)
        {
            var result = new ParameterExtractionResult;
            {
                Parameters = new Dictionary<string, ExtractedParameter>(),
                NamedParameters = new Dictionary<string, string>(),
                PositionalParameters = new List<ExtractedParameter>(),
                DefaultValues = new Dictionary<string, object>()
            };

            try
            {
                // Extract parameters based on verb template;
                if (verbExtraction.PrimaryVerb?.Template != null)
                {
                    var template = verbExtraction.PrimaryVerb.Template;

                    // Extract required parameters;
                    foreach (var requiredObject in template.RequiredObjects)
                    {
                        var parameter = await ExtractParameterForObjectAsync(
                            requiredObject,
                            objectExtraction,
                            syntaxTree,
                            cancellationToken);

                        if (parameter != null)
                        {
                            result.Parameters[requiredObject] = parameter;
                        }
                    }

                    // Extract optional parameters;
                    foreach (var optionalObject in template.OptionalObjects)
                    {
                        var parameter = await ExtractParameterForObjectAsync(
                            optionalObject,
                            objectExtraction,
                            syntaxTree,
                            cancellationToken);

                        if (parameter != null)
                        {
                            result.Parameters[optionalObject] = parameter;
                            parameter.IsOptional = true;
                        }
                    }

                    // Extract prepositional parameters;
                    foreach (var preposition in template.Prepositions)
                    {
                        var prepParameter = await ExtractPrepositionalParameterAsync(
                            preposition,
                            objectExtraction,
                            syntaxTree,
                            cancellationToken);

                        if (prepParameter != null)
                        {
                            result.Parameters[$"prep_{preposition}"] = prepParameter;
                        }
                    }
                }

                // Extract parameters from entities;
                var entityParameters = await ExtractParametersFromEntitiesAsync(entities, cancellationToken);
                foreach (var param in entityParameters)
                {
                    if (!result.Parameters.ContainsKey(param.Name))
                    {
                        result.Parameters[param.Name] = param;
                    }
                }

                // Extract parameters from syntax modifiers;
                var modifierParameters = await ExtractParametersFromModifiersAsync(syntaxTree, cancellationToken);
                foreach (var param in modifierParameters)
                {
                    if (!result.Parameters.ContainsKey(param.Name))
                    {
                        result.Parameters[param.Name] = param;
                    }
                }

                // Classify parameters;
                foreach (var kvp in result.Parameters)
                {
                    if (!string.IsNullOrEmpty(kvp.Value.Name))
                    {
                        result.NamedParameters[kvp.Value.Name] = kvp.Value.Value?.ToString() ?? string.Empty;
                    }
                    else;
                    {
                        result.PositionalParameters.Add(kvp.Value);
                    }
                }

                // Calculate overall confidence;
                result.Confidence = CalculateParameterExtractionConfidence(result.Parameters.Values.ToList());

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting parameters");
                return result;
            }
        }

        /// <summary>
        /// Eylem pattern'lerini eşleştirir;
        /// </summary>
        private async Task<PatternMatchingResult> MatchActionPatternsAsync(
            string text,
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            VerbExtractionResult verbExtraction,
            ObjectExtractionResult objectExtraction,
            CancellationToken cancellationToken)
        {
            var result = new PatternMatchingResult;
            {
                MatchedPatterns = new List<MatchedPattern>(),
                BestMatch = null,
                PatternConfidence = 0.0;
            };

            try
            {
                // Get applicable patterns;
                var applicablePatterns = _actionPatterns.Values;
                    .Where(p => p.IsActive &&
                               (p.Language == Language.English || p.Language == Language.Any))
                    .OrderByDescending(p => p.Priority)
                    .ThenByDescending(p => p.Confidence)
                    .Take(_config.MaxPatternsToMatch)
                    .ToList();

                foreach (var pattern in applicablePatterns)
                {
                    var match = await MatchPatternAsync(
                        pattern,
                        text,
                        syntaxTree,
                        semanticAnalysis,
                        verbExtraction,
                        objectExtraction,
                        cancellationToken);

                    if (match != null && match.Confidence >= _config.PatternThreshold)
                    {
                        result.MatchedPatterns.Add(match);

                        // Update best match;
                        if (result.BestMatch == null || match.Confidence > result.BestMatch.Confidence)
                        {
                            result.BestMatch = match;
                        }
                    }
                }

                // Calculate overall pattern confidence;
                if (result.MatchedPatterns.Any())
                {
                    result.PatternConfidence = result.MatchedPatterns.Max(p => p.Confidence);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error matching action patterns");
                return result;
            }
        }

        /// <summary>
        /// Pattern'i eşleştirir;
        /// </summary>
        private async Task<MatchedPattern> MatchPatternAsync(
            ActionPattern pattern,
            string text,
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            VerbExtractionResult verbExtraction,
            ObjectExtractionResult objectExtraction,
            CancellationToken cancellationToken)
        {
            try
            {
                var match = new MatchedPattern;
                {
                    PatternId = pattern.Id,
                    PatternName = pattern.Name,
                    Template = pattern.Template,
                    MatchedText = text,
                    Confidence = pattern.Confidence,
                    Components = new Dictionary<string, string>()
                };

                // Parse pattern template;
                var templateComponents = ParsePatternTemplate(pattern.Template);

                // Match components;
                foreach (var component in templateComponents)
                {
                    var componentValue = await MatchPatternComponentAsync(
                        component,
                        text,
                        syntaxTree,
                        semanticAnalysis,
                        verbExtraction,
                        objectExtraction,
                        cancellationToken);

                    if (!string.IsNullOrEmpty(componentValue))
                    {
                        match.Components[component] = componentValue;
                        match.Confidence *= 1.1; // Boost confidence for matched components;
                    }
                    else;
                    {
                        match.Confidence *= 0.9; // Reduce confidence for missing components;
                    }
                }

                // Check if pattern matches example structure;
                if (!string.IsNullOrEmpty(pattern.Example))
                {
                    var similarity = await CalculateTextSimilarityAsync(text, pattern.Example, cancellationToken);
                    match.Confidence *= (0.5 + (similarity * 0.5));
                }

                return match.Confidence >= _config.PatternThreshold ? match : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error matching pattern: {PatternId}", pattern.Id);
                return null;
            }
        }

        /// <summary>
        /// Eylemi oluşturur;
        /// </summary>
        private async Task<ComposedAction> ComposeActionAsync(
            VerbExtractionResult verbExtraction,
            ObjectExtractionResult objectExtraction,
            ParameterExtractionResult parameterExtraction,
            PatternMatchingResult patternMatching,
            ParsingContext context,
            CancellationToken cancellationToken)
        {
            var composedAction = new ComposedAction;
            {
                ActionId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                Components = new Dictionary<string, object>(),
                Relationships = new List<ActionRelationship>()
            };

            try
            {
                // Set primary verb;
                if (verbExtraction.PrimaryVerb != null)
                {
                    composedAction.PrimaryVerb = verbExtraction.PrimaryVerb;
                    composedAction.ActionType = verbExtraction.PrimaryVerb.ActionType;
                    composedAction.Components["verb"] = verbExtraction.PrimaryVerb;
                }

                // Set direct objects;
                if (objectExtraction.DirectObjects.Any())
                {
                    composedAction.DirectObjects = objectExtraction.DirectObjects;
                    composedAction.PrimaryObject = objectExtraction.DirectObjects.FirstOrDefault();
                    composedAction.Components["direct_objects"] = objectExtraction.DirectObjects;
                }

                // Set indirect objects;
                if (objectExtraction.IndirectObjects.Any())
                {
                    composedAction.IndirectObjects = objectExtraction.IndirectObjects;
                    composedAction.Components["indirect_objects"] = objectExtraction.IndirectObjects;
                }

                // Set parameters;
                if (parameterExtraction.Parameters.Any())
                {
                    composedAction.Parameters = parameterExtraction.Parameters;
                    composedAction.NamedParameters = parameterExtraction.NamedParameters;
                    composedAction.Components["parameters"] = parameterExtraction.Parameters;
                }

                // Set pattern;
                if (patternMatching.BestMatch != null)
                {
                    composedAction.Pattern = patternMatching.BestMatch;
                    composedAction.Components["pattern"] = patternMatching.BestMatch;
                }

                // Set auxiliary verbs;
                if (verbExtraction.AuxiliaryVerbs.Any())
                {
                    composedAction.AuxiliaryVerbs = verbExtraction.AuxiliaryVerbs;
                    composedAction.Components["auxiliary_verbs"] = verbExtraction.AuxiliaryVerbs;
                }

                // Set verb phrases;
                if (verbExtraction.VerbPhrases.Any())
                {
                    composedAction.VerbPhrases = verbExtraction.VerbPhrases;
                    composedAction.Components["verb_phrases"] = verbExtraction.VerbPhrases;
                }

                // Analyze action relationships;
                composedAction.Relationships = await AnalyzeActionRelationshipsAsync(
                    composedAction,
                    cancellationToken);

                // Calculate action completeness;
                composedAction.CompletenessScore = CalculateActionCompleteness(composedAction);

                // Determine action mood;
                composedAction.Mood = DetermineActionMood(composedAction, context);

                // Determine action tense;
                composedAction.Tense = DetermineActionTense(composedAction);

                return composedAction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error composing action");
                return composedAction;
            }
        }

        /// <summary>
        /// Ayrıştırılmış eylemi oluşturur;
        /// </summary>
        private async Task<ParsedAction> BuildParsedActionAsync(
            string parsingId,
            ActionParsingRequest request,
            string preprocessedText,
            SyntaxTree syntaxTree,
            SemanticAnalysis semanticAnalysis,
            VerbExtractionResult verbExtraction,
            ObjectExtractionResult objectExtraction,
            ParameterExtractionResult parameterExtraction,
            PatternMatchingResult patternMatching,
            ComposedAction composedAction,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            var parsedAction = new ParsedAction;
            {
                Id = parsingId,
                RequestId = request.Id,
                OriginalText = request.Text,
                PreprocessedText = preprocessedText,
                ComposedAction = composedAction,
                VerbExtraction = verbExtraction,
                ObjectExtraction = objectExtraction,
                ParameterExtraction = parameterExtraction,
                PatternMatching = patternMatching,
                SyntaxTree = syntaxTree,
                SemanticAnalysis = semanticAnalysis,
                ParsingTime = DateTime.UtcNow - startTime,
                Timestamp = DateTime.UtcNow,
                Language = request.Context?.Language ?? Language.English,
                Domain = request.Context?.Domain ?? "General",
                Metadata = new Dictionary<string, object>
                {
                    ["preprocessing_applied"] = true,
                    ["spell_check"] = _options.EnableSpellCheck,
                    ["stop_words_removed"] = _options.RemoveStopWords,
                    ["patterns_matched"] = patternMatching.MatchedPatterns.Count;
                }
            };

            // Calculate overall confidence;
            parsedAction.Confidence = CalculateOverallConfidence(
                verbExtraction,
                objectExtraction,
                parameterExtraction,
                patternMatching,
                composedAction);

            // Generate action summary;
            parsedAction.Summary = await GenerateActionSummaryAsync(parsedAction, cancellationToken);

            // Generate execution plan;
            parsedAction.ExecutionPlan = await GenerateExecutionPlanAsync(parsedAction, cancellationToken);

            // Check for ambiguities;
            parsedAction.Ambiguities = await DetectAmbiguitiesAsync(parsedAction, cancellationToken);

            return parsedAction;
        }

        /// <summary>
        /// Çoklu eylem ayrıştırması yapar;
        /// </summary>
        public async Task<BatchParsingResult> ParseActionsBatchAsync(
            List<ActionParsingRequest> requests,
            BatchParsingConfig config = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialization();

            if (requests == null || !requests.Any())
                throw new ArgumentException("Requests cannot be null or empty", nameof(requests));

            config ??= new BatchParsingConfig();

            try
            {
                _logger.LogInformation("Starting batch action parsing for {RequestCount} requests", requests.Count);

                var batchId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                var results = new List<ParsedAction>();
                var failedRequests = new List<FailedParsing>();

                // Process in batches;
                var batchSize = Math.Min(config.MaxBatchSize, _config.MaxConcurrentParsings);
                var batches = requests.Chunk(batchSize);

                foreach (var batch in batches)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var batchTasks = batch.Select(async request =>
                    {
                        try
                        {
                            var result = await ParseActionAsync(request, cancellationToken);
                            return (Success: true, Result: result, Request: request, Error: null as string);
                        }
                        catch (Exception ex)
                        {
                            return (Success: false, Result: null, Request: request, Error: ex.Message);
                        }
                    }).ToList();

                    var batchResults = await Task.WhenAll(batchTasks);

                    foreach (var batchResult in batchResults)
                    {
                        if (batchResult.Success)
                        {
                            results.Add(batchResult.Result);
                        }
                        else;
                        {
                            failedRequests.Add(new FailedParsing;
                            {
                                Request = batchResult.Request,
                                Error = batchResult.Error,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    }

                    _logger.LogDebug("Completed batch of {BatchSize} parsings", batch.Length);
                }

                var batchResult = new BatchParsingResult;
                {
                    BatchId = batchId,
                    TotalRequests = requests.Count,
                    SuccessfulParsings = results,
                    FailedParsings = failedRequests,
                    OverallStatistics = CalculateBatchStatistics(results),
                    ProcessingTime = DateTime.UtcNow - startTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation(
                    "Batch action parsing completed: {SuccessCount}/{TotalCount} successful",
                    results.Count, requests.Count);

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch action parsing");
                throw new ActionParsingException("Batch action parsing failed", ex);
            }
        }

        /// <summary>
        /// ActionParser istatistiklerini getirir;
        /// </summary>
        public async Task<ActionParserStatistics> GetStatisticsAsync()
        {
            ValidateInitialization();

            try
            {
                var stats = new ActionParserStatistics;
                {
                    TotalPatterns = _actionPatterns.Count,
                    ActivePatterns = _actionPatterns.Values.Count(p => p.IsActive),
                    VerbTemplates = _verbTemplates.Count,
                    CacheHitRate = CalculateCacheHitRate(),
                    AverageParsingTime = CalculateAverageParsingTime(),
                    TotalParsings = await GetTotalParsingsAsync(),
                    SuccessRate = await CalculateSuccessRateAsync(),
                    PatternUsage = GetPatternUsage(),
                    LanguageDistribution = GetLanguageDistribution(),
                    Uptime = DateTime.UtcNow - _startTime,
                    MemoryUsage = GC.GetTotalMemory(false),
                    CurrentLoad = _config.MaxConcurrentParsings - _parsingSemaphore.CurrentCount;
                };

                // Verb extraction statistics;
                stats.VerbExtractionStats = await GetVerbExtractionStatsAsync();

                // Pattern matching statistics;
                stats.PatternMatchingStats = await GetPatternMatchingStatsAsync();

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                throw;
            }
        }

        /// <summary>
        /// ActionParser'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                _logger.LogInformation("Shutting down ActionParser...");

                // Wait for ongoing parsings;
                await Task.Delay(1000);

                // Clear caches;
                ClearCaches();

                // Shutdown dependencies;
                await _syntaxAnalyzer.ShutdownAsync();
                await _semanticAnalyzer.ShutdownAsync();
                await _entityExtractor.ShutdownAsync();

                _isInitialized = false;

                _logger.LogInformation("ActionParser shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during shutdown");
                throw;
            }
        }

        /// <summary>
        /// Dispose pattern implementation;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    ShutdownAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during disposal");
                }

                _parsingSemaphore?.Dispose();
                ClearCaches();

                // Clear event handlers;
                OnParsingStarted = null;
                OnParsingProgress = null;
                OnParsingCompleted = null;
                OnAmbiguityDetected = null;
                OnActionPatternRecognized = null;
            }
        }

        // Helper methods;
        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new ActionParserNotInitializedException(
                    "ActionParser must be initialized before use. Call InitializeAsync() first.");
            }
        }

        private string GenerateCacheKey(ActionParsingRequest request)
        {
            var textHash = request.Text.GetHashCode().ToString("X");
            var language = request.Context?.Language.ToString() ?? "EN";
            var domain = request.Context?.Domain ?? "general";
            return $"{textHash}_{language}_{domain}";
        }

        private async Task ReportProgressAsync(
            string parsingId,
            int progressPercentage,
            string message,
            ParsingContext context)
        {
            try
            {
                OnParsingProgress?.Invoke(this, new ActionParsingProgressEventArgs;
                {
                    ParsingId = parsingId,
                    ProgressPercentage = progressPercentage,
                    Message = message,
                    Context = context,
                    Timestamp = DateTime.UtcNow;
                });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reporting progress");
            }
        }

        private async Task CheckPatternRecognitionAsync(
            PatternMatchingResult patternMatching,
            ParsedAction parsedAction,
            CancellationToken cancellationToken)
        {
            try
            {
                if (patternMatching.BestMatch != null &&
                    patternMatching.BestMatch.Confidence >= _config.PatternRecognitionThreshold)
                {
                    OnActionPatternRecognized?.Invoke(this, new ActionPatternRecognizedEventArgs;
                    {
                        ParsingId = parsedAction.Id,
                        Pattern = patternMatching.BestMatch,
                        ParsedAction = parsedAction,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking pattern recognition");
            }
        }

        private void ClearCaches()
        {
            _parsingCache.Clear();
            _logger.LogDebug("ActionParser caches cleared");
        }

        private DateTime _startTime = DateTime.UtcNow;

        // Additional helper and calculation methods would continue here...
    }

    #region Supporting Classes and Enums;

    public enum Language;
    {
        English,
        Turkish,
        Spanish,
        French,
        German,
        Chinese,
        Japanese,
        Korean,
        Russian,
        Arabic,
        Any;
    }

    public enum ActionType;
    {
        Creation,
        Deletion,
        Modification,
        Access,
        Movement,
        Copy,
        Execution,
        Analysis,
        Communication,
        Storage,
        Retrieval,
        Transformation,
        Comparison,
        Validation,
        Verification,
        Installation,
        Uninstallation,
        Configuration,
        Monitoring,
        Control,
        Unknown;
    }

    public enum VerbTense;
    {
        Present,
        Past,
        Future,
        PresentPerfect,
        PastPerfect,
        FuturePerfect,
        PresentContinuous,
        PastContinuous,
        FutureContinuous,
        Unknown;
    }

    public enum VerbPerson;
    {
        FirstSingular,
        SecondSingular,
        ThirdSingular,
        FirstPlural,
        SecondPlural,
        ThirdPlural,
        Any,
        Unknown;
    }

    public enum PatternPriority;
    {
        Critical,
        High,
        Medium,
        Low;
    }

    public enum ActionMood;
    {
        Imperative,
        Indicative,
        Interrogative,
        Conditional,
        Subjunctive,
        Unknown;
    }

    public class ActionParserConfig;
    {
        public bool EnableCaching { get; set; } = true;
        public double CacheThreshold { get; set; } = 0.7;
        public int MaxConcurrentParsings { get; set; } = Environment.ProcessorCount * 2;
        public int MaxPatternsToMatch { get; set; } = 10;
        public double PatternThreshold { get; set; } = 0.6;
        public double PatternRecognitionThreshold { get; set; } = 0.8;
        public bool EnableAmbiguityDetection { get; set; } = true;
        public bool EnablePatternLearning { get; set; } = true;
        public TimeSpan ParsingTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public class ActionParserOptions;
    {
        public bool CaseSensitive { get; set; } = false;
        public bool EnableSpellCheck { get; set; } = true;
        public bool RemoveStopWords { get; set; } = false;
        public bool ExpandContractions { get; set; } = true;
        public bool NormalizeNumbers { get; set; } = true;
        public double MinimumConfidence { get; set; } = 0.5;
        public int MaxTextLength { get; set; } = 1000;
    }

    public class ActionParsingRequest;
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public ParsingContext Context { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public DateTime RequestTime { get; set; }
    }

    public class ParsedAction;
    {
        public string Id { get; set; }
        public string RequestId { get; set; }
        public string OriginalText { get; set; }
        public string PreprocessedText { get; set; }
        public string Summary { get; set; }
        public ComposedAction ComposedAction { get; set; }
        public VerbExtractionResult VerbExtraction { get; set; }
        public ObjectExtractionResult ObjectExtraction { get; set; }
        public ParameterExtractionResult ParameterExtraction { get; set; }
        public PatternMatchingResult PatternMatching { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public SemanticAnalysis SemanticAnalysis { get; set; }
        public List<Ambiguity> Ambiguities { get; set; }
        public ActionExecutionPlan ExecutionPlan { get; set; }
        public double Confidence { get; set; }
        public TimeSpan ParsingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public Language Language { get; set; }
        public string Domain { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ActionPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Template { get; set; }
        public string Example { get; set; }
        public Language Language { get; set; }
        public PatternPriority Priority { get; set; }
        public double Confidence { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsed { get; set; }
        public int UsageCount { get; set; }
    }

    public class VerbTemplate;
    {
        public string Verb { get; set; }
        public string BaseForm { get; set; }
        public string PastTense { get; set; }
        public string PresentParticiple { get; set; }
        public string ThirdPerson { get; set; }
        public ActionType ActionType { get; set; }
        public List<string> RequiredObjects { get; set; }
        public List<string> OptionalObjects { get; set; }
        public List<string> Prepositions { get; set; }
        public string Example { get; set; }
        public double Confidence { get; set; }
    }

    // Additional supporting classes would be defined here...
    // Due to length constraints, I'm showing the main structure;

    #endregion;

    #region Exceptions;

    public class ActionParserException : Exception
    {
        public ActionParserException(string message) : base(message) { }
        public ActionParserException(string message, Exception inner) : base(message, inner) { }
    }

    public class ActionParserNotInitializedException : ActionParserException;
    {
        public ActionParserNotInitializedException(string message) : base(message) { }
    }

    public class ActionParsingException : ActionParserException;
    {
        public ActionParsingException(string message) : base(message) { }
        public ActionParsingException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;

    #region Events;

    public class ActionParsingStartedEventArgs : EventArgs;
    {
        public string ParsingId { get; set; }
        public ActionParsingRequest Request { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActionParsingProgressEventArgs : EventArgs;
    {
        public string ParsingId { get; set; }
        public int ProgressPercentage { get; set; }
        public string Message { get; set; }
        public ParsingContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActionParsingCompletedEventArgs : EventArgs;
    {
        public string ParsingId { get; set; }
        public ActionParsingRequest Request { get; set; }
        public ParsedAction ParsedAction { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class AmbiguityDetectedEventArgs : EventArgs;
    {
        public string ParsingId { get; set; }
        public Ambiguity Ambiguity { get; set; }
        public ParsedAction ParsedAction { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActionPatternRecognizedEventArgs : EventArgs;
    {
        public string ParsingId { get; set; }
        public MatchedPattern Pattern { get; set; }
        public ParsedAction ParsedAction { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}

// Interface definition for dependency injection;
public interface IActionParser : IDisposable
{
    Task InitializeAsync(ActionParserConfig config = null);
    Task<ParsedAction> ParseActionAsync(
        ActionParsingRequest request,
        CancellationToken cancellationToken = default);
    Task<BatchParsingResult> ParseActionsBatchAsync(
        List<ActionParsingRequest> requests,
        BatchParsingConfig config = null,
        CancellationToken cancellationToken = default);
    Task<ActionParserStatistics> GetStatisticsAsync();
    Task ShutdownAsync();

    bool IsInitialized { get; }

    event EventHandler<ActionParsingStartedEventArgs> OnParsingStarted;
    event EventHandler<ActionParsingProgressEventArgs> OnParsingProgress;
    event EventHandler<ActionParsingCompletedEventArgs> OnParsingCompleted;
    event EventHandler<AmbiguityDetectedEventArgs> OnAmbiguityDetected;
    event EventHandler<ActionPatternRecognizedEventArgs> OnActionPatternRecognized;
}

// Supporting interfaces;
public interface ISyntaxAnalyzer;
{
    Task<SyntaxTree> AnalyzeAsync(string text, CancellationToken cancellationToken);
    Task InitializeAsync();
    Task ShutdownAsync();
}

public interface ISemanticAnalyzer;
{
    Task<SemanticAnalysis> AnalyzeAsync(string text, CancellationToken cancellationToken);
    Task InitializeAsync();
    Task ShutdownAsync();
}

public interface IEntityExtractor;
{
    Task<List<ExtractedEntity>> ExtractEntitiesAsync(string text, CancellationToken cancellationToken);
    Task InitializeAsync();
    Task ShutdownAsync();
}
