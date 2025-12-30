using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.NeuralNetwork.Common;
using NEDA.Brain.NLP_Engine;

namespace NEDA.NeuralNetwork.PatternRecognition.TextPatterns;
{
    /// <summary>
    /// Metin analiz motoru - Metin desenleri, yapısal analiz ve anlamsal örüntü tanıma;
    /// NLP teknikleri ve makine öğrenimi modellerini entegre eder;
    /// </summary>
    public class TextAnalyzer : ITextAnalyzer, IDisposable;
    {
        private readonly ILogger<TextAnalyzer> _logger;
        private readonly TextAnalysisConfig _config;
        private readonly ITokenizer _tokenizer;
        private readonly IStemmer _stemmer;
        private readonly IWordEmbedding _wordEmbedding;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly ISyntaxAnalyzer _syntaxAnalyzer;
        private readonly ConcurrentDictionary<string, TextPattern> _textPatterns;
        private readonly ConcurrentDictionary<string, LanguageModel> _languageModels;
        private readonly TextCorpus _corpus;
        private readonly SemaphoreSlim _analysisLock;
        private readonly MetricsCollector _metricsCollector;
        private readonly CancellationTokenSource _processingCancellation;
        private Task _backgroundProcessingTask;
        private bool _isInitialized;
        private bool _isDisposed;
        private DateTime _lastModelUpdate;
        private readonly object _modelUpdateLock = new object();

        /// <summary>
        /// Metin analiz motorunu başlatır;
        /// </summary>
        public TextAnalyzer(
            ILogger<TextAnalyzer> logger,
            IOptions<TextAnalysisConfig> config,
            ITokenizer tokenizer = null,
            IStemmer stemmer = null,
            IWordEmbedding wordEmbedding = null,
            ISemanticAnalyzer semanticAnalyzer = null,
            ISyntaxAnalyzer syntaxAnalyzer = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? TextAnalysisConfig.Default;

            _tokenizer = tokenizer ?? new Tokenizer();
            _stemmer = stemmer ?? new Stemmer();
            _wordEmbedding = wordEmbedding ?? new Word2VecEmbedding();
            _semanticAnalyzer = semanticAnalyzer ?? new SemanticAnalyzer();
            _syntaxAnalyzer = syntaxAnalyzer ?? new SyntaxAnalyzer();

            _textPatterns = new ConcurrentDictionary<string, TextPattern>();
            _languageModels = new ConcurrentDictionary<string, LanguageModel>();
            _corpus = new TextCorpus(_config.MaxCorpusSize);

            _analysisLock = new SemaphoreSlim(1, 1);
            _metricsCollector = new MetricsCollector("TextAnalyzer");
            _processingCancellation = new CancellationTokenSource();

            _logger.LogInformation("TextAnalyzer initialized with language: {Language}",
                _config.DefaultLanguage);
        }

        /// <summary>
        /// Asenkron başlatma işlemi;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                await ValidateConfigurationAsync();
                await LoadLanguageModelsAsync();
                await InitializeNLPComponentsAsync();
                await StartBackgroundProcessingAsync();

                _isInitialized = true;
                _logger.LogInformation("TextAnalyzer initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize TextAnalyzer");
                throw new TextAnalyzerInitializationException(
                    "TextAnalyzer initialization failed", ex);
            }
        }

        /// <summary>
        /// Metni analiz eder ve detaylı sonuçlar üretir;
        /// </summary>
        public async Task<TextAnalysisResult> AnalyzeTextAsync(
            string text,
            AnalysisOptions options = null)
        {
            ValidateText(text);
            options ??= AnalysisOptions.Default;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await _analysisLock.WaitAsync();

                // Önbellek kontrolü;
                var cacheKey = GenerateCacheKey(text, options);
                if (options.UseCache && _corpus.TryGetCachedResult(cacheKey, out var cachedResult))
                {
                    _metricsCollector.IncrementCounter("cache_hits");
                    return cachedResult;
                }

                // Temel analizler;
                var basicAnalysis = await PerformBasicAnalysisAsync(text, options);
                var tokenAnalysis = await AnalyzeTokensAsync(basicAnalysis.Tokens, options);
                var semanticAnalysis = await PerformSemanticAnalysisAsync(text, basicAnalysis, options);
                var syntacticAnalysis = await PerformSyntacticAnalysisAsync(text, options);

                // Desen tespiti;
                var patternAnalysis = await DetectTextPatternsAsync(text, basicAnalysis, options);

                // Duygu analizi;
                var sentimentAnalysis = options.PerformSentimentAnalysis;
                    ? await AnalyzeSentimentAsync(text, semanticAnalysis, options)
                    : null;

                // Varlık tanıma;
                var entityAnalysis = options.PerformEntityRecognition;
                    ? await RecognizeEntitiesAsync(text, semanticAnalysis, options)
                    : null;

                // Sonuçları birleştir;
                var result = new TextAnalysisResult;
                {
                    Text = text,
                    BasicAnalysis = basicAnalysis,
                    TokenAnalysis = tokenAnalysis,
                    SemanticAnalysis = semanticAnalysis,
                    SyntacticAnalysis = syntacticAnalysis,
                    PatternAnalysis = patternAnalysis,
                    SentimentAnalysis = sentimentAnalysis,
                    EntityAnalysis = entityAnalysis,
                    ProcessingTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new AnalysisMetadata;
                    {
                        Language = basicAnalysis.Language,
                        TextLength = text.Length,
                        WordCount = basicAnalysis.WordCount,
                        SentenceCount = basicAnalysis.SentenceCount;
                    }
                };

                // Sonuç iyileştirme;
                if (options.PerformResultRefinement)
                {
                    result = await RefineAnalysisResultAsync(result, options);
                }

                // Önbelleğe ekle;
                if (options.UseCache)
                {
                    _corpus.CacheResult(cacheKey, result);
                }

                // Korpüse ekle (öğrenme için)
                if (options.AddToCorpus)
                {
                    await AddToCorpusAsync(text, result);
                }

                stopwatch.Stop();
                _metricsCollector.RecordLatency("text_analysis", stopwatch.Elapsed);

                _logger.LogDebug("Text analysis completed in {ProcessingTime}ms",
                    result.ProcessingTime.TotalMilliseconds);

                return result;
            }
            finally
            {
                _analysisLock.Release();
            }
        }

        /// <summary>
        /// Metin koleksiyonunu analiz eder;
        /// </summary>
        public async Task<BatchAnalysisResult> AnalyzeTextBatchAsync(
            IEnumerable<string> texts,
            BatchAnalysisOptions options = null)
        {
            ValidateTextBatch(texts);
            options ??= BatchAnalysisOptions.Default;

            var textList = texts.ToList();
            var results = new ConcurrentBag<TextAnalysisResult>();
            var overallResult = new BatchAnalysisResult();

            var parallelOptions = new ParallelOptions;
            {
                MaxDegreeOfParallelism = options.MaxParallelism,
                CancellationToken = options.CancellationToken;
            };

            await Parallel.ForEachAsync(textList, parallelOptions, async (text, cancellationToken) =>
            {
                try
                {
                    var analysisOptions = options.AnalysisOptions ?? AnalysisOptions.Default;
                    var result = await AnalyzeTextAsync(text, analysisOptions);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to analyze text in batch");
                }
            });

            // Sonuçları birleştir;
            overallResult.IndividualResults = results.ToList();
            overallResult.OverallStatistics = CalculateBatchStatistics(overallResult.IndividualResults);
            overallResult.CommonPatterns = FindCommonPatternsAcrossTexts(overallResult.IndividualResults, options);
            overallResult.ComparativeAnalysis = await PerformComparativeAnalysisAsync(overallResult.IndividualResults);

            // Tematik analiz;
            if (options.PerformThematicAnalysis)
            {
                overallResult.ThematicAnalysis = await AnalyzeThemesAsync(overallResult.IndividualResults, options);
            }

            _logger.LogInformation("Batch analysis completed: {TextCount} texts analyzed",
                textList.Count);

            return overallResult;
        }

        /// <summary>
        /// Metin desenlerini tespit eder;
        /// </summary>
        public async Task<PatternDetectionResult> DetectTextPatternsAsync(
            string text,
            PatternDetectionOptions options = null)
        {
            ValidateText(text);
            options ??= PatternDetectionOptions.Default;

            var tokens = await _tokenizer.TokenizeAsync(text, options.TokenizationOptions);
            var patterns = new List<TextPattern>();

            // Sıklık bazlı desenler;
            if (options.DetectFrequencyPatterns)
            {
                var frequencyPatterns = await DetectFrequencyPatternsAsync(tokens, options);
                patterns.AddRange(frequencyPatterns);
            }

            // Yapısal desenler;
            if (options.DetectStructuralPatterns)
            {
                var structuralPatterns = await DetectStructuralPatternsAsync(text, options);
                patterns.AddRange(structuralPatterns);
            }

            // Anlamsal desenler;
            if (options.DetectSemanticPatterns)
            {
                var semanticPatterns = await DetectSemanticPatternsAsync(text, tokens, options);
                patterns.AddRange(semanticPatterns);
            }

            // Dizilim desenleri;
            if (options.DetectSequentialPatterns)
            {
                var sequentialPatterns = await DetectSequentialPatternsAsync(tokens, options);
                patterns.AddRange(sequentialPatterns);
            }

            // Anomali desenleri;
            if (options.DetectAnomalyPatterns)
            {
                var anomalyPatterns = await DetectAnomalyPatternsAsync(text, tokens, patterns, options);
                patterns.AddRange(anomalyPatterns);
            }

            // Desenleri birleştir ve çakışmaları çöz;
            var mergedPatterns = MergeSimilarPatterns(patterns, options.MergeThreshold);

            return new PatternDetectionResult;
            {
                DetectedPatterns = mergedPatterns,
                PatternCount = mergedPatterns.Count,
                PatternDensity = CalculatePatternDensity(mergedPatterns, text.Length),
                Confidence = CalculateOverallPatternConfidence(mergedPatterns)
            };
        }

        /// <summary>
        /// Metin sınıflandırma yapar;
        /// </summary>
        public async Task<ClassificationResult> ClassifyTextAsync(
            string text,
            ClassificationOptions options = null)
        {
            ValidateText(text);
            options ??= ClassificationOptions.Default;

            var features = await ExtractClassificationFeaturesAsync(text, options);
            var classification = await PerformClassificationAsync(features, options);

            var result = new ClassificationResult;
            {
                Text = text,
                PredictedCategory = classification.PredictedCategory,
                Confidence = classification.Confidence,
                AllProbabilities = classification.Probabilities,
                Features = features,
                ClassificationTime = DateTime.UtcNow;
            };

            // Detaylı analiz;
            if (options.PerformDetailedAnalysis)
            {
                result.DetailedAnalysis = await PerformDetailedClassificationAnalysisAsync(text, classification, options);
            }

            return result;
        }

        /// <summary>
        /// Metin kümeleme yapar;
        /// </summary>
        public async Task<ClusteringResult> ClusterTextsAsync(
            IEnumerable<string> texts,
            ClusteringOptions options = null)
        {
            ValidateTextBatch(texts);
            options ??= ClusteringOptions.Default;

            var textList = texts.ToList();
            var features = await ExtractClusteringFeaturesAsync(textList, options);
            var clusters = await PerformClusteringAsync(features, options);

            return new ClusteringResult;
            {
                Texts = textList,
                Clusters = clusters,
                ClusterCount = clusters.Count,
                SilhouetteScore = CalculateSilhouetteScore(features, clusters),
                FeatureVectors = features;
            };
        }

        /// <summary>
        /// Anlamsal benzerlik analizi yapar;
        /// </summary>
        public async Task<SimilarityAnalysisResult> AnalyzeSemanticSimilarityAsync(
            string text1,
            string text2,
            SimilarityOptions options = null)
        {
            ValidateText(text1);
            ValidateText(text2);
            options ??= SimilarityOptions.Default;

            var embedding1 = await GetTextEmbeddingAsync(text1, options);
            var embedding2 = await GetTextEmbeddingAsync(text2, options);

            var similarity = CalculateSimilarity(embedding1, embedding2, options.Metric);
            var confidence = CalculateSimilarityConfidence(similarity, embedding1, embedding2);

            return new SimilarityAnalysisResult;
            {
                Text1 = text1,
                Text2 = text2,
                SimilarityScore = similarity,
                Confidence = confidence,
                Embedding1 = embedding1,
                Embedding2 = embedding2,
                IsSimilar = similarity >= options.SimilarityThreshold;
            };
        }

        /// <summary>
        /// Metin özetleme yapar;
        /// </summary>
        public async Task<SummarizationResult> SummarizeTextAsync(
            string text,
            SummarizationOptions options = null)
        {
            ValidateText(text);
            options ??= SummarizationOptions.Default;

            var sentences = await SplitIntoSentencesAsync(text);
            var sentenceScores = await ScoreSentencesAsync(sentences, options);
            var summarySentences = SelectSummarySentences(sentences, sentenceScores, options);

            var summary = string.Join(" ", summarySentences);
            var compressionRatio = (double)summary.Length / text.Length;

            return new SummarizationResult;
            {
                OriginalText = text,
                Summary = summary,
                CompressionRatio = compressionRatio,
                SentenceCount = summarySentences.Count,
                SelectedSentences = summarySentences,
                SentenceScores = sentenceScores;
            };
        }

        /// <summary>
        /// Anahtar kelime çıkarımı yapar;
        /// </summary>
        public async Task<KeywordExtractionResult> ExtractKeywordsAsync(
            string text,
            ExtractionOptions options = null)
        {
            ValidateText(text);
            options ??= ExtractionOptions.Default;

            var tokens = await _tokenizer.TokenizeAsync(text);
            var candidateKeywords = await ExtractCandidateKeywordsAsync(tokens, options);
            var scoredKeywords = await ScoreKeywordsAsync(candidateKeywords, text, options);

            var topKeywords = scoredKeywords;
                .OrderByDescending(k => k.Score)
                .Take(options.MaxKeywords)
                .ToList();

            return new KeywordExtractionResult;
            {
                Text = text,
                Keywords = topKeywords,
                TotalCandidates = candidateKeywords.Count,
                KeywordDensity = (double)topKeywords.Count / tokens.Count;
            };
        }

        /// <summary>
        /// Konu modelleme yapar;
        /// </summary>
        public async Task<TopicModelingResult> ModelTopicsAsync(
            IEnumerable<string> documents,
            TopicModelingOptions options = null)
        {
            ValidateTextBatch(documents);
            options ??= TopicModelingOptions.Default;

            var documentList = documents.ToList();
            var processedDocs = await PreprocessDocumentsAsync(documentList, options);
            var topics = await ExtractTopicsAsync(processedDocs, options);
            var documentTopics = await AssignTopicsToDocumentsAsync(documentList, topics, options);

            return new TopicModelingResult;
            {
                Documents = documentList,
                Topics = topics,
                DocumentTopics = documentTopics,
                TopicCount = topics.Count,
                CoherenceScore = CalculateTopicCoherence(topics, processedDocs),
                Perplexity = await CalculatePerplexityAsync(processedDocs, topics, options)
            };
        }

        /// <summary>
        /// Metin kalitesi analizi yapar;
        /// </summary>
        public async Task<TextQualityResult> AnalyzeTextQualityAsync(
            string text,
            QualityAnalysisOptions options = null)
        {
            ValidateText(text);
            options ??= QualityAnalysisOptions.Default;

            var readabilityScore = await CalculateReadabilityAsync(text, options);
            var grammarScore = await AnalyzeGrammarAsync(text, options);
            var styleScore = await AnalyzeStyleAsync(text, options);
            var complexityScore = await AnalyzeComplexityAsync(text, options);

            var overallQuality = CalculateOverallQuality(
                readabilityScore,
                grammarScore,
                styleScore,
                complexityScore);

            return new TextQualityResult;
            {
                Text = text,
                ReadabilityScore = readabilityScore,
                GrammarScore = grammarScore,
                StyleScore = styleScore,
                ComplexityScore = complexityScore,
                OverallQuality = overallQuality,
                QualityGrade = DetermineQualityGrade(overallQuality),
                Suggestions = await GenerateQualitySuggestionsAsync(text, overallQuality, options)
            };
        }

        /// <summary>
        /// Metin öğrenme ve model güncellemesi yapar;
        /// </summary>
        public async Task<LearningResult> LearnFromTextsAsync(
            IEnumerable<TextSample> samples,
            LearningOptions options = null)
        {
            ValidateLearningSamples(samples);
            options ??= LearningOptions.Default;

            var sampleList = samples.ToList();
            var learningContext = new LearningContext;
            {
                Samples = sampleList,
                Options = options,
                CurrentPatterns = _textPatterns.Values.ToList(),
                LanguageModels = _languageModels;
            };

            var result = await PerformTextLearningAsync(learningContext);

            if (result.Success)
            {
                await UpdateTextModelsAsync(result);
                await UpdatePatternDatabaseAsync(result.NewPatterns);

                lock (_modelUpdateLock)
                {
                    _lastModelUpdate = DateTime.UtcNow;
                }
            }

            _logger.LogInformation("Text learning completed: {NewPatterns} new patterns learned",
                result.NewPatterns.Count);

            return result;
        }

        /// <summary>
        /// Gerçek zamanlı metin analizi başlatır;
        /// </summary>
        public async Task<RealTimeAnalysisResult> StartRealTimeAnalysisAsync(
            ITextStream textStream,
            RealTimeOptions options)
        {
            ValidateTextStream(textStream);
            ValidateRealTimeOptions(options);

            var analyzer = new RealTimeTextAnalyzer(
                textStream,
                options,
                this,
                _logger);

            await analyzer.InitializeAsync();
            await analyzer.StartAnalysisAsync();

            return new RealTimeAnalysisResult;
            {
                Analyzer = analyzer,
                Status = AnalysisStatus.Running,
                StartedAt = DateTime.UtcNow;
            };
        }

        #region Private Implementation;

        private async Task StartBackgroundProcessingAsync()
        {
            _backgroundProcessingTask = Task.Run(async () =>
            {
                while (!_processingCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessCorpusUpdatesAsync();
                        await UpdateLanguageModelsAsync();
                        await CleanupCacheAsync();

                        await Task.Delay(_config.BackgroundProcessingInterval, _processingCancellation.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in background processing");
                        await Task.Delay(TimeSpan.FromSeconds(5), _processingCancellation.Token);
                    }
                }
            }, _processingCancellation.Token);
        }

        private async Task<BasicTextAnalysis> PerformBasicAnalysisAsync(
            string text,
            AnalysisOptions options)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var tokens = await _tokenizer.TokenizeAsync(text);
            var sentences = await SplitIntoSentencesAsync(text);
            var language = await DetectLanguageAsync(text);
            var wordCount = tokens.Count;
            var sentenceCount = sentences.Count;
            var characterCount = text.Length;

            var readability = options.CalculateReadability;
                ? await CalculateReadabilityAsync(text)
                : 0;

            stopwatch.Stop();

            return new BasicTextAnalysis;
            {
                Tokens = tokens,
                Sentences = sentences,
                Language = language,
                WordCount = wordCount,
                SentenceCount = sentenceCount,
                CharacterCount = characterCount,
                ReadabilityScore = readability,
                ProcessingTime = stopwatch.Elapsed;
            };
        }

        private async Task<TokenAnalysis> AnalyzeTokensAsync(
            List<Token> tokens,
            AnalysisOptions options)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var stemmedTokens = await _stemmer.StemAsync(tokens);
            var posTags = await _syntaxAnalyzer.TagPartsOfSpeechAsync(tokens);
            var frequencies = CalculateTokenFrequencies(tokens);
            var uniqueTokens = tokens.Select(t => t.Value).Distinct().ToList();

            var tokenEmbeddings = options.GenerateEmbeddings;
                ? await _wordEmbedding.GetEmbeddingsAsync(tokens.Select(t => t.Value).ToList())
                : null;

            stopwatch.Stop();

            return new TokenAnalysis;
            {
                OriginalTokens = tokens,
                StemmedTokens = stemmedTokens,
                PosTags = posTags,
                TokenFrequencies = frequencies,
                UniqueTokenCount = uniqueTokens.Count,
                TokenEmbeddings = tokenEmbeddings,
                ProcessingTime = stopwatch.Elapsed;
            };
        }

        private async Task<SemanticAnalysis> PerformSemanticAnalysisAsync(
            string text,
            BasicTextAnalysis basicAnalysis,
            AnalysisOptions options)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var semanticGraph = await _semanticAnalyzer.BuildSemanticGraphAsync(text);
            var topics = await ExtractTopicsFromTextAsync(text, options.TopicExtraction);
            var entities = options.ExtractEntities;
                ? await _semanticAnalyzer.ExtractEntitiesAsync(text)
                : new List<Entity>();

            var relationships = await ExtractSemanticRelationshipsAsync(text, entities);
            var sentiment = options.AnalyzeSentiment;
                ? await _semanticAnalyzer.AnalyzeSentimentAsync(text)
                : null;

            stopwatch.Stop();

            return new SemanticAnalysis;
            {
                SemanticGraph = semanticGraph,
                Topics = topics,
                Entities = entities,
                Relationships = relationships,
                Sentiment = sentiment,
                ProcessingTime = stopwatch.Elapsed;
            };
        }

        private async Task<List<TextPattern>> DetectFrequencyPatternsAsync(
            List<Token> tokens,
            PatternDetectionOptions options)
        {
            var patterns = new List<TextPattern>();

            // N-gram desenleri;
            var ngrams = await ExtractNGramsAsync(tokens, options.NGramSizes);
            foreach (var ngram in ngrams)
            {
                if (ngram.Frequency >= options.MinimumFrequency)
                {
                    patterns.Add(new TextPattern;
                    {
                        Type = PatternType.Frequency,
                        PatternText = ngram.Text,
                        Frequency = ngram.Frequency,
                        Confidence = CalculateFrequencyConfidence(ngram.Frequency, tokens.Count)
                    });
                }
            }

            // Collocation desenleri;
            var collocations = await FindCollocationsAsync(tokens, options);
            patterns.AddRange(collocations);

            return patterns;
        }

        private async Task<List<TextPattern>> DetectStructuralPatternsAsync(
            string text,
            PatternDetectionOptions options)
        {
            var patterns = new List<TextPattern>();

            // Cümle yapısı desenleri;
            var sentencePatterns = await AnalyzeSentenceStructureAsync(text, options);
            patterns.AddRange(sentencePatterns);

            // Paragraf yapısı desenleri;
            var paragraphPatterns = await AnalyzeParagraphStructureAsync(text, options);
            patterns.AddRange(paragraphPatterns);

            // Formatlama desenleri;
            var formattingPatterns = await AnalyzeFormattingPatternsAsync(text, options);
            patterns.AddRange(formattingPatterns);

            return patterns;
        }

        private async Task<string> DetectLanguageAsync(string text)
        {
            if (text.Length < _config.MinTextLengthForLanguageDetection)
                return _config.DefaultLanguage;

            try
            {
                var language = await _languageModels.Keys;
                    .OrderByDescending(lang => CalculateLanguageProbability(text, lang))
                    .FirstOrDefaultAsync();

                return language ?? _config.DefaultLanguage;
            }
            catch
            {
                return _config.DefaultLanguage;
            }
        }

        private string GenerateCacheKey(string text, AnalysisOptions options)
        {
            var textHash = ComputeTextHash(text);
            return $"{textHash}_{options.GetHashCode()}_{text.Length}";
        }

        private string ComputeTextHash(string text)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        #endregion;

        #region Validation Methods;

        private async Task ValidateConfigurationAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.DefaultLanguage))
                throw new InvalidConfigurationException("Default language cannot be empty");

            if (_config.MaxTextLength <= 0)
                throw new InvalidConfigurationException("Max text length must be positive");

            if (_config.MinTextLengthForAnalysis < 1)
                throw new InvalidConfigurationException("Min text length for analysis must be at least 1");

            await Task.CompletedTask;
        }

        private void ValidateText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            if (text.Length > _config.MaxTextLength)
                throw new ArgumentException($"Text exceeds maximum length of {_config.MaxTextLength} characters", nameof(text));

            if (text.Length < _config.MinTextLengthForAnalysis)
                throw new ArgumentException($"Text too short. Minimum length: {_config.MinTextLengthForAnalysis}", nameof(text));
        }

        private void ValidateTextBatch(IEnumerable<string> texts)
        {
            if (texts == null)
                throw new ArgumentNullException(nameof(texts));

            var textList = texts.ToList();
            if (!textList.Any())
                throw new ArgumentException("Text batch cannot be empty", nameof(texts));

            if (textList.Count > _config.MaxBatchSize)
                throw new ArgumentException($"Batch size exceeds maximum of {_config.MaxBatchSize}", nameof(texts));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _processingCancellation.Cancel();

                    try
                    {
                        _backgroundProcessingTask?.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (AggregateException)
                    {
                        // Task iptal istisnalarını yoksay;
                    }

                    _processingCancellation.Dispose();
                    _analysisLock.Dispose();
                    _corpus?.Dispose();
                }

                _isDisposed = true;
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

    public class TextAnalysisConfig;
    {
        public string DefaultLanguage { get; set; } = "en";
        public int MaxTextLength { get; set; } = 1000000;
        public int MinTextLengthForAnalysis { get; set; } = 10;
        public int MinTextLengthForLanguageDetection { get; set; } = 50;
        public int MaxBatchSize { get; set; } = 1000;
        public int MaxCorpusSize { get; set; } = 10000;
        public TimeSpan BackgroundProcessingInterval { get; set; } = TimeSpan.FromMinutes(5);
        public List<string> SupportedLanguages { get; set; } = new List<string> { "en", "tr", "de", "fr", "es" };
        public bool EnableCaching { get; set; } = true;
        public int CacheSize { get; set; } = 1000;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
        public double DefaultConfidenceThreshold { get; set; } = 0.7;

        public static TextAnalysisConfig Default => new TextAnalysisConfig();
    }

    public class TextAnalysisResult;
    {
        public string Text { get; set; }
        public BasicTextAnalysis BasicAnalysis { get; set; }
        public TokenAnalysis TokenAnalysis { get; set; }
        public SemanticAnalysis SemanticAnalysis { get; set; }
        public SyntacticAnalysis SyntacticAnalysis { get; set; }
        public PatternAnalysis PatternAnalysis { get; set; }
        public SentimentAnalysis SentimentAnalysis { get; set; }
        public EntityAnalysis EntityAnalysis { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public AnalysisMetadata Metadata { get; set; }
    }

    public class BasicTextAnalysis;
    {
        public List<Token> Tokens { get; set; }
        public List<string> Sentences { get; set; }
        public string Language { get; set; }
        public int WordCount { get; set; }
        public int SentenceCount { get; set; }
        public int CharacterCount { get; set; }
        public double ReadabilityScore { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class TokenAnalysis;
    {
        public List<Token> OriginalTokens { get; set; }
        public List<Token> StemmedTokens { get; set; }
        public List<PosTag> PosTags { get; set; }
        public Dictionary<string, int> TokenFrequencies { get; set; }
        public int UniqueTokenCount { get; set; }
        public List<double[]> TokenEmbeddings { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class SemanticAnalysis;
    {
        public SemanticGraph SemanticGraph { get; set; }
        public List<Topic> Topics { get; set; }
        public List<Entity> Entities { get; set; }
        public List<Relationship> Relationships { get; set; }
        public SentimentResult Sentiment { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class PatternDetectionResult;
    {
        public List<TextPattern> DetectedPatterns { get; set; }
        public int PatternCount { get; set; }
        public double PatternDensity { get; set; }
        public double Confidence { get; set; }
        public Dictionary<PatternType, int> PatternTypeCounts { get; set; }
    }

    public class TextPattern;
    {
        public string PatternId { get; set; }
        public PatternType Type { get; set; }
        public string PatternText { get; set; }
        public double Frequency { get; set; }
        public double Confidence { get; set; }
        public List<PatternContext> Contexts { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ClassificationResult;
    {
        public string Text { get; set; }
        public string PredictedCategory { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> AllProbabilities { get; set; }
        public FeatureVector Features { get; set; }
        public DateTime ClassificationTime { get; set; }
        public DetailedClassificationAnalysis DetailedAnalysis { get; set; }
    }

    public class SimilarityAnalysisResult;
    {
        public string Text1 { get; set; }
        public string Text2 { get; set; }
        public double SimilarityScore { get; set; }
        public double Confidence { get; set; }
        public double[] Embedding1 { get; set; }
        public double[] Embedding2 { get; set; }
        public bool IsSimilar { get; set; }
        public SimilarityLevel Level { get; set; }
    }

    public enum PatternType;
    {
        Frequency,
        Structural,
        Semantic,
        Sequential,
        Anomaly,
        Formatting,
        Thematic;
    }

    public enum AnalysisStatus;
    {
        Initializing,
        Running,
        Paused,
        Stopped,
        Error;
    }

    public enum SimilarityLevel;
    {
        Identical,
        VerySimilar,
        Similar,
        SomewhatSimilar,
        Different,
        VeryDifferent;
    }

    #endregion;
}
