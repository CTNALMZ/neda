using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Logging;
using NEDA.Brain.NLP_Engine;
using NEDA.Brain.IntentRecognition;
using NEDA.Brain.MemorySystem;
using NEDA.Communication.DialogSystem.ConversationManager;

namespace NEDA.Communication.DialogSystem.ClarificationEngine;
{
    /// <summary>
    /// Belirsizlikleri çözümlemek ve kullanıcıdan netleştirme isteğinde bulunmak için kullanılan motor.
    /// Endüstriyel seviyede, tam işlevsel bir belirsizlik çözümleyici.
    /// </summary>
    public interface IClarifier;
    {
        /// <summary>
        /// Kullanıcı sorgusundaki belirsizlikleri analiz eder.
        /// </summary>
        /// <param name="query">Kullanıcı sorgusu</param>
        /// <param name="context">Konuşma bağlamı</param>
        /// <returns>Belirsizlik analiz sonucu</returns>
        Task<AmbiguityAnalysisResult> AnalyzeAmbiguitiesAsync(string query, ConversationContext context);

        /// <summary>
        /// Belirsizlikleri çözmek için netleştirme soruları oluşturur.
        /// </summary>
        /// <param name="analysisResult">Belirsizlik analiz sonucu</param>
        /// <returns>Netleştirme soruları koleksiyonu</returns>
        Task<IEnumerable<ClarificationQuestion>> GenerateClarificationQuestionsAsync(AmbiguityAnalysisResult analysisResult);

        /// <summary>
        /// Kullanıcının netleştirme cevabını işler ve orijinal sorguyu günceller.
        /// </summary>
        /// <param name="originalQuery">Orijinal sorgu</param>
        /// <param name="clarificationResponse">Netleştirme cevabı</param>
        /// <param name="context">Konuşma bağlamı</param>
        /// <returns>Netleştirilmiş sorgu</returns>
        Task<ResolvedQuery> ResolveAmbiguityAsync(string originalQuery, ClarificationResponse clarificationResponse, ConversationContext context);

        /// <summary>
        /// Belirsizlik seviyesini değerlendirir.
        /// </summary>
        /// <param name="query">Değerlendirilecek sorgu</param>
        /// <returns>Belirsizlik skoru (0-1 arası)</returns>
        Task<double> CalculateAmbiguityScoreAsync(string query);
    }

    /// <summary>
    /// Belirsizlik analiz sonucu;
    /// </summary>
    public class AmbiguityAnalysisResult;
    {
        public string OriginalQuery { get; set; }
        public double AmbiguityScore { get; set; }
        public List<Ambiguity> DetectedAmbiguities { get; set; }
        public List<PotentialIntent> PotentialIntents { get; set; }
        public List<MissingParameter> MissingParameters { get; set; }
        public DateTime AnalysisTimestamp { get; set; }

        public AmbiguityAnalysisResult()
        {
            DetectedAmbiguities = new List<Ambiguity>();
            PotentialIntents = new List<PotentialIntent>();
            MissingParameters = new List<MissingParameter>();
            AnalysisTimestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Algılanan belirsizlik;
    /// </summary>
    public class Ambiguity;
    {
        public AmbiguityType Type { get; set; }
        public string SourceText { get; set; }
        public string Description { get; set; }
        public List<string> PossibleInterpretations { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public double Confidence { get; set; }

        public Ambiguity()
        {
            PossibleInterpretations = new List<string>();
        }
    }

    /// <summary>
    /// Belirsizlik türleri;
    /// </summary>
    public enum AmbiguityType;
    {
        Lexical,        // Sözcük düzeyinde belirsizlik;
        Syntactic,      // Sözdizimsel belirsizlik;
        Semantic,       // Anlamsal belirsizlik;
        Referential,    // Göndergesel belirsizlik;
        Temporal,       // Zamansal belirsizlik;
        Quantifier,     // Niceleyici belirsizliği;
        Scope,          // Kapsam belirsizliği;
        Pronoun,        // Zamir belirsizliği;
        Contextual,     // Bağlamsal belirsizlik;
        DomainSpecific  // Alan özel belirsizlik;
    }

    /// <summary>
    /// Potansiyel niyet;
    /// </summary>
    public class PotentialIntent;
    {
        public string IntentName { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<string> SupportingEvidence { get; set; }

        public PotentialIntent()
        {
            Parameters = new Dictionary<string, object>();
            SupportingEvidence = new List<string>();
        }
    }

    /// <summary>
    /// Eksik parametre;
    /// </summary>
    public class MissingParameter;
    {
        public string ParameterName { get; set; }
        public string ParameterType { get; set; }
        public string ExpectedValueFormat { get; set; }
        public string ContextHint { get; set; }
        public bool IsRequired { get; set; }
        public List<string> PossibleValues { get; set; }

        public MissingParameter()
        {
            PossibleValues = new List<string>();
            IsRequired = true;
        }
    }

    /// <summary>
    /// Netleştirme sorusu;
    /// </summary>
    public class ClarificationQuestion;
    {
        public string QuestionId { get; set; }
        public string QuestionText { get; set; }
        public QuestionType Type { get; set; }
        public List<string> Options { get; set; }
        public string TargetAmbiguityId { get; set; }
        public int Priority { get; set; }
        public string ExpectedResponseFormat { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public ClarificationQuestion()
        {
            Options = new List<string>();
            Context = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Soru türleri;
    /// </summary>
    public enum QuestionType;
    {
        YesNo,              // Evet/Hayır sorusu;
        MultipleChoice,     // Çoktan seçmeli;
        OpenEnded,          // Açık uçlu;
        Confirmation,       // Onay sorusu;
        Disambiguation,     // Ayırt etme sorusu;
        Specification,      // Belirtme sorusu;
        Clarification,      // Netleştirme sorusu;
        Verification        // Doğrulama sorusu;
    }

    /// <summary>
    /// Netleştirme cevabı;
    /// </summary>
    public class ClarificationResponse;
    {
        public string QuestionId { get; set; }
        public string ResponseText { get; set; }
        public Dictionary<string, object> ExtractedInformation { get; set; }
        public double Confidence { get; set; }
        public DateTime ResponseTimestamp { get; set; }

        public ClarificationResponse()
        {
            ExtractedInformation = new Dictionary<string, object>();
            ResponseTimestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Çözümlenmiş sorgu;
    /// </summary>
    public class ResolvedQuery;
    {
        public string OriginalQuery { get; set; }
        public string ResolvedQueryText { get; set; }
        public string PrimaryIntent { get; set; }
        public Dictionary<string, object> ResolvedParameters { get; set; }
        public double ResolutionConfidence { get; set; }
        public List<ClarificationQuestion> AskedQuestions { get; set; }
        public List<ClarificationResponse> GivenResponses { get; set; }

        public ResolvedQuery()
        {
            ResolvedParameters = new Dictionary<string, object>();
            AskedQuestions = new List<ClarificationQuestion>();
            GivenResponses = new List<ClarificationResponse>();
        }
    }

    /// <summary>
    /// Konuşma bağlamı;
    /// </summary>
    public class ConversationContext;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public List<string> ConversationHistory { get; set; }
        public Dictionary<string, object> UserPreferences { get; set; }
        public string DomainContext { get; set; }
        public DateTime ConversationStartTime { get; set; }
        public Dictionary<string, object> EnvironmentalFactors { get; set; }

        public ConversationContext()
        {
            ConversationHistory = new List<string>();
            UserPreferences = new Dictionary<string, object>();
            EnvironmentalFactors = new Dictionary<string, object>();
            ConversationStartTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Belirsizlik çözümleyici implementasyonu;
    /// </summary>
    public class Clarifier : IClarifier;
    {
        private readonly ILogger<Clarifier> _logger;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IIntentDetector _intentDetector;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly IConversationEngine _conversationEngine;
        private readonly AmbiguityDetectionRules _detectionRules;
        private readonly ClarificationStrategies _strategies;

        public Clarifier(
            ILogger<Clarifier> logger,
            ISemanticAnalyzer semanticAnalyzer,
            IIntentDetector intentDetector,
            IShortTermMemory shortTermMemory,
            IConversationEngine conversationEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _intentDetector = intentDetector ?? throw new ArgumentNullException(nameof(intentDetector));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _conversationEngine = conversationEngine ?? throw new ArgumentNullException(nameof(conversationEngine));

            _detectionRules = new AmbiguityDetectionRules();
            _strategies = new ClarificationStrategies();
        }

        /// <inheritdoc/>
        public async Task<AmbiguityAnalysisResult> AnalyzeAmbiguitiesAsync(string query, ConversationContext context)
        {
            _logger.LogInformation("Ambiguity analysis started for query: {Query}", query);

            var result = new AmbiguityAnalysisResult;
            {
                OriginalQuery = query;
            };

            try
            {
                // 1. Sözcüksel belirsizlikleri tespit et;
                var lexicalAmbiguities = await DetectLexicalAmbiguitiesAsync(query);
                result.DetectedAmbiguities.AddRange(lexicalAmbiguities);

                // 2. Sözdizimsel belirsizlikleri tespit et;
                var syntacticAmbiguities = await DetectSyntacticAmbiguitiesAsync(query);
                result.DetectedAmbiguities.AddRange(syntacticAmbiguities);

                // 3. Anlamsal belirsizlikleri tespit et;
                var semanticAmbiguities = await DetectSemanticAmbiguitiesAsync(query, context);
                result.DetectedAmbiguities.AddRange(semanticAmbiguities);

                // 4. Potansiyel niyetleri belirle;
                result.PotentialIntents = await IdentifyPotentialIntentsAsync(query, context);

                // 5. Eksik parametreleri belirle;
                result.MissingParameters = await IdentifyMissingParametersAsync(query, result.PotentialIntents);

                // 6. Belirsizlik skorunu hesapla;
                result.AmbiguityScore = await CalculateAmbiguityScoreAsync(query, result);

                _logger.LogInformation("Ambiguity analysis completed. Score: {Score}, Ambiguities: {Count}",
                    result.AmbiguityScore, result.DetectedAmbiguities.Count);

                // Kısa süreli belleğe kaydet;
                await _shortTermMemory.StoreAsync($"ambiguity_analysis_{DateTime.UtcNow.Ticks}", result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during ambiguity analysis for query: {Query}", query);
                throw new AmbiguityAnalysisException("Failed to analyze ambiguities", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<ClarificationQuestion>> GenerateClarificationQuestionsAsync(AmbiguityAnalysisResult analysisResult)
        {
            _logger.LogInformation("Generating clarification questions for {AmbiguityCount} ambiguities",
                analysisResult.DetectedAmbiguities.Count);

            var questions = new List<ClarificationQuestion>();

            try
            {
                // Öncelik sırasına göre belirsizlikleri sırala;
                var prioritizedAmbiguities = analysisResult.DetectedAmbiguities;
                    .OrderByDescending(a => GetAmbiguityPriority(a.Type))
                    .ThenByDescending(a => a.Confidence)
                    .ToList();

                foreach (var ambiguity in prioritizedAmbiguities.Take(3)) // En fazla 3 soru;
                {
                    var question = await GenerateQuestionForAmbiguityAsync(ambiguity, analysisResult);
                    if (question != null)
                    {
                        questions.Add(question);
                    }
                }

                // Eksik parametreler için sorular oluştur;
                var requiredMissingParams = analysisResult.MissingParameters;
                    .Where(p => p.IsRequired)
                    .Take(2); // En fazla 2 eksik parametre sorusu;

                foreach (var param in requiredMissingParams)
                {
                    var question = await GenerateQuestionForMissingParameterAsync(param, analysisResult);
                    if (question != null)
                    {
                        questions.Add(question);
                    }
                }

                // Birden fazla niyet varsa netleştirme sorusu;
                if (analysisResult.PotentialIntents.Count > 1)
                {
                    var intentQuestion = await GenerateIntentDisambiguationQuestionAsync(analysisResult);
                    if (intentQuestion != null)
                    {
                        questions.Insert(0, intentQuestion); // En başa ekle;
                    }
                }

                _logger.LogInformation("Generated {QuestionCount} clarification questions", questions.Count);

                return questions.OrderBy(q => q.Priority).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating clarification questions");
                throw new ClarificationGenerationException("Failed to generate clarification questions", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<ResolvedQuery> ResolveAmbiguityAsync(string originalQuery, ClarificationResponse clarificationResponse, ConversationContext context)
        {
            _logger.LogInformation("Resolving ambiguity with response: {ResponseId}", clarificationResponse.QuestionId);

            try
            {
                var resolvedQuery = new ResolvedQuery;
                {
                    OriginalQuery = originalQuery;
                };

                // Bağlamdan önceki analiz sonucunu al;
                var previousAnalysis = await _shortTermMemory.RecallAsync<AmbiguityAnalysisResult>(
                    $"ambiguity_analysis_{context.SessionId}_latest");

                if (previousAnalysis == null)
                {
                    // Yeni analiz yap;
                    previousAnalysis = await AnalyzeAmbiguitiesAsync(originalQuery, context);
                }

                // Netleştirme cevabını işle;
                await ProcessClarificationResponseAsync(clarificationResponse, previousAnalysis, context);

                // Çözümlenmiş sorguyu oluştur;
                resolvedQuery.ResolvedQueryText = await GenerateResolvedQueryTextAsync(
                    originalQuery, clarificationResponse, previousAnalysis);

                // Birincil niyeti belirle;
                resolvedQuery.PrimaryIntent = await DeterminePrimaryIntentAsync(
                    previousAnalysis, clarificationResponse);

                // Parametreleri çözümle;
                resolvedQuery.ResolvedParameters = await ResolveParametersAsync(
                    previousAnalysis, clarificationResponse, context);

                // Güven skorunu hesapla;
                resolvedQuery.ResolutionConfidence = await CalculateResolutionConfidenceAsync(
                    previousAnalysis, clarificationResponse);

                _logger.LogInformation("Ambiguity resolved successfully. Confidence: {Confidence}",
                    resolvedQuery.ResolutionConfidence);

                return resolvedQuery;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving ambiguity for query: {Query}", originalQuery);
                throw new AmbiguityResolutionException("Failed to resolve ambiguity", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<double> CalculateAmbiguityScoreAsync(string query)
        {
            try
            {
                var context = new ConversationContext();
                var analysis = await AnalyzeAmbiguitiesAsync(query, context);
                return analysis.AmbiguityScore;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating ambiguity score for query: {Query}", query);
                return 1.0; // En yüksek belirsizlik skoru;
            }
        }

        #region Private Methods;

        private async Task<List<Ambiguity>> DetectLexicalAmbiguitiesAsync(string query)
        {
            var ambiguities = new List<Ambiguity>();

            // Çok anlamlı kelimeleri tespit et;
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i].ToLower().Trim('.,;!?');

                if (_detectionRules.PolysemousWords.ContainsKey(word))
                {
                    var interpretations = _detectionRules.PolysemousWords[word];
                    ambiguities.Add(new Ambiguity;
                    {
                        Type = AmbiguityType.Lexical,
                        SourceText = word,
                        Description = $"Çok anlamlı kelime: '{word}'",
                        PossibleInterpretations = interpretations,
                        StartIndex = query.IndexOf(word, StringComparison.OrdinalIgnoreCase),
                        EndIndex = query.IndexOf(word, StringComparison.OrdinalIgnoreCase) + word.Length,
                        Confidence = 0.8;
                    });
                }
            }

            return await Task.FromResult(ambiguities);
        }

        private async Task<List<Ambiguity>> DetectSyntacticAmbiguitiesAsync(string query)
        {
            var ambiguities = new List<Ambiguity>();

            // Prepositional phrase attachment;
            if (query.Contains(" with ") || query.Contains(" in ") || query.Contains(" on "))
            {
                // Örnek: "the man saw the girl with the telescope"
                ambiguities.Add(new Ambiguity;
                {
                    Type = AmbiguityType.Syntactic,
                    SourceText = query,
                    Description = "Edat öbeği bağlanma belirsizliği",
                    PossibleInterpretations = new List<string>
                    {
                        "Edat öbeği fiile bağlı",
                        "Edat öbeği isme bağlı"
                    },
                    StartIndex = 0,
                    EndIndex = query.Length,
                    Confidence = 0.6;
                });
            }

            // Coordination ambiguity;
            if (query.Contains(" and ") || query.Contains(" or "))
            {
                ambiguities.Add(new Ambiguity;
                {
                    Type = AmbiguityType.Syntactic,
                    SourceText = query,
                    Description = "Koordinasyon belirsizliği",
                    PossibleInterpretations = new List<string>
                    {
                        "Öğeler paralel bağlanmış",
                        "Öğeler hiyerarşik bağlanmış"
                    },
                    StartIndex = 0,
                    EndIndex = query.Length,
                    Confidence = 0.5;
                });
            }

            return await Task.FromResult(ambiguities);
        }

        private async Task<List<Ambiguity>> DetectSemanticAmbiguitiesAsync(string query, ConversationContext context)
        {
            var ambiguities = new List<Ambiguity>();

            try
            {
                // Anlamsal analiz yap;
                var semanticAnalysis = await _semanticAnalyzer.AnalyzeAsync(query);

                // Belirsiz referanslar;
                var pronouns = new[] { "it", "this", "that", "they", "them", "he", "she" };
                foreach (var pronoun in pronouns)
                {
                    if (query.Contains($" {pronoun} ", StringComparison.OrdinalIgnoreCase))
                    {
                        var possibleReferents = await FindPossibleReferentsAsync(pronoun, context);
                        if (possibleReferents.Count > 1)
                        {
                            ambiguities.Add(new Ambiguity;
                            {
                                Type = AmbiguityType.Referential,
                                SourceText = pronoun,
                                Description = $"Belirsiz zamir: '{pronoun}'",
                                PossibleInterpretations = possibleReferents,
                                StartIndex = query.IndexOf(pronoun, StringComparison.OrdinalIgnoreCase),
                                EndIndex = query.IndexOf(pronoun, StringComparison.OrdinalIgnoreCase) + pronoun.Length,
                                Confidence = 0.7;
                            });
                        }
                    }
                }

                // Zaman belirsizlikleri;
                var timeWords = new[] { "now", "later", "soon", "tomorrow", "yesterday" };
                foreach (var timeWord in timeWords)
                {
                    if (query.Contains(timeWord, StringComparison.OrdinalIgnoreCase))
                    {
                        ambiguities.Add(new Ambiguity;
                        {
                            Type = AmbiguityType.Temporal,
                            SourceText = timeWord,
                            Description = "Göreceli zaman ifadesi",
                            PossibleInterpretations = new List<string>
                            {
                                "Tam zaman belirtilmedi",
                                "Bağlama bağlı zaman"
                            },
                            StartIndex = query.IndexOf(timeWord, StringComparison.OrdinalIgnoreCase),
                            EndIndex = query.IndexOf(timeWord, StringComparison.OrdinalIgnoreCase) + timeWord.Length,
                            Confidence = 0.6;
                        });
                    }
                }

                return ambiguities;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in semantic ambiguity detection");
                return ambiguities;
            }
        }

        private async Task<List<PotentialIntent>> IdentifyPotentialIntentsAsync(string query, ConversationContext context)
        {
            var intents = new List<PotentialIntent>();

            try
            {
                // Niyet tespiti yap;
                var detectedIntents = await _intentDetector.DetectAsync(query, context);

                foreach (var intent in detectedIntents)
                {
                    intents.Add(new PotentialIntent;
                    {
                        IntentName = intent.Name,
                        Confidence = intent.Confidence,
                        Parameters = intent.Parameters,
                        SupportingEvidence = intent.Evidence;
                    });
                }

                return intents.OrderByDescending(i => i.Confidence).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error identifying potential intents");
                return intents;
            }
        }

        private async Task<List<MissingParameter>> IdentifyMissingParametersAsync(string query, List<PotentialIntent> potentialIntents)
        {
            var missingParams = new List<MissingParameter>();

            foreach (var intent in potentialIntents)
            {
                // Niyete özel gerekli parametreleri kontrol et;
                var requiredParams = _strategies.GetRequiredParameters(intent.IntentName);

                foreach (var requiredParam in requiredParams)
                {
                    if (!intent.Parameters.ContainsKey(requiredParam.Key))
                    {
                        missingParams.Add(new MissingParameter;
                        {
                            ParameterName = requiredParam.Key,
                            ParameterType = requiredParam.Value,
                            ExpectedValueFormat = GetExpectedFormat(requiredParam.Key),
                            ContextHint = GetParameterHint(requiredParam.Key),
                            IsRequired = true,
                            PossibleValues = await GetPossibleValuesAsync(requiredParam.Key, query)
                        });
                    }
                }
            }

            return missingParams;
        }

        private async Task<double> CalculateAmbiguityScoreAsync(string query, AmbiguityAnalysisResult analysis)
        {
            double score = 0.0;

            // Belirsizlik sayısına göre;
            score += Math.Min(analysis.DetectedAmbiguities.Count * 0.1, 0.3);

            // Belirsizlik tiplerine göre;
            foreach (var ambiguity in analysis.DetectedAmbiguities)
            {
                score += GetAmbiguityWeight(ambiguity.Type) * ambiguity.Confidence;
            }

            // Niyet sayısına göre;
            if (analysis.PotentialIntents.Count > 1)
            {
                score += Math.Min((analysis.PotentialIntents.Count - 1) * 0.15, 0.3);
            }

            // Eksik parametrelere göre;
            score += Math.Min(analysis.MissingParameters.Count(p => p.IsRequired) * 0.05, 0.2);

            // Skoru 0-1 arasına sınırla;
            return Math.Min(Math.Max(score, 0.0), 1.0);
        }

        private async Task<ClarificationQuestion> GenerateQuestionForAmbiguityAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
        {
            var strategy = _strategies.GetQuestionStrategy(ambiguity.Type);

            return new ClarificationQuestion;
            {
                QuestionId = $"amb_{Guid.NewGuid():N}",
                QuestionText = await strategy.GenerateQuestionAsync(ambiguity, analysis),
                Type = strategy.QuestionType,
                Options = await strategy.GenerateOptionsAsync(ambiguity, analysis),
                TargetAmbiguityId = ambiguity.SourceText,
                Priority = GetAmbiguityPriority(ambiguity.Type),
                ExpectedResponseFormat = strategy.ExpectedResponseFormat,
                Context = new Dictionary<string, object>
                {
                    ["ambiguity_type"] = ambiguity.Type.ToString(),
                    ["source_text"] = ambiguity.SourceText,
                    ["confidence"] = ambiguity.Confidence;
                }
            };
        }

        private async Task<ClarificationQuestion> GenerateQuestionForMissingParameterAsync(MissingParameter parameter, AmbiguityAnalysisResult analysis)
        {
            return new ClarificationQuestion;
            {
                QuestionId = $"param_{Guid.NewGuid():N}",
                QuestionText = $"Lütfen {parameter.ParameterName} belirtin: {parameter.ContextHint}",
                Type = QuestionType.OpenEnded,
                Options = parameter.PossibleValues,
                TargetAmbiguityId = $"missing_{parameter.ParameterName}",
                Priority = 2, // Orta öncelik;
                ExpectedResponseFormat = parameter.ExpectedValueFormat,
                Context = new Dictionary<string, object>
                {
                    ["parameter_name"] = parameter.ParameterName,
                    ["parameter_type"] = parameter.ParameterType,
                    ["is_required"] = parameter.IsRequired;
                }
            };
        }

        private async Task<ClarificationQuestion> GenerateIntentDisambiguationQuestionAsync(AmbiguityAnalysisResult analysis)
        {
            var intentNames = analysis.PotentialIntents;
                .Select(i => i.IntentName)
                .ToList();

            return new ClarificationQuestion;
            {
                QuestionId = $"intent_{Guid.NewGuid():N}",
                QuestionText = "Hangi işlemi yapmak istiyorsunuz?",
                Type = QuestionType.MultipleChoice,
                Options = intentNames,
                TargetAmbiguityId = "multiple_intents",
                Priority = 1, // En yüksek öncelik;
                ExpectedResponseFormat = "choice",
                Context = new Dictionary<string, object>
                {
                    ["potential_intents"] = intentNames,
                    ["intent_count"] = intentNames.Count;
                }
            };
        }

        private async Task ProcessClarificationResponseAsync(ClarificationResponse response, AmbiguityAnalysisResult analysis, ConversationContext context)
        {
            // Cevabı konuşma geçmişine ekle;
            context.ConversationHistory.Add($"Kullanıcı: {response.ResponseText}");

            // Cevaptan bilgi çıkar;
            var extractedInfo = await ExtractInformationFromResponseAsync(response, analysis);

            // Bağlama göre bilgiyi işle;
            await UpdateContextWithResponseAsync(context, response, extractedInfo);

            _logger.LogDebug("Processed clarification response: {ResponseId}", response.QuestionId);
        }

        private async Task<string> GenerateResolvedQueryTextAsync(string originalQuery, ClarificationResponse response, AmbiguityAnalysisResult analysis)
        {
            // Basit bir çözümleme: orijinal sorguyu netleştirilmiş hale getir;
            var resolved = originalQuery;

            // Belirsiz kelimeleri değiştir;
            foreach (var ambiguity in analysis.DetectedAmbiguities)
            {
                if (response.ExtractedInformation.ContainsKey(ambiguity.SourceText))
                {
                    var resolvedTerm = response.ExtractedInformation[ambiguity.SourceText].ToString();
                    resolved = resolved.Replace(ambiguity.SourceText, resolvedTerm, StringComparison.OrdinalIgnoreCase);
                }
            }

            return await Task.FromResult(resolved);
        }

        private async Task<string> DeterminePrimaryIntentAsync(AmbiguityAnalysisResult analysis, ClarificationResponse response)
        {
            if (response.ExtractedInformation.ContainsKey("selected_intent"))
            {
                return response.ExtractedInformation["selected_intent"].ToString();
            }

            // En yüksek güven skorlu niyeti seç;
            return analysis.PotentialIntents;
                .OrderByDescending(i => i.Confidence)
                .FirstOrDefault()?.IntentName ?? "unknown";
        }

        private async Task<Dictionary<string, object>> ResolveParametersAsync(
            AmbiguityAnalysisResult analysis,
            ClarificationResponse response,
            ConversationContext context)
        {
            var resolvedParams = new Dictionary<string, object>();

            // Analizden parametreleri al;
            foreach (var intent in analysis.PotentialIntents)
            {
                foreach (var param in intent.Parameters)
                {
                    resolvedParams[param.Key] = param.Value;
                }
            }

            // Cevaplardan parametreleri ekle;
            foreach (var info in response.ExtractedInformation)
            {
                resolvedParams[info.Key] = info.Value;
            }

            // Bağlamdan parametreleri ekle;
            if (context.UserPreferences != null)
            {
                foreach (var pref in context.UserPreferences)
                {
                    if (!resolvedParams.ContainsKey(pref.Key))
                    {
                        resolvedParams[pref.Key] = pref.Value;
                    }
                }
            }

            return await Task.FromResult(resolvedParams);
        }

        private async Task<double> CalculateResolutionConfidenceAsync(
            AmbiguityAnalysisResult analysis,
            ClarificationResponse response)
        {
            double confidence = 0.5; // Başlangıç değeri;

            // Cevapların netliğine göre;
            confidence += response.Confidence * 0.3;

            // Belirsizliklerin çözülme oranına göre;
            var resolvedAmbiguities = analysis.DetectedAmbiguities;
                .Count(a => response.ExtractedInformation.ContainsKey(a.SourceText));

            var resolutionRate = analysis.DetectedAmbiguities.Count > 0;
                ? (double)resolvedAmbiguities / analysis.DetectedAmbiguities.Count;
                : 1.0;

            confidence += resolutionRate * 0.2;

            // 0-1 arasına sınırla;
            return Math.Min(Math.Max(confidence, 0.0), 1.0);
        }

        private async Task<List<string>> FindPossibleReferentsAsync(string pronoun, ConversationContext context)
        {
            var referents = new List<string>();

            // Konuşma geçmişinden referansları bul;
            foreach (var utterance in context.ConversationHistory.Reverse<string>().Take(5))
            {
                // Basit isim bulma (gerçek implementasyonda NLP kullanılmalı)
                var nouns = ExtractNounsFromText(utterance);
                referents.AddRange(nouns);
            }

            return await Task.FromResult(referents.Distinct().ToList());
        }

        private List<string> ExtractNounsFromText(string text)
        {
            // Basit implementasyon - gerçekte POS tagging kullanılmalı;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var nouns = new List<string>();

            // Basit heuristics;
            foreach (var word in words)
            {
                if (word.Length > 3 && char.IsUpper(word[0]))
                {
                    nouns.Add(word);
                }
            }

            return nouns;
        }

        private async Task<Dictionary<string, object>> ExtractInformationFromResponseAsync(
            ClarificationResponse response,
            AmbiguityAnalysisResult analysis)
        {
            var extractedInfo = new Dictionary<string, object>();

            try
            {
                // Basit bilgi çıkarımı;
                if (response.ResponseText.ToLower().Contains("evet") ||
                    response.ResponseText.ToLower().Contains("yes"))
                {
                    extractedInfo["confirmation"] = true;
                }
                else if (response.ResponseText.ToLower().Contains("hayır") ||
                         response.ResponseText.ToLower().Contains("no"))
                {
                    extractedInfo["confirmation"] = false;
                }

                // Sayısal değer çıkarımı;
                var numbers = System.Text.RegularExpressions.Regex.Matches(response.ResponseText, @"\d+");
                if (numbers.Count > 0)
                {
                    extractedInfo["numeric_value"] = int.Parse(numbers[0].Value);
                }

                // Seçenek eşleştirme;
                foreach (var ambiguity in analysis.DetectedAmbiguities)
                {
                    foreach (var interpretation in ambiguity.PossibleInterpretations)
                    {
                        if (response.ResponseText.Contains(interpretation, StringComparison.OrdinalIgnoreCase))
                        {
                            extractedInfo[ambiguity.SourceText] = interpretation;
                            break;
                        }
                    }
                }

                return extractedInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error extracting information from response");
                return extractedInfo;
            }
        }

        private async Task UpdateContextWithResponseAsync(
            ConversationContext context,
            ClarificationResponse response,
            Dictionary<string, object> extractedInfo)
        {
            // Kullanıcı tercihlerini güncelle;
            foreach (var info in extractedInfo)
            {
                if (context.UserPreferences.ContainsKey(info.Key))
                {
                    context.UserPreferences[info.Key] = info.Value;
                }
                else;
                {
                    context.UserPreferences.Add(info.Key, info.Value);
                }
            }

            // Oturum verilerini güncelle;
            await _shortTermMemory.StoreAsync($"clarification_{response.QuestionId}", new;
            {
                Response = response,
                ExtractedInfo = extractedInfo,
                Timestamp = DateTime.UtcNow;
            });
        }

        private async Task<List<string>> GetPossibleValuesAsync(string parameterName, string query)
        {
            // Parametre tipine göre olası değerler;
            var values = new List<string>();

            switch (parameterName.ToLower())
            {
                case "color":
                    values.AddRange(new[] { "kırmızı", "mavi", "yeşil", "sarı", "siyah", "beyaz" });
                    break;
                case "size":
                    values.AddRange(new[] { "küçük", "orta", "büyük", "XL", "XXL" });
                    break;
                case "date":
                    values.AddRange(new[] { "bugün", "yarın", "gelecek hafta", "bu ay sonu" });
                    break;
                case "location":
                    values.AddRange(new[] { "ev", "iş", "okul", "market", "restoran" });
                    break;
            }

            return await Task.FromResult(values);
        }

        private string GetExpectedFormat(string parameterName)
        {
            switch (parameterName.ToLower())
            {
                case "date": return "YYYY-MM-DD veya 'bugün', 'yarın' gibi";
                case "time": return "HH:MM veya 'akşam', 'öğleden sonra' gibi";
                case "number": return "rakam";
                case "text": return "serbest metin";
                default: return "serbest metin";
            }
        }

        private string GetParameterHint(string parameterName)
        {
            switch (parameterName.ToLower())
            {
                case "date": return "(örnek: 2024-01-15 veya 'yarın')";
                case "time": return "(örnek: 14:30 veya 'akşam 8')";
                case "color": return "(renk adı)";
                case "size": return "(ölçü veya boyut)";
                default: return "";
            }
        }

        private double GetAmbiguityWeight(AmbiguityType type)
        {
            return type switch;
            {
                AmbiguityType.Referential => 0.3,
                AmbiguityType.Semantic => 0.25,
                AmbiguityType.Syntactic => 0.2,
                AmbiguityType.Lexical => 0.15,
                AmbiguityType.Temporal => 0.15,
                _ => 0.1;
            };
        }

        private int GetAmbiguityPriority(AmbiguityType type)
        {
            return type switch;
            {
                AmbiguityType.Referential => 1,    // En yüksek öncelik;
                AmbiguityType.Semantic => 2,
                AmbiguityType.Syntactic => 3,
                AmbiguityType.Lexical => 4,
                AmbiguityType.Temporal => 5,
                _ => 6                            // En düşük öncelik;
            };
        }

        #endregion;

        #region Supporting Classes;

        /// <summary>
        /// Belirsizlik tespit kuralları;
        /// </summary>
        private class AmbiguityDetectionRules;
        {
            public Dictionary<string, List<string>> PolysemousWords { get; }

            public AmbiguityDetectionRules()
            {
                PolysemousWords = new Dictionary<string, List<string>>
                {
                    ["bank"] = new List<string> { "banka", "nehir kenarı", "veri bankası" },
                    ["bat"] = new List<string> { "yarasa", "vurmak", "şarj" },
                    ["light"] = new List<string> { "ışık", "hafif", "açık renk" },
                    ["right"] = new List<string> { "sağ", "doğru", "hak" },
                    ["left"] = new List<string> { "sol", "kalan", "ayrıldı" },
                    ["book"] = new List<string> { "kitap", "rezervasyon yapmak" },
                    ["run"] = new List<string> { "koşmak", "çalıştırmak", "işletmek" },
                    ["set"] = new List<string> { "kurmak", "takım", "ayarlamak" },
                    ["play"] = new List<string> { "oynamak", "çalmak", "oyun" },
                    ["bear"] = new List<string> { "ayı", "taşımak", "katlanmak" }
                };
            }
        }

        /// <summary>
        /// Netleştirme stratejileri;
        /// </summary>
        private class ClarificationStrategies;
        {
            private readonly Dictionary<AmbiguityType, QuestionStrategy> _strategies;

            public ClarificationStrategies()
            {
                _strategies = new Dictionary<AmbiguityType, QuestionStrategy>
                {
                    [AmbiguityType.Lexical] = new LexicalAmbiguityStrategy(),
                    [AmbiguityType.Syntactic] = new SyntacticAmbiguityStrategy(),
                    [AmbiguityType.Semantic] = new SemanticAmbiguityStrategy(),
                    [AmbiguityType.Referential] = new ReferentialAmbiguityStrategy(),
                    [AmbiguityType.Temporal] = new TemporalAmbiguityStrategy()
                };
            }

            public QuestionStrategy GetQuestionStrategy(AmbiguityType type)
            {
                return _strategies.TryGetValue(type, out var strategy)
                    ? strategy;
                    : new DefaultAmbiguityStrategy();
            }

            public Dictionary<string, string> GetRequiredParameters(string intentName)
            {
                // Niyete özel gerekli parametreler;
                var parameters = new Dictionary<string, string>();

                switch (intentName.ToLower())
                {
                    case "order_product":
                        parameters.Add("product_name", "string");
                        parameters.Add("quantity", "integer");
                        parameters.Add("color", "string");
                        break;
                    case "schedule_meeting":
                        parameters.Add("date", "datetime");
                        parameters.Add("time", "datetime");
                        parameters.Add("participants", "list");
                        break;
                    case "search_information":
                        parameters.Add("query", "string");
                        parameters.Add("source", "string");
                        break;
                    case "set_reminder":
                        parameters.Add("reminder_text", "string");
                        parameters.Add("reminder_time", "datetime");
                        break;
                }

                return parameters;
            }
        }

        /// <summary>
        /// Soru stratejisi temel sınıfı;
        /// </summary>
        private abstract class QuestionStrategy;
        {
            public abstract QuestionType QuestionType { get; }
            public abstract string ExpectedResponseFormat { get; }

            public abstract Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis);
            public abstract Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis);
        }

        private class LexicalAmbiguityStrategy : QuestionStrategy;
        {
            public override QuestionType QuestionType => QuestionType.MultipleChoice;
            public override string ExpectedResponseFormat => "choice";

            public override async Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(
                    $"'{ambiguity.SourceText}' kelimesiyle neyi kastettiniz?");
            }

            public override async Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(ambiguity.PossibleInterpretations);
            }
        }

        private class ReferentialAmbiguityStrategy : QuestionStrategy;
        {
            public override QuestionType QuestionType => QuestionType.Disambiguation;
            public override string ExpectedResponseFormat => "reference";

            public override async Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(
                    $"'{ambiguity.SourceText}' neyi ifade ediyor?");
            }

            public override async Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(ambiguity.PossibleInterpretations);
            }
        }

        private class TemporalAmbiguityStrategy : QuestionStrategy;
        {
            public override QuestionType QuestionType => QuestionType.Specification;
            public override string ExpectedResponseFormat => "datetime";

            public override async Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(
                    $"Tam olarak ne zaman? (örnek: 2024-01-15, yarın saat 14:00)");
            }

            public override async Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                var options = new List<string>
                {
                    "bugün",
                    "yarın",
                    "gelecek hafta",
                    "bu ay sonu",
                    "belirtmek istemiyorum"
                };
                return await Task.FromResult(options);
            }
        }

        private class SemanticAmbiguityStrategy : QuestionStrategy;
        {
            public override QuestionType QuestionType => QuestionType.OpenEnded;
            public override string ExpectedResponseFormat => "explanation";

            public override async Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(
                    $"Lütfen '{ambiguity.SourceText}' ile ne demek istediğinizi biraz daha açıklar mısınız?");
            }

            public override async Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(new List<string>());
            }
        }

        private class SyntacticAmbiguityStrategy : QuestionStrategy;
        {
            public override QuestionType QuestionType => QuestionType.Confirmation;
            public override string ExpectedResponseFormat => "yes_no";

            public override async Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(
                    $"'{ambiguity.SourceText}' ifadesini şu şekilde mi anlamalıyım: '{ambiguity.PossibleInterpretations.FirstOrDefault()}'?");
            }

            public override async Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(new List<string> { "Evet", "Hayır" });
            }
        }

        private class DefaultAmbiguityStrategy : QuestionStrategy;
        {
            public override QuestionType QuestionType => QuestionType.Clarification;
            public override string ExpectedResponseFormat => "clarification";

            public override async Task<string> GenerateQuestionAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(
                    $"Lütfen '{ambiguity.SourceText}' hakkında daha fazla bilgi verir misiniz?");
            }

            public override async Task<List<string>> GenerateOptionsAsync(Ambiguity ambiguity, AmbiguityAnalysisResult analysis)
            {
                return await Task.FromResult(new List<string>());
            }
        }

        #endregion;
    }

    #region Custom Exceptions;

    /// <summary>
    /// Belirsizlik analiz istisnası;
    /// </summary>
    public class AmbiguityAnalysisException : Exception
    {
        public AmbiguityAnalysisException(string message) : base(message) { }
        public AmbiguityAnalysisException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Netleştirme soruları oluşturma istisnası;
    /// </summary>
    public class ClarificationGenerationException : Exception
    {
        public ClarificationGenerationException(string message) : base(message) { }
        public ClarificationGenerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Belirsizlik çözümleme istisnası;
    /// </summary>
    public class AmbiguityResolutionException : Exception
    {
        public AmbiguityResolutionException(string message) : base(message) { }
        public AmbiguityResolutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
