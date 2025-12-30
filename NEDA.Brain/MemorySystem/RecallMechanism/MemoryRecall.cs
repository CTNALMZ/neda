using Microsoft.Extensions.Logging;
using NEDA.Brain.KnowledgeBase.ProcedureLibrary;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.AI.KnowledgeBase.LongTermMemory.LongTermMemory;

namespace NEDA.Brain.MemorySystem.RecallMechanism;
{
    /// <summary>
    /// Memory Recall Engine - Provides intelligent memory retrieval mechanisms;
    /// with contextual association and pattern-based recall;
    /// </summary>
    public class MemoryRecall : IMemoryRecall;
    {
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ILongTermMemory _longTermMemory;
        private readonly IAssociationEngine _associationEngine;
        private readonly ILogger<MemoryRecall> _logger;
        private readonly IRetrievalSystem _retrievalSystem;

        // Configuration;
        private readonly MemoryRecallConfig _config;

        // Cache for frequently accessed memories;
        private readonly MemoryCache<MemoryItem> _recallCache;

        // Statistics;
        private readonly RecallMetrics _metrics;

        /// <summary>
        /// Initializes a new instance of MemoryRecall;
        /// </summary>
        public MemoryRecall(
            IShortTermMemory shortTermMemory,
            ILongTermMemory longTermMemory,
            IAssociationEngine associationEngine,
            IRetrievalSystem retrievalSystem,
            ILogger<MemoryRecall> logger,
            MemoryRecallConfig config = null)
        {
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _associationEngine = associationEngine ?? throw new ArgumentNullException(nameof(associationEngine));
            _retrievalSystem = retrievalSystem ?? throw new ArgumentNullException(nameof(retrievalSystem));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _config = config ?? MemoryRecallConfig.Default;
            _recallCache = new MemoryCache<MemoryItem>(_config.CacheSize, _config.CacheExpiration);
            _metrics = new RecallMetrics();

            _logger.LogInformation("MemoryRecall initialized with config: {@Config}", _config);
        }

        /// <summary>
        /// Recalls a specific memory by its unique identifier;
        /// </summary>
        public async Task<MemoryRecallResult> RecallByIdAsync(Guid memoryId, RecallContext context = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("MemoryRecall.RecallById"))
            {
                try
                {
                    _metrics.IncrementRecallAttempts();

                    // Check cache first;
                    var cachedItem = _recallCache.Get(memoryId.ToString());
                    if (cachedItem != null)
                    {
                        _metrics.IncrementCacheHits();
                        _logger.LogDebug("Memory {MemoryId} retrieved from cache", memoryId);
                        return MemoryRecallResult.Success(cachedItem, recallSource: RecallSource.Cache);
                    }

                    // Try short-term memory;
                    var shortTermResult = await _shortTermMemory.RetrieveAsync(memoryId);
                    if (shortTermResult.IsSuccess)
                    {
                        CacheMemoryItem(shortTermResult.Memory);
                        _logger.LogDebug("Memory {MemoryId} retrieved from short-term memory", memoryId);
                        return MemoryRecallResult.Success(shortTermResult.Memory, recallSource: RecallSource.ShortTerm);
                    }

                    // Try long-term memory;
                    var longTermResult = await _longTermMemory.RetrieveAsync(memoryId);
                    if (longTermResult.IsSuccess)
                    {
                        CacheMemoryItem(longTermResult.Memory);

                        // Update memory strength based on recall;
                        await UpdateMemoryStrengthAsync(longTermResult.Memory, context);

                        _logger.LogDebug("Memory {MemoryId} retrieved from long-term memory", memoryId);
                        return MemoryRecallResult.Success(longTermResult.Memory, recallSource: RecallSource.LongTerm);
                    }

                    _metrics.IncrementRecallFailures();
                    _logger.LogWarning("Memory {MemoryId} not found in any memory store", memoryId);
                    return MemoryRecallResult.NotFound($"Memory with ID {memoryId} not found");
                }
                catch (Exception ex)
                {
                    _metrics.IncrementRecallErrors();
                    _logger.LogError(ex, "Error recalling memory {MemoryId}", memoryId);
                    throw new MemoryRecallException($"Failed to recall memory {memoryId}", ex);
                }
            }
        }

        /// <summary>
        /// Recalls memories based on contextual cues and associations;
        /// </summary>
        public async Task<IEnumerable<MemoryRecallResult>> RecallByContextAsync(
            RecallContext context,
            RecallOptions options = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("MemoryRecall.RecallByContext"))
            {
                options ??= RecallOptions.Default;

                try
                {
                    _metrics.IncrementContextRecallAttempts();

                    // Generate search queries from context;
                    var searchQueries = GenerateSearchQueries(context, options);
                    _logger.LogDebug("Generated {Count} search queries from context", searchQueries.Count);

                    var recallTasks = new List<Task<IEnumerable<MemoryRecallResult>>>();

                    // Search in short-term memory;
                    if (options.SearchShortTermMemory)
                    {
                        recallTasks.Add(SearchShortTermMemoryAsync(searchQueries, options));
                    }

                    // Search in long-term memory;
                    if (options.SearchLongTermMemory)
                    {
                        recallTasks.Add(SearchLongTermMemoryAsync(searchQueries, options));
                    }

                    // Wait for all searches to complete;
                    var results = await Task.WhenAll(recallTasks);

                    // Merge and rank results;
                    var mergedResults = MergeAndRankResults(results.SelectMany(r => r), context, options);

                    // Apply recall limit;
                    var finalResults = mergedResults.Take(options.MaxResults).ToList();

                    _logger.LogInformation("Context recall returned {Count} results", finalResults.Count);
                    return finalResults;
                }
                catch (Exception ex)
                {
                    _metrics.IncrementContextRecallErrors();
                    _logger.LogError(ex, "Error in context-based recall");
                    throw new MemoryRecallException("Context-based recall failed", ex);
                }
            }
        }

        /// <summary>
        /// Recalls memories based on associative patterns;
        /// </summary>
        public async Task<AssociativeRecallResult> RecallAssociativelyAsync(
            string seedConcept,
            int maxDepth = 3,
            int maxAssociations = 10)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("MemoryRecall.RecallAssociatively"))
            {
                try
                {
                    _metrics.IncrementAssociativeRecallAttempts();

                    var associationGraph = new AssociationGraph();
                    var visitedConcepts = new HashSet<string>();
                    var recallQueue = new Queue<AssociationNode>();

                    // Start with seed concept;
                    var seedNode = new AssociationNode(seedConcept, 0);
                    recallQueue.Enqueue(seedNode);
                    visitedConcepts.Add(seedConcept);

                    while (recallQueue.Count > 0 && associationGraph.Nodes.Count < maxAssociations)
                    {
                        var currentNode = recallQueue.Dequeue();

                        if (currentNode.Depth >= maxDepth)
                            continue;

                        // Find associations for current concept;
                        var associations = await _associationEngine.FindAssociationsAsync(
                            currentNode.Concept,
                            maxAssociations: 5);

                        foreach (var association in associations)
                        {
                            if (!visitedConcepts.Contains(association.RelatedConcept))
                            {
                                var newNode = new AssociationNode(
                                    association.RelatedConcept,
                                    currentNode.Depth + 1);

                                associationGraph.AddNode(newNode);
                                associationGraph.AddEdge(currentNode, newNode, association.Strength);

                                recallQueue.Enqueue(newNode);
                                visitedConcepts.Add(association.RelatedConcept);
                            }
                        }
                    }

                    // Retrieve memories for associated concepts;
                    var memoryTasks = associationGraph.Nodes;
                        .Select(node => RecallByContextAsync(
                            new RecallContext { Keywords = new[] { node.Concept } },
                            new RecallOptions { MaxResults = 3 }))
                        .ToList();

                    var memoryResults = await Task.WhenAll(memoryTasks);

                    // Map memories to association graph;
                    for (int i = 0; i < associationGraph.Nodes.Count; i++)
                    {
                        associationGraph.Nodes[i].RelatedMemories = memoryResults[i].ToList();
                    }

                    var result = new AssociativeRecallResult;
                    {
                        SeedConcept = seedConcept,
                        AssociationGraph = associationGraph,
                        TotalNodes = associationGraph.Nodes.Count,
                        TotalEdges = associationGraph.Edges.Count;
                    };

                    _logger.LogInformation("Associative recall generated graph with {Nodes} nodes and {Edges} edges",
                        result.TotalNodes, result.TotalEdges);

                    return result;
                }
                catch (Exception ex)
                {
                    _metrics.IncrementAssociativeRecallErrors();
                    _logger.LogError(ex, "Error in associative recall for concept {Concept}", seedConcept);
                    throw new MemoryRecallException($"Associative recall failed for concept: {seedConcept}", ex);
                }
            }
        }

        /// <summary>
        /// Recalls episodic memories (events with temporal context)
        /// </summary>
        public async Task<IEnumerable<EpisodicMemory>> RecallEpisodicAsync(
            DateTime? startTime = null,
            DateTime? endTime = null,
            string location = null,
            string[] participants = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("MemoryRecall.RecallEpisodic"))
            {
                try
                {
                    _metrics.IncrementEpisodicRecallAttempts();

                    var query = new EpisodicMemoryQuery;
                    {
                        StartTime = startTime,
                        EndTime = endTime,
                        Location = location,
                        Participants = participants?.ToList() ?? new List<string>()
                    };

                    var results = await _longTermMemory.QueryEpisodicMemoriesAsync(query);

                    // Apply temporal recency bias;
                    var rankedResults = ApplyTemporalRecencyBias(results).ToList();

                    _logger.LogInformation("Episodic recall returned {Count} memories", rankedResults.Count);
                    return rankedResults;
                }
                catch (Exception ex)
                {
                    _metrics.IncrementEpisodicRecallErrors();
                    _logger.LogError(ex, "Error in episodic recall");
                    throw new MemoryRecallException("Episodic recall failed", ex);
                }
            }
        }

        /// <summary>
        /// Recalls procedural memories (skills and how-to knowledge)
        /// </summary>
        public async Task<ProceduralMemoryRecallResult> RecallProceduralAsync(
            string skillName,
            ComplexityLevel complexity = ComplexityLevel.Intermediate)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("MemoryRecall.RecallProcedural"))
            {
                try
                {
                    _metrics.IncrementProceduralRecallAttempts();

                    var procedures = await _longTermMemory.RetrieveProceduresAsync(skillName, complexity);

                    if (!procedures.Any())
                    {
                        _logger.LogWarning("No procedural memories found for skill {SkillName}", skillName);
                        return ProceduralMemoryRecallResult.Empty(skillName);
                    }

                    // Select the most relevant procedure based on success rate and recency;
                    var bestProcedure = procedures;
                        .OrderByDescending(p => p.SuccessRate)
                        .ThenByDescending(p => p.LastUsed)
                        .First();

                    // Retrieve related declarative knowledge;
                    var relatedKnowledge = await RecallByContextAsync(
                        new RecallContext { Keywords = bestProcedure.RelatedConcepts },
                        new RecallOptions { MaxResults = 5 });

                    var result = new ProceduralMemoryRecallResult;
                    {
                        SkillName = skillName,
                        PrimaryProcedure = bestProcedure,
                        AlternativeProcedures = procedures.Where(p => p.Id != bestProcedure.Id).ToList(),
                        RelatedKnowledge = relatedKnowledge.ToList(),
                        TotalProceduresFound = procedures.Count;
                    };

                    // Update procedure usage statistics;
                    await UpdateProcedureUsageAsync(bestProcedure);

                    _logger.LogInformation("Procedural recall found {Count} procedures for {SkillName}",
                        result.TotalProceduresFound, skillName);

                    return result;
                }
                catch (Exception ex)
                {
                    _metrics.IncrementProceduralRecallErrors();
                    _logger.LogError(ex, "Error recalling procedural memory for {SkillName}", skillName);
                    throw new MemoryRecallException($"Procedural recall failed for skill: {skillName}", ex);
                }
            }
        }

        /// <summary>
        /// Performs fuzzy recall based on partial or imperfect information;
        /// </summary>
        public async Task<IEnumerable<FuzzyRecallResult>> RecallFuzzyAsync(
            string partialContent,
            double similarityThreshold = 0.7)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("MemoryRecall.RecallFuzzy"))
            {
                try
                {
                    _metrics.IncrementFuzzyRecallAttempts();

                    var fuzzyMatcher = new FuzzyMemoryMatcher(similarityThreshold);
                    var allMemories = await _longTermMemory.GetAllMemoriesAsync();

                    var matches = new List<FuzzyRecallResult>();

                    foreach (var memory in allMemories)
                    {
                        var similarity = fuzzyMatcher.CalculateSimilarity(partialContent, memory.Content);
                        if (similarity >= similarityThreshold)
                        {
                            matches.Add(new FuzzyRecallResult;
                            {
                                Memory = memory,
                                SimilarityScore = similarity,
                                MatchedFragments = fuzzyMatcher.GetMatchingFragments(partialContent, memory.Content)
                            });
                        }
                    }

                    var rankedMatches = matches;
                        .OrderByDescending(m => m.SimilarityScore)
                        .ThenByDescending(m => m.Memory.Strength)
                        .ToList();

                    _logger.LogDebug("Fuzzy recall found {Count} matches for partial content", rankedMatches.Count);
                    return rankedMatches;
                }
                catch (Exception ex)
                {
                    _metrics.IncrementFuzzyRecallErrors();
                    _logger.LogError(ex, "Error in fuzzy recall for content: {Content}", partialContent);
                    throw new MemoryRecallException("Fuzzy recall failed", ex);
                }
            }
        }

        /// <summary>
        /// Gets recall performance metrics;
        /// </summary>
        public RecallMetrics GetMetrics() => _metrics.Clone();

        /// <summary>
        /// Clears the recall cache;
        /// </summary>
        public void ClearCache()
        {
            _recallCache.Clear();
            _logger.LogInformation("Recall cache cleared");
        }

        #region Private Methods;

        private void CacheMemoryItem(MemoryItem memory)
        {
            if (memory != null && _config.EnableCaching)
            {
                _recallCache.Set(memory.Id.ToString(), memory, _config.CacheExpiration);
            }
        }

        private async Task UpdateMemoryStrengthAsync(MemoryItem memory, RecallContext context)
        {
            if (memory.StorageType == MemoryStorageType.LongTerm)
            {
                // Increase strength based on recall frequency and context relevance;
                var strengthIncrease = CalculateStrengthIncrease(memory, context);
                await _longTermMemory.UpdateMemoryStrengthAsync(memory.Id, strengthIncrease);
            }
        }

        private double CalculateStrengthIncrease(MemoryItem memory, RecallContext context)
        {
            double baseIncrease = 0.1;

            // Context relevance bonus;
            if (context != null && context.Keywords != null)
            {
                var keywordMatches = context.Keywords.Count(k =>
                    memory.Tags?.Contains(k, StringComparer.OrdinalIgnoreCase) == true ||
                    memory.Content.Contains(k, StringComparison.OrdinalIgnoreCase));

                baseIncrease += (keywordMatches * 0.05);
            }

            // Temporal decay compensation;
            var daysSinceLastRecall = (DateTime.UtcNow - memory.LastAccessed).TotalDays;
            if (daysSinceLastRecall > 30)
            {
                baseIncrease *= 1.5; // Boost for rarely accessed memories;
            }

            return Math.Min(baseIncrease, 0.3); // Cap the increase;
        }

        private List<SearchQuery> GenerateSearchQueries(RecallContext context, RecallOptions options)
        {
            var queries = new List<SearchQuery>();

            // Primary keyword query;
            if (context.Keywords != null && context.Keywords.Any())
            {
                queries.Add(new SearchQuery;
                {
                    Type = SearchQueryType.Keyword,
                    Terms = context.Keywords,
                    Weight = 1.0;
                });
            }

            // Semantic expansion query;
            if (options.UseSemanticExpansion && context.Keywords != null)
            {
                queries.Add(new SearchQuery;
                {
                    Type = SearchQueryType.Semantic,
                    Terms = context.Keywords,
                    Weight = 0.7;
                });
            }

            // Temporal context query;
            if (context.TimeRange != null)
            {
                queries.Add(new SearchQuery;
                {
                    Type = SearchQueryType.Temporal,
                    StartTime = context.TimeRange.Start,
                    EndTime = context.TimeRange.End,
                    Weight = 0.5;
                });
            }

            // Spatial context query;
            if (!string.IsNullOrEmpty(context.Location))
            {
                queries.Add(new SearchQuery;
                {
                    Type = SearchQueryType.Spatial,
                    Location = context.Location,
                    Weight = 0.4;
                });
            }

            return queries;
        }

        private async Task<IEnumerable<MemoryRecallResult>> SearchShortTermMemoryAsync(
            List<SearchQuery> queries,
            RecallOptions options)
        {
            var results = new List<MemoryRecallResult>();

            foreach (var memory in await _shortTermMemory.GetAllAsync())
            {
                var relevanceScore = CalculateRelevanceScore(memory, queries);
                if (relevanceScore >= options.MinRelevanceScore)
                {
                    results.Add(MemoryRecallResult.Success(
                        memory,
                        relevanceScore: relevanceScore,
                        recallSource: RecallSource.ShortTerm));
                }
            }

            return results.OrderByDescending(r => r.RelevanceScore);
        }

        private async Task<IEnumerable<MemoryRecallResult>> SearchLongTermMemoryAsync(
            List<SearchQuery> queries,
            RecallOptions options)
        {
            var searchResults = await _retrievalSystem.SearchAsync(queries, options);

            return searchResults.Select(result => MemoryRecallResult.Success(
                result.Memory,
                relevanceScore: result.RelevanceScore,
                recallSource: RecallSource.LongTerm));
        }

        private double CalculateRelevanceScore(MemoryItem memory, List<SearchQuery> queries)
        {
            double totalScore = 0.0;
            double totalWeight = 0.0;

            foreach (var query in queries)
            {
                var queryScore = CalculateQueryScore(memory, query);
                totalScore += queryScore * query.Weight;
                totalWeight += query.Weight;
            }

            return totalWeight > 0 ? totalScore / totalWeight : 0;
        }

        private double CalculateQueryScore(MemoryItem memory, SearchQuery query)
        {
            return query.Type switch;
            {
                SearchQueryType.Keyword => CalculateKeywordScore(memory, query.Terms),
                SearchQueryType.Semantic => CalculateSemanticScore(memory, query.Terms),
                SearchQueryType.Temporal => CalculateTemporalScore(memory, query.StartTime, query.EndTime),
                SearchQueryType.Spatial => CalculateSpatialScore(memory, query.Location),
                _ => 0.0;
            };
        }

        private double CalculateKeywordScore(MemoryItem memory, string[] terms)
        {
            if (terms == null || !terms.Any()) return 0.0;

            int matches = 0;
            foreach (var term in terms)
            {
                if (memory.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    memory.Tags?.Contains(term, StringComparer.OrdinalIgnoreCase) == true ||
                    memory.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
                {
                    matches++;
                }
            }

            return (double)matches / terms.Length;
        }

        private double CalculateSemanticScore(MemoryItem memory, string[] terms)
        {
            // This would integrate with semantic analysis engine;
            // For now, use keyword matching as fallback;
            return CalculateKeywordScore(memory, terms) * 0.8;
        }

        private double CalculateTemporalScore(MemoryItem memory, DateTime? startTime, DateTime? endTime)
        {
            if (!startTime.HasValue && !endTime.HasValue) return 0.5;

            var memoryTime = memory.Timestamp;
            bool isInRange = true;

            if (startTime.HasValue && memoryTime < startTime.Value)
                isInRange = false;
            if (endTime.HasValue && memoryTime > endTime.Value)
                isInRange = false;

            return isInRange ? 1.0 : 0.0;
        }

        private double CalculateSpatialScore(MemoryItem memory, string location)
        {
            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(memory.Location))
                return 0.5;

            return memory.Location.Equals(location, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
        }

        private IEnumerable<MemoryRecallResult> MergeAndRankResults(
            IEnumerable<MemoryRecallResult> results,
            RecallContext context,
            RecallOptions options)
        {
            // Group by memory ID to remove duplicates;
            var uniqueResults = results;
                .GroupBy(r => r.Memory?.Id ?? Guid.Empty)
                .Select(g => g.OrderByDescending(r => r.RelevanceScore).First())
                .Where(r => r.IsSuccess)
                .ToList();

            // Apply ranking algorithm;
            var rankedResults = uniqueResults;
                .Select(r => ApplyRankingFactors(r, context))
                .OrderByDescending(r => r.FinalScore)
                .ToList();

            return rankedResults;
        }

        private RankedRecallResult ApplyRankingFactors(MemoryRecallResult result, RecallContext context)
        {
            var memory = result.Memory;
            double finalScore = result.RelevanceScore;

            // Recency factor (more recent memories get higher score)
            var daysOld = (DateTime.UtcNow - memory.Timestamp).TotalDays;
            var recencyFactor = Math.Exp(-daysOld / 30.0); // 30-day half-life;
            finalScore *= (0.3 + 0.7 * recencyFactor);

            // Strength factor (stronger memories get higher score)
            finalScore *= (0.5 + 0.5 * memory.Strength);

            // Contextual alignment factor;
            if (context.EmotionalContext != null && memory.EmotionalWeight.HasValue)
            {
                var emotionalAlignment = CalculateEmotionalAlignment(
                    context.EmotionalContext,
                    memory.EmotionalWeight.Value);
                finalScore *= (0.8 + 0.2 * emotionalAlignment);
            }

            // Source weight (long-term vs short-term)
            var sourceWeight = result.RecallSource == RecallSource.LongTerm ? 1.1 : 1.0;
            finalScore *= sourceWeight;

            return new RankedRecallResult;
            {
                Memory = memory,
                OriginalScore = result.RelevanceScore,
                FinalScore = finalScore,
                RecallSource = result.RecallSource,
                RankingFactors = new RankingFactors;
                {
                    RecencyFactor = recencyFactor,
                    StrengthFactor = memory.Strength,
                    SourceWeight = sourceWeight;
                }
            };
        }

        private double CalculateEmotionalAlignment(EmotionalContext context, double memoryEmotionalWeight)
        {
            // Simplified emotional alignment calculation;
            // In production, this would use more sophisticated emotion analysis;
            return 1.0 - Math.Abs(context.Valence - memoryEmotionalWeight);
        }

        private IEnumerable<EpisodicMemory> ApplyTemporalRecencyBias(IEnumerable<EpisodicMemory> memories)
        {
            return memories;
                .OrderByDescending(m => m.Timestamp)
                .ThenByDescending(m => m.Vividness)
                .ToList();
        }

        private async Task UpdateProcedureUsageAsync(Procedure procedure)
        {
            procedure.UsageCount++;
            procedure.LastUsed = DateTime.UtcNow;

            // Update success rate based on recent usage;
            // This would integrate with actual success tracking;
            await Task.CompletedTask;
        }

        #endregion;

        #region IDisposable Support;
        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _recallCache?.Dispose();
                    _metrics?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IMemoryRecall : IDisposable
    {
        Task<MemoryRecallResult> RecallByIdAsync(Guid memoryId, RecallContext context = null);
        Task<IEnumerable<MemoryRecallResult>> RecallByContextAsync(RecallContext context, RecallOptions options = null);
        Task<AssociativeRecallResult> RecallAssociativelyAsync(string seedConcept, int maxDepth = 3, int maxAssociations = 10);
        Task<IEnumerable<EpisodicMemory>> RecallEpisodicAsync(DateTime? startTime = null, DateTime? endTime = null, string location = null, string[] participants = null);
        Task<ProceduralMemoryRecallResult> RecallProceduralAsync(string skillName, ComplexityLevel complexity = ComplexityLevel.Intermediate);
        Task<IEnumerable<FuzzyRecallResult>> RecallFuzzyAsync(string partialContent, double similarityThreshold = 0.7);
        RecallMetrics GetMetrics();
        void ClearCache();
    }

    public class MemoryRecallConfig;
    {
        public static MemoryRecallConfig Default => new MemoryRecallConfig;
        {
            CacheSize = 1000,
            CacheExpiration = TimeSpan.FromMinutes(30),
            EnableCaching = true,
            DefaultMaxResults = 20,
            MinRelevanceScore = 0.3;
        };

        public int CacheSize { get; set; }
        public TimeSpan CacheExpiration { get; set; }
        public bool EnableCaching { get; set; }
        public int DefaultMaxResults { get; set; }
        public double MinRelevanceScore { get; set; }
    }

    public class RecallContext;
    {
        public string[] Keywords { get; set; }
        public TimeRange TimeRange { get; set; }
        public string Location { get; set; }
        public EmotionalContext EmotionalContext { get; set; }
        public string[] AssociatedConcepts { get; set; }
        public int Priority { get; set; } = 1;
    }

    public class RecallOptions;
    {
        public static RecallOptions Default => new RecallOptions;
        {
            MaxResults = 20,
            MinRelevanceScore = 0.3,
            SearchShortTermMemory = true,
            SearchLongTermMemory = true,
            UseSemanticExpansion = true,
            IncludeEpisodic = true,
            IncludeDeclarative = true,
            IncludeProcedural = true;
        };

        public int MaxResults { get; set; }
        public double MinRelevanceScore { get; set; }
        public bool SearchShortTermMemory { get; set; }
        public bool SearchLongTermMemory { get; set; }
        public bool UseSemanticExpansion { get; set; }
        public bool IncludeEpisodic { get; set; }
        public bool IncludeDeclarative { get; set; }
        public bool IncludeProcedural { get; set; }
    }

    public class MemoryRecallResult;
    {
        public bool IsSuccess { get; set; }
        public MemoryItem Memory { get; set; }
        public double RelevanceScore { get; set; }
        public RecallSource RecallSource { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime RecallTime { get; set; }

        public static MemoryRecallResult Success(MemoryItem memory, double relevanceScore = 1.0, RecallSource recallSource = RecallSource.Unknown)
        {
            return new MemoryRecallResult;
            {
                IsSuccess = true,
                Memory = memory,
                RelevanceScore = relevanceScore,
                RecallSource = recallSource,
                RecallTime = DateTime.UtcNow;
            };
        }

        public static MemoryRecallResult NotFound(string message)
        {
            return new MemoryRecallResult;
            {
                IsSuccess = false,
                ErrorMessage = message,
                RecallTime = DateTime.UtcNow;
            };
        }

        public static MemoryRecallResult Error(string message)
        {
            return new MemoryRecallResult;
            {
                IsSuccess = false,
                ErrorMessage = message,
                RecallTime = DateTime.UtcNow;
            };
        }
    }

    public enum RecallSource;
    {
        Unknown,
        Cache,
        ShortTerm,
        LongTerm,
        External;
    }

    public class AssociativeRecallResult;
    {
        public string SeedConcept { get; set; }
        public AssociationGraph AssociationGraph { get; set; }
        public int TotalNodes { get; set; }
        public int TotalEdges { get; set; }
        public DateTime RecallTime { get; set; } = DateTime.UtcNow;
    }

    public class ProceduralMemoryRecallResult;
    {
        public string SkillName { get; set; }
        public Procedure PrimaryProcedure { get; set; }
        public List<Procedure> AlternativeProcedures { get; set; }
        public List<MemoryRecallResult> RelatedKnowledge { get; set; }
        public int TotalProceduresFound { get; set; }
        public bool HasProcedures => PrimaryProcedure != null;

        public static ProceduralMemoryRecallResult Empty(string skillName)
        {
            return new ProceduralMemoryRecallResult;
            {
                SkillName = skillName,
                AlternativeProcedures = new List<Procedure>(),
                RelatedKnowledge = new List<MemoryRecallResult>(),
                TotalProceduresFound = 0;
            };
        }
    }

    public class FuzzyRecallResult;
    {
        public MemoryItem Memory { get; set; }
        public double SimilarityScore { get; set; }
        public List<string> MatchedFragments { get; set; }
    }

    public class RankedRecallResult : MemoryRecallResult;
    {
        public double FinalScore { get; set; }
        public RankingFactors RankingFactors { get; set; }
    }

    public class RankingFactors;
    {
        public double RecencyFactor { get; set; }
        public double StrengthFactor { get; set; }
        public double SourceWeight { get; set; }
        public double ContextualAlignment { get; set; }
    }

    [Serializable]
    public class MemoryRecallException : Exception
    {
        public MemoryRecallException() { }
        public MemoryRecallException(string message) : base(message) { }
        public MemoryRecallException(string message, Exception inner) : base(message, inner) { }
        protected MemoryRecallException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    #endregion;
}
