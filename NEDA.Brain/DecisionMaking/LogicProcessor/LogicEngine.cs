// NEDA.Brain/DecisionMaking/LogicProcessor/LogicEngine.cs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.Brain.DecisionMaking.EthicalChecker;
using NEDA.Brain.DecisionMaking.LogicProcessor;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.NeuralNetwork;
using NEDA.Common.Extensions;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.Monitoring.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.Brain.DecisionMaking.LogicProcessor;
{
    /// <summary>
    /// NEDA sisteminin mantık işleme ve karar verme motoru.
    /// Çok katmanlı mantık, tümdengelim, tümevarım ve abduksiyon yöntemlerini destekler.
    /// </summary>
    public class LogicEngine : ILogicEngine, IDisposable;
    {
        private readonly ILogger<LogicEngine> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly IReasoner _reasoner;
        private readonly IInferenceSystem _inferenceSystem;
        private readonly IEmotionDetector _emotionDetector;
        private readonly IDiagnosticTool _diagnosticTool;

        // Logic processing caches;
        private readonly Dictionary<string, LogicRule> _ruleCache;
        private readonly Dictionary<string, List<LogicInference>> _inferenceCache;
        private readonly PriorityQueue<LogicTask, int> _taskQueue;

        // Configuration;
        private LogicEngineConfig _config;
        private bool _isInitialized;
        private bool _isProcessing;
        private readonly object _lockObject = new object();

        // Events;
        public event EventHandler<LogicProcessingEventArgs> OnLogicProcessed;
        public event EventHandler<DecisionMadeEventArgs> OnDecisionMade;
        public event EventHandler<InferenceCompletedEventArgs> OnInferenceCompleted;

        /// <summary>
        /// Logic Engine constructor;
        /// </summary>
        public LogicEngine(
            ILogger<LogicEngine> logger,
            IServiceProvider serviceProvider,
            IKnowledgeBase knowledgeBase,
            IReasoner reasoner,
            IInferenceSystem inferenceSystem,
            IEmotionDetector emotionDetector,
            IDiagnosticTool diagnosticTool)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _reasoner = reasoner ?? throw new ArgumentNullException(nameof(reasoner));
            _inferenceSystem = inferenceSystem ?? throw new ArgumentNullException(nameof(inferenceSystem));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));

            _ruleCache = new Dictionary<string, LogicRule>();
            _inferenceCache = new Dictionary<string, List<LogicInference>>();
            _taskQueue = new PriorityQueue<LogicTask, int>();

            _config = new LogicEngineConfig();
            _isInitialized = false;
            _isProcessing = false;

            _logger.LogInformation("LogicEngine initialized successfully");
        }

        /// <summary>
        /// Logic Engine'i belirtilen konfigürasyon ile başlatır;
        /// </summary>
        public async Task InitializeAsync(LogicEngineConfig config = null)
        {
            try
            {
                _logger.LogInformation("Initializing LogicEngine...");

                _config = config ?? new LogicEngineConfig();

                // Rule cache'ini yükle;
                await LoadLogicRulesAsync();

                // Inference motorunu başlat;
                await _inferenceSystem.InitializeAsync();

                // Reasoner'ı başlat;
                await _reasoner.InitializeAsync();

                _isInitialized = true;

                _logger.LogInformation("LogicEngine initialized successfully with {RuleCount} rules",
                    _ruleCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LogicEngine");
                throw new LogicEngineException("LogicEngine initialization failed", ex);
            }
        }

        /// <summary>
        /// Mantık kurallarını yükler;
        /// </summary>
        private async Task LoadLogicRulesAsync()
        {
            try
            {
                // Temel mantık kurallarını yükle;
                var basicRules = GetBasicLogicRules();
                foreach (var rule in basicRules)
                {
                    _ruleCache[rule.Id] = rule;
                }

                // KnowledgeBase'den kuralları yükle;
                var kbRules = await _knowledgeBase.GetLogicRulesAsync();
                foreach (var rule in kbRules)
                {
                    if (!_ruleCache.ContainsKey(rule.Id))
                    {
                        _ruleCache[rule.Id] = rule;
                    }
                }

                _logger.LogDebug("Loaded {RuleCount} logic rules", _ruleCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load logic rules");
                throw;
            }
        }

        /// <summary>
        /// Temel mantık kurallarını döndürür;
        /// </summary>
        private IEnumerable<LogicRule> GetBasicLogicRules()
        {
            return new List<LogicRule>
            {
                new LogicRule;
                {
                    Id = "RULE_DEDUCTION_001",
                    Name = "Modus Ponens",
                    Description = "If P implies Q, and P is true, then Q is true",
                    Pattern = "P → Q, P ⊢ Q",
                    Priority = LogicPriority.High,
                    Category = LogicCategory.Deductive,
                    Conditions = new List<string> { "P", "P→Q" },
                    Conclusion = "Q",
                    Confidence = 1.0,
                    IsActive = true;
                },
                new LogicRule;
                {
                    Id = "RULE_DEDUCTION_002",
                    Name = "Modus Tollens",
                    Description = "If P implies Q, and Q is false, then P is false",
                    Pattern = "P → Q, ¬Q ⊢ ¬P",
                    Priority = LogicPriority.High,
                    Category = LogicCategory.Deductive,
                    Conditions = new List<string> { "¬Q", "P→Q" },
                    Conclusion = "¬P",
                    Confidence = 1.0,
                    IsActive = true;
                },
                new LogicRule;
                {
                    Id = "RULE_INDUCTION_001",
                    Name = "Simple Induction",
                    Description = "From specific instances to general conclusion",
                    Pattern = "P₁, P₂, ..., Pₙ ⊢ ∀x P(x)",
                    Priority = LogicPriority.Medium,
                    Category = LogicCategory.Inductive,
                    Conditions = new List<string> { "P₁", "P₂", "...", "Pₙ" },
                    Conclusion = "∀x P(x)",
                    Confidence = 0.8,
                    IsActive = true;
                },
                new LogicRule;
                {
                    Id = "RULE_ABDUCTION_001",
                    Name = "Inference to Best Explanation",
                    Description = "From observation to hypothesis that best explains it",
                    Pattern = "Q, P→Q ⊢ P",
                    Priority = LogicPriority.Medium,
                    Category = LogicCategory.Abductive,
                    Conditions = new List<string> { "Q", "P→Q" },
                    Conclusion = "P",
                    Confidence = 0.7,
                    IsActive = true;
                },
                new LogicRule;
                {
                    Id = "RULE_TEMPORAL_001",
                    Name = "Temporal Reasoning",
                    Description = "Reasoning about time and sequences",
                    Pattern = "P at t₁, P→Q at t₂ ⊢ Q at t₂",
                    Priority = LogicPriority.Medium,
                    Category = LogicCategory.Temporal,
                    Conditions = new List<string> { "P(t₁)", "P→Q", "t₁ < t₂" },
                    Conclusion = "Q(t₂)",
                    Confidence = 0.9,
                    IsActive = true;
                }
            };
        }

        /// <summary>
        /// Mantıksal bir ifadeyi değerlendirir;
        /// </summary>
        public async Task<LogicEvaluationResult> EvaluateExpressionAsync(
            string expression,
            LogicContext context = null)
        {
            ValidateInitialization();

            try
            {
                _logger.LogDebug("Evaluating logic expression: {Expression}", expression);

                // Context oluştur veya mevcut context'i kullan;
                var evaluationContext = context ?? new LogicContext;
                {
                    Timestamp = DateTime.UtcNow,
                    SessionId = Guid.NewGuid().ToString(),
                    Parameters = new Dictionary<string, object>()
                };

                // İfadeyi parse et;
                var parsedExpression = await ParseLogicalExpressionAsync(expression);

                // İfadeyi değerlendir;
                var evaluationResult = await EvaluateParsedExpressionAsync(parsedExpression, evaluationContext);

                // Sonuçları cache'e ekle;
                CacheEvaluationResult(expression, evaluationResult);

                // Event tetikle;
                OnLogicProcessed?.Invoke(this, new LogicProcessingEventArgs;
                {
                    Expression = expression,
                    Result = evaluationResult,
                    Context = evaluationContext,
                    Timestamp = DateTime.UtcNow;
                });

                return evaluationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating logic expression: {Expression}", expression);
                throw new LogicEvaluationException($"Failed to evaluate expression: {expression}", ex);
            }
        }

        /// <summary>
        /// Mantıksal ifadeyi parse eder;
        /// </summary>
        private async Task<ParsedExpression> ParseLogicalExpressionAsync(string expression)
        {
            try
            {
                // Tokenization;
                var tokens = TokenizeExpression(expression);

                // Syntax analysis;
                var syntaxTree = BuildSyntaxTree(tokens);

                // Semantic analysis;
                var semanticInfo = await AnalyzeSemanticsAsync(syntaxTree);

                return new ParsedExpression;
                {
                    Original = expression,
                    Tokens = tokens,
                    SyntaxTree = syntaxTree,
                    SemanticInfo = semanticInfo,
                    ParseTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse logical expression");
                throw;
            }
        }

        /// <summary>
        /// İfadeyi token'lara ayırır;
        /// </summary>
        private List<LogicToken> TokenizeExpression(string expression)
        {
            var tokens = new List<LogicToken>();
            var currentPosition = 0;

            while (currentPosition < expression.Length)
            {
                // Boşlukları atla;
                if (char.IsWhiteSpace(expression[currentPosition]))
                {
                    currentPosition++;
                    continue;
                }

                // Operatörleri kontrol et;
                var @operator = DetectOperator(expression, currentPosition);
                if (@operator != null)
                {
                    tokens.Add(new LogicToken;
                    {
                        Type = LogicTokenType.Operator,
                        Value = @operator.Symbol,
                        Position = currentPosition,
                        Length = @operator.Symbol.Length;
                    });
                    currentPosition += @operator.Symbol.Length;
                    continue;
                }

                // Sayıları kontrol et;
                if (char.IsDigit(expression[currentPosition]))
                {
                    var number = ExtractNumber(expression, ref currentPosition);
                    tokens.Add(new LogicToken;
                    {
                        Type = LogicTokenType.Number,
                        Value = number,
                        Position = currentPosition - number.Length,
                        Length = number.Length;
                    });
                    continue;
                }

                // Değişkenleri kontrol et;
                if (char.IsLetter(expression[currentPosition]) || expression[currentPosition] == '_')
                {
                    var variable = ExtractVariable(expression, ref currentPosition);
                    tokens.Add(new LogicToken;
                    {
                        Type = LogicTokenType.Variable,
                        Value = variable,
                        Position = currentPosition - variable.Length,
                        Length = variable.Length;
                    });
                    continue;
                }

                // Parantezleri kontrol et;
                if (expression[currentPosition] == '(' || expression[currentPosition] == ')')
                {
                    tokens.Add(new LogicToken;
                    {
                        Type = LogicTokenType.Parenthesis,
                        Value = expression[currentPosition].ToString(),
                        Position = currentPosition,
                        Length = 1;
                    });
                    currentPosition++;
                    continue;
                }

                // Bilinmeyen karakter;
                throw new LogicParsingException($"Unknown character at position {currentPosition}: '{expression[currentPosition]}'");
            }

            return tokens;
        }

        /// <summary>
        /// Syntax ağacı oluşturur;
        /// </summary>
        private SyntaxNode BuildSyntaxTree(List<LogicToken> tokens)
        {
            // Shunting-yard algoritması ile parse tree oluştur;
            var outputQueue = new Queue<LogicToken>();
            var operatorStack = new Stack<LogicToken>();

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case LogicTokenType.Number:
                    case LogicTokenType.Variable:
                        outputQueue.Enqueue(token);
                        break;

                    case LogicTokenType.Operator:
                        while (operatorStack.Count > 0 &&
                               operatorStack.Peek().Type == LogicTokenType.Operator &&
                               GetOperatorPrecedence(operatorStack.Peek().Value) >= GetOperatorPrecedence(token.Value))
                        {
                            outputQueue.Enqueue(operatorStack.Pop());
                        }
                        operatorStack.Push(token);
                        break;

                    case LogicTokenType.Parenthesis:
                        if (token.Value == "(")
                        {
                            operatorStack.Push(token);
                        }
                        else if (token.Value == ")")
                        {
                            while (operatorStack.Count > 0 && operatorStack.Peek().Value != "(")
                            {
                                outputQueue.Enqueue(operatorStack.Pop());
                            }
                            if (operatorStack.Count == 0)
                            {
                                throw new LogicParsingException("Mismatched parentheses");
                            }
                            operatorStack.Pop(); // '(' i çıkar;
                        }
                        break;
                }
            }

            // Kalan operatörleri ekle;
            while (operatorStack.Count > 0)
            {
                if (operatorStack.Peek().Value == "(" || operatorStack.Peek().Value == ")")
                {
                    throw new LogicParsingException("Mismatched parentheses");
                }
                outputQueue.Enqueue(operatorStack.Pop());
            }

            // Syntax tree oluştur;
            return BuildTreeFromRPN(outputQueue.ToList());
        }

        /// <summary>
        /// Parse edilmiş ifadeyi değerlendirir;
        /// </summary>
        private async Task<LogicEvaluationResult> EvaluateParsedExpressionAsync(
            ParsedExpression parsedExpression,
            LogicContext context)
        {
            try
            {
                // Değerlendirme başlat;
                var evaluationStartTime = DateTime.UtcNow;

                // Syntax tree'yi değerlendir;
                var evaluationResult = await EvaluateSyntaxTreeAsync(
                    parsedExpression.SyntaxTree,
                    context);

                // Semantic bilgileri ekle;
                evaluationResult.SemanticInfo = parsedExpression.SemanticInfo;

                // Confidence hesapla;
                evaluationResult.Confidence = CalculateConfidenceScore(
                    evaluationResult,
                    parsedExpression);

                // Performance metrics ekle;
                evaluationResult.EvaluationTime = DateTime.UtcNow - evaluationStartTime;
                evaluationResult.MemoryUsage = GC.GetTotalMemory(false);

                // Emotional context ekle (eğer varsa)
                if (context.EmotionalContext != null)
                {
                    evaluationResult.EmotionalContext = context.EmotionalContext;
                }

                _logger.LogDebug("Expression evaluated with confidence: {Confidence}",
                    evaluationResult.Confidence);

                return evaluationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating parsed expression");
                throw;
            }
        }

        /// <summary>
        /// Syntax tree'yi değerlendirir;
        /// </summary>
        private async Task<LogicEvaluationResult> EvaluateSyntaxTreeAsync(
            SyntaxNode node,
            LogicContext context)
        {
            if (node == null)
            {
                return new LogicEvaluationResult;
                {
                    IsValid = false,
                    Error = "Empty syntax tree"
                };
            }

            switch (node.Type)
            {
                case NodeType.Number:
                    return new LogicEvaluationResult;
                    {
                        IsValid = true,
                        Value = Convert.ToDouble(node.Value),
                        DataType = LogicDataType.Number,
                        Confidence = 1.0;
                    };

                case NodeType.Variable:
                    return await ResolveVariableAsync(node.Value.ToString(), context);

                case NodeType.Operation:
                    return await EvaluateOperationAsync(node, context);

                default:
                    throw new LogicEvaluationException($"Unknown node type: {node.Type}");
            }
        }

        /// <summary>
        /// Değişken değerini çözümler;
        /// </summary>
        private async Task<LogicEvaluationResult> ResolveVariableAsync(
            string variableName,
            LogicContext context)
        {
            try
            {
                // Önce context'ten kontrol et;
                if (context.Parameters != null &&
                    context.Parameters.ContainsKey(variableName))
                {
                    return new LogicEvaluationResult;
                    {
                        IsValid = true,
                        Value = context.Parameters[variableName],
                        DataType = GetDataType(context.Parameters[variableName]),
                        Confidence = 0.9;
                    };
                }

                // KnowledgeBase'den kontrol et;
                var kbValue = await _knowledgeBase.GetFactValueAsync(variableName);
                if (kbValue != null)
                {
                    return new LogicEvaluationResult;
                    {
                        IsValid = true,
                        Value = kbValue.Value,
                        DataType = MapToLogicDataType(kbValue.DataType),
                        Confidence = kbValue.Confidence;
                    };
                }

                // Inference sistemi ile çözümle;
                var inferenceResult = await _inferenceSystem.InferValueAsync(variableName, context);
                if (inferenceResult != null && inferenceResult.IsValid)
                {
                    return new LogicEvaluationResult;
                    {
                        IsValid = true,
                        Value = inferenceResult.Value,
                        DataType = inferenceResult.DataType,
                        Confidence = inferenceResult.Confidence;
                    };
                }

                // Değişken bulunamadı;
                return new LogicEvaluationResult;
                {
                    IsValid = false,
                    Error = $"Variable '{variableName}' not found",
                    Confidence = 0.0;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving variable: {VariableName}", variableName);
                throw;
            }
        }

        /// <summary>
        /// Operasyonu değerlendirir;
        /// </summary>
        private async Task<LogicEvaluationResult> EvaluateOperationAsync(
            SyntaxNode operationNode,
            LogicContext context)
        {
            try
            {
                // Operand'ları değerlendir;
                var leftResult = await EvaluateSyntaxTreeAsync(operationNode.Left, context);
                var rightResult = await EvaluateSyntaxTreeAsync(operationNode.Right, context);

                if (!leftResult.IsValid || !rightResult.IsValid)
                {
                    return new LogicEvaluationResult;
                    {
                        IsValid = false,
                        Error = "Invalid operands",
                        Confidence = Math.Min(leftResult.Confidence, rightResult.Confidence)
                    };
                }

                // Operatöre göre işlem yap;
                switch (operationNode.Value.ToString())
                {
                    case "+":
                        return EvaluateAddition(leftResult, rightResult);
                    case "-":
                        return EvaluateSubtraction(leftResult, rightResult);
                    case "*":
                        return EvaluateMultiplication(leftResult, rightResult);
                    case "/":
                        return EvaluateDivision(leftResult, rightResult);
                    case "∧": // AND;
                        return EvaluateLogicalAnd(leftResult, rightResult);
                    case "∨": // OR;
                        return EvaluateLogicalOr(leftResult, rightResult);
                    case "→": // IMPLIES;
                        return EvaluateImplication(leftResult, rightResult);
                    case "≡": // EQUIVALENT;
                        return EvaluateEquivalence(leftResult, rightResult);
                    case "≠": // NOT EQUAL;
                        return EvaluateNotEqual(leftResult, rightResult);
                    case ">":
                        return EvaluateGreaterThan(leftResult, rightResult);
                    case "<":
                        return EvaluateLessThan(leftResult, rightResult);
                    case "≥":
                        return EvaluateGreaterThanOrEqual(leftResult, rightResult);
                    case "≤":
                        return EvaluateLessThanOrEqual(leftResult, rightResult);
                    default:
                        throw new LogicEvaluationException($"Unknown operator: {operationNode.Value}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating operation");
                throw;
            }
        }

        /// <summary>
        /// Mantıksal AND işlemi;
        /// </summary>
        private LogicEvaluationResult EvaluateLogicalAnd(
            LogicEvaluationResult left,
            LogicEvaluationResult right)
        {
            try
            {
                var leftBool = Convert.ToBoolean(left.Value);
                var rightBool = Convert.ToBoolean(right.Value);

                return new LogicEvaluationResult;
                {
                    IsValid = true,
                    Value = leftBool && rightBool,
                    DataType = LogicDataType.Boolean,
                    Confidence = Math.Min(left.Confidence, right.Confidence) * 0.95;
                };
            }
            catch
            {
                return new LogicEvaluationResult;
                {
                    IsValid = false,
                    Error = "Invalid operands for logical AND",
                    Confidence = Math.Min(left.Confidence, right.Confidence) * 0.5;
                };
            }
        }

        /// <summary>
        /// Mantıksal OR işlemi;
        /// </summary>
        private LogicEvaluationResult EvaluateLogicalOr(
            LogicEvaluationResult left,
            LogicEvaluationResult right)
        {
            try
            {
                var leftBool = Convert.ToBoolean(left.Value);
                var rightBool = Convert.ToBoolean(right.Value);

                return new LogicEvaluationResult;
                {
                    IsValid = true,
                    Value = leftBool || rightBool,
                    DataType = LogicDataType.Boolean,
                    Confidence = Math.Min(left.Confidence, right.Confidence) * 0.95;
                };
            }
            catch
            {
                return new LogicEvaluationResult;
                {
                    IsValid = false,
                    Error = "Invalid operands for logical OR",
                    Confidence = Math.Min(left.Confidence, right.Confidence) * 0.5;
                };
            }
        }

        /// <summary>
        /// Mantıksal IMPLICATION işlemi;
        /// </summary>
        private LogicEvaluationResult EvaluateImplication(
            LogicEvaluationResult left,
            LogicEvaluationResult right)
        {
            try
            {
                var leftBool = Convert.ToBoolean(left.Value);
                var rightBool = Convert.ToBoolean(right.Value);

                // P → Q mantığı: false only when P is true and Q is false;
                var result = !leftBool || rightBool;

                return new LogicEvaluationResult;
                {
                    IsValid = true,
                    Value = result,
                    DataType = LogicDataType.Boolean,
                    Confidence = Math.Min(left.Confidence, right.Confidence) * 0.9;
                };
            }
            catch
            {
                return new LogicEvaluationResult;
                {
                    IsValid = false,
                    Error = "Invalid operands for implication",
                    Confidence = Math.Min(left.Confidence, right.Confidence) * 0.5;
                };
            }
        }

        /// <summary>
        /// Karar verme süreci;
        /// </summary>
        public async Task<DecisionResult> MakeDecisionAsync(
            DecisionRequest request,
            LogicContext context = null)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Making decision for request: {RequestId}", request.Id);

                var decisionContext = context ?? new LogicContext;
                {
                    Timestamp = DateTime.UtcNow,
                    SessionId = Guid.NewGuid().ToString(),
                    DecisionParameters = new Dictionary<string, object>()
                };

                // 1. Seçenekleri analiz et;
                var analyzedOptions = await AnalyzeDecisionOptionsAsync(request.Options, decisionContext);

                // 2. Kriterleri değerlendir;
                var evaluatedCriteria = await EvaluateDecisionCriteriaAsync(request.Criteria, decisionContext);

                // 3. Risk analizi yap;
                var riskAnalysis = await AnalyzeRisksAsync(analyzedOptions, decisionContext);

                // 4. Optimizasyon yap;
                var optimizedDecision = await OptimizeDecisionAsync(
                    analyzedOptions,
                    evaluatedCriteria,
                    riskAnalysis,
                    decisionContext);

                // 5. Ethical check yap;
                var ethicalCheck = await PerformEthicalCheckAsync(optimizedDecision, decisionContext);

                // 6. Final kararı oluştur;
                var decisionResult = await FormulateFinalDecisionAsync(
                    optimizedDecision,
                    ethicalCheck,
                    decisionContext);

                // Event tetikle;
                OnDecisionMade?.Invoke(this, new DecisionMadeEventArgs;
                {
                    RequestId = request.Id,
                    Decision = decisionResult,
                    Context = decisionContext,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Decision made successfully for request: {RequestId}", request.Id);

                return decisionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error making decision for request: {RequestId}", request.Id);
                throw new DecisionMakingException($"Failed to make decision for request: {request.Id}", ex);
            }
        }

        /// <summary>
        /// Seçenekleri analiz eder;
        /// </summary>
        private async Task<List<AnalyzedOption>> AnalyzeDecisionOptionsAsync(
            List<DecisionOption> options,
            LogicContext context)
        {
            var analyzedOptions = new List<AnalyzedOption>();

            foreach (var option in options)
            {
                var analyzedOption = new AnalyzedOption;
                {
                    Option = option,
                    FeasibilityScore = await CalculateFeasibilityScoreAsync(option, context),
                    CostBenefitRatio = await CalculateCostBenefitRatioAsync(option, context),
                    RiskScore = await CalculateRiskScoreAsync(option, context),
                    AlignmentScore = await CalculateAlignmentScoreAsync(option, context),
                    Dependencies = await IdentifyDependenciesAsync(option, context)
                };

                analyzedOptions.Add(analyzedOption);
            }

            return analyzedOptions;
        }

        /// <summary>
        /// Uygulanabilirlik skorunu hesaplar;
        /// </summary>
        private async Task<double> CalculateFeasibilityScoreAsync(
            DecisionOption option,
            LogicContext context)
        {
            try
            {
                var feasibilityFactors = new List<double>();

                // Resource availability kontrolü;
                if (option.RequiredResources != null)
                {
                    var resourceScore = await CalculateResourceAvailabilityScoreAsync(
                        option.RequiredResources,
                        context);
                    feasibilityFactors.Add(resourceScore);
                }

                // Technical feasibility kontrolü;
                var technicalScore = await CalculateTechnicalFeasibilityScoreAsync(option, context);
                feasibilityFactors.Add(technicalScore);

                // Time feasibility kontrolü;
                var timeScore = await CalculateTimeFeasibilityScoreAsync(option, context);
                feasibilityFactors.Add(timeScore);

                // Ortalama skoru hesapla;
                return feasibilityFactors.Any() ? feasibilityFactors.Average() : 0.5;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating feasibility score for option: {OptionId}", option.Id);
                return 0.3; // Conservative default;
            }
        }

        /// <summary>
        /// Çıkarım yapar;
        /// </summary>
        public async Task<InferenceResult> MakeInferenceAsync(
            InferenceRequest request,
            LogicContext context = null)
        {
            ValidateInitialization();

            try
            {
                _logger.LogDebug("Making inference: {PremisesCount} premises",
                    request.Premises?.Count ?? 0);

                var inferenceContext = context ?? new LogicContext;
                {
                    Timestamp = DateTime.UtcNow,
                    SessionId = Guid.NewGuid().ToString(),
                    InferenceParameters = new Dictionary<string, object>()
                };

                // 1. Premise'leri değerlendir;
                var evaluatedPremises = await EvaluatePremisesAsync(request.Premises, inferenceContext);

                // 2. Uygulanabilir kuralları bul;
                var applicableRules = await FindApplicableRulesAsync(evaluatedPremises, inferenceContext);

                // 3. Çıkarım zinciri oluştur;
                var inferenceChain = await BuildInferenceChainAsync(
                    evaluatedPremises,
                    applicableRules,
                    inferenceContext);

                // 4. Sonuçları sentezle;
                var synthesizedResult = await SynthesizeInferenceResultsAsync(
                    inferenceChain,
                    inferenceContext);

                // 5. Confidence hesapla;
                var confidenceScore = await CalculateInferenceConfidenceAsync(
                    synthesizedResult,
                    inferenceChain,
                    inferenceContext);

                // 6. Final inference result oluştur;
                var inferenceResult = new InferenceResult;
                {
                    Id = Guid.NewGuid().ToString(),
                    Conclusions = synthesizedResult.Conclusions,
                    Confidence = confidenceScore,
                    InferenceChain = inferenceChain,
                    SupportingEvidence = synthesizedResult.SupportingEvidence,
                    Contradictions = synthesizedResult.Contradictions,
                    Timestamp = DateTime.UtcNow,
                    ProcessingTime = DateTime.UtcNow - inferenceContext.Timestamp;
                };

                // Cache'e ekle;
                CacheInferenceResult(request, inferenceResult);

                // Event tetikle;
                OnInferenceCompleted?.Invoke(this, new InferenceCompletedEventArgs;
                {
                    Request = request,
                    Result = inferenceResult,
                    Context = inferenceContext,
                    Timestamp = DateTime.UtcNow;
                });

                return inferenceResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error making inference");
                throw new InferenceException("Failed to make inference", ex);
            }
        }

        /// <summary>
        /// Mantık kurallarını ekler;
        /// </summary>
        public async Task AddLogicRuleAsync(LogicRule rule)
        {
            ValidateInitialization();

            try
            {
                if (rule == null)
                    throw new ArgumentNullException(nameof(rule));

                if (string.IsNullOrEmpty(rule.Id))
                    rule.Id = $"RULE_{Guid.NewGuid():N}".ToUpper();

                // Rule validation;
                await ValidateLogicRuleAsync(rule);

                // Cache'e ekle;
                lock (_lockObject)
                {
                    _ruleCache[rule.Id] = rule;
                }

                // KnowledgeBase'e ekle;
                await _knowledgeBase.AddLogicRuleAsync(rule);

                _logger.LogInformation("Logic rule added: {RuleId} - {RuleName}", rule.Id, rule.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding logic rule");
                throw;
            }
        }

        /// <summary>
        /// Mantık kuralını doğrular;
        /// </summary>
        private async Task ValidateLogicRuleAsync(LogicRule rule)
        {
            var validationErrors = new List<string>();

            if (string.IsNullOrEmpty(rule.Name))
                validationErrors.Add("Rule name is required");

            if (rule.Conditions == null || !rule.Conditions.Any())
                validationErrors.Add("Rule must have at least one condition");

            if (string.IsNullOrEmpty(rule.Conclusion))
                validationErrors.Add("Rule conclusion is required");

            if (rule.Confidence < 0 || rule.Confidence > 1)
                validationErrors.Add("Confidence must be between 0 and 1");

            // Pattern validation;
            if (!string.IsNullOrEmpty(rule.Pattern))
            {
                try
                {
                    await ParseLogicalExpressionAsync(rule.Pattern);
                }
                catch (LogicParsingException ex)
                {
                    validationErrors.Add($"Invalid pattern format: {ex.Message}");
                }
            }

            if (validationErrors.Any())
            {
                throw new LogicRuleValidationException(
                    $"Rule validation failed: {string.Join("; ", validationErrors)}");
            }
        }

        /// <summary>
        /// Mantık kuralını kaldırır;
        /// </summary>
        public async Task<bool> RemoveLogicRuleAsync(string ruleId)
        {
            ValidateInitialization();

            try
            {
                bool removedFromCache = false;
                bool removedFromKB = false;

                lock (_lockObject)
                {
                    removedFromCache = _ruleCache.Remove(ruleId);
                }

                // KnowledgeBase'den kaldır;
                if (removedFromCache)
                {
                    removedFromKB = await _knowledgeBase.RemoveLogicRuleAsync(ruleId);
                }

                _logger.LogInformation("Logic rule removed: {RuleId} (Cache: {CacheRemoved}, KB: {KBRemoved})",
                    ruleId, removedFromCache, removedFromKB);

                return removedFromCache && removedFromKB;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing logic rule: {RuleId}", ruleId);
                throw;
            }
        }

        /// <summary>
        /// Tüm mantık kurallarını getirir;
        /// </summary>
        public async Task<IEnumerable<LogicRule>> GetAllRulesAsync()
        {
            ValidateInitialization();

            lock (_lockObject)
            {
                return _ruleCache.Values.ToList();
            }
        }

        /// <summary>
        /// Kategoriye göre mantık kurallarını getirir;
        /// </summary>
        public async Task<IEnumerable<LogicRule>> GetRulesByCategoryAsync(LogicCategory category)
        {
            ValidateInitialization();

            lock (_lockObject)
            {
                return _ruleCache.Values;
                    .Where(r => r.Category == category && r.IsActive)
                    .OrderByDescending(r => r.Priority)
                    .ToList();
            }
        }

        /// <summary>
        /// Logic Engine istatistiklerini getirir;
        /// </summary>
        public async Task<LogicEngineStats> GetStatisticsAsync()
        {
            ValidateInitialization();

            try
            {
                var stats = new LogicEngineStats;
                {
                    TotalRules = _ruleCache.Count,
                    ActiveRules = _ruleCache.Values.Count(r => r.IsActive),
                    CacheSize = _ruleCache.Count + _inferenceCache.Count,
                    TaskQueueSize = _taskQueue.Count,
                    Uptime = DateTime.UtcNow - (StartTime ?? DateTime.UtcNow),
                    MemoryUsage = GC.GetTotalMemory(false),
                    IsProcessing = _isProcessing,
                    LastEvaluationTime = _lastEvaluationTime;
                };

                // Category breakdown;
                stats.CategoryBreakdown = _ruleCache.Values;
                    .GroupBy(r => r.Category)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());

                // Confidence distribution;
                var confidenceGroups = _ruleCache.Values;
                    .GroupBy(r => Math.Floor(r.Confidence * 10) / 10)
                    .OrderBy(g => g.Key);

                stats.ConfidenceDistribution = confidenceGroups;
                    .ToDictionary(g => $"{g.Key:0.0}-{g.Key + 0.1:0.0}", g => g.Count());

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                throw;
            }
        }

        /// <summary>
        /// Logic Engine'i durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                _logger.LogInformation("Shutting down LogicEngine...");

                _isProcessing = false;

                // Task queue'yu temizle;
                lock (_lockObject)
                {
                    _taskQueue.Clear();
                }

                // Inference system'i durdur;
                await _inferenceSystem.ShutdownAsync();

                // Cache'leri temizle;
                ClearCaches();

                _isInitialized = false;

                _logger.LogInformation("LogicEngine shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during shutdown");
                throw;
            }
        }

        /// <summary>
        /// Cache'leri temizler;
        /// </summary>
        private void ClearCaches()
        {
            lock (_lockObject)
            {
                _ruleCache.Clear();
                _inferenceCache.Clear();
            }

            _logger.LogDebug("LogicEngine caches cleared");
        }

        /// <summary>
        /// Başlatma durumunu kontrol eder;
        /// </summary>
        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new LogicEngineNotInitializedException(
                    "LogicEngine must be initialized before use. Call InitializeAsync() first.");
            }
        }

        /// <summary>
        /// Değerlendirme sonucunu cache'e ekler;
        /// </summary>
        private void CacheEvaluationResult(string expression, LogicEvaluationResult result)
        {
            if (_config.EnableCaching && result.IsValid && result.Confidence > _config.CacheThreshold)
            {
                var cacheKey = GenerateCacheKey(expression);
                // Implement caching logic here;
                _lastEvaluationTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Çıkarım sonucunu cache'e ekler;
        /// </summary>
        private void CacheInferenceResult(InferenceRequest request, InferenceResult result)
        {
            if (_config.EnableCaching && result.Confidence > _config.CacheThreshold)
            {
                var cacheKey = GenerateInferenceCacheKey(request);
                if (!_inferenceCache.ContainsKey(cacheKey))
                {
                    _inferenceCache[cacheKey] = new List<LogicInference>();
                }

                // Convert to LogicInference and cache;
                var inference = new LogicInference;
                {
                    Id = result.Id,
                    Premises = request.Premises,
                    Conclusions = result.Conclusions,
                    Confidence = result.Confidence,
                    Timestamp = result.Timestamp;
                };

                _inferenceCache[cacheKey].Add(inference);

                // Cache size management;
                ManageCacheSize();
            }
        }

        /// <summary>
        /// Cache boyutunu yönetir;
        /// </summary>
        private void ManageCacheSize()
        {
            if (_inferenceCache.Count > _config.MaxCacheSize)
            {
                // LRU strategy: Remove oldest entries;
                var oldestKey = _inferenceCache;
                    .OrderBy(kvp => kvp.Value.Min(v => v.Timestamp))
                    .FirstOrDefault().Key;

                if (oldestKey != null)
                {
                    _inferenceCache.Remove(oldestKey);
                    _logger.LogDebug("Removed oldest cache entry: {CacheKey}", oldestKey);
                }
            }
        }

        /// <summary>
        /// Confidence skorunu hesaplar;
        /// </summary>
        private double CalculateConfidenceScore(
            LogicEvaluationResult result,
            ParsedExpression parsedExpression)
        {
            if (!result.IsValid)
                return 0.0;

            var confidenceFactors = new List<double>();

            // Semantic analiz kalitesi;
            if (parsedExpression.SemanticInfo != null)
            {
                confidenceFactors.Add(parsedExpression.SemanticInfo.QualityScore);
            }

            // Data type consistency;
            confidenceFactors.Add(CalculateTypeConsistencyScore(result));

            // Historical accuracy (if available)
            if (_config.EnableLearning)
            {
                var historicalScore = CalculateHistoricalAccuracyScore(parsedExpression.Original);
                confidenceFactors.Add(historicalScore);
            }

            return confidenceFactors.Any() ? confidenceFactors.Average() : 0.7;
        }

        /// <summary>
        /// Disposable pattern implementation;
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

                ClearCaches();

                // Event handlers'ı temizle;
                OnLogicProcessed = null;
                OnDecisionMade = null;
                OnInferenceCompleted = null;
            }
        }

        // Helper methods for operator detection and tokenization;
        private LogicOperator DetectOperator(string expression, int position)
        {
            // Common logical operators;
            var operators = new Dictionary<string, LogicOperator>
            {
                { "∧", new LogicOperator("∧", OperatorType.LogicalAnd, 3) },
                { "∨", new LogicOperator("∨", OperatorType.LogicalOr, 2) },
                { "→", new LogicOperator("→", OperatorType.Implication, 1) },
                { "≡", new LogicOperator("≡", OperatorType.Equivalence, 1) },
                { "¬", new LogicOperator("¬", OperatorType.Negation, 4) },
                { "≠", new LogicOperator("≠", OperatorType.NotEqual, 1) },
                { "=", new LogicOperator("=", OperatorType.Equal, 1) },
                { ">", new LogicOperator(">", OperatorType.GreaterThan, 1) },
                { "<", new LogicOperator("<", OperatorType.LessThan, 1) },
                { "≥", new LogicOperator("≥", OperatorType.GreaterThanOrEqual, 1) },
                { "≤", new LogicOperator("≤", OperatorType.LessThanOrEqual, 1) },
                { "+", new LogicOperator("+", OperatorType.Addition, 2) },
                { "-", new LogicOperator("-", OperatorType.Subtraction, 2) },
                { "*", new LogicOperator("*", OperatorType.Multiplication, 3) },
                { "/", new LogicOperator("/", OperatorType.Division, 3) }
            };

            foreach (var op in operators)
            {
                if (position + op.Key.Length <= expression.Length &&
                    expression.Substring(position, op.Key.Length) == op.Key)
                {
                    return op.Value;
                }
            }

            return null;
        }

        private string ExtractNumber(string expression, ref int position)
        {
            var start = position;
            while (position < expression.Length &&
                  (char.IsDigit(expression[position]) || expression[position] == '.'))
            {
                position++;
            }
            return expression.Substring(start, position - start);
        }

        private string ExtractVariable(string expression, ref int position)
        {
            var start = position;
            while (position < expression.Length &&
                  (char.IsLetterOrDigit(expression[position]) || expression[position] == '_'))
            {
                position++;
            }
            return expression.Substring(start, position - start);
        }

        private int GetOperatorPrecedence(string op)
        {
            return op switch;
            {
                "¬" => 4,
                "∧" => 3,
                "∨" => 2,
                "→" => 1,
                "≡" => 1,
                _ => 0;
            };
        }

        private SyntaxNode BuildTreeFromRPN(List<LogicToken> rpnTokens)
        {
            var stack = new Stack<SyntaxNode>();

            foreach (var token in rpnTokens)
            {
                if (token.Type == LogicTokenType.Number || token.Type == LogicTokenType.Variable)
                {
                    stack.Push(new SyntaxNode;
                    {
                        Type = token.Type == LogicTokenType.Number ? NodeType.Number : NodeType.Variable,
                        Value = token.Value,
                        Position = token.Position;
                    });
                }
                else if (token.Type == LogicTokenType.Operator)
                {
                    if (stack.Count < 2)
                        throw new LogicParsingException("Invalid RPN expression");

                    var right = stack.Pop();
                    var left = stack.Pop();

                    stack.Push(new SyntaxNode;
                    {
                        Type = NodeType.Operation,
                        Value = token.Value,
                        Left = left,
                        Right = right,
                        Position = token.Position;
                    });
                }
            }

            if (stack.Count != 1)
                throw new LogicParsingException("Invalid RPN expression");

            return stack.Pop();
        }

        // Properties;
        public bool IsInitialized => _isInitialized;
        public bool IsProcessing => _isProcessing;
        public int RuleCount => _ruleCache.Count;
        public int CacheSize => _inferenceCache.Count;
        public DateTime? StartTime { get; private set; }
        private DateTime? _lastEvaluationTime;
    }

    #region Supporting Classes and Enums;

    public enum LogicCategory;
    {
        Deductive,
        Inductive,
        Abductive,
        Temporal,
        Modal,
        Fuzzy,
        Default;
    }

    public enum LogicPriority;
    {
        Critical = 0,
        High = 1,
        Medium = 2,
        Low = 3;
    }

    public enum LogicDataType;
    {
        Boolean,
        Number,
        String,
        DateTime,
        Object,
        Array,
        Unknown;
    }

    public enum LogicTokenType;
    {
        Number,
        Variable,
        Operator,
        Parenthesis,
        Function;
    }

    public enum NodeType;
    {
        Number,
        Variable,
        Operation,
        Function;
    }

    public enum OperatorType;
    {
        LogicalAnd,
        LogicalOr,
        Implication,
        Equivalence,
        Negation,
        Equal,
        NotEqual,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual,
        Addition,
        Subtraction,
        Multiplication,
        Division;
    }

    public class LogicEngineConfig;
    {
        public bool EnableCaching { get; set; } = true;
        public double CacheThreshold { get; set; } = 0.7;
        public int MaxCacheSize { get; set; } = 1000;
        public bool EnableLearning { get; set; } = true;
        public int MaxInferenceDepth { get; set; } = 10;
        public TimeSpan EvaluationTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool EnableParallelProcessing { get; set; } = true;
        public int MaxParallelTasks { get; set; } = Environment.ProcessorCount;
    }

    public class LogicRule;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Pattern { get; set; }
        public LogicPriority Priority { get; set; }
        public LogicCategory Category { get; set; }
        public List<string> Conditions { get; set; }
        public string Conclusion { get; set; }
        public double Confidence { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsed { get; set; }
        public int UsageCount { get; set; }
    }

    public class LogicContext;
    {
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public Dictionary<string, object> DecisionParameters { get; set; }
        public Dictionary<string, object> InferenceParameters { get; set; }
        public object EmotionalContext { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class LogicEvaluationResult;
    {
        public bool IsValid { get; set; }
        public object Value { get; set; }
        public LogicDataType DataType { get; set; }
        public double Confidence { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public object SemanticInfo { get; set; }
        public TimeSpan EvaluationTime { get; set; }
        public long MemoryUsage { get; set; }
        public object EmotionalContext { get; set; }
    }

    public class DecisionRequest;
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public List<DecisionOption> Options { get; set; }
        public List<DecisionCriterion> Criteria { get; set; }
        public Dictionary<string, object> Constraints { get; set; }
        public Dictionary<string, object> Preferences { get; set; }
        public DateTime RequestTime { get; set; }
    }

    public class DecisionResult;
    {
        public string RequestId { get; set; }
        public DecisionOption SelectedOption { get; set; }
        public List<DecisionOption> AlternativeOptions { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> CriterionScores { get; set; }
        public RiskAssessment RiskAssessment { get; set; }
        public EthicalAssessment EthicalAssessment { get; set; }
        public string Rationale { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime DecisionTime { get; set; }
    }

    public class InferenceRequest;
    {
        public string Id { get; set; }
        public List<string> Premises { get; set; }
        public List<LogicRule> AdditionalRules { get; set; }
        public Dictionary<string, object> Constraints { get; set; }
        public int MaxDepth { get; set; } = 5;
        public bool AllowUncertainty { get; set; } = true;
    }

    public class InferenceResult;
    {
        public string Id { get; set; }
        public List<string> Conclusions { get; set; }
        public double Confidence { get; set; }
        public List<LogicInference> InferenceChain { get; set; }
        public List<string> SupportingEvidence { get; set; }
        public List<string> Contradictions { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class LogicEngineStats;
    {
        public int TotalRules { get; set; }
        public int ActiveRules { get; set; }
        public int CacheSize { get; set; }
        public int TaskQueueSize { get; set; }
        public TimeSpan Uptime { get; set; }
        public long MemoryUsage { get; set; }
        public bool IsProcessing { get; set; }
        public DateTime? LastEvaluationTime { get; set; }
        public Dictionary<string, int> CategoryBreakdown { get; set; }
        public Dictionary<string, int> ConfidenceDistribution { get; set; }
    }

    // Additional supporting classes...
    public class LogicToken;
    {
        public LogicTokenType Type { get; set; }
        public string Value { get; set; }
        public int Position { get; set; }
        public int Length { get; set; }
    }

    public class SyntaxNode;
    {
        public NodeType Type { get; set; }
        public object Value { get; set; }
        public SyntaxNode Left { get; set; }
        public SyntaxNode Right { get; set; }
        public int Position { get; set; }
    }

    public class LogicOperator;
    {
        public string Symbol { get; set; }
        public OperatorType Type { get; set; }
        public int Precedence { get; set; }

        public LogicOperator(string symbol, OperatorType type, int precedence)
        {
            Symbol = symbol;
            Type = type;
            Precedence = precedence;
        }
    }

    public class ParsedExpression;
    {
        public string Original { get; set; }
        public List<LogicToken> Tokens { get; set; }
        public SyntaxNode SyntaxTree { get; set; }
        public object SemanticInfo { get; set; }
        public DateTime ParseTime { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class LogicEngineException : Exception
    {
        public LogicEngineException(string message) : base(message) { }
        public LogicEngineException(string message, Exception inner) : base(message, inner) { }
    }

    public class LogicEngineNotInitializedException : LogicEngineException;
    {
        public LogicEngineNotInitializedException(string message) : base(message) { }
    }

    public class LogicEvaluationException : LogicEngineException;
    {
        public LogicEvaluationException(string message) : base(message) { }
        public LogicEvaluationException(string message, Exception inner) : base(message, inner) { }
    }

    public class LogicParsingException : LogicEngineException;
    {
        public LogicParsingException(string message) : base(message) { }
    }

    public class DecisionMakingException : LogicEngineException;
    {
        public DecisionMakingException(string message) : base(message) { }
        public DecisionMakingException(string message, Exception inner) : base(message, inner) { }
    }

    public class InferenceException : LogicEngineException;
    {
        public InferenceException(string message) : base(message) { }
        public InferenceException(string message, Exception inner) : base(message, inner) { }
    }

    public class LogicRuleValidationException : LogicEngineException;
    {
        public LogicRuleValidationException(string message) : base(message) { }
    }

    #endregion;

    #region Events;

    public class LogicProcessingEventArgs : EventArgs;
    {
        public string Expression { get; set; }
        public LogicEvaluationResult Result { get; set; }
        public LogicContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class DecisionMadeEventArgs : EventArgs;
    {
        public string RequestId { get; set; }
        public DecisionResult Decision { get; set; }
        public LogicContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class InferenceCompletedEventArgs : EventArgs;
    {
        public InferenceRequest Request { get; set; }
        public InferenceResult Result { get; set; }
        public LogicContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}

// Interface definition for dependency injection;
public interface ILogicEngine : IDisposable
{
    Task InitializeAsync(LogicEngineConfig config = null);
    Task<LogicEvaluationResult> EvaluateExpressionAsync(string expression, LogicContext context = null);
    Task<DecisionResult> MakeDecisionAsync(DecisionRequest request, LogicContext context = null);
    Task<InferenceResult> MakeInferenceAsync(InferenceRequest request, LogicContext context = null);
    Task AddLogicRuleAsync(LogicRule rule);
    Task<bool> RemoveLogicRuleAsync(string ruleId);
    Task<IEnumerable<LogicRule>> GetAllRulesAsync();
    Task<IEnumerable<LogicRule>> GetRulesByCategoryAsync(LogicCategory category);
    Task<LogicEngineStats> GetStatisticsAsync();
    Task ShutdownAsync();

    bool IsInitialized { get; }
    bool IsProcessing { get; }
    int RuleCount { get; }
    int CacheSize { get; }

    event EventHandler<LogicProcessingEventArgs> OnLogicProcessed;
    event EventHandler<DecisionMadeEventArgs> OnDecisionMade;
    event EventHandler<InferenceCompletedEventArgs> OnInferenceCompleted;
}
