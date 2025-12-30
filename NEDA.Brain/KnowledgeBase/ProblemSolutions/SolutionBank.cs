using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.NeuralNetwork.CognitiveModels;

namespace NEDA.Brain.KnowledgeBase.ProblemSolutions;
{
    /// <summary>
    /// Comprehensive repository of problem solutions with intelligent retrieval,
    /// adaptation, and learning capabilities for NEDA's problem-solving systems.
    /// </summary>
    public class SolutionBank : ISolutionBank, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly ISolutionAnalyzer _solutionAnalyzer;
        private readonly SolutionBankConfiguration _configuration;
        private bool _disposed = false;
        private readonly object _syncLock = new object();

        // Core data structures;
        private readonly Dictionary<string, Solution> _solutions;
        private readonly Dictionary<string, ProblemTemplate> _problemTemplates;
        private readonly Dictionary<string, SolutionPattern> _solutionPatterns;

        // Indexing and search;
        private readonly SolutionIndex _solutionIndex;
        private readonly PatternIndex _patternIndex;
        private readonly SolutionCache _solutionCache;

        // Learning and adaptation;
        private readonly SolutionLearner _solutionLearner;
        private readonly SolutionAdapter _solutionAdapter;
        private readonly PatternExtractor _patternExtractor;

        // Analytics and metrics;
        private readonly SolutionMetricsCollector _metricsCollector;
        private readonly List<SolutionUsage> _usageHistory;
        private readonly SolutionBankStatistics _statistics;

        /// <summary>
        /// Initializes a new instance of SolutionBank;
        /// </summary>
        public SolutionBank(
            ILogger logger,
            IKnowledgeGraph knowledgeGraph,
            ISolutionAnalyzer solutionAnalyzer,
            SolutionBankConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _solutionAnalyzer = solutionAnalyzer ?? throw new ArgumentNullException(nameof(solutionAnalyzer));
            _configuration = configuration ?? SolutionBankConfiguration.Default;

            // Initialize core data structures;
            _solutions = new Dictionary<string, Solution>();
            _problemTemplates = new Dictionary<string, ProblemTemplate>();
            _solutionPatterns = new Dictionary<string, SolutionPattern>();

            // Initialize indexing and search;
            _solutionIndex = new SolutionIndex();
            _patternIndex = new PatternIndex();
            _solutionCache = new SolutionCache(_configuration.CacheConfiguration);

            // Initialize learning components;
            _solutionLearner = new SolutionLearner();
            _solutionAdapter = new SolutionAdapter();
            _patternExtractor = new PatternExtractor();

            // Initialize analytics;
            _metricsCollector = new SolutionMetricsCollector();
            _usageHistory = new List<SolutionUsage>();
            _statistics = new SolutionBankStatistics();

            // Load default solutions and patterns;
            LoadDefaultSolutions();
            LoadSolutionPatterns();
            BuildIndices();

            _logger.LogInformation($"SolutionBank initialized with {_solutions.Count} solutions and {_solutionPatterns.Count} patterns");
        }

        /// <summary>
        /// Loads default solutions from built-in knowledge base;
        /// </summary>
        private void LoadDefaultSolutions()
        {
            try
            {
                // Load algorithmic solutions;
                LoadAlgorithmSolutions();

                // Load design pattern solutions;
                LoadDesignPatternSolutions();

                // Load troubleshooting solutions;
                LoadTroubleshootingSolutions();

                // Load optimization solutions;
                LoadOptimizationSolutions();

                // Load creative solutions;
                LoadCreativeSolutions();

                _logger.LogDebug($"Loaded {_solutions.Count} default solutions");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load default solutions: {ex.Message}");
                throw new SolutionBankException("Failed to initialize solution bank", ex);
            }
        }

        /// <summary>
        /// Loads algorithmic solutions;
        /// </summary>
        private void LoadAlgorithmSolutions()
        {
            // Sorting algorithms;
            AddSolution(new Solution;
            {
                Id = "sol_sort_quicksort",
                Title = "Quicksort Algorithm",
                Description = "Efficient comparison-based sorting algorithm using divide-and-conquer strategy",
                ProblemType = ProblemType.Algorithmic,
                Domain = "Computer Science",
                Complexity = SolutionComplexity.Medium,
                Implementation = new Implementation;
                {
                    Language = "C#",
                    Code = @"public void QuickSort(int[] arr, int low, int high)
{
    if (low < high)
    {
        int pi = Partition(arr, low, high);
        QuickSort(arr, low, pi - 1);
        QuickSort(arr, pi + 1, high);
    }
}

private int Partition(int[] arr, int low, int high)
{
    int pivot = arr[high];
    int i = low - 1;
    
    for (int j = low; j < high; j++)
    {
        if (arr[j] < pivot)
        {
            i++;
            Swap(arr, i, j);
        }
    }
    
    Swap(arr, i + 1, high);
    return i + 1;
}",
                    TimeComplexity = "O(n log n) average, O(n²) worst",
                    SpaceComplexity = "O(log n)"
                },
                Tags = new List<string> { "sorting", "algorithm", "divide-conquer", "recursive" },
                SuccessRate = 0.95,
                UsageCount = 0,
                CreatedAt = DateTime.UtcNow;
            });

            // Search algorithms;
            AddSolution(new Solution;
            {
                Id = "sol_search_binary",
                Title = "Binary Search",
                Description = "Efficient search algorithm for sorted arrays",
                ProblemType = ProblemType.Algorithmic,
                Domain = "Computer Science",
                Complexity = SolutionComplexity.Low,
                Implementation = new Implementation;
                {
                    Language = "C#",
                    Code = @"public int BinarySearch(int[] arr, int target)
{
    int left = 0;
    int right = arr.Length - 1;
    
    while (left <= right)
    {
        int mid = left + (right - left) / 2;
        
        if (arr[mid] == target)
            return mid;
            
        if (arr[mid] < target)
            left = mid + 1;
        else;
            right = mid - 1;
    }
    
    return -1;
}",
                    TimeComplexity = "O(log n)",
                    SpaceComplexity = "O(1)"
                },
                Tags = new List<string> { "search", "algorithm", "sorted", "efficient" }
            });

            // Graph algorithms;
            AddSolution(new Solution;
            {
                Id = "sol_graph_dijkstra",
                Title = "Dijkstra's Algorithm",
                Description = "Finds shortest paths between nodes in a graph with non-negative edge weights",
                ProblemType = ProblemType.Algorithmic,
                Domain = "Computer Science",
                Complexity = SolutionComplexity.High,
                Tags = new List<string> { "graph", "shortest-path", "algorithm", "weighted" }
            });

            _logger.LogDebug("Algorithmic solutions loaded");
        }

        /// <summary>
        /// Loads design pattern solutions;
        /// </summary>
        private void LoadDesignPatternSolutions()
        {
            AddSolution(new Solution;
            {
                Id = "sol_pattern_singleton",
                Title = "Singleton Pattern Implementation",
                Description = "Ensures a class has only one instance and provides a global point of access",
                ProblemType = ProblemType.Design,
                Domain = "Software Architecture",
                Complexity = SolutionComplexity.Low,
                Implementation = new Implementation;
                {
                    Language = "C#",
                    Code = @"public class Singleton;
{
    private static Singleton _instance;
    private static readonly object _lock = new object();
    
    private Singleton() { }
    
    public static Singleton Instance;
    {
        get;
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new Singleton();
                    }
                }
            }
            return _instance;
        }
    }
}",
                    DesignPattern = "Singleton",
                    UseCase = "When exactly one instance of a class is needed"
                },
                Tags = new List<string> { "design-pattern", "creational", "singleton", "thread-safe" }
            });

            AddSolution(new Solution;
            {
                Id = "sol_pattern_observer",
                Title = "Observer Pattern Implementation",
                Description = "Defines a one-to-many dependency between objects",
                ProblemType = ProblemType.Design,
                Domain = "Software Architecture",
                Complexity = SolutionComplexity.Medium,
                Tags = new List<string> { "design-pattern", "behavioral", "observer", "pub-sub" }
            });

            _logger.LogDebug("Design pattern solutions loaded");
        }

        /// <summary>
        /// Loads troubleshooting solutions;
        /// </summary>
        private void LoadTroubleshootingSolutions()
        {
            AddSolution(new Solution;
            {
                Id = "sol_trouble_null_reference",
                Title = "Null Reference Exception Resolution",
                Description = "Common solutions for handling null reference exceptions in C#",
                ProblemType = ProblemType.Troubleshooting,
                Domain = "Software Development",
                Complexity = SolutionComplexity.Low,
                Steps = new List<SolutionStep>
                {
                    new SolutionStep;
                    {
                        Order = 1,
                        Action = "Check which variable is null",
                        Description = "Examine the exception stack trace to identify the null variable"
                    },
                    new SolutionStep;
                    {
                        Order = 2,
                        Action = "Add null checks",
                        Description = "Use null conditional operators or if statements to handle null values"
                    },
                    new SolutionStep;
                    {
                        Order = 3,
                        Action = "Initialize variables properly",
                        Description = "Ensure variables are initialized before use"
                    }
                },
                Tags = new List<string> { "troubleshooting", "exception", "null", "csharp" }
            });

            AddSolution(new Solution;
            {
                Id = "sol_trouble_performance",
                Title = "Performance Bottleneck Analysis",
                Description = "Systematic approach to identifying and resolving performance issues",
                ProblemType = ProblemType.Troubleshooting,
                Domain = "Software Development",
                Complexity = SolutionComplexity.High,
                Tags = new List<string> { "troubleshooting", "performance", "optimization", "profiling" }
            });

            _logger.LogDebug("Troubleshooting solutions loaded");
        }

        /// <summary>
        /// Loads optimization solutions;
        /// </summary>
        private void LoadOptimizationSolutions()
        {
            AddSolution(new Solution;
            {
                Id = "sol_opt_memory",
                Title = "Memory Optimization Techniques",
                Description = "Strategies for reducing memory usage in applications",
                ProblemType = ProblemType.Optimization,
                Domain = "Software Development",
                Complexity = SolutionComplexity.Medium,
                Tags = new List<string> { "optimization", "memory", "performance", "garbage-collection" }
            });

            AddSolution(new Solution;
            {
                Id = "sol_opt_database",
                Title = "Database Query Optimization",
                Description = "Techniques for improving database query performance",
                ProblemType = ProblemType.Optimization,
                Domain = "Database",
                Complexity = SolutionComplexity.Medium,
                Tags = new List<string> { "optimization", "database", "sql", "performance" }
            });

            _logger.LogDebug("Optimization solutions loaded");
        }

        /// <summary>
        /// Loads creative solutions;
        /// </summary>
        private void LoadCreativeSolutions()
        {
            AddSolution(new Solution;
            {
                Id = "sol_creative_brainstorm",
                Title = "Structured Brainstorming Technique",
                Description = "Systematic approach to generating creative ideas",
                ProblemType = ProblemType.Creative,
                Domain = "Creative Thinking",
                Complexity = SolutionComplexity.Low,
                Tags = new List<string> { "creative", "brainstorming", "ideation", "innovation" }
            });

            AddSolution(new Solution;
            {
                Id = "sol_creative_prototype",
                Title = "Rapid Prototyping Methodology",
                Description = "Quick iteration approach for testing and refining ideas",
                ProblemType = ProblemType.Creative,
                Domain = "Product Development",
                Complexity = SolutionComplexity.Medium,
                Tags = new List<string> { "creative", "prototyping", "iteration", "design-thinking" }
            });

            _logger.LogDebug("Creative solutions loaded");
        }

        /// <summary>
        /// Loads solution patterns for intelligent matching;
        /// </summary>
        private void LoadSolutionPatterns()
        {
            // Algorithmic patterns;
            AddSolutionPattern(new SolutionPattern;
            {
                Id = "pattern_divide_conquer",
                Name = "Divide and Conquer",
                Description = "Break problem into subproblems, solve recursively, combine results",
                Applicability = new List<string> { "sorting", "searching", "computational geometry" },
                Structure = new PatternStructure;
                {
                    Steps = new List<string>
                    {
                        "Divide problem into smaller subproblems",
                        "Conquer subproblems recursively",
                        "Combine subproblem solutions"
                    }
                },
                Examples = new List<string> { "Quicksort", "Merge sort", "Binary search" }
            });

            // Dynamic programming pattern;
            AddSolutionPattern(new SolutionPattern;
            {
                Id = "pattern_dynamic_programming",
                Name = "Dynamic Programming",
                Description = "Solve complex problems by breaking them down into simpler overlapping subproblems",
                Applicability = new List<string> { "optimization", "combinatorial", "sequence alignment" },
                Examples = new List<string> { "Fibonacci sequence", "Knapsack problem", "Shortest path" }
            });

            _logger.LogDebug($"Loaded {_solutionPatterns.Count} solution patterns");
        }

        /// <summary>
        /// Builds search indices for efficient retrieval;
        /// </summary>
        private void BuildIndices()
        {
            foreach (var solution in _solutions.Values)
            {
                _solutionIndex.AddSolution(solution);
            }

            foreach (var pattern in _solutionPatterns.Values)
            {
                _patternIndex.AddPattern(pattern);
            }

            _logger.LogDebug($"Indices built: {_solutionIndex.Count} solutions, {_patternIndex.Count} patterns");
        }

        /// <summary>
        /// Searches for solutions matching the given problem;
        /// </summary>
        public SolutionSearchResult SearchSolutions(ProblemQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Searching solutions for: {query.Description}");

                    // Check cache first;
                    var cacheKey = query.GetCacheKey();
                    if (_solutionCache.TryGetSearchResult(cacheKey, out var cachedResult))
                    {
                        _logger.LogDebug($"Cache hit for query: {query.Description}");
                        _metricsCollector.RecordCacheHit();
                        return cachedResult;
                    }

                    var startTime = DateTime.UtcNow;

                    // Analyze problem to extract features;
                    var problemAnalysis = AnalyzeProblem(query);

                    // Search using multiple strategies;
                    var searchResults = new List<SolutionMatch>();

                    // 1. Direct keyword search;
                    var keywordMatches = SearchByKeywords(query);
                    searchResults.AddRange(keywordMatches);

                    // 2. Semantic similarity search;
                    var semanticMatches = SearchBySemantics(problemAnalysis);
                    searchResults.AddRange(semanticMatches);

                    // 3. Pattern-based search;
                    var patternMatches = SearchByPatterns(problemAnalysis);
                    searchResults.AddRange(patternMatches);

                    // 4. Domain-specific search;
                    var domainMatches = SearchByDomain(problemAnalysis);
                    searchResults.AddRange(domainMatches);

                    // Combine and deduplicate results;
                    var combinedResults = CombineSearchResults(searchResults);

                    // Score and rank results;
                    var scoredResults = ScoreSolutions(combinedResults, problemAnalysis);

                    // Apply filters;
                    var filteredResults = ApplyFilters(scoredResults, query.Filters);

                    // Paginate results;
                    var paginatedResults = PaginateResults(filteredResults, query);

                    var searchResult = new SolutionSearchResult;
                    {
                        Query = query,
                        ProblemAnalysis = problemAnalysis,
                        Matches = paginatedResults,
                        TotalCount = filteredResults.Count,
                        SearchDuration = DateTime.UtcNow - startTime,
                        SearchStrategy = query.SearchStrategy,
                        Suggestions = GenerateSearchSuggestions(query, problemAnalysis),
                        SearchId = Guid.NewGuid().ToString()
                    };

                    // Cache the result;
                    _solutionCache.CacheSearchResult(cacheKey, searchResult);

                    // Update metrics;
                    _metricsCollector.RecordSearch(searchResult);

                    _logger.LogInformation($"Solution search completed: {filteredResults.Count} matches found");

                    return searchResult;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Solution search failed: {ex.Message}");
                    throw new SolutionSearchException($"Search failed for query: {query.Description}", ex);
                }
            }
        }

        /// <summary>
        /// Gets a specific solution by ID;
        /// </summary>
        public Solution GetSolution(string solutionId)
        {
            if (string.IsNullOrWhiteSpace(solutionId))
                throw new ArgumentException("Solution ID cannot be null or empty", nameof(solutionId));

            lock (_syncLock)
            {
                if (_solutions.TryGetValue(solutionId, out var solution))
                {
                    // Record usage;
                    RecordSolutionUsage(solutionId, SolutionUsageType.Retrieval);

                    // Update popularity;
                    solution.UsageCount++;
                    solution.LastUsed = DateTime.UtcNow;

                    _logger.LogDebug($"Retrieved solution: {solutionId}");

                    return solution;
                }

                throw new SolutionNotFoundException($"Solution not found: {solutionId}");
            }
        }

        /// <summary>
        /// Adapts a solution to a specific problem context;
        /// </summary>
        public AdaptedSolution AdaptSolution(string solutionId, AdaptationContext context)
        {
            if (string.IsNullOrWhiteSpace(solutionId))
                throw new ArgumentException("Solution ID cannot be null or empty", nameof(solutionId));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Adapting solution {solutionId} for context: {context.Description}");

                    // Get the base solution;
                    var baseSolution = GetSolution(solutionId);

                    // Analyze adaptation requirements;
                    var adaptationAnalysis = AnalyzeAdaptationRequirements(baseSolution, context);

                    // Apply adaptation;
                    var adaptedSolution = _solutionAdapter.Adapt(baseSolution, adaptationAnalysis);

                    // Validate adaptation;
                    var validationResult = ValidateAdaptation(adaptedSolution, context);

                    // Generate adaptation report;
                    var adaptationReport = GenerateAdaptationReport(baseSolution, adaptedSolution, adaptationAnalysis, validationResult);

                    var result = new AdaptedSolution;
                    {
                        BaseSolution = baseSolution,
                        AdaptedSolution = adaptedSolution,
                        AdaptationContext = context,
                        AdaptationAnalysis = adaptationAnalysis,
                        ValidationResult = validationResult,
                        AdaptationReport = adaptationReport,
                        AdaptationId = Guid.NewGuid().ToString(),
                        Timestamp = DateTime.UtcNow;
                    };

                    // Record adaptation usage;
                    RecordSolutionUsage(solutionId, SolutionUsageType.Adaptation);

                    // Learn from adaptation;
                    LearnFromAdaptation(baseSolution, adaptedSolution, context);

                    _logger.LogInformation($"Solution adaptation completed: {solutionId}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Solution adaptation failed: {ex.Message}");
                    throw new SolutionAdaptationException($"Failed to adapt solution: {solutionId}", ex);
                }
            }
        }

        /// <summary>
        /// Generates a new solution using AI and existing patterns;
        /// </summary>
        public GeneratedSolution GenerateSolution(ProblemSpecification specification, GenerationOptions options = null)
        {
            if (specification == null)
                throw new ArgumentNullException(nameof(specification));

            options ??= new GenerationOptions();

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Generating solution for: {specification.Title}");

                    var generationId = Guid.NewGuid().ToString();
                    var startTime = DateTime.UtcNow;

                    // Analyze problem specification;
                    var problemAnalysis = AnalyzeProblemSpecification(specification);

                    // Find similar existing solutions;
                    var similarSolutions = FindSimilarSolutions(problemAnalysis, options.MaxSimilarSolutions);

                    // Extract relevant patterns;
                    var relevantPatterns = ExtractRelevantPatterns(problemAnalysis);

                    // Generate solution using AI;
                    var generatedSolution = GenerateSolutionUsingAI(problemAnalysis, similarSolutions, relevantPatterns, options);

                    // Validate generated solution;
                    var validationResult = ValidateGeneratedSolution(generatedSolution, specification);

                    // Optimize solution;
                    var optimizedSolution = OptimizeSolution(generatedSolution, options);

                    // Create generation report;
                    var generationReport = CreateGenerationReport(
                        specification,
                        generatedSolution,
                        similarSolutions,
                        relevantPatterns,
                        validationResult);

                    var result = new GeneratedSolution;
                    {
                        Specification = specification,
                        GeneratedSolution = optimizedSolution,
                        SimilarSolutions = similarSolutions,
                        UsedPatterns = relevantPatterns,
                        ValidationResult = validationResult,
                        GenerationReport = generationReport,
                        GenerationId = generationId,
                        GenerationTime = DateTime.UtcNow - startTime,
                        Timestamp = DateTime.UtcNow;
                    };

                    // Store generated solution if valid;
                    if (validationResult.IsValid && options.StoreGeneratedSolution)
                    {
                        StoreGeneratedSolution(optimizedSolution, specification);
                    }

                    // Update generation metrics;
                    _metricsCollector.RecordGeneration(result);

                    _logger.LogInformation($"Solution generation completed: {specification.Title}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Solution generation failed: {ex.Message}");
                    throw new SolutionGenerationException($"Failed to generate solution: {specification.Title}", ex);
                }
            }
        }

        /// <summary>
        /// Evaluates the effectiveness of a solution;
        /// </summary>
        public SolutionEvaluation EvaluateSolution(string solutionId, EvaluationContext context)
        {
            if (string.IsNullOrWhiteSpace(solutionId))
                throw new ArgumentException("Solution ID cannot be null or empty", nameof(solutionId));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogInformation($"Evaluating solution: {solutionId}");

                    var solution = GetSolution(solutionId);

                    // Perform comprehensive evaluation;
                    var evaluation = _solutionAnalyzer.Evaluate(solution, context);

                    // Update solution metrics;
                    solution.Evaluations.Add(evaluation);
                    solution.SuccessRate = CalculateUpdatedSuccessRate(solution);

                    // Generate evaluation report;
                    var evaluationReport = GenerateEvaluationReport(solution, evaluation, context);

                    var result = new SolutionEvaluation;
                    {
                        Solution = solution,
                        Evaluation = evaluation,
                        Context = context,
                        EvaluationReport = evaluationReport,
                        EvaluationId = Guid.NewGuid().ToString(),
                        Timestamp = DateTime.UtcNow;
                    };

                    // Record evaluation usage;
                    RecordSolutionUsage(solutionId, SolutionUsageType.Evaluation);

                    _logger.LogInformation($"Solution evaluation completed: {solutionId} => Score: {evaluation.OverallScore:F2}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Solution evaluation failed: {ex.Message}");
                    throw new SolutionEvaluationException($"Failed to evaluate solution: {solutionId}", ex);
                }
            }
        }

        /// <summary>
        /// Learns from successful solution applications;
        /// </summary>
        public void LearnFromApplication(string solutionId, ApplicationResult result)
        {
            if (string.IsNullOrWhiteSpace(solutionId))
                throw new ArgumentException("Solution ID cannot be null or empty", nameof(solutionId));

            if (result == null)
                throw new ArgumentNullException(nameof(result));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogDebug($"Learning from application of solution: {solutionId}");

                    var solution = GetSolution(solutionId);

                    // Update solution success metrics;
                    solution.ApplicationResults.Add(result);
                    solution.SuccessRate = CalculateUpdatedSuccessRate(solution);

                    // Extract patterns from successful application;
                    if (result.SuccessLevel >= SuccessLevel.High)
                    {
                        ExtractPatternFromApplication(solution, result);
                    }

                    // Update solution effectiveness;
                    UpdateSolutionEffectiveness(solution, result);

                    // Store application feedback;
                    StoreApplicationFeedback(solutionId, result);

                    _logger.LogInformation($"Learned from solution application: {solutionId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Learning from application failed: {ex.Message}");
                    // Don't throw - learning failures shouldn't break the system;
                }
            }
        }

        /// <summary>
        /// Adds a new solution to the bank;
        /// </summary>
        public void AddSolution(Solution solution)
        {
            if (solution == null)
                throw new ArgumentNullException(nameof(solution));

            lock (_syncLock)
            {
                try
                {
                    ValidateSolution(solution);

                    // Generate ID if not provided;
                    if (string.IsNullOrWhiteSpace(solution.Id))
                    {
                        solution.Id = GenerateSolutionId(solution);
                    }

                    // Check for duplicates;
                    if (_solutions.ContainsKey(solution.Id))
                    {
                        throw new DuplicateSolutionException($"Solution with ID {solution.Id} already exists");
                    }

                    // Initialize metadata;
                    solution.CreatedAt = DateTime.UtcNow;
                    solution.UpdatedAt = DateTime.UtcNow;
                    solution.UsageCount = 0;
                    solution.Evaluations = new List<SolutionEvaluation>();
                    solution.ApplicationResults = new List<ApplicationResult>();

                    // Add to collections;
                    _solutions[solution.Id] = solution;
                    _solutionIndex.AddSolution(solution);

                    // Extract patterns from new solution;
                    ExtractPatternsFromSolution(solution);

                    // Update statistics;
                    _statistics.TotalSolutions++;
                    _statistics.SolutionsByType[solution.ProblemType] =
                        _statistics.SolutionsByType.GetValueOrDefault(solution.ProblemType) + 1;

                    _logger.LogInformation($"Added new solution: {solution.Id} - {solution.Title}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to add solution: {ex.Message}");
                    throw new SolutionBankException($"Failed to add solution: {solution.Title}", ex);
                }
            }
        }

        /// <summary>
        /// Updates an existing solution;
        /// </summary>
        public void UpdateSolution(string solutionId, Solution updatedSolution)
        {
            if (string.IsNullOrWhiteSpace(solutionId))
                throw new ArgumentException("Solution ID cannot be null or empty", nameof(solutionId));

            if (updatedSolution == null)
                throw new ArgumentNullException(nameof(updatedSolution));

            lock (_syncLock)
            {
                if (!_solutions.ContainsKey(solutionId))
                    throw new SolutionNotFoundException($"Solution not found: {solutionId}");

                try
                {
                    ValidateSolution(updatedSolution);

                    // Preserve metadata;
                    var existingSolution = _solutions[solutionId];
                    updatedSolution.Id = solutionId;
                    updatedSolution.CreatedAt = existingSolution.CreatedAt;
                    updatedSolution.UpdatedAt = DateTime.UtcNow;
                    updatedSolution.UsageCount = existingSolution.UsageCount;
                    updatedSolution.Evaluations = existingSolution.Evaluations;
                    updatedSolution.ApplicationResults = existingSolution.ApplicationResults;

                    // Update solution;
                    _solutions[solutionId] = updatedSolution;

                    // Update index;
                    _solutionIndex.UpdateSolution(updatedSolution);

                    _logger.LogInformation($"Updated solution: {solutionId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to update solution: {ex.Message}");
                    throw new SolutionBankException($"Failed to update solution: {solutionId}", ex);
                }
            }
        }

        /// <summary>
        /// Gets solution bank statistics;
        /// </summary>
        public SolutionBankStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                // Update statistics with current data;
                _statistics.TotalSolutions = _solutions.Count;
                _statistics.TotalPatterns = _solutionPatterns.Count;
                _statistics.TotalSearches = _metricsCollector.TotalSearches;
                _statistics.TotalGenerations = _metricsCollector.TotalGenerations;
                _statistics.AverageSearchTime = _metricsCollector.AverageSearchTime;
                _statistics.LastUpdated = DateTime.UtcNow;

                return _statistics.Clone();
            }
        }

        /// <summary>
        /// Gets solution recommendations for a user;
        /// </summary>
        public List<SolutionRecommendation> GetRecommendations(string userId, RecommendationContext context)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogDebug($"Getting recommendations for user: {userId}");

                    var recommendations = new List<SolutionRecommendation>();

                    // Get user's solution usage history;
                    var userHistory = GetUserUsageHistory(userId);

                    // Get popular solutions;
                    var popularSolutions = GetPopularSolutions(context.Domain, 10);

                    // Get solutions similar to user's interests;
                    var interestBased = GetInterestBasedRecommendations(userId, context);

                    // Get recently added solutions;
                    var recentSolutions = GetRecentSolutions(20);

                    // Combine and rank recommendations;
                    recommendations.AddRange(CreateRecommendations(popularSolutions, RecommendationType.Popular));
                    recommendations.AddRange(CreateRecommendations(interestBased, RecommendationType.InterestBased));
                    recommendations.AddRange(CreateRecommendations(recentSolutions, RecommendationType.Recent));

                    // Apply diversity filtering;
                    var diversified = ApplyDiversityFilter(recommendations, context.MaxRecommendations);

                    // Score recommendations;
                    var scoredRecommendations = ScoreRecommendations(diversified, userId, context);

                    _logger.LogInformation($"Generated {scoredRecommendations.Count} recommendations for user: {userId}");

                    return scoredRecommendations;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Recommendation generation failed: {ex.Message}");
                    return new List<SolutionRecommendation>();
                }
            }
        }

        /// <summary>
        /// Exports solutions in specified format;
        /// </summary>
        public string ExportSolutions(ExportFormat format, ExportOptions options = null)
        {
            lock (_syncLock)
            {
                options ??= ExportOptions.Default;

                try
                {
                    _logger.LogInformation($"Exporting solutions in {format} format");

                    switch (format)
                    {
                        case ExportFormat.JSON:
                            return ExportToJson(options);

                        case ExportFormat.XML:
                            return ExportToXml(options);

                        case ExportFormat.Markdown:
                            return ExportToMarkdown(options);

                        case ExportFormat.CSV:
                            return ExportToCsv(options);

                        default:
                            throw new NotSupportedException($"Export format not supported: {format}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Solution export failed: {ex.Message}");
                    throw new SolutionExportException($"Failed to export solutions in {format} format", ex);
                }
            }
        }

        /// <summary>
        /// Clears all caches;
        /// </summary>
        public void ClearCaches()
        {
            lock (_syncLock)
            {
                _solutionCache.Clear();
                _logger.LogInformation("Solution caches cleared");
            }
        }

        #region Private Helper Methods;

        private void AddSolutionPattern(SolutionPattern pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern.Id))
                pattern.Id = GeneratePatternId(pattern);

            _solutionPatterns[pattern.Id] = pattern;
            _patternIndex.AddPattern(pattern);
        }

        private void ValidateSolution(Solution solution)
        {
            if (string.IsNullOrWhiteSpace(solution.Title))
                throw new ArgumentException("Solution title cannot be null or empty");

            if (string.IsNullOrWhiteSpace(solution.Description))
                throw new ArgumentException("Solution description cannot be null or empty");
        }

        private string GenerateSolutionId(Solution solution)
        {
            var baseId = solution.Title.ToLower().Replace(" ", "_").Replace("-", "_");
            var suffix = 1;
            var candidateId = $"sol_{baseId}";

            while (_solutions.ContainsKey(candidateId))
            {
                suffix++;
                candidateId = $"sol_{baseId}_{suffix}";
            }

            return candidateId;
        }

        private string GeneratePatternId(SolutionPattern pattern)
        {
            var baseId = pattern.Name.ToLower().Replace(" ", "_").Replace("-", "_");
            return $"pattern_{baseId}";
        }

        private ProblemAnalysis AnalyzeProblem(ProblemQuery query)
        {
            var analysis = new ProblemAnalysis;
            {
                OriginalQuery = query,
                ExtractedKeywords = ExtractKeywords(query.Description),
                DetectedDomain = DetectDomain(query.Description),
                ProblemType = DetectProblemType(query.Description),
                ComplexityEstimate = EstimateComplexity(query.Description),
                Constraints = ExtractConstraints(query.Description)
            };

            return analysis;
        }

        private List<SolutionMatch> SearchByKeywords(ProblemQuery query)
        {
            return _solutionIndex.SearchByKeywords(query.Keywords)
                .Select(solId => CreateSolutionMatch(solId, MatchStrategy.Keyword))
                .ToList();
        }

        private List<SolutionMatch> SearchBySemantics(ProblemAnalysis analysis)
        {
            // This would use semantic similarity models in production;
            return _solutionIndex.SearchByDomain(analysis.DetectedDomain)
                .Select(solId => CreateSolutionMatch(solId, MatchStrategy.Semantic))
                .ToList();
        }

        private List<SolutionMatch> SearchByPatterns(ProblemAnalysis analysis)
        {
            var patternMatches = _patternIndex.FindMatchingPatterns(analysis);
            var solutionMatches = new List<SolutionMatch>();

            foreach (var pattern in patternMatches)
            {
                var solutions = _solutionIndex.SearchByPattern(pattern.Id);
                solutionMatches.AddRange(solutions.Select(solId =>
                    CreateSolutionMatch(solId, MatchStrategy.Pattern, pattern.Id)));
            }

            return solutionMatches;
        }

        private List<SolutionMatch> SearchByDomain(ProblemAnalysis analysis)
        {
            return _solutionIndex.SearchByDomain(analysis.DetectedDomain)
                .Select(solId => CreateSolutionMatch(solId, MatchStrategy.Domain))
                .ToList();
        }

        private SolutionMatch CreateSolutionMatch(string solutionId, MatchStrategy strategy, string patternId = null)
        {
            var solution = _solutions[solutionId];
            return new SolutionMatch;
            {
                Solution = solution,
                MatchStrategy = strategy,
                PatternId = patternId,
                BaseScore = CalculateBaseScore(solution)
            };
        }

        private double CalculateBaseScore(Solution solution)
        {
            // Base score calculation;
            double score = 0.0;

            // Popularity factor;
            score += Math.Log(solution.UsageCount + 1) * 0.3;

            // Success rate factor;
            score += solution.SuccessRate * 0.4;

            // Recency factor;
            if (solution.LastUsed.HasValue)
            {
                var daysSinceUse = (DateTime.UtcNow - solution.LastUsed.Value).TotalDays;
                score += (1.0 / (daysSinceUse + 1)) * 0.3;
            }

            return score;
        }

        private List<SolutionMatch> CombineSearchResults(List<SolutionMatch> results)
        {
            var combined = new Dictionary<string, SolutionMatch>();

            foreach (var match in results)
            {
                var solutionId = match.Solution.Id;

                if (combined.TryGetValue(solutionId, out var existing))
                {
                    // Merge match strategies;
                    existing.MatchStrategies.Add(match.MatchStrategy);
                    existing.Score = Math.Max(existing.Score, match.Score);
                }
                else;
                {
                    match.MatchStrategies = new List<MatchStrategy> { match.MatchStrategy };
                    combined[solutionId] = match;
                }
            }

            return combined.Values.ToList();
        }

        private List<SolutionMatch> ScoreSolutions(List<SolutionMatch> matches, ProblemAnalysis analysis)
        {
            foreach (var match in matches)
            {
                var relevanceScore = CalculateRelevanceScore(match.Solution, analysis);
                var confidenceScore = CalculateConfidenceScore(match.Solution);

                match.RelevanceScore = relevanceScore;
                match.ConfidenceScore = confidenceScore;
                match.OverallScore = (relevanceScore * 0.6) + (confidenceScore * 0.4);
            }

            return matches.OrderByDescending(m => m.OverallScore).ToList();
        }

        private double CalculateRelevanceScore(Solution solution, ProblemAnalysis analysis)
        {
            double score = 0.0;

            // Domain match;
            if (solution.Domain == analysis.DetectedDomain)
                score += 0.3;

            // Problem type match;
            if (solution.ProblemType == analysis.ProblemType)
                score += 0.2;

            // Keyword overlap;
            var keywordOverlap = CalculateKeywordOverlap(solution, analysis);
            score += keywordOverlap * 0.3;

            // Complexity match;
            var complexityMatch = CalculateComplexityMatch(solution, analysis);
            score += complexityMatch * 0.2;

            return Math.Min(score, 1.0);
        }

        private double CalculateConfidenceScore(Solution solution)
        {
            return solution.SuccessRate * 0.7 + (solution.UsageCount > 10 ? 0.3 : solution.UsageCount / 10.0 * 0.3);
        }

        private List<SolutionMatch> ApplyFilters(List<SolutionMatch> matches, SolutionFilters filters)
        {
            if (filters == null)
                return matches;

            var filtered = matches;

            if (filters.MinComplexity.HasValue)
            {
                filtered = filtered.Where(m => m.Solution.Complexity >= filters.MinComplexity.Value).ToList();
            }

            if (filters.MaxComplexity.HasValue)
            {
                filtered = filtered.Where(m => m.Solution.Complexity <= filters.MaxComplexity.Value).ToList();
            }

            if (filters.MinSuccessRate.HasValue)
            {
                filtered = filtered.Where(m => m.Solution.SuccessRate >= filters.MinSuccessRate.Value).ToList();
            }

            if (filters.Domains != null && filters.Domains.Any())
            {
                filtered = filtered.Where(m => filters.Domains.Contains(m.Solution.Domain)).ToList();
            }

            if (filters.ProblemTypes != null && filters.ProblemTypes.Any())
            {
                filtered = filtered.Where(m => filters.ProblemTypes.Contains(m.Solution.ProblemType)).ToList();
            }

            return filtered;
        }

        private List<SolutionMatch> PaginateResults(List<SolutionMatch> matches, ProblemQuery query)
        {
            return matches;
                .Skip(query.Skip)
                .Take(query.Take)
                .ToList();
        }

        private void RecordSolutionUsage(string solutionId, SolutionUsageType usageType)
        {
            var usage = new SolutionUsage;
            {
                SolutionId = solutionId,
                UsageType = usageType,
                Timestamp = DateTime.UtcNow;
            };

            _usageHistory.Add(usage);

            // Trim history if too large;
            if (_usageHistory.Count > 100000)
            {
                _usageHistory.RemoveRange(0, 10000);
            }
        }

        private List<SolutionUsage> GetUserUsageHistory(string userId)
        {
            // Filter usage history by user;
            // In production, this would query a user-specific usage database;
            return _usageHistory.Take(100).ToList();
        }

        private List<Solution> GetPopularSolutions(string domain, int count)
        {
            return _solutions.Values;
                .Where(s => string.IsNullOrEmpty(domain) || s.Domain == domain)
                .OrderByDescending(s => s.UsageCount)
                .ThenByDescending(s => s.SuccessRate)
                .Take(count)
                .ToList();
        }

        private List<Solution> GetInterestBasedRecommendations(string userId, RecommendationContext context)
        {
            // Simplified interest-based recommendations;
            // In production, this would use collaborative filtering or content-based filtering;
            return _solutions.Values;
                .Where(s => s.Domain == context.Domain)
                .OrderByDescending(s => s.SuccessRate)
                .Take(context.MaxRecommendations / 2)
                .ToList();
        }

        private List<Solution> GetRecentSolutions(int count)
        {
            return _solutions.Values;
                .OrderByDescending(s => s.CreatedAt)
                .Take(count)
                .ToList();
        }

        private List<SolutionRecommendation> CreateRecommendations(List<Solution> solutions, RecommendationType type)
        {
            return solutions.Select(s => new SolutionRecommendation;
            {
                Solution = s,
                RecommendationType = type,
                Reason = GetRecommendationReason(type, s)
            }).ToList();
        }

        private string GetRecommendationReason(RecommendationType type, Solution solution)
        {
            return type switch;
            {
                RecommendationType.Popular => $"Popular solution with {solution.UsageCount} uses",
                RecommendationType.InterestBased => $"Matches your interests in {solution.Domain}",
                RecommendationType.Recent => $"Recently added solution",
                _ => "Recommended based on your activity"
            };
        }

        private List<SolutionRecommendation> ApplyDiversityFilter(List<SolutionRecommendation> recommendations, int maxCount)
        {
            // Ensure diversity across domains and problem types;
            var diversified = new List<SolutionRecommendation>();
            var domainsCovered = new HashSet<string>();
            var typesCovered = new HashSet<ProblemType>();

            foreach (var rec in recommendations.OrderByDescending(r => r.Solution.UsageCount))
            {
                if (domainsCovered.Count >= 3 && domainsCovered.Contains(rec.Solution.Domain) &&
                    typesCovered.Count >= 3 && typesCovered.Contains(rec.Solution.ProblemType))
                {
                    continue;
                }

                diversified.Add(rec);
                domainsCovered.Add(rec.Solution.Domain);
                typesCovered.Add(rec.Solution.ProblemType);

                if (diversified.Count >= maxCount)
                    break;
            }

            return diversified;
        }

        private List<SolutionRecommendation> ScoreRecommendations(List<SolutionRecommendation> recommendations, string userId, RecommendationContext context)
        {
            foreach (var rec in recommendations)
            {
                rec.Score = CalculateRecommendationScore(rec, userId, context);
            }

            return recommendations.OrderByDescending(r => r.Score).ToList();
        }

        private double CalculateRecommendationScore(SolutionRecommendation recommendation, string userId, RecommendationContext context)
        {
            double score = 0.0;

            // Solution quality;
            score += recommendation.Solution.SuccessRate * 0.4;

            // Popularity;
            score += Math.Log(recommendation.Solution.UsageCount + 1) * 0.3;

            // Recency;
            var daysSinceCreation = (DateTime.UtcNow - recommendation.Solution.CreatedAt).TotalDays;
            score += (1.0 / (daysSinceCreation + 1)) * 0.2;

            // Domain relevance;
            if (recommendation.Solution.Domain == context.Domain)
                score += 0.1;

            return Math.Min(score, 1.0);
        }

        private void ExtractPatternsFromSolution(Solution solution)
        {
            var patterns = _patternExtractor.ExtractPatterns(solution);
            foreach (var pattern in patterns)
            {
                if (!_solutionPatterns.ContainsKey(pattern.Id))
                {
                    AddSolutionPattern(pattern);
                }
            }
        }

        private void ExtractPatternFromApplication(Solution solution, ApplicationResult result)
        {
            // Extract successful application patterns;
            var pattern = _patternExtractor.ExtractPatternFromResult(solution, result);
            if (pattern != null && !_solutionPatterns.ContainsKey(pattern.Id))
            {
                AddSolutionPattern(pattern);
            }
        }

        private double CalculateUpdatedSuccessRate(Solution solution)
        {
            if (solution.ApplicationResults.Count == 0)
                return solution.SuccessRate;

            var successfulApplications = solution.ApplicationResults.Count(r => r.SuccessLevel >= SuccessLevel.Medium);
            return (double)successfulApplications / solution.ApplicationResults.Count;
        }

        private void UpdateSolutionEffectiveness(Solution solution, ApplicationResult result)
        {
            // Update solution metrics based on application results;
            solution.EffectivenessScore = CalculateEffectivenessScore(solution);
        }

        private double CalculateEffectivenessScore(Solution solution)
        {
            // Composite effectiveness score;
            return (solution.SuccessRate * 0.6) +
                   (solution.Evaluations.Average(e => e.OverallScore) * 0.4);
        }

        private string ExportToJson(ExportOptions options)
        {
            var exportData = new;
            {
                Metadata = new;
                {
                    ExportDate = DateTime.UtcNow,
                    TotalSolutions = _solutions.Count,
                    TotalPatterns = _solutionPatterns.Count,
                    Version = "1.0"
                },
                Solutions = options.IncludeSolutions ? _solutions.Values : null,
                Patterns = options.IncludePatterns ? _solutionPatterns.Values : null,
                Statistics = options.IncludeStatistics ? _statistics : null;
            };

            return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = options.PrettyPrint,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });
        }

        private string ExportToMarkdown(ExportOptions options)
        {
            var markdown = new System.Text.StringBuilder();
            markdown.AppendLine($"# Solution Bank Export");
            markdown.AppendLine($"**Export Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            markdown.AppendLine($"**Total Solutions:** {_solutions.Count}");
            markdown.AppendLine($"**Total Patterns:** {_solutionPatterns.Count}");
            markdown.AppendLine();

            if (options.IncludeSolutions)
            {
                markdown.AppendLine("## Solutions");
                markdown.AppendLine();
                foreach (var solution in _solutions.Values.Take(options.MaxItems))
                {
                    markdown.AppendLine($"### {solution.Title}");
                    markdown.AppendLine($"**ID:** {solution.Id}");
                    markdown.AppendLine($"**Domain:** {solution.Domain}");
                    markdown.AppendLine($"**Type:** {solution.ProblemType}");
                    markdown.AppendLine($"**Success Rate:** {solution.SuccessRate:P0}");
                    markdown.AppendLine($"**Usage Count:** {solution.UsageCount}");
                    markdown.AppendLine();
                }
            }

            return markdown.ToString();
        }

        private string ExportToCsv(ExportOptions options)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("ID,Title,Domain,Type,SuccessRate,UsageCount,CreatedAt");

            foreach (var solution in _solutions.Values)
            {
                csv.AppendLine($"{solution.Id},{EscapeCsv(solution.Title)},{solution.Domain},{solution.ProblemType},{solution.SuccessRate},{solution.UsageCount},{solution.CreatedAt:yyyy-MM-dd}");
            }

            return csv.ToString();
        }

        private string ExportToXml(ExportOptions options)
        {
            // Simplified XML export;
            return $"<SolutionBank exportDate=\"{DateTime.UtcNow:O}\" totalSolutions=\"{_solutions.Count}\" />";
        }

        private string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        #region Analysis Helper Methods;

        private List<string> ExtractKeywords(string text)
        {
            // Simple keyword extraction;
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLower())
                .Distinct()
                .ToList();
        }

        private string DetectDomain(string description)
        {
            // Simple domain detection;
            var domains = new Dictionary<string, string>
            {
                { "algorithm", "Computer Science" },
                { "sort", "Computer Science" },
                { "search", "Computer Science" },
                { "database", "Database" },
                { "query", "Database" },
                { "performance", "Optimization" },
                { "memory", "Optimization" },
                { "design", "Software Architecture" },
                { "pattern", "Software Architecture" }
            };

            foreach (var domain in domains)
            {
                if (description.Contains(domain.Key, StringComparison.OrdinalIgnoreCase))
                    return domain.Value;
            }

            return "General";
        }

        private ProblemType DetectProblemType(string description)
        {
            // Simple problem type detection;
            if (description.Contains("algorithm", StringComparison.OrdinalIgnoreCase))
                return ProblemType.Algorithmic;

            if (description.Contains("design", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("pattern", StringComparison.OrdinalIgnoreCase))
                return ProblemType.Design;

            if (description.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("bug", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("fix", StringComparison.OrdinalIgnoreCase))
                return ProblemType.Troubleshooting;

            if (description.Contains("optimize", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("performance", StringComparison.OrdinalIgnoreCase))
                return ProblemType.Optimization;

            if (description.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("innovate", StringComparison.OrdinalIgnoreCase))
                return ProblemType.Creative;

            return ProblemType.General;
        }

        private SolutionComplexity EstimateComplexity(string description)
        {
            // Simple complexity estimation;
            var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (words < 10) return SolutionComplexity.Low;
            if (words < 20) return SolutionComplexity.Medium;
            if (words < 30) return SolutionComplexity.High;
            return SolutionComplexity.VeryHigh;
        }

        private List<string> ExtractConstraints(string description)
        {
            // Simple constraint extraction;
            var constraints = new List<string>();

            if (description.Contains("time", StringComparison.OrdinalIgnoreCase) &&
                description.Contains("complexity", StringComparison.OrdinalIgnoreCase))
                constraints.Add("Time complexity requirements");

            if (description.Contains("space", StringComparison.OrdinalIgnoreCase) &&
                description.Contains("complexity", StringComparison.OrdinalIgnoreCase))
                constraints.Add("Space complexity requirements");

            if (description.Contains("memory", StringComparison.OrdinalIgnoreCase))
                constraints.Add("Memory constraints");

            return constraints;
        }

        private double CalculateKeywordOverlap(Solution solution, ProblemAnalysis analysis)
        {
            var solutionKeywords = ExtractKeywords(solution.Description + " " + solution.Title);
            var problemKeywords = analysis.ExtractedKeywords;

            if (solutionKeywords.Count == 0 || problemKeywords.Count == 0)
                return 0.0;

            var intersection = solutionKeywords.Intersect(problemKeywords).Count();
            return (double)intersection / Math.Max(solutionKeywords.Count, problemKeywords.Count);
        }

        private double CalculateComplexityMatch(Solution solution, ProblemAnalysis analysis)
        {
            var solutionComplexity = (int)solution.Complexity;
            var problemComplexity = (int)analysis.ComplexityEstimate;

            var difference = Math.Abs(solutionComplexity - problemComplexity);
            return 1.0 - (difference / 4.0); // 4 is max complexity difference;
        }

        #endregion;

        #region Not Implemented Stubs (Would be implemented in production)

        private List<string> GenerateSearchSuggestions(ProblemQuery query, ProblemAnalysis analysis)
        {
            // Generate search suggestions based on query;
            return new List<string>
            {
                $"Try searching for solutions in {analysis.DetectedDomain} domain",
                $"Consider {analysis.ProblemType} type solutions"
            };
        }

        private AdaptationAnalysis AnalyzeAdaptationRequirements(Solution solution, AdaptationContext context)
        {
            return new AdaptationAnalysis;
            {
                RequiredChanges = new List<AdaptationChange>(),
                CompatibilityScore = 0.8;
            };
        }

        private ValidationResult ValidateAdaptation(AdaptedSolution adaptedSolution, AdaptationContext context)
        {
            return new ValidationResult;
            {
                IsValid = true,
                Issues = new List<string>(),
                Confidence = 0.9;
            };
        }

        private AdaptationReport GenerateAdaptationReport(Solution baseSolution, AdaptedSolution adaptedSolution,
            AdaptationAnalysis analysis, ValidationResult validation)
        {
            return new AdaptationReport;
            {
                Summary = "Adaptation completed successfully",
                ChangesApplied = new List<string>(),
                ValidationResults = new List<string>()
            };
        }

        private void LearnFromAdaptation(Solution baseSolution, AdaptedSolution adaptedSolution, AdaptationContext context)
        {
            // Learn from successful adaptations;
        }

        private ProblemAnalysis AnalyzeProblemSpecification(ProblemSpecification specification)
        {
            return new ProblemAnalysis;
            {
                OriginalQuery = new ProblemQuery { Description = specification.Description }
            };
        }

        private List<Solution> FindSimilarSolutions(ProblemAnalysis analysis, int maxCount)
        {
            return _solutions.Values;
                .Where(s => s.Domain == analysis.DetectedDomain)
                .Take(maxCount)
                .ToList();
        }

        private List<SolutionPattern> ExtractRelevantPatterns(ProblemAnalysis analysis)
        {
            return _solutionPatterns.Values;
                .Where(p => p.Applicability.Any(a => analysis.DetectedDomain.Contains(a)))
                .ToList();
        }

        private Solution GenerateSolutionUsingAI(ProblemAnalysis analysis, List<Solution> similarSolutions,
            List<SolutionPattern> patterns, GenerationOptions options)
        {
            // AI-based solution generation;
            return new Solution;
            {
                Title = $"Generated Solution for {analysis.DetectedDomain}",
                Description = "AI-generated solution based on similar patterns",
                ProblemType = analysis.ProblemType,
                Domain = analysis.DetectedDomain;
            };
        }

        private ValidationResult ValidateGeneratedSolution(Solution solution, ProblemSpecification specification)
        {
            return new ValidationResult;
            {
                IsValid = true,
                Issues = new List<string>(),
                Confidence = 0.8;
            };
        }

        private Solution OptimizeSolution(Solution solution, GenerationOptions options)
        {
            return solution; // No optimization in stub;
        }

        private GenerationReport CreateGenerationReport(ProblemSpecification specification, Solution generatedSolution,
            List<Solution> similarSolutions, List<SolutionPattern> patterns, ValidationResult validation)
        {
            return new GenerationReport;
            {
                Summary = "Solution generated successfully",
                UsedReferences = new List<string>(),
                GenerationMethod = "AI Pattern Synthesis"
            };
        }

        private void StoreGeneratedSolution(Solution solution, ProblemSpecification specification)
        {
            // Store the generated solution;
            AddSolution(solution);
        }

        private EvaluationReport GenerateEvaluationReport(Solution solution, Evaluation evaluation, EvaluationContext context)
        {
            return new EvaluationReport;
            {
                Summary = $"Solution scored {evaluation.OverallScore:F2}",
                Strengths = new List<string>(),
                Weaknesses = new List<string>()
            };
        }

        private void StoreApplicationFeedback(string solutionId, ApplicationResult result)
        {
            // Store application feedback for learning;
        }

        #endregion;

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _solutions.Clear();
                    _problemTemplates.Clear();
                    _solutionPatterns.Clear();
                    _usageHistory.Clear();

                    _solutionCache.Dispose();
                    _solutionIndex.Dispose();
                    _patternIndex.Dispose();

                    _logger.LogInformation("SolutionBank disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SolutionBank()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface ISolutionBank;
    {
        SolutionSearchResult SearchSolutions(ProblemQuery query);
        Solution GetSolution(string solutionId);
        AdaptedSolution AdaptSolution(string solutionId, AdaptationContext context);
        GeneratedSolution GenerateSolution(ProblemSpecification specification, GenerationOptions options);
        SolutionEvaluation EvaluateSolution(string solutionId, EvaluationContext context);
        void LearnFromApplication(string solutionId, ApplicationResult result);
        void AddSolution(Solution solution);
        void UpdateSolution(string solutionId, Solution updatedSolution);
        SolutionBankStatistics GetStatistics();
        List<SolutionRecommendation> GetRecommendations(string userId, RecommendationContext context);
        string ExportSolutions(ExportFormat format, ExportOptions options);
        void ClearCaches();
    }

    public interface ISolutionAnalyzer;
    {
        Evaluation Evaluate(Solution solution, EvaluationContext context);
        ProblemAnalysis AnalyzeProblem(string description);
        AdaptationAnalysis AnalyzeAdaptation(Solution solution, AdaptationContext context);
    }

    /// <summary>
    /// Represents a problem solution in the bank;
    /// </summary>
    public class Solution;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public ProblemType ProblemType { get; set; }
        public string Domain { get; set; }
        public SolutionComplexity Complexity { get; set; }
        public Implementation Implementation { get; set; }
        public List<SolutionStep> Steps { get; set; }
        public List<string> Tags { get; set; }
        public double SuccessRate { get; set; }
        public int UsageCount { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<SolutionEvaluation> Evaluations { get; set; }
        public List<ApplicationResult> ApplicationResults { get; set; }
        public double EffectivenessScore { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public Solution()
        {
            Steps = new List<SolutionStep>();
            Tags = new List<string>();
            Evaluations = new List<SolutionEvaluation>();
            ApplicationResults = new List<ApplicationResult>();
            Metadata = new Dictionary<string, object>();
            SuccessRate = 0.5; // Default;
        }
    }

    /// <summary>
    /// Solution implementation details;
    /// </summary>
    public class Implementation;
    {
        public string Language { get; set; }
        public string Code { get; set; }
        public string TimeComplexity { get; set; }
        public string SpaceComplexity { get; set; }
        public string DesignPattern { get; set; }
        public string UseCase { get; set; }
        public List<string> Dependencies { get; set; }
        public Dictionary<string, object> Configuration { get; set; }

        public Implementation()
        {
            Dependencies = new List<string>();
            Configuration = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Step in a solution procedure;
    /// </summary>
    public class SolutionStep;
    {
        public int Order { get; set; }
        public string Action { get; set; }
        public string Description { get; set; }
        public string ExpectedOutcome { get; set; }
        public List<string> Tips { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        public SolutionStep()
        {
            Tips = new List<string>();
            Parameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Problem query for solution search;
    /// </summary>
    public class ProblemQuery;
    {
        public string Description { get; set; }
        public List<string> Keywords { get; set; }
        public SolutionFilters Filters { get; set; }
        public SearchStrategy SearchStrategy { get; set; }
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 20;
        public Dictionary<string, object> Context { get; set; }

        public ProblemQuery()
        {
            Keywords = new List<string>();
            Filters = new SolutionFilters();
            Context = new Dictionary<string, object>();
            SearchStrategy = SearchStrategy.Comprehensive;
        }

        public string GetCacheKey()
        {
            return $"{Description}_{Filters?.GetHashCode()}_{Skip}_{Take}_{SearchStrategy}";
        }
    }

    /// <summary>
    /// Solution search result;
    /// </summary>
    public class SolutionSearchResult;
    {
        public ProblemQuery Query { get; set; }
        public ProblemAnalysis ProblemAnalysis { get; set; }
        public List<SolutionMatch> Matches { get; set; }
        public int TotalCount { get; set; }
        public TimeSpan SearchDuration { get; set; }
        public SearchStrategy SearchStrategy { get; set; }
        public List<string> Suggestions { get; set; }
        public string SearchId { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public SolutionSearchResult()
        {
            Matches = new List<SolutionMatch>();
            Suggestions = new List<string>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Solution match in search results;
    /// </summary>
    public class SolutionMatch;
    {
        public Solution Solution { get; set; }
        public List<MatchStrategy> MatchStrategies { get; set; }
        public string PatternId { get; set; }
        public double BaseScore { get; set; }
        public double RelevanceScore { get; set; }
        public double ConfidenceScore { get; set; }
        public double OverallScore { get; set; }
        public string MatchExplanation { get; set; }

        public SolutionMatch()
        {
            MatchStrategies = new List<MatchStrategy>();
        }
    }

    /// <summary>
    /// Adapted solution for specific context;
    /// </summary>
    public class AdaptedSolution;
    {
        public Solution BaseSolution { get; set; }
        public Solution AdaptedSolution { get; set; }
        public AdaptationContext AdaptationContext { get; set; }
        public AdaptationAnalysis AdaptationAnalysis { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public AdaptationReport AdaptationReport { get; set; }
        public string AdaptationId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// AI-generated solution;
    /// </summary>
    public class GeneratedSolution;
    {
        public ProblemSpecification Specification { get; set; }
        public Solution GeneratedSolution { get; set; }
        public List<Solution> SimilarSolutions { get; set; }
        public List<SolutionPattern> UsedPatterns { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public GenerationReport GenerationReport { get; set; }
        public string GenerationId { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public DateTime Timestamp { get; set; }

        public GeneratedSolution()
        {
            SimilarSolutions = new List<Solution>();
            UsedPatterns = new List<SolutionPattern>();
        }
    }

    /// <summary>
    /// Solution evaluation result;
    /// </summary>
    public class SolutionEvaluation;
    {
        public Solution Solution { get; set; }
        public Evaluation Evaluation { get; set; }
        public EvaluationContext Context { get; set; }
        public EvaluationReport EvaluationReport { get; set; }
        public string EvaluationId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Solution pattern for intelligent matching;
    /// </summary>
    public class SolutionPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Applicability { get; set; }
        public PatternStructure Structure { get; set; }
        public List<string> Examples { get; set; }
        public List<string> Tags { get; set; }
        public int UsageCount { get; set; }
        public double Effectiveness { get; set; }
        public DateTime CreatedAt { get; set; }

        public SolutionPattern()
        {
            Applicability = new List<string>();
            Examples = new List<string>();
            Tags = new List<string>();
            CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Solution recommendation for users;
    /// </summary>
    public class SolutionRecommendation;
    {
        public Solution Solution { get; set; }
        public RecommendationType RecommendationType { get; set; }
        public string Reason { get; set; }
        public double Score { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Problem analysis for intelligent search;
    /// </summary>
    public class ProblemAnalysis;
    {
        public ProblemQuery OriginalQuery { get; set; }
        public List<string> ExtractedKeywords { get; set; }
        public string DetectedDomain { get; set; }
        public ProblemType ProblemType { get; set; }
        public SolutionComplexity ComplexityEstimate { get; set; }
        public List<string> Constraints { get; set; }
        public Dictionary<string, object> Features { get; set; }

        public ProblemAnalysis()
        {
            ExtractedKeywords = new List<string>();
            Constraints = new List<string>();
            Features = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Solution usage tracking;
    /// </summary>
    public class SolutionUsage;
    {
        public string SolutionId { get; set; }
        public SolutionUsageType UsageType { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public SolutionUsage()
        {
            Context = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Solution bank statistics;
    /// </summary>
    public class SolutionBankStatistics;
    {
        public int TotalSolutions { get; set; }
        public int TotalPatterns { get; set; }
        public int TotalSearches { get; set; }
        public int TotalGenerations { get; set; }
        public TimeSpan AverageSearchTime { get; set; }
        public Dictionary<ProblemType, int> SolutionsByType { get; set; }
        public Dictionary<string, int> SolutionsByDomain { get; set; }
        public DateTime LastUpdated { get; set; }

        public SolutionBankStatistics()
        {
            SolutionsByType = new Dictionary<ProblemType, int>();
            SolutionsByDomain = new Dictionary<string, int>();
        }

        public SolutionBankStatistics Clone()
        {
            return (SolutionBankStatistics)MemberwiseClone();
        }
    }

    /// <summary>
    /// Configuration for solution bank;
    /// </summary>
    public class SolutionBankConfiguration;
    {
        public static SolutionBankConfiguration Default => new SolutionBankConfiguration();

        public CacheConfiguration CacheConfiguration { get; set; }
        public IndexConfiguration IndexConfiguration { get; set; }
        public GenerationConfiguration GenerationConfiguration { get; set; }
        public List<string> DefaultSolutionSources { get; set; }
        public bool EnableLearning { get; set; } = true;
        public int MaxCacheSize { get; set; } = 10000;

        public SolutionBankConfiguration()
        {
            CacheConfiguration = new CacheConfiguration();
            IndexConfiguration = new IndexConfiguration();
            GenerationConfiguration = new GenerationConfiguration();
            DefaultSolutionSources = new List<string>();
        }
    }

    #region Enumerations;

    public enum ProblemType;
    {
        General = 0,
        Algorithmic = 1,
        Design = 2,
        Troubleshooting = 3,
        Optimization = 4,
        Creative = 5,
        Mathematical = 6,
        Logical = 7;
    }

    public enum SolutionComplexity;
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4;
    }

    public enum SearchStrategy;
    {
        Keyword = 0,
        Semantic = 1,
        Pattern = 2,
        Hybrid = 3,
        Comprehensive = 4;
    }

    public enum MatchStrategy;
    {
        Keyword = 0,
        Semantic = 1,
        Pattern = 2,
        Domain = 3,
        Popularity = 4;
    }

    public enum SolutionUsageType;
    {
        Retrieval = 0,
        Adaptation = 1,
        Evaluation = 2,
        Application = 3,
        Generation = 4;
    }

    public enum RecommendationType;
    {
        Popular = 0,
        InterestBased = 1,
        Recent = 2,
        Similar = 3,
        Trending = 4;
    }

    public enum ExportFormat;
    {
        JSON = 0,
        XML = 1,
        Markdown = 2,
        CSV = 3;
    }

    public enum SuccessLevel;
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Excellent = 4;
    }

    #endregion;

    #region Supporting Classes;

    public class SolutionFilters;
    {
        public SolutionComplexity? MinComplexity { get; set; }
        public SolutionComplexity? MaxComplexity { get; set; }
        public double? MinSuccessRate { get; set; }
        public List<string> Domains { get; set; }
        public List<ProblemType> ProblemTypes { get; set; }
        public List<string> Tags { get; set; }

        public SolutionFilters()
        {
            Domains = new List<string>();
            ProblemTypes = new List<ProblemType>();
            Tags = new List<string>();
        }
    }

    public class AdaptationContext;
    {
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<string> Constraints { get; set; }
        public AdaptationGoal Goal { get; set; }

        public AdaptationContext()
        {
            Parameters = new Dictionary<string, object>();
            Constraints = new List<string>();
        }
    }

    public class ProblemSpecification;
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public List<string> Requirements { get; set; }
        public List<string> Constraints { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public ProblemSpecification()
        {
            Requirements = new List<string>();
            Constraints = new List<string>();
            Context = new Dictionary<string, object>();
        }
    }

    public class GenerationOptions;
    {
        public bool StoreGeneratedSolution { get; set; } = true;
        public int MaxSimilarSolutions { get; set; } = 5;
        public double CreativityLevel { get; set; } = 0.7;
        public Dictionary<string, object> Parameters { get; set; }

        public GenerationOptions()
        {
            Parameters = new Dictionary<string, object>();
        }
    }

    public class EvaluationContext;
    {
        public string Scenario { get; set; }
        public Dictionary<string, object> TestData { get; set; }
        public List<EvaluationCriteria> Criteria { get; set; }

        public EvaluationContext()
        {
            TestData = new Dictionary<string, object>();
            Criteria = new List<EvaluationCriteria>();
        }
    }

    public class RecommendationContext;
    {
        public string Domain { get; set; }
        public int MaxRecommendations { get; set; } = 10;
        public Dictionary<string, object> UserPreferences { get; set; }

        public RecommendationContext()
        {
            UserPreferences = new Dictionary<string, object>();
        }
    }

    public class ExportOptions;
    {
        public static ExportOptions Default => new ExportOptions();

        public bool IncludeSolutions { get; set; } = true;
        public bool IncludePatterns { get; set; } = true;
        public bool IncludeStatistics { get; set; } = true;
        public bool PrettyPrint { get; set; } = true;
        public int MaxItems { get; set; } = 1000;
        public List<string> SelectedDomains { get; set; }

        public ExportOptions()
        {
            SelectedDomains = new List<string>();
        }
    }

    // Additional supporting classes would be defined here...

    #endregion;

    #region Internal Support Classes;

    internal class SolutionIndex : IDisposable
    {
        private readonly Dictionary<string, List<string>> _keywordIndex;
        private readonly Dictionary<string, List<string>> _domainIndex;
        private readonly Dictionary<string, List<string>> _typeIndex;
        private readonly Dictionary<string, List<string>> _patternIndex;

        public int Count => GetTotalCount();

        public SolutionIndex()
        {
            _keywordIndex = new Dictionary<string, List<string>>();
            _domainIndex = new Dictionary<string, List<string>>();
            _typeIndex = new Dictionary<string, List<string>>();
            _patternIndex = new Dictionary<string, List<string>>();
        }

        public void AddSolution(Solution solution)
        {
            // Index by keywords;
            var keywords = ExtractKeywords(solution.Title + " " + solution.Description);
            foreach (var keyword in keywords)
            {
                if (!_keywordIndex.ContainsKey(keyword))
                    _keywordIndex[keyword] = new List<string>();
                _keywordIndex[keyword].Add(solution.Id);
            }

            // Index by domain;
            if (!string.IsNullOrEmpty(solution.Domain))
            {
                if (!_domainIndex.ContainsKey(solution.Domain))
                    _domainIndex[solution.Domain] = new List<string>();
                _domainIndex[solution.Domain].Add(solution.Id);
            }

            // Index by problem type;
            var typeKey = solution.ProblemType.ToString();
            if (!_typeIndex.ContainsKey(typeKey))
                _typeIndex[typeKey] = new List<string>();
            _typeIndex[typeKey].Add(solution.Id);

            // Index by patterns (from tags)
            var patternTags = solution.Tags.Where(t => t.Contains("pattern")).ToList();
            foreach (var patternTag in patternTags)
            {
                if (!_patternIndex.ContainsKey(patternTag))
                    _patternIndex[patternTag] = new List<string>();
                _patternIndex[patternTag].Add(solution.Id);
            }
        }

        public void UpdateSolution(Solution solution)
        {
            // Remove old indexes;
            // In production, we would track and remove old indexes;
            // For simplicity, we'll just re-add;
            AddSolution(solution);
        }

        public List<string> SearchByKeywords(List<string> keywords)
        {
            var results = new HashSet<string>();

            foreach (var keyword in keywords)
            {
                if (_keywordIndex.TryGetValue(keyword, out var solutions))
                {
                    results.UnionWith(solutions);
                }
            }

            return results.ToList();
        }

        public List<string> SearchByDomain(string domain)
        {
            return _domainIndex.TryGetValue(domain, out var solutions)
                ? solutions;
                : new List<string>();
        }

        public List<string> SearchByPattern(string patternId)
        {
            return _patternIndex.TryGetValue(patternId, out var solutions)
                ? solutions;
                : new List<string>();
        }

        private List<string> ExtractKeywords(string text)
        {
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLower())
                .Distinct()
                .ToList();
        }

        private int GetTotalCount()
        {
            var allIds = new HashSet<string>();
            foreach (var ids in _keywordIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _domainIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _typeIndex.Values) allIds.UnionWith(ids);
            return allIds.Count;
        }

        public void Dispose()
        {
            _keywordIndex.Clear();
            _domainIndex.Clear();
            _typeIndex.Clear();
            _patternIndex.Clear();
        }
    }

    // Additional internal classes would be defined here...

    #endregion;

    #region Exceptions;

    public class SolutionBankException : Exception
    {
        public SolutionBankException(string message) : base(message) { }
        public SolutionBankException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SolutionNotFoundException : Exception
    {
        public string SolutionId { get; }

        public SolutionNotFoundException(string solutionId)
            : base($"Solution not found: {solutionId}")
        {
            SolutionId = solutionId;
        }
    }

    public class DuplicateSolutionException : Exception
    {
        public DuplicateSolutionException(string message) : base(message) { }
    }

    public class SolutionSearchException : Exception
    {
        public SolutionSearchException(string message) : base(message) { }
        public SolutionSearchException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SolutionAdaptationException : Exception
    {
        public SolutionAdaptationException(string message) : base(message) { }
        public SolutionAdaptationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SolutionGenerationException : Exception
    {
        public SolutionGenerationException(string message) : base(message) { }
        public SolutionGenerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SolutionEvaluationException : Exception
    {
        public SolutionEvaluationException(string message) : base(message) { }
        public SolutionEvaluationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SolutionExportException : Exception
    {
        public SolutionExportException(string message) : base(message) { }
        public SolutionExportException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
