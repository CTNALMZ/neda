using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.ExceptionHandling;
using NEDA.Brain.NeuralNetwork.DeepLearning;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.KnowledgeBase;
using NEDA.Automation.WorkflowEngine;

namespace NEDA.NeuralNetwork.CognitiveModels.ProblemSolving;
{
    /// <summary>
    /// Gelişmiş problem çözme ve çözüm bulma motoru;
    /// Çoklu stratejili, öğrenen, uyarlanabilir çözüm bulucu;
    /// </summary>
    public class SolutionFinder : ISolutionFinder, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IMemorySystem _memorySystem;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly IWorkflowEngine _workflowEngine;
        private readonly ProblemAnalyzer _problemAnalyzer;
        private readonly SolutionEvaluator _solutionEvaluator;
        private readonly StrategySelector _strategySelector;

        private readonly ConcurrentDictionary<string, SolutionSession> _activeSessions;
        private readonly SolutionCache _solutionCache;
        private readonly LearningEngine _learningEngine;
        private readonly PerformanceMonitor _performanceMonitor;

        private bool _disposed = false;

        /// <summary>
        /// SolutionFinder constructor - Dependency Injection ile bağımlılıklar;
        /// </summary>
        public SolutionFinder(
            ILogger logger,
            INeuralNetwork neuralNetwork,
            IMemorySystem memorySystem,
            IKnowledgeBase knowledgeBase,
            IWorkflowEngine workflowEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));
            _memorySystem = memorySystem ?? throw new ArgumentNullException(nameof(memorySystem));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));

            _problemAnalyzer = new ProblemAnalyzer(logger, knowledgeBase);
            _solutionEvaluator = new SolutionEvaluator(logger);
            _strategySelector = new StrategySelector(logger);

            _activeSessions = new ConcurrentDictionary<string, SolutionSession>();
            _solutionCache = new SolutionCache();
            _learningEngine = new LearningEngine(logger, neuralNetwork, memorySystem);
            _performanceMonitor = new PerformanceMonitor();

            InitializeSolutionFinder();
        }

        /// <summary>
        /// Problem için çözüm bul;
        /// </summary>
        public async Task<SolutionResult> FindSolutionAsync(ProblemDefinition problem, SolutionOptions options = null)
        {
            _performanceMonitor.StartOperation("FindSolution");

            try
            {
                _logger.LogInformation($"Finding solution for problem: {problem.Id} - {problem.Title}");

                // Cache kontrolü;
                var cacheKey = GenerateCacheKey(problem, options);
                if (_solutionCache.TryGet(cacheKey, out var cachedSolution))
                {
                    _logger.LogDebug($"Cache hit for problem: {problem.Id}");
                    return cachedSolution;
                }

                // Problem analizi;
                var analysis = await AnalyzeProblemAsync(problem);

                // Çözüm stratejisi seç;
                var strategy = SelectSolutionStrategy(analysis, options);

                // Çözüm bulma süreci başlat;
                var session = CreateSolutionSession(problem, analysis, strategy, options);
                _activeSessions[session.Id] = session;

                // Çözüm bul;
                var solution = await ExecuteSolutionStrategyAsync(session, strategy);

                // Çözümü değerlendir;
                var evaluation = await EvaluateSolutionAsync(solution, analysis);

                // Sonuç oluştur;
                var result = new SolutionResult;
                {
                    ProblemId = problem.Id,
                    Solution = solution,
                    Evaluation = evaluation,
                    StrategyUsed = strategy.Name,
                    ProcessingTime = _performanceMonitor.GetOperationTime("FindSolution"),
                    ConfidenceScore = evaluation.OverallScore,
                    Alternatives = await FindAlternativeSolutionsAsync(solution, analysis, options),
                    SessionId = session.Id;
                };

                // Öğrenme ve cache;
                await LearnFromSolutionAsync(problem, solution, evaluation);
                _solutionCache.Add(cacheKey, result);

                // Session'ı temizle;
                _activeSessions.TryRemove(session.Id, out _);

                _performanceMonitor.EndOperation("FindSolution");
                return result;
            }
            catch (SolutionFinderException ex)
            {
                _logger.LogError(ex, $"Solution finding failed for problem: {problem.Id}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, $"Critical error in solution finder");
                throw new SolutionFinderException(
                    ErrorCodes.SolutionFinder.CriticalError,
                    $"Critical error while finding solution for problem: {problem.Id}",
                    ex);
            }
        }

        /// <summary>
        /// Birden fazla çözüm bul;
        /// </summary>
        public async Task<MultiSolutionResult> FindMultipleSolutionsAsync(
            ProblemDefinition problem,
            int solutionCount,
            SolutionOptions options = null)
        {
            _performanceMonitor.StartOperation("FindMultipleSolutions");

            try
            {
                _logger.LogInformation($"Finding {solutionCount} solutions for problem: {problem.Id}");

                var analysis = await AnalyzeProblemAsync(problem);
                var strategy = SelectMultiSolutionStrategy(analysis, solutionCount, options);

                var solutions = new List<Solution>();
                var tasks = new List<Task<Solution>>();

                // Paralel çözüm bulma;
                for (int i = 0; i < solutionCount; i++)
                {
                    var task = FindSolutionWithVariationAsync(problem, analysis, strategy, i, options);
                    tasks.Add(task);
                }

                // Tüm çözümleri bekle;
                var foundSolutions = await Task.WhenAll(tasks);
                solutions.AddRange(foundSolutions);

                // Çözümleri değerlendir ve sırala;
                var evaluatedSolutions = new List<EvaluatedSolution>();
                foreach (var solution in solutions)
                {
                    var evaluation = await EvaluateSolutionAsync(solution, analysis);
                    evaluatedSolutions.Add(new EvaluatedSolution;
                    {
                        Solution = solution,
                        Evaluation = evaluation,
                        RankingScore = evaluation.OverallScore;
                    });
                }

                // Skora göre sırala;
                var rankedSolutions = evaluatedSolutions;
                    .OrderByDescending(s => s.RankingScore)
                    .ToList();

                var result = new MultiSolutionResult;
                {
                    ProblemId = problem.Id,
                    Solutions = rankedSolutions,
                    BestSolution = rankedSolutions.FirstOrDefault(),
                    DiversityScore = CalculateSolutionDiversity(rankedSolutions),
                    ProcessingTime = _performanceMonitor.GetOperationTime("FindMultipleSolutions")
                };

                _performanceMonitor.EndOperation("FindMultipleSolutions");
                return result;
            }
            finally
            {
                _performanceMonitor.LogOperationMetrics("FindMultipleSolutions");
            }
        }

        /// <summary>
        /// Optimize edilmiş çözüm bul;
        /// </summary>
        public async Task<OptimizedSolutionResult> FindOptimizedSolutionAsync(
            ProblemDefinition problem,
            OptimizationCriteria criteria,
            SolutionOptions options = null)
        {
            _performanceMonitor.StartOperation("FindOptimizedSolution");

            try
            {
                _logger.LogInformation($"Finding optimized solution for problem: {problem.Id}");

                // İlk çözümü bul;
                var initialResult = await FindSolutionAsync(problem, options);

                // Optimizasyon süreci;
                var optimizedSolution = await OptimizeSolutionAsync(
                    initialResult.Solution,
                    criteria,
                    initialResult.Evaluation);

                // Optimize edilmiş çözümü değerlendir;
                var optimizedEvaluation = await EvaluateSolutionAsync(optimizedSolution, initialResult.Evaluation.ProblemAnalysis);

                // İyileştirme metriklerini hesapla;
                var improvementMetrics = CalculateImprovementMetrics(
                    initialResult.Evaluation,
                    optimizedEvaluation);

                var result = new OptimizedSolutionResult;
                {
                    ProblemId = problem.Id,
                    InitialSolution = initialResult.Solution,
                    OptimizedSolution = optimizedSolution,
                    InitialEvaluation = initialResult.Evaluation,
                    OptimizedEvaluation = optimizedEvaluation,
                    ImprovementMetrics = improvementMetrics,
                    OptimizationCriteria = criteria,
                    ProcessingTime = _performanceMonitor.GetOperationTime("FindOptimizedSolution"),
                    OptimizationSteps = improvementMetrics.OptimizationSteps;
                };

                // Optimizasyonu öğren;
                await LearnFromOptimizationAsync(problem, initialResult.Solution, optimizedSolution, improvementMetrics);

                return result;
            }
            finally
            {
                _performanceMonitor.LogOperationMetrics("FindOptimizedSolution");
            }
        }

        /// <summary>
        /// Yaratıcı/İnovatif çözüm bul;
        /// </summary>
        public async Task<CreativeSolutionResult> FindCreativeSolutionAsync(
            ProblemDefinition problem,
            CreativityParameters parameters,
            SolutionOptions options = null)
        {
            _performanceMonitor.StartOperation("FindCreativeSolution");

            try
            {
                _logger.LogInformation($"Finding creative solution for problem: {problem.Id}");

                var analysis = await AnalyzeProblemAsync(problem);

                // Yaratıcılık stratejileri;
                var creativeStrategies = new ICreativityStrategy[]
                {
                    new LateralThinkingStrategy(_neuralNetwork),
                    new BrainstormingStrategy(_knowledgeBase),
                    new AnalogyStrategy(_memorySystem),
                    new RandomCombinationStrategy()
                };

                var creativeSolutions = new List<Solution>();
                var tasks = new List<Task<Solution>>();

                // Paralel yaratıcı çözüm bulma;
                foreach (var strategy in creativeStrategies)
                {
                    var task = Task.Run(() => strategy.GenerateCreativeSolution(problem, analysis, parameters));
                    tasks.Add(task);
                }

                var generatedSolutions = await Task.WhenAll(tasks);
                creativeSolutions.AddRange(generatedSolutions.Where(s => s != null));

                // Çözümleri filtrele ve sırala;
                var filteredSolutions = FilterCreativeSolutions(creativeSolutions, parameters);
                var evaluatedSolutions = await EvaluateCreativeSolutionsAsync(filteredSolutions, analysis, parameters);

                // En iyi yaratıcı çözümü seç;
                var bestCreativeSolution = SelectBestCreativeSolution(evaluatedSolutions, parameters);

                var result = new CreativeSolutionResult;
                {
                    ProblemId = problem.Id,
                    CreativeSolution = bestCreativeSolution.Solution,
                    AllCreativeSolutions = evaluatedSolutions,
                    CreativityScore = bestCreativeSolution.CreativityScore,
                    NoveltyScore = bestCreativeSolution.NoveltyScore,
                    FeasibilityScore = bestCreativeSolution.FeasibilityScore,
                    ParametersUsed = parameters,
                    ProcessingTime = _performanceMonitor.GetOperationTime("FindCreativeSolution")
                };

                // Yaratıcı çözümü öğren;
                await LearnFromCreativeSolutionAsync(problem, bestCreativeSolution);

                return result;
            }
            finally
            {
                _performanceMonitor.LogOperationMetrics("FindCreativeSolution");
            }
        }

        /// <summary>
        /// Problem analizi yap;
        /// </summary>
        private async Task<ProblemAnalysis> AnalyzeProblemAsync(ProblemDefinition problem)
        {
            return await _problemAnalyzer.AnalyzeAsync(problem);
        }

        /// <summary>
        /// Çözüm stratejisi seç;
        /// </summary>
        private SolutionStrategy SelectSolutionStrategy(ProblemAnalysis analysis, SolutionOptions options)
        {
            return _strategySelector.SelectStrategy(analysis, options);
        }

        /// <summary>
        /// Çözüm stratejisini çalıştır;
        /// </summary>
        private async Task<Solution> ExecuteSolutionStrategyAsync(
            SolutionSession session,
            SolutionStrategy strategy)
        {
            try
            {
                session.Status = SolutionSessionStatus.Running;
                session.StartTime = DateTime.UtcNow;

                _logger.LogDebug($"Executing strategy: {strategy.Name} for session: {session.Id}");

                // Workflow oluştur ve çalıştır;
                var workflow = CreateSolutionWorkflow(strategy, session);
                var executionResult = await _workflowEngine.ExecuteWorkflowAsync(workflow);

                if (executionResult.Status == WorkflowExecutionStatus.Completed)
                {
                    var solution = ExtractSolutionFromWorkflowResult(executionResult);
                    session.EndTime = DateTime.UtcNow;
                    session.Status = SolutionSessionStatus.Completed;
                    session.SolutionFound = solution;

                    return solution;
                }
                else;
                {
                    throw new SolutionFinderException(
                        ErrorCodes.SolutionFinder.StrategyExecutionFailed,
                        $"Strategy execution failed: {executionResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                session.Status = SolutionSessionStatus.Failed;
                session.Error = ex;
                throw;
            }
        }

        /// <summary>
        /// Çözümü değerlendir;
        /// </summary>
        private async Task<SolutionEvaluation> EvaluateSolutionAsync(
            Solution solution,
            ProblemAnalysis analysis)
        {
            return await _solutionEvaluator.EvaluateAsync(solution, analysis);
        }

        /// <summary>
        /// Alternatif çözümler bul;
        /// </summary>
        private async Task<List<AlternativeSolution>> FindAlternativeSolutionsAsync(
            Solution primarySolution,
            ProblemAnalysis analysis,
            SolutionOptions options)
        {
            var alternatives = new List<AlternativeSolution>();

            // 1. Varyasyonlar oluştur;
            var variations = GenerateSolutionVariations(primarySolution, 3);

            // 2. Her varyasyonu değerlendir;
            foreach (var variation in variations)
            {
                var evaluation = await EvaluateSolutionAsync(variation, analysis);

                if (evaluation.OverallScore >= options?.MinimumAlternativeScore ?? 0.6)
                {
                    alternatives.Add(new AlternativeSolution;
                    {
                        Solution = variation,
                        Evaluation = evaluation,
                        SimilarityToPrimary = CalculateSolutionSimilarity(primarySolution, variation),
                        Advantages = FindAdvantagesOverPrimary(primarySolution, variation, evaluation)
                    });
                }
            }

            // 3. En iyi alternatifleri seç;
            return alternatives;
                .OrderByDescending(a => a.Evaluation.OverallScore)
                .Take(options?.MaxAlternatives ?? 5)
                .ToList();
        }

        /// <summary>
        /// Çözümden öğren;
        /// </summary>
        private async Task LearnFromSolutionAsync(
            ProblemDefinition problem,
            Solution solution,
            SolutionEvaluation evaluation)
        {
            await _learningEngine.LearnFromSolutionAsync(problem, solution, evaluation);
        }

        /// <summary>
        /// SolutionSession oluştur;
        /// </summary>
        private SolutionSession CreateSolutionSession(
            ProblemDefinition problem,
            ProblemAnalysis analysis,
            SolutionStrategy strategy,
            SolutionOptions options)
        {
            return new SolutionSession;
            {
                Id = Guid.NewGuid().ToString(),
                Problem = problem,
                Analysis = analysis,
                Strategy = strategy,
                Options = options,
                CreatedAt = DateTime.UtcNow,
                Status = SolutionSessionStatus.Created;
            };
        }

        /// <summary>
        /// Cache key oluştur;
        /// </summary>
        private string GenerateCacheKey(ProblemDefinition problem, SolutionOptions options)
        {
            return $"{problem.Id}_{problem.Complexity}_{options?.GetHashCode() ?? 0}";
        }

        /// <summary>
        /// SolutionFinder'ı başlat;
        /// </summary>
        private void InitializeSolutionFinder()
        {
            try
            {
                _logger.LogInformation("Initializing SolutionFinder...");

                // Öğrenilmiş modelleri yükle;
                _learningEngine.LoadLearnedModels();

                // Stratejileri yapılandır;
                _strategySelector.InitializeStrategies();

                // Cache'i temizle;
                _solutionCache.ClearExpired();

                _logger.LogInformation("SolutionFinder initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SolutionFinder");
                throw;
            }
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
            if (!_disposed)
            {
                if (disposing)
                {
                    // Yönetilen kaynakları serbest bırak;
                    _solutionCache.Dispose();
                    _learningEngine.Dispose();

                    // Tüm aktif session'ları temizle;
                    foreach (var session in _activeSessions.Values)
                    {
                        if (session.Status == SolutionSessionStatus.Running)
                        {
                            session.Status = SolutionSessionStatus.Cancelled;
                        }
                    }
                    _activeSessions.Clear();
                }

                _disposed = true;
            }
        }

        ~SolutionFinder()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// SolutionFinder arayüzü;
    /// </summary>
    public interface ISolutionFinder : IDisposable
    {
        Task<SolutionResult> FindSolutionAsync(ProblemDefinition problem, SolutionOptions options = null);
        Task<MultiSolutionResult> FindMultipleSolutionsAsync(ProblemDefinition problem, int solutionCount, SolutionOptions options = null);
        Task<OptimizedSolutionResult> FindOptimizedSolutionAsync(ProblemDefinition problem, OptimizationCriteria criteria, SolutionOptions options = null);
        Task<CreativeSolutionResult> FindCreativeSolutionAsync(ProblemDefinition problem, CreativityParameters parameters, SolutionOptions options = null);
    }

    /// <summary>
    /// Problem tanımı;
    /// </summary>
    public class ProblemDefinition;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<Constraint> Constraints { get; set; }
        public List<Objective> Objectives { get; set; }
        public ProblemComplexity Complexity { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public ProblemDefinition()
        {
            Parameters = new Dictionary<string, object>();
            Constraints = new List<Constraint>();
            Objectives = new List<Objective>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Çözüm;
    /// </summary>
    public class Solution;
    {
        public string Id { get; set; }
        public string ProblemId { get; set; }
        public string Description { get; set; }
        public SolutionType Type { get; set; }
        public List<SolutionStep> Steps { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public Dictionary<string, object> Results { get; set; }
        public List<ResourceRequirement> ResourceRequirements { get; set; }
        public DateTime CreatedAt { get; set; }
        public TimeSpan EstimatedExecutionTime { get; set; }

        public Solution()
        {
            Steps = new List<SolutionStep>();
            Parameters = new Dictionary<string, object>();
            Results = new Dictionary<string, object>();
            ResourceRequirements = new List<ResourceRequirement>();
            CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Çözüm sonucu;
    /// </summary>
    public class SolutionResult;
    {
        public string ProblemId { get; set; }
        public Solution Solution { get; set; }
        public SolutionEvaluation Evaluation { get; set; }
        public string StrategyUsed { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public double ConfidenceScore { get; set; }
        public List<AlternativeSolution> Alternatives { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public SolutionResult()
        {
            Alternatives = new List<AlternativeSolution>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Çözüm değerlendirmesi;
    /// </summary>
    public class SolutionEvaluation;
    {
        public string SolutionId { get; set; }
        public ProblemAnalysis ProblemAnalysis { get; set; }
        public double FeasibilityScore { get; set; }
        public double EfficiencyScore { get; set; }
        public double EffectivenessScore { get; set; }
        public double CostScore { get; set; }
        public double RiskScore { get; set; }
        public double InnovationScore { get; set; }
        public double OverallScore { get; set; }
        public List<EvaluationMetric> Metrics { get; set; }
        public List<string> Strengths { get; set; }
        public List<string> Weaknesses { get; set; }
        public List<Recommendation> Recommendations { get; set; }

        public SolutionEvaluation()
        {
            Metrics = new List<EvaluationMetric>();
            Strengths = new List<string>();
            Weaknesses = new List<string>();
            Recommendations = new List<Recommendation>();
        }
    }

    /// <summary>
    /// Çözüm stratejisi;
    /// </summary>
    public class SolutionStrategy;
    {
        public string Name { get; set; }
        public StrategyType Type { get; set; }
        public List<string> ApplicableProblemTypes { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public double SuccessRate { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public List<string> RequiredCapabilities { get; set; }

        public SolutionStrategy()
        {
            ApplicableProblemTypes = new List<string>();
            Parameters = new Dictionary<string, object>();
            RequiredCapabilities = new List<string>();
        }
    }

    /// <summary>
    /// Problem analizi;
    /// </summary>
    public class ProblemAnalysis;
    {
        public string ProblemId { get; set; }
        public ProblemComplexity Complexity { get; set; }
        public List<string> ProblemTypes { get; set; }
        public Dictionary<string, double> FeatureWeights { get; set; }
        public List<ConstraintAnalysis> ConstraintAnalyses { get; set; }
        public List<ObjectiveAnalysis> ObjectiveAnalyses { get; set; }
        public List<SimilarProblem> SimilarProblems { get; set; }
        public List<RecommendedApproach> RecommendedApproaches { get; set; }
        public Dictionary<string, object> AnalysisData { get; set; }

        public ProblemAnalysis()
        {
            ProblemTypes = new List<string>();
            FeatureWeights = new Dictionary<string, double>();
            ConstraintAnalyses = new List<ConstraintAnalysis>();
            ObjectiveAnalyses = new List<ObjectiveAnalysis>();
            SimilarProblems = new List<SimilarProblem>();
            RecommendedApproaches = new List<RecommendedApproach>();
            AnalysisData = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// SolutionSession;
    /// </summary>
    public class SolutionSession;
    {
        public string Id { get; set; }
        public ProblemDefinition Problem { get; set; }
        public ProblemAnalysis Analysis { get; set; }
        public SolutionStrategy Strategy { get; set; }
        public SolutionOptions Options { get; set; }
        public Solution SolutionFound { get; set; }
        public SolutionSessionStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public Exception Error { get; set; }
        public Dictionary<string, object> SessionData { get; set; }

        public SolutionSession()
        {
            SessionData = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Enum'lar;
    /// </summary>
    public enum ProblemComplexity;
    {
        Simple,
        Moderate,
        Complex,
        VeryComplex,
        ExtremelyComplex;
    }

    public enum SolutionType;
    {
        Algorithmic,
        Heuristic,
        Analytical,
        Simulation,
        Optimization,
        Creative,
        Hybrid;
    }

    public enum StrategyType;
    {
        BruteForce,
        Heuristic,
        GeneticAlgorithm,
        NeuralNetwork,
        ExpertSystem,
        CaseBased,
        ConstraintSatisfaction,
        MonteCarlo,
        Hybrid;
    }

    public enum SolutionSessionStatus;
    {
        Created,
        Running,
        Completed,
        Failed,
        Cancelled,
        Timeout;
    }

    /// <summary>
    /// Özel exception sınıfları;
    /// </summary>
    public class SolutionFinderException : Exception
    {
        public string ErrorCode { get; }

        public SolutionFinderException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public SolutionFinderException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}

// Not: Yardımcı sınıflar (ProblemAnalyzer, SolutionEvaluator, StrategySelector, LearningEngine, SolutionCache)
// ayrı dosyalarda implemente edilmelidir. Bu dosya ana SolutionFinder sınıfını içerir.
