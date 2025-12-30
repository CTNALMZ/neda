// NEDA.Brain/KnowledgeBase/ProcedureLibrary/MethodLibrary.cs;
using Microsoft.Extensions.Logging;
using NEDA.Brain.DecisionMaking;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.KnowledgeBase.ProblemSolutions;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.Engine;
using NEDA.ExceptionHandling;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.Brain.KnowledgeBase.ProcedureLibrary;
{
    /// <summary>
    /// MethodLibrary - Yöntem ve prosedürlerin depolandığı, yönetildiği ve erişildiği merkezi kütüphane;
    /// </summary>
    public class MethodLibrary : IMethodLibrary;
    {
        private readonly ILogger<MethodLibrary> _logger;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly ILogicEngine _logicEngine;
        private readonly ISolutionBank _solutionBank;

        private readonly ConcurrentDictionary<string, MethodDefinition> _methods;
        private readonly ConcurrentDictionary<string, ProcedureTemplate> _procedures;
        private readonly ConcurrentDictionary<string, WorkflowPattern> _workflowPatterns;
        private readonly ConcurrentDictionary<MethodCategory, List<string>> _categoryIndex;

        private readonly object _syncLock = new object();
        private bool _isInitialized = false;

        /// <summary>
        /// MethodLibrary constructor;
        /// </summary>
        public MethodLibrary(
            ILogger<MethodLibrary> logger,
            IKnowledgeGraph knowledgeGraph,
            ILogicEngine logicEngine,
            ISolutionBank solutionBank)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _logicEngine = logicEngine ?? throw new ArgumentNullException(nameof(logicEngine));
            _solutionBank = solutionBank ?? throw new ArgumentNullException(nameof(solutionBank));

            _methods = new ConcurrentDictionary<string, MethodDefinition>(StringComparer.OrdinalIgnoreCase);
            _procedures = new ConcurrentDictionary<string, ProcedureTemplate>(StringComparer.OrdinalIgnoreCase);
            _workflowPatterns = new ConcurrentDictionary<string, WorkflowPattern>(StringComparer.OrdinalIgnoreCase);
            _categoryIndex = new ConcurrentDictionary<MethodCategory, List<string>>();

            _logger.LogInformation("MethodLibrary initialized");
        }

        /// <summary>
        /// Kütüphaneyi başlat ve temel metodları yükle;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing MethodLibrary...");

                // Temel metod kategorilerini oluştur;
                InitializeCategories();

                // Standart metodları yükle;
                await LoadStandardMethodsAsync(cancellationToken);

                // Sistem prosedürlerini yükle;
                await LoadSystemProceduresAsync(cancellationToken);

                // Workflow pattern'lerini yükle;
                await LoadWorkflowPatternsAsync(cancellationToken);

                // KnowledgeGraph ile entegrasyon;
                await IndexMethodsToKnowledgeGraphAsync(cancellationToken);

                _isInitialized = true;
                _logger.LogInformation("MethodLibrary initialized successfully with {MethodCount} methods, {ProcedureCount} procedures, {PatternCount} patterns",
                    _methods.Count, _procedures.Count, _workflowPatterns.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MethodLibrary");
                throw new LibraryInitializationException("MethodLibrary initialization failed", ex);
            }
        }

        /// <summary>
        /// Kategori yapısını başlat;
        /// </summary>
        private void InitializeCategories()
        {
            var categories = Enum.GetValues<MethodCategory>();
            foreach (var category in categories)
            {
                _categoryIndex[category] = new List<string>();
            }
        }

        /// <summary>
        /// Standart metodları yükle;
        /// </summary>
        private async Task LoadStandardMethodsAsync(CancellationToken cancellationToken)
        {
            var standardMethods = new List<MethodDefinition>
            {
                // Problem Çözme Metodları;
                CreateMethodDefinition(
                    id: "METHOD_PROBLEM_ANALYSIS",
                    name: "Problem Analysis",
                    description: "Sistematik problem analizi yöntemi",
                    category: MethodCategory.ProblemSolving,
                    steps: new List<MethodStep>
                    {
                        new MethodStep(1, "Problem tanımını netleştir", "PROBLEM_DEFINITION"),
                        new MethodStep(2, "Kök neden analizi yap", "ROOT_CAUSE_ANALYSIS"),
                        new MethodStep(3, "Etki analizi gerçekleştir", "IMPACT_ANALYSIS"),
                        new MethodStep(4, "Çözüm kriterlerini belirle", "SOLUTION_CRITERIA")
                    },
                    inputParameters: new Dictionary<string, ParameterDefinition>
                    {
                        { "problem", new ParameterDefinition("problem", "string", "Çözülecek problem") },
                        { "context", new ParameterDefinition("context", "string", "Problem bağlamı") }
                    },
                    outputParameters: new Dictionary<string, ParameterDefinition>
                    {
                        { "analysis", new ParameterDefinition("analysis", "ProblemAnalysis", "Problem analiz sonucu") },
                        { "rootCauses", new ParameterDefinition("rootCauses", "List<string>", "Kök nedenler") }
                    }
                ),
                
                // Karar Verme Metodları;
                CreateMethodDefinition(
                    id: "METHOD_DECISION_MATRIX",
                    name: "Decision Matrix Analysis",
                    description: "Karar matrisi analizi yöntemi",
                    category: MethodCategory.DecisionMaking,
                    steps: new List<MethodStep>
                    {
                        new MethodStep(1, "Kriterleri belirle", "DEFINE_CRITERIA"),
                        new MethodStep(2, "Ağırlıklandırma yap", "WEIGHT_CRITERIA"),
                        new MethodStep(3, "Alternatifleri puanla", "SCORE_ALTERNATIVES"),
                        new MethodStep(4, "Sonucu hesapla", "CALCULATE_RESULT")
                    }
                ),
                
                // Yaratıcı Düşünce Metodları;
                CreateMethodDefinition(
                    id: "METHOD_BRAINSTORMING",
                    name: "Structured Brainstorming",
                    description: "Yapılandırılmış beyin fırtınası tekniği",
                    category: MethodCategory.CreativeThinking,
                    steps: new List<MethodStep>
                    {
                        new MethodStep(1, "Temayı belirle", "DEFINE_THEME"),
                        new MethodStep(2, "Fikir üretme oturumu", "IDEA_GENERATION"),
                        new MethodStep(3, "Fikirleri grupla", "GROUP_IDEAS"),
                        new MethodStep(4, "En iyi fikirleri seç", "SELECT_BEST")
                    }
                ),
                
                // Optimizasyon Metodları;
                CreateMethodDefinition(
                    id: "METHOD_OPTIMIZATION_LOOP",
                    name: "Optimization Feedback Loop",
                    description: "Optimizasyon geri bildirim döngüsü",
                    category: MethodCategory.Optimization,
                    steps: new List<MethodStep>
                    {
                        new MethodStep(1, "Mevcut performansı ölç", "MEASURE_PERFORMANCE"),
                        new MethodStep(2, "İyileştirme alanlarını belirle", "IDENTIFY_IMPROVEMENTS"),
                        new MethodStep(3, "Değişiklikleri uygula", "APPLY_CHANGES"),
                        new MethodStep(4, "Sonuçları değerlendir", "EVALUATE_RESULTS"),
                        new MethodStep(5, "Döngüyü tekrarla", "ITERATE_LOOP")
                    }
                ),
                
                // Risk Yönetimi Metodları;
                CreateMethodDefinition(
                    id: "METHOD_RISK_ASSESSMENT",
                    name: "Comprehensive Risk Assessment",
                    description: "Kapsamlı risk değerlendirme metodolojisi",
                    category: MethodCategory.RiskManagement,
                    steps: new List<MethodStep>
                    {
                        new MethodStep(1, "Riskleri tanımla", "IDENTIFY_RISKS"),
                        new MethodStep(2, "Risk analizi yap", "ANALYZE_RISKS"),
                        new MethodStep(3, "Risk önceliklendirme", "PRIORITIZE_RISKS"),
                        new MethodStep(4, "Risk tepkilerini planla", "PLAN_RESPONSES")
                    }
                )
            };

            foreach (var method in standardMethods)
            {
                await AddMethodAsync(method, cancellationToken);
            }
        }

        /// <summary>
        /// Sistem prosedürlerini yükle;
        /// </summary>
        private async Task LoadSystemProceduresAsync(CancellationToken cancellationToken)
        {
            var procedures = new List<ProcedureTemplate>
            {
                new ProcedureTemplate(
                    id: "PROC_SYSTEM_DIAGNOSTICS",
                    name: "System Diagnostics Procedure",
                    description: "Tam sistem tanılama prosedürü",
                    methodIds: new List<string>
                    {
                        "METHOD_PROBLEM_ANALYSIS",
                        "METHOD_OPTIMIZATION_LOOP"
                    },
                    executionOrder: ProcedureExecutionOrder.Sequential,
                    timeout: TimeSpan.FromMinutes(5)
                ),

                new ProcedureTemplate(
                    id: "PROC_SECURITY_AUDIT",
                    name: "Security Audit Procedure",
                    description: "Güvenlik denetim prosedürü",
                    methodIds: new List<string>
                    {
                        "METHOD_RISK_ASSESSMENT",
                        "METHOD_DECISION_MATRIX"
                    },
                    executionOrder: ProcedureExecutionOrder.Parallel,
                    timeout: TimeSpan.FromMinutes(10)
                ),

                new ProcedureTemplate(
                    id: "PROC_PERFORMANCE_TUNING",
                    name: "Performance Tuning Procedure",
                    description: "Performans ayarlama prosedürü",
                    methodIds: new List<string>
                    {
                        "METHOD_OPTIMIZATION_LOOP",
                        "METHOD_PROBLEM_ANALYSIS"
                    },
                    executionOrder: ProcedureExecutionOrder.Sequential,
                    timeout: TimeSpan.FromMinutes(15)
                )
            };

            foreach (var procedure in procedures)
            {
                await AddProcedureAsync(procedure, cancellationToken);
            }
        }

        /// <summary>
        /// Workflow pattern'lerini yükle;
        /// </summary>
        private async Task LoadWorkflowPatternsAsync(CancellationToken cancellationToken)
        {
            var patterns = new List<WorkflowPattern>
            {
                new WorkflowPattern(
                    id: "PATTERN_ITERATIVE_DEVELOPMENT",
                    name: "Iterative Development Pattern",
                    description: "Yinelemeli geliştirme deseni",
                    phases: new List<WorkflowPhase>
                    {
                        new WorkflowPhase("Planning", new List<string> { "METHOD_PROBLEM_ANALYSIS" }),
                        new WorkflowPhase("Execution", new List<string> { "METHOD_OPTIMIZATION_LOOP" }),
                        new WorkflowPhase("Review", new List<string> { "METHOD_DECISION_MATRIX" })
                    },
                    conditions: new Dictionary<string, string>
                    {
                        { "phase_complete", "All phase methods must complete successfully" },
                        { "error_handling", "Continue on error with fallback" }
                    }
                ),

                new WorkflowPattern(
                    id: "PATTERN_AGILE_RESPONSE",
                    name: "Agile Response Pattern",
                    description: "Çevik tepki deseni",
                    phases: new List<WorkflowPhase>
                    {
                        new WorkflowPhase("Assess", new List<string> { "METHOD_RISK_ASSESSMENT" }),
                        new WorkflowPhase("Decide", new List<string> { "METHOD_DECISION_MATRIX" }),
                        new WorkflowPhase("Execute", new List<string> { "METHOD_OPTIMIZATION_LOOP" })
                    }
                )
            };

            foreach (var pattern in patterns)
            {
                await AddWorkflowPatternAsync(pattern, cancellationToken);
            }
        }

        /// <summary>
        /// Metodları KnowledgeGraph'e indeksle;
        /// </summary>
        private async Task IndexMethodsToKnowledgeGraphAsync(CancellationToken cancellationToken)
        {
            foreach (var method in _methods.Values)
            {
                var methodNode = new KnowledgeNode;
                {
                    Id = $"method:{method.Id}",
                    Type = "Method",
                    Properties = new Dictionary<string, object>
                    {
                        { "name", method.Name },
                        { "description", method.Description },
                        { "category", method.Category.ToString() },
                        { "complexity", method.Complexity },
                        { "successRate", method.SuccessRate }
                    }
                };

                await _knowledgeGraph.AddNodeAsync(methodNode, cancellationToken);

                // İlişkileri ekle;
                foreach (var relatedMethodId in method.RelatedMethodIds)
                {
                    if (_methods.ContainsKey(relatedMethodId))
                    {
                        await _knowledgeGraph.AddRelationshipAsync(
                            $"method:{method.Id}",
                            $"method:{relatedMethodId}",
                            "RELATED_TO",
                            cancellationToken);
                    }
                }
            }
        }

        /// <summary>
        /// Yeni bir metod tanımı oluştur;
        /// </summary>
        private MethodDefinition CreateMethodDefinition(
            string id,
            string name,
            string description,
            MethodCategory category,
            List<MethodStep> steps,
            Dictionary<string, ParameterDefinition> inputParameters = null,
            Dictionary<string, ParameterDefinition> outputParameters = null,
            double successRate = 0.85,
            ComplexityLevel complexity = ComplexityLevel.Medium,
            List<string> prerequisites = null,
            List<string> relatedMethodIds = null)
        {
            return new MethodDefinition;
            {
                Id = id,
                Name = name,
                Description = description,
                Category = category,
                Steps = steps,
                InputParameters = inputParameters ?? new Dictionary<string, ParameterDefinition>(),
                OutputParameters = outputParameters ?? new Dictionary<string, ParameterDefinition>(),
                SuccessRate = successRate,
                Complexity = complexity,
                CreatedDate = DateTime.UtcNow,
                LastModifiedDate = DateTime.UtcNow,
                Version = "1.0",
                Prerequisites = prerequisites ?? new List<string>(),
                RelatedMethodIds = relatedMethodIds ?? new List<string>(),
                Tags = GetDefaultTagsForCategory(category),
                IsActive = true,
                ExecutionCount = 0,
                AverageExecutionTime = TimeSpan.Zero;
            };
        }

        /// <summary>
        /// Kategoriye göre default tag'ler;
        /// </summary>
        private List<string> GetDefaultTagsForCategory(MethodCategory category)
        {
            return category switch;
            {
                MethodCategory.ProblemSolving => new List<string> { "analysis", "diagnostics", "troubleshooting" },
                MethodCategory.DecisionMaking => new List<string> { "decision", "evaluation", "selection" },
                MethodCategory.CreativeThinking => new List<string> { "creativity", "innovation", "ideation" },
                MethodCategory.Optimization => new List<string> { "optimization", "efficiency", "performance" },
                MethodCategory.RiskManagement => new List<string> { "risk", "security", "assessment" },
                MethodCategory.Learning => new List<string> { "learning", "training", "education" },
                MethodCategory.Communication => new List<string> { "communication", "collaboration", "coordination" },
                _ => new List<string> { "general" }
            };
        }

        #region Public Interface Implementation;

        public async Task<MethodDefinition> GetMethodAsync(string methodId, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(methodId, nameof(methodId));

            if (!_methods.TryGetValue(methodId, out var method))
            {
                throw new MethodNotFoundException($"Method '{methodId}' not found");
            }

            return await Task.FromResult(method);
        }

        public async Task<IEnumerable<MethodDefinition>> GetMethodsByCategoryAsync(
            MethodCategory category,
            CancellationToken cancellationToken = default)
        {
            if (!_categoryIndex.TryGetValue(category, out var methodIds))
            {
                return Enumerable.Empty<MethodDefinition>();
            }

            var methods = new List<MethodDefinition>();
            foreach (var methodId in methodIds)
            {
                if (_methods.TryGetValue(methodId, out var method))
                {
                    methods.Add(method);
                }
            }

            return await Task.FromResult(methods.AsEnumerable());
        }

        public async Task<IEnumerable<MethodDefinition>> SearchMethodsAsync(
            string query,
            MethodSearchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(query, nameof(query));
            options ??= new MethodSearchOptions();

            var results = new List<MethodDefinition>();
            var searchTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var method in _methods.Values)
            {
                if (!method.IsActive && options.ExcludeInactive)
                    continue;

                var score = CalculateSearchScore(method, searchTerms, options);
                if (score >= options.MinimumScore)
                {
                    results.Add(method);
                }
            }

            // Sıralama;
            results = options.SortBy switch;
            {
                MethodSortBy.Relevance => results.OrderByDescending(m => CalculateSearchScore(m, searchTerms, options)).ToList(),
                MethodSortBy.Name => results.OrderBy(m => m.Name).ToList(),
                MethodSortBy.SuccessRate => results.OrderByDescending(m => m.SuccessRate).ToList(),
                MethodSortBy.Complexity => results.OrderBy(m => m.Complexity).ToList(),
                MethodSortBy.ExecutionCount => results.OrderByDescending(m => m.ExecutionCount).ToList(),
                _ => results;
            };

            // Sayfalama;
            if (options.MaxResults > 0)
            {
                results = results.Take(options.MaxResults).ToList();
            }

            return await Task.FromResult(results.AsEnumerable());
        }

        private double CalculateSearchScore(MethodDefinition method, string[] searchTerms, MethodSearchOptions options)
        {
            double score = 0;

            foreach (var term in searchTerms)
            {
                // İsimde arama;
                if (method.Name.ToLowerInvariant().Contains(term))
                    score += 3.0;

                // Açıklamada arama;
                if (method.Description.ToLowerInvariant().Contains(term))
                    score += 1.0;

                // Tag'lerde arama;
                if (method.Tags.Any(tag => tag.ToLowerInvariant().Contains(term)))
                    score += 2.0;

                // Kategori eşleşmesi;
                if (options.Category.HasValue && method.Category == options.Category.Value)
                    score += 1.5;
            }

            return score;
        }

        public async Task<MethodDefinition> AddMethodAsync(
            MethodDefinition method,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(method, nameof(method));
            Guard.ArgumentNotNullOrEmpty(method.Id, nameof(method.Id));
            Guard.ArgumentNotNullOrEmpty(method.Name, nameof(method.Name));

            lock (_syncLock)
            {
                if (_methods.ContainsKey(method.Id))
                {
                    throw new MethodAlreadyExistsException($"Method '{method.Id}' already exists");
                }

                // Validasyon;
                ValidateMethod(method);

                // Tarih bilgilerini güncelle;
                method.CreatedDate = DateTime.UtcNow;
                method.LastModifiedDate = DateTime.UtcNow;
                method.Version = method.Version ?? "1.0";

                // Koleksiyona ekle;
                if (!_methods.TryAdd(method.Id, method))
                {
                    throw new ConcurrentOperationException($"Failed to add method '{method.Id}'");
                }

                // Kategori indeksine ekle;
                if (_categoryIndex.TryGetValue(method.Category, out var categoryList))
                {
                    categoryList.Add(method.Id);
                }

                _logger.LogInformation("Method '{MethodId}' added to library", method.Id);
            }

            // KnowledgeGraph'e ekle;
            await IndexMethodToKnowledgeGraphAsync(method, cancellationToken);

            return await Task.FromResult(method);
        }

        private void ValidateMethod(MethodDefinition method)
        {
            if (method.Steps == null || !method.Steps.Any())
                throw new InvalidMethodException("Method must have at least one step");

            if (method.SuccessRate < 0 || method.SuccessRate > 1)
                throw new InvalidMethodException("Success rate must be between 0 and 1");

            if (method.Complexity < ComplexityLevel.VeryLow || method.Complexity > ComplexityLevel.VeryHigh)
                throw new InvalidMethodException("Invalid complexity level");
        }

        private async Task IndexMethodToKnowledgeGraphAsync(MethodDefinition method, CancellationToken cancellationToken)
        {
            var methodNode = new KnowledgeNode;
            {
                Id = $"method:{method.Id}",
                Type = "Method",
                Properties = new Dictionary<string, object>
                {
                    { "name", method.Name },
                    { "description", method.Description },
                    { "category", method.Category.ToString() },
                    { "complexity", method.Complexity.ToString() },
                    { "successRate", method.SuccessRate },
                    { "version", method.Version },
                    { "createdDate", method.CreatedDate },
                    { "isActive", method.IsActive }
                }
            };

            await _knowledgeGraph.AddNodeAsync(methodNode, cancellationToken);
        }

        public async Task<MethodDefinition> UpdateMethodAsync(
            string methodId,
            Action<MethodDefinition> updateAction,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(methodId, nameof(methodId));
            Guard.ArgumentNotNull(updateAction, nameof(updateAction));

            if (!_methods.TryGetValue(methodId, out var method))
            {
                throw new MethodNotFoundException($"Method '{methodId}' not found");
            }

            lock (_syncLock)
            {
                // Update işlemi;
                updateAction(method);

                // Validasyon;
                ValidateMethod(method);

                // Metadata güncelle;
                method.LastModifiedDate = DateTime.UtcNow;
                method.Version = IncrementVersion(method.Version);

                // Kategori değişti mi kontrol et;
                var oldCategory = method.Category;
                if (method.Category != oldCategory)
                {
                    // Eski kategoriden çıkar;
                    if (_categoryIndex.TryGetValue(oldCategory, out var oldList))
                    {
                        oldList.Remove(methodId);
                    }

                    // Yeni kategoriye ekle;
                    if (_categoryIndex.TryGetValue(method.Category, out var newList))
                    {
                        newList.Add(methodId);
                    }
                }

                _logger.LogInformation("Method '{MethodId}' updated", methodId);
            }

            // KnowledgeGraph'i güncelle;
            await UpdateMethodInKnowledgeGraphAsync(method, cancellationToken);

            return await Task.FromResult(method);
        }

        private string IncrementVersion(string currentVersion)
        {
            if (string.IsNullOrEmpty(currentVersion))
                return "1.0";

            var parts = currentVersion.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var minor))
            {
                return $"{parts[0]}.{minor + 1}";
            }

            return currentVersion;
        }

        private async Task UpdateMethodInKnowledgeGraphAsync(MethodDefinition method, CancellationToken cancellationToken)
        {
            var updateProperties = new Dictionary<string, object>
            {
                { "name", method.Name },
                { "description", method.Description },
                { "category", method.Category.ToString() },
                { "complexity", method.Complexity.ToString() },
                { "successRate", method.SuccessRate },
                { "version", method.Version },
                { "lastModifiedDate", method.LastModifiedDate },
                { "isActive", method.IsActive },
                { "executionCount", method.ExecutionCount },
                { "averageExecutionTime", method.AverageExecutionTime.TotalMilliseconds }
            };

            await _knowledgeGraph.UpdateNodePropertiesAsync(
                $"method:{method.Id}",
                updateProperties,
                cancellationToken);
        }

        public async Task<bool> DeleteMethodAsync(string methodId, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(methodId, nameof(methodId));

            if (!_methods.TryRemove(methodId, out var removedMethod))
            {
                return await Task.FromResult(false);
            }

            lock (_syncLock)
            {
                // Kategori indeksinden çıkar;
                if (_categoryIndex.TryGetValue(removedMethod.Category, out var categoryList))
                {
                    categoryList.Remove(methodId);
                }

                _logger.LogInformation("Method '{MethodId}' deleted", methodId);
            }

            // KnowledgeGraph'ten sil;
            await _knowledgeGraph.RemoveNodeAsync($"method:{methodId}", cancellationToken);

            return await Task.FromResult(true);
        }

        public async Task<MethodExecutionResult> ExecuteMethodAsync(
            string methodId,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(methodId, nameof(methodId));
            Guard.ArgumentNotNull(context, nameof(context));

            var method = await GetMethodAsync(methodId, cancellationToken);

            // Input parametrelerini validasyon;
            ValidateParameters(method.InputParameters, parameters);

            // Execution tracking;
            var executionId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            _logger.LogInformation("Starting execution of method '{MethodId}' with execution ID: {ExecutionId}",
                methodId, executionId);

            try
            {
                // Metodun adımlarını sırayla çalıştır;
                var stepResults = new List<StepExecutionResult>();
                var outputValues = new Dictionary<string, object>();

                foreach (var step in method.Steps.OrderBy(s => s.Order))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Method execution cancelled");
                    }

                    var stepResult = await ExecuteStepAsync(step, parameters, context, cancellationToken);
                    stepResults.Add(stepResult);

                    // Step output'larını topla;
                    if (stepResult.OutputValues != null)
                    {
                        foreach (var output in stepResult.OutputValues)
                        {
                            outputValues[output.Key] = output.Value;
                        }
                    }

                    // Step başarısız olduysa;
                    if (!stepResult.IsSuccess)
                    {
                        _logger.LogWarning("Step '{StepId}' failed in method '{MethodId}'", step.Id, methodId);
                        break;
                    }
                }

                // Execution süresini hesapla;
                var executionTime = DateTime.UtcNow - startTime;

                // Success rate'i güncelle;
                var success = stepResults.All(r => r.IsSuccess);
                await UpdateMethodStatisticsAsync(methodId, executionTime, success, cancellationToken);

                // Sonucu oluştur;
                var result = new MethodExecutionResult;
                {
                    ExecutionId = executionId,
                    MethodId = methodId,
                    IsSuccess = success,
                    ExecutionTime = executionTime,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    StepResults = stepResults,
                    OutputValues = outputValues,
                    ErrorMessage = success ? null : "One or more steps failed"
                };

                _logger.LogInformation("Method '{MethodId}' execution completed with success: {Success}, time: {ExecutionTime}ms",
                    methodId, success, executionTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing method '{MethodId}'", methodId);

                // Statistics güncelle;
                await UpdateMethodStatisticsAsync(methodId, DateTime.UtcNow - startTime, false, cancellationToken);

                throw new MethodExecutionException($"Error executing method '{methodId}'", ex);
            }
        }

        private void ValidateParameters(
            Dictionary<string, ParameterDefinition> expectedParams,
            Dictionary<string, object> providedParams)
        {
            // Gerekli parametreleri kontrol et;
            foreach (var param in expectedParams.Values.Where(p => p.IsRequired))
            {
                if (!providedParams.ContainsKey(param.Name))
                {
                    throw new ParameterValidationException($"Required parameter '{param.Name}' not provided");
                }

                // Type kontrolü (basit)
                if (param.Type != "object" && providedParams[param.Name]?.GetType().Name != param.Type)
                {
                    // Burada daha gelişmiş type checking yapılabilir;
                }
            }
        }

        private async Task<StepExecutionResult> ExecuteStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            var stepStartTime = DateTime.UtcNow;

            try
            {
                // Step tipine göre işlem yap;
                var result = step.Type switch;
                {
                    "PROBLEM_DEFINITION" => await ExecuteProblemDefinitionStepAsync(step, parameters, context, cancellationToken),
                    "ROOT_CAUSE_ANALYSIS" => await ExecuteRootCauseAnalysisStepAsync(step, parameters, context, cancellationToken),
                    "DECISION_MATRIX" => await ExecuteDecisionMatrixStepAsync(step, parameters, context, cancellationToken),
                    "BRAINSTORMING" => await ExecuteBrainstormingStepAsync(step, parameters, context, cancellationToken),
                    "OPTIMIZATION" => await ExecuteOptimizationStepAsync(step, parameters, context, cancellationToken),
                    _ => await ExecuteGenericStepAsync(step, parameters, context, cancellationToken)
                };

                var executionTime = DateTime.UtcNow - stepStartTime;

                return new StepExecutionResult;
                {
                    StepId = step.Id,
                    StepName = step.Name,
                    IsSuccess = true,
                    ExecutionTime = executionTime,
                    OutputValues = result,
                    Messages = new List<string> { $"Step '{step.Name}' completed successfully" }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing step '{StepId}'", step.Id);

                return new StepExecutionResult;
                {
                    StepId = step.Id,
                    StepName = step.Name,
                    IsSuccess = false,
                    ExecutionTime = DateTime.UtcNow - stepStartTime,
                    ErrorMessage = ex.Message,
                    Exception = ex;
                };
            }
        }

        private async Task<Dictionary<string, object>> ExecuteProblemDefinitionStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Problem tanımlama adımı;
            var problem = parameters.GetValueOrDefault("problem") as string;

            // LogicEngine kullanarak problem analizi;
            var analysis = await _logicEngine.AnalyzeProblemAsync(problem, context, cancellationToken);

            // SolutionBank'tan benzer çözümleri ara;
            var similarSolutions = await _solutionBank.FindSimilarSolutionsAsync(problem, cancellationToken);

            return new Dictionary<string, object>
            {
                { "problemAnalysis", analysis },
                { "similarSolutions", similarSolutions },
                { "definedProblem", $"Analyzed problem: {problem}" }
            };
        }

        private async Task<Dictionary<string, object>> ExecuteRootCauseAnalysisStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Kök neden analizi adımı;
            var problemAnalysis = parameters.GetValueOrDefault("problemAnalysis");

            // 5 Whys tekniği veya benzeri metodoloji uygula;
            var rootCauses = await _logicEngine.FindRootCausesAsync(problemAnalysis, context, cancellationToken);

            return new Dictionary<string, object>
            {
                { "rootCauses", rootCauses },
                { "causeCount", rootCauses.Count },
                { "primaryCause", rootCauses.FirstOrDefault() }
            };
        }

        private async Task<Dictionary<string, object>> ExecuteDecisionMatrixStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Karar matrisi adımı;
            var alternatives = parameters.GetValueOrDefault("alternatives") as List<object> ?? new List<object>();
            var criteria = parameters.GetValueOrDefault("criteria") as List<string> ?? new List<string>();

            // LogicEngine ile karar analizi;
            var decision = await _logicEngine.EvaluateDecisionMatrixAsync(alternatives, criteria, context, cancellationToken);

            return new Dictionary<string, object>
            {
                { "decisionResult", decision },
                { "bestAlternative", decision.BestAlternative },
                { "confidenceScore", decision.ConfidenceScore }
            };
        }

        private async Task<Dictionary<string, object>> ExecuteBrainstormingStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Beyin fırtınası adımı;
            var theme = parameters.GetValueOrDefault("theme") as string;
            var constraints = parameters.GetValueOrDefault("constraints") as List<string> ?? new List<string>();

            // Creative thinking engine kullan;
            var ideas = await _logicEngine.GenerateIdeasAsync(theme, constraints, context, cancellationToken);

            return new Dictionary<string, object>
            {
                { "generatedIdeas", ideas },
                { "ideaCount", ideas.Count },
                { "topIdeas", ideas.Take(5).ToList() }
            };
        }

        private async Task<Dictionary<string, object>> ExecuteOptimizationStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Optimizasyon adımı;
            var currentState = parameters.GetValueOrDefault("currentState");
            var targetMetrics = parameters.GetValueOrDefault("targetMetrics") as Dictionary<string, double>;

            // Optimizasyon algoritması uygula;
            var optimizationResult = await _logicEngine.OptimizeAsync(currentState, targetMetrics, context, cancellationToken);

            return new Dictionary<string, object>
            {
                { "optimizationResult", optimizationResult },
                { "improvementPercentage", optimizationResult.ImprovementPercentage },
                { "recommendedChanges", optimizationResult.RecommendedChanges }
            };
        }

        private async Task<Dictionary<string, object>> ExecuteGenericStepAsync(
            MethodStep step,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Generic step execution;
            await Task.Delay(100, cancellationToken); // Simüle edilmiş işlem;

            return new Dictionary<string, object>
            {
                { "stepCompleted", true },
                { "stepName", step.Name },
                { "executionTimestamp", DateTime.UtcNow }
            };
        }

        private async Task UpdateMethodStatisticsAsync(
            string methodId,
            TimeSpan executionTime,
            bool success,
            CancellationToken cancellationToken)
        {
            if (!_methods.TryGetValue(methodId, out var method))
                return;

            lock (_syncLock)
            {
                method.ExecutionCount++;

                // Ortalama execution time'ı güncelle;
                if (method.AverageExecutionTime == TimeSpan.Zero)
                {
                    method.AverageExecutionTime = executionTime;
                }
                else;
                {
                    method.AverageExecutionTime = TimeSpan.FromMilliseconds(
                        (method.AverageExecutionTime.TotalMilliseconds * (method.ExecutionCount - 1) + executionTime.TotalMilliseconds) / method.ExecutionCount);
                }

                // Success rate'i güncelle;
                var successCount = method.ExecutionCount * method.SuccessRate;
                successCount = success ? successCount + 1 : successCount;
                method.SuccessRate = successCount / method.ExecutionCount;

                method.LastModifiedDate = DateTime.UtcNow;
            }

            // KnowledgeGraph'i güncelle;
            await UpdateMethodInKnowledgeGraphAsync(method, cancellationToken);
        }

        public async Task<ProcedureTemplate> GetProcedureAsync(string procedureId, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(procedureId, nameof(procedureId));

            if (!_procedures.TryGetValue(procedureId, out var procedure))
            {
                throw new ProcedureNotFoundException($"Procedure '{procedureId}' not found");
            }

            return await Task.FromResult(procedure);
        }

        public async Task<ProcedureExecutionResult> ExecuteProcedureAsync(
            string procedureId,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var procedure = await GetProcedureAsync(procedureId, cancellationToken);

            var executionId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            _logger.LogInformation("Starting execution of procedure '{ProcedureId}'", procedureId);

            try
            {
                var methodResults = new List<MethodExecutionResult>();
                var outputValues = new Dictionary<string, object>();

                // Prosedürdeki metodları sırayla çalıştır;
                foreach (var methodId in procedure.MethodIds)
                {
                    var methodResult = await ExecuteMethodAsync(
                        methodId,
                        parameters,
                        context,
                        cancellationToken);

                    methodResults.Add(methodResult);

                    // Output'ları birleştir;
                    if (methodResult.OutputValues != null)
                    {
                        foreach (var output in methodResult.OutputValues)
                        {
                            outputValues[output.Key] = output.Value;
                        }
                    }

                    // Başarısız metod varsa prosedürü durdur;
                    if (!methodResult.IsSuccess && procedure.StopOnFailure)
                    {
                        break;
                    }
                }

                var executionTime = DateTime.UtcNow - startTime;
                var success = methodResults.All(r => r.IsSuccess);

                var result = new ProcedureExecutionResult;
                {
                    ExecutionId = executionId,
                    ProcedureId = procedureId,
                    IsSuccess = success,
                    ExecutionTime = executionTime,
                    MethodResults = methodResults,
                    OutputValues = outputValues;
                };

                _logger.LogInformation("Procedure '{ProcedureId}' execution completed", procedureId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing procedure '{ProcedureId}'", procedureId);
                throw new ProcedureExecutionException($"Error executing procedure '{procedureId}'", ex);
            }
        }

        public async Task<IEnumerable<MethodDefinition>> RecommendMethodsAsync(
            ProblemContext problemContext,
            RecommendationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(problemContext, nameof(problemContext));
            options ??= new RecommendationOptions();

            var candidates = new List<MethodRecommendation>();

            // Tüm metodları değerlendir;
            foreach (var method in _methods.Values.Where(m => m.IsActive))
            {
                var relevanceScore = CalculateRelevanceScore(method, problemContext, options);
                var confidenceScore = CalculateConfidenceScore(method, problemContext);

                if (relevanceScore >= options.MinimumRelevance)
                {
                    candidates.Add(new MethodRecommendation;
                    {
                        Method = method,
                        RelevanceScore = relevanceScore,
                        ConfidenceScore = confidenceScore,
                        CompositeScore = relevanceScore * 0.7 + confidenceScore * 0.3;
                    });
                }
            }

            // Sırala ve filtrele;
            var recommendations = candidates;
                .OrderByDescending(c => c.CompositeScore)
                .Take(options.MaxRecommendations)
                .Select(c => c.Method)
                .ToList();

            return await Task.FromResult(recommendations.AsEnumerable());
        }

        private double CalculateRelevanceScore(MethodDefinition method, ProblemContext context, RecommendationOptions options)
        {
            double score = 0;

            // Kategori eşleşmesi;
            if (context.PreferredCategories.Contains(method.Category))
                score += 2.0;

            // Tag eşleşmesi;
            var matchingTags = method.Tags.Intersect(context.ProblemTags).Count();
            score += matchingTags * 0.5;

            // Complexity uygunluğu;
            if (method.Complexity <= context.MaxAllowedComplexity)
                score += 1.0;

            // Success rate;
            score += method.SuccessRate * 0.5;

            return score;
        }

        private double CalculateConfidenceScore(MethodDefinition method, ProblemContext context)
        {
            // Execution history'e göre confidence;
            double confidence = method.SuccessRate;

            // Daha fazla execution daha yüksek confidence;
            if (method.ExecutionCount > 10)
                confidence += 0.1;
            else if (method.ExecutionCount > 50)
                confidence += 0.2;

            // Recent success;
            if (method.LastModifiedDate > DateTime.UtcNow.AddDays(-30))
                confidence += 0.05;

            return Math.Min(confidence, 1.0);
        }

        public async Task<MethodLibraryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            var stats = new MethodLibraryStatistics;
            {
                TotalMethods = _methods.Count,
                TotalProcedures = _procedures.Count,
                TotalWorkflowPatterns = _workflowPatterns.Count,
                MethodsByCategory = _categoryIndex.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Count),
                AverageSuccessRate = _methods.Values.Average(m => m.SuccessRate),
                MostExecutedMethods = _methods.Values;
                    .OrderByDescending(m => m.ExecutionCount)
                    .Take(5)
                    .Select(m => new MethodExecutionStats;
                    {
                        MethodId = m.Id,
                        MethodName = m.Name,
                        ExecutionCount = m.ExecutionCount,
                        AverageExecutionTime = m.AverageExecutionTime,
                        SuccessRate = m.SuccessRate;
                    })
                    .ToList(),
                LastUpdated = _methods.Values.Max(m => m.LastModifiedDate)
            };

            return await Task.FromResult(stats);
        }

        #endregion;

        #region Helper Methods;

        public async Task<bool> ExportLibraryAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));

            try
            {
                var exportData = new MethodLibraryExport;
                {
                    Methods = _methods.Values.ToList(),
                    Procedures = _procedures.Values.ToList(),
                    WorkflowPatterns = _workflowPatterns.Values.ToList(),
                    ExportDate = DateTime.UtcNow,
                    Version = "1.0"
                };

                var json = JsonConvert.SerializeObject(exportData, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json, cancellationToken);

                _logger.LogInformation("MethodLibrary exported to '{FilePath}'", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export MethodLibrary to '{FilePath}'", filePath);
                throw new LibraryExportException($"Failed to export library to '{filePath}'", ex);
            }
        }

        public async Task<bool> ImportLibraryAsync(string filePath, ImportOptions options = null, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));
            options ??= new ImportOptions();

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Import file not found: {filePath}");

            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var importData = JsonConvert.DeserializeObject<MethodLibraryExport>(json);

                if (importData == null)
                    throw new InvalidDataException("Invalid import file format");

                lock (_syncLock)
                {
                    // Clear existing data if requested;
                    if (options.ClearExisting)
                    {
                        _methods.Clear();
                        _procedures.Clear();
                        _workflowPatterns.Clear();
                        _categoryIndex.Clear();
                        InitializeCategories();
                    }

                    // Import methods;
                    foreach (var method in importData.Methods)
                    {
                        if (options.OverwriteExisting || !_methods.ContainsKey(method.Id))
                        {
                            _methods[method.Id] = method;

                            // Kategori indeksini güncelle;
                            if (_categoryIndex.TryGetValue(method.Category, out var categoryList))
                            {
                                if (!categoryList.Contains(method.Id))
                                    categoryList.Add(method.Id);
                            }
                        }
                    }

                    // Import procedures;
                    foreach (var procedure in importData.Procedures)
                    {
                        if (options.OverwriteExisting || !_procedures.ContainsKey(procedure.Id))
                        {
                            _procedures[procedure.Id] = procedure;
                        }
                    }

                    // Import patterns;
                    foreach (var pattern in importData.WorkflowPatterns)
                    {
                        if (options.OverwriteExisting || !_workflowPatterns.ContainsKey(pattern.Id))
                        {
                            _workflowPatterns[pattern.Id] = pattern;
                        }
                    }
                }

                // Re-index to KnowledgeGraph;
                await ReindexToKnowledgeGraphAsync(cancellationToken);

                _logger.LogInformation("MethodLibrary imported from '{FilePath}' with {MethodCount} methods",
                    filePath, importData.Methods.Count);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import MethodLibrary from '{FilePath}'", filePath);
                throw new LibraryImportException($"Failed to import library from '{filePath}'", ex);
            }
        }

        private async Task ReindexToKnowledgeGraphAsync(CancellationToken cancellationToken)
        {
            // Clear existing method nodes;
            await _knowledgeGraph.RemoveNodesByTypeAsync("Method", cancellationToken);

            // Re-index all methods;
            foreach (var method in _methods.Values)
            {
                await IndexMethodToKnowledgeGraphAsync(method, cancellationToken);
            }
        }

        public async Task<IEnumerable<MethodDefinition>> GetMethodsByTagsAsync(
            IEnumerable<string> tags,
            bool matchAll = false,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(tags, nameof(tags));

            var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            var results = new List<MethodDefinition>();

            foreach (var method in _methods.Values)
            {
                var methodTags = new HashSet<string>(method.Tags, StringComparer.OrdinalIgnoreCase);

                if (matchAll)
                {
                    if (tagSet.IsSubsetOf(methodTags))
                        results.Add(method);
                }
                else;
                {
                    if (tagSet.Overlaps(methodTags))
                        results.Add(method);
                }
            }

            return await Task.FromResult(results.AsEnumerable());
        }

        public async Task<MethodDefinition> FindBestMethodForProblemAsync(
            string problemDescription,
            ProblemConstraints constraints,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(problemDescription, nameof(problemDescription));
            Guard.ArgumentNotNull(constraints, nameof(constraints));

            // NLP ile problem analizi (basit)
            var problemTags = ExtractTagsFromProblem(problemDescription);

            var context = new ProblemContext;
            {
                ProblemDescription = problemDescription,
                ProblemTags = problemTags.ToList(),
                PreferredCategories = constraints.PreferredCategories,
                MaxAllowedComplexity = constraints.MaxComplexity,
                TimeConstraint = constraints.TimeLimit;
            };

            var recommendations = await RecommendMethodsAsync(context, new RecommendationOptions;
            {
                MaxRecommendations = 1,
                MinimumRelevance = 0.5;
            }, cancellationToken);

            return recommendations.FirstOrDefault();
        }

        private IEnumerable<string> ExtractTagsFromProblem(string problemDescription)
        {
            // Basit keyword extraction;
            var keywords = new List<string> { "problem", "error", "issue", "bug", "failure", "performance", "security" };
            var tags = new List<string>();

            foreach (var keyword in keywords)
            {
                if (problemDescription.ToLowerInvariant().Contains(keyword))
                    tags.Add(keyword);
            }

            return tags.Distinct();
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    /// <summary>
    /// MethodLibrary interface;
    /// </summary>
    public interface IMethodLibrary;
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<MethodDefinition> GetMethodAsync(string methodId, CancellationToken cancellationToken = default);
        Task<IEnumerable<MethodDefinition>> GetMethodsByCategoryAsync(MethodCategory category, CancellationToken cancellationToken = default);
        Task<IEnumerable<MethodDefinition>> SearchMethodsAsync(string query, MethodSearchOptions options = null, CancellationToken cancellationToken = default);
        Task<MethodDefinition> AddMethodAsync(MethodDefinition method, CancellationToken cancellationToken = default);
        Task<MethodDefinition> UpdateMethodAsync(string methodId, Action<MethodDefinition> updateAction, CancellationToken cancellationToken = default);
        Task<bool> DeleteMethodAsync(string methodId, CancellationToken cancellationToken = default);
        Task<MethodExecutionResult> ExecuteMethodAsync(string methodId, Dictionary<string, object> parameters, ExecutionContext context, CancellationToken cancellationToken = default);
        Task<ProcedureTemplate> GetProcedureAsync(string procedureId, CancellationToken cancellationToken = default);
        Task<ProcedureExecutionResult> ExecuteProcedureAsync(string procedureId, Dictionary<string, object> parameters, ExecutionContext context, CancellationToken cancellationToken = default);
        Task<IEnumerable<MethodDefinition>> RecommendMethodsAsync(ProblemContext problemContext, RecommendationOptions options = null, CancellationToken cancellationToken = default);
        Task<MethodLibraryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task<bool> ExportLibraryAsync(string filePath, CancellationToken cancellationToken = default);
        Task<bool> ImportLibraryAsync(string filePath, ImportOptions options = null, CancellationToken cancellationToken = default);
        Task<IEnumerable<MethodDefinition>> GetMethodsByTagsAsync(IEnumerable<string> tags, bool matchAll = false, CancellationToken cancellationToken = default);
        Task<MethodDefinition> FindBestMethodForProblemAsync(string problemDescription, ProblemConstraints constraints, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Method definition;
    /// </summary>
    public class MethodDefinition;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public MethodCategory Category { get; set; }
        public List<MethodStep> Steps { get; set; }
        public Dictionary<string, ParameterDefinition> InputParameters { get; set; }
        public Dictionary<string, ParameterDefinition> OutputParameters { get; set; }
        public double SuccessRate { get; set; }
        public ComplexityLevel Complexity { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public string Version { get; set; }
        public List<string> Prerequisites { get; set; }
        public List<string> RelatedMethodIds { get; set; }
        public List<string> Tags { get; set; }
        public bool IsActive { get; set; }
        public int ExecutionCount { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
    }

    /// <summary>
    /// Method step;
    /// </summary>
    public class MethodStep;
    {
        public MethodStep(int order, string name, string type)
        {
            Id = Guid.NewGuid().ToString();
            Order = order;
            Name = name;
            Type = type;
            Description = name;
        }

        public string Id { get; }
        public int Order { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> OutputMapping { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Parameter definition;
    /// </summary>
    public class ParameterDefinition;
    {
        public ParameterDefinition(string name, string type, string description, bool isRequired = true)
        {
            Name = name;
            Type = type;
            Description = description;
            IsRequired = isRequired;
        }

        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public bool IsRequired { get; set; }
        public object DefaultValue { get; set; }
        public List<string> AllowedValues { get; set; } = new List<string>();
        public ValidationRule ValidationRule { get; set; }
    }

    /// <summary>
    /// Procedure template;
    /// </summary>
    public class ProcedureTemplate;
    {
        public ProcedureTemplate(
            string id,
            string name,
            string description,
            List<string> methodIds,
            ProcedureExecutionOrder executionOrder,
            TimeSpan timeout)
        {
            Id = id;
            Name = name;
            Description = description;
            MethodIds = methodIds;
            ExecutionOrder = executionOrder;
            Timeout = timeout;
            CreatedDate = DateTime.UtcNow;
            StopOnFailure = true;
        }

        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> MethodIds { get; set; }
        public ProcedureExecutionOrder ExecutionOrder { get; set; }
        public TimeSpan Timeout { get; set; }
        public bool StopOnFailure { get; set; }
        public Dictionary<string, object> DefaultParameters { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedDate { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public string Version { get; set; } = "1.0";
    }

    /// <summary>
    /// Workflow pattern;
    /// </summary>
    public class WorkflowPattern;
    {
        public WorkflowPattern(string id, string name, string description, List<WorkflowPhase> phases, Dictionary<string, string> conditions = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Phases = phases;
            Conditions = conditions ?? new Dictionary<string, string>();
            CreatedDate = DateTime.UtcNow;
        }

        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<WorkflowPhase> Phases { get; set; }
        public Dictionary<string, string> Conditions { get; set; }
        public DateTime CreatedDate { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Workflow phase;
    /// </summary>
    public class WorkflowPhase;
    {
        public WorkflowPhase(string name, List<string> methodIds)
        {
            Name = name;
            MethodIds = methodIds;
            PhaseId = Guid.NewGuid().ToString();
        }

        public string PhaseId { get; }
        public string Name { get; set; }
        public List<string> MethodIds { get; set; }
        public Dictionary<string, object> PhaseParameters { get; set; } = new Dictionary<string, object>();
        public List<PhaseTransition> Transitions { get; set; } = new List<PhaseTransition>();
    }

    /// <summary>
    /// Method execution result;
    /// </summary>
    public class MethodExecutionResult;
    {
        public string ExecutionId { get; set; }
        public string MethodId { get; set; }
        public bool IsSuccess { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<StepExecutionResult> StepResults { get; set; }
        public Dictionary<string, object> OutputValues { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> ExecutionContext { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Step execution result;
    /// </summary>
    public class StepExecutionResult;
    {
        public string StepId { get; set; }
        public string StepName { get; set; }
        public bool IsSuccess { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public Dictionary<string, object> OutputValues { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
    }

    /// <summary>
    /// Procedure execution result;
    /// </summary>
    public class ProcedureExecutionResult;
    {
        public string ExecutionId { get; set; }
        public string ProcedureId { get; set; }
        public bool IsSuccess { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public List<MethodExecutionResult> MethodResults { get; set; }
        public Dictionary<string, object> OutputValues { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Method library statistics;
    /// </summary>
    public class MethodLibraryStatistics;
    {
        public int TotalMethods { get; set; }
        public int TotalProcedures { get; set; }
        public int TotalWorkflowPatterns { get; set; }
        public Dictionary<MethodCategory, int> MethodsByCategory { get; set; }
        public double AverageSuccessRate { get; set; }
        public List<MethodExecutionStats> MostExecutedMethods { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Method execution statistics;
    /// </summary>
    public class MethodExecutionStats;
    {
        public string MethodId { get; set; }
        public string MethodName { get; set; }
        public int ExecutionCount { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public double SuccessRate { get; set; }
    }

    /// <summary>
    /// Method recommendation;
    /// </summary>
    public class MethodRecommendation;
    {
        public MethodDefinition Method { get; set; }
        public double RelevanceScore { get; set; }
        public double ConfidenceScore { get; set; }
        public double CompositeScore { get; set; }
    }

    /// <summary>
    /// Problem context for recommendations;
    /// </summary>
    public class ProblemContext;
    {
        public string ProblemDescription { get; set; }
        public List<string> ProblemTags { get; set; } = new List<string>();
        public List<MethodCategory> PreferredCategories { get; set; } = new List<MethodCategory>();
        public ComplexityLevel MaxAllowedComplexity { get; set; } = ComplexityLevel.VeryHigh;
        public TimeSpan? TimeConstraint { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Problem constraints;
    /// </summary>
    public class ProblemConstraints;
    {
        public List<MethodCategory> PreferredCategories { get; set; } = new List<MethodCategory>();
        public ComplexityLevel MaxComplexity { get; set; } = ComplexityLevel.High;
        public TimeSpan? TimeLimit { get; set; }
        public List<string> RequiredTags { get; set; } = new List<string>();
        public double MinimumSuccessRate { get; set; } = 0.5;
    }

    /// <summary>
    /// Method search options;
    /// </summary>
    public class MethodSearchOptions;
    {
        public MethodCategory? Category { get; set; }
        public ComplexityLevel? MaxComplexity { get; set; }
        public double MinimumSuccessRate { get; set; } = 0.0;
        public bool ExcludeInactive { get; set; } = true;
        public MethodSortBy SortBy { get; set; } = MethodSortBy.Relevance;
        public int MaxResults { get; set; } = 50;
        public double MinimumScore { get; set; } = 1.0;
    }

    /// <summary>
    /// Recommendation options;
    /// </summary>
    public class RecommendationOptions;
    {
        public int MaxRecommendations { get; set; } = 5;
        public double MinimumRelevance { get; set; } = 0.3;
        public bool ConsiderExecutionHistory { get; set; } = true;
        public bool ConsiderComplexity { get; set; } = true;
    }

    /// <summary>
    /// Import options;
    /// </summary>
    public class ImportOptions;
    {
        public bool ClearExisting { get; set; } = false;
        public bool OverwriteExisting { get; set; } = true;
        public bool ValidateBeforeImport { get; set; } = true;
    }

    /// <summary>
    /// Method library export data;
    /// </summary>
    public class MethodLibraryExport;
    {
        public List<MethodDefinition> Methods { get; set; }
        public List<ProcedureTemplate> Procedures { get; set; }
        public List<WorkflowPattern> WorkflowPatterns { get; set; }
        public DateTime ExportDate { get; set; }
        public string Version { get; set; }
    }

    /// <summary>
    /// Method categories;
    /// </summary>
    public enum MethodCategory;
    {
        ProblemSolving,
        DecisionMaking,
        CreativeThinking,
        Optimization,
        RiskManagement,
        Learning,
        Communication,
        SystemAdministration,
        DataAnalysis,
        Other;
    }

    /// <summary>
    /// Complexity levels;
    /// </summary>
    public enum ComplexityLevel;
    {
        VeryLow = 1,
        Low = 2,
        Medium = 3,
        High = 4,
        VeryHigh = 5;
    }

    /// <summary>
    /// Procedure execution order;
    /// </summary>
    public enum ProcedureExecutionOrder;
    {
        Sequential,
        Parallel,
        Conditional;
    }

    /// <summary>
    /// Sort options;
    /// </summary>
    public enum MethodSortBy;
    {
        Relevance,
        Name,
        SuccessRate,
        Complexity,
        ExecutionCount,
        LastModified;
    }

    /// <summary>
    /// Phase transition;
    /// </summary>
    public class PhaseTransition;
    {
        public string FromPhaseId { get; set; }
        public string ToPhaseId { get; set; }
        public string Condition { get; set; }
        public Dictionary<string, object> TransitionParameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Validation rule;
    /// </summary>
    public class ValidationRule;
    {
        public string RuleType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string ErrorMessage { get; set; }
    }

    #endregion;

    #region Custom Exceptions;

    public class LibraryInitializationException : Exception
    {
        public LibraryInitializationException(string message) : base(message) { }
        public LibraryInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class MethodNotFoundException : Exception
    {
        public MethodNotFoundException(string message) : base(message) { }
    }

    public class MethodAlreadyExistsException : Exception
    {
        public MethodAlreadyExistsException(string message) : base(message) { }
    }

    public class InvalidMethodException : Exception
    {
        public InvalidMethodException(string message) : base(message) { }
    }

    public class ConcurrentOperationException : Exception
    {
        public ConcurrentOperationException(string message) : base(message) { }
    }

    public class ParameterValidationException : Exception
    {
        public ParameterValidationException(string message) : base(message) { }
    }

    public class MethodExecutionException : Exception
    {
        public MethodExecutionException(string message) : base(message) { }
        public MethodExecutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProcedureNotFoundException : Exception
    {
        public ProcedureNotFoundException(string message) : base(message) { }
    }

    public class ProcedureExecutionException : Exception
    {
        public ProcedureExecutionException(string message) : base(message) { }
        public ProcedureExecutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class LibraryExportException : Exception
    {
        public LibraryExportException(string message) : base(message) { }
        public LibraryExportException(string message, Exception inner) : base(message, inner) { }
    }

    public class LibraryImportException : Exception
    {
        public LibraryImportException(string message) : base(message) { }
        public LibraryImportException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
