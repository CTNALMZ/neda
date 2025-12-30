using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;

namespace NEDA.Brain.KnowledgeBase.CreativePatterns;
{
    /// <summary>
    /// Central repository for design patterns, creative templates, and innovation patterns;
    /// used across NEDA's AI systems for content generation and problem-solving.
    /// </summary>
    public class PatternLibrary : IPatternLibrary, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IConfigurationManager _configManager;
        private readonly PatternCache _cache;
        private readonly PatternIndex _index;
        private bool _disposed = false;
        private readonly object _syncLock = new object();

        // Pattern categories and collections;
        private readonly Dictionary<string, DesignPattern> _designPatterns;
        private readonly Dictionary<string, CreativeTemplate> _creativeTemplates;
        private readonly Dictionary<string, InnovationPattern> _innovationPatterns;
        private readonly Dictionary<string, DomainPattern> _domainPatterns;

        // Pattern relationships and hierarchies;
        private readonly PatternGraph _patternGraph;
        private readonly PatternTaxonomy _taxonomy;

        // Statistics and metrics;
        private PatternStatistics _statistics;
        private readonly List<PatternUsage> _usageHistory;

        /// <summary>
        /// Initializes a new instance of PatternLibrary;
        /// </summary>
        public PatternLibrary(
            ILogger logger,
            IConfigurationManager configManager,
            PatternLibraryConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));

            configuration ??= PatternLibraryConfiguration.Default;

            // Initialize collections;
            _designPatterns = new Dictionary<string, DesignPattern>();
            _creativeTemplates = new Dictionary<string, CreativeTemplate>();
            _innovationPatterns = new Dictionary<string, InnovationPattern>();
            _domainPatterns = new Dictionary<string, DomainPattern>();

            // Initialize structures;
            _patternGraph = new PatternGraph();
            _taxonomy = new PatternTaxonomy();
            _cache = new PatternCache(configuration.CacheConfiguration);
            _index = new PatternIndex();
            _usageHistory = new List<PatternUsage>();
            _statistics = new PatternStatistics();

            // Load patterns from configuration and default sources;
            LoadDefaultPatterns();
            LoadDomainSpecificPatterns();

            _logger.LogInformation($"PatternLibrary initialized with {GetTotalPatternCount()} patterns");
        }

        /// <summary>
        /// Loads default design patterns and creative templates;
        /// </summary>
        private void LoadDefaultPatterns()
        {
            try
            {
                // Load design patterns (Gang of Four, architectural, etc.)
                LoadGangOfFourPatterns();
                LoadArchitecturalPatterns();
                LoadGameDesignPatterns();
                LoadUIPatterns();

                // Load creative templates;
                LoadStoryTemplates();
                LoadCharacterArchetypes();
                LoadWorldBuildingTemplates();
                LoadVisualCompositionTemplates();

                // Load innovation patterns;
                LoadInnovationPatterns();
                LoadProblemSolvingPatterns();
                LoadCreativeThinkingPatterns();

                _logger.LogInformation($"Loaded {_designPatterns.Count} design patterns, " +
                                      $"{_creativeTemplates.Count} creative templates, " +
                                      $"{_innovationPatterns.Count} innovation patterns");

                // Build relationships and indices;
                BuildPatternRelationships();
                RebuildIndices();

                _logger.LogDebug("Pattern relationships and indices built");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load default patterns: {ex.Message}");
                throw new PatternLibraryException("Failed to initialize pattern library", ex);
            }
        }

        /// <summary>
        /// Loads Gang of Four design patterns;
        /// </summary>
        private void LoadGangOfFourPatterns()
        {
            // Creational Patterns;
            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_singleton",
                Name = "Singleton",
                Category = PatternCategory.Creational,
                Description = "Ensures a class has only one instance and provides a global point of access to it.",
                Applicability = "Use when exactly one instance of a class is needed to coordinate actions across the system.",
                Structure = new PatternStructure;
                {
                    Elements = new List<string> { "Singleton", "Instance", "GetInstance()" },
                    Relationships = new Dictionary<string, List<string>>
                    {
                        { "Singleton", new List<string> { "returns", "Instance" } }
                    }
                },
                Examples = new List<PatternExample>
                {
                    new PatternExample;
                    {
                        Context = "Game Manager in Unity",
                        Code = "public class GameManager : MonoBehaviour\n{\n    private static GameManager _instance;\n    \n    public static GameManager Instance\n    {\n        get\n        {\n            if (_instance == null)\n                _instance = FindObjectOfType<GameManager>();\n            return _instance;\n        }\n    }\n}",
                        Language = "C#"
                    }
                },
                Tags = new List<string> { "creational", "global", "instance", "coordination" },
                Complexity = PatternComplexity.Low,
                UsageCount = 0,
                LastUsed = null;
            });

            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_factory",
                Name = "Factory Method",
                Category = PatternCategory.Creational,
                Description = "Defines an interface for creating an object, but lets subclasses decide which class to instantiate.",
                Applicability = "Use when a class can't anticipate the class of objects it must create.",
                Tags = new List<string> { "creational", "object-creation", "polymorphism" }
            });

            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_observer",
                Name = "Observer",
                Category = PatternCategory.Behavioral,
                Description = "Defines a one-to-many dependency between objects so that when one object changes state, all its dependents are notified and updated automatically.",
                Applicability = "Use when a change to one object requires changing others, and you don't know how many objects need to be changed.",
                Tags = new List<string> { "behavioral", "event", "notification", "pub-sub" }
            });

            // Add more GoF patterns...
        }

        /// <summary>
        /// Loads architectural patterns;
        /// </summary>
        private void LoadArchitecturalPatterns()
        {
            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_mvc",
                Name = "Model-View-Controller",
                Category = PatternCategory.Architectural,
                Description = "Separates application into three main components: Model (data), View (UI), and Controller (logic).",
                Applicability = "Use for applications with complex user interfaces that need separation of concerns.",
                Tags = new List<string> { "architectural", "separation", "ui", "data", "logic" }
            });

            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_repository",
                Name = "Repository",
                Category = PatternCategory.Architectural,
                Description = "Mediates between the domain and data mapping layers, acting like an in-memory domain object collection.",
                Applicability = "Use to decouple business logic from data access logic.",
                Tags = new List<string> { "architectural", "data-access", "abstraction", "persistence" }
            });

            // Add more architectural patterns...
        }

        /// <summary>
        /// Loads game design patterns;
        /// </summary>
        private void LoadGameDesignPatterns()
        {
            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_game_state",
                Name = "Game State",
                Category = PatternCategory.GameDesign,
                Description = "Manages different states of a game (menu, playing, paused, game over) with clear transitions.",
                Applicability = "Use in games with distinct modes or states that need separate behavior.",
                Tags = new List<string> { "game", "state", "transition", "fsm" }
            });

            AddDesignPattern(new DesignPattern;
            {
                Id = "pattern_object_pool",
                Name = "Object Pool",
                Category = PatternCategory.GameDesign,
                Description = "Reuses objects from a pool rather than creating and destroying them repeatedly.",
                Applicability = "Use for frequently created/destroyed objects like bullets, particles, or enemies.",
                Tags = new List<string> { "game", "performance", "memory", "optimization" }
            });

            // Add more game design patterns...
        }

        /// <summary>
        /// Loads story templates and narrative patterns;
        /// </summary>
        private void LoadStoryTemplates()
        {
            AddCreativeTemplate(new CreativeTemplate;
            {
                Id = "template_heros_journey",
                Name = "The Hero's Journey",
                Category = TemplateCategory.Narrative,
                Description = "A classic narrative pattern involving a hero who goes on an adventure, faces crisis, and returns transformed.",
                Structure = new TemplateStructure;
                {
                    Stages = new List<string>
                    {
                        "Ordinary World",
                        "Call to Adventure",
                        "Refusal of the Call",
                        "Meeting the Mentor",
                        "Crossing the Threshold",
                        "Tests, Allies, Enemies",
                        "Approach to the Inmost Cave",
                        "Ordeal",
                        "Reward",
                        "The Road Back",
                        "Resurrection",
                        "Return with the Elixir"
                    }
                },
                Examples = new List<string>
                {
                    "Star Wars: A New Hope",
                    "The Lord of the Rings",
                    "Harry Potter"
                },
                Tags = new List<string> { "narrative", "hero", "journey", "transformation" }
            });

            AddCreativeTemplate(new CreativeTemplate;
            {
                Id = "template_three_act",
                Name = "Three-Act Structure",
                Category = TemplateCategory.Narrative,
                Description = "Divides story into Setup, Confrontation, and Resolution.",
                Tags = new List<string> { "narrative", "structure", "screenwriting" }
            });
        }

        /// <summary>
        /// Loads character archetypes;
        /// </summary>
        private void LoadCharacterArchetypes()
        {
            AddCreativeTemplate(new CreativeTemplate;
            {
                Id = "template_hero",
                Name = "The Hero",
                Category = TemplateCategory.Character,
                Description = "Protagonist who undertakes a journey and undergoes transformation.",
                Attributes = new Dictionary<string, object>
                {
                    { "Motivation", "To achieve a goal or complete a quest" },
                    { "Flaw", "Hubris, fear, or ignorance" },
                    { "Strength", "Courage, determination, integrity" },
                    { "Arc", "From ordinary to extraordinary" }
                },
                Tags = new List<string> { "character", "protagonist", "hero", "journey" }
            });

            // Add more character archetypes...
        }

        /// <summary>
        /// Loads innovation patterns;
        /// </summary>
        private void LoadInnovationPatterns()
        {
            AddInnovationPattern(new InnovationPattern;
            {
                Id = "innovation_combination",
                Name = "Combination Innovation",
                Description = "Creating innovation by combining existing elements in new ways.",
                Process = new List<string>
                {
                    "Identify existing elements or technologies",
                    "Analyze their core functionalities",
                    "Explore potential combinations",
                    "Test combined functionality",
                    "Refine the integration"
                },
                Examples = new List<string>
                {
                    "Smartphone (phone + computer + camera)",
                    "Electric car (car + battery technology)"
                },
                Tags = new List<string> { "innovation", "combination", "integration" }
            });

            AddInnovationPattern(new InnovationPattern;
            {
                Id = "innovation_disruption",
                Name = "Disruptive Innovation",
                Description = "Creating new market value by disrupting existing markets.",
                Process = new List<string>
                {
                    "Identify overserved or underserved markets",
                    "Create simpler, more affordable solution",
                    "Start at bottom of market",
                    "Move upmarket over time"
                },
                Tags = new List<string> { "innovation", "disruption", "market" }
            });
        }

        /// <summary>
        /// Loads domain-specific patterns;
        /// </summary>
        private void LoadDomainSpecificPatterns()
        {
            // This would load patterns from external sources or databases;
            // For now, we'll initialize with empty domain patterns;
            _logger.LogDebug("Domain-specific patterns loading configured");
        }

        /// <summary>
        /// Builds relationships between patterns;
        /// </summary>
        private void BuildPatternRelationships()
        {
            // Build hierarchical relationships;
            _taxonomy.AddCategory("Design Patterns", new List<string>
            {
                PatternCategory.Creational.ToString(),
                PatternCategory.Structural.ToString(),
                PatternCategory.Behavioral.ToString(),
                PatternCategory.Architectural.ToString(),
                PatternCategory.GameDesign.ToString()
            });

            _taxonomy.AddCategory("Creative Templates", new List<string>
            {
                TemplateCategory.Narrative.ToString(),
                TemplateCategory.Character.ToString(),
                TemplateCategory.World.ToString(),
                TemplateCategory.Visual.ToString()
            });

            // Build graph relationships;
            // Example: Singleton is related to Factory, both are creational;
            _patternGraph.AddNode("pattern_singleton");
            _patternGraph.AddNode("pattern_factory");
            _patternGraph.AddEdge("pattern_singleton", "pattern_factory",
                new PatternRelationship { Type = RelationshipType.Related, Strength = 0.7 });

            // Add more relationships...
        }

        /// <summary>
        /// Rebuilds search indices;
        /// </summary>
        private void RebuildIndices()
        {
            _index.Clear();

            // Index all patterns;
            foreach (var pattern in _designPatterns.Values)
            {
                _index.AddPattern(pattern);
            }

            foreach (var template in _creativeTemplates.Values)
            {
                _index.AddTemplate(template);
            }

            foreach (var innovation in _innovationPatterns.Values)
            {
                _index.AddInnovation(innovation);
            }

            _logger.LogDebug($"Index rebuilt with {_index.GetTotalCount()} entries");
        }

        /// <summary>
        /// Searches for patterns based on query and criteria;
        /// </summary>
        public PatternSearchResult SearchPatterns(PatternSearchQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogDebug($"Searching patterns with query: {query.Query}");

                    // Check cache first;
                    var cacheKey = query.GetCacheKey();
                    if (_cache.TryGetSearchResult(cacheKey, out var cachedResult))
                    {
                        _logger.LogDebug($"Cache hit for search query: {query.Query}");
                        return cachedResult;
                    }

                    var results = new List<PatternMatch>();

                    // Search across all pattern types;
                    var designMatches = SearchDesignPatterns(query);
                    var templateMatches = SearchCreativeTemplates(query);
                    var innovationMatches = SearchInnovationPatterns(query);

                    results.AddRange(designMatches);
                    results.AddRange(templateMatches);
                    results.AddRange(innovationMatches);

                    // Apply filters;
                    if (query.Filters != null)
                    {
                        results = ApplyFilters(results, query.Filters);
                    }

                    // Sort by relevance;
                    results = results;
                        .OrderByDescending(r => r.RelevanceScore)
                        .ThenByDescending(r => r.Pattern.GetPopularityScore())
                        .ToList();

                    // Apply pagination;
                    var totalCount = results.Count;
                    var paginatedResults = results;
                        .Skip(query.Skip)
                        .Take(query.Take)
                        .ToList();

                    var searchResult = new PatternSearchResult;
                    {
                        Query = query,
                        Matches = paginatedResults,
                        TotalCount = totalCount,
                        Page = query.Skip / query.Take + 1,
                        TotalPages = (int)Math.Ceiling(totalCount / (double)query.Take),
                        SearchDuration = TimeSpan.Zero, // Would be calculated in real implementation;
                        SearchId = Guid.NewGuid().ToString()
                    };

                    // Update usage statistics;
                    UpdateSearchStatistics(query, searchResult);

                    // Cache the result;
                    _cache.CacheSearchResult(cacheKey, searchResult);

                    _logger.LogInformation($"Pattern search completed: {totalCount} total matches");

                    return searchResult;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Pattern search failed: {ex.Message}");
                    throw new PatternSearchException($"Search failed for query: {query.Query}", ex);
                }
            }
        }

        /// <summary>
        /// Gets a specific design pattern by ID;
        /// </summary>
        public DesignPattern GetDesignPattern(string patternId)
        {
            if (string.IsNullOrWhiteSpace(patternId))
                throw new ArgumentException("Pattern ID cannot be null or empty", nameof(patternId));

            lock (_syncLock)
            {
                if (_designPatterns.TryGetValue(patternId, out var pattern))
                {
                    UpdatePatternUsage(patternId, PatternType.Design);
                    return pattern;
                }

                throw new PatternNotFoundException($"Design pattern not found: {patternId}");
            }
        }

        /// <summary>
        /// Gets a creative template by ID;
        /// </summary>
        public CreativeTemplate GetCreativeTemplate(string templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            lock (_syncLock)
            {
                if (_creativeTemplates.TryGetValue(templateId, out var template))
                {
                    UpdatePatternUsage(templateId, PatternType.Creative);
                    return template;
                }

                throw new PatternNotFoundException($"Creative template not found: {templateId}");
            }
        }

        /// <summary>
        /// Gets related patterns for a given pattern;
        /// </summary>
        public List<RelatedPattern> GetRelatedPatterns(string patternId, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(patternId))
                throw new ArgumentException("Pattern ID cannot be null or empty", nameof(patternId));

            lock (_syncLock)
            {
                var related = _patternGraph.GetRelatedNodes(patternId, maxResults);

                var results = new List<RelatedPattern>();
                foreach (var node in related)
                {
                    var pattern = GetPatternById(node.NodeId);
                    if (pattern != null)
                    {
                        results.Add(new RelatedPattern;
                        {
                            Pattern = pattern,
                            Relationship = node.Relationship,
                            Relevance = node.Strength;
                        });
                    }
                }

                return results;
            }
        }

        /// <summary>
        /// Suggests patterns based on context and requirements;
        /// </summary>
        public List<PatternSuggestion> SuggestPatterns(SuggestionContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            lock (_syncLock)
            {
                try
                {
                    _logger.LogDebug($"Generating pattern suggestions for context: {context.Description}");

                    var suggestions = new List<PatternSuggestion>();

                    // Analyze context to determine requirements;
                    var requirements = AnalyzeContext(context);

                    // Match patterns to requirements;
                    var matchedPatterns = MatchPatternsToRequirements(requirements);

                    // Score and rank suggestions;
                    foreach (var match in matchedPatterns)
                    {
                        var score = CalculateSuggestionScore(match.Pattern, requirements, context);

                        suggestions.Add(new PatternSuggestion;
                        {
                            Pattern = match.Pattern,
                            MatchReason = match.Reason,
                            ConfidenceScore = score,
                            Applicability = CalculateApplicability(match.Pattern, context),
                            EstimatedImplementationComplexity = EstimateComplexity(match.Pattern, context)
                        });
                    }

                    // Sort by confidence score;
                    suggestions = suggestions;
                        .OrderByDescending(s => s.ConfidenceScore)
                        .Take(context.MaxSuggestions)
                        .ToList();

                    // Update suggestion statistics;
                    UpdateSuggestionStatistics(context, suggestions);

                    _logger.LogInformation($"Generated {suggestions.Count} pattern suggestions");

                    return suggestions;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Pattern suggestion failed: {ex.Message}");
                    throw new PatternSuggestionException($"Suggestion generation failed", ex);
                }
            }
        }

        /// <summary>
        /// Adds a new pattern to the library;
        /// </summary>
        public void AddPattern(IPattern pattern)
        {
            if (pattern == null)
                throw new ArgumentNullException(nameof(pattern));

            lock (_syncLock)
            {
                try
                {
                    switch (pattern)
                    {
                        case DesignPattern designPattern:
                            AddDesignPattern(designPattern);
                            break;

                        case CreativeTemplate creativeTemplate:
                            AddCreativeTemplate(creativeTemplate);
                            break;

                        case InnovationPattern innovationPattern:
                            AddInnovationPattern(innovationPattern);
                            break;

                        default:
                            throw new ArgumentException($"Unsupported pattern type: {pattern.GetType().Name}");
                    }

                    // Update indices and cache;
                    RebuildIndices();
                    _cache.Invalidate();

                    _logger.LogInformation($"Added new pattern: {pattern.Name} ({pattern.GetType().Name})");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to add pattern: {ex.Message}");
                    throw new PatternLibraryException($"Failed to add pattern: {pattern.Name}", ex);
                }
            }
        }

        /// <summary>
        /// Updates an existing pattern;
        /// </summary>
        public void UpdatePattern(string patternId, IPattern updatedPattern)
        {
            if (string.IsNullOrWhiteSpace(patternId))
                throw new ArgumentException("Pattern ID cannot be null or empty", nameof(patternId));

            if (updatedPattern == null)
                throw new ArgumentNullException(nameof(updatedPattern));

            lock (_syncLock)
            {
                // Find and update the pattern;
                // Implementation depends on pattern type;

                // Rebuild indices and clear cache;
                RebuildIndices();
                _cache.Invalidate();

                _logger.LogInformation($"Updated pattern: {patternId}");
            }
        }

        /// <summary>
        /// Exports patterns in specified format;
        /// </summary>
        public string ExportPatterns(ExportFormat format, ExportOptions options = null)
        {
            lock (_syncLock)
            {
                options ??= ExportOptions.Default;

                try
                {
                    _logger.LogDebug($"Exporting patterns in {format} format");

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
                    _logger.LogError($"Pattern export failed: {ex.Message}");
                    throw new PatternExportException($"Failed to export patterns in {format} format", ex);
                }
            }
        }

        /// <summary>
        /// Gets library statistics and metrics;
        /// </summary>
        public PatternStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                // Update statistics with current data;
                _statistics.TotalPatterns = GetTotalPatternCount();
                _statistics.DesignPatterns = _designPatterns.Count;
                _statistics.CreativeTemplates = _creativeTemplates.Count;
                _statistics.InnovationPatterns = _innovationPatterns.Count;
                _statistics.LastUpdated = DateTime.UtcNow;

                return _statistics.Clone();
            }
        }

        /// <summary>
        /// Gets pattern usage analytics;
        /// </summary>
        public PatternAnalytics GetAnalytics(TimePeriod period)
        {
            lock (_syncLock)
            {
                var analytics = new PatternAnalytics;
                {
                    Period = period,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Calculate usage statistics for the period;
                var periodStart = GetPeriodStart(period);
                var usageInPeriod = _usageHistory;
                    .Where(u => u.Timestamp >= periodStart)
                    .ToList();

                analytics.TotalUses = usageInPeriod.Count;
                analytics.MostUsedPatterns = GetMostUsedPatterns(usageInPeriod, 10);
                analytics.UsageByCategory = GetUsageByCategory(usageInPeriod);
                analytics.UsageTrend = CalculateUsageTrend(period);

                return analytics;
            }
        }

        #region Private Helper Methods;

        private void AddDesignPattern(DesignPattern pattern)
        {
            ValidatePattern(pattern);
            _designPatterns[pattern.Id] = pattern;
            _patternGraph.AddNode(pattern.Id);
        }

        private void AddCreativeTemplate(CreativeTemplate template)
        {
            ValidateTemplate(template);
            _creativeTemplates[template.Id] = template;
            _patternGraph.AddNode(template.Id);
        }

        private void AddInnovationPattern(InnovationPattern pattern)
        {
            ValidateInnovation(pattern);
            _innovationPatterns[pattern.Id] = pattern;
            _patternGraph.AddNode(pattern.Id);
        }

        private void ValidatePattern(DesignPattern pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern.Id))
                throw new ArgumentException("Pattern ID cannot be null or empty");

            if (string.IsNullOrWhiteSpace(pattern.Name))
                throw new ArgumentException("Pattern name cannot be null or empty");

            if (_designPatterns.ContainsKey(pattern.Id))
                throw new DuplicatePatternException($"Pattern with ID {pattern.Id} already exists");
        }

        private void ValidateTemplate(CreativeTemplate template)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
                throw new ArgumentException("Template ID cannot be null or empty");

            if (string.IsNullOrWhiteSpace(template.Name))
                throw new ArgumentException("Template name cannot be null or empty");

            if (_creativeTemplates.ContainsKey(template.Id))
                throw new DuplicatePatternException($"Template with ID {template.Id} already exists");
        }

        private void ValidateInnovation(InnovationPattern pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern.Id))
                throw new ArgumentException("Innovation pattern ID cannot be null or empty");

            if (string.IsNullOrWhiteSpace(pattern.Name))
                throw new ArgumentException("Innovation pattern name cannot be null or empty");

            if (_innovationPatterns.ContainsKey(pattern.Id))
                throw new DuplicatePatternException($"Innovation pattern with ID {pattern.Id} already exists");
        }

        private int GetTotalPatternCount()
        {
            return _designPatterns.Count + _creativeTemplates.Count + _innovationPatterns.Count;
        }

        private List<PatternMatch> SearchDesignPatterns(PatternSearchQuery query)
        {
            var matches = new List<PatternMatch>();

            foreach (var pattern in _designPatterns.Values)
            {
                var relevance = CalculateRelevance(pattern, query);
                if (relevance > 0)
                {
                    matches.Add(new PatternMatch;
                    {
                        Pattern = pattern,
                        RelevanceScore = relevance,
                        MatchType = MatchType.DesignPattern;
                    });
                }
            }

            return matches;
        }

        private List<PatternMatch> SearchCreativeTemplates(PatternSearchQuery query)
        {
            var matches = new List<PatternMatch>();

            foreach (var template in _creativeTemplates.Values)
            {
                var relevance = CalculateRelevance(template, query);
                if (relevance > 0)
                {
                    matches.Add(new PatternMatch;
                    {
                        Pattern = template,
                        RelevanceScore = relevance,
                        MatchType = MatchType.CreativeTemplate;
                    });
                }
            }

            return matches;
        }

        private List<PatternMatch> SearchInnovationPatterns(PatternSearchQuery query)
        {
            var matches = new List<PatternMatch>();

            foreach (var innovation in _innovationPatterns.Values)
            {
                var relevance = CalculateRelevance(innovation, query);
                if (relevance > 0)
                {
                    matches.Add(new PatternMatch;
                    {
                        Pattern = innovation,
                        RelevanceScore = relevance,
                        MatchType = MatchType.InnovationPattern;
                    });
                }
            }

            return matches;
        }

        private double CalculateRelevance(IPattern pattern, PatternSearchQuery query)
        {
            double relevance = 0.0;

            // Check name match;
            if (pattern.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
                relevance += 0.5;

            // Check description match;
            if (pattern.Description.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
                relevance += 0.3;

            // Check tag matches;
            if (pattern.Tags != null)
            {
                foreach (var tag in pattern.Tags)
                {
                    if (tag.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
                        relevance += 0.2;
                }
            }

            // Boost recent or popular patterns;
            relevance += pattern.GetPopularityScore() * 0.1;

            return Math.Min(relevance, 1.0);
        }

        private List<PatternMatch> ApplyFilters(List<PatternMatch> matches, PatternFilters filters)
        {
            var filtered = matches;

            if (filters.Categories != null && filters.Categories.Any())
            {
                filtered = filtered.Where(m =>
                    filters.Categories.Contains(m.Pattern.GetCategory())).ToList();
            }

            if (filters.MinComplexity.HasValue)
            {
                filtered = filtered.Where(m =>
                    m.Pattern.GetComplexity() >= filters.MinComplexity.Value).ToList();
            }

            if (filters.MaxComplexity.HasValue)
            {
                filtered = filtered.Where(m =>
                    m.Pattern.GetComplexity() <= filters.MaxComplexity.Value).ToList();
            }

            if (filters.Tags != null && filters.Tags.Any())
            {
                filtered = filtered.Where(m =>
                    m.Pattern.Tags != null &&
                    filters.Tags.Any(t => m.Pattern.Tags.Contains(t))).ToList();
            }

            return filtered;
        }

        private void UpdatePatternUsage(string patternId, PatternType type)
        {
            var usage = new PatternUsage;
            {
                PatternId = patternId,
                PatternType = type,
                Timestamp = DateTime.UtcNow;
            };

            _usageHistory.Add(usage);

            // Trim history if too large;
            if (_usageHistory.Count > 10000)
            {
                _usageHistory.RemoveRange(0, 1000);
            }
        }

        private void UpdateSearchStatistics(PatternSearchQuery query, PatternSearchResult result)
        {
            _statistics.TotalSearches++;
            _statistics.AverageResultsPerSearch =
                (_statistics.AverageResultsPerSearch * (_statistics.TotalSearches - 1) + result.TotalCount) / _statistics.TotalSearches;
        }

        private void UpdateSuggestionStatistics(SuggestionContext context, List<PatternSuggestion> suggestions)
        {
            _statistics.TotalSuggestions += suggestions.Count;
        }

        private List<Requirement> AnalyzeContext(SuggestionContext context)
        {
            // Simple analysis - would use NLP in real implementation;
            var requirements = new List<Requirement>();

            // Extract keywords from description;
            var keywords = ExtractKeywords(context.Description);

            // Map keywords to requirements;
            foreach (var keyword in keywords)
            {
                requirements.Add(new Requirement;
                {
                    Type = MapKeywordToRequirement(keyword),
                    Priority = 1.0;
                });
            }

            return requirements;
        }

        private List<PatternMatch> MatchPatternsToRequirements(List<Requirement> requirements)
        {
            var matches = new List<PatternMatch>();

            // Match all patterns against requirements;
            var allPatterns = _designPatterns.Values;
                .Cast<IPattern>()
                .Concat(_creativeTemplates.Values)
                .Concat(_innovationPatterns.Values);

            foreach (var pattern in allPatterns)
            {
                var matchScore = CalculateRequirementMatch(pattern, requirements);
                if (matchScore > 0.3) // Threshold;
                {
                    matches.Add(new PatternMatch;
                    {
                        Pattern = pattern,
                        RelevanceScore = matchScore,
                        MatchType = GetMatchType(pattern)
                    });
                }
            }

            return matches;
        }

        private double CalculateRequirementMatch(IPattern pattern, List<Requirement> requirements)
        {
            double totalScore = 0.0;

            foreach (var requirement in requirements)
            {
                var patternScore = EvaluatePatternAgainstRequirement(pattern, requirement);
                totalScore += patternScore * requirement.Priority;
            }

            return totalScore / requirements.Count;
        }

        private double EvaluatePatternAgainstRequirement(IPattern pattern, Requirement requirement)
        {
            // Simple evaluation - would be more sophisticated in real implementation;
            return pattern.Tags?.Contains(requirement.Type.ToString()) == true ? 0.8 : 0.2;
        }

        private double CalculateSuggestionScore(IPattern pattern, List<Requirement> requirements, SuggestionContext context)
        {
            var requirementMatch = CalculateRequirementMatch(pattern, requirements);
            var popularity = pattern.GetPopularityScore();
            var recency = pattern.GetRecencyScore();

            return (requirementMatch * 0.6) + (popularity * 0.3) + (recency * 0.1);
        }

        private double CalculateApplicability(IPattern pattern, SuggestionContext context)
        {
            // Simplified applicability calculation;
            return 0.7; // Would be calculated based on context constraints;
        }

        private PatternComplexity EstimateComplexity(IPattern pattern, SuggestionContext context)
        {
            return pattern.GetComplexity();
        }

        private List<string> ExtractKeywords(string text)
        {
            // Simple keyword extraction;
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLower())
                .Distinct()
                .ToList();
        }

        private RequirementType MapKeywordToRequirement(string keyword)
        {
            // Simplified mapping;
            var mapping = new Dictionary<string, RequirementType>
            {
                { "game", RequirementType.GameDesign },
                { "ui", RequirementType.UserInterface },
                { "data", RequirementType.DataManagement },
                { "performance", RequirementType.Performance },
                { "security", RequirementType.Security }
            };

            return mapping.TryGetValue(keyword, out var type) ? type : RequirementType.General;
        }

        private MatchType GetMatchType(IPattern pattern)
        {
            return pattern switch;
            {
                DesignPattern => MatchType.DesignPattern,
                CreativeTemplate => MatchType.CreativeTemplate,
                InnovationPattern => MatchType.InnovationPattern,
                _ => MatchType.Unknown;
            };
        }

        private IPattern GetPatternById(string patternId)
        {
            if (_designPatterns.TryGetValue(patternId, out var designPattern))
                return designPattern;

            if (_creativeTemplates.TryGetValue(patternId, out var creativeTemplate))
                return creativeTemplate;

            if (_innovationPatterns.TryGetValue(patternId, out var innovationPattern))
                return innovationPattern;

            return null;
        }

        private DateTime GetPeriodStart(TimePeriod period)
        {
            return period switch;
            {
                TimePeriod.Last24Hours => DateTime.UtcNow.AddHours(-24),
                TimePeriod.LastWeek => DateTime.UtcNow.AddDays(-7),
                TimePeriod.LastMonth => DateTime.UtcNow.AddDays(-30),
                TimePeriod.LastYear => DateTime.UtcNow.AddDays(-365),
                _ => DateTime.MinValue;
            };
        }

        private List<UsageStatistic> GetMostUsedPatterns(List<PatternUsage> usage, int count)
        {
            return usage;
                .GroupBy(u => u.PatternId)
                .Select(g => new UsageStatistic;
                {
                    PatternId = g.Key,
                    Count = g.Count(),
                    LastUsed = g.Max(u => u.Timestamp)
                })
                .OrderByDescending(s => s.Count)
                .Take(count)
                .ToList();
        }

        private Dictionary<string, int> GetUsageByCategory(List<PatternUsage> usage)
        {
            // Simplified implementation;
            return new Dictionary<string, int>
            {
                { "Design Patterns", usage.Count(u => u.PatternType == PatternType.Design) },
                { "Creative Templates", usage.Count(u => u.PatternType == PatternType.Creative) },
                { "Innovation Patterns", usage.Count(u => u.PatternType == PatternType.Innovation) }
            };
        }

        private double CalculateUsageTrend(TimePeriod period)
        {
            // Simplified trend calculation;
            return 1.0; // Would compare current period with previous period;
        }

        private string ExportToJson(ExportOptions options)
        {
            var exportData = new;
            {
                Metadata = new;
                {
                    ExportDate = DateTime.UtcNow,
                    TotalPatterns = GetTotalPatternCount(),
                    Version = "1.0"
                },
                DesignPatterns = options.IncludeDesignPatterns ? _designPatterns.Values : null,
                CreativeTemplates = options.IncludeCreativeTemplates ? _creativeTemplates.Values : null,
                InnovationPatterns = options.IncludeInnovationPatterns ? _innovationPatterns.Values : null;
            };

            return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = options.PrettyPrint,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });
        }

        private string ExportToXml(ExportOptions options)
        {
            // Simplified XML export;
            return $"<PatternLibrary exportDate=\"{DateTime.UtcNow:O}\" totalPatterns=\"{GetTotalPatternCount()}\" />";
        }

        private string ExportToMarkdown(ExportOptions options)
        {
            var markdown = new System.Text.StringBuilder();
            markdown.AppendLine($"# Pattern Library Export");
            markdown.AppendLine($"**Export Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            markdown.AppendLine($"**Total Patterns:** {GetTotalPatternCount()}");
            markdown.AppendLine();

            if (options.IncludeDesignPatterns && _designPatterns.Any())
            {
                markdown.AppendLine("## Design Patterns");
                markdown.AppendLine();
                foreach (var pattern in _designPatterns.Values)
                {
                    markdown.AppendLine($"### {pattern.Name}");
                    markdown.AppendLine($"**ID:** {pattern.Id}");
                    markdown.AppendLine($"**Category:** {pattern.Category}");
                    markdown.AppendLine($"**Description:** {pattern.Description}");
                    markdown.AppendLine();
                }
            }

            return markdown.ToString();
        }

        private string ExportToCsv(ExportOptions options)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Type,ID,Name,Category,Description");

            if (options.IncludeDesignPatterns)
            {
                foreach (var pattern in _designPatterns.Values)
                {
                    csv.AppendLine($"Design Pattern,{pattern.Id},{EscapeCsv(pattern.Name)},{pattern.Category},{EscapeCsv(pattern.Description)}");
                }
            }

            return csv.ToString();
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

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _designPatterns.Clear();
                    _creativeTemplates.Clear();
                    _innovationPatterns.Clear();
                    _domainPatterns.Clear();
                    _usageHistory.Clear();

                    _cache.Dispose();
                    _index.Dispose();

                    _logger.LogInformation("PatternLibrary disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PatternLibrary()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IPatternLibrary;
    {
        PatternSearchResult SearchPatterns(PatternSearchQuery query);
        DesignPattern GetDesignPattern(string patternId);
        CreativeTemplate GetCreativeTemplate(string templateId);
        List<RelatedPattern> GetRelatedPatterns(string patternId, int maxResults = 10);
        List<PatternSuggestion> SuggestPatterns(SuggestionContext context);
        void AddPattern(IPattern pattern);
        void UpdatePattern(string patternId, IPattern updatedPattern);
        string ExportPatterns(ExportFormat format, ExportOptions options);
        PatternStatistics GetStatistics();
        PatternAnalytics GetAnalytics(TimePeriod period);
    }

    public interface IPattern;
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        List<string> Tags { get; }
        string GetCategory();
        double GetPopularityScore();
        double GetRecencyScore();
        PatternComplexity GetComplexity();
    }

    /// <summary>
    /// Design pattern representation (Gang of Four, architectural, etc.)
    /// </summary>
    public class DesignPattern : IPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public PatternCategory Category { get; set; }
        public string Applicability { get; set; }
        public PatternStructure Structure { get; set; }
        public List<PatternExample> Examples { get; set; }
        public List<string> Tags { get; set; }
        public PatternComplexity Complexity { get; set; }
        public int UsageCount { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Author { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public DesignPattern()
        {
            Examples = new List<PatternExample>();
            Tags = new List<string>();
            Metadata = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }

        public string GetCategory() => Category.ToString();
        public double GetPopularityScore() => Math.Log(UsageCount + 1) / Math.Log(100);
        public double GetRecencyScore() => LastUsed.HasValue ? 1.0 / (DateTime.UtcNow - LastUsed.Value).TotalDays : 0.0;
        public PatternComplexity GetComplexity() => Complexity;
    }

    /// <summary>
    /// Creative template for narratives, characters, worlds, etc.
    /// </summary>
    public class CreativeTemplate : IPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public TemplateCategory Category { get; set; }
        public TemplateStructure Structure { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
        public List<string> Examples { get; set; }
        public List<string> Tags { get; set; }
        public int UsageCount { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public CreativeTemplate()
        {
            Attributes = new Dictionary<string, object>();
            Examples = new List<string>();
            Tags = new List<string>();
            Metadata = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
        }

        public string GetCategory() => Category.ToString();
        public double GetPopularityScore() => Math.Log(UsageCount + 1) / Math.Log(100);
        public double GetRecencyScore() => LastUsed.HasValue ? 1.0 / (DateTime.UtcNow - LastUsed.Value).TotalDays : 0.0;
        public PatternComplexity GetComplexity() => PatternComplexity.Medium;
    }

    /// <summary>
    /// Innovation and problem-solving patterns;
    /// </summary>
    public class InnovationPattern : IPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Process { get; set; }
        public List<string> Examples { get; set; }
        public List<string> Tags { get; set; }
        public InnovationType Type { get; set; }
        public int SuccessRate { get; set; } // Percentage;
        public int UsageCount { get; set; }
        public DateTime? LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public InnovationPattern()
        {
            Process = new List<string>();
            Examples = new List<string>();
            Tags = new List<string>();
            Metadata = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
        }

        public string GetCategory() => Type.ToString();
        public double GetPopularityScore() => Math.Log(UsageCount + 1) / Math.Log(100);
        public double GetRecencyScore() => LastUsed.HasValue ? 1.0 / (DateTime.UtcNow - LastUsed.Value).TotalDays : 0.0;
        public PatternComplexity GetComplexity() => PatternComplexity.High;
    }

    /// <summary>
    /// Domain-specific patterns (game development, AI, etc.)
    /// </summary>
    public class DomainPattern : IPattern;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Domain { get; set; }
        public List<string> UseCases { get; set; }
        public List<string> Tags { get; set; }
        public PatternComplexity Complexity { get; set; }
        public int UsageCount { get; set; }
        public DateTime? LastUsed { get; set; }

        public DomainPattern()
        {
            UseCases = new List<string>();
            Tags = new List<string>();
        }

        public string GetCategory() => Domain;
        public double GetPopularityScore() => Math.Log(UsageCount + 1) / Math.Log(100);
        public double GetRecencyScore() => LastUsed.HasValue ? 1.0 / (DateTime.UtcNow - LastUsed.Value).TotalDays : 0.0;
        public PatternComplexity GetComplexity() => Complexity;
    }

    /// <summary>
    /// Search query for pattern library;
    /// </summary>
    public class PatternSearchQuery;
    {
        public string Query { get; set; }
        public PatternFilters Filters { get; set; }
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 20;
        public SortOption SortBy { get; set; } = SortOption.Relevance;
        public bool IncludeExamples { get; set; } = true;

        public string GetCacheKey()
        {
            return $"{Query}_{Filters?.GetHashCode()}_{Skip}_{Take}_{SortBy}";
        }
    }

    /// <summary>
    /// Search result for pattern queries;
    /// </summary>
    public class PatternSearchResult;
    {
        public PatternSearchQuery Query { get; set; }
        public List<PatternMatch> Matches { get; set; }
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int TotalPages { get; set; }
        public TimeSpan SearchDuration { get; set; }
        public string SearchId { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public PatternSearchResult()
        {
            Matches = new List<PatternMatch>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Pattern match in search results;
    /// </summary>
    public class PatternMatch;
    {
        public IPattern Pattern { get; set; }
        public double RelevanceScore { get; set; }
        public MatchType MatchType { get; set; }
        public string MatchReason { get; set; }
    }

    /// <summary>
    /// Related pattern with relationship information;
    /// </summary>
    public class RelatedPattern;
    {
        public IPattern Pattern { get; set; }
        public PatternRelationship Relationship { get; set; }
        public double Relevance { get; set; }
    }

    /// <summary>
    /// Pattern suggestion with confidence score;
    /// </summary>
    public class PatternSuggestion;
    {
        public IPattern Pattern { get; set; }
        public string MatchReason { get; set; }
        public double ConfidenceScore { get; set; }
        public double Applicability { get; set; }
        public PatternComplexity EstimatedImplementationComplexity { get; set; }
        public List<string> RecommendedModifications { get; set; }

        public PatternSuggestion()
        {
            RecommendedModifications = new List<string>();
        }
    }

    /// <summary>
    /// Context for pattern suggestion;
    /// </summary>
    public class SuggestionContext;
    {
        public string Description { get; set; }
        public string Domain { get; set; }
        public List<string> Constraints { get; set; }
        public List<string> Goals { get; set; }
        public int MaxSuggestions { get; set; } = 5;
        public Dictionary<string, object> AdditionalContext { get; set; }

        public SuggestionContext()
        {
            Constraints = new List<string>();
            Goals = new List<string>();
            AdditionalContext = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Pattern usage tracking;
    /// </summary>
    public class PatternUsage;
    {
        public string PatternId { get; set; }
        public PatternType PatternType { get; set; }
        public DateTime Timestamp { get; set; }
        public string Context { get; set; }
        public string UserId { get; set; }
    }

    /// <summary>
    /// Pattern statistics;
    /// </summary>
    public class PatternStatistics;
    {
        public int TotalPatterns { get; set; }
        public int DesignPatterns { get; set; }
        public int CreativeTemplates { get; set; }
        public int InnovationPatterns { get; set; }
        public int TotalSearches { get; set; }
        public int TotalSuggestions { get; set; }
        public double AverageResultsPerSearch { get; set; }
        public DateTime LastUpdated { get; set; }

        public PatternStatistics Clone()
        {
            return (PatternStatistics)MemberwiseClone();
        }
    }

    /// <summary>
    /// Pattern analytics for reporting;
    /// </summary>
    public class PatternAnalytics;
    {
        public TimePeriod Period { get; set; }
        public int TotalUses { get; set; }
        public List<UsageStatistic> MostUsedPatterns { get; set; }
        public Dictionary<string, int> UsageByCategory { get; set; }
        public double UsageTrend { get; set; }
        public DateTime GeneratedAt { get; set; }

        public PatternAnalytics()
        {
            MostUsedPatterns = new List<UsageStatistic>();
            UsageByCategory = new Dictionary<string, int>();
        }
    }

    /// <summary>
    /// Usage statistic for individual patterns;
    /// </summary>
    public class UsageStatistic;
    {
        public string PatternId { get; set; }
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }
    }

    /// <summary>
    /// Pattern filters for search;
    /// </summary>
    public class PatternFilters;
    {
        public List<string> Categories { get; set; }
        public PatternComplexity? MinComplexity { get; set; }
        public PatternComplexity? MaxComplexity { get; set; }
        public List<string> Tags { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? UpdatedAfter { get; set; }

        public PatternFilters()
        {
            Categories = new List<string>();
            Tags = new List<string>();
        }
    }

    /// <summary>
    /// Export options for pattern library;
    /// </summary>
    public class ExportOptions;
    {
        public static ExportOptions Default => new ExportOptions();

        public bool IncludeDesignPatterns { get; set; } = true;
        public bool IncludeCreativeTemplates { get; set; } = true;
        public bool IncludeInnovationPatterns { get; set; } = true;
        public bool PrettyPrint { get; set; } = true;
        public bool IncludeMetadata { get; set; } = true;
        public List<string> SelectedCategories { get; set; }

        public ExportOptions()
        {
            SelectedCategories = new List<string>();
        }
    }

    /// <summary>
    /// Pattern structure representation;
    /// </summary>
    public class PatternStructure;
    {
        public List<string> Elements { get; set; }
        public Dictionary<string, List<string>> Relationships { get; set; }
        public string DiagramUrl { get; set; }

        public PatternStructure()
        {
            Elements = new List<string>();
            Relationships = new Dictionary<string, List<string>>();
        }
    }

    /// <summary>
    /// Template structure for creative templates;
    /// </summary>
    public class TemplateStructure;
    {
        public List<string> Stages { get; set; }
        public Dictionary<string, string> Sections { get; set; }
        public List<string> RequiredElements { get; set; }
        public List<string> OptionalElements { get; set; }

        public TemplateStructure()
        {
            Stages = new List<string>();
            Sections = new Dictionary<string, string>();
            RequiredElements = new List<string>();
            OptionalElements = new List<string>();
        }
    }

    /// <summary>
    /// Pattern example with code or implementation;
    /// </summary>
    public class PatternExample;
    {
        public string Context { get; set; }
        public string Code { get; set; }
        public string Language { get; set; }
        public string Explanation { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public PatternExample()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Pattern relationship in graph;
    /// </summary>
    public class PatternRelationship;
    {
        public RelationshipType Type { get; set; }
        public double Strength { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Requirement for pattern matching;
    /// </summary>
    public class Requirement;
    {
        public RequirementType Type { get; set; }
        public double Priority { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Configuration for pattern library;
    /// </summary>
    public class PatternLibraryConfiguration;
    {
        public static PatternLibraryConfiguration Default => new PatternLibraryConfiguration();

        public CacheConfiguration CacheConfiguration { get; set; }
        public IndexConfiguration IndexConfiguration { get; set; }
        public List<string> DefaultPatternSources { get; set; }
        public bool AutoLoadPatterns { get; set; } = true;
        public int MaxCacheSize { get; set; } = 1000;

        public PatternLibraryConfiguration()
        {
            CacheConfiguration = new CacheConfiguration();
            IndexConfiguration = new IndexConfiguration();
            DefaultPatternSources = new List<string>();
        }
    }

    #region Enumerations;

    public enum PatternCategory;
    {
        Creational = 0,
        Structural = 1,
        Behavioral = 2,
        Architectural = 3,
        GameDesign = 4,
        Concurrent = 5,
        Distributed = 6;
    }

    public enum TemplateCategory;
    {
        Narrative = 0,
        Character = 1,
        World = 2,
        Visual = 3,
        Audio = 4,
        Gameplay = 5;
    }

    public enum InnovationType;
    {
        Combination = 0,
        Disruption = 1,
        Incremental = 2,
        Radical = 3,
        Architectural = 4,
        Process = 5;
    }

    public enum PatternComplexity;
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4;
    }

    public enum MatchType;
    {
        Unknown = 0,
        DesignPattern = 1,
        CreativeTemplate = 2,
        InnovationPattern = 3,
        DomainPattern = 4;
    }

    public enum SortOption;
    {
        Relevance = 0,
        Name = 1,
        Popularity = 2,
        Recency = 3,
        Complexity = 4;
    }

    public enum PatternType;
    {
        Design = 0,
        Creative = 1,
        Innovation = 2,
        Domain = 3;
    }

    public enum RelationshipType;
    {
        Related = 0,
        Alternative = 1,
        Complementary = 2,
        Specialization = 3,
        Generalization = 4,
        Composition = 5,
        Dependency = 6;
    }

    public enum RequirementType;
    {
        General = 0,
        Performance = 1,
        Security = 2,
        Scalability = 3,
        Maintainability = 4,
        Usability = 5,
        GameDesign = 6,
        UserInterface = 7,
        DataManagement = 8;
    }

    public enum ExportFormat;
    {
        JSON = 0,
        XML = 1,
        Markdown = 2,
        CSV = 3,
        PDF = 4;
    }

    public enum TimePeriod;
    {
        AllTime = 0,
        Last24Hours = 1,
        LastWeek = 2,
        LastMonth = 3,
        LastYear = 4;
    }

    #endregion;

    #region Internal Support Classes;

    internal class PatternCache : IDisposable
    {
        private readonly Dictionary<string, PatternSearchResult> _searchCache;
        private readonly Queue<string> _cacheQueue;
        private readonly int _maxSize;

        public PatternCache(CacheConfiguration config)
        {
            _maxSize = config.MaxSize;
            _searchCache = new Dictionary<string, PatternSearchResult>();
            _cacheQueue = new Queue<string>();
        }

        public bool TryGetSearchResult(string key, out PatternSearchResult result)
        {
            return _searchCache.TryGetValue(key, out result);
        }

        public void CacheSearchResult(string key, PatternSearchResult result)
        {
            if (_searchCache.Count >= _maxSize)
            {
                var oldestKey = _cacheQueue.Dequeue();
                _searchCache.Remove(oldestKey);
            }

            _searchCache[key] = result;
            _cacheQueue.Enqueue(key);
        }

        public void Invalidate()
        {
            _searchCache.Clear();
            _cacheQueue.Clear();
        }

        public void Dispose()
        {
            _searchCache.Clear();
            _cacheQueue.Clear();
        }
    }

    internal class PatternIndex : IDisposable
    {
        private readonly Dictionary<string, List<string>> _tagIndex;
        private readonly Dictionary<string, List<string>> _categoryIndex;
        private readonly Dictionary<string, List<string>> _keywordIndex;

        public PatternIndex()
        {
            _tagIndex = new Dictionary<string, List<string>>();
            _categoryIndex = new Dictionary<string, List<string>>();
            _keywordIndex = new Dictionary<string, List<string>>();
        }

        public void AddPattern(DesignPattern pattern)
        {
            IndexPattern(pattern.Id, pattern.Tags, pattern.Category.ToString(), pattern.Name, pattern.Description);
        }

        public void AddTemplate(CreativeTemplate template)
        {
            IndexPattern(template.Id, template.Tags, template.Category.ToString(), template.Name, template.Description);
        }

        public void AddInnovation(InnovationPattern innovation)
        {
            IndexPattern(innovation.Id, innovation.Tags, innovation.Type.ToString(), innovation.Name, innovation.Description);
        }

        private void IndexPattern(string id, List<string> tags, string category, string name, string description)
        {
            // Index by tags;
            foreach (var tag in tags)
            {
                if (!_tagIndex.ContainsKey(tag))
                    _tagIndex[tag] = new List<string>();
                _tagIndex[tag].Add(id);
            }

            // Index by category;
            if (!_categoryIndex.ContainsKey(category))
                _categoryIndex[category] = new List<string>();
            _categoryIndex[category].Add(id);

            // Index by keywords (simplified)
            var keywords = ExtractKeywords(name + " " + description);
            foreach (var keyword in keywords)
            {
                if (!_keywordIndex.ContainsKey(keyword))
                    _keywordIndex[keyword] = new List<string>();
                _keywordIndex[keyword].Add(id);
            }
        }

        private List<string> ExtractKeywords(string text)
        {
            // Simple keyword extraction;
            return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLower())
                .Distinct()
                .ToList();
        }

        public int GetTotalCount()
        {
            var allIds = new HashSet<string>();
            foreach (var ids in _tagIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _categoryIndex.Values) allIds.UnionWith(ids);
            foreach (var ids in _keywordIndex.Values) allIds.UnionWith(ids);
            return allIds.Count;
        }

        public void Clear()
        {
            _tagIndex.Clear();
            _categoryIndex.Clear();
            _keywordIndex.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }

    internal class PatternGraph;
    {
        private readonly Dictionary<string, List<GraphEdge>> _adjacencyList;

        public PatternGraph()
        {
            _adjacencyList = new Dictionary<string, List<GraphEdge>>();
        }

        public void AddNode(string nodeId)
        {
            if (!_adjacencyList.ContainsKey(nodeId))
                _adjacencyList[nodeId] = new List<GraphEdge>();
        }

        public void AddEdge(string fromNode, string toNode, PatternRelationship relationship)
        {
            AddNode(fromNode);
            AddNode(toNode);

            _adjacencyList[fromNode].Add(new GraphEdge { NodeId = toNode, Relationship = relationship });
            _adjacencyList[toNode].Add(new GraphEdge { NodeId = fromNode, Relationship = relationship });
        }

        public List<GraphNode> GetRelatedNodes(string nodeId, int maxResults)
        {
            if (!_adjacencyList.ContainsKey(nodeId))
                return new List<GraphNode>();

            return _adjacencyList[nodeId]
                .OrderByDescending(e => e.Relationship.Strength)
                .Take(maxResults)
                .Select(e => new GraphNode;
                {
                    NodeId = e.NodeId,
                    Relationship = e.Relationship,
                    Strength = e.Relationship.Strength;
                })
                .ToList();
        }

        internal class GraphEdge;
        {
            public string NodeId { get; set; }
            public PatternRelationship Relationship { get; set; }
        }

        internal class GraphNode;
        {
            public string NodeId { get; set; }
            public PatternRelationship Relationship { get; set; }
            public double Strength { get; set; }
        }
    }

    internal class PatternTaxonomy;
    {
        private readonly Dictionary<string, List<string>> _categories;

        public PatternTaxonomy()
        {
            _categories = new Dictionary<string, List<string>>();
        }

        public void AddCategory(string category, List<string> subcategories)
        {
            _categories[category] = subcategories;
        }

        public List<string> GetSubcategories(string category)
        {
            return _categories.TryGetValue(category, out var subcategories)
                ? subcategories;
                : new List<string>();
        }
    }

    internal class CacheConfiguration;
    {
        public int MaxSize { get; set; } = 1000;
        public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(30);
    }

    internal class IndexConfiguration;
    {
        public bool EnableFullTextSearch { get; set; } = true;
        public int MaxIndexSize { get; set; } = 10000;
    }

    #endregion;

    #region Exceptions;

    public class PatternLibraryException : Exception
    {
        public PatternLibraryException(string message) : base(message) { }
        public PatternLibraryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class PatternNotFoundException : Exception
    {
        public string PatternId { get; }

        public PatternNotFoundException(string patternId)
            : base($"Pattern not found: {patternId}")
        {
            PatternId = patternId;
        }
    }

    public class DuplicatePatternException : Exception
    {
        public DuplicatePatternException(string message) : base(message) { }
    }

    public class PatternSearchException : Exception
    {
        public PatternSearchException(string message) : base(message) { }
        public PatternSearchException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class PatternSuggestionException : Exception
    {
        public PatternSuggestionException(string message) : base(message) { }
        public PatternSuggestionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class PatternExportException : Exception
    {
        public PatternExportException(string message) : base(message) { }
        public PatternExportException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
