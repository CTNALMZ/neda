using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.Common;
using NEDA.Brain.NLP_Engine;
using NEDA.Brain.NeuralNetwork;

namespace NEDA.Interface.TextInput.NaturalLanguageInput;
{
    /// <summary>
    /// Metin işleme motoru - Doğal dil metinlerini işler ve anlamlandırır;
    /// </summary>
    public class TextProcessor : ITextProcessor, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly Tokenizer _tokenizer;
        private readonly SyntaxAnalyzer _syntaxAnalyzer;
        private readonly SemanticAnalyzer _semanticAnalyzer;
        private readonly EntityExtractor _entityExtractor;
        private readonly SentimentAnalyzer _sentimentAnalyzer;

        private readonly TextProcessingConfiguration _configuration;
        private readonly object _processingLock = new object();
        private bool _isInitialized;
        private CancellationTokenSource _processingCts;

        /// <summary>
        /// İşlemci durumu;
        /// </summary>
        public ProcessorState State { get; private set; }

        /// <summary>
        /// İşlem istatistikleri;
        /// </summary>
        public ProcessingStatistics Statistics { get; private set; }

        /// <summary>
        /// Dil modelleri;
        /// </summary>
        public LanguageModels LanguageModels { get; private set; }

        /// <summary>
        /// Yeni bir TextProcessor örneği oluşturur;
        /// </summary>
        public TextProcessor(
            ILogger logger,
            Tokenizer tokenizer,
            SyntaxAnalyzer syntaxAnalyzer,
            SemanticAnalyzer semanticAnalyzer,
            EntityExtractor entityExtractor,
            SentimentAnalyzer sentimentAnalyzer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
            _syntaxAnalyzer = syntaxAnalyzer ?? throw new ArgumentNullException(nameof(syntaxAnalyzer));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _entityExtractor = entityExtractor ?? throw new ArgumentNullException(nameof(entityExtractor));
            _sentimentAnalyzer = sentimentAnalyzer ?? throw new ArgumentNullException(nameof(sentimentAnalyzer));

            _configuration = new TextProcessingConfiguration();
            Statistics = new ProcessingStatistics();
            State = ProcessorState.Stopped;
            _processingCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Metin işlemciyi başlatır;
        /// </summary>
        public async Task InitializeAsync(TextProcessingConfiguration configuration = null)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Metin işlemci başlatılıyor...");

                // Yapılandırmayı güncelle;
                if (configuration != null)
                    _configuration = configuration;

                State = ProcessorState.Initializing;

                // Dil modellerini yükle;
                await LoadLanguageModelsAsync();

                // NLP bileşenlerini başlat;
                await InitializeNLPComponentsAsync();

                // Önbellekleri temizle;
                ClearProcessingCaches();

                // Performans sayaçlarını sıfırla;
                ResetStatistics();

                _isInitialized = true;
                State = ProcessorState.Ready;

                _logger.LogInformation("Metin işlemci başarıyla başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metin işlemci başlatma hatası: {ex.Message}", ex);
                State = ProcessorState.Error;
                throw new TextProcessingException("Metin işlemci başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Ham metni işler ve yapılandırılmış veriye dönüştürür;
        /// </summary>
        public async Task<ProcessedText> ProcessTextAsync(
            string rawText,
            ProcessingOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateProcessorState();

            try
            {
                var startTime = DateTime.UtcNow;

                // Giriş doğrulama;
                ValidateInputText(rawText);

                // İşlem seçeneklerini ayarla;
                var processingOptions = options ?? new ProcessingOptions();

                _logger.LogDebug($"Metin işleniyor: {rawText.Truncate(100)}");

                // Tokenizasyon;
                var tokens = await TokenizeAsync(rawText, processingOptions);

                // Sözdizimi analizi;
                var syntaxTree = await AnalyzeSyntaxAsync(tokens);

                // Anlamsal analiz;
                var semanticAnalysis = await AnalyzeSemanticsAsync(syntaxTree, processingOptions);

                // Varlık çıkarımı;
                var entities = await ExtractEntitiesAsync(semanticAnalysis);

                // Duygu analizi;
                var sentiment = await AnalyzeSentimentAsync(semanticAnalysis);

                // Bağlam çıkarımı;
                var context = await ExtractContextAsync(semanticAnalysis, processingOptions);

                // İşlenmiş metin oluştur;
                var processedText = new ProcessedText;
                {
                    OriginalText = rawText,
                    Tokens = tokens,
                    SyntaxTree = syntaxTree,
                    SemanticAnalysis = semanticAnalysis,
                    Entities = entities,
                    Sentiment = sentiment,
                    Context = context,
                    ProcessingOptions = processingOptions,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    LanguageDetected = DetectLanguage(tokens)
                };

                // İstatistikleri güncelle;
                UpdateStatistics(processedText);

                // Önbelleğe al (eğer etkinse)
                if (_configuration.EnableCaching)
                    CacheProcessedText(processedText);

                _logger.LogInformation($"Metin başarıyla işlendi: {processedText.Entities.Count} varlık, {processedText.Sentiment.Score:F2} duygu skoru");

                return processedText;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Metin işleme iptal edildi");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metin işleme hatası: {ex.Message}", ex);
                throw new TextProcessingException($"Metin işlenemedi: {rawText.Truncate(50)}", ex);
            }
        }

        /// <summary>
        /// Toplu metin işleme gerçekleştirir;
        /// </summary>
        public async Task<BatchProcessingResult> ProcessBatchAsync(
            IEnumerable<string> texts,
            BatchProcessingOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateProcessorState();

            try
            {
                var batchOptions = options ?? new BatchProcessingOptions();
                var results = new List<ProcessedText>();
                var errors = new List<ProcessingError>();
                var startTime = DateTime.UtcNow;

                _logger.LogInformation($"Toplu metin işleme başlatılıyor: {texts.Count()} metin");

                // Paralel işleme için yapılandır;
                var parallelOptions = new ParallelOptions;
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = batchOptions.MaxParallelism ?? Environment.ProcessorCount;
                };

                // Toplu işleme;
                await Parallel.ForEachAsync(
                    texts,
                    parallelOptions,
                    async (text, ct) =>
                    {
                        try
                        {
                            var result = await ProcessTextAsync(text, batchOptions.ProcessingOptions, ct);
                            lock (results)
                            {
                                results.Add(result);
                            }
                        }
                        catch (Exception ex)
                        {
                            var error = new ProcessingError;
                            {
                                Text = text,
                                ErrorMessage = ex.Message,
                                Timestamp = DateTime.UtcNow;
                            };
                            lock (errors)
                            {
                                errors.Add(error);
                            }
                            _logger.LogWarning($"Toplu işleme hatası: {ex.Message}");
                        }
                    });

                var batchResult = new BatchProcessingResult;
                {
                    ProcessedTexts = results,
                    Errors = errors,
                    TotalProcessed = results.Count,
                    TotalErrors = errors.Count,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    SuccessRate = errors.Count == 0 ? 1.0 : (double)results.Count / (results.Count + errors.Count)
                };

                _logger.LogInformation($"Toplu işleme tamamlandı: {batchResult.TotalProcessed} başarılı, {batchResult.TotalErrors} hatalı");

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Toplu metin işleme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Metin sınıflandırma gerçekleştirir;
        /// </summary>
        public async Task<ClassificationResult> ClassifyTextAsync(
            string text,
            ClassificationModel model = null,
            ClassificationOptions options = null)
        {
            ValidateProcessorState();

            try
            {
                // Metni işle;
                var processedText = await ProcessTextAsync(text);

                // Sınıflandırma modelini seç;
                var classificationModel = model ?? await GetDefaultClassifierAsync();

                // Özellik vektörünü çıkar;
                var features = ExtractClassificationFeatures(processedText);

                // Sınıflandırma yap;
                var classification = await classificationModel.ClassifyAsync(features);

                // Güven skorunu hesapla;
                var confidence = CalculateClassificationConfidence(classification, processedText);

                var result = new ClassificationResult;
                {
                    Text = text,
                    Category = classification.PrimaryCategory,
                    Subcategories = classification.Subcategories,
                    Confidence = confidence,
                    FeaturesUsed = features.Keys.ToList(),
                    ProcessingDetails = processedText,
                    Timestamp = DateTime.UtcNow;
                };

                // Model performansını güncelle;
                await UpdateClassifierPerformanceAsync(classificationModel, result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metin sınıflandırma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Metin özetleme gerçekleştirir;
        /// </summary>
        public async Task<SummaryResult> SummarizeTextAsync(
            string text,
            SummarizationOptions options = null)
        {
            ValidateProcessorState();

            try
            {
                var summarizationOptions = options ?? new SummarizationOptions();

                // Metni işle;
                var processedText = await ProcessTextAsync(text);

                // Önemli cümleleri belirle;
                var importantSentences = await ExtractImportantSentencesAsync(processedText, summarizationOptions);

                // Özeti oluştur;
                var summary = await GenerateSummaryAsync(importantSentences, summarizationOptions);

                // Özet kalitesini değerlendir;
                var qualityScore = await EvaluateSummaryQualityAsync(summary, processedText);

                var result = new SummaryResult;
                {
                    OriginalText = text,
                    Summary = summary,
                    CompressionRatio = (double)summary.Length / text.Length,
                    QualityScore = qualityScore,
                    ImportantSentences = importantSentences.Count,
                    OptionsUsed = summarizationOptions,
                    ProcessingDetails = processedText;
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metin özetleme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Metin benzerliğini hesaplar;
        /// </summary>
        public async Task<SimilarityResult> CalculateSimilarityAsync(
            string text1,
            string text2,
            SimilarityAlgorithm algorithm = SimilarityAlgorithm.Cosine)
        {
            ValidateProcessorState();

            try
            {
                // Her iki metni işle;
                var processedText1 = await ProcessTextAsync(text1);
                var processedText2 = await ProcessTextAsync(text2);

                // Özellik vektörlerini çıkar;
                var vector1 = await ExtractFeatureVectorAsync(processedText1);
                var vector2 = await ExtractFeatureVectorAsync(processedText2);

                // Benzerlik hesapla;
                double similarity;
                switch (algorithm)
                {
                    case SimilarityAlgorithm.Cosine:
                        similarity = CalculateCosineSimilarity(vector1, vector2);
                        break;
                    case SimilarityAlgorithm.Jaccard:
                        similarity = CalculateJaccardSimilarity(vector1, vector2);
                        break;
                    case SimilarityAlgorithm.Euclidean:
                        similarity = CalculateEuclideanSimilarity(vector1, vector2);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen benzerlik algoritması: {algorithm}");
                }

                var result = new SimilarityResult;
                {
                    Text1 = text1,
                    Text2 = text2,
                    SimilarityScore = similarity,
                    AlgorithmUsed = algorithm,
                    IsSimilar = similarity >= _configuration.SimilarityThreshold,
                    ProcessingDetails1 = processedText1,
                    ProcessingDetails2 = processedText2;
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Benzerlik hesaplama hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Anahtar kelime çıkarımı yapar;
        /// </summary>
        public async Task<KeywordExtractionResult> ExtractKeywordsAsync(
            string text,
            KeywordExtractionOptions options = null)
        {
            ValidateProcessorState();

            try
            {
                var extractionOptions = options ?? new KeywordExtractionOptions();

                // Metni işle;
                var processedText = await ProcessTextAsync(text);

                // TF-IDF hesapla;
                var tfidfScores = await CalculateTFIDFAsync(processedText);

                // Anahtar kelimeleri belirle;
                var keywords = await IdentifyKeywordsAsync(tfidfScores, extractionOptions);

                // İlgili kelimeleri grupla;
                var keywordGroups = await GroupRelatedKeywordsAsync(keywords, processedText);

                var result = new KeywordExtractionResult;
                {
                    Text = text,
                    Keywords = keywords,
                    KeywordGroups = keywordGroups,
                    TotalKeywords = keywords.Count,
                    TopKeywords = keywords.OrderByDescending(k => k.Score).Take(extractionOptions.TopN).ToList(),
                    ProcessingDetails = processedText;
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Anahtar kelime çıkarım hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Metin işlemciyi durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Metin işlemci durduruluyor...");

                State = ProcessorState.Stopping;

                // İptal token'ını tetikle;
                _processingCts?.Cancel();

                // Aktif işlemleri tamamla;
                await CompletePendingOperationsAsync();

                // Kaynakları serbest bırak;
                await ReleaseResourcesAsync();

                // İstatistikleri kaydet;
                await SaveStatisticsAsync();

                _isInitialized = false;
                State = ProcessorState.Stopped;

                _logger.LogInformation("Metin işlemci başarıyla durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metin işlemci durdurma hatası: {ex.Message}", ex);
                throw;
            }
        }

        #region Private Methods;

        private void ValidateProcessorState()
        {
            lock (_processingLock)
            {
                if (!_isInitialized || State != ProcessorState.Ready)
                    throw new InvalidOperationException("Metin işlemci hazır durumda değil");
            }
        }

        private void ValidateInputText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("İşlenecek metin boş olamaz", nameof(text));

            if (text.Length > _configuration.MaxTextLength)
                throw new ArgumentException($"Metin uzunluğu sınırı aşıldı: {text.Length} > {_configuration.MaxTextLength}");
        }

        private async Task<IEnumerable<Token>> TokenizeAsync(string text, ProcessingOptions options)
        {
            var tokenizationOptions = new TokenizationOptions;
            {
                Language = options.Language,
                IncludePunctuation = options.IncludePunctuation,
                IncludeWhitespace = options.IncludeWhitespace,
                CaseSensitive = options.CaseSensitive;
            };

            return await _tokenizer.TokenizeAsync(text, tokenizationOptions);
        }

        private async Task<SyntaxTree> AnalyzeSyntaxAsync(IEnumerable<Token> tokens)
        {
            return await _syntaxAnalyzer.AnalyzeAsync(tokens);
        }

        private async Task<SemanticAnalysis> AnalyzeSemanticsAsync(SyntaxTree syntaxTree, ProcessingOptions options)
        {
            var semanticOptions = new SemanticAnalysisOptions;
            {
                DeepAnalysis = options.DeepAnalysis,
                IncludeRelations = options.IncludeRelations,
                ContextAware = options.ContextAware;
            };

            return await _semanticAnalyzer.AnalyzeAsync(syntaxTree, semanticOptions);
        }

        private async Task<IEnumerable<Entity>> ExtractEntitiesAsync(SemanticAnalysis semanticAnalysis)
        {
            return await _entityExtractor.ExtractAsync(semanticAnalysis);
        }

        private async Task<SentimentAnalysis> AnalyzeSentimentAsync(SemanticAnalysis semanticAnalysis)
        {
            return await _sentimentAnalyzer.AnalyzeAsync(semanticAnalysis);
        }

        private async Task<TextContext> ExtractContextAsync(SemanticAnalysis semanticAnalysis, ProcessingOptions options)
        {
            // Bağlam çıkarımı için karmaşık analiz;
            var contextBuilder = new ContextBuilder();
            return await contextBuilder.BuildAsync(semanticAnalysis, options);
        }

        private async Task LoadLanguageModelsAsync()
        {
            _logger.LogDebug("Dil modelleri yükleniyor...");

            // Ana dil modelini yükle;
            var mainModel = await LanguageModelLoader.LoadDefaultModelAsync();

            // Özel modelleri yükle;
            var specializedModels = await LoadSpecializedModelsAsync();

            LanguageModels = new LanguageModels;
            {
                MainModel = mainModel,
                SpecializedModels = specializedModels,
                LoadedAt = DateTime.UtcNow;
            };

            _logger.LogInformation($"{specializedModels.Count + 1} dil modeli yüklendi");
        }

        private async Task InitializeNLPComponentsAsync()
        {
            var initializationTasks = new List<Task>
            {
                _tokenizer.InitializeAsync(LanguageModels.MainModel),
                _syntaxAnalyzer.InitializeAsync(),
                _semanticAnalyzer.InitializeAsync(LanguageModels.MainModel),
                _entityExtractor.InitializeAsync(),
                _sentimentAnalyzer.InitializeAsync()
            };

            await Task.WhenAll(initializationTasks);
        }

        private void ClearProcessingCaches()
        {
            // Önbellek temizleme işlemleri;
            var cacheManager = new ProcessingCacheManager();
            cacheManager.ClearAllCaches();
        }

        private void ResetStatistics()
        {
            Statistics = new ProcessingStatistics;
            {
                StartTime = DateTime.UtcNow,
                TotalProcessed = 0,
                TotalErrors = 0,
                TotalCharacters = 0,
                AverageProcessingTime = TimeSpan.Zero;
            };
        }

        private void UpdateStatistics(ProcessedText processedText)
        {
            lock (Statistics)
            {
                Statistics.TotalProcessed++;
                Statistics.TotalCharacters += processedText.OriginalText.Length;
                Statistics.LastProcessed = DateTime.UtcNow;

                // Ortalama işlem süresini güncelle;
                var totalTime = Statistics.AverageProcessingTime.TotalMilliseconds * (Statistics.TotalProcessed - 1);
                totalTime += processedText.ProcessingTime.TotalMilliseconds;
                Statistics.AverageProcessingTime = TimeSpan.FromMilliseconds(totalTime / Statistics.TotalProcessed);

                // Varlık istatistiklerini güncelle;
                UpdateEntityStatistics(processedText.Entities);

                // Duygu istatistiklerini güncelle;
                UpdateSentimentStatistics(processedText.Sentiment);
            }
        }

        private void UpdateEntityStatistics(IEnumerable<Entity> entities)
        {
            foreach (var entity in entities)
            {
                if (!Statistics.EntityCounts.ContainsKey(entity.Type))
                    Statistics.EntityCounts[entity.Type] = 0;

                Statistics.EntityCounts[entity.Type]++;
            }
        }

        private void UpdateSentimentStatistics(SentimentAnalysis sentiment)
        {
            Statistics.TotalSentimentScore += sentiment.Score;
            Statistics.AverageSentiment = Statistics.TotalSentimentScore / Statistics.TotalProcessed;
        }

        private LanguageInfo DetectLanguage(IEnumerable<Token> tokens)
        {
            var languageDetector = new LanguageDetector();
            return languageDetector.Detect(tokens);
        }

        private void CacheProcessedText(ProcessedText processedText)
        {
            var cacheKey = GenerateCacheKey(processedText.OriginalText);
            var cache = ProcessingCache.Instance;
            cache.Set(cacheKey, processedText, TimeSpan.FromMinutes(_configuration.CacheDurationMinutes));
        }

        private string GenerateCacheKey(string text)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
            return Convert.ToBase64String(hash);
        }

        private async Task CompletePendingOperationsAsync()
        {
            // Bekleyen işlemleri tamamla;
            await Task.Delay(100); // Kısa bir süre ver;
        }

        private async Task ReleaseResourcesAsync()
        {
            // Kaynakları serbest bırak;
            var releaseTasks = new List<Task>
            {
                _tokenizer.DisposeAsync().AsTask(),
                _syntaxAnalyzer.DisposeAsync().AsTask(),
                _semanticAnalyzer.DisposeAsync().AsTask(),
                _entityExtractor.DisposeAsync().AsTask(),
                _sentimentAnalyzer.DisposeAsync().AsTask()
            };

            await Task.WhenAll(releaseTasks);
        }

        private async Task SaveStatisticsAsync()
        {
            // İstatistikleri kalıcı depolama alanına kaydet;
            var statsRepository = new StatisticsRepository();
            await statsRepository.SaveAsync(Statistics);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processingCts?.Dispose();

                    // Bileşenleri dispose et;
                    (_tokenizer as IDisposable)?.Dispose();
                    (_syntaxAnalyzer as IDisposable)?.Dispose();
                    (_semanticAnalyzer as IDisposable)?.Dispose();
                    (_entityExtractor as IDisposable)?.Dispose();
                    (_sentimentAnalyzer as IDisposable)?.Dispose();
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

    #region Supporting Types;

    /// <summary>
    /// Metin işleme yapılandırması;
    /// </summary>
    public class TextProcessingConfiguration;
    {
        public int MaxTextLength { get; set; } = 10000;
        public bool EnableCaching { get; set; } = true;
        public int CacheDurationMinutes { get; set; } = 30;
        public double SimilarityThreshold { get; set; } = 0.7;
        public bool EnableParallelProcessing { get; set; } = true;
        public int MaxParallelOperations { get; set; } = Environment.ProcessorCount * 2;
    }

    /// <summary>
    /// İşlemci durumu;
    /// </summary>
    public enum ProcessorState;
    {
        Stopped,
        Initializing,
        Ready,
        Processing,
        Stopping,
        Error;
    }

    /// <summary>
    /// İşlenmiş metin;
    /// </summary>
    public class ProcessedText;
    {
        public string OriginalText { get; set; }
        public IEnumerable<Token> Tokens { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public SemanticAnalysis SemanticAnalysis { get; set; }
        public IEnumerable<Entity> Entities { get; set; }
        public SentimentAnalysis Sentiment { get; set; }
        public TextContext Context { get; set; }
        public ProcessingOptions ProcessingOptions { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public LanguageInfo LanguageDetected { get; set; }
    }

    /// <summary>
    /// İşlem istatistikleri;
    /// </summary>
    public class ProcessingStatistics;
    {
        public DateTime StartTime { get; set; }
        public DateTime LastProcessed { get; set; }
        public int TotalProcessed { get; set; }
        public int TotalErrors { get; set; }
        public long TotalCharacters { get; set; }
        public TimeSpan AverageProcessingTime { get; set; }
        public double AverageSentiment { get; set; }
        public Dictionary<EntityType, int> EntityCounts { get; set; } = new Dictionary<EntityType, int>();
        public double TotalSentimentScore { get; set; }
    }

    /// <summary>
    /// İşleme seçenekleri;
    /// </summary>
    public class ProcessingOptions;
    {
        public Language Language { get; set; } = Language.Auto;
        public bool IncludePunctuation { get; set; } = true;
        public bool IncludeWhitespace { get; set; } = false;
        public bool CaseSensitive { get; set; } = false;
        public bool DeepAnalysis { get; set; } = false;
        public bool IncludeRelations { get; set; } = true;
        public bool ContextAware { get; set; } = true;
        public int MaxTokens { get; set; } = 1000;
    }

    /// <summary>
    /// Dil modelleri;
    /// </summary>
    public class LanguageModels;
    {
        public LanguageModel MainModel { get; set; }
        public Dictionary<string, LanguageModel> SpecializedModels { get; set; } = new Dictionary<string, LanguageModel>();
        public DateTime LoadedAt { get; set; }
    }

    /// <summary>
    /// Toplu işleme seçenekleri;
    /// </summary>
    public class BatchProcessingOptions;
    {
        public ProcessingOptions ProcessingOptions { get; set; } = new ProcessingOptions();
        public int? MaxParallelism { get; set; }
        public bool StopOnFirstError { get; set; } = false;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Toplu işleme sonucu;
    /// </summary>
    public class BatchProcessingResult;
    {
        public IEnumerable<ProcessedText> ProcessedTexts { get; set; }
        public IEnumerable<ProcessingError> Errors { get; set; }
        public int TotalProcessed { get; set; }
        public int TotalErrors { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public double SuccessRate { get; set; }
    }

    /// <summary>
    /// İşleme hatası;
    /// </summary>
    public class ProcessingError;
    {
        public string Text { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
        public string StackTrace { get; set; }
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Metin işlemci arayüzü;
    /// </summary>
    public interface ITextProcessor : IDisposable
    {
        Task InitializeAsync(TextProcessingConfiguration configuration = null);
        Task<ProcessedText> ProcessTextAsync(string rawText, ProcessingOptions options = null, CancellationToken cancellationToken = default);
        Task<BatchProcessingResult> ProcessBatchAsync(IEnumerable<string> texts, BatchProcessingOptions options = null, CancellationToken cancellationToken = default);
        Task ShutdownAsync();
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Metin işleme istisnası;
    /// </summary>
    public class TextProcessingException : Exception
    {
        public TextProcessingException() { }
        public TextProcessingException(string message) : base(message) { }
        public TextProcessingException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
