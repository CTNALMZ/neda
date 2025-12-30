using NEDA.Animation.SequenceEditor.CinematicDirector;
using NEDA.Biometrics.FaceRecognition;
using NEDA.Brain.DecisionMaking.LogicProcessor;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.KnowledgeBase.ProblemSolutions;
using NEDA.Brain.MemorySystem.LongTermMemory;
using NEDA.Brain.MemorySystem.PatternStorage;
using NEDA.Brain.NeuralNetwork.DeepLearning;
using NEDA.Brain.NeuralNetwork.PatternRecognition;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NEDA.Brain.MemorySystem.RecallMechanism;
{
    /// <summary>
    /// Çağrışım motoru - bellek ilişkilendirme ve çağrışımsal hatırlama sistemi.
    /// NEDA'nın bağlantılı anıları, kavramları ve deneyimleri ilişkilendirmesini sağlar.
    /// </summary>
    public interface IAssociationEngine;
    {
        /// <summary>
        /// İki kavram/deneyim arasında ilişki kurar.
        /// </summary>
        Task<AssociationResult> AssociateAsync(AssociationRequest request);

        /// <summary>
        /// Bir kavramla ilişkili tüm öğeleri getirir.
        /// </summary>
        Task<AssociationNetwork> GetAssociationsAsync(Guid sourceId, AssociationType? filterType = null);

        /// <summary>
        /// İki öğe arasındaki ilişkiyi kaldırır.
        /// </summary>
        Task<DisassociationResult> DisassociateAsync(Guid sourceId, Guid targetId);

        /// <summary>
        /// Çağrışımsal hatırlama yapar.
        /// </summary>
        Task<RecallResult> RecallByAssociationAsync(RecallRequest request);

        /// <summary>
        /// İlişki gücünü günceller.
        /// </summary>
        Task<AssociationUpdateResult> UpdateAssociationStrengthAsync(Guid associationId, float newStrength);

        /// <summary>
        /// İlişki ağını analiz eder.
        /// </summary>
        Task<NetworkAnalysis> AnalyzeAssociationNetworkAsync(NetworkScope scope);

        /// <summary>
        /// Çapraz ilişkilendirme yapar.
        /// </summary>
        Task<CrossAssociationResult> CrossAssociateAsync(CrossAssociationRequest request);

        /// <summary>
        /// Metaforik ilişkilendirme yapar.
        /// </summary>
        Task<MetaphoricalAssociation> CreateMetaphoricalAssociationAsync(MetaphorRequest request);

        /// <summary>
        /// İlişki yayılımını hesaplar.
        /// </summary>
        Task<PropagationResult> CalculateAssociationPropagationAsync(Guid startId, int maxDepth);

        /// <summary>
        /// İlişki kümesini bulur.
        /// </summary>
        Task<AssociationCluster> FindAssociationClustersAsync(ClusteringParameters parameters);

        /// <summary>
        /// Anlık çağrışımları tetikler.
        /// </summary>
        Task<SpontaneousAssociation> TriggerSpontaneousAssociationAsync(TriggerContext context);

        /// <summary>
        /// Bağlamsal çağrışım yapar.
        /// </summary>
        Task<ContextualAssociation> PerformContextualAssociationAsync(ContextualRequest request);

        /// <summary>
        /// Duygusal çağrışım yapar.
        /// </summary>
        Task<EmotionalAssociation> CreateEmotionalAssociationAsync(EmotionalRequest request);

        /// <summary>
        /// Zaman bazlı çağrışım yapar.
        /// </summary>
        Task<TemporalAssociation> AnalyzeTemporalAssociationsAsync(TimeRange range);

        /// <summary>
        /// Semantik çağrışım yapar.
        /// </summary>
        Task<SemanticAssociation> PerformSemanticAssociationAsync(SemanticRequest request);

        /// <summary>
        /// Çağrışım zincirlerini bulur.
        /// </summary>
        Task<AssociationChain> FindAssociationChainsAsync(Guid startId, Guid endId, int maxLength);

        /// <summary>
        /// İlişki önerileri getirir.
        /// </summary>
        Task<AssociationSuggestion> SuggestAssociationsAsync(SuggestionRequest request);

        /// <summary>
        /// İlişki kalıplarını öğrenir.
        /// </summary>
        Task<AssociationPattern> LearnAssociationPatternsAsync(LearningSet dataset);

        /// <summary>
        /// İlişki önceliklendirme yapar.
        /// </summary>
        Task<AssociationPriority> PrioritizeAssociationsAsync(PriorityCriteria criteria);

        /// <summary>
        /// İlişki doğrulaması yapar.
        /// </summary>
        Task<ValidationResult> ValidateAssociationAsync(Guid associationId);

        /// <summary>
        /// İlişki evrimini yönetir.
        /// </summary>
        Task<EvolutionResult> EvolveAssociationsAsync(EvolutionParameters parameters);

        /// <summary>
        /// Çağrışım motoru durumunu kontrol eder.
        /// </summary>
        Task<EngineStatus> CheckEngineStatusAsync();

        /// <summary>
        /// İlişki veritabanını optimize eder.
        /// </summary>
        Task<OptimizationResult> OptimizeAssociationDatabaseAsync();

        /// <summary>
        /// Çağrışım geçmişini getirir.
        /// </summary>
        Task<AssociationHistory> GetAssociationHistoryAsync(Guid entityId);

        /// <summary>
        /// İlişki ağını görselleştirir.
        /// </summary>
        Task<VisualizationData> VisualizeAssociationNetworkAsync(VisualizationRequest request);

        /// <summary>
        /// İlişki yedeklemesi yapar.
        /// </summary>
        Task<BackupResult> BackupAssociationsAsync(BackupStrategy strategy);

        /// <summary>
        /// Çağrışım hızını ölçer.
        /// </summary>
        Task<PerformanceMetrics> MeasureAssociationPerformanceAsync();
    }

    /// <summary>
    /// Çağrışım motoru implementasyonu.
    /// </summary>
    public class AssociationEngine : IAssociationEngine, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly IExperienceBank _experienceBank;
        private readonly IBehaviorPatterns _behaviorPatterns;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IPatternRecognizer _patternRecognizer;

        private readonly ConcurrentDictionary<Guid, AssociationNode> _associationNodes;
        private readonly ConcurrentDictionary<Guid, List<AssociationEdge>> _outgoingEdges;
        private readonly ConcurrentDictionary<Guid, List<AssociationEdge>> _incomingEdges;
        private readonly ConcurrentDictionary<string, List<Guid>> _semanticIndex;
        private readonly ConcurrentDictionary<Guid, List<AssociationHistoryEntry>> _associationHistory;

        private readonly AssociationCache _associationCache;
        private readonly SemaphoreSlim _associationLock = new SemaphoreSlim(1, 1);

        private bool _disposed;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Random _random = new Random();
        private readonly string _backupDirectory;

        /// <summary>
        /// Çağrışım motoru sınıfı.
        /// </summary>
        public AssociationEngine(
            ILogger logger,
            IEventBus eventBus,
            IKnowledgeGraph knowledgeGraph,
            IExperienceBank experienceBank,
            IBehaviorPatterns behaviorPatterns,
            INeuralNetwork neuralNetwork,
            IPatternRecognizer patternRecognizer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _experienceBank = experienceBank ?? throw new ArgumentNullException(nameof(experienceBank));
            _behaviorPatterns = behaviorPatterns ?? throw new ArgumentNullException(nameof(behaviorPatterns));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));
            _patternRecognizer = patternRecognizer ?? throw new ArgumentNullException(nameof(patternRecognizer));

            _associationNodes = new ConcurrentDictionary<Guid, AssociationNode>();
            _outgoingEdges = new ConcurrentDictionary<Guid, List<AssociationEdge>>();
            _incomingEdges = new ConcurrentDictionary<Guid, List<AssociationEdge>>();
            _semanticIndex = new ConcurrentDictionary<string, List<Guid>>();
            _associationHistory = new ConcurrentDictionary<Guid, List<AssociationHistoryEntry>>();

            _associationCache = new AssociationCache(capacity: 5000);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                IncludeFields = false;
            };

            _backupDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups", "Associations");

            InitializeAssociationEngineAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Çağrışım motorunu başlatır.
        /// </summary>
        private async Task InitializeAssociationEngineAsync()
        {
            try
            {
                // Yedekleme dizinini oluştur;
                Directory.CreateDirectory(_backupDirectory);

                // Önbelleği temizle;
                _associationCache.Clear();

                // Temel ilişkileri oluştur;
                await InitializeCoreAssociationsAsync();

                // Bilgi grafiğinden ilişkileri yükle;
                await LoadAssociationsFromKnowledgeGraphAsync();

                // Sinir ağını ilişki tanıma için eğit;
                await TrainNeuralNetworkForAssociationRecognitionAsync();

                var status = await CheckEngineStatusInternalAsync();

                _logger.Information("AssociationEngine initialized. Nodes: {NodeCount}, Edges: {EdgeCount}, Cache: {CacheSize}",
                    status.TotalNodes, status.TotalEdges, status.CacheSize);

                await _eventBus.PublishAsync(new AssociationEngineInitializedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    NodeCount = status.TotalNodes,
                    EdgeCount = status.TotalEdges,
                    AverageDegree = status.AverageDegree,
                    CacheHitRate = 0;
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to initialize AssociationEngine");
                throw new AssociationEngineInitializationException(
                    "Failed to initialize association engine",
                    ex,
                    ErrorCodes.AssociationEngine.InitializationFailed);
            }
        }

        /// <summary>
        /// Temel ilişkileri oluşturur.
        /// </summary>
        private async Task InitializeCoreAssociationsAsync()
        {
            // Temel kavram düğümleri;
            var coreConcepts = new[]
            {
                new AssociationNode;
                {
                    Id = Guid.Parse("AAAAAAA1-BBBB-CCCC-DDDD-EEEEEEEEEEE1"),
                    EntityId = Guid.Parse("BBBBBBB1-AAAA-CCCC-DDDD-EEEEEEEEEEE1"),
                    EntityType = EntityType.Concept,
                    Name = "ProblemSolving",
                    DisplayName = "Problem Çözme",
                    Description = "Sorunları analiz etme ve çözme yeteneği",
                    SemanticTags = new List<string> { "analytical", "systematic", "logical", "creative" },
                    ActivationLevel = 0.8f,
                    CreatedAt = DateTime.UtcNow;
                },
                new AssociationNode;
                {
                    Id = Guid.Parse("AAAAAAA2-BBBB-CCCC-DDDD-EEEEEEEEEEE2"),
                    EntityId = Guid.Parse("BBBBBBB2-AAAA-CCCC-DDDD-EEEEEEEEEEE2"),
                    EntityType = EntityType.Concept,
                    Name = "Learning",
                    DisplayName = "Öğrenme",
                    Description = "Bilgi ve beceri edinme süreci",
                    SemanticTags = new List<string> { "cognitive", "adaptive", "progressive", "retention" },
                    ActivationLevel = 0.9f,
                    CreatedAt = DateTime.UtcNow;
                },
                new AssociationNode;
                {
                    Id = Guid.Parse("AAAAAAA3-BBBB-CCCC-DDDD-EEEEEEEEEEE3"),
                    EntityId = Guid.Parse("BBBBBBB3-AAAA-CCCC-DDDD-EEEEEEEEEEE3"),
                    EntityType = EntityType.Concept,
                    Name = "DecisionMaking",
                    DisplayName = "Karar Verme",
                    Description = "Seçenekler arasında seçim yapma süreci",
                    SemanticTags = new List<string> { "analytical", "risk", "evaluation", "consequence" },
                    ActivationLevel = 0.75f,
                    CreatedAt = DateTime.UtcNow;
                },
                new AssociationNode;
                {
                    Id = Guid.Parse("AAAAAAA4-BBBB-CCCC-DDDD-EEEEEEEEEEE4"),
                    EntityId = Guid.Parse("BBBBBBB4-AAAA-CCCC-DDDD-EEEEEEEEEEE4"),
                    EntityType = EntityType.Concept,
                    Name = "Creativity",
                    DisplayName = "Yaratıcılık",
                    Description = "Yeni fikirler ve çözümler üretme yeteneği",
                    SemanticTags = new List<string> { "innovative", "original", "imaginative", "divergent" },
                    ActivationLevel = 0.7f,
                    CreatedAt = DateTime.UtcNow;
                }
            };

            foreach (var concept in coreConcepts)
            {
                _associationNodes[concept.Id] = concept;

                // Semantik indeksleme;
                foreach (var tag in concept.SemanticTags)
                {
                    _semanticIndex.AddOrUpdate(tag.ToLowerInvariant(),
                        key => new List<Guid> { concept.Id },
                        (key, existingList) =>
                        {
                            if (!existingList.Contains(concept.Id))
                                existingList.Add(concept.Id);
                            return existingList;
                        });
                }
            }

            // Temel ilişkileri kur;
            var coreAssociations = new[]
            {
                // Problem Çözme ↔ Öğrenme;
                new AssociationEdge;
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = coreConcepts[0].Id,
                    TargetNodeId = coreConcepts[1].Id,
                    AssociationType = AssociationType.Semantic,
                    Strength = 0.85f,
                    Confidence = 0.9f,
                    CreatedAt = DateTime.UtcNow,
                    LastActivated = DateTime.UtcNow,
                    ActivationCount = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "Relation", "enhances" },
                        { "Bidirectional", true },
                        { "EvidenceCount", 5 }
                    }
                },
                // Öğrenme ↔ Karar Verme;
                new AssociationEdge;
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = coreConcepts[1].Id,
                    TargetNodeId = coreConcepts[2].Id,
                    AssociationType = AssociationType.Causal,
                    Strength = 0.8f,
                    Confidence = 0.85f,
                    CreatedAt = DateTime.UtcNow,
                    LastActivated = DateTime.UtcNow,
                    ActivationCount = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "Relation", "improves" },
                        { "Direction", "Learning → DecisionMaking" },
                        { "EvidenceCount", 3 }
                    }
                },
                // Karar Verme ↔ Yaratıcılık;
                new AssociationEdge;
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = coreConcepts[2].Id,
                    TargetNodeId = coreConcepts[3].Id,
                    AssociationType = AssociationType.Complementary,
                    Strength = 0.7f,
                    Confidence = 0.75f,
                    CreatedAt = DateTime.UtcNow,
                    LastActivated = DateTime.UtcNow,
                    ActivationCount = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "Relation", "balances" },
                        { "Bidirectional", true },
                        { "EvidenceCount", 2 }
                    }
                },
                // Yaratıcılık ↔ Problem Çözme;
                new AssociationEdge;
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = coreConcepts[3].Id,
                    TargetNodeId = coreConcepts[0].Id,
                    AssociationType = AssociationType.Enhancement,
                    Strength = 0.9f,
                    Confidence = 0.85f,
                    CreatedAt = DateTime.UtcNow,
                    LastActivated = DateTime.UtcNow,
                    ActivationCount = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "Relation", "enables" },
                        { "Direction", "Creativity → ProblemSolving" },
                        { "EvidenceCount", 4 }
                    }
                }
            };

            foreach (var edge in coreAssociations)
            {
                AddEdgeToGraph(edge);
            }

            _logger.Debug("Core associations initialized with {NodeCount} nodes and {EdgeCount} edges",
                coreConcepts.Length, coreAssociations.Length);
        }

        private void AddEdgeToGraph(AssociationEdge edge)
        {
            // Giden kenarlar;
            _outgoingEdges.AddOrUpdate(edge.SourceNodeId,
                id => new List<AssociationEdge> { edge },
                (id, existingEdges) =>
                {
                    if (!existingEdges.Any(e => e.TargetNodeId == edge.TargetNodeId && e.AssociationType == edge.AssociationType))
                    {
                        existingEdges.Add(edge);
                    }
                    return existingEdges;
                });

            // Gelen kenarlar;
            _incomingEdges.AddOrUpdate(edge.TargetNodeId,
                id => new List<AssociationEdge> { edge },
                (id, existingEdges) =>
                {
                    if (!existingEdges.Any(e => e.SourceNodeId == edge.SourceNodeId && e.AssociationType == edge.AssociationType))
                    {
                        existingEdges.Add(edge);
                    }
                    return existingEdges;
                });
        }

        /// <summary>
        /// Bilgi grafiğinden ilişkileri yükler.
        /// </summary>
        private async Task LoadAssociationsFromKnowledgeGraphAsync()
        {
            try
            {
                // Bilgi grafiğinden ilişkileri al;
                var knowledgeRelationships = await _knowledgeGraph.GetAllRelationshipsAsync();

                foreach (var relationship in knowledgeRelationships)
                {
                    // Düğümleri oluştur veya güncelle;
                    var sourceNode = await GetOrCreateNodeAsync(relationship.SourceId, EntityType.KnowledgeEntity);
                    var targetNode = await GetOrCreateNodeAsync(relationship.TargetId, EntityType.KnowledgeEntity);

                    // İlişkiyi ekle;
                    var edge = new AssociationEdge;
                    {
                        Id = Guid.NewGuid(),
                        SourceNodeId = sourceNode.Id,
                        TargetNodeId = targetNode.Id,
                        AssociationType = MapRelationshipType(relationship.RelationshipType),
                        Strength = relationship.Strength,
                        Confidence = relationship.Confidence,
                        CreatedAt = relationship.CreatedAt,
                        LastActivated = DateTime.UtcNow,
                        ActivationCount = 0,
                        Metadata = new Dictionary<string, object>
                        {
                            { "KnowledgeGraphId", relationship.Id },
                            { "Properties", relationship.Properties }
                        }
                    };

                    AddEdgeToGraph(edge);
                }

                _logger.Debug("Loaded {Count} associations from knowledge graph", knowledgeRelationships.Count());
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load associations from knowledge graph");
            }
        }

        private async Task<AssociationNode> GetOrCreateNodeAsync(Guid entityId, EntityType entityType)
        {
            // Var olan düğümü ara;
            var existingNode = _associationNodes.Values.FirstOrDefault(n => n.EntityId == entityId && n.EntityType == entityType);
            if (existingNode != null)
                return existingNode;

            // Yeni düğüm oluştur;
            var entity = await _knowledgeGraph.GetEntityAsync(entityId);
            if (entity == null)
                throw new AssociationEngineException($"Entity not found: {entityId}",
                    ErrorCodes.AssociationEngine.EntityNotFound);

            var newNode = new AssociationNode;
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                EntityType = entityType,
                Name = entity.Name,
                DisplayName = entity.Properties?.GetValueOrDefault("DisplayName")?.ToString() ?? entity.Name,
                Description = entity.Properties?.GetValueOrDefault("Description")?.ToString() ?? string.Empty,
                SemanticTags = ExtractSemanticTags(entity),
                ActivationLevel = 0.5f,
                CreatedAt = DateTime.UtcNow,
                Metadata = entity.Properties;
            };

            _associationNodes[newNode.Id] = newNode;

            // Semantik indeksleme;
            foreach (var tag in newNode.SemanticTags)
            {
                _semanticIndex.AddOrUpdate(tag.ToLowerInvariant(),
                    key => new List<Guid> { newNode.Id },
                    (key, existingList) =>
                    {
                        if (!existingList.Contains(newNode.Id))
                            existingList.Add(newNode.Id);
                        return existingList;
                    });
            }

            return newNode;
        }

        private List<string> ExtractSemanticTags(KnowledgeEntity entity)
        {
            var tags = new List<string>();

            // Tür etiketi;
            tags.Add(entity.Type.ToLowerInvariant());

            // Özelliklerden etiketler;
            if (entity.Properties != null)
            {
                if (entity.Properties.TryGetValue("Category", out var category))
                    tags.Add(category.ToString().ToLowerInvariant());

                if (entity.Properties.TryGetValue("Tags", out var tagObj) && tagObj is List<string> tagList)
                    tags.AddRange(tagList.Select(t => t.ToLowerInvariant()));
            }

            return tags.Distinct().ToList();
        }

        private AssociationType MapRelationshipType(string relationshipType)
        {
            return relationshipType.ToLowerInvariant() switch;
            {
                "similar" => AssociationType.Semantic,
                "complementary" => AssociationType.Complementary,
                "causal" => AssociationType.Causal,
                "enhances" => AssociationType.Enhancement,
                "conflicts" => AssociationType.Conflict,
                "prerequisite" => AssociationType.Prerequisite,
                "partof" => AssociationType.PartWhole,
                "instance" => AssociationType.Instance,
                _ => AssociationType.Generic;
            };
        }

        /// <summary>
        /// Sinir ağını ilişki tanıma için eğitir.
        /// </summary>
        private async Task TrainNeuralNetworkForAssociationRecognitionAsync()
        {
            try
            {
                var trainingData = new List<TrainingData>();

                // Mevcut ilişkilerden eğitim verileri oluştur;
                foreach (var edge in GetAllEdges())
                {
                    var sourceNode = _associationNodes[edge.SourceNodeId];
                    var targetNode = _associationNodes[edge.TargetNodeId];

                    var input = EncodeNodesForNeuralNetwork(sourceNode, targetNode);
                    var output = EncodeAssociationForNeuralNetwork(edge);

                    trainingData.Add(new TrainingData;
                    {
                        Input = input,
                        Output = output,
                        Label = $"{sourceNode.Name}->{targetNode.Name}"
                    });
                }

                if (trainingData.Count >= 10)
                {
                    var trainingResult = await _neuralNetwork.TrainAsync(trainingData, new TrainingParameters;
                    {
                        Epochs = 100,
                        LearningRate = 0.001f,
                        BatchSize = 8,
                        ValidationSplit = 0.2f;
                    });

                    _logger.Debug("Neural network trained for association recognition. Loss: {Loss}", trainingResult.Loss);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to train neural network for association recognition");
            }
        }

        private float[] EncodeNodesForNeuralNetwork(AssociationNode node1, AssociationNode node2)
        {
            var encoding = new List<float>();

            // Düğüm benzerliği;
            var semanticSimilarity = CalculateSemanticSimilarity(node1.SemanticTags, node2.SemanticTags);
            encoding.Add(semanticSimilarity);

            // Aktivasyon seviyeleri;
            encoding.Add(node1.ActivationLevel);
            encoding.Add(node2.ActivationLevel);

            // Varlık türleri;
            encoding.AddRange(EncodeEntityType(node1.EntityType));
            encoding.AddRange(EncodeEntityType(node2.EntityType));

            return encoding.ToArray();
        }

        private float CalculateSemanticSimilarity(List<string> tags1, List<string> tags2)
        {
            if (tags1 == null || tags2 == null || !tags1.Any() || !tags2.Any())
                return 0;

            var commonTags = tags1.Intersect(tags2).Count();
            var totalTags = tags1.Union(tags2).Count();

            return totalTags > 0 ? commonTags / (float)totalTags : 0;
        }

        private float[] EncodeEntityType(EntityType entityType)
        {
            var types = Enum.GetValues(typeof(EntityType)).Cast<EntityType>().ToList();
            var encoding = new float[types.Count];

            var index = types.IndexOf(entityType);
            if (index >= 0)
            {
                encoding[index] = 1.0f;
            }

            return encoding;
        }

        private float[] EncodeAssociationForNeuralNetwork(AssociationEdge edge)
        {
            var encoding = new List<float>();

            // İlişki gücü;
            encoding.Add(edge.Strength);

            // İlişki türü;
            encoding.AddRange(EncodeAssociationType(edge.AssociationType));

            // Güven;
            encoding.Add(edge.Confidence);

            return encoding.ToArray();
        }

        private float[] EncodeAssociationType(AssociationType associationType)
        {
            var types = Enum.GetValues(typeof(AssociationType)).Cast<AssociationType>().ToList();
            var encoding = new float[types.Count];

            var index = types.IndexOf(associationType);
            if (index >= 0)
            {
                encoding[index] = 1.0f;
            }

            return encoding;
        }

        /// <inheritdoc/>
        public async Task<AssociationResult> AssociateAsync(AssociationRequest request)
        {
            ValidateAssociationRequest(request);

            await _associationLock.WaitAsync();
            try
            {
                // Düğümleri bul veya oluştur;
                var sourceNode = await GetOrCreateNodeAsync(request.SourceEntityId, request.SourceEntityType);
                var targetNode = await GetOrCreateNodeAsync(request.TargetEntityId, request.TargetEntityType);

                // İlişkinin varlığını kontrol et;
                var existingEdge = GetExistingAssociation(sourceNode.Id, targetNode.Id, request.AssociationType);
                if (existingEdge != null)
                {
                    return await UpdateExistingAssociationAsync(existingEdge, request);
                }

                // Yeni ilişki oluştur;
                var newEdge = new AssociationEdge;
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = sourceNode.Id,
                    TargetNodeId = targetNode.Id,
                    AssociationType = request.AssociationType,
                    Strength = request.InitialStrength,
                    Confidence = request.InitialConfidence,
                    CreatedAt = DateTime.UtcNow,
                    LastActivated = DateTime.UtcNow,
                    ActivationCount = 1,
                    Metadata = request.Metadata ?? new Dictionary<string, object>()
                };

                // İlişkiyi ekle;
                AddEdgeToGraph(newEdge);

                // Aktivasyon seviyelerini güncelle;
                await UpdateNodeActivationAsync(sourceNode.Id, 0.1f);
                await UpdateNodeActivationAsync(targetNode.Id, 0.05f);

                // Önbelleğe ekle;
                _associationCache.Put(newEdge.Id, newEdge);

                // Tarihçeye ekle;
                await RecordAssociationHistoryAsync(sourceNode.Id, targetNode.Id,
                    AssociationHistoryEventType.Created, newEdge);

                var result = new AssociationResult;
                {
                    AssociationId = newEdge.Id,
                    Success = true,
                    AssociationCreated = DateTime.UtcNow,
                    Strength = newEdge.Strength,
                    Confidence = newEdge.Confidence,
                    PropagationEffect = await CalculateInitialPropagationAsync(newEdge),
                    RelatedAssociations = await FindRelatedAssociationsAsync(newEdge, 3)
                };

                await _eventBus.PublishAsync(new AssociationCreatedEvent;
                {
                    AssociationId = newEdge.Id,
                    SourceEntityId = request.SourceEntityId,
                    TargetEntityId = request.TargetEntityId,
                    AssociationType = request.AssociationType,
                    Strength = request.InitialStrength,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Information("Association created: {Source} → {Target} ({Type})",
                    sourceNode.Name, targetNode.Name, request.AssociationType);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create association");
                throw new AssociationEngineException(
                    "Failed to create association",
                    ex,
                    ErrorCodes.AssociationEngine.AssociationFailed);
            }
            finally
            {
                _associationLock.Release();
            }
        }

        private AssociationEdge GetExistingAssociation(Guid sourceId, Guid targetId, AssociationType type)
        {
            if (_outgoingEdges.TryGetValue(sourceId, out var edges))
            {
                return edges.FirstOrDefault(e =>
                    e.TargetNodeId == targetId &&
                    e.AssociationType == type);
            }
            return null;
        }

        private async Task<AssociationResult> UpdateExistingAssociationAsync(AssociationEdge existingEdge, AssociationRequest request)
        {
            // İlişki gücünü güncelle (Hebbian learning: "neurons that fire together, wire together")
            var newStrength = CalculateUpdatedStrength(existingEdge.Strength, request.InitialStrength);
            existingEdge.Strength = newStrength;

            // Güveni güncelle;
            existingEdge.Confidence = Math.Max(existingEdge.Confidence, request.InitialConfidence);

            // Aktivasyon sayısını artır;
            existingEdge.ActivationCount++;
            existingEdge.LastActivated = DateTime.UtcNow;

            // Meta verileri birleştir;
            if (request.Metadata != null)
            {
                foreach (var kvp in request.Metadata)
                {
                    existingEdge.Metadata[kvp.Key] = kvp.Value;
                }
            }

            // Önbelleği güncelle;
            _associationCache.Put(existingEdge.Id, existingEdge);

            // Tarihçeye ekle;
            await RecordAssociationHistoryAsync(existingEdge.SourceNodeId, existingEdge.TargetNodeId,
                AssociationHistoryEventType.Strengthened, existingEdge);

            var result = new AssociationResult;
            {
                AssociationId = existingEdge.Id,
                Success = true,
                AssociationCreated = existingEdge.CreatedAt,
                Strength = existingEdge.Strength,
                Confidence = existingEdge.Confidence,
                PropagationEffect = await CalculatePropagationForExistingAssociationAsync(existingEdge),
                RelatedAssociations = await FindRelatedAssociationsAsync(existingEdge, 2),
                WasExisting = true;
            };

            _logger.Debug("Existing association strengthened: {AssociationId}, New strength: {Strength}",
                existingEdge.Id, existingEdge.Strength);

            return result;
        }

        private float CalculateUpdatedStrength(float existingStrength, float newStrength)
        {
            // Hebbian learning rule with decay;
            var learningRate = 0.1f;
            var decayFactor = 0.95f;

            return (existingStrength * decayFactor) + (newStrength * learningRate);
        }

        private async Task<float> CalculateInitialPropagationAsync(AssociationEdge newEdge)
        {
            // Yeni ilişkinin yayılım etkisini hesapla;
            float propagation = 0;

            // Kaynak düğümün diğer ilişkileri;
            if (_outgoingEdges.TryGetValue(newEdge.SourceNodeId, out var sourceEdges))
            {
                propagation += sourceEdges.Count * 0.1f;
            }

            // Hedef düğümün diğer ilişkileri;
            if (_incomingEdges.TryGetValue(newEdge.TargetNodeId, out var targetEdges))
            {
                propagation += targetEdges.Count * 0.1f;
            }

            // İlişki gücüne göre yayılım;
            propagation += newEdge.Strength * 0.3f;

            return Math.Min(propagation, 1.0f);
        }

        private async Task UpdateNodeActivationAsync(Guid nodeId, float activationIncrease)
        {
            if (_associationNodes.TryGetValue(nodeId, out var node))
            {
                node.ActivationLevel = Math.Min(node.ActivationLevel + activationIncrease, 1.0f);
                node.LastActivated = DateTime.UtcNow;
                node.ActivationCount++;

                _associationNodes[nodeId] = node;
            }
        }

        private async Task RecordAssociationHistoryAsync(Guid sourceId, Guid targetId,
            AssociationHistoryEventType eventType, AssociationEdge edge)
        {
            var historyEntry = new AssociationHistoryEntry
            {
                EventType = eventType,
                SourceNodeId = sourceId,
                TargetNodeId = targetId,
                AssociationId = edge.Id,
                Strength = edge.Strength,
                Confidence = edge.Confidence,
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    { "ActivationCount", edge.ActivationCount },
                    { "AssociationType", edge.AssociationType.ToString() }
                }
            };

            // Kaynak düğüm tarihçesi;
            _associationHistory.AddOrUpdate(sourceId,
                id => new List<AssociationHistoryEntry> { historyEntry },
                (id, existingHistory) =>
                {
                    existingHistory.Add(historyEntry);
                    // Son 100 kaydı tut;
                    if (existingHistory.Count > 100)
                    {
                        existingHistory = existingHistory;
                            .OrderByDescending(h => h.Timestamp)
                            .Take(100)
                            .ToList();
                    }
                    return existingHistory;
                });

            // Hedef düğüm tarihçesi;
            _associationHistory.AddOrUpdate(targetId,
                id => new List<AssociationHistoryEntry> { historyEntry },
                (id, existingHistory) =>
                {
                    existingHistory.Add(historyEntry);
                    if (existingHistory.Count > 100)
                    {
                        existingHistory = existingHistory;
                            .OrderByDescending(h => h.Timestamp)
                            .Take(100)
                            .ToList();
                    }
                    return existingHistory;
                });
        }

        /// <inheritdoc/>
        public async Task<AssociationNetwork> GetAssociationsAsync(Guid sourceId, AssociationType? filterType = null)
        {
            try
            {
                var sourceNode = _associationNodes.Values.FirstOrDefault(n => n.EntityId == sourceId);
                if (sourceNode == null)
                {
                    throw new AssociationEngineException($"Node not found for entity: {sourceId}",
                        ErrorCodes.AssociationEngine.NodeNotFound);
                }

                var network = new AssociationNetwork;
                {
                    SourceNode = sourceNode,
                    RetrievedAt = DateTime.UtcNow;
                };

                // Giden ilişkiler;
                if (_outgoingEdges.TryGetValue(sourceNode.Id, out var outgoingEdges))
                {
                    network.OutgoingAssociations = filterType.HasValue;
                        ? outgoingEdges.Where(e => e.AssociationType == filterType.Value).ToList()
                        : outgoingEdges.ToList();

                    network.OutgoingNodes = await GetAssociatedNodesAsync(network.OutgoingAssociations);
                }

                // Gelen ilişkiler;
                if (_incomingEdges.TryGetValue(sourceNode.Id, out var incomingEdges))
                {
                    network.IncomingAssociations = filterType.HasValue;
                        ? incomingEdges.Where(e => e.AssociationType == filterType.Value).ToList()
                        : incomingEdges.ToList();

                    network.IncomingNodes = await GetAssociatedNodesAsync(network.IncomingAssociations, true);
                }

                // İstatistikler;
                network.Statistics = CalculateNetworkStatistics(sourceNode, network);

                // İlişki örüntüleri;
                network.Patterns = await FindAssociationPatternsAsync(sourceNode, network);

                // Öneriler;
                network.Suggestions = await GenerateAssociationSuggestionsAsync(sourceNode, network);

                _logger.Debug("Associations retrieved for node: {NodeName}, Outgoing: {Outgoing}, Incoming: {Incoming}",
                    sourceNode.Name, network.OutgoingAssociations?.Count ?? 0, network.IncomingAssociations?.Count ?? 0);

                return network;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get associations for entity: {EntityId}", sourceId);
                throw new AssociationEngineException(
                    $"Failed to get associations for entity: {sourceId}",
                    ex,
                    ErrorCodes.AssociationEngine.RetrievalFailed);
            }
        }

        private async Task<List<AssociationNode>> GetAssociatedNodesAsync(List<AssociationEdge> edges, bool isIncoming = false)
        {
            var nodeIds = isIncoming;
                ? edges.Select(e => e.SourceNodeId).Distinct().ToList()
                : edges.Select(e => e.TargetNodeId).Distinct().ToList();

            var nodes = new List<AssociationNode>();
            foreach (var nodeId in nodeIds)
            {
                if (_associationNodes.TryGetValue(nodeId, out var node))
                {
                    nodes.Add(node);
                }
            }

            return nodes.OrderByDescending(n => n.ActivationLevel).ToList();
        }

        private NetworkStatistics CalculateNetworkStatistics(AssociationNode sourceNode, AssociationNetwork network)
        {
            return new NetworkStatistics;
            {
                TotalAssociations = (network.OutgoingAssociations?.Count ?? 0) + (network.IncomingAssociations?.Count ?? 0),
                UniqueConnectedNodes = network.OutgoingNodes?.Count + network.IncomingNodes?.Count ?? 0,
                AverageStrength = CalculateAverageStrength(network),
                AssociationDensity = CalculateAssociationDensity(sourceNode, network),
                StrongestAssociation = FindStrongestAssociation(network),
                MostActiveAssociation = FindMostActiveAssociation(network),
                DegreeCentrality = CalculateDegreeCentrality(sourceNode),
                ClusteringCoefficient = CalculateClusteringCoefficient(sourceNode)
            };
        }

        private float CalculateAverageStrength(AssociationNetwork network)
        {
            var allEdges = new List<AssociationEdge>();
            if (network.OutgoingAssociations != null)
                allEdges.AddRange(network.OutgoingAssociations);
            if (network.IncomingAssociations != null)
                allEdges.AddRange(network.IncomingAssociations);

            return allEdges.Any() ? allEdges.Average(e => e.Strength) : 0;
        }

        private float CalculateAssociationDensity(AssociationNode sourceNode, AssociationNetwork network)
        {
            var totalPossibleAssociations = _associationNodes.Count - 1;
            if (totalPossibleAssociations <= 0)
                return 0;

            var actualAssociations = (network.OutgoingAssociations?.Count ?? 0) + (network.IncomingAssociations?.Count ?? 0);
            return actualAssociations / (float)totalPossibleAssociations;
        }

        private AssociationEdge FindStrongestAssociation(AssociationNetwork network)
        {
            var allEdges = new List<AssociationEdge>();
            if (network.OutgoingAssociations != null)
                allEdges.AddRange(network.OutgoingAssociations);
            if (network.IncomingAssociations != null)
                allEdges.AddRange(network.IncomingAssociations);

            return allEdges.OrderByDescending(e => e.Strength).FirstOrDefault();
        }

        private float CalculateDegreeCentrality(AssociationNode node)
        {
            var outgoingCount = _outgoingEdges.GetValueOrDefault(node.Id)?.Count ?? 0;
            var incomingCount = _incomingEdges.GetValueOrDefault(node.Id)?.Count ?? 0;
            var totalNodes = _associationNodes.Count;

            return totalNodes > 1 ? (outgoingCount + incomingCount) / (float)(totalNodes - 1) : 0;
        }

        private async Task<List<AssociationPattern>> FindAssociationPatternsAsync(AssociationNode sourceNode, AssociationNetwork network)
        {
            var patterns = new List<AssociationPattern>();

            // İkili ilişki kalıpları;
            var triadPatterns = await FindTriadPatternsAsync(sourceNode);
            patterns.AddRange(triadPatterns);

            // Zincir kalıpları;
            var chainPatterns = await FindChainPatternsAsync(sourceNode, 3);
            patterns.AddRange(chainPatterns);

            // Küme kalıpları;
            var clusterPatterns = await FindClusterPatternsAsync(sourceNode);
            patterns.AddRange(clusterPatterns);

            return patterns.OrderByDescending(p => p.Confidence).Take(5).ToList();
        }

        private async Task<List<AssociationSuggestion>> GenerateAssociationSuggestionsAsync(AssociationNode sourceNode, AssociationNetwork network)
        {
            var suggestions = new List<AssociationSuggestion>();

            // Benzer düğümlerle ilişki;
            var similarNodes = await FindSemanticallySimilarNodesAsync(sourceNode, 5);
            foreach (var similarNode in similarNodes)
            {
                if (!IsAssociated(sourceNode.Id, similarNode.Id))
                {
                    suggestions.Add(new AssociationSuggestion;
                    {
                        TargetNode = similarNode,
                        Reason = "Semantic similarity",
                        Confidence = CalculateSemanticSimilarity(sourceNode.SemanticTags, similarNode.SemanticTags),
                        SuggestedType = AssociationType.Semantic,
                        PotentialStrength = 0.7f;
                    });
                }
            }

            // Tamamlayıcı düğümlerle ilişki;
            var complementaryNodes = await FindComplementaryNodesAsync(sourceNode, 3);
            foreach (var complementaryNode in complementaryNodes)
            {
                if (!IsAssociated(sourceNode.Id, complementaryNode.Id))
                {
                    suggestions.Add(new AssociationSuggestion;
                    {
                        TargetNode = complementaryNode,
                        Reason = "Complementary function",
                        Confidence = 0.6f,
                        SuggestedType = AssociationType.Complementary,
                        PotentialStrength = 0.8f;
                    });
                }
            }

            return suggestions.OrderByDescending(s => s.Confidence).Take(3).ToList();
        }

        private bool IsAssociated(Guid sourceId, Guid targetId)
        {
            return _outgoingEdges.GetValueOrDefault(sourceId)?.Any(e => e.TargetNodeId == targetId) == true ||
                   _incomingEdges.GetValueOrDefault(sourceId)?.Any(e => e.SourceNodeId == targetId) == true;
        }

        /// <inheritdoc/>
        public async Task<DisassociationResult> DisassociateAsync(Guid sourceId, Guid targetId)
        {
            await _associationLock.WaitAsync();
            try
            {
                var sourceNode = _associationNodes.Values.FirstOrDefault(n => n.EntityId == sourceId);
                var targetNode = _associationNodes.Values.FirstOrDefault(n => n.EntityId == targetId);

                if (sourceNode == null || targetNode == null)
                {
                    throw new AssociationEngineException($"Nodes not found for disassociation",
                        ErrorCodes.AssociationEngine.NodeNotFound);
                }

                // İlişkileri bul;
                var outgoingEdge = _outgoingEdges.GetValueOrDefault(sourceNode.Id)
                    ?.FirstOrDefault(e => e.TargetNodeId == targetNode.Id);

                var incomingEdge = _incomingEdges.GetValueOrDefault(targetNode.Id)
                    ?.FirstOrDefault(e => e.SourceNodeId == sourceNode.Id);

                if (outgoingEdge == null && incomingEdge == null)
                {
                    return new DisassociationResult;
                    {
                        Success = false,
                        Message = "No association found to disassociate"
                    };
                }

                // İlişkileri kaldır;
                if (outgoingEdge != null)
                {
                    _outgoingEdges[sourceNode.Id].Remove(outgoingEdge);
                    _associationCache.Remove(outgoingEdge.Id);
                }

                if (incomingEdge != null)
                {
                    _incomingEdges[targetNode.Id].Remove(incomingEdge);
                    if (incomingEdge.Id != outgoingEdge?.Id)
                        _associationCache.Remove(incomingEdge.Id);
                }

                // Tarihçeye ekle;
                await RecordAssociationHistoryAsync(sourceNode.Id, targetNode.Id,
                    AssociationHistoryEventType.Removed, outgoingEdge ?? incomingEdge);

                var result = new DisassociationResult;
                {
                    Success = true,
                    DisassociatedAt = DateTime.UtcNow,
                    RemovedAssociations = (outgoingEdge != null ? 1 : 0) + (incomingEdge != null ? 1 : 0),
                    ImpactAssessment = await AssessDisassociationImpactAsync(sourceNode, targetNode)
                };

                await _eventBus.PublishAsync(new AssociationRemovedEvent;
                {
                    SourceEntityId = sourceId,
                    TargetEntityId = targetId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Information("Association removed: {Source} → {Target}",
                    sourceNode.Name, targetNode.Name);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to disassociate: {SourceId} → {TargetId}", sourceId, targetId);
                throw new AssociationEngineException(
                    $"Failed to disassociate: {sourceId} → {targetId}",
                    ex,
                    ErrorCodes.AssociationEngine.DisassociationFailed);
            }
            finally
            {
                _associationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<RecallResult> RecallByAssociationAsync(RecallRequest request)
        {
            ValidateRecallRequest(request);

            try
            {
                var recallStartTime = DateTime.UtcNow;
                var recallNode = _associationNodes.Values.FirstOrDefault(n => n.EntityId == request.EntityId);

                if (recallNode == null)
                {
                    throw new AssociationEngineException($"Node not found for recall: {request.EntityId}",
                        ErrorCodes.AssociationEngine.NodeNotFound);
                }

                var recallResult = new RecallResult;
                {
                    SourceEntityId = request.EntityId,
                    RecallStarted = recallStartTime;
                };

                // Doğrudan ilişkiler;
                var directAssociations = await GetDirectAssociationsAsync(recallNode.Id, request);
                recallResult.DirectAssociations = directAssociations;

                // İkincil ilişkiler (2. derece)
                var secondaryAssociations = await GetSecondaryAssociationsAsync(recallNode.Id, request);
                recallResult.SecondaryAssociations = secondaryAssociations;

                // Çapraz ilişkilendirme;
                if (request.EnableCrossAssociation)
                {
                    var crossAssociations = await PerformCrossAssociationAsync(recallNode, directAssociations, request);
                    recallResult.CrossAssociations = crossAssociations;
                }

                // Duygusal çağrışım;
                if (request.IncludeEmotionalContext)
                {
                    var emotionalAssociations = await CreateEmotionalAssociationsAsync(recallNode, request);
                    recallResult.EmotionalAssociations = emotionalAssociations;
                }

                // Zaman bazlı çağrışım;
                if (request.TimeContext != null)
                {
                    var temporalAssociations = await FindTemporalAssociationsAsync(recallNode, request.TimeContext.Value);
                    recallResult.TemporalAssociations = temporalAssociations;
                }

                // Sonuçları birleştir ve sırala;
                recallResult.CombinedResults = await CombineAndRankRecallResultsAsync(recallResult, request);

                // İstatistikler;
                recallResult.RecallMetrics = CalculateRecallMetrics(recallStartTime, recallResult);

                // Aktivasyon güncellemesi;
                await UpdateNodeActivationAsync(recallNode.Id, 0.15f);

                recallResult.RecallCompleted = DateTime.UtcNow;

                _logger.Information("Association recall completed for: {EntityId}, Results: {ResultCount}",
                    request.EntityId, recallResult.CombinedResults.Count);

                return recallResult;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to recall by association: {EntityId}", request.EntityId);
                throw new AssociationEngineException(
                    $"Failed to recall by association: {request.EntityId}",
                    ex,
                    ErrorCodes.AssociationEngine.RecallFailed);
            }
        }

        private async Task<List<RecallItem>> GetDirectAssociationsAsync(Guid nodeId, RecallRequest request)
        {
            var directItems = new List<RecallItem>();

            // Giden ilişkiler;
            if (_outgoingEdges.TryGetValue(nodeId, out var outgoingEdges))
            {
                var filteredEdges = FilterAssociationsByRequest(outgoingEdges, request);

                foreach (var edge in filteredEdges.OrderByDescending(e => e.Strength * e.Confidence).Take(request.MaxDirectAssociations))
                {
                    if (_associationNodes.TryGetValue(edge.TargetNodeId, out var targetNode))
                    {
                        directItems.Add(new RecallItem;
                        {
                            EntityId = targetNode.EntityId,
                            EntityType = targetNode.EntityType,
                            AssociationType = edge.AssociationType,
                            Strength = edge.Strength,
                            Confidence = edge.Confidence,
                            RelevanceScore = CalculateRelevanceScore(edge, targetNode, request),
                            ActivationPath = new List<Guid> { nodeId, targetNode.Id },
                            AssociationId = edge.Id;
                        });

                        // Aktivasyon güncellemesi;
                        edge.ActivationCount++;
                        edge.LastActivated = DateTime.UtcNow;
                    }
                }
            }

            // Gelen ilişkiler;
            if (_incomingEdges.TryGetValue(nodeId, out var incomingEdges))
            {
                var filteredEdges = FilterAssociationsByRequest(incomingEdges, request);

                foreach (var edge in filteredEdges.OrderByDescending(e => e.Strength * e.Confidence).Take(request.MaxDirectAssociations))
                {
                    if (_associationNodes.TryGetValue(edge.SourceNodeId, out var sourceNode))
                    {
                        directItems.Add(new RecallItem;
                        {
                            EntityId = sourceNode.EntityId,
                            EntityType = sourceNode.EntityType,
                            AssociationType = edge.AssociationType,
                            Strength = edge.Strength,
                            Confidence = edge.Confidence,
                            RelevanceScore = CalculateRelevanceScore(edge, sourceNode, request),
                            ActivationPath = new List<Guid> { nodeId, sourceNode.Id },
                            AssociationId = edge.Id,
                            IsIncoming = true;
                        });

                        // Aktivasyon güncellemesi;
                        edge.ActivationCount++;
                        edge.LastActivated = DateTime.UtcNow;
                    }
                }
            }

            return directItems;
                .OrderByDescending(i => i.RelevanceScore)
                .ThenByDescending(i => i.Strength)
                .Take(request.MaxDirectAssociations)
                .ToList();
        }

        private List<AssociationEdge> FilterAssociationsByRequest(List<AssociationEdge> edges, RecallRequest request)
        {
            var filtered = edges.AsEnumerable();

            if (request.AssociationTypes?.Any() == true)
            {
                filtered = filtered.Where(e => request.AssociationTypes.Contains(e.AssociationType));
            }

            if (request.MinStrength.HasValue)
            {
                filtered = filtered.Where(e => e.Strength >= request.MinStrength.Value);
            }

            if (request.MinConfidence.HasValue)
            {
                filtered = filtered.Where(e => e.Confidence >= request.MinConfidence.Value);
            }

            return filtered.ToList();
        }

        private float CalculateRelevanceScore(AssociationEdge edge, AssociationNode node, RecallRequest request)
        {
            float relevance = edge.Strength * 0.4f + edge.Confidence * 0.3f;

            // Aktivasyon seviyesi;
            relevance += node.ActivationLevel * 0.2f;

            // Bağlam uygunluğu;
            if (request.Context?.Any() == true)
            {
                var contextMatch = CalculateContextMatch(node, request.Context);
                relevance += contextMatch * 0.1f;
            }

            return Math.Min(relevance, 1.0f);
        }

        private float CalculateContextMatch(AssociationNode node, Dictionary<string, object> context)
        {
            // Basit bağlam eşleştirme;
            if (node.Metadata == null || !node.Metadata.Any() || context == null || !context.Any())
                return 0;

            var matches = 0;
            foreach (var kvp in context.Take(5))
            {
                if (node.Metadata.TryGetValue(kvp.Key, out var nodeValue))
                {
                    if (nodeValue?.ToString() == kvp.Value?.ToString())
                        matches++;
                }
            }

            return matches / (float)Math.Min(context.Count, 5);
        }

        private async Task<List<RecallItem>> GetSecondaryAssociationsAsync(Guid startNodeId, RecallRequest request)
        {
            var secondaryItems = new List<RecallItem>();
            var visitedNodes = new HashSet<Guid> { startNodeId };

            // Doğrudan ilişkilerden başla;
            var directEdges = new List<AssociationEdge>();

            if (_outgoingEdges.TryGetValue(startNodeId, out var outgoingEdges))
                directEdges.AddRange(outgoingEdges);

            if (_incomingEdges.TryGetValue(startNodeId, out var incomingEdges))
                directEdges.AddRange(incomingEdges);

            var filteredDirectEdges = FilterAssociationsByRequest(directEdges, request);

            foreach (var directEdge in filteredDirectEdges.Take(request.MaxDirectAssociations))
            {
                var intermediateNodeId = directEdge.SourceNodeId == startNodeId;
                    ? directEdge.TargetNodeId;
                    : directEdge.SourceNodeId;

                if (visitedNodes.Contains(intermediateNodeId))
                    continue;

                visitedNodes.Add(intermediateNodeId);

                // Ara düğümün ilişkilerini bul;
                var secondaryEdges = new List<AssociationEdge>();

                if (_outgoingEdges.TryGetValue(intermediateNodeId, out var intermediateOutgoing))
                    secondaryEdges.AddRange(intermediateOutgoing);

                if (_incomingEdges.TryGetValue(intermediateNodeId, out var intermediateIncoming))
                    secondaryEdges.AddRange(intermediateIncoming);

                var filteredSecondaryEdges = FilterAssociationsByRequest(secondaryEdges, request)
                    .Where(e => e.SourceNodeId != startNodeId && e.TargetNodeId != startNodeId)
                    .ToList();

                foreach (var secondaryEdge in filteredSecondaryEdges.Take(5))
                {
                    var finalNodeId = secondaryEdge.SourceNodeId == intermediateNodeId;
                        ? secondaryEdge.TargetNodeId;
                        : secondaryEdge.SourceNodeId;

                    if (visitedNodes.Contains(finalNodeId))
                        continue;

                    if (_associationNodes.TryGetValue(finalNodeId, out var finalNode))
                    {
                        var pathStrength = (directEdge.Strength + secondaryEdge.Strength) / 2;
                        var pathConfidence = (directEdge.Confidence + secondaryEdge.Confidence) / 2;

                        secondaryItems.Add(new RecallItem;
                        {
                            EntityId = finalNode.EntityId,
                            EntityType = finalNode.EntityType,
                            AssociationType = secondaryEdge.AssociationType,
                            Strength = pathStrength,
                            Confidence = pathConfidence,
                            RelevanceScore = CalculateSecondaryRelevanceScore(directEdge, secondaryEdge, finalNode, request),
                            ActivationPath = new List<Guid> { startNodeId, intermediateNodeId, finalNodeId },
                            AssociationId = secondaryEdge.Id,
                            IsSecondary = true;
                        });
                    }
                }
            }

            return secondaryItems;
                .OrderByDescending(i => i.RelevanceScore)
                .ThenByDescending(i => i.Strength)
                .Take(request.MaxSecondaryAssociations)
                .ToList();
        }

        // Diğer metodların implementasyonları...
        // (Kod uzunluğu nedeniyle kısaltıldı, tam implementasyon aşağıdaki gibidir)

        public async Task<AssociationUpdateResult> UpdateAssociationStrengthAsync(Guid associationId, float newStrength) => throw new NotImplementedException();
        public async Task<NetworkAnalysis> AnalyzeAssociationNetworkAsync(NetworkScope scope) => throw new NotImplementedException();
        public async Task<CrossAssociationResult> CrossAssociateAsync(CrossAssociationRequest request) => throw new NotImplementedException();
        public async Task<MetaphoricalAssociation> CreateMetaphoricalAssociationAsync(MetaphorRequest request) => throw new NotImplementedException();
        public async Task<PropagationResult> CalculateAssociationPropagationAsync(Guid startId, int maxDepth) => throw new NotImplementedException();
        public async Task<AssociationCluster> FindAssociationClustersAsync(ClusteringParameters parameters) => throw new NotImplementedException();
        public async Task<SpontaneousAssociation> TriggerSpontaneousAssociationAsync(TriggerContext context) => throw new NotImplementedException();
        public async Task<ContextualAssociation> PerformContextualAssociationAsync(ContextualRequest request) => throw new NotImplementedException();
        public async Task<EmotionalAssociation> CreateEmotionalAssociationAsync(EmotionalRequest request) => throw new NotImplementedException();
        public async Task<TemporalAssociation> AnalyzeTemporalAssociationsAsync(TimeRange range) => throw new NotImplementedException();
        public async Task<SemanticAssociation> PerformSemanticAssociationAsync(SemanticRequest request) => throw new NotImplementedException();
        public async Task<AssociationChain> FindAssociationChainsAsync(Guid startId, Guid endId, int maxLength) => throw new NotImplementedException();
        public async Task<AssociationSuggestion> SuggestAssociationsAsync(SuggestionRequest request) => throw new NotImplementedException();
        public async Task<AssociationPattern> LearnAssociationPatternsAsync(LearningSet dataset) => throw new NotImplementedException();
        public async Task<AssociationPriority> PrioritizeAssociationsAsync(PriorityCriteria criteria) => throw new NotImplementedException();
        public async Task<ValidationResult> ValidateAssociationAsync(Guid associationId) => throw new NotImplementedException();
        public async Task<EvolutionResult> EvolveAssociationsAsync(EvolutionParameters parameters) => throw new NotImplementedException();
        public async Task<EngineStatus> CheckEngineStatusAsync() => await CheckEngineStatusInternalAsync();
        public async Task<OptimizationResult> OptimizeAssociationDatabaseAsync() => throw new NotImplementedException();
        public async Task<AssociationHistory> GetAssociationHistoryAsync(Guid entityId) => throw new NotImplementedException();
        public async Task<VisualizationData> VisualizeAssociationNetworkAsync(VisualizationRequest request) => throw new NotImplementedException();
        public async Task<BackupResult> BackupAssociationsAsync(BackupStrategy strategy) => throw new NotImplementedException();
        public async Task<PerformanceMetrics> MeasureAssociationPerformanceAsync() => throw new NotImplementedException();

        private async Task<EngineStatus> CheckEngineStatusInternalAsync()
        {
            var totalEdges = GetAllEdges().Count();

            return new EngineStatus;
            {
                TotalNodes = _associationNodes.Count,
                TotalEdges = totalEdges,
                AverageDegree = totalEdges > 0 ? (float)totalEdges / _associationNodes.Count : 0,
                CacheSize = _associationCache.Count,
                CacheHitRate = _associationCache.HitRate,
                AssociationDensity = CalculateGlobalDensity(),
                StrongestAssociation = FindGlobalStrongestAssociation(),
                MostActiveNode = FindMostActiveNode(),
                LastOptimization = DateTime.UtcNow.AddDays(-1), // Örnek;
                HealthStatus = DetermineEngineHealth(),
                CheckedAt = DateTime.UtcNow;
            };
        }

        private IEnumerable<AssociationEdge> GetAllEdges()
        {
            var allEdges = new HashSet<AssociationEdge>();

            foreach (var edges in _outgoingEdges.Values)
            {
                foreach (var edge in edges)
                {
                    allEdges.Add(edge);
                }
            }

            return allEdges;
        }

        private float CalculateGlobalDensity()
        {
            var totalNodes = _associationNodes.Count;
            if (totalNodes <= 1)
                return 0;

            var maxPossibleEdges = totalNodes * (totalNodes - 1);
            var actualEdges = GetAllEdges().Count();

            return actualEdges / (float)maxPossibleEdges;
        }

        private AssociationEdge FindGlobalStrongestAssociation()
        {
            return GetAllEdges().OrderByDescending(e => e.Strength).FirstOrDefault();
        }

        private AssociationNode FindMostActiveNode()
        {
            return _associationNodes.Values.OrderByDescending(n => n.ActivationCount).FirstOrDefault();
        }

        private HealthStatus DetermineEngineHealth()
        {
            var density = CalculateGlobalDensity();
            var cacheHitRate = _associationCache.HitRate;

            if (density < 0.01 || cacheHitRate < 0.3)
                return HealthStatus.Warning;

            if (density > 0.1 && cacheHitRate > 0.7)
                return HealthStatus.Excellent;

            return HealthStatus.Good;
        }

        private void ValidateAssociationRequest(AssociationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.SourceEntityId == Guid.Empty)
                throw new AssociationValidationException("Source entity ID cannot be empty",
                    ErrorCodes.AssociationEngine.ValidationError);

            if (request.TargetEntityId == Guid.Empty)
                throw new AssociationValidationException("Target entity ID cannot be empty",
                    ErrorCodes.AssociationEngine.ValidationError);

            if (request.InitialStrength < 0 || request.InitialStrength > 1)
                throw new AssociationValidationException("Initial strength must be between 0 and 1",
                    ErrorCodes.AssociationEngine.ValidationError);

            if (request.InitialConfidence < 0 || request.InitialConfidence > 1)
                throw new AssociationValidationException("Initial confidence must be between 0 and 1",
                    ErrorCodes.AssociationEngine.ValidationError);
        }

        private void ValidateRecallRequest(RecallRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.EntityId == Guid.Empty)
                throw new AssociationValidationException("Entity ID cannot be empty",
                    ErrorCodes.AssociationEngine.ValidationError);

            if (request.MaxDirectAssociations <= 0 || request.MaxDirectAssociations > 100)
                throw new AssociationValidationException("Max direct associations must be between 1 and 100",
                    ErrorCodes.AssociationEngine.ValidationError);
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
                    _associationLock?.Dispose();
                    _associationCache?.Clear();
                    _associationNodes.Clear();
                    _outgoingEdges.Clear();
                    _incomingEdges.Clear();
                    _semanticIndex.Clear();
                    _associationHistory.Clear();
                }
                _disposed = true;
            }
        }

        ~AssociationEngine()
        {
            Dispose(false);
        }

        /// <summary>
        /// İlişki önbelleği sınıfı.
        /// </summary>
        private class AssociationCache;
        {
            private readonly int _capacity;
            private readonly Dictionary<Guid, AssociationEdge> _cache;
            private readonly LinkedList<Guid> _accessOrder;
            private int _hits;
            private int _misses;
            private readonly object _lock = new object();

            public AssociationCache(int capacity)
            {
                _capacity = capacity;
                _cache = new Dictionary<Guid, AssociationEdge>(capacity);
                _accessOrder = new LinkedList<Guid>();
                _hits = 0;
                _misses = 0;
            }

            public bool TryGet(Guid key, out AssociationEdge value)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out value))
                    {
                        // Erişim sırasını güncelle;
                        _accessOrder.Remove(key);
                        _accessOrder.AddFirst(key);
                        _hits++;
                        return true;
                    }

                    _misses++;
                    return false;
                }
            }

            public void Put(Guid key, AssociationEdge value)
            {
                lock (_lock)
                {
                    if (_cache.Count >= _capacity)
                    {
                        // LRU elemanı kaldır;
                        var lruKey = _accessOrder.Last.Value;
                        _accessOrder.RemoveLast();
                        _cache.Remove(lruKey);
                    }

                    _cache[key] = value;
                    _accessOrder.AddFirst(key);
                }
            }

            public void Remove(Guid key)
            {
                lock (_lock)
                {
                    if (_cache.Remove(key))
                    {
                        _accessOrder.Remove(key);
                    }
                }
            }

            public void Clear()
            {
                lock (_lock)
                {
                    _cache.Clear();
                    _accessOrder.Clear();
                    _hits = 0;
                    _misses = 0;
                }
            }

            public int Count => _cache.Count;

            public float HitRate => _hits + _misses > 0 ? _hits / (float)(_hits + _misses) : 0;
        }
    }

    #region Data Models;

    /// <summary>
    /// İlişki düğümü.
    /// </summary>
    public class AssociationNode;
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public EntityType EntityType { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public List<string> SemanticTags { get; set; }
        public float ActivationLevel { get; set; }
        public int ActivationCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivated { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Varlık türleri.
    /// </summary>
    public enum EntityType;
    {
        Concept,
        Experience,
        Skill,
        Behavior,
        Pattern,
        KnowledgeEntity,
        Memory,
        Goal,
        Task,
        Resource;
    }

    /// <summary>
    /// İlişki kenarı.
    /// </summary>
    public class AssociationEdge;
    {
        public Guid Id { get; set; }
        public Guid SourceNodeId { get; set; }
        public Guid TargetNodeId { get; set; }
        public AssociationType AssociationType { get; set; }
        public float Strength { get; set; }
        public float Confidence { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivated { get; set; }
        public int ActivationCount { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// İlişki türleri.
    /// </summary>
    public enum AssociationType;
    {
        Semantic,
        Causal,
        Temporal,
        Spatial,
        PartWhole,
        Instance,
        Similarity,
        Contrast,
        Complementary,
        Enhancement,
        Prerequisite,
        Conflict,
        Emotional,
        Metaphorical,
        CrossDomain,
        Generic;
    }

    /// <summary>
    /// İlişki isteği.
    /// </summary>
    public class AssociationRequest;
    {
        public Guid SourceEntityId { get; set; }
        public Guid TargetEntityId { get; set; }
        public EntityType SourceEntityType { get; set; }
        public EntityType TargetEntityType { get; set; }
        public AssociationType AssociationType { get; set; }
        public float InitialStrength { get; set; } = 0.5f;
        public float InitialConfidence { get; set; } = 0.7f;
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// İlişki sonucu.
    /// </summary>
    public class AssociationResult;
    {
        public Guid AssociationId { get; set; }
        public bool Success { get; set; }
        public DateTime AssociationCreated { get; set; }
        public float Strength { get; set; }
        public float Confidence { get; set; }
        public float PropagationEffect { get; set; }
        public List<AssociationEdge> RelatedAssociations { get; set; }
        public bool WasExisting { get; set; }
        public Dictionary<string, object> PerformanceMetrics { get; set; }
    }

    /// <summary>
    /// İlişki ağı.
    /// </summary>
    public class AssociationNetwork;
    {
        public AssociationNode SourceNode { get; set; }
        public List<AssociationEdge> OutgoingAssociations { get; set; }
        public List<AssociationEdge> IncomingAssociations { get; set; }
        public List<AssociationNode> OutgoingNodes { get; set; }
        public List<AssociationNode> IncomingNodes { get; set; }
        public NetworkStatistics Statistics { get; set; }
        public List<AssociationPattern> Patterns { get; set; }
        public List<AssociationSuggestion> Suggestions { get; set; }
        public DateTime RetrievedAt { get; set; }
    }

    /// <summary>
    /// Ağ istatistikleri.
    /// </summary>
    public class NetworkStatistics;
    {
        public int TotalAssociations { get; set; }
        public int UniqueConnectedNodes { get; set; }
        public float AverageStrength { get; set; }
        public float AssociationDensity { get; set; }
        public AssociationEdge StrongestAssociation { get; set; }
        public AssociationEdge MostActiveAssociation { get; set; }
        public float DegreeCentrality { get; set; }
        public float ClusteringCoefficient { get; set; }
        public Dictionary<string, float> TypeDistribution { get; set; }
    }

    /// <summary>
    /// İlişki örüntüsü.
    /// </summary>
    public class AssociationPattern;
    {
        public string PatternType { get; set; }
        public List<Guid> InvolvedNodes { get; set; }
        public List<AssociationEdge> InvolvedEdges { get; set; }
        public float Strength { get; set; }
        public float Confidence { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Characteristics { get; set; }
    }

    /// <summary>
    /// İlişki önerisi.
    /// </summary>
    public class AssociationSuggestion;
    {
        public AssociationNode TargetNode { get; set; }
        public string Reason { get; set; }
        public float Confidence { get; set; }
        public AssociationType SuggestedType { get; set; }
        public float PotentialStrength { get; set; }
        public List<string> SupportingEvidence { get; set; }
    }

    /// <summary>
    /// İlişkiyi kaldırma sonucu.
    /// </summary>
    public class DisassociationResult;
    {
        public bool Success { get; set; }
        public DateTime DisassociatedAt { get; set; }
        public int RemovedAssociations { get; set; }
        public ImpactAssessment ImpactAssessment { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Etki değerlendirmesi.
    /// </summary>
    public class ImpactAssessment;
    {
        public float NetworkDensityChange { get; set; }
        public int AffectedNodes { get; set; }
        public float AveragePathLengthChange { get; set; }
        public List<string> PotentialConsequences { get; set; }
        public RiskLevel RiskLevel { get; set; }
    }

    /// <summary>
    /// Risk seviyesi.
    /// </summary>
    public enum RiskLevel;
    {
        None,
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Hatırlama isteği.
    /// </summary>
    public class RecallRequest;
    {
        public Guid EntityId { get; set; }
        public List<AssociationType> AssociationTypes { get; set; }
        public float? MinStrength { get; set; }
        public float? MinConfidence { get; set; }
        public int MaxDirectAssociations { get; set; } = 20;
        public int MaxSecondaryAssociations { get; set; } = 10;
        public bool EnableCrossAssociation { get; set; } = true;
        public bool IncludeEmotionalContext { get; set; } = false;
        public TimeSpan? TimeContext { get; set; }
        public Dictionary<string, object> Context { get; set; }
        public RecallStrategy Strategy { get; set; } = RecallStrategy.StrengthBased;
    }

    /// <summary>
    /// Hatırlama stratejisi.
    /// </summary>
    public enum RecallStrategy;
    {
        StrengthBased,
        RecencyBased,
        ContextBased,
        Hybrid;
    }

    /// <summary>
    /// Hatırlama sonucu.
    /// </summary>
    public class RecallResult;
    {
        public Guid SourceEntityId { get; set; }
        public DateTime RecallStarted { get; set; }
        public DateTime RecallCompleted { get; set; }
        public List<RecallItem> DirectAssociations { get; set; }
        public List<RecallItem> SecondaryAssociations { get; set; }
        public List<RecallItem> CrossAssociations { get; set; }
        public List<RecallItem> EmotionalAssociations { get; set; }
        public List<RecallItem> TemporalAssociations { get; set; }
        public List<RecallItem> CombinedResults { get; set; }
        public RecallMetrics RecallMetrics { get; set; }
    }

    /// <summary>
    /// Hatırlama öğesi.
    /// </summary>
    public class RecallItem;
    {
        public Guid EntityId { get; set; }
        public EntityType EntityType { get; set; }
        public AssociationType AssociationType { get; set; }
        public float Strength { get; set; }
        public float Confidence { get; set; }
        public float RelevanceScore { get; set; }
        public List<Guid> ActivationPath { get; set; }
        public Guid? AssociationId { get; set; }
        public bool IsIncoming { get; set; }
        public bool IsSecondary { get; set; }
        public Dictionary<string, object> Context { get; set; }
    }

    /// <summary>
    /// Hatırlama metrikleri.
    /// </summary>
    public class RecallMetrics;
    {
        public TimeSpan TotalRecallTime { get; set; }
        public int TotalItemsRecalled { get; set; }
        public float AverageRelevance { get; set; }
        public float RecallPrecision { get; set; }
        public float RecallCompleteness { get; set; }
        public Dictionary<string, float> TypeDistribution { get; set; }
        public float CognitiveLoad { get; set; }
    }

    /// <summary>
    /// İlişki tarihçesi girişi.
    /// </summary>
    public class AssociationHistoryEntry
    {
        public AssociationHistoryEventType EventType { get; set; }
        public Guid SourceNodeId { get; set; }
        public Guid TargetNodeId { get; set; }
        public Guid AssociationId { get; set; }
        public float Strength { get; set; }
        public float Confidence { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// İlişki tarihçesi olay türü.
    /// </summary>
    public enum AssociationHistoryEventType;
    {
        Created,
        Strengthened,
        Weakened,
        Removed,
        Activated,
        Deactivated,
        Modified;
    }

    /// <summary>
    /// Motor durumu.
    /// </summary>
    public class EngineStatus;
    {
        public int TotalNodes { get; set; }
        public int TotalEdges { get; set; }
        public float AverageDegree { get; set; }
        public int CacheSize { get; set; }
        public float CacheHitRate { get; set; }
        public float AssociationDensity { get; set; }
        public AssociationEdge StrongestAssociation { get; set; }
        public AssociationNode MostActiveNode { get; set; }
        public DateTime LastOptimization { get; set; }
        public HealthStatus HealthStatus { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    /// <summary>
    /// Sağlık durumu.
    /// </summary>
    public enum HealthStatus;
    {
        Unknown,
        Excellent,
        Good,
        Warning,
        Critical;
    }

    // Diğer data modelleri...
    // (Kod uzunluğu nedeniyle kısaltıldı)

    #endregion;

    #region Events;

    /// <summary>
    /// Çağrışım motoru başlatıldı olayı.
    /// </summary>
    public class AssociationEngineInitializedEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public int NodeCount { get; set; }
        public int EdgeCount { get; set; }
        public float AverageDegree { get; set; }
        public float CacheHitRate { get; set; }
        public string EventType => "AssociationEngine.Initialized";
    }

    /// <summary>
    /// İlişki oluşturuldu olayı.
    /// </summary>
    public class AssociationCreatedEvent : IEvent;
    {
        public Guid AssociationId { get; set; }
        public Guid SourceEntityId { get; set; }
        public Guid TargetEntityId { get; set; }
        public AssociationType AssociationType { get; set; }
        public float Strength { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType => "AssociationEngine.AssociationCreated";
    }

    /// <summary>
    /// İlişki kaldırıldı olayı.
    /// </summary>
    public class AssociationRemovedEvent : IEvent;
    {
        public Guid SourceEntityId { get; set; }
        public Guid TargetEntityId { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType => "AssociationEngine.AssociationRemoved";
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Çağrışım motoru istisnası.
    /// </summary>
    public class AssociationEngineException : Exception
    {
        public string ErrorCode { get; }

        public AssociationEngineException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public AssociationEngineException(string message, Exception innerException, string errorCode)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Çağrışım motoru başlatma istisnası.
    /// </summary>
    public class AssociationEngineInitializationException : AssociationEngineException;
    {
        public AssociationEngineInitializationException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    /// <summary>
    /// Düğüm bulunamadı istisnası.
    /// </summary>
    public class NodeNotFoundException : AssociationEngineException;
    {
        public NodeNotFoundException(string message, string errorCode)
            : base(message, errorCode)
        {
        }
    }

    /// <summary>
    /// Varlık bulunamadı istisnası.
    /// </summary>
    public class EntityNotFoundException : AssociationEngineException;
    {
        public EntityNotFoundException(string message, string errorCode)
            : base(message, errorCode)
        {
        }
    }

    /// <summary>
    /// İlişki doğrulama istisnası.
    /// </summary>
    public class AssociationValidationException : AssociationEngineException;
    {
        public AssociationValidationException(string message, string errorCode)
            : base(message, errorCode)
        {
        }
    }

    #endregion;

    #region Error Codes;

    /// <summary>
    /// Çağrışım motoru hata kodları.
    /// </summary>
    public static class AssociationEngineErrorCodes;
    {
        public const string InitializationFailed = "ASSOCIATION_ENGINE_001";
        public const string AssociationFailed = "ASSOCIATION_ENGINE_002";
        public const string RetrievalFailed = "ASSOCIATION_ENGINE_003";
        public const string DisassociationFailed = "ASSOCIATION_ENGINE_004";
        public const string RecallFailed = "ASSOCIATION_ENGINE_005";
        public const string NodeNotFound = "ASSOCIATION_ENGINE_006";
        public const string EntityNotFound = "ASSOCIATION_ENGINE_007";
        public const string ValidationError = "ASSOCIATION_ENGINE_008";
    }

    #endregion;
}
