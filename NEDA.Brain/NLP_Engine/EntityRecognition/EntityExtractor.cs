using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Transforms.Text;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Common;
using NEDA.Common.Extensions;
using NEDA.ExceptionHandling;
using NEDA.Monitoring;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Brain.NLP_Engine.EntityRecognition;
{
    /// <summary>
    /// Gelişmiş varlık tanıma ve çıkarım sistemi.
    /// Çoklu dil desteği, bağlamsal anlama ve özel varlık türleri ile çalışır.
    /// </summary>
    public class EntityExtractor : IEntityExtractor, IDisposable;
    {
        private readonly ILogger<EntityExtractor> _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEntityRepository _entityRepository;
        private readonly MLContext _mlContext;

        private readonly ConcurrentDictionary<string, EntityModel> _loadedModels;
        private readonly ConcurrentDictionary<string, EntityPattern> _customPatterns;
        private readonly ConcurrentDictionary<string, EntityContext> _contextCache;
        private readonly ReaderWriterLockSlim _extractorLock;

        private bool _disposed;
        private readonly EntityExtractorConfiguration _configuration;

        /// <summary>
        /// Varlık modeli yapısı;
        /// </summary>
        private class EntityModel;
        {
            public string Language { get; set; } = "en";
            public string ModelType { get; set; } = "CRF";
            public ITransformer? MlModel { get; set; }
            public DateTime LoadedAt { get; set; }
            public DateTime LastUsed { get; set; }
            public int UsageCount { get; set; }
            public Dictionary<string, EntityTypeInfo> EntityTypes { get; set; } = new();
            public EntityModelPerformance Performance { get; set; } = new();
        }

        /// <summary>
        /// Varlık deseni yapısı;
        /// </summary>
        private class EntityPattern;
        {
            public string PatternId { get; set; } = string.Empty;
            public string PatternType { get; set; } = "regex";
            public string Language { get; set; } = "en";
            public Regex? RegexPattern { get; set; }
            public string PatternText { get; set; } = string.Empty;
            public EntityType TargetEntityType { get; set; } = new();
            public double Confidence { get; set; } = 0.9;
            public Dictionary<string, object> Metadata { get; set; } = new();
            public DateTime CreatedAt { get; set; }
            public DateTime LastModified { get; set; }
            public int MatchCount { get; set; }
        }

        /// <summary>
        /// Varlık bağlamı yapısı;
        /// </summary>
        private class EntityContext;
        {
            public string ContextId { get; set; } = string.Empty;
            public string Domain { get; set; } = "general";
            public Dictionary<string, double> EntityWeights { get; set; } = new();
            public List<EntityRelation> CommonRelations { get; set; } = new();
            public Dictionary<string, List<string>> EntitySynonyms { get; set; } = new();
            public DateTime LastUpdated { get; set; }
            public int ReferenceCount { get; set; }
        }

        /// <summary>
        /// Varlık çıkarıcı konfigürasyonu;
        /// </summary>
        public class EntityExtractorConfiguration;
        {
            public int MaxTextLength { get; set; } = 10000;
            public int BatchSize { get; set; } = 100;
            public TimeSpan ModelCacheDuration { get; set; } = TimeSpan.FromHours(24);
            public TimeSpan ContextCacheDuration { get; set; } = TimeSpan.FromMinutes(30);
            public bool EnableMLModels { get; set; } = true;
            public bool EnableRegexPatterns { get; set; } = true;
            public bool EnableDictionaryLookup { get; set; } = true;
            public bool EnableContextualAnalysis { get; set; } = true;
            public bool EnableEntityLinking { get; set; } = true;
            public double ConfidenceThreshold { get; set; } = 0.7;
            public int MaxEntitiesPerText { get; set; } = 100;
            public List<string> SupportedLanguages { get; set; } = new() { "en", "tr", "es", "fr", "de", "it", "ru", "zh", "ja", "ko" };
            public Dictionary<string, double> LanguageWeights { get; set; } = new();
            public bool EnableRealTimeLearning { get; set; } = true;
            public int LearningBatchSize { get; set; } = 50;
        }

        /// <summary>
        /// Varlık türü bilgisi;
        /// </summary>
        public class EntityType;
        {
            public string Name { get; set; } = string.Empty;
            public string Category { get; set; } = "general";
            public string Description { get; set; } = string.Empty;
            public List<string> Aliases { get; set; } = new();
            public Dictionary<string, object> Attributes { get; set; } = new();
            public double Priority { get; set; } = 1.0;
            public ColorRepresentation Color { get; set; } = new();

            // Validasyon kuralları;
            public List<ValidationRule> ValidationRules { get; set; } = new();
            public List<ExtractionRule> ExtractionRules { get; set; } = new();

            public static readonly EntityType Person = new()
            {
                Name = "PERSON",
                Category = "personal",
                Description = "İnsan isimleri",
                Priority = 1.0,
                Attributes = new Dictionary<string, object>
                {
                    { "has_gender", true },
                    { "has_title", true },
                    { "can_have_multiple_parts", true }
                }
            };

            public static readonly EntityType Organization = new()
            {
                Name = "ORGANIZATION",
                Category = "corporate",
                Description = "Kurum ve organizasyon isimleri",
                Priority = 0.9;
            };

            public static readonly EntityType Location = new()
            {
                Name = "LOCATION",
                Category = "geographic",
                Description = "Coğrafi konumlar",
                Priority = 0.8;
            };

            public static readonly EntityType Date = new()
            {
                Name = "DATE",
                Category = "temporal",
                Description = "Tarih ve zaman bilgileri",
                Priority = 0.7;
            };

            public static readonly EntityType Money = new()
            {
                Name = "MONEY",
                Category = "financial",
                Description = "Para miktarları",
                Priority = 0.8;
            };

            public static readonly EntityType Email = new()
            {
                Name = "EMAIL",
                Category = "contact",
                Description = "E-posta adresleri",
                Priority = 0.9;
            };

            public static readonly EntityType Phone = new()
            {
                Name = "PHONE",
                Category = "contact",
                Description = "Telefon numaraları",
                Priority = 0.9;
            };

            public static readonly EntityType URL = new()
            {
                Name = "URL",
                Category = "digital",
                Description = "Web adresleri",
                Priority = 0.9;
            };
        }

        /// <summary>
        /// Varlık türü bilgisi (genişletilmiş)
        /// </summary>
        public class EntityTypeInfo : EntityType;
        {
            public int TrainingExamples { get; set; }
            public double Accuracy { get; set; }
            public double Recall { get; set; }
            public double F1Score { get; set; }
            public DateTime LastTrained { get; set; }
            public List<string> CommonPatterns { get; set; } = new();
        }

        /// <summary>
        /// Çıkarılan varlık;
        /// </summary>
        public class ExtractedEntity;
        {
            public string Text { get; set; } = string.Empty;
            public EntityType Type { get; set; } = new();
            public double Confidence { get; set; }
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public int Length => EndIndex - StartIndex;
            public Dictionary<string, object> Attributes { get; set; } = new();
            public List<EntityRelation> Relations { get; set; } = new();
            public string NormalizedValue { get; set; } = string.Empty;
            public string Language { get; set; } = "en";
            public EntitySource Source { get; set; } = EntitySource.Unknown;
            public DateTime ExtractedAt { get; set; }

            // Bağlamsal bilgiler;
            public string Context { get; set; } = string.Empty;
            public int SentenceIndex { get; set; }
            public int TokenIndex { get; set; }

            // Metadata;
            public Dictionary<string, object> Metadata { get; set; } = new();
        }

        /// <summary>
        /// Varlık ilişkisi;
        /// </summary>
        public class EntityRelation;
        {
            public string RelationType { get; set; } = string.Empty;
            public string SourceEntity { get; set; } = string.Empty;
            public string TargetEntity { get; set; } = string.Empty;
            public double Confidence { get; set; }
            public Dictionary<string, object> Attributes { get; set; } = new();
        }

        /// <summary>
        /// Varlık model performansı;
        /// </summary>
        public class EntityModelPerformance;
        {
            public int TotalExtractions { get; set; }
            public int CorrectExtractions { get; set; }
            public double Accuracy => TotalExtractions > 0 ? (double)CorrectExtractions / TotalExtractions : 0;
            public double AverageConfidence { get; set; }
            public TimeSpan AverageProcessingTime { get; set; }
            public Dictionary<string, int> EntityTypeCounts { get; set; } = new();
            public DateTime LastEvaluation { get; set; }
        }

        /// <summary>
        /// Renk temsili;
        /// </summary>
        public class ColorRepresentation;
        {
            public string Hex { get; set; } = "#3498db";
            public int R { get; set; } = 52;
            public int G { get; set; } = 152;
            public int B { get; set; } = 219;
        }

        /// <summary>
        /// Varlık kaynağı;
        /// </summary>
        public enum EntitySource;
        {
            Unknown,
            MLModel,
            RegexPattern,
            Dictionary,
            Contextual,
            Hybrid,
            CustomRule;
        }

        /// <summary>
        /// Validasyon kuralı;
        /// </summary>
        public class ValidationRule;
        {
            public string RuleId { get; set; } = string.Empty;
            public string RuleType { get; set; } = "regex";
            public string Pattern { get; set; } = string.Empty;
            public string ErrorMessage { get; set; } = string.Empty;
            public bool IsRequired { get; set; }
        }

        /// <summary>
        /// Çıkarma kuralı;
        /// </summary>
        public class ExtractionRule;
        {
            public string RuleId { get; set; } = string.Empty;
            public string Pattern { get; set; } = string.Empty;
            public EntityType TargetType { get; set; } = new();
            public double Weight { get; set; } = 1.0;
            public Dictionary<string, string> CaptureGroups { get; set; } = new();
        }

        public event EventHandler<EntityExtractionEventArgs>? EntityExtracted;
        public event EventHandler<EntityExtractionEventArgs>? EntityValidationFailed;
        public event EventHandler<ModelLoadedEventArgs>? ModelLoaded;

        private readonly EntityExtractorStatistics _statistics;
        private readonly object _statsLock = new object();

        // Önceden tanımlı regex pattern'leri;
        private readonly Dictionary<string, string> _predefinedPatterns = new()
        {
            { "EMAIL", @"[\w\.-]+@[\w\.-]+\.\w+" },
            { "PHONE", @"(\+\d{1,3}[-\.\s]??)?\d{3}[-\.\s]??\d{3}[-\.\s]??\d{4}" },
            { "URL", @"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)" },
            { "DATE", @"\b(\d{1,2}[\/\-\.]\d{1,2}[\/\-\.]\d{2,4}|\d{4}[\/\-\.]\d{1,2}[\/\-\.]\d{1,2})\b" },
            { "MONEY", @"\$\d+(?:\.\d{2})?|\d+\s*(?:dolar|tl|eur|usd)" },
            { "IP_ADDRESS", @"\b(?:\d{1,3}\.){3}\d{1,3}\b" },
            { "HASHTAG", @"#\w+" },
            { "MENTION", @"@\w+" }
        };

        /// <summary>
        /// EntityExtractor örneği oluşturur;
        /// </summary>
        public EntityExtractor(
            ILogger<EntityExtractor> logger,
            IMetricsCollector metricsCollector,
            IEntityRepository entityRepository,
            EntityExtractorConfiguration? configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _entityRepository = entityRepository ?? throw new ArgumentNullException(nameof(entityRepository));

            _configuration = configuration ?? new EntityExtractorConfiguration();
            _mlContext = new MLContext(seed: 42);
            _loadedModels = new ConcurrentDictionary<string, EntityModel>();
            _customPatterns = new ConcurrentDictionary<string, EntityPattern>();
            _contextCache = new ConcurrentDictionary<string, EntityContext>();
            _extractorLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _statistics = new EntityExtractorStatistics();

            InitializePredefinedPatterns();
            LoadDefaultModels();

            _logger.LogInformation("EntityExtractor initialized with {Count} predefined patterns",
                _predefinedPatterns.Count);
        }

        /// <summary>
        /// Önceden tanımlı pattern'leri yükler;
        /// </summary>
        private void InitializePredefinedPatterns()
        {
            try
            {
                foreach (var pattern in _predefinedPatterns)
                {
                    var entityPattern = new EntityPattern;
                    {
                        PatternId = $"predefined_{pattern.Key.ToLower()}",
                        PatternType = "regex",
                        Language = "multi",
                        PatternText = pattern.Value,
                        TargetEntityType = GetEntityTypeByPattern(pattern.Key),
                        Confidence = 0.95,
                        CreatedAt = DateTime.UtcNow,
                        LastModified = DateTime.UtcNow;
                    };

                    try
                    {
                        entityPattern.RegexPattern = new Regex(pattern.Value,
                            RegexOptions.Compiled | RegexOptions.IgnoreCase,
                            TimeSpan.FromMilliseconds(100));

                        _customPatterns[entityPattern.PatternId] = entityPattern;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to compile regex pattern for {Pattern}", pattern.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing predefined patterns");
            }
        }

        /// <summary>
        /// Varsayılan modelleri yükler;
        /// </summary>
        private void LoadDefaultModels()
        {
            if (!_configuration.EnableMLModels) return;

            try
            {
                // İngilizce için varsayılan model;
                LoadModelForLanguage("en");

                // Türkçe için özel model (eğer varsa)
                if (_configuration.SupportedLanguages.Contains("tr"))
                {
                    LoadModelForLanguage("tr");
                }

                _logger.LogDebug("Default models loaded for languages: {Languages}",
                    string.Join(", ", _loadedModels.Keys));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading default models");
            }
        }

        /// <summary>
        /// Metinden varlık çıkarır;
        /// </summary>
        public async Task<EntityExtractionResult> ExtractEntitiesAsync(
            string text,
            string language = "en",
            string? domain = null,
            ExtractionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var extractionId = Guid.NewGuid().ToString("N");
            var extractionOptions = options ?? new ExtractionOptions();

            try
            {
                _extractorLock.EnterUpgradeableReadLock();

                // Metin ön işleme;
                var processedText = PreprocessText(text, language);

                if (processedText.Length > _configuration.MaxTextLength)
                {
                    processedText = processedText.Substring(0, _configuration.MaxTextLength);
                    _logger.LogWarning("Text truncated to {Length} characters", _configuration.MaxTextLength);
                }

                // Varlık çıkarma pipeline'ı;
                var extractedEntities = new List<ExtractedEntity>();

                // 1. ML Model tabanlı çıkarma;
                if (_configuration.EnableMLModels && extractionOptions.UseMLModels)
                {
                    var mlEntities = await ExtractWithMLModelAsync(processedText, language, domain, cancellationToken);
                    extractedEntities.AddRange(mlEntities);
                }

                // 2. Regex pattern tabanlı çıkarma;
                if (_configuration.EnableRegexPatterns && extractionOptions.UsePatterns)
                {
                    var patternEntities = ExtractWithPatterns(processedText, language, domain);
                    extractedEntities.AddRange(patternEntities);
                }

                // 3. Sözlük tabanlı çıkarma;
                if (_configuration.EnableDictionaryLookup && extractionOptions.UseDictionary)
                {
                    var dictEntities = await ExtractWithDictionaryAsync(processedText, language, domain, cancellationToken);
                    extractedEntities.AddRange(dictEntities);
                }

                // 4. Çakışan varlıkları birleştir;
                var mergedEntities = MergeOverlappingEntities(extractedEntities, extractionOptions);

                // 5. Güvenilirlik filtresi;
                var filteredEntities = mergedEntities;
                    .Where(e => e.Confidence >= extractionOptions.ConfidenceThreshold)
                    .ToList();

                // 6. Bağlamsal analiz;
                if (_configuration.EnableContextualAnalysis && extractionOptions.UseContext)
                {
                    filteredEntities = await ApplyContextualAnalysisAsync(
                        filteredEntities, processedText, language, domain, cancellationToken);
                }

                // 7. Varlık bağlama (entity linking)
                if (_configuration.EnableEntityLinking && extractionOptions.UseEntityLinking)
                {
                    filteredEntities = await ApplyEntityLinkingAsync(
                        filteredEntities, language, domain, cancellationToken);
                }

                // 8. Normalizasyon;
                filteredEntities = NormalizeEntities(filteredEntities, language);

                // 9. Sıralama;
                filteredEntities = SortEntities(filteredEntities, extractionOptions);

                // 10. Limit kontrolü;
                if (filteredEntities.Count > _configuration.MaxEntitiesPerText)
                {
                    filteredEntities = filteredEntities;
                        .Take(_configuration.MaxEntitiesPerText)
                        .ToList();
                }

                // İstatistikleri güncelle;
                UpdateStatistics(filteredEntities, stopwatch.Elapsed);

                // Event tetikle;
                foreach (var entity in filteredEntities)
                {
                    EntityExtracted?.Invoke(this,
                        new EntityExtractionEventArgs(extractionId, entity, EntityEventType.Extracted));
                }

                // Metrik kaydet;
                RecordExtractionMetrics(extractionId, filteredEntities, stopwatch.Elapsed, language, domain);

                _logger.LogDebug("Extracted {Count} entities from text (Language: {Language}, Domain: {Domain})",
                    filteredEntities.Count, language, domain ?? "general");

                return EntityExtractionResult.Success(extractionId, filteredEntities, processedText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting entities from text (Language: {Language})", language);

                EntityExtracted?.Invoke(this,
                    new EntityExtractionEventArgs(extractionId, null, EntityEventType.ExtractionFailed));

                throw new EntityExtractionException($"Failed to extract entities: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
                _extractorLock.ExitUpgradeableReadLock();
            }
        }

        /// <summary>
        /// ML model ile varlık çıkarma;
        /// </summary>
        private async Task<List<ExtractedEntity>> ExtractWithMLModelAsync(
            string text,
            string language,
            string? domain,
            CancellationToken cancellationToken)
        {
            var entities = new List<ExtractedEntity>();

            try
            {
                // Model yükle;
                var model = await GetOrLoadModelAsync(language, domain, cancellationToken);
                if (model?.MlModel == null) return entities;

                // Metni token'lara ayır;
                var sentences = SplitIntoSentences(text, language);

                for (int i = 0; i < sentences.Count; i++)
                {
                    var sentence = sentences[i];
                    var tokens = TokenizeSentence(sentence, language);

                    // ML.NET ile tahmin yap (gerçek implementasyonda model prediction kullanılır)
                    // Bu örnek için basit bir simülasyon;
                    var predictedEntities = SimulateMLPrediction(tokens, model, language);

                    foreach (var predEntity in predictedEntities)
                    {
                        var entity = new ExtractedEntity;
                        {
                            Text = predEntity.Text,
                            Type = predEntity.Type,
                            Confidence = predEntity.Confidence * model.Performance.Accuracy,
                            StartIndex = predEntity.StartIndex,
                            EndIndex = predEntity.EndIndex,
                            Language = language,
                            Source = EntitySource.MLModel,
                            ExtractedAt = DateTime.UtcNow,
                            SentenceIndex = i,
                            TokenIndex = predEntity.TokenIndex,
                            Attributes = predEntity.Attributes;
                        };

                        entities.Add(entity);
                    }
                }

                // Model kullanım sayacını güncelle;
                model.UsageCount++;
                model.LastUsed = DateTime.UtcNow;

                _logger.LogTrace("ML model extracted {Count} entities for language: {Language}",
                    entities.Count, language);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ML model extraction failed for language: {Language}", language);
            }

            return entities;
        }

        /// <summary>
        /// Regex pattern'leri ile varlık çıkarma;
        /// </summary>
        private List<ExtractedEntity> ExtractWithPatterns(string text, string language, string? domain)
        {
            var entities = new List<ExtractedEntity>();

            try
            {
                // Dil ve domain'e özel pattern'leri al;
                var relevantPatterns = _customPatterns.Values;
                    .Where(p => p.Language == "multi" || p.Language == language)
                    .Where(p => p.RegexPattern != null)
                    .ToList();

                foreach (var pattern in relevantPatterns)
                {
                    try
                    {
                        var matches = pattern.RegexPattern!.Matches(text);

                        foreach (Match match in matches)
                        {
                            if (match.Success && !string.IsNullOrWhiteSpace(match.Value))
                            {
                                var entity = new ExtractedEntity;
                                {
                                    Text = match.Value,
                                    Type = pattern.TargetEntityType,
                                    Confidence = pattern.Confidence,
                                    StartIndex = match.Index,
                                    EndIndex = match.Index + match.Length,
                                    Language = language,
                                    Source = EntitySource.RegexPattern,
                                    ExtractedAt = DateTime.UtcNow,
                                    Attributes = new Dictionary<string, object>
                                    {
                                        { "pattern_id", pattern.PatternId },
                                        { "match_groups", match.Groups.Count }
                                    }
                                };

                                // Capture group'ları işle;
                                ProcessCaptureGroups(entity, match, pattern);

                                entities.Add(entity);

                                // Pattern kullanım sayacını güncelle;
                                pattern.MatchCount++;
                            }
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        _logger.LogWarning("Regex pattern timeout for pattern: {PatternId}", pattern.PatternId);
                    }
                }

                _logger.LogTrace("Pattern extraction found {Count} entities", entities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern extraction failed");
            }

            return entities;
        }

        /// <summary>
        /// Sözlük tabanlı varlık çıkarma;
        /// </summary>
        private async Task<List<ExtractedEntity>> ExtractWithDictionaryAsync(
            string text,
            string language,
            string? domain,
            CancellationToken cancellationToken)
        {
            var entities = new List<ExtractedEntity>();

            try
            {
                // Domain'e özel sözlük yükle;
                var dictionary = await _entityRepository.GetDictionaryAsync(language, domain, cancellationToken);
                if (dictionary == null || !dictionary.Entities.Any()) return entities;

                // Metni token'lara ayır;
                var tokens = TokenizeText(text, language);
                var textLower = text.ToLowerInvariant();

                foreach (var dictEntity in dictionary.Entities)
                {
                    // Varlık adaylarını ara;
                    var candidates = FindDictionaryCandidates(dictEntity, textLower, tokens);

                    foreach (var candidate in candidates)
                    {
                        var entity = new ExtractedEntity;
                        {
                            Text = candidate.Text,
                            Type = dictEntity.Type,
                            Confidence = candidate.Confidence,
                            StartIndex = candidate.StartIndex,
                            EndIndex = candidate.EndIndex,
                            Language = language,
                            Source = EntitySource.Dictionary,
                            ExtractedAt = DateTime.UtcNow,
                            NormalizedValue = dictEntity.NormalizedValue,
                            Attributes = dictEntity.Attributes,
                            Metadata = new Dictionary<string, object>
                            {
                                { "dictionary_id", dictEntity.Id },
                                { "dictionary_source", dictEntity.Source }
                            }
                        };

                        entities.Add(entity);
                    }
                }

                _logger.LogTrace("Dictionary extraction found {Count} entities", entities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dictionary extraction failed for language: {Language}", language);
            }

            return entities;
        }

        /// <summary>
        /// Bağlamsal analiz uygula;
        /// </summary>
        private async Task<List<ExtractedEntity>> ApplyContextualAnalysisAsync(
            List<ExtractedEntity> entities,
            string text,
            string language,
            string? domain,
            CancellationToken cancellationToken)
        {
            if (!entities.Any()) return entities;

            try
            {
                // Bağlam yükle;
                var context = await GetOrLoadContextAsync(language, domain, cancellationToken);
                if (context == null) return entities;

                // Her varlık için bağlamsal skor hesapla;
                foreach (var entity in entities)
                {
                    var contextualScore = CalculateContextualScore(entity, entities, text, context);
                    entity.Confidence *= contextualScore;

                    // Bağlamsal özellikleri ekle;
                    entity.Attributes["contextual_score"] = contextualScore;
                    entity.Attributes["domain_relevance"] = context.Domain == domain ? 1.0 : 0.7;
                }

                // Bağlamsal ilişkileri bul;
                var relations = FindContextualRelations(entities, context);
                foreach (var relation in relations)
                {
                    // İlişkileri varlıklara ekle;
                    var sourceEntity = entities.FirstOrDefault(e => e.Text == relation.SourceEntity);
                    var targetEntity = entities.FirstOrDefault(e => e.Text == relation.TargetEntity);

                    if (sourceEntity != null) sourceEntity.Relations.Add(relation);
                    if (targetEntity != null) targetEntity.Relations.Add(relation);
                }

                context.ReferenceCount++;
                context.LastUpdated = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Contextual analysis failed");
            }

            return entities;
        }

        /// <summary>
        /// Varlık bağlama (entity linking) uygula;
        /// </summary>
        private async Task<List<ExtractedEntity>> ApplyEntityLinkingAsync(
            List<ExtractedEntity> entities,
            string language,
            string? domain,
            CancellationToken cancellationToken)
        {
            if (!entities.Any()) return entities;

            try
            {
                // Bilgi tabanından varlık bağlantılarını getir;
                var linkingTasks = entities.Select(async entity =>
                {
                    try
                    {
                        var linkedEntities = await _entityRepository.FindLinkedEntitiesAsync(
                            entity.Text, entity.Type.Name, language, domain, cancellationToken);

                        if (linkedEntities.Any())
                        {
                            entity.Attributes["linked_entities"] = linkedEntities;
                            entity.Attributes["knowledge_base_id"] = linkedEntities.First().Id;
                            entity.Confidence *= 1.1; // Bağlantı bulunduğunda güvenilirlik artır;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Entity linking failed for: {Entity}", entity.Text);
                    }

                    return entity;
                });

                entities = (await Task.WhenAll(linkingTasks)).ToList();

                _logger.LogTrace("Entity linking processed {Count} entities", entities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Entity linking failed");
            }

            return entities;
        }

        /// <summary>
        /// Özel varlık pattern'i ekler;
        /// </summary>
        public async Task<PatternRegistrationResult> AddCustomPatternAsync(
            CustomPatternRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            try
            {
                _extractorLock.EnterWriteLock();

                // Pattern validasyonu;
                var validationResult = ValidatePatternRequest(request);
                if (!validationResult.IsValid)
                {
                    return PatternRegistrationResult.Failure(validationResult.Errors);
                }

                // Pattern ID oluştur;
                var patternId = $"custom_{request.PatternType}_{Guid.NewGuid():N}";

                // Regex pattern oluştur;
                Regex? regexPattern = null;
                if (request.PatternType == "regex")
                {
                    try
                    {
                        regexPattern = new Regex(request.PatternText,
                            RegexOptions.Compiled | RegexOptions.IgnoreCase,
                            TimeSpan.FromMilliseconds(100));
                    }
                    catch (Exception ex)
                    {
                        return PatternRegistrationResult.Failure(new[] { $"Invalid regex pattern: {ex.Message}" });
                    }
                }

                // Entity type bul veya oluştur;
                var entityType = await GetOrCreateEntityTypeAsync(request.EntityType, cancellationToken);

                // Pattern oluştur;
                var pattern = new EntityPattern;
                {
                    PatternId = patternId,
                    PatternType = request.PatternType,
                    Language = request.Language,
                    PatternText = request.PatternText,
                    RegexPattern = regexPattern,
                    TargetEntityType = entityType,
                    Confidence = request.Confidence,
                    Metadata = new Dictionary<string, object>(request.Metadata),
                    CreatedAt = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow;
                };

                // Kaydet;
                if (_customPatterns.TryAdd(patternId, pattern))
                {
                    // Repository'e kaydet;
                    await _entityRepository.SavePatternAsync(pattern, cancellationToken);

                    _logger.LogInformation("Custom pattern added: {PatternId} for entity type: {EntityType}",
                        patternId, entityType.Name);

                    return PatternRegistrationResult.Success(patternId);
                }

                return PatternRegistrationResult.Failure(new[] { "Failed to add pattern" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding custom pattern");
                throw new EntityExtractionException("Failed to add custom pattern", ex);
            }
            finally
            {
                _extractorLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Model eğitimi başlatır;
        /// </summary>
        public async Task<ModelTrainingResult> TrainModelAsync(
            TrainingData trainingData,
            string language = "en",
            CancellationToken cancellationToken = default)
        {
            if (trainingData == null) throw new ArgumentNullException(nameof(trainingData));
            if (!trainingData.Examples.Any())
                throw new ArgumentException("Training data must contain examples", nameof(trainingData));

            try
            {
                _extractorLock.EnterWriteLock();

                _logger.LogInformation("Starting model training for language: {Language} with {Count} examples",
                    language, trainingData.Examples.Count);

                // Eğitim verisini hazırla;
                var preparedData = PrepareTrainingData(trainingData, language);

                // ML pipeline oluştur;
                var pipeline = CreateTrainingPipeline(language);

                // Model eğit;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var trainedModel = pipeline.Fit(preparedData);
                stopwatch.Stop();

                // Model değerlendirme;
                var evaluationResult = EvaluateModel(trainedModel, preparedData);

                // Modeli kaydet;
                var model = new EntityModel;
                {
                    Language = language,
                    ModelType = "CRF",
                    MlModel = trainedModel,
                    LoadedAt = DateTime.UtcNow,
                    LastUsed = DateTime.UtcNow,
                    Performance = new EntityModelPerformance;
                    {
                        TotalExtractions = trainingData.Examples.Count,
                        CorrectExtractions = (int)(trainingData.Examples.Count * evaluationResult.Accuracy),
                        AverageConfidence = evaluationResult.AverageConfidence,
                        AverageProcessingTime = stopwatch.Elapsed / trainingData.Examples.Count,
                        LastEvaluation = DateTime.UtcNow;
                    }
                };

                // Entity type bilgilerini güncelle;
                UpdateEntityTypeInfo(model, trainingData);

                // Modeli cache'e ekle;
                var modelKey = GetModelKey(language, trainingData.Domain);
                _loadedModels[modelKey] = model;

                // Repository'e kaydet;
                await _entityRepository.SaveModelAsync(model, cancellationToken);

                // Event tetikle;
                ModelLoaded?.Invoke(this, new ModelLoadedEventArgs(language, modelKey, evaluationResult));

                _logger.LogInformation("Model training completed for {Language}. Accuracy: {Accuracy:F2}, Time: {Time}ms",
                    language, evaluationResult.Accuracy * 100, stopwatch.ElapsedMilliseconds);

                return ModelTrainingResult.Success(modelKey, evaluationResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error training model for language: {Language}", language);
                throw new EntityExtractionException($"Failed to train model for language: {language}", ex);
            }
            finally
            {
                _extractorLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Çıkarıcı istatistiklerini getirir;
        /// </summary>
        public EntityExtractorStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                _statistics.LoadedModelsCount = _loadedModels.Count;
                _statistics.CustomPatternsCount = _customPatterns.Count;
                _statistics.ContextCacheCount = _contextCache.Count;
                _statistics.LastUpdated = DateTime.UtcNow;

                return new EntityExtractorStatistics;
                {
                    TotalExtractions = _statistics.TotalExtractions,
                    SuccessfulExtractions = _statistics.SuccessfulExtractions,
                    AverageConfidence = _statistics.AverageConfidence,
                    AverageProcessingTime = _statistics.AverageProcessingTime,
                    LanguageDistribution = new Dictionary<string, int>(_statistics.LanguageDistribution),
                    EntityTypeDistribution = new Dictionary<string, int>(_statistics.EntityTypeDistribution),
                    LoadedModelsCount = _statistics.LoadedModelsCount,
                    CustomPatternsCount = _statistics.CustomPatternsCount,
                    ContextCacheCount = _statistics.ContextCacheCount,
                    LastUpdated = _statistics.LastUpdated;
                };
            }
        }

        /// <summary>
        /// Kaynakları serbest bırakır;
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
                    _extractorLock?.Dispose();

                    // Modelleri temizle;
                    _loadedModels.Clear();

                    _logger.LogInformation("EntityExtractor disposed");
                }

                _disposed = true;
            }
        }

        // Yardımcı metodlar;
        private string PreprocessText(string text, string language)
        {
            // Temel temizlik;
            var processed = text.Trim();

            // Çoklu boşlukları tek boşluğa indir;
            processed = Regex.Replace(processed, @"\s+", " ");

            // Dil spesifik ön işlemeler;
            switch (language)
            {
                case "tr":
                    // Türkçe özel karakter normalizasyonu;
                    processed = processed.Replace("İ", "i").Replace("I", "ı");
                    break;

                case "de":
                    // Almanca umlaut normalizasyonu;
                    processed = processed.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
                                       .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue")
                                       .Replace("ß", "ss");
                    break;
            }

            return processed;
        }

        private List<ExtractedEntity> MergeOverlappingEntities(List<ExtractedEntity> entities, ExtractionOptions options)
        {
            if (!entities.Any()) return entities;

            var merged = new List<ExtractedEntity>();
            var sorted = entities.OrderBy(e => e.StartIndex).ThenByDescending(e => e.Confidence).ToList();

            while (sorted.Count > 0)
            {
                var current = sorted[0];
                sorted.RemoveAt(0);

                // Çakışan varlıkları bul;
                var overlapping = sorted.Where(e =>
                    e.StartIndex < current.EndIndex && e.EndIndex > current.StartIndex).ToList();

                if (overlapping.Any())
                {
                    // En yüksek güvenilirliği seç;
                    var bestEntity = overlapping.Prepend(current)
                        .OrderByDescending(e => e.Confidence)
                        .ThenByDescending(e => e.Type.Priority)
                        .First();

                    merged.Add(bestEntity);

                    // Çakışanları listeden çıkar;
                    foreach (var overlap in overlapping)
                    {
                        sorted.Remove(overlap);
                    }
                }
                else;
                {
                    merged.Add(current);
                }
            }

            return merged;
        }

        private List<ExtractedEntity> NormalizeEntities(List<ExtractedEntity> entities, string language)
        {
            foreach (var entity in entities)
            {
                // Varlık normalizasyonu;
                entity.NormalizedValue = NormalizeEntityText(entity.Text, entity.Type, language);

                // Ek özellikler ekle;
                AddDerivedAttributes(entity, language);
            }

            return entities;
        }

        private List<ExtractedEntity> SortEntities(List<ExtractedEntity> entities, ExtractionOptions options)
        {
            return options.SortBy switch;
            {
                "confidence" => entities.OrderByDescending(e => e.Confidence).ToList(),
                "position" => entities.OrderBy(e => e.StartIndex).ToList(),
                "type" => entities.OrderByDescending(e => e.Type.Priority).ThenBy(e => e.StartIndex).ToList(),
                "length" => entities.OrderByDescending(e => e.Length).ToList(),
                _ => entities.OrderByDescending(e => e.Confidence * e.Type.Priority).ToList()
            };
        }

        private async Task<EntityModel?> GetOrLoadModelAsync(string language, string? domain, CancellationToken cancellationToken)
        {
            var modelKey = GetModelKey(language, domain);

            if (_loadedModels.TryGetValue(modelKey, out var model))
            {
                // Cache süresi kontrolü;
                if (DateTime.UtcNow - model.LoadedAt < _configuration.ModelCacheDuration)
                {
                    return model;
                }
            }

            // Modeli repository'den yükle;
            model = await _entityRepository.LoadModelAsync(language, domain, cancellationToken);
            if (model != null)
            {
                model.LoadedAt = DateTime.UtcNow;
                _loadedModels[modelKey] = model;

                ModelLoaded?.Invoke(this, new ModelLoadedEventArgs(language, modelKey, null));

                _logger.LogDebug("Model loaded from repository: {ModelKey}", modelKey);
            }

            return model;
        }

        private async Task<EntityContext?> GetOrLoadContextAsync(string language, string? domain, CancellationToken cancellationToken)
        {
            var contextKey = $"{language}:{domain ?? "general"}";

            if (_contextCache.TryGetValue(contextKey, out var context))
            {
                if (DateTime.UtcNow - context.LastUpdated < _configuration.ContextCacheDuration)
                {
                    return context;
                }
            }

            // Bağlamı repository'den yükle;
            context = await _entityRepository.LoadContextAsync(language, domain, cancellationToken);
            if (context != null)
            {
                context.LastUpdated = DateTime.UtcNow;
                _contextCache[contextKey] = context;
            }

            return context;
        }

        private async Task<EntityType> GetOrCreateEntityTypeAsync(EntityTypeRequest request, CancellationToken cancellationToken)
        {
            // Repository'den kontrol et;
            var existingType = await _entityRepository.GetEntityTypeAsync(request.Name, cancellationToken);
            if (existingType != null) return existingType;

            // Yeni entity type oluştur;
            var entityType = new EntityType;
            {
                Name = request.Name,
                Category = request.Category,
                Description = request.Description,
                Aliases = new List<string>(request.Aliases),
                Attributes = new Dictionary<string, object>(request.Attributes),
                Priority = request.Priority,
                Color = request.Color;
            };

            // Repository'e kaydet;
            await _entityRepository.SaveEntityTypeAsync(entityType, cancellationToken);

            return entityType;
        }

        private void UpdateStatistics(List<ExtractedEntity> entities, TimeSpan processingTime)
        {
            lock (_statsLock)
            {
                _statistics.TotalExtractions += entities.Count;
                _statistics.SuccessfulExtractions += entities.Count(e => e.Confidence >= _configuration.ConfidenceThreshold);

                if (entities.Any())
                {
                    _statistics.AverageConfidence = (_statistics.AverageConfidence * (_statistics.TotalExtractions - entities.Count) +
                                                    entities.Average(e => e.Confidence)) / _statistics.TotalExtractions;

                    _statistics.AverageProcessingTime = TimeSpan.FromMilliseconds(
                        (_statistics.AverageProcessingTime.TotalMilliseconds * (_statistics.TotalExtractions - entities.Count) +
                         processingTime.TotalMilliseconds) / _statistics.TotalExtractions);
                }

                // Dil dağılımı;
                foreach (var entity in entities)
                {
                    if (_statistics.LanguageDistribution.ContainsKey(entity.Language))
                        _statistics.LanguageDistribution[entity.Language]++;
                    else;
                        _statistics.LanguageDistribution[entity.Language] = 1;

                    // Entity type dağılımı;
                    var typeName = entity.Type.Name;
                    if (_statistics.EntityTypeDistribution.ContainsKey(typeName))
                        _statistics.EntityTypeDistribution[typeName]++;
                    else;
                        _statistics.EntityTypeDistribution[typeName] = 1;
                }
            }
        }

        private void RecordExtractionMetrics(string extractionId, List<ExtractedEntity> entities,
                                           TimeSpan processingTime, string language, string? domain)
        {
            _metricsCollector.RecordMetric("entity_extraction_count", entities.Count,
                new Dictionary<string, string>
                {
                    { "extraction_id", extractionId },
                    { "language", language },
                    { "domain", domain ?? "general" }
                });

            _metricsCollector.RecordMetric("entity_extraction_time", processingTime.TotalMilliseconds,
                new Dictionary<string, string>
                {
                    { "extraction_id", extractionId },
                    { "entity_count", entities.Count.ToString() }
                });
        }

        private string GetModelKey(string language, string? domain) =>
            $"{language}:{domain ?? "general"}";

        private EntityType GetEntityTypeByPattern(string patternKey)
        {
            return patternKey switch;
            {
                "EMAIL" => EntityType.Email,
                "PHONE" => EntityType.Phone,
                "URL" => EntityType.URL,
                "DATE" => EntityType.Date,
                "MONEY" => EntityType.Money,
                "IP_ADDRESS" => EntityType.Organization, // Placeholder;
                "HASHTAG" => new EntityType { Name = "HASHTAG", Category = "social" },
                "MENTION" => new EntityType { Name = "MENTION", Category = "social" },
                _ => new EntityType { Name = patternKey, Category = "custom" }
            };
        }

        // Simülasyon metodları (gerçek implementasyonda ML model tahmini yapılır)
        private List<ExtractedEntity> SimulateMLPrediction(List<string> tokens, EntityModel model, string language)
        {
            var entities = new List<ExtractedEntity>();
            // Gerçek implementasyonda ML model prediction kullanılır;
            return entities;
        }

        private IDataView PrepareTrainingData(TrainingData trainingData, string language)
        {
            // Eğitim verisini ML.NET formatına dönüştür;
            // Gerçek implementasyonda detaylı veri hazırlama yapılır;
            return _mlContext.Data.LoadFromEnumerable(new List<object>());
        }

        private IEstimator<ITransformer> CreateTrainingPipeline(string language)
        {
            // ML.NET pipeline oluştur;
            // Gerçek implementasyonda detaylı pipeline yapılandırması yapılır;
            return _mlContext.Transforms.Conversion.MapValueToKey("Label");
        }

        private ModelEvaluationResult EvaluateModel(ITransformer model, IDataView testData)
        {
            // Model değerlendirme;
            // Gerçek implementasyonda cross-validation ve metrik hesaplama yapılır;
            return new ModelEvaluationResult;
            {
                Accuracy = 0.85,
                Precision = 0.82,
                Recall = 0.87,
                F1Score = 0.84,
                AverageConfidence = 0.78;
            };
        }

        ~EntityExtractor()
        {
            Dispose(false);
        }
    }

    // Yardımcı sınıflar ve interface'ler;

    /// <summary>
    /// Varlık çıkarma seçenekleri;
    /// </summary>
    public class ExtractionOptions;
    {
        public bool UseMLModels { get; set; } = true;
        public bool UsePatterns { get; set; } = true;
        public bool UseDictionary { get; set; } = true;
        public bool UseContext { get; set; } = true;
        public bool UseEntityLinking { get; set; } = true;
        public double ConfidenceThreshold { get; set; } = 0.7;
        public string SortBy { get; set; } = "confidence";
        public bool IncludeMetadata { get; set; } = true;
        public bool NormalizeEntities { get; set; } = true;
        public Dictionary<string, object> AdditionalParameters { get; set; } = new();
    }

    /// <summary>
    /// Varlık çıkarma sonucu;
    /// </summary>
    public class EntityExtractionResult;
    {
        public bool IsSuccess { get; }
        public string ExtractionId { get; }
        public List<EntityExtractor.ExtractedEntity> Entities { get; }
        public string ProcessedText { get; }
        public TimeSpan ProcessingTime { get; }
        public Dictionary<string, object> Metadata { get; }
        public string? ErrorMessage { get; }

        private EntityExtractionResult(bool isSuccess, string extractionId,
                                     List<EntityExtractor.ExtractedEntity> entities,
                                     string processedText, string? errorMessage)
        {
            IsSuccess = isSuccess;
            ExtractionId = extractionId;
            Entities = entities;
            ProcessedText = processedText;
            ProcessingTime = TimeSpan.Zero;
            Metadata = new Dictionary<string, object>();
            ErrorMessage = errorMessage;
        }

        public static EntityExtractionResult Success(string extractionId,
            List<EntityExtractor.ExtractedEntity> entities, string processedText)
            => new EntityExtractionResult(true, extractionId, entities, processedText, null);

        public static EntityExtractionResult Failure(string errorMessage)
            => new EntityExtractionResult(false, string.Empty, new List<EntityExtractor.ExtractedEntity>(), string.Empty, errorMessage);
    }

    /// <summary>
    /// Özel pattern isteği;
    /// </summary>
    public class CustomPatternRequest;
    {
        public string PatternType { get; set; } = "regex";
        public string PatternText { get; set; } = string.Empty;
        public EntityTypeRequest EntityType { get; set; } = new();
        public string Language { get; set; } = "en";
        public double Confidence { get; set; } = 0.9;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Varlık türü isteği;
    /// </summary>
    public class EntityTypeRequest;
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "general";
        public string Description { get; set; } = string.Empty;
        public List<string> Aliases { get; set; } = new();
        public Dictionary<string, object> Attributes { get; set; } = new();
        public double Priority { get; set; } = 1.0;
        public EntityExtractor.ColorRepresentation Color { get; set; } = new();
    }

    /// <summary>
    /// Pattern kayıt sonucu;
    /// </summary>
    public class PatternRegistrationResult;
    {
        public bool IsSuccess { get; }
        public string? PatternId { get; }
        public IEnumerable<string> Errors { get; }

        private PatternRegistrationResult(bool isSuccess, string? patternId, IEnumerable<string> errors)
        {
            IsSuccess = isSuccess;
            PatternId = patternId;
            Errors = errors;
        }

        public static PatternRegistrationResult Success(string patternId)
            => new PatternRegistrationResult(true, patternId, Array.Empty<string>());

        public static PatternRegistrationResult Failure(IEnumerable<string> errors)
            => new PatternRegistrationResult(false, null, errors);
    }

    /// <summary>
    /// Eğitim verisi;
    /// </summary>
    public class TrainingData;
    {
        public string Language { get; set; } = "en";
        public string? Domain { get; set; }
        public List<TrainingExample> Examples { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Eğitim örneği;
    /// </summary>
    public class TrainingExample;
    {
        public string Text { get; set; } = string.Empty;
        public List<Annotation> Annotations { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Annotation (etiketleme)
    /// </summary>
    public class Annotation;
    {
        public string Text { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new();
    }

    /// <summary>
    /// Model eğitim sonucu;
    /// </summary>
    public class ModelTrainingResult;
    {
        public bool IsSuccess { get; }
        public string ModelKey { get; }
        public ModelEvaluationResult? Evaluation { get; }
        public string? ErrorMessage { get; }

        private ModelTrainingResult(bool isSuccess, string modelKey,
                                  ModelEvaluationResult? evaluation, string? errorMessage)
        {
            IsSuccess = isSuccess;
            ModelKey = modelKey;
            Evaluation = evaluation;
            ErrorMessage = errorMessage;
        }

        public static ModelTrainingResult Success(string modelKey, ModelEvaluationResult evaluation)
            => new ModelTrainingResult(true, modelKey, evaluation, null);

        public static ModelTrainingResult Failure(string errorMessage)
            => new ModelTrainingResult(false, string.Empty, null, errorMessage);
    }

    /// <summary>
    /// Model değerlendirme sonucu;
    /// </summary>
    public class ModelEvaluationResult;
    {
        public double Accuracy { get; set; }
        public double Precision { get; set; }
        public double Recall { get; set; }
        public double F1Score { get; set; }
        public double AverageConfidence { get; set; }
        public Dictionary<string, double> PerEntityMetrics { get; set; } = new();
        public TimeSpan TrainingTime { get; set; }
    }

    /// <summary>
    /// Varlık çıkarıcı istatistikleri;
    /// </summary>
    public class EntityExtractorStatistics;
    {
        public long TotalExtractions { get; set; }
        public long SuccessfulExtractions { get; set; }
        public double AverageConfidence { get; set; }
        public TimeSpan AverageProcessingTime { get; set; }
        public Dictionary<string, int> LanguageDistribution { get; set; } = new();
        public Dictionary<string, int> EntityTypeDistribution { get; set; } = new();
        public int LoadedModelsCount { get; set; }
        public int CustomPatternsCount { get; set; }
        public int ContextCacheCount { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Varlık çıkarma event argümanları;
    /// </summary>
    public class EntityExtractionEventArgs : EventArgs;
    {
        public string ExtractionId { get; }
        public EntityExtractor.ExtractedEntity? Entity { get; }
        public EntityEventType EventType { get; }
        public DateTime Timestamp { get; }

        public EntityExtractionEventArgs(string extractionId, EntityExtractor.ExtractedEntity? entity,
                                       EntityEventType eventType)
        {
            ExtractionId = extractionId;
            Entity = entity;
            EventType = eventType;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Model yükleme event argümanları;
    /// </summary>
    public class ModelLoadedEventArgs : EventArgs;
    {
        public string Language { get; }
        public string ModelKey { get; }
        public ModelEvaluationResult? Evaluation { get; }
        public DateTime Timestamp { get; }

        public ModelLoadedEventArgs(string language, string modelKey, ModelEvaluationResult? evaluation)
        {
            Language = language;
            ModelKey = modelKey;
            Evaluation = evaluation;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Varlık event tipleri;
    /// </summary>
    public enum EntityEventType;
    {
        Extracted,
        ValidationFailed,
        ExtractionFailed,
        PatternAdded,
        ModelTrained;
    }

    /// <summary>
    /// Varlık çıkarma exception;
    /// </summary>
    public class EntityExtractionException : Exception
    {
        public EntityExtractionException(string message) : base(message) { }
        public EntityExtractionException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Varlık çıkarıcı interface'i;
    /// </summary>
    public interface IEntityExtractor : IDisposable
    {
        Task<EntityExtractionResult> ExtractEntitiesAsync(
            string text,
            string language = "en",
            string? domain = null,
            ExtractionOptions? options = null,
            CancellationToken cancellationToken = default);

        Task<PatternRegistrationResult> AddCustomPatternAsync(
            CustomPatternRequest request,
            CancellationToken cancellationToken = default);

        Task<ModelTrainingResult> TrainModelAsync(
            TrainingData trainingData,
            string language = "en",
            CancellationToken cancellationToken = default);

        EntityExtractorStatistics GetStatistics();

        event EventHandler<EntityExtractionEventArgs>? EntityExtracted;
        event EventHandler<EntityExtractionEventArgs>? EntityValidationFailed;
        event EventHandler<ModelLoadedEventArgs>? ModelLoaded;
    }

    /// <summary>
    /// Varlık repository interface'i;
    /// </summary>
    public interface IEntityRepository;
    {
        Task<EntityDictionary?> GetDictionaryAsync(string language, string? domain, CancellationToken cancellationToken);
        Task<List<LinkedEntity>> FindLinkedEntitiesAsync(string entityText, string entityType, string language, string? domain, CancellationToken cancellationToken);
        Task<EntityExtractor.EntityModel?> LoadModelAsync(string language, string? domain, CancellationToken cancellationToken);
        Task SaveModelAsync(EntityExtractor.EntityModel model, CancellationToken cancellationToken);
        Task<EntityExtractor.EntityContext?> LoadContextAsync(string language, string? domain, CancellationToken cancellationToken);
        Task<EntityExtractor.EntityType?> GetEntityTypeAsync(string name, CancellationToken cancellationToken);
        Task SaveEntityTypeAsync(EntityExtractor.EntityType entityType, CancellationToken cancellationToken);
        Task SavePatternAsync(EntityExtractor.EntityPattern pattern, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Varlık sözlüğü;
    /// </summary>
    public class EntityDictionary;
    {
        public string Language { get; set; } = "en";
        public string? Domain { get; set; }
        public List<DictionaryEntity> Entities { get; set; } = new();
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Sözlük varlığı;
    /// </summary>
    public class DictionaryEntity;
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public EntityExtractor.EntityType Type { get; set; } = new();
        public string NormalizedValue { get; set; } = string.Empty;
        public Dictionary<string, object> Attributes { get; set; } = new();
        public string Source { get; set; } = string.Empty;
        public List<string> Variants { get; set; } = new();
    }

    /// <summary>
    /// Bağlantılı varlık;
    /// </summary>
    public class LinkedEntity;
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string KnowledgeBase { get; set; } = string.Empty;
        public double Relevance { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Sözlük adayı;
    /// </summary>
    public class DictionaryCandidate;
    {
        public string Text { get; set; } = string.Empty;
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Bağlamsal skor;
    /// </summary>
    public class ContextualScore;
    {
        public double Score { get; set; }
        public Dictionary<string, double> Factors { get; set; } = new();
    }

    // Yardımcı extension metodlar;
    internal static class EntityExtractorExtensions;
    {
        public static List<string> SplitIntoSentences(this string text, string language)
        {
            // Basit cümle bölme;
            return text.Split(new[] { '.', '!', '?', ';', ':', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => !string.IsNullOrEmpty(s))
                      .ToList();
        }

        public static List<string> TokenizeSentence(this string sentence, string language)
        {
            // Basit tokenization;
            return sentence.Split(new[] { ' ', '\t', ',', '(', ')', '[', ']', '{', '}', '"', '\'' },
                               StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Trim())
                          .Where(t => !string.IsNullOrEmpty(t))
                          .ToList();
        }

        public static List<string> TokenizeText(this string text, string language)
        {
            // Tüm metni token'lara ayır;
            return text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'' },
                            StringSplitOptions.RemoveEmptyEntries)
                      .Select(t => t.Trim())
                      .Where(t => !string.IsNullOrEmpty(t))
                      .ToList();
        }

        public static List<DictionaryCandidate> FindDictionaryCandidates(this DictionaryEntity dictEntity, string textLower, List<string> tokens)
        {
            var candidates = new List<DictionaryCandidate>();

            // Ana metin arama;
            var index = textLower.IndexOf(dictEntity.Text.ToLowerInvariant());
            if (index >= 0)
            {
                candidates.Add(new DictionaryCandidate;
                {
                    Text = dictEntity.Text,
                    StartIndex = index,
                    EndIndex = index + dictEntity.Text.Length,
                    Confidence = 1.0;
                });
            }

            // Varyantları ara;
            foreach (var variant in dictEntity.Variants)
            {
                index = textLower.IndexOf(variant.ToLowerInvariant());
                if (index >= 0)
                {
                    candidates.Add(new DictionaryCandidate;
                    {
                        Text = variant,
                        StartIndex = index,
                        EndIndex = index + variant.Length,
                        Confidence = 0.9;
                    });
                }
            }

            return candidates;
        }

        public static void ProcessCaptureGroups(this EntityExtractor.ExtractedEntity entity, Match match, EntityExtractor.EntityPattern pattern)
        {
            // Capture group'ları işle;
            for (int i = 1; i < match.Groups.Count; i++)
            {
                var group = match.Groups[i];
                if (group.Success)
                {
                    entity.Attributes[$"group_{i}"] = group.Value;
                }
            }
        }

        public static double CalculateContextualScore(this EntityExtractor.ExtractedEntity entity,
            List<EntityExtractor.ExtractedEntity> allEntities, string text, EntityExtractor.EntityContext context)
        {
            double score = 1.0;

            // Entity weight;
            if (context.EntityWeights.TryGetValue(entity.Type.Name, out var weight))
            {
                score *= weight;
            }

            // Domain relevance;
            if (context.EntitySynonyms.ContainsKey(entity.Text.ToLower()))
            {
                score *= 1.2;
            }

            // Position in text;
            var textPosition = (double)entity.StartIndex / text.Length;
            score *= 1.0 + (0.1 * (1.0 - textPosition)); // Metnin başında daha yüksek skor;

            return Math.Min(score, 1.5); // Maksimum 1.5x artış;
        }

        public static List<EntityExtractor.EntityRelation> FindContextualRelations(
            List<EntityExtractor.ExtractedEntity> entities, EntityExtractor.EntityContext context)
        {
            var relations = new List<EntityExtractor.EntityRelation>();

            // Bağlamdaki ortak ilişkileri uygula;
            foreach (var commonRelation in context.CommonRelations)
            {
                var sourceEntity = entities.FirstOrDefault(e => e.Text == commonRelation.SourceEntity);
                var targetEntity = entities.FirstOrDefault(e => e.Text == commonRelation.TargetEntity);

                if (sourceEntity != null && targetEntity != null)
                {
                    relations.Add(commonRelation);
                }
            }

            return relations;
        }

        public static string NormalizeEntityText(this string entityText, EntityExtractor.EntityType entityType, string language)
        {
            // Varlık türüne göre normalizasyon;
            return entityType.Name switch;
            {
                "EMAIL" => entityText.ToLowerInvariant(),
                "PHONE" => Regex.Replace(entityText, @"[^\d+]", ""),
                "DATE" => NormalizeDate(entityText, language),
                "MONEY" => NormalizeMoney(entityText, language),
                _ => entityText.Trim()
            };
        }

        private static string NormalizeDate(string dateText, string language)
        {
            // Tarih normalizasyonu;
            // Gerçek implementasyonda detaylı tarih parsing yapılır;
            return dateText;
        }

        private static string NormalizeMoney(string moneyText, string language)
        {
            // Para birimi normalizasyonu;
            // Gerçek implementasyonda para birimi dönüşümü yapılır;
            return Regex.Replace(moneyText, @"[^\d.,]", "");
        }

        public static void AddDerivedAttributes(this EntityExtractor.ExtractedEntity entity, string language)
        {
            // Türetilmiş özellikler ekle;
            entity.Attributes["length"] = entity.Length;
            entity.Attributes["word_count"] = entity.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            entity.Attributes["contains_numbers"] = entity.Text.Any(char.IsDigit);
            entity.Attributes["contains_special_chars"] = entity.Text.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));

            // Dil spesifik özellikler;
            if (language == "tr")
            {
                entity.Attributes["turkish_specific"] = entity.Text.Contains("ğ") || entity.Text.Contains("ı") ||
                                                       entity.Text.Contains("ö") || entity.Text.Contains("ü") ||
                                                       entity.Text.Contains("ş") || entity.Text.Contains("ç");
            }
        }

        public static ValidationResult ValidatePatternRequest(this CustomPatternRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.PatternText))
                errors.Add("Pattern text is required");

            if (request.EntityType == null || string.IsNullOrWhiteSpace(request.EntityType.Name))
                errors.Add("Entity type name is required");

            if (request.Confidence < 0 || request.Confidence > 1)
                errors.Add("Confidence must be between 0 and 1");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        public static void UpdateEntityTypeInfo(this EntityExtractor.EntityModel model, TrainingData trainingData)
        {
            // Entity type istatistiklerini güncelle;
            foreach (var example in trainingData.Examples)
            {
                foreach (var annotation in example.Annotations)
                {
                    if (!model.EntityTypes.ContainsKey(annotation.EntityType))
                    {
                        model.EntityTypes[annotation.EntityType] = new EntityExtractor.EntityTypeInfo;
                        {
                            Name = annotation.EntityType,
                            TrainingExamples = 0,
                            LastTrained = DateTime.UtcNow;
                        };
                    }

                    model.EntityTypes[annotation.EntityType].TrainingExamples++;
                }
            }
        }

        public static void LoadModelForLanguage(this EntityExtractor extractor, string language)
        {
            // Dil için model yükleme simülasyonu;
            // Gerçek implementasyonda dosya sisteminden veya repository'den yüklenir;
        }
    }
}
