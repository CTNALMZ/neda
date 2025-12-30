using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis;
using NEDA.Brain.NLP_Engine.EntityRecognition;
using NEDA.Brain.NLP_Engine.SentimentAnalysis;
using NEDA.Brain.IntentRecognition;
using NEDA.Brain.MemorySystem;
using NEDA.Core.Engine;
using NEDA.Core.Logging;

namespace NEDA.Interface.TextInput.NaturalLanguageInput;
{
    /// <summary>
    /// Doğal dil girdilerini anlama ve yapılandırılmış komutlara dönüştürme motoru;
    /// </summary>
    public class LanguageUnderstanding : IDisposable
    {
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IParser _syntaxParser;
        private readonly IEntityExtractor _entityExtractor;
        private readonly ISentimentAnalyzer _sentimentAnalyzer;
        private readonly IIntentDetector _intentDetector;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ILogger _logger;

        private readonly Dictionary<string, List<ContextualPattern>> _contextualPatterns;
        private readonly UnderstandingConfiguration _configuration;
        private bool _isInitialized;
        private UnderstandingContext _currentContext;

        /// <summary>
        /// Dil anlama motorunu başlatır;
        /// </summary>
        public LanguageUnderstanding(
            ISemanticAnalyzer semanticAnalyzer,
            IParser syntaxParser,
            IEntityExtractor entityExtractor,
            ISentimentAnalyzer sentimentAnalyzer,
            IIntentDetector intentDetector,
            IShortTermMemory shortTermMemory,
            ILogger logger)
        {
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _syntaxParser = syntaxParser ?? throw new ArgumentNullException(nameof(syntaxParser));
            _entityExtractor = entityExtractor ?? throw new ArgumentNullException(nameof(entityExtractor));
            _sentimentAnalyzer = sentimentAnalyzer ?? throw new ArgumentNullException(nameof(sentimentAnalyzer));
            _intentDetector = intentDetector ?? throw new ArgumentNullException(nameof(intentDetector));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _contextualPatterns = new Dictionary<string, List<ContextualPattern>>();
            _configuration = new UnderstandingConfiguration();
            _isInitialized = false;
            _currentContext = new UnderstandingContext();

            _logger.LogInformation("LanguageUnderstanding initialized with all dependencies.");
        }

        /// <summary>
        /// Dil anlama motorunu yapılandırır ve başlatır;
        /// </summary>
        /// <param name="configuration">Yapılandırma ayarları</param>
        public async Task InitializeAsync(UnderstandingConfiguration configuration = null)
        {
            try
            {
                if (configuration != null)
                {
                    _configuration.Merge(configuration);
                }

                // Bağımlılıkları başlat;
                var tasks = new List<Task>
                {
                    _semanticAnalyzer.InitializeAsync(),
                    _syntaxParser.InitializeAsync(),
                    _entityExtractor.InitializeAsync(),
                    _sentimentAnalyzer.InitializeAsync(),
                    _intentDetector.InitializeAsync()
                };

                await Task.WhenAll(tasks);

                // Bağlamsal desenleri yükle;
                await LoadContextualPatternsAsync();

                _isInitialized = true;
                _logger.LogInformation("LanguageUnderstanding initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LanguageUnderstanding.");
                throw new LanguageUnderstandingException("Initialization failed.", ex);
            }
        }

        /// <summary>
        /// Doğal dil girdisini anlar ve yapılandırılmış sonuç döndürür;
        /// </summary>
        /// <param name="inputText">Kullanıcı girdisi</param>
        /// <param name="context">Ek bağlam bilgisi</param>
        /// <returns>Anlama sonucu</returns>
        public async Task<UnderstandingResult> UnderstandAsync(string inputText, UnderstandingContext context = null)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(inputText))
            {
                throw new ArgumentException("Input text cannot be null or empty.", nameof(inputText));
            }

            try
            {
                _logger.LogDebug($"Processing natural language input: {inputText}");

                // Bağlamı güncelle;
                UpdateContext(context);

                // Girdiyi ön işleme;
                var preprocessedText = PreprocessInput(inputText);

                // Paralel analiz işlemleri;
                var analysisTasks = await PerformParallelAnalysisAsync(preprocessedText);

                // Sonuçları birleştir ve yorumla;
                var result = await InterpretResultsAsync(inputText, preprocessedText, analysisTasks);

                // Kısa süreli hafızaya kaydet;
                await StoreInShortTermMemoryAsync(result);

                _logger.LogInformation($"Successfully understood input: {inputText}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error understanding input: {inputText}");
                return await CreateFallbackResultAsync(inputText, ex);
            }
        }

        /// <summary>
        /// Çoklu girdi için toplu anlama işlemi;
        /// </summary>
        /// <param name="inputs">Girdi listesi</param>
        /// <returns>Anlama sonuçları</returns>
        public async Task<IEnumerable<UnderstandingResult>> BatchUnderstandAsync(IEnumerable<string> inputs)
        {
            ValidateInitialization();

            var inputList = inputs?.ToList() ?? throw new ArgumentNullException(nameof(inputs));

            if (!inputList.Any())
            {
                return Enumerable.Empty<UnderstandingResult>();
            }

            _logger.LogDebug($"Batch processing {inputList.Count} inputs.");

            var results = new List<UnderstandingResult>();
            var semaphore = new SemaphoreSlim(_configuration.MaxConcurrentBatchOperations);
            var tasks = new List<Task>();

            foreach (var input in inputList)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await UnderstandAsync(input);
                        lock (results)
                        {
                            results.Add(result);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// Bağlamsal desen ekler;
        /// </summary>
        /// <param name="domain">Etki alanı</param>
        /// <param name="pattern">Bağlamsal desen</param>
        public void AddContextualPattern(string domain, ContextualPattern pattern)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("Domain cannot be null or empty.", nameof(domain));
            }

            if (pattern == null)
            {
                throw new ArgumentNullException(nameof(pattern));
            }

            lock (_contextualPatterns)
            {
                if (!_contextualPatterns.ContainsKey(domain))
                {
                    _contextualPatterns[domain] = new List<ContextualPattern>();
                }

                _contextualPatterns[domain].Add(pattern);
                _logger.LogDebug($"Added contextual pattern to domain '{domain}': {pattern.Pattern}");
            }
        }

        /// <summary>
        /// Dil modelini günceller;
        /// </summary>
        /// <param name="trainingData">Eğitim verileri</param>
        public async Task UpdateLanguageModelAsync(IEnumerable<LanguageTrainingData> trainingData)
        {
            ValidateInitialization();

            var dataList = trainingData?.ToList() ?? throw new ArgumentNullException(nameof(trainingData));

            if (!dataList.Any())
            {
                return;
            }

            try
            {
                _logger.LogInformation($"Updating language model with {dataList.Count} training samples.");

                // Her bir bileşen için güncelleme;
                var updateTasks = new List<Task>
                {
                    _semanticAnalyzer.UpdateModelAsync(dataList),
                    _syntaxParser.UpdateModelAsync(dataList),
                    _entityExtractor.UpdateModelAsync(dataList),
                    _intentDetector.UpdateModelAsync(dataList)
                };

                await Task.WhenAll(updateTasks);

                // Bağlamsal desenleri yeniden yükle;
                await ReloadContextualPatternsAsync(dataList);

                _logger.LogInformation("Language model updated successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update language model.");
                throw new LanguageUnderstandingException("Language model update failed.", ex);
            }
        }

        /// <summary>
        /// Mevcut bağlamı getirir;
        /// </summary>
        public UnderstandingContext GetCurrentContext() => _currentContext.Clone();

        /// <summary>
        /// Bağlamı sıfırlar;
        /// </summary>
        public void ResetContext()
        {
            _currentContext = new UnderstandingContext();
            _logger.LogDebug("Context reset.");
        }

        /// <summary>
        /// Sistem durumunu kontrol eder;
        /// </summary>
        public async Task<SystemHealthStatus> GetHealthStatusAsync()
        {
            var healthChecks = new List<Task<bool>>
            {
                CheckComponentHealthAsync(_semanticAnalyzer),
                CheckComponentHealthAsync(_syntaxParser),
                CheckComponentHealthAsync(_entityExtractor),
                CheckComponentHealthAsync(_sentimentAnalyzer),
                CheckComponentHealthAsync(_intentDetector)
            };

            var results = await Task.WhenAll(healthChecks);
            var healthyComponents = results.Count(r => r);
            var totalComponents = results.Length;

            var status = new SystemHealthStatus;
            {
                IsInitialized = _isInitialized,
                HealthyComponents = healthyComponents,
                TotalComponents = totalComponents,
                HealthPercentage = (double)healthyComponents / totalComponents * 100,
                LastOperationTime = DateTime.UtcNow,
                ContextSize = _currentContext.GetSize()
            };

            return status;
        }

        #region Private Implementation Methods;

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("LanguageUnderstanding must be initialized before use.");
            }
        }

        private void UpdateContext(UnderstandingContext context)
        {
            if (context != null)
            {
                _currentContext.Merge(context);
            }

            // Bağlamı temizle (eski girişleri kaldır)
            _currentContext.Cleanup(TimeSpan.FromMinutes(_configuration.ContextRetentionMinutes));
        }

        private string PreprocessInput(string input)
        {
            var builder = new StringBuilder(input);

            // Temel temizleme;
            builder = builder.Replace("\r\n", " ")
                            .Replace("\n", " ")
                            .Replace("\t", " ");

            // Fazla boşlukları temizle;
            var text = builder.ToString();
            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            // Trim işlemi;
            text = text.Trim();

            // Büyük/küçük harf normalizasyonu (ilk harfi büyük yap)
            if (!string.IsNullOrEmpty(text) && text.Length > 1)
            {
                text = char.ToUpper(text[0]) + text.Substring(1).ToLower();
            }

            return text;
        }

        private async Task<ParallelAnalysisResults> PerformParallelAnalysisAsync(string text)
        {
            var tasks = new Dictionary<string, Task<object>>
            {
                ["Syntax"] = Task.Run(() => _syntaxParser.ParseAsync(text)),
                ["Semantic"] = Task.Run(() => _semanticAnalyzer.AnalyzeAsync(text)),
                ["Entities"] = Task.Run(() => _entityExtractor.ExtractAsync(text)),
                ["Sentiment"] = Task.Run(() => _sentimentAnalyzer.AnalyzeAsync(text)),
                ["Intent"] = Task.Run(() => _intentDetector.DetectAsync(text, _currentContext))
            };

            await Task.WhenAll(tasks.Values);

            return new ParallelAnalysisResults;
            {
                SyntaxTree = (SyntaxTree)await tasks["Syntax"],
                SemanticAnalysis = (SemanticAnalysisResult)await tasks["Semantic"],
                Entities = (IEnumerable<ExtractedEntity>)await tasks["Entities"],
                Sentiment = (SentimentAnalysisResult)await tasks["Sentiment"],
                Intent = (IntentDetectionResult)await tasks["Intent"]
            };
        }

        private async Task<UnderstandingResult> InterpretResultsAsync(
            string originalText,
            string processedText,
            ParallelAnalysisResults analysis)
        {
            // Güven skorunu hesapla;
            var confidenceScore = CalculateConfidenceScore(analysis);

            // Bağlamsal uygunluğu kontrol et;
            var contextualRelevance = await CheckContextualRelevanceAsync(analysis, processedText);

            // Eylemleri çıkar;
            var extractedActions = ExtractActions(analysis, contextualRelevance);

            // Önceliklendirme;
            var prioritizedActions = PrioritizeActions(extractedActions, analysis.Intent.Priority);

            var result = new UnderstandingResult;
            {
                OriginalText = originalText,
                ProcessedText = processedText,
                ConfidenceScore = confidenceScore,
                DetectedIntent = analysis.Intent.IntentName,
                IntentConfidence = analysis.Intent.Confidence,
                ExtractedEntities = analysis.Entities.ToList(),
                SemanticMeaning = analysis.SemanticAnalysis.MainMeaning,
                Sentiment = analysis.Sentiment.OverallSentiment,
                SuggestedActions = prioritizedActions,
                ContextualRelevance = contextualRelevance,
                Timestamp = DateTime.UtcNow,
                AnalysisMetadata = new AnalysisMetadata;
                {
                    ProcessingTime = DateTime.UtcNow,
                    UsedComponents = GetUsedComponents(analysis),
                    PatternMatches = contextualRelevance.PatternMatches;
                }
            };

            // Bağlamı güncelle;
            _currentContext.AddConversationTurn(result);

            return result;
        }

        private double CalculateConfidenceScore(ParallelAnalysisResults analysis)
        {
            var scores = new List<double>
            {
                analysis.Intent.Confidence,
                analysis.SemanticAnalysis.Confidence,
                analysis.Sentiment.Confidence;
            };

            // Varlık çıkarımı güven skoru;
            if (analysis.Entities.Any())
            {
                var entityConfidence = analysis.Entities.Average(e => e.Confidence);
                scores.Add(entityConfidence);
            }

            // Ağırlıklı ortalama;
            var weights = new[] { 0.4, 0.3, 0.2, 0.1 }; // Intent en yüksek ağırlıkta;
            var weightedAverage = scores.Take(weights.Length)
                                      .Select((score, index) => score * weights[index])
                                      .Sum();

            return Math.Min(weightedAverage, 1.0); // 1.0'ı geçmemesi için;
        }

        private async Task<ContextualRelevance> CheckContextualRelevanceAsync(
            ParallelAnalysisResults analysis,
            string processedText)
        {
            var relevance = new ContextualRelevance;
            {
                IsContextuallyRelevant = true,
                DomainMatches = new List<DomainMatch>(),
                PatternMatches = new List<PatternMatch>()
            };

            // Etki alanı eşleşmelerini kontrol et;
            foreach (var domain in _contextualPatterns.Keys)
            {
                if (_contextualPatterns[domain].Any(pattern =>
                    pattern.Matches(processedText, analysis.Entities)))
                {
                    var matchingPatterns = _contextualPatterns[domain]
                        .Where(pattern => pattern.Matches(processedText, analysis.Entities))
                        .ToList();

                    relevance.DomainMatches.Add(new DomainMatch;
                    {
                        Domain = domain,
                        Confidence = matchingPatterns.Average(p => p.Confidence),
                        MatchingPatterns = matchingPatterns;
                    });
                }
            }

            // Bağlamsal uygunluk skoru;
            if (relevance.DomainMatches.Any())
            {
                relevance.ContextualScore = relevance.DomainMatches.Average(dm => dm.Confidence);
            }
            else;
            {
                relevance.ContextualScore = 0.3; // Minimum skor;
                relevance.IsContextuallyRelevant = relevance.ContextualScore > 0.2;
            }

            return relevance;
        }

        private List<SuggestedAction> ExtractActions(
            ParallelAnalysisResults analysis,
            ContextualRelevance relevance)
        {
            var actions = new List<SuggestedAction>();

            // Intent'ten gelen eylemler;
            if (analysis.Intent.SuggestedActions != null)
            {
                actions.AddRange(analysis.Intent.SuggestedActions);
            }

            // Varlıklardan türetilen eylemler;
            foreach (var entity in analysis.Entities.Where(e => e.SuggestedActions != null))
            {
                actions.AddRange(entity.SuggestedActions);
            }

            // Bağlamsal desenlerden eylemler;
            foreach (var domainMatch in relevance.DomainMatches)
            {
                foreach (var pattern in domainMatch.MatchingPatterns)
                {
                    if (pattern.Actions != null)
                    {
                        actions.AddRange(pattern.Actions);
                    }
                }
            }

            return actions.Distinct(new ActionEqualityComparer()).ToList();
        }

        private List<SuggestedAction> PrioritizeActions(
            List<SuggestedAction> actions,
            IntentPriority priority)
        {
            if (!actions.Any())
            {
                return actions;
            }

            // Öncelik ağırlıkları;
            var priorityWeights = new Dictionary<IntentPriority, double>
            {
                [IntentPriority.Critical] = 1.0,
                [IntentPriority.High] = 0.8,
                [IntentPriority.Medium] = 0.6,
                [IntentPriority.Low] = 0.4,
                [IntentPriority.Background] = 0.2;
            };

            var priorityWeight = priorityWeights[priority];

            // Eylemleri skorlarına göre sırala;
            return actions;
                .Select(action => new;
                {
                    Action = action,
                    Score = action.BasePriority * priorityWeight *
                           (1.0 + (action.ContextualRelevance ?? 0.5))
                })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Action)
                .ToList();
        }

        private async Task StoreInShortTermMemoryAsync(UnderstandingResult result)
        {
            try
            {
                var memoryItem = new MemoryItem;
                {
                    Id = Guid.NewGuid(),
                    Content = result.OriginalText,
                    Type = MemoryItemType.NaturalLanguageInput,
                    Timestamp = result.Timestamp,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Confidence"] = result.ConfidenceScore,
                        ["Intent"] = result.DetectedIntent,
                        ["Sentiment"] = result.Sentiment.ToString(),
                        ["Entities"] = result.ExtractedEntities.Select(e => e.Name).ToList()
                    }
                };

                await _shortTermMemory.StoreAsync(memoryItem, TimeSpan.FromHours(24));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store understanding result in short-term memory.");
            }
        }

        private async Task<UnderstandingResult> CreateFallbackResultAsync(string inputText, Exception exception)
        {
            _logger.LogWarning($"Creating fallback result for input: {inputText}");

            return new UnderstandingResult;
            {
                OriginalText = inputText,
                ProcessedText = inputText,
                ConfidenceScore = 0.1,
                DetectedIntent = "Unknown",
                IntentConfidence = 0.0,
                ExtractedEntities = new List<ExtractedEntity>(),
                SemanticMeaning = "Unable to process input",
                Sentiment = Sentiment.Neutral,
                SuggestedActions = new List<SuggestedAction>
                {
                    new SuggestedAction;
                    {
                        ActionType = "Retry",
                        Description = "Retry with different wording",
                        Priority = 0.5;
                    },
                    new SuggestedAction;
                    {
                        ActionType = "Clarify",
                        Description = "Ask for clarification",
                        Priority = 0.3;
                    }
                },
                ContextualRelevance = new ContextualRelevance;
                {
                    IsContextuallyRelevant = false,
                    ContextualScore = 0.0;
                },
                Timestamp = DateTime.UtcNow,
                Error = new UnderstandingError;
                {
                    ErrorCode = "LU001",
                    Message = "Failed to understand input",
                    ExceptionDetails = exception.Message;
                }
            };
        }

        private async Task<bool> CheckComponentHealthAsync(object component)
        {
            try
            {
                if (component is IHealthCheckable healthCheckable)
                {
                    return await healthCheckable.CheckHealthAsync();
                }

                // Refleksiyonla basit sağlık kontrolü;
                var pingMethod = component.GetType().GetMethod("PingAsync");
                if (pingMethod != null)
                {
                    var task = (Task<bool>)pingMethod.Invoke(component, null);
                    return await task;
                }

                return true; // Kontrol edilemiyorsa sağlıklı kabul et;
            }
            catch
            {
                return false;
            }
        }

        private async Task LoadContextualPatternsAsync()
        {
            try
            {
                // Varsayılan desenleri yükle;
                var defaultPatterns = GetDefaultContextualPatterns();
                foreach (var pattern in defaultPatterns)
                {
                    AddContextualPattern(pattern.Domain, pattern);
                }

                await Task.CompletedTask;
                _logger.LogDebug($"Loaded {defaultPatterns.Count} default contextual patterns.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load contextual patterns.");
            }
        }

        private async Task ReloadContextualPatternsAsync(IEnumerable<LanguageTrainingData> trainingData)
        {
            lock (_contextualPatterns)
            {
                _contextualPatterns.Clear();
            }

            await LoadContextualPatternsAsync();

            // Eğitim verilerinden yeni desenler çıkar;
            ExtractPatternsFromTrainingData(trainingData);
        }

        private void ExtractPatternsFromTrainingData(IEnumerable<LanguageTrainingData> trainingData)
        {
            // Basit desen çıkarımı;
            foreach (var data in trainingData.Where(d => !string.IsNullOrEmpty(d.Domain)))
            {
                var pattern = new ContextualPattern;
                {
                    Domain = data.Domain,
                    Pattern = data.Text,
                    Confidence = 0.7,
                    Actions = data.ExpectedActions?.Select(a => new SuggestedAction;
                    {
                        ActionType = a.ActionType,
                        Description = a.Description,
                        Priority = a.Priority;
                    }).ToList()
                };

                AddContextualPattern(data.Domain, pattern);
            }
        }

        private List<ContextualPattern> GetDefaultContextualPatterns()
        {
            return new List<ContextualPattern>
            {
                new ContextualPattern;
                {
                    Domain = "SystemControl",
                    Pattern = "(start|stop|restart).*",
                    Confidence = 0.9,
                    Actions = new List<SuggestedAction>
                    {
                        new SuggestedAction { ActionType = "SystemCommand", Priority = 0.8 }
                    }
                },
                new ContextualPattern;
                {
                    Domain = "FileManagement",
                    Pattern = "(create|delete|move|copy).*(file|folder|directory)",
                    Confidence = 0.85,
                    Actions = new List<SuggestedAction>
                    {
                        new SuggestedAction { ActionType = "FileOperation", Priority = 0.7 }
                    }
                },
                new ContextualPattern;
                {
                    Domain = "ProjectManagement",
                    Pattern = "(project|task|todo|reminder).*",
                    Confidence = 0.8,
                    Actions = new List<SuggestedAction>
                    {
                        new SuggestedAction { ActionType = "ProjectOperation", Priority = 0.6 }
                    }
                }
            };
        }

        private string[] GetUsedComponents(ParallelAnalysisResults analysis)
        {
            var components = new List<string> { "SyntaxParser", "SemanticAnalyzer", "EntityExtractor", "SentimentAnalyzer", "IntentDetector" };

            if (!analysis.Entities.Any())
            {
                components.Remove("EntityExtractor");
            }

            if (analysis.Sentiment.OverallSentiment == Sentiment.Neutral)
            {
                components.Remove("SentimentAnalyzer");
            }

            return components.ToArray();
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Yönetilen kaynakları serbest bırak;
                    if (_semanticAnalyzer is IDisposable semanticDisposable)
                        semanticDisposable.Dispose();

                    if (_syntaxParser is IDisposable syntaxDisposable)
                        syntaxDisposable.Dispose();

                    if (_entityExtractor is IDisposable entityDisposable)
                        entityDisposable.Dispose();

                    if (_sentimentAnalyzer is IDisposable sentimentDisposable)
                        sentimentDisposable.Dispose();

                    if (_intentDetector is IDisposable intentDisposable)
                        intentDisposable.Dispose();

                    _contextualPatterns.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~LanguageUnderstanding()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Dil anlama yapılandırması;
    /// </summary>
    public class UnderstandingConfiguration;
    {
        public int MaxConcurrentBatchOperations { get; set; } = 10;
        public int ContextRetentionMinutes { get; set; } = 60;
        public double MinimumConfidenceThreshold { get; set; } = 0.3;
        public bool EnableFallbackProcessing { get; set; } = true;
        public int MaxInputLength { get; set; } = 1000;
        public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();

        public void Merge(UnderstandingConfiguration other)
        {
            if (other == null) return;

            MaxConcurrentBatchOperations = other.MaxConcurrentBatchOperations;
            ContextRetentionMinutes = other.ContextRetentionMinutes;
            MinimumConfidenceThreshold = other.MinimumConfidenceThreshold;
            EnableFallbackProcessing = other.EnableFallbackProcessing;
            MaxInputLength = other.MaxInputLength;

            foreach (var setting in other.AdvancedSettings)
            {
                AdvancedSettings[setting.Key] = setting.Value;
            }
        }
    }

    /// <summary>
    /// Dil anlama bağlamı;
    /// </summary>
    public class UnderstandingContext : ICloneable;
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string Domain { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public List<UnderstandingResult> ConversationHistory { get; set; } = new List<UnderstandingResult>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public void Merge(UnderstandingContext other)
        {
            if (other == null) return;

            if (!string.IsNullOrEmpty(other.Domain))
                Domain = other.Domain;

            foreach (var param in other.Parameters)
            {
                Parameters[param.Key] = param.Value;
            }

            LastUpdated = DateTime.UtcNow;
        }

        public void AddConversationTurn(UnderstandingResult result)
        {
            ConversationHistory.Add(result);
            LastUpdated = DateTime.UtcNow;

            // Tarihçeyi sınırla;
            if (ConversationHistory.Count > 100)
            {
                ConversationHistory = ConversationHistory;
                    .Skip(ConversationHistory.Count - 50)
                    .ToList();
            }
        }

        public void Cleanup(TimeSpan retentionTime)
        {
            var cutoff = DateTime.UtcNow - retentionTime;
            ConversationHistory = ConversationHistory;
                .Where(r => r.Timestamp > cutoff)
                .ToList();
        }

        public int GetSize() => ConversationHistory.Count;

        public UnderstandingContext Clone()
        {
            return new UnderstandingContext;
            {
                SessionId = SessionId,
                UserId = UserId,
                Domain = Domain,
                Parameters = new Dictionary<string, object>(Parameters),
                ConversationHistory = new List<UnderstandingResult>(ConversationHistory),
                CreatedAt = CreatedAt,
                LastUpdated = LastUpdated;
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Dil anlama sonucu;
    /// </summary>
    public class UnderstandingResult;
    {
        public string OriginalText { get; set; }
        public string ProcessedText { get; set; }
        public double ConfidenceScore { get; set; }
        public string DetectedIntent { get; set; }
        public double IntentConfidence { get; set; }
        public List<ExtractedEntity> ExtractedEntities { get; set; } = new List<ExtractedEntity>();
        public string SemanticMeaning { get; set; }
        public Sentiment Sentiment { get; set; }
        public List<SuggestedAction> SuggestedActions { get; set; } = new List<SuggestedAction>();
        public ContextualRelevance ContextualRelevance { get; set; }
        public DateTime Timestamp { get; set; }
        public AnalysisMetadata AnalysisMetadata { get; set; }
        public UnderstandingError Error { get; set; }
    }

    /// <summary>
    /// Bağlamsal uygunluk bilgisi;
    /// </summary>
    public class ContextualRelevance;
    {
        public bool IsContextuallyRelevant { get; set; }
        public double ContextualScore { get; set; }
        public List<DomainMatch> DomainMatches { get; set; } = new List<DomainMatch>();
        public List<PatternMatch> PatternMatches { get; set; } = new List<PatternMatch>();
    }

    /// <summary>
    /// Etki alanı eşleşmesi;
    /// </summary>
    public class DomainMatch;
    {
        public string Domain { get; set; }
        public double Confidence { get; set; }
        public List<ContextualPattern> MatchingPatterns { get; set; } = new List<ContextualPattern>();
    }

    /// <summary>
    /// Desen eşleşmesi;
    /// </summary>
    public class PatternMatch;
    {
        public string Pattern { get; set; }
        public double Score { get; set; }
        public Dictionary<string, string> CapturedGroups { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Bağlamsal desen;
    /// </summary>
    public class ContextualPattern;
    {
        public string Domain { get; set; }
        public string Pattern { get; set; }
        public double Confidence { get; set; }
        public List<SuggestedAction> Actions { get; set; }

        public bool Matches(string text, IEnumerable<ExtractedEntity> entities)
        {
            try
            {
                // Basit regex eşleşmesi;
                if (System.Text.RegularExpressions.Regex.IsMatch(text, Pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return true;
                }

                // Varlık bazlı eşleşme;
                if (entities.Any(entity =>
                    Pattern.Contains(entity.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Önerilen eylem;
    /// </summary>
    public class SuggestedAction;
    {
        public string ActionType { get; set; }
        public string Description { get; set; }
        public double Priority { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public double? ContextualRelevance { get; set; }
        public double BasePriority { get; set; } = 0.5;
    }

    /// <summary>
    /// Analiz meta verileri;
    /// </summary>
    public class AnalysisMetadata;
    {
        public DateTime ProcessingTime { get; set; }
        public string[] UsedComponents { get; set; }
        public List<PatternMatch> PatternMatches { get; set; } = new List<PatternMatch>();
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dil anlama hatası;
    /// </summary>
    public class UnderstandingError;
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public string ExceptionDetails { get; set; }
        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Paralel analiz sonuçları;
    /// </summary>
    internal class ParallelAnalysisResults;
    {
        public SyntaxTree SyntaxTree { get; set; }
        public SemanticAnalysisResult SemanticAnalysis { get; set; }
        public IEnumerable<ExtractedEntity> Entities { get; set; }
        public SentimentAnalysisResult Sentiment { get; set; }
        public IntentDetectionResult Intent { get; set; }
    }

    /// <summary>
    /// Eşitlik karşılaştırıcı;
    /// </summary>
    internal class ActionEqualityComparer : IEqualityComparer<SuggestedAction>
    {
        public bool Equals(SuggestedAction x, SuggestedAction y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            return x.ActionType == y.ActionType &&
                   x.Description == y.Description;
        }

        public int GetHashCode(SuggestedAction obj)
        {
            return HashCode.Combine(obj.ActionType, obj.Description);
        }
    }

    /// <summary>
    /// Eğitim verisi;
    /// </summary>
    public class LanguageTrainingData;
    {
        public string Text { get; set; }
        public string Domain { get; set; }
        public string ExpectedIntent { get; set; }
        public List<ExpectedAction> ExpectedActions { get; set; }
        public Dictionary<string, string> ExpectedEntities { get; set; }
    }

    /// <summary>
    /// Beklenen eylem;
    /// </summary>
    public class ExpectedAction;
    {
        public string ActionType { get; set; }
        public string Description { get; set; }
        public double Priority { get; set; }
    }

    /// <summary>
    /// Sistem sağlık durumu;
    /// </summary>
    public class SystemHealthStatus;
    {
        public bool IsInitialized { get; set; }
        public int HealthyComponents { get; set; }
        public int TotalComponents { get; set; }
        public double HealthPercentage { get; set; }
        public DateTime LastOperationTime { get; set; }
        public int ContextSize { get; set; }

        public bool IsHealthy => IsInitialized && HealthPercentage > 70;
    }

    #endregion;

    #region Interfaces from Other Modules;

    // Diğer modüllerden gelen arayüzlerin basitleştirilmiş versiyonları;
    // Gerçek uygulamada bu arayüzler ilgili modüllerden gelir;

    public interface ISemanticAnalyzer : IHealthCheckable;
    {
        Task<SemanticAnalysisResult> AnalyzeAsync(string text);
        Task InitializeAsync();
        Task UpdateModelAsync(IEnumerable<LanguageTrainingData> trainingData);
    }

    public interface IParser : IHealthCheckable;
    {
        Task<SyntaxTree> ParseAsync(string text);
        Task InitializeAsync();
        Task UpdateModelAsync(IEnumerable<LanguageTrainingData> trainingData);
    }

    public interface IEntityExtractor : IHealthCheckable;
    {
        Task<IEnumerable<ExtractedEntity>> ExtractAsync(string text);
        Task InitializeAsync();
        Task UpdateModelAsync(IEnumerable<LanguageTrainingData> trainingData);
    }

    public interface ISentimentAnalyzer : IHealthCheckable;
    {
        Task<SentimentAnalysisResult> AnalyzeAsync(string text);
        Task InitializeAsync();
    }

    public interface IIntentDetector : IHealthCheckable;
    {
        Task<IntentDetectionResult> DetectAsync(string text, UnderstandingContext context);
        Task InitializeAsync();
        Task UpdateModelAsync(IEnumerable<LanguageTrainingData> trainingData);
    }

    public interface IHealthCheckable;
    {
        Task<bool> CheckHealthAsync();
    }

    public class SemanticAnalysisResult;
    {
        public string MainMeaning { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, string> AdditionalMeanings { get; set; } = new Dictionary<string, string>();
    }

    public class SyntaxTree;
    {
        public string Root { get; set; }
        public List<SyntaxNode> Nodes { get; set; } = new List<SyntaxNode>();
    }

    public class SyntaxNode;
    {
        public string Type { get; set; }
        public string Value { get; set; }
        public List<SyntaxNode> Children { get; set; } = new List<SyntaxNode>();
    }

    public class ExtractedEntity;
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
        public List<SuggestedAction> SuggestedActions { get; set; }
    }

    public class SentimentAnalysisResult;
    {
        public Sentiment OverallSentiment { get; set; }
        public double Confidence { get; set; }
        public Dictionary<Sentiment, double> SentimentScores { get; set; } = new Dictionary<Sentiment, double>();
    }

    public enum Sentiment;
    {
        Positive,
        Neutral,
        Negative,
        Mixed;
    }

    public class IntentDetectionResult;
    {
        public string IntentName { get; set; }
        public double Confidence { get; set; }
        public IntentPriority Priority { get; set; }
        public List<SuggestedAction> SuggestedActions { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public enum IntentPriority;
    {
        Critical,
        High,
        Medium,
        Low,
        Background;
    }

    public class MemoryItem;
    {
        public Guid Id { get; set; }
        public string Content { get; set; }
        public MemoryItemType Type { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public enum MemoryItemType;
    {
        NaturalLanguageInput,
        Command,
        Response,
        SystemEvent;
    }

    #endregion;

    #region Custom Exceptions;

    /// <summary>
    /// Dil anlama istisnası;
    /// </summary>
    public class LanguageUnderstandingException : Exception
    {
        public string ErrorCode { get; }
        public DateTime Timestamp { get; }

        public LanguageUnderstandingException(string message)
            : base(message)
        {
            ErrorCode = "LU001";
            Timestamp = DateTime.UtcNow;
        }

        public LanguageUnderstandingException(string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = "LU002";
            Timestamp = DateTime.UtcNow;
        }

        public LanguageUnderstandingException(string errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Timestamp = DateTime.UtcNow;
        }
    }

    #endregion;
}
