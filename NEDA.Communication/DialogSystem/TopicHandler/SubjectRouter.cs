using Microsoft.Extensions.Logging;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NLP_Engine;
using NEDA.Communication.DialogSystem.QuestionAnswering;
using NEDA.Communication.DialogSystem.TopicHandler;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.Communication.DialogSystem.TopicHandler;
{
    /// <summary>
    /// Konu yönlendirme sistemi için ana interface;
    /// Gelen mesajları analiz eder ve uygun konu handler'lara yönlendirir;
    /// </summary>
    public interface ISubjectRouter : IDisposable
    {
        /// <summary>
        /// Mesajı analiz eder ve uygun konuya yönlendirir;
        /// </summary>
        Task<RoutingResult> RouteMessageAsync(RoutingRequest request);

        /// <summary>
        /// Konu geçişini yönetir;
        /// </summary>
        Task<TopicTransitionResult> TransitionTopicAsync(TopicTransitionRequest request);

        /// <summary>
        /// Çoklu mesajları toplu olarak yönlendirir;
        /// </summary>
        Task<IEnumerable<RoutingResult>> RouteBatchMessagesAsync(IEnumerable<RoutingRequest> requests);

        /// <summary>
        /// Konu bağımlılıklarını analiz eder;
        /// </summary>
        Task<TopicDependencyAnalysis> AnalyzeDependenciesAsync(string topic);

        /// <summary>
        /// Konu önceliklerini günceller;
        /// </summary>
        Task UpdateTopicPrioritiesAsync(TopicPriorityUpdate update);

        /// <summary>
        /// Router durumunu kontrol eder;
        /// </summary>
        Task<RouterHealth> CheckHealthAsync();

        /// <summary>
        /// Konu haritasını getirir;
        /// </summary>
        Task<TopicMap> GetTopicMapAsync(TopicMapRequest request);

        /// <summary>
        /// Konu istatistiklerini getirir;
        /// </summary>
        Task<RouterStatistics> GetStatisticsAsync();
    }

    /// <summary>
    /// Konu yönlendirme sistemi implementasyonu;
    /// Akıllı konu tanıma ve yönlendirme sağlar;
    /// </summary>
    public class SubjectRouter : ISubjectRouter;
    {
        private readonly ILogger<SubjectRouter> _logger;
        private readonly INLPEngine _nlpEngine;
        private readonly ITopicManager _topicManager;
        private readonly IContextSwitcher _contextSwitcher;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly IMemoryRecall _memoryRecall;
        private readonly ITopicRegistry _topicRegistry
        private readonly IRoutingCache _routingCache;
        private readonly IRoutingStrategy _routingStrategy;
        private bool _disposed;
        private readonly RouterMetrics _metrics;

        /// <summary>
        /// SubjectRouter constructor;
        /// </summary>
        public SubjectRouter(
            ILogger<SubjectRouter> logger,
            INLPEngine nlpEngine,
            ITopicManager topicManager,
            IContextSwitcher contextSwitcher,
            IKnowledgeGraph knowledgeGraph,
            IMemoryRecall memoryRecall,
            ITopicRegistry topicRegistry,
            IRoutingCache routingCache,
            IRoutingStrategy routingStrategy)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _nlpEngine = nlpEngine ?? throw new ArgumentNullException(nameof(nlpEngine));
            _topicManager = topicManager ?? throw new ArgumentNullException(nameof(topicManager));
            _contextSwitcher = contextSwitcher ?? throw new ArgumentNullException(nameof(contextSwitcher));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _memoryRecall = memoryRecall ?? throw new ArgumentNullException(nameof(memoryRecall));
            _topicRegistry = topicRegistry ?? throw new ArgumentNullException(nameof(topicRegistry));
            _routingCache = routingCache ?? throw new ArgumentNullException(nameof(routingCache));
            _routingStrategy = routingStrategy ?? throw new ArgumentNullException(nameof(routingStrategy));

            _metrics = new RouterMetrics();
            _logger.LogInformation("SubjectRouter initialized with {TopicCount} registered topics",
                _topicRegistry.GetTopicCount());
        }

        /// <summary>
        /// Mesajı yönlendirir;
        /// </summary>
        public async Task<RoutingResult> RouteMessageAsync(RoutingRequest request)
        {
            try
            {
                ValidateRoutingRequest(request);
                _metrics.IncrementTotalRequests();

                _logger.LogDebug("Routing message: {MessageId}", request.MessageId);

                // 1. Cache kontrolü;
                var cachedResult = await _routingCache.GetAsync(request.MessageHash);
                if (cachedResult != null)
                {
                    _metrics.IncrementCacheHits();
                    _logger.LogDebug("Cache hit for message: {MessageId}", request.MessageId);
                    return cachedResult;
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 2. Mesajı analiz et;
                var analysis = await AnalyzeMessageAsync(request);

                // 3. Konuları tespit et;
                var detectedTopics = await DetectTopicsAsync(analysis);

                // 4. Önceliklendir;
                var prioritizedTopics = await PrioritizeTopicsAsync(detectedTopics, request.Context);

                // 5. Routing stratejisini uygula;
                var routingDecision = await ApplyRoutingStrategyAsync(prioritizedTopics, request);

                // 6. Context güncelle;
                await UpdateContextAsync(request, routingDecision);

                // 7. Sonucu oluştur;
                var result = CreateRoutingResult(request, analysis, routingDecision);

                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;
                _metrics.RecordProcessingTime(stopwatch.Elapsed);

                // 8. Cache'e ekle;
                await _routingCache.SetAsync(request.MessageHash, result, TimeSpan.FromMinutes(30));

                LogRoutingDecision(request, result);

                return result;
            }
            catch (Exception ex)
            {
                _metrics.IncrementErrors();
                _logger.LogError(ex, "Error routing message: {MessageId}", request.MessageId);
                return await CreateErrorRoutingResultAsync(request, ex);
            }
        }

        /// <summary>
        /// Konu geçişini yönetir;
        /// </summary>
        public async Task<TopicTransitionResult> TransitionTopicAsync(TopicTransitionRequest request)
        {
            try
            {
                ValidateTransitionRequest(request);

                _logger.LogInformation("Transitioning from topic {FromTopic} to {ToTopic}",
                    request.CurrentTopic, request.TargetTopic);

                // 1. Geçiş uygunluğunu kontrol et;
                var transitionValidation = await ValidateTransitionAsync(request);
                if (!transitionValidation.IsValid)
                {
                    return new TopicTransitionResult;
                    {
                        Success = false,
                        Error = transitionValidation.Error,
                        SuggestedAlternative = transitionValidation.SuggestedAlternative;
                    };
                }

                // 2. Context switch hazırlığı;
                await PrepareContextSwitchAsync(request);

                // 3. Geçiş stratejisini uygula;
                var transitionStrategy = await DetermineTransitionStrategyAsync(request);

                // 4. Geçişi gerçekleştir;
                var transitionResult = await ExecuteTransitionAsync(request, transitionStrategy);

                // 5. Geçişi kaydet;
                await LogTransitionAsync(request, transitionResult);

                _logger.LogInformation("Topic transition completed successfully");

                return transitionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during topic transition");
                throw new TopicTransitionException("Topic transition failed", ex);
            }
        }

        /// <summary>
        /// Toplu mesaj yönlendirme;
        /// </summary>
        public async Task<IEnumerable<RoutingResult>> RouteBatchMessagesAsync(IEnumerable<RoutingRequest> requests)
        {
            var batchResults = new List<RoutingResult>();
            var tasks = new List<Task<RoutingResult>>();

            foreach (var request in requests)
            {
                tasks.Add(RouteMessageAsync(request));
            }

            var results = await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// Konu bağımlılıklarını analiz eder;
        /// </summary>
        public async Task<TopicDependencyAnalysis> AnalyzeDependenciesAsync(string topic)
        {
            try
            {
                _logger.LogDebug("Analyzing dependencies for topic: {Topic}", topic);

                var analysis = new TopicDependencyAnalysis;
                {
                    Topic = topic,
                    Timestamp = DateTime.UtcNow;
                };

                // 1. Prerequisite konuları bul;
                analysis.Prerequisites = await FindPrerequisitesAsync(topic);

                // 2. Dependent konuları bul;
                analysis.Dependents = await FindDependentsAsync(topic);

                // 3. İlgili konuları bul;
                analysis.RelatedTopics = await FindRelatedTopicsAsync(topic);

                // 4. Conflict konuları bul;
                analysis.Conflicts = await FindConflictsAsync(topic);

                // 5. Dependency graph oluştur;
                analysis.DependencyGraph = await BuildDependencyGraphAsync(topic);

                // 6. Analiz sonuçlarını hesapla;
                analysis.DependencyComplexity = CalculateComplexity(analysis);
                analysis.StrongestDependency = FindStrongestDependency(analysis);

                _logger.LogDebug("Dependency analysis completed for topic: {Topic}", topic);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing dependencies for topic: {Topic}", topic);
                throw new DependencyAnalysisException($"Failed to analyze dependencies for topic: {topic}", ex);
            }
        }

        /// <summary>
        /// Konu önceliklerini günceller;
        /// </summary>
        public async Task UpdateTopicPrioritiesAsync(TopicPriorityUpdate update)
        {
            try
            {
                ValidatePriorityUpdate(update);

                _logger.LogInformation("Updating priorities for {TopicCount} topics", update.TopicUpdates.Count);

                foreach (var topicUpdate in update.TopicUpdates)
                {
                    try
                    {
                        // Mevcut önceliği al;
                        var currentPriority = await _topicManager.GetTopicPriorityAsync(topicUpdate.TopicId);

                        // Yeni önceliği hesapla;
                        var newPriority = CalculateNewPriority(currentPriority, topicUpdate);

                        // Önceliği güncelle;
                        await _topicManager.UpdateTopicPriorityAsync(topicUpdate.TopicId, newPriority);

                        // Cache'i temizle;
                        await _routingCache.InvalidateTopicAsync(topicUpdate.TopicId);

                        _logger.LogDebug("Updated priority for topic {TopicId}: {OldPriority} -> {NewPriority}",
                            topicUpdate.TopicId, currentPriority, newPriority);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update priority for topic: {TopicId}", topicUpdate.TopicId);
                    }
                }

                // Global öncelikleri yeniden hesapla;
                await RecalculateGlobalPrioritiesAsync();

                _logger.LogInformation("Topic priorities updated successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating topic priorities");
                throw new PriorityUpdateException("Failed to update topic priorities", ex);
            }
        }

        /// <summary>
        /// Router sağlık kontrolü;
        /// </summary>
        public async Task<RouterHealth> CheckHealthAsync()
        {
            var healthChecks = new List<ComponentHealth>();

            try
            {
                // NLP Engine sağlık kontrolü;
                healthChecks.Add(await CheckNLPEngineHealthAsync());

                // Topic Manager sağlık kontrolü;
                healthChecks.Add(await CheckTopicManagerHealthAsync());

                // Knowledge Graph sağlık kontrolü;
                healthChecks.Add(await CheckKnowledgeGraphHealthAsync());

                // Cache sağlık kontrolü;
                healthChecks.Add(await CheckCacheHealthAsync());

                // Registry sağlık kontrolü;
                healthChecks.Add(await CheckRegistryHealthAsync());

                var overallHealth = DetermineOverallHealth(healthChecks);

                return new RouterHealth;
                {
                    Status = overallHealth,
                    ComponentHealth = healthChecks,
                    Metrics = _metrics.GetCurrentMetrics(),
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return new RouterHealth;
                {
                    Status = HealthStatus.Critical,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Konu haritasını getirir;
        /// </summary>
        public async Task<TopicMap> GetTopicMapAsync(TopicMapRequest request)
        {
            try
            {
                _logger.LogDebug("Generating topic map for scope: {Scope}", request.Scope);

                var topicMap = new TopicMap;
                {
                    Scope = request.Scope,
                    GeneratedAt = DateTime.UtcNow;
                };

                // 1. Temel konuları al;
                topicMap.BaseTopics = await GetBaseTopicsAsync(request);

                // 2. Hiyerarşiyi oluştur;
                topicMap.Hierarchy = await BuildTopicHierarchyAsync(topicMap.BaseTopics, request.Depth);

                // 3. İlişkileri ekle;
                topicMap.Relationships = await ExtractTopicRelationshipsAsync(topicMap.Hierarchy);

                // 4. Yoğunluk analizi;
                topicMap.DensityAnalysis = await AnalyzeTopicDensityAsync(topicMap.Hierarchy);

                // 5. Önemli konuları belirle;
                topicMap.KeyTopics = await IdentifyKeyTopicsAsync(topicMap);

                // 6. Görselleştirme verilerini hazırla;
                if (request.IncludeVisualizationData)
                {
                    topicMap.VisualizationData = await PrepareVisualizationDataAsync(topicMap);
                }

                _logger.LogDebug("Topic map generated with {TopicCount} topics",
                    topicMap.Hierarchy.TotalTopicCount);

                return topicMap;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating topic map");
                throw new TopicMapException("Failed to generate topic map", ex);
            }
        }

        /// <summary>
        Router istatistiklerini getirir;
        /// </summary>
        public async Task<RouterStatistics> GetStatisticsAsync()
        {
            var metrics = _metrics.GetCurrentMetrics();
            var cacheStats = await _routingCache.GetStatisticsAsync();
            var topicStats = await _topicManager.GetStatisticsAsync();

            return new RouterStatistics;
            {
                Metrics = metrics,
                CacheStatistics = cacheStats,
                TopicStatistics = topicStats,
                Uptime = DateTime.UtcNow - _metrics.StartTime,
                GeneratedAt = DateTime.UtcNow;
            };
        }

        #region Private Implementation Methods;

        /// <summary>
        /// Mesajı analiz eder;
        /// </summary>
        private async Task<MessageAnalysis> AnalyzeMessageAsync(RoutingRequest request)
        {
            var analysis = await _nlpEngine.AnalyzeAsync(request.Message, request.Context);

            return new MessageAnalysis;
            {
                MessageId = request.MessageId,
                Text = request.Message,
                Tokens = analysis.Tokens,
                Entities = analysis.Entities,
                Intent = analysis.Intent,
                Sentiment = analysis.Sentiment,
                Language = analysis.Language,
                ClarityScore = analysis.Clarity,
                ComplexityScore = analysis.Complexity,
                KeyPhrases = ExtractKeyPhrases(analysis.Tokens),
                ContextualRelevance = await CalculateContextualRelevanceAsync(request.Message, request.Context)
            };
        }

        /// <summary>
        /// Konuları tespit eder;
        /// </summary>
        private async Task<List<DetectedTopic>> DetectTopicsAsync(MessageAnalysis analysis)
        {
            var detectedTopics = new List<DetectedTopic>();

            // 1. Entity-based topic detection;
            var entityTopics = await DetectTopicsFromEntitiesAsync(analysis.Entities);
            detectedTopics.AddRange(entityTopics);

            // 2. Intent-based topic detection;
            var intentTopics = await DetectTopicsFromIntentAsync(analysis.Intent);
            detectedTopics.AddRange(intentTopics);

            // 3. Keyword-based topic detection;
            var keywordTopics = await DetectTopicsFromKeywordsAsync(analysis.KeyPhrases);
            detectedTopics.AddRange(keywordTopics);

            // 4. Context-based topic detection;
            var contextTopics = await DetectTopicsFromContextAsync(analysis);
            detectedTopics.AddRange(contextTopics);

            // 5. Duplicate'leri kaldır ve confidence'ı birleştir;
            var mergedTopics = MergeDuplicateTopics(detectedTopics);

            return mergedTopics.OrderByDescending(t => t.Confidence).ToList();
        }

        /// <summary>
        /// Konuları önceliklendirir;
        /// </summary>
        private async Task<List<PrioritizedTopic>> PrioritizeTopicsAsync(
            List<DetectedTopic> topics,
            ConversationContext context)
        {
            var prioritizedTopics = new List<PrioritizedTopic>();

            foreach (var topic in topics)
            {
                var priorityScore = await CalculateTopicPriorityAsync(topic, context);

                prioritizedTopics.Add(new PrioritizedTopic;
                {
                    Topic = topic,
                    PriorityScore = priorityScore,
                    WeightedConfidence = topic.Confidence * priorityScore;
                });
            }

            return prioritizedTopics;
                .OrderByDescending(t => t.WeightedConfidence)
                .ThenByDescending(t => t.PriorityScore)
                .ToList();
        }

        /// <summary>
        /// Routing stratejisini uygular;
        /// </summary>
        private async Task<RoutingDecision> ApplyRoutingStrategyAsync(
            List<PrioritizedTopic> topics,
            RoutingRequest request)
        {
            var strategyContext = new RoutingStrategyContext;
            {
                PrioritizedTopics = topics,
                Request = request,
                ConversationHistory = await GetConversationHistoryAsync(request.SessionId),
                UserPreferences = await GetUserPreferencesAsync(request.UserId)
            };

            return await _routingStrategy.DecideAsync(strategyContext);
        }

        /// <summary>
        /// Context'i günceller;
        /// </summary>
        private async Task UpdateContextAsync(RoutingRequest request, RoutingDecision decision)
        {
            var contextUpdate = new ContextUpdate;
            {
                SessionId = request.SessionId,
                CurrentTopic = decision.PrimaryTopic?.TopicId,
                PreviousTopic = request.Context?.CurrentTopic,
                TransitionReason = decision.RoutingReason,
                Timestamp = DateTime.UtcNow;
            };

            await _contextSwitcher.UpdateContextAsync(contextUpdate);
        }

        /// <summary>
        /// Routing sonucunu oluşturur;
        /// </summary>
        private RoutingResult CreateRoutingResult(
            RoutingRequest request,
            MessageAnalysis analysis,
            RoutingDecision decision)
        {
            return new RoutingResult;
            {
                RequestId = request.MessageId,
                SessionId = request.SessionId,
                PrimaryTopic = decision.PrimaryTopic,
                AlternativeTopics = decision.AlternativeTopics,
                RoutingConfidence = decision.Confidence,
                RoutingReason = decision.RoutingReason,
                RequiresClarification = decision.RequiresClarification,
                ClarificationQuestions = decision.ClarificationQuestions,
                SuggestedFollowUps = decision.SuggestedFollowUps,
                AnalysisSummary = analysis,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Geçiş validasyonu yapar;
        /// </summary>
        private async Task<TransitionValidation> ValidateTransitionAsync(TopicTransitionRequest request)
        {
            var validation = new TransitionValidation;
            {
                IsValid = true;
            };

            // 1. Topic varlığını kontrol et;
            var targetTopicExists = await _topicManager.TopicExistsAsync(request.TargetTopic);
            if (!targetTopicExists)
            {
                validation.IsValid = false;
                validation.Error = $"Target topic '{request.TargetTopic}' does not exist";
                return validation;
            }

            // 2. Dependency kontrolü;
            var dependencies = await AnalyzeDependenciesAsync(request.TargetTopic);
            var missingPrerequisites = dependencies.Prerequisites;
                .Where(p => !p.IsSatisfied)
                .ToList();

            if (missingPrerequisites.Any())
            {
                validation.IsValid = false;
                validation.Error = $"Missing prerequisites for topic '{request.TargetTopic}'";
                validation.SuggestedAlternative = await FindAlternativeTopicAsync(
                    request.TargetTopic,
                    missingPrerequisites);
                return validation;
            }

            // 3. Conflict kontrolü;
            var conflicts = dependencies.Conflicts;
                .Where(c => c.Severity > ConflictSeverity.Medium)
                .ToList();

            if (conflicts.Any())
            {
                validation.IsValid = false;
                validation.Error = $"Topic conflict detected: {string.Join(", ", conflicts.Select(c => c.ConflictTopic))}";
                return validation;
            }

            // 4. Context uyumluluğu kontrolü;
            var contextCompatibility = await CheckContextCompatibilityAsync(request);
            if (!contextCompatibility.IsCompatible)
            {
                validation.IsValid = false;
                validation.Error = contextCompatibility.Reason;
                return validation;
            }

            return validation;
        }

        /// <summary>
        /// Context switch hazırlığı yapar;
        /// </summary>
        private async Task PrepareContextSwitchAsync(TopicTransitionRequest request)
        {
            // 1. Mevcut context'i kaydet;
            await SaveCurrentContextAsync(request);

            // 2. Yeni context'i hazırla;
            await PrepareNewContextAsync(request);

            // 3. Transition kaynaklarını yükle;
            await LoadTransitionResourcesAsync(request);

            // 4. Cache'i hazırla;
            await PrepareTransitionCacheAsync(request);
        }

        /// <summary>
        /// Geçiş stratejisini belirler;
        /// </summary>
        private async Task<TransitionStrategy> DetermineTransitionStrategyAsync(TopicTransitionRequest request)
        {
            var strategyContext = new TransitionStrategyContext;
            {
                CurrentTopic = request.CurrentTopic,
                TargetTopic = request.TargetTopic,
                TransitionType = request.TransitionType,
                Urgency = request.Urgency,
                UserExperienceLevel = await GetUserExperienceLevelAsync(request.UserId)
            };

            return await _routingStrategy.DetermineTransitionStrategyAsync(strategyContext);
        }

        /// <summary>
        /// Geçişi gerçekleştirir;
        /// </summary>
        private async Task<TopicTransitionResult> ExecuteTransitionAsync(
            TopicTransitionRequest request,
            TransitionStrategy strategy)
        {
            var result = new TopicTransitionResult;
            {
                Success = true,
                FromTopic = request.CurrentTopic,
                ToTopic = request.TargetTopic,
                TransitionStrategy = strategy.Type,
                StartedAt = DateTime.UtcNow;
            };

            // Stratejiye göre geçişi gerçekleştir;
            switch (strategy.Type)
            {
                case TransitionType.Immediate:
                    result = await ExecuteImmediateTransitionAsync(request, strategy);
                    break;

                case TransitionType.Gradual:
                    result = await ExecuteGradualTransitionAsync(request, strategy);
                    break;

                case TransitionType.ContextPreserving:
                    result = await ExecuteContextPreservingTransitionAsync(request, strategy);
                    break;

                default:
                    result = await ExecuteDefaultTransitionAsync(request, strategy);
                    break;
            }

            result.CompletedAt = DateTime.UtcNow;
            result.Duration = result.CompletedAt - result.StartedAt;

            return result;
        }

        /// <summary>
        /// Geçişi kaydeder;
        /// </summary>
        private async Task LogTransitionAsync(TopicTransitionRequest request, TopicTransitionResult result)
        {
            var transitionLog = new TransitionLog;
            {
                SessionId = request.SessionId,
                UserId = request.UserId,
                FromTopic = request.CurrentTopic,
                ToTopic = request.TargetTopic,
                Strategy = result.TransitionStrategy,
                Success = result.Success,
                Duration = result.Duration,
                Error = result.Error,
                Timestamp = DateTime.UtcNow;
            };

            await _topicManager.LogTransitionAsync(transitionLog);
        }

        /// <summary>
        /// Prerequisite konuları bulur;
        /// </summary>
        private async Task<List<Dependency>> FindPrerequisitesAsync(string topic)
        {
            return await _knowledgeGraph.GetPrerequisitesAsync(topic);
        }

        /// <summary>
        /// Dependent konuları bulur;
        /// </summary>
        private async Task<List<Dependency>> FindDependentsAsync(string topic)
        {
            return await _knowledgeGraph.GetDependentsAsync(topic);
        }

        /// <summary>
        /// İlgili konuları bulur;
        /// </summary>
        private async Task<List<RelatedTopic>> FindRelatedTopicsAsync(string topic)
        {
            return await _knowledgeGraph.GetRelatedTopicsAsync(topic, 10);
        }

        /// <summary>
        /// Conflict konuları bulur;
        /// </summary>
        private async Task<List<TopicConflict>> FindConflictsAsync(string topic)
        {
            return await _knowledgeGraph.GetConflictsAsync(topic);
        }

        /// <summary>
        /// Dependency graph oluşturur;
        /// </summary>
        private async Task<DependencyGraph> BuildDependencyGraphAsync(string topic)
        {
            return await _knowledgeGraph.BuildDependencyGraphAsync(topic, 3);
        }

        /// <summary>
        /// Bağımlılık karmaşıklığını hesaplar;
        /// </summary>
        private double CalculateComplexity(TopicDependencyAnalysis analysis)
        {
            var totalDependencies = analysis.Prerequisites.Count + analysis.Dependents.Count;
            var conflictCount = analysis.Conflicts.Count;

            return (totalDependencies * 0.6) + (conflictCount * 0.4);
        }

        /// <summary>
        /// En güçlü bağımlılığı bulur;
        /// </summary>
        private Dependency FindStrongestDependency(TopicDependencyAnalysis analysis)
        {
            var allDependencies = analysis.Prerequisites;
                .Concat(analysis.Dependents)
                .ToList();

            return allDependencies.OrderByDescending(d => d.Strength).FirstOrDefault();
        }

        /// <summary>
        /// Yeni önceliği hesaplar;
        /// </summary>
        private TopicPriority CalculateNewPriority(TopicPriority current, TopicPriorityUpdateItem update)
        {
            var newPriority = new TopicPriority;
            {
                BasePriority = update.BasePriority ?? current.BasePriority,
                UsageFrequency = update.UsageFrequency ?? current.UsageFrequency,
                Recency = update.Recency ?? current.Recency,
                UserImportance = update.UserImportance ?? current.UserImportance,
                ContextualRelevance = update.ContextualRelevance ?? current.ContextualRelevance;
            };

            // Weighted average hesapla;
            newPriority.CalculatedPriority = CalculateWeightedPriority(newPriority);

            return newPriority;
        }

        /// <summary>
        /// Ağırlıklı öncelik hesaplar;
        /// </summary>
        private double CalculateWeightedPriority(TopicPriority priority)
        {
            return (priority.BasePriority * 0.3) +
                   (priority.UsageFrequency * 0.25) +
                   (priority.Recency * 0.2) +
                   (priority.UserImportance * 0.15) +
                   (priority.ContextualRelevance * 0.1);
        }

        /// <summary>
        /// Global öncelikleri yeniden hesaplar;
        /// </summary>
        private async Task RecalculateGlobalPrioritiesAsync()
        {
            var allTopics = await _topicManager.GetAllTopicsAsync();

            foreach (var topic in allTopics)
            {
                var updatedPriority = await RecalculateTopicPriorityAsync(topic);
                await _topicManager.UpdateTopicPriorityAsync(topic.Id, updatedPriority);
            }
        }

        #endregion;

        #region Health Check Methods;

        private async Task<ComponentHealth> CheckNLPEngineHealthAsync()
        {
            try
            {
                var isHealthy = await _nlpEngine.CheckHealthAsync();
                return new ComponentHealth;
                {
                    Name = "NLPEngine",
                    Status = isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth;
                {
                    Name = "NLPEngine",
                    Status = HealthStatus.Critical,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        private async Task<ComponentHealth> CheckTopicManagerHealthAsync()
        {
            try
            {
                var stats = await _topicManager.GetStatisticsAsync();
                var isHealthy = stats.TotalTopics > 0 && stats.AverageResponseTime < TimeSpan.FromSeconds(2);

                return new ComponentHealth;
                {
                    Name = "TopicManager",
                    Status = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded,
                    Details = $"Topics: {stats.TotalTopics}, Avg Response: {stats.AverageResponseTime}",
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth;
                {
                    Name = "TopicManager",
                    Status = HealthStatus.Critical,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        private async Task<ComponentHealth> CheckKnowledgeGraphHealthAsync()
        {
            try
            {
                var health = await _knowledgeGraph.CheckHealthAsync();
                return new ComponentHealth;
                {
                    Name = "KnowledgeGraph",
                    Status = health.IsHealthy ? HealthStatus.Healthy : HealthStatus.Degraded,
                    Details = health.Details,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth;
                {
                    Name = "KnowledgeGraph",
                    Status = HealthStatus.Critical,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        private async Task<ComponentHealth> CheckCacheHealthAsync()
        {
            try
            {
                var stats = await _routingCache.GetStatisticsAsync();
                var hitRate = stats.TotalRequests > 0 ? (double)stats.Hits / stats.TotalRequests : 0;
                var isHealthy = hitRate > 0.3 && stats.EvictionRate < 0.1;

                return new ComponentHealth;
                {
                    Name = "RoutingCache",
                    Status = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded,
                    Details = $"Hit Rate: {hitRate:P2}, Size: {stats.CurrentSize}",
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth;
                {
                    Name = "RoutingCache",
                    Status = HealthStatus.Critical,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        private async Task<ComponentHealth> CheckRegistryHealthAsync()
        {
            try
            {
                var count = _topicRegistry.GetTopicCount();
                var isHealthy = count > 0;

                return new ComponentHealth;
                {
                    Name = "TopicRegistry",
                    Status = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded,
                    Details = $"Registered Topics: {count}",
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new ComponentHealth;
                {
                    Name = "TopicRegistry",
                    Status = HealthStatus.Critical,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        private HealthStatus DetermineOverallHealth(List<ComponentHealth> componentHealth)
        {
            if (componentHealth.Any(c => c.Status == HealthStatus.Critical))
                return HealthStatus.Critical;

            if (componentHealth.Any(c => c.Status == HealthStatus.Unhealthy))
                return HealthStatus.Unhealthy;

            if (componentHealth.Any(c => c.Status == HealthStatus.Degraded))
                return HealthStatus.Degraded;

            return HealthStatus.Healthy;
        }

        #endregion;

        #region Helper Methods;

        private List<string> ExtractKeyPhrases(List<string> tokens)
        {
            // Basit key phrase extraction;
            // Gerçek implementasyonda daha gelişmiş NLP kullanılır;
            return tokens;
                .Where(t => t.Length > 3)
                .Distinct()
                .Take(10)
                .ToList();
        }

        private async Task<double> CalculateContextualRelevanceAsync(string message, ConversationContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.CurrentTopic))
                return 0.5;

            var currentTopic = await _topicManager.GetTopicAsync(context.CurrentTopic);
            if (currentTopic == null)
                return 0.5;

            // Basit relevance hesaplama;
            // Gerçek implementasyonda semantic similarity kullanılır;
            var topicKeywords = currentTopic.Keywords ?? new List<string>();
            var messageWords = message.ToLower().Split(' ');

            var matches = messageWords.Count(w => topicKeywords.Any(k => w.Contains(k.ToLower())));
            var relevance = (double)matches / Math.Max(1, messageWords.Length);

            return Math.Min(1.0, relevance);
        }

        private List<DetectedTopic> MergeDuplicateTopics(List<DetectedTopic> topics)
        {
            var merged = new Dictionary<string, DetectedTopic>();

            foreach (var topic in topics)
            {
                if (merged.TryGetValue(topic.TopicId, out var existing))
                {
                    // Confidence'ı birleştir;
                    existing.Confidence = Math.Max(existing.Confidence, topic.Confidence);
                    existing.Sources = existing.Sources.Union(topic.Sources).Distinct().ToList();
                }
                else;
                {
                    merged[topic.TopicId] = topic;
                }
            }

            return merged.Values.ToList();
        }

        private async Task<double> CalculateTopicPriorityAsync(DetectedTopic topic, ConversationContext context)
        {
            var basePriority = await _topicManager.GetTopicPriorityAsync(topic.TopicId);
            if (basePriority == null)
                return 0.5;

            var contextualBoost = await CalculateContextualBoostAsync(topic, context);
            var recencyBoost = CalculateRecencyBoost(topic);
            var frequencyBoost = await CalculateFrequencyBoostAsync(topic);

            return basePriority.CalculatedPriority *
                   (1.0 + contextualBoost + recencyBoost + frequencyBoost);
        }

        private async Task<double> CalculateContextualBoostAsync(DetectedTopic topic, ConversationContext context)
        {
            if (context?.CurrentTopic == topic.TopicId)
                return 0.3; // Mevcut konuya boost;

            if (context?.RecentTopics?.Contains(topic.TopicId) == true)
                return 0.2; // Yakın zamanda konuşulan konuya boost;

            var relatedness = await _knowledgeGraph.CalculateRelatednessAsync(
                context?.CurrentTopic,
                topic.TopicId);

            return relatedness * 0.15;
        }

        private double CalculateRecencyBoost(DetectedTopic topic)
        {
            // Son erişim zamanına göre boost hesapla;
            var hoursSinceLastAccess = topic.LastAccessed.HasValue;
                ? (DateTime.UtcNow - topic.LastAccessed.Value).TotalHours;
                : 24;

            return Math.Max(0, 0.1 * (24 - hoursSinceLastAccess) / 24);
        }

        private async Task<double> CalculateFrequencyBoostAsync(DetectedTopic topic)
        {
            var stats = await _topicManager.GetTopicStatisticsAsync(topic.TopicId);
            if (stats == null)
                return 0;

            // Normalize edilmiş frekans boost'u;
            var normalizedFrequency = Math.Min(1.0, stats.AccessCount / 1000.0);
            return normalizedFrequency * 0.1;
        }

        private async Task<ConversationHistory> GetConversationHistoryAsync(string sessionId)
        {
            // Gerçek implementasyonda veritabanından alınır;
            return await Task.FromResult(new ConversationHistory;
            {
                SessionId = sessionId,
                RecentMessages = new List<string>(),
                TopicPatterns = new Dictionary<string, int>()
            });
        }

        private async Task<UserPreferences> GetUserPreferencesAsync(string userId)
        {
            // Gerçek implementasyonda user servisinden alınır;
            return await Task.FromResult(new UserPreferences;
            {
                PreferredTopics = new List<string>(),
                AvoidedTopics = new List<string>(),
                TopicWeights = new Dictionary<string, double>()
            });
        }

        private async Task<TopicTransitionResult> ExecuteImmediateTransitionAsync(
            TopicTransitionRequest request,
            TransitionStrategy strategy)
        {
            // Immediate transition implementasyonu;
            await Task.Delay(50); // Simüle edilmiş işlem;

            return new TopicTransitionResult;
            {
                Success = true,
                TransitionStrategy = TransitionType.Immediate;
            };
        }

        private async Task<TopicTransitionResult> ExecuteGradualTransitionAsync(
            TopicTransitionRequest request,
            TransitionStrategy strategy)
        {
            // Gradual transition implementasyonu;
            var steps = strategy.Parameters?.Steps ?? 3;

            for (int i = 1; i <= steps; i++)
            {
                await ExecuteTransitionStepAsync(request, i, steps);
                await Task.Delay(100); // Simüle edilmiş adım;
            }

            return new TopicTransitionResult;
            {
                Success = true,
                TransitionStrategy = TransitionType.Gradual,
                StepsCompleted = steps;
            };
        }

        private async Task<TopicTransitionResult> ExecuteContextPreservingTransitionAsync(
            TopicTransitionRequest request,
            TransitionStrategy strategy)
        {
            // Context preserving transition implementasyonu;
            await PreserveContextAsync(request);
            await Task.Delay(100); // Simüle edilmiş işlem;

            return new TopicTransitionResult;
            {
                Success = true,
                TransitionStrategy = TransitionType.ContextPreserving,
                PreservedContextElements = new List<string> { "user_state", "conversation_flow" }
            };
        }

        private async Task<TopicTransitionResult> ExecuteDefaultTransitionAsync(
            TopicTransitionRequest request,
            TransitionStrategy strategy)
        {
            // Default transition implementasyonu;
            await Task.Delay(50); // Simüle edilmiş işlem;

            return new TopicTransitionResult;
            {
                Success = true,
                TransitionStrategy = TransitionType.Immediate;
            };
        }

        private async Task ExecuteTransitionStepAsync(TopicTransitionRequest request, int step, int totalSteps)
        {
            // Transition step implementasyonu;
            _logger.LogDebug("Executing transition step {Step}/{TotalSteps}", step, totalSteps);
            await Task.Delay(10);
        }

        private async Task PreserveContextAsync(TopicTransitionRequest request)
        {
            // Context preservation implementasyonu;
            _logger.LogDebug("Preserving context during transition");
            await Task.Delay(10);
        }

        private async Task<double> GetUserExperienceLevelAsync(string userId)
        {
            // Gerçek implementasyonda user profili servisinden alınır;
            return await Task.FromResult(0.5);
        }

        private async Task<List<Topic>> GetBaseTopicsAsync(TopicMapRequest request)
        {
            switch (request.Scope)
            {
                case TopicMapScope.Global:
                    return await _topicManager.GetAllTopicsAsync();

                case TopicMapScope.Session:
                    return await _topicManager.GetSessionTopicsAsync(request.SessionId);

                case TopicMapScope.User:
                    return await _topicManager.GetUserTopicsAsync(request.UserId);

                case TopicMapScope.Contextual:
                    return await _topicManager.GetContextualTopicsAsync(request.Context);

                default:
                    return await _topicManager.GetAllTopicsAsync();
            }
        }

        private async Task<TopicHierarchy> BuildTopicHierarchyAsync(List<Topic> baseTopics, int depth)
        {
            var hierarchy = new TopicHierarchy;
            {
                RootTopics = new List<TopicNode>(),
                TotalTopicCount = baseTopics.Count;
            };

            foreach (var topic in baseTopics.Take(100)) // Limit for performance;
            {
                var node = await BuildTopicNodeAsync(topic, depth);
                hierarchy.RootTopics.Add(node);
            }

            return hierarchy;
        }

        private async Task<TopicNode> BuildTopicNodeAsync(Topic topic, int remainingDepth)
        {
            var node = new TopicNode;
            {
                Topic = topic,
                Children = new List<TopicNode>()
            };

            if (remainingDepth > 0)
            {
                var subtopics = await _knowledgeGraph.GetSubtopicsAsync(topic.Id, 5);
                foreach (var subtopic in subtopics)
                {
                    var childNode = await BuildTopicNodeAsync(subtopic, remainingDepth - 1);
                    node.Children.Add(childNode);
                }
            }

            return node;
        }

        private async Task<List<TopicRelationship>> ExtractTopicRelationshipsAsync(TopicHierarchy hierarchy)
        {
            var relationships = new List<TopicRelationship>();

            // Implement relationship extraction logic;
            await Task.Delay(10); // Simüle edilmiş işlem;

            return relationships;
        }

        private async Task<TopicDensityAnalysis> AnalyzeTopicDensityAsync(TopicHierarchy hierarchy)
        {
            return new TopicDensityAnalysis;
            {
                AverageDepth = CalculateAverageDepth(hierarchy),
                BranchingFactor = CalculateBranchingFactor(hierarchy),
                DensityScore = await CalculateDensityScoreAsync(hierarchy)
            };
        }

        private double CalculateAverageDepth(TopicHierarchy hierarchy)
        {
            // Implement average depth calculation;
            return 2.5;
        }

        private double CalculateBranchingFactor(TopicHierarchy hierarchy)
        {
            // Implement branching factor calculation;
            return 3.2;
        }

        private async Task<double> CalculateDensityScoreAsync(TopicHierarchy hierarchy)
        {
            // Implement density score calculation;
            await Task.Delay(10);
            return 0.75;
        }

        private async Task<List<KeyTopic>> IdentifyKeyTopicsAsync(TopicMap topicMap)
        {
            var keyTopics = new List<KeyTopic>();

            // Implement key topic identification logic;
            await Task.Delay(10); // Simüle edilmiş işlem;

            return keyTopics;
        }

        private async Task<VisualizationData> PrepareVisualizationDataAsync(TopicMap topicMap)
        {
            return new VisualizationData;
            {
                Nodes = topicMap.Hierarchy.RootTopics.Select(t => t.Topic.Id).ToList(),
                Links = new List<VisualizationLink>(),
                Layout = "hierarchical"
            };
        }

        private async Task<RoutingResult> CreateErrorRoutingResultAsync(RoutingRequest request, Exception ex)
        {
            return new RoutingResult;
            {
                RequestId = request.MessageId,
                SessionId = request.SessionId,
                PrimaryTopic = new RoutedTopic;
                {
                    TopicId = "error",
                    TopicName = "Error Handling",
                    Confidence = 0.0;
                },
                RoutingConfidence = 0.0,
                RoutingReason = "System error occurred",
                Error = new RoutingError;
                {
                    Code = "ROUTING_ERROR",
                    Message = ex.Message,
                    Details = ex.ToString()
                },
                Timestamp = DateTime.UtcNow;
            };
        }

        private void ValidateRoutingRequest(RoutingRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Message))
                throw new ArgumentException("Message cannot be empty", nameof(request.Message));

            if (string.IsNullOrWhiteSpace(request.MessageId))
                throw new ArgumentException("MessageId cannot be empty", nameof(request.MessageId));
        }

        private void ValidateTransitionRequest(TopicTransitionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.CurrentTopic))
                throw new ArgumentException("CurrentTopic cannot be empty", nameof(request.CurrentTopic));

            if (string.IsNullOrWhiteSpace(request.TargetTopic))
                throw new ArgumentException("TargetTopic cannot be empty", nameof(request.TargetTopic));
        }

        private void ValidatePriorityUpdate(TopicPriorityUpdate update)
        {
            if (update == null)
                throw new ArgumentNullException(nameof(update));

            if (update.TopicUpdates == null || !update.TopicUpdates.Any())
                throw new ArgumentException("TopicUpdates cannot be empty", nameof(update.TopicUpdates));
        }

        private void LogRoutingDecision(RoutingRequest request, RoutingResult result)
        {
            _logger.LogInformation(
                "Message {MessageId} routed to topic {Topic} with confidence {Confidence}",
                request.MessageId,
                result.PrimaryTopic?.TopicName,
                result.RoutingConfidence);
        }

        #endregion;

        #region Dispose Pattern;

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
                    // Managed kaynakları serbest bırak;
                    _logger.LogInformation("SubjectRouter disposing");
                    _metrics.Dispose();
                }

                _disposed = true;
            }
        }

        ~SubjectRouter()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Data Models;

    public class RoutingRequest;
    {
        public string MessageId { get; set; }
        public string Message { get; set; }
        public ConversationContext Context { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public string MessageHash => CalculateMessageHash();

        private string CalculateMessageHash()
        {
            // Basit hash hesaplama;
            return $"{MessageId}_{Message?.GetHashCode()}_{Context?.CurrentTopic}";
        }
    }

    public class RoutingResult;
    {
        public string RequestId { get; set; }
        public string SessionId { get; set; }
        public RoutedTopic PrimaryTopic { get; set; }
        public List<RoutedTopic> AlternativeTopics { get; set; } = new List<RoutedTopic>();
        public double RoutingConfidence { get; set; }
        public string RoutingReason { get; set; }
        public bool RequiresClarification { get; set; }
        public List<string> ClarificationQuestions { get; set; } = new List<string>();
        public List<string> SuggestedFollowUps { get; set; } = new List<string>();
        public MessageAnalysis AnalysisSummary { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public RoutingError Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TopicTransitionRequest;
    {
        public string CurrentTopic { get; set; }
        public string TargetTopic { get; set; }
        public TransitionType TransitionType { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public TransitionUrgency Urgency { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class TopicTransitionResult;
    {
        public bool Success { get; set; }
        public string FromTopic { get; set; }
        public string ToTopic { get; set; }
        public TransitionType TransitionStrategy { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public int StepsCompleted { get; set; }
        public List<string> PreservedContextElements { get; set; } = new List<string>();
        public string Error { get; set; }
        public string SuggestedAlternative { get; set; }
    }

    public class TopicDependencyAnalysis;
    {
        public string Topic { get; set; }
        public List<Dependency> Prerequisites { get; set; } = new List<Dependency>();
        public List<Dependency> Dependents { get; set; } = new List<Dependency>();
        public List<RelatedTopic> RelatedTopics { get; set; } = new List<RelatedTopic>();
        public List<TopicConflict> Conflicts { get; set; } = new List<TopicConflict>();
        public DependencyGraph DependencyGraph { get; set; }
        public double DependencyComplexity { get; set; }
        public Dependency StrongestDependency { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TopicPriorityUpdate;
    {
        public List<TopicPriorityUpdateItem> TopicUpdates { get; set; } = new List<TopicPriorityUpdateItem>();
        public UpdateStrategy Strategy { get; set; }
        public string Reason { get; set; }
    }

    public class RouterHealth;
    {
        public HealthStatus Status { get; set; }
        public List<ComponentHealth> ComponentHealth { get; set; } = new List<ComponentHealth>();
        public RouterMetricsData Metrics { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TopicMapRequest;
    {
        public TopicMapScope Scope { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public ConversationContext Context { get; set; }
        public int Depth { get; set; } = 3;
        public bool IncludeVisualizationData { get; set; }
    }

    public class TopicMap;
    {
        public TopicMapScope Scope { get; set; }
        public List<Topic> BaseTopics { get; set; } = new List<Topic>();
        public TopicHierarchy Hierarchy { get; set; }
        public List<TopicRelationship> Relationships { get; set; } = new List<TopicRelationship>();
        public TopicDensityAnalysis DensityAnalysis { get; set; }
        public List<KeyTopic> KeyTopics { get; set; } = new List<KeyTopic>();
        public VisualizationData VisualizationData { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    public class RouterStatistics;
    {
        public RouterMetricsData Metrics { get; set; }
        public CacheStatistics CacheStatistics { get; set; }
        public TopicStatistics TopicStatistics { get; set; }
        public TimeSpan Uptime { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    #endregion;

    #region Supporting Models;

    public class ConversationContext;
    {
        public string CurrentTopic { get; set; }
        public List<string> RecentTopics { get; set; } = new List<string>();
        public Dictionary<string, object> ContextData { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class MessageAnalysis;
    {
        public string MessageId { get; set; }
        public string Text { get; set; }
        public List<string> Tokens { get; set; }
        public List<Entity> Entities { get; set; }
        public string Intent { get; set; }
        public Sentiment Sentiment { get; set; }
        public string Language { get; set; }
        public double ClarityScore { get; set; }
        public double ComplexityScore { get; set; }
        public List<string> KeyPhrases { get; set; }
        public double ContextualRelevance { get; set; }
    }

    public class DetectedTopic;
    {
        public string TopicId { get; set; }
        public string TopicName { get; set; }
        public double Confidence { get; set; }
        public List<string> Sources { get; set; } = new List<string>();
        public DateTime? LastAccessed { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class PrioritizedTopic;
    {
        public DetectedTopic Topic { get; set; }
        public double PriorityScore { get; set; }
        public double WeightedConfidence { get; set; }
    }

    public class RoutingDecision;
    {
        public RoutedTopic PrimaryTopic { get; set; }
        public List<RoutedTopic> AlternativeTopics { get; set; } = new List<RoutedTopic>();
        public double Confidence { get; set; }
        public string RoutingReason { get; set; }
        public bool RequiresClarification { get; set; }
        public List<string> ClarificationQuestions { get; set; } = new List<string>();
        public List<string> SuggestedFollowUps { get; set; } = new List<string>();
    }

    public class RoutedTopic;
    {
        public string TopicId { get; set; }
        public string TopicName { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> RoutingMetadata { get; set; }
    }

    public class TransitionValidation;
    {
        public bool IsValid { get; set; }
        public string Error { get; set; }
        public string SuggestedAlternative { get; set; }
    }

    public class TransitionStrategy;
    {
        public TransitionType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public double EstimatedDuration { get; set; }
    }

    public class Dependency;
    {
        public string TopicId { get; set; }
        public string TopicName { get; set; }
        public DependencyType Type { get; set; }
        public double Strength { get; set; }
        public bool IsSatisfied { get; set; }
        public string Description { get; set; }
    }

    public class RelatedTopic;
    {
        public string TopicId { get; set; }
        public string TopicName { get; set; }
        public RelationType Relation { get; set; }
        public double Relevance { get; set; }
    }

    public class TopicConflict;
    {
        public string ConflictTopic { get; set; }
        public ConflictType Type { get; set; }
        public ConflictSeverity Severity { get; set; }
        public string Description { get; set; }
        public string Resolution { get; set; }
    }

    public class DependencyGraph;
    {
        public List<GraphNode> Nodes { get; set; } = new List<GraphNode>();
        public List<GraphEdge> Edges { get; set; } = new List<GraphEdge>();
        public GraphMetrics Metrics { get; set; }
    }

    public class TopicPriority;
    {
        public double BasePriority { get; set; }
        public double UsageFrequency { get; set; }
        public double Recency { get; set; }
        public double UserImportance { get; set; }
        public double ContextualRelevance { get; set; }
        public double CalculatedPriority { get; set; }
        public DateTime LastCalculated { get; set; }
    }

    public class TopicPriorityUpdateItem;
    {
        public string TopicId { get; set; }
        public double? BasePriority { get; set; }
        public double? UsageFrequency { get; set; }
        public double? Recency { get; set; }
        public double? UserImportance { get; set; }
        public double? ContextualRelevance { get; set; }
    }

    public class ComponentHealth;
    {
        public string Name { get; set; }
        public HealthStatus Status { get; set; }
        public string Details { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TopicHierarchy;
    {
        public List<TopicNode> RootTopics { get; set; } = new List<TopicNode>();
        public int TotalTopicCount { get; set; }
        public int MaxDepth { get; set; }
    }

    public class TopicNode;
    {
        public Topic Topic { get; set; }
        public List<TopicNode> Children { get; set; } = new List<TopicNode>();
        public int Depth { get; set; }
    }

    public class TopicDensityAnalysis;
    {
        public double AverageDepth { get; set; }
        public double BranchingFactor { get; set; }
        public double DensityScore { get; set; }
    }

    public class KeyTopic;
    {
        public string TopicId { get; set; }
        public string TopicName { get; set; }
        public double ImportanceScore { get; set; }
        public List<string> KeyFactors { get; set; } = new List<string>();
    }

    public class VisualizationData;
    {
        public List<string> Nodes { get; set; } = new List<string>();
        public List<VisualizationLink> Links { get; set; } = new List<VisualizationLink>();
        public string Layout { get; set; }
    }

    public class RoutingError;
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    #endregion;

    #region Enums;

    public enum TransitionType;
    {
        Immediate,
        Gradual,
        ContextPreserving,
        Assisted;
    }

    public enum TransitionUrgency;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum TopicMapScope;
    {
        Global,
        Session,
        User,
        Contextual;
    }

    public enum HealthStatus;
    {
        Healthy,
        Degraded,
        Unhealthy,
        Critical;
    }

    public enum DependencyType;
    {
        Prerequisite,
        Corequisite,
        Postrequisite,
        Recommended;
    }

    public enum RelationType;
    {
        ParentChild,
        Sibling,
        Related,
        Similar,
        Complementary;
    }

    public enum ConflictType;
    {
        Conceptual,
        Temporal,
        Resource,
        Logical,
        Contextual;
    }

    public enum ConflictSeverity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum UpdateStrategy;
    {
        Incremental,
        Complete,
        Adaptive,
        Manual;
    }

    #endregion;

    #region Dependency Interfaces;

    public interface ITopicManager;
    {
        Task<Topic> GetTopicAsync(string topicId);
        Task<List<Topic>> GetAllTopicsAsync();
        Task<List<Topic>> GetSessionTopicsAsync(string sessionId);
        Task<List<Topic>> GetUserTopicsAsync(string userId);
        Task<List<Topic>> GetContextualTopicsAsync(ConversationContext context);
        Task<TopicPriority> GetTopicPriorityAsync(string topicId);
        Task UpdateTopicPriorityAsync(string topicId, TopicPriority priority);
        Task<bool> TopicExistsAsync(string topicId);
        Task<TopicStatistics> GetTopicStatisticsAsync(string topicId);
        Task<TopicStatistics> GetStatisticsAsync();
        Task LogTransitionAsync(TransitionLog log);
    }

    public interface IContextSwitcher;
    {
        Task UpdateContextAsync(ContextUpdate update);
        Task<ConversationContext> GetContextAsync(string sessionId);
        Task<bool> CheckContextCompatibilityAsync(TopicTransitionRequest request);
    }

    public interface IKnowledgeGraph;
    {
        Task<List<Dependency>> GetPrerequisitesAsync(string topic);
        Task<List<Dependency>> GetDependentsAsync(string topic);
        Task<List<RelatedTopic>> GetRelatedTopicsAsync(string topic, int limit);
        Task<List<TopicConflict>> GetConflictsAsync(string topic);
        Task<DependencyGraph> BuildDependencyGraphAsync(string topic, int depth);
        Task<double> CalculateRelatednessAsync(string topic1, string topic2);
        Task<List<Topic>> GetSubtopicsAsync(string topicId, int limit);
        Task<KnowledgeGraphHealth> CheckHealthAsync();
    }

    public interface ITopicRegistry
    {
        int GetTopicCount();
        Task<bool> RegisterTopicAsync(Topic topic);
        Task<bool> UnregisterTopicAsync(string topicId);
        Task<Topic> LookupTopicAsync(string topicId);
        Task<List<Topic>> SearchTopicsAsync(string query);
    }

    public interface IRoutingCache;
    {
        Task<RoutingResult> GetAsync(string key);
        Task SetAsync(string key, RoutingResult value, TimeSpan expiration);
        Task InvalidateTopicAsync(string topicId);
        Task InvalidateSessionAsync(string sessionId);
        Task<CacheStatistics> GetStatisticsAsync();
    }

    public interface IRoutingStrategy;
    {
        Task<RoutingDecision> DecideAsync(RoutingStrategyContext context);
        Task<TransitionStrategy> DetermineTransitionStrategyAsync(TransitionStrategyContext context);
    }

    #endregion;

    #region Supporting Classes;

    public class Topic;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class ConversationHistory;
    {
        public string SessionId { get; set; }
        public List<string> RecentMessages { get; set; } = new List<string>();
        public Dictionary<string, int> TopicPatterns { get; set; } = new Dictionary<string, int>();
        public DateTime LastUpdated { get; set; }
    }

    public class UserPreferences;
    {
        public List<string> PreferredTopics { get; set; } = new List<string>();
        public List<string> AvoidedTopics { get; set; } = new List<string>();
        public Dictionary<string, double> TopicWeights { get; set; } = new Dictionary<string, double>();
    }

    public class RoutingStrategyContext;
    {
        public List<PrioritizedTopic> PrioritizedTopics { get; set; }
        public RoutingRequest Request { get; set; }
        public ConversationHistory ConversationHistory { get; set; }
        public UserPreferences UserPreferences { get; set; }
    }

    public class TransitionStrategyContext;
    {
        public string CurrentTopic { get; set; }
        public string TargetTopic { get; set; }
        public TransitionType TransitionType { get; set; }
        public TransitionUrgency Urgency { get; set; }
        public double UserExperienceLevel { get; set; }
    }

    public class ContextUpdate;
    {
        public string SessionId { get; set; }
        public string CurrentTopic { get; set; }
        public string PreviousTopic { get; set; }
        public string TransitionReason { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class GraphNode;
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public Dictionary<string, object> Properties { get; set; }
    }

    public class GraphEdge;
    {
        public string SourceId { get; set; }
        public string TargetId { get; set; }
        public string Label { get; set; }
        public Dictionary<string, object> Properties { get; set; }
    }

    public class GraphMetrics;
    {
        public int NodeCount { get; set; }
        public int EdgeCount { get; set; }
        public double AverageDegree { get; set; }
        public double Density { get; set; }
    }

    public class TopicStatistics;
    {
        public int TotalTopics { get; set; }
        public int ActiveTopics { get; set; }
        public TimeSpan AverageResponseTime { get; set; }
        public double AveragePriority { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    public class CacheStatistics;
    {
        public long Hits { get; set; }
        public long Misses { get; set; }
        public long TotalRequests => Hits + Misses;
        public double HitRate => TotalRequests > 0 ? (double)Hits / TotalRequests : 0;
        public int CurrentSize { get; set; }
        public long Evictions { get; set; }
        public double EvictionRate { get; set; }
    }

    public class TransitionLog;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string FromTopic { get; set; }
        public string ToTopic { get; set; }
        public TransitionType Strategy { get; set; }
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class KnowledgeGraphHealth;
    {
        public bool IsHealthy { get; set; }
        public string Details { get; set; }
        public int NodeCount { get; set; }
        public int EdgeCount { get; set; }
    }

    public class VisualizationLink;
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string Type { get; set; }
        public double Strength { get; set; }
    }

    #endregion;

    #region Metrics Classes;

    internal class RouterMetrics : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _uptime;
        private long _totalRequests;
        private long _successfulRequests;
        private long _failedRequests;
        private long _cacheHits;
        private long _cacheMisses;
        private readonly List<TimeSpan> _processingTimes;
        private readonly object _lock = new object();

        public RouterMetrics()
        {
            _uptime = System.Diagnostics.Stopwatch.StartNew();
            _processingTimes = new List<TimeSpan>();
        }

        public void IncrementTotalRequests() => Interlocked.Increment(ref _totalRequests);
        public void IncrementSuccessfulRequests() => Interlocked.Increment(ref _successfulRequests);
        public void IncrementFailedRequests() => Interlocked.Increment(ref _failedRequests);
        public void IncrementCacheHits() => Interlocked.Increment(ref _cacheHits);
        public void IncrementCacheMisses() => Interlocked.Increment(ref _cacheMisses);
        public void IncrementErrors() => Interlocked.Increment(ref _failedRequests);

        public void RecordProcessingTime(TimeSpan time)
        {
            lock (_lock)
            {
                _processingTimes.Add(time);
                // Son 1000 kaydı tut;
                if (_processingTimes.Count > 1000)
                {
                    _processingTimes.RemoveAt(0);
                }
            }
        }

        public RouterMetricsData GetCurrentMetrics()
        {
            lock (_lock)
            {
                return new RouterMetricsData;
                {
                    TotalRequests = _totalRequests,
                    SuccessfulRequests = _successfulRequests,
                    FailedRequests = _failedRequests,
                    SuccessRate = _totalRequests > 0 ? (double)_successfulRequests / _totalRequests : 0,
                    CacheHits = _cacheHits,
                    CacheMisses = _cacheMisses,
                    CacheHitRate = (_cacheHits + _cacheMisses) > 0 ? (double)_cacheHits / (_cacheHits + _cacheMisses) : 0,
                    AverageProcessingTime = _processingTimes.Any()
                        ? TimeSpan.FromTicks((long)_processingTimes.Average(t => t.Ticks))
                        : TimeSpan.Zero,
                    P95ProcessingTime = CalculatePercentile(95),
                    P99ProcessingTime = CalculatePercentile(99),
                    Uptime = _uptime.Elapsed,
                    StartTime = DateTime.UtcNow - _uptime.Elapsed;
                };
            }
        }

        private TimeSpan CalculatePercentile(int percentile)
        {
            if (!_processingTimes.Any())
                return TimeSpan.Zero;

            var sortedTimes = _processingTimes.OrderBy(t => t.Ticks).ToList();
            var index = (int)Math.Ceiling(percentile / 100.0 * sortedTimes.Count) - 1;
            index = Math.Max(0, Math.Min(index, sortedTimes.Count - 1));

            return sortedTimes[index];
        }

        public DateTime StartTime => DateTime.UtcNow - _uptime.Elapsed;

        public void Dispose()
        {
            _uptime.Stop();
        }
    }

    public class RouterMetricsData;
    {
        public long TotalRequests { get; set; }
        public long SuccessfulRequests { get; set; }
        public long FailedRequests { get; set; }
        public double SuccessRate { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public double CacheHitRate { get; set; }
        public TimeSpan AverageProcessingTime { get; set; }
        public TimeSpan P95ProcessingTime { get; set; }
        public TimeSpan P99ProcessingTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public DateTime StartTime { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class SubjectRouterException : Exception
    {
        public string ErrorCode { get; }

        public SubjectRouterException(string message) : base(message)
        {
            ErrorCode = "SUBJECT_ROUTER_ERROR";
        }

        public SubjectRouterException(string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = "SUBJECT_ROUTER_ERROR";
        }

        public SubjectRouterException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    public class TopicTransitionException : SubjectRouterException;
    {
        public TopicTransitionException(string message) : base("TOPIC_TRANSITION_ERROR", message)
        {
        }

        public TopicTransitionException(string message, Exception innerException)
            : base("TOPIC_TRANSITION_ERROR", message, innerException)
        {
        }
    }

    public class DependencyAnalysisException : SubjectRouterException;
    {
        public DependencyAnalysisException(string message) : base("DEPENDENCY_ANALYSIS_ERROR", message)
        {
        }

        public DependencyAnalysisException(string message, Exception innerException)
            : base("DEPENDENCY_ANALYSIS_ERROR", message, innerException)
        {
        }
    }

    public class PriorityUpdateException : SubjectRouterException;
    {
        public PriorityUpdateException(string message) : base("PRIORITY_UPDATE_ERROR", message)
        {
        }

        public PriorityUpdateException(string message, Exception innerException)
            : base("PRIORITY_UPDATE_ERROR", message, innerException)
        {
        }
    }

    public class TopicMapException : SubjectRouterException;
    {
        public TopicMapException(string message) : base("TOPIC_MAP_ERROR", message)
        {
        }

        public TopicMapException(string message, Exception innerException)
            : base("TOPIC_MAP_ERROR", message, innerException)
        {
        }
    }

    #endregion;
}
