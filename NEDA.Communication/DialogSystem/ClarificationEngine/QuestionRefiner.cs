// NEDA.Brain/NLP_Engine/IntentRecognition/ClarificationEngine/QuestionRefiner.cs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Brain.NLP_Engine.Tokenization;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Core.Logging;
using NEDA.Common.Utilities;
using NEDA.Communication.DialogSystem.ClarificationEngine;

namespace NEDA.Brain.NLP_Engine.IntentRecognition.ClarificationEngine;
{
    /// <summary>
    /// Soruları netleştirme, belirsizlikleri çözme ve daha anlaşılır hale getirme motoru;
    /// Endüstriyel seviyede profesyonel implementasyon;
    /// </summary>
    public interface IQuestionRefiner;
    {
        /// <summary>
        /// Soruyu analiz edip belirsiz noktaları tespit eder;
        /// </summary>
        Task<QuestionAnalysisResult> AnalyzeQuestionAsync(string question,
            ConversationContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Belirsiz soruyu netleştirir ve daha anlaşılır hale getirir;
        /// </summary>
        Task<RefinementResult> RefineQuestionAsync(string question,
            QuestionAnalysisResult analysis,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Çoklu olası yorumlamaları tespit eder;
        /// </summary>
        Task<List<QuestionInterpretation>> GetPossibleInterpretationsAsync(string question,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sorunun belirsizlik seviyesini ölçer (0-1 arası)
        /// </summary>
        Task<double> CalculateAmbiguityScoreAsync(string question,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Soru netleştirme motoru - Profesyonel implementasyon;
    /// </summary>
    public class QuestionRefiner : IQuestionRefiner;
    {
        private readonly ILogger<QuestionRefiner> _logger;
        private readonly ITokenizer _tokenizer;
        private readonly ISyntaxAnalyzer _syntaxAnalyzer;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IAmbiguityResolver _ambiguityResolver;
        private readonly QuestionRefinerConfiguration _configuration;
        private readonly Random _random;
        private readonly Dictionary<string, RefinementPattern> _refinementPatterns;

        public QuestionRefiner(
            ILogger<QuestionRefiner> logger,
            ITokenizer tokenizer,
            ISyntaxAnalyzer syntaxAnalyzer,
            ISemanticAnalyzer semanticAnalyzer,
            IAmbiguityResolver ambiguityResolver,
            QuestionRefinerConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
            _syntaxAnalyzer = syntaxAnalyzer ?? throw new ArgumentNullException(nameof(syntaxAnalyzer));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _ambiguityResolver = ambiguityResolver ?? throw new ArgumentNullException(nameof(ambiguityResolver));
            _configuration = configuration ?? QuestionRefinerConfiguration.Default;
            _random = new Random(Guid.NewGuid().GetHashCode());

            _refinementPatterns = InitializeRefinementPatterns();

            _logger.LogInformation("QuestionRefiner initialized with {PatternCount} refinement patterns",
                _refinementPatterns.Count);
        }

        /// <summary>
        /// Soruyu analiz edip belirsiz noktaları tespit eder;
        /// </summary>
        public async Task<QuestionAnalysisResult> AnalyzeQuestionAsync(string question,
            ConversationContext context = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Analyzing question: {Question}", question);

                if (string.IsNullOrWhiteSpace(question))
                {
                    throw new ArgumentException("Question cannot be null or empty", nameof(question));
                }

                // 1. Tokenizasyon;
                var tokens = await _tokenizer.TokenizeAsync(question, cancellationToken);

                // 2. Sentaks analizi;
                var syntaxTree = await _syntaxAnalyzer.ParseAsync(question, cancellationToken);

                // 3. Semantik analiz;
                var semanticAnalysis = await _semanticAnalyzer.AnalyzeAsync(question, cancellationToken);

                // 4. Belirsizlik tespiti;
                var ambiguityScore = await CalculateAmbiguityScoreAsync(question, cancellationToken);

                // 5. Soru türü tespiti;
                var questionType = DetectQuestionType(tokens, syntaxTree);

                // 6. Eksik bilgilerin tespiti;
                var missingInformation = await DetectMissingInformationAsync(tokens, semanticAnalysis, context, cancellationToken);

                // 7. Çoklu yorumlamalar;
                var possibleInterpretations = await GetPossibleInterpretationsAsync(question, cancellationToken);

                var result = new QuestionAnalysisResult;
                {
                    OriginalQuestion = question,
                    Tokens = tokens,
                    SyntaxTree = syntaxTree,
                    SemanticAnalysis = semanticAnalysis,
                    AmbiguityScore = ambiguityScore,
                    QuestionType = questionType,
                    MissingInformation = missingInformation,
                    PossibleInterpretations = possibleInterpretations,
                    DetectedAmbiguities = DetectAmbiguities(tokens, semanticAnalysis),
                    ContextRelevance = CalculateContextRelevance(context, semanticAnalysis),
                    AnalysisTimestamp = DateTime.UtcNow,
                    AnalysisId = Guid.NewGuid()
                };

                _logger.LogInformation("Question analysis completed. Ambiguity: {AmbiguityScore:F2}, Type: {QuestionType}",
                    ambiguityScore, questionType);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Question analysis was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing question: {Question}", question);
                throw new QuestionAnalysisException($"Failed to analyze question: {question}", ex);
            }
        }

        /// <summary>
        /// Belirsiz soruyu netleştirir ve daha anlaşılır hale getirir;
        /// </summary>
        public async Task<RefinementResult> RefineQuestionAsync(string question,
            QuestionAnalysisResult analysis,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Refining question: {Question}", question);

                if (analysis.AmbiguityScore < _configuration.MinAmbiguityThreshold)
                {
                    // Soru yeterince net, netleştirme gerekmiyor;
                    return new RefinementResult;
                    {
                        OriginalQuestion = question,
                        RefinedQuestions = new List<string> { question },
                        AppliedRefinements = new List<RefinementType>(),
                        AmbiguityReduction = 0.0,
                        IsRefined = false,
                        ConfidenceScore = 1.0 - analysis.AmbiguityScore;
                    };
                }

                var refinements = new List<RefinementType>();
                var refinedQuestions = new List<string> { question };
                var currentAmbiguity = analysis.AmbiguityScore;

                // 1. Pronoun referanslarını netleştir;
                if (analysis.DetectedAmbiguities.HasFlag(AmbiguityType.PronounReference))
                {
                    var pronounRefined = await RefinePronounReferencesAsync(question, analysis, cancellationToken);
                    if (pronounRefined != null)
                    {
                        refinedQuestions.Add(pronounRefined);
                        refinements.Add(RefinementType.PronounClarification);
                        currentAmbiguity *= 0.7; // %30 azalma;
                    }
                }

                // 2. Belirsiz terimleri netleştir;
                if (analysis.DetectedAmbiguities.HasFlag(AmbiguityType.AmbiguousTerm))
                {
                    var termRefined = await RefineAmbiguousTermsAsync(question, analysis, cancellationToken);
                    if (termRefined != null)
                    {
                        refinedQuestions.Add(termRefined);
                        refinements.Add(RefinementType.TermClarification);
                        currentAmbiguity *= 0.8; // %20 azalma;
                    }
                }

                // 3. Eksik bilgileri ekle;
                if (analysis.MissingInformation.Any())
                {
                    var completed = await AddMissingInformationAsync(question, analysis, cancellationToken);
                    if (completed != null)
                    {
                        refinedQuestions.Add(completed);
                        refinements.Add(RefinementType.InformationCompletion);
                        currentAmbiguity *= 0.6; // %40 azalma;
                    }
                }

                // 4. Çok anlamlı kelimeleri netleştir;
                if (analysis.DetectedAmbiguities.HasFlag(AmbiguityType.WordSense))
                {
                    var senseRefined = await DisambiguateWordSensesAsync(question, analysis, cancellationToken);
                    if (senseRefined != null)
                    {
                        refinedQuestions.Add(senseRefined);
                        refinements.Add(RefinementType.WordSenseDisambiguation);
                        currentAmbiguity *= 0.75; // %25 azalma;
                    }
                }

                // 5. Soru yapısını iyileştir;
                var structureRefined = ImproveQuestionStructure(question, analysis);
                if (structureRefined != question)
                {
                    refinedQuestions.Add(structureRefined);
                    refinements.Add(RefinementType.StructureImprovement);
                    currentAmbiguity *= 0.9; // %10 azalma;
                }

                // Benzersiz soruları filtrele;
                refinedQuestions = refinedQuestions.Distinct().ToList();

                var ambiguityReduction = analysis.AmbiguityScore - currentAmbiguity;

                var result = new RefinementResult;
                {
                    OriginalQuestion = question,
                    RefinedQuestions = refinedQuestions,
                    AppliedRefinements = refinements,
                    AmbiguityReduction = Math.Max(0, ambiguityReduction),
                    IsRefined = refinements.Any(),
                    ConfidenceScore = 1.0 - currentAmbiguity,
                    SuggestedClarificationQuestions = GenerateClarificationQuestions(analysis),
                    RefinementTimestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Question refined. Applied {RefinementCount} refinements, " +
                    "ambiguity reduced by {Reduction:F2}",
                    refinements.Count, ambiguityReduction);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Question refinement was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refining question: {Question}", question);
                throw new QuestionRefinementException($"Failed to refine question: {question}", ex);
            }
        }

        /// <summary>
        /// Çoklu olası yorumlamaları tespit eder;
        /// </summary>
        public async Task<List<QuestionInterpretation>> GetPossibleInterpretationsAsync(string question,
            CancellationToken cancellationToken = default)
        {
            var interpretations = new List<QuestionInterpretation>();

            // 1. Farklı zaman yorumlamaları;
            var timeInterpretations = await GetTimeBasedInterpretationsAsync(question, cancellationToken);
            interpretations.AddRange(timeInterpretations);

            // 2. Farklı yer yorumlamaları;
            var locationInterpretations = await GetLocationBasedInterpretationsAsync(question, cancellationToken);
            interpretations.AddRange(locationInterpretations);

            // 3. Farklı özne yorumlamaları;
            var subjectInterpretations = await GetSubjectBasedInterpretationsAsync(question, cancellationToken);
            interpretations.AddRange(subjectInterpretations);

            // 4. Farklı eylem yorumlamaları;
            var actionInterpretations = await GetActionBasedInterpretationsAsync(question, cancellationToken);
            interpretations.AddRange(actionInterpretations);

            // 5. Semantic farklılıklar;
            var semanticInterpretations = await GetSemanticInterpretationsAsync(question, cancellationToken);
            interpretations.AddRange(semanticInterpretations);

            // Olasılık skorlarını normalize et;
            NormalizeInterpretationProbabilities(interpretations);

            return interpretations.OrderByDescending(i => i.Probability).ToList();
        }

        /// <summary>
        /// Sorunun belirsizlik seviyesini ölçer;
        /// </summary>
        public async Task<double> CalculateAmbiguityScoreAsync(string question,
            CancellationToken cancellationToken = default)
        {
            var score = 0.0;
            var factors = new List<double>();

            // 1. Lexical belirsizlik (çok anlamlı kelimeler)
            var lexicalAmbiguity = await CalculateLexicalAmbiguityAsync(question, cancellationToken);
            factors.Add(lexicalAmbiguity * 0.3);

            // 2. Structural belirsizlik (söz dizimsel)
            var structuralAmbiguity = await CalculateStructuralAmbiguityAsync(question, cancellationToken);
            factors.Add(structuralAmbiguity * 0.25);

            // 3. Semantic belirsizlik (anlamsal)
            var semanticAmbiguity = await CalculateSemanticAmbiguityAsync(question, cancellationToken);
            factors.Add(semanticAmbiguity * 0.25);

            // 4. Pragmatic belirsizlik (bağlamsal)
            var pragmaticAmbiguity = await CalculatePragmaticAmbiguityAsync(question, cancellationToken);
            factors.Add(pragmaticAmbiguity * 0.2);

            score = factors.Sum();

            // 0-1 aralığına sınırla;
            return Math.Min(1.0, Math.Max(0.0, score));
        }

        #region Private Methods - Profesyonel Implementasyon Detayları;

        private Dictionary<string, RefinementPattern> InitializeRefinementPatterns()
        {
            return new Dictionary<string, RefinementPattern>
            {
                {
                    "PRONOUN_REFERENCE",
                    new RefinementPattern;
                    {
                        PatternType = "PronounReference",
                        DetectionRegex = @"\b(he|she|it|they|them|his|her|its|their)\b",
                        ReplacementTemplate = "Can you specify who/what '{0}' refers to?",
                        Priority = 1,
                        ApplicableQuestionTypes = new[] { QuestionType.Who, QuestionType.What, QuestionType.Which }
                    }
                },
                {
                    "TIME_AMBIGUITY",
                    new RefinementPattern;
                    {
                        PatternType = "TimeAmbiguity",
                        DetectionRegex = @"\b(soon|later|recently|before|after)\b",
                        ReplacementTemplate = "Can you specify the exact time for '{0}'?",
                        Priority = 2,
                        ApplicableQuestionTypes = new[] { QuestionType.When, QuestionType.HowLong }
                    }
                },
                {
                    "QUANTITY_AMBIGUITY",
                    new RefinementPattern;
                    {
                        PatternType = "QuantityAmbiguity",
                        DetectionRegex = @"\b(some|few|many|several|most)\b",
                        ReplacementTemplate = "Can you specify the quantity for '{0}'?",
                        Priority = 3,
                        ApplicableQuestionTypes = new[] { QuestionType.HowMany, QuestionType.HowMuch }
                    }
                },
                {
                    "LOCATION_AMBIGUITY",
                    new RefinementPattern;
                    {
                        PatternType = "LocationAmbiguity",
                        DetectionRegex = @"\b(here|there|nearby|somewhere)\b",
                        ReplacementTemplate = "Can you specify the location for '{0}'?",
                        Priority = 2,
                        ApplicableQuestionTypes = new[] { QuestionType.Where }
                    }
                }
            };
        }

        private async Task<string> RefinePronounReferencesAsync(string question, QuestionAnalysisResult analysis,
            CancellationToken cancellationToken)
        {
            var pronouns = analysis.Tokens;
                .Where(t => t.PosTag?.StartsWith("PRP") == true)
                .Select(t => t.Text)
                .Distinct()
                .ToList();

            if (!pronouns.Any())
                return null;

            var refinement = question;
            foreach (var pronoun in pronouns)
            {
                var pattern = _refinementPatterns["PRONOUN_REFERENCE"];
                var clarification = string.Format(pattern.ReplacementTemplate, pronoun);
                refinement = refinement.Replace(pronoun, $"{pronoun} ({clarification})");
            }

            return refinement;
        }

        private async Task<string> RefineAmbiguousTermsAsync(string question, QuestionAnalysisResult analysis,
            CancellationToken cancellationToken)
        {
            var ambiguousTerms = await _ambiguityResolver.GetAmbiguousTermsAsync(question, cancellationToken);

            if (!ambiguousTerms.Any())
                return null;

            var refinement = question;
            foreach (var term in ambiguousTerms.Where(t => t.AmbiguityScore > 0.5))
            {
                var possibleMeanings = term.PossibleMeanings.Take(3).ToList();
                var meaningsText = string.Join(", ", possibleMeanings.Select(m => $"'{m}'"));
                refinement = refinement.Replace(term.Term, $"{term.Term} [meaning: {meaningsText}]");
            }

            return refinement;
        }

        private async Task<string> AddMissingInformationAsync(string question, QuestionAnalysisResult analysis,
            CancellationToken cancellationToken)
        {
            var missing = analysis.MissingInformation;
                .Where(m => m.Importance > 0.5)
                .OrderByDescending(m => m.Importance)
                .Take(2)
                .ToList();

            if (!missing.Any())
                return null;

            var additions = missing.Select(m => $"[{m.InformationType}: ?]");
            return $"{question} {string.Join(" ", additions)}";
        }

        private async Task<string> DisambiguateWordSensesAsync(string question, QuestionAnalysisResult analysis,
            CancellationToken cancellationToken)
        {
            var multiSenseWords = analysis.Tokens;
                .Where(t => t.WordSenseCount > 1)
                .Select(t => t.Text)
                .Distinct()
                .ToList();

            if (!multiSenseWords.Any())
                return null;

            var refinement = question;
            foreach (var word in multiSenseWords)
            {
                var senses = await _semanticAnalyzer.GetWordSensesAsync(word, cancellationToken);
                if (senses.Count > 1)
                {
                    var senseList = string.Join("/", senses.Select(s => s.Gloss.Substring(0, Math.Min(20, s.Gloss.Length))));
                    refinement = refinement.Replace(word, $"{word}({senseList})");
                }
            }

            return refinement;
        }

        private string ImproveQuestionStructure(string question, QuestionAnalysisResult analysis)
        {
            // Basit yapı iyileştirmeleri;
            var improved = question;

            // Çift negatifleri düzelt;
            improved = improved.Replace("don't not", "do");
            improved = improved.Replace("can't not", "can");

            // Belirsiz sıralamaları düzelt;
            if (improved.Contains(" or ") && !improved.Contains(" either ") && !improved.Contains(" whether "))
            {
                improved = improved.Replace(" or ", " or specifically ");
            }

            return improved;
        }

        private List<string> GenerateClarificationQuestions(QuestionAnalysisResult analysis)
        {
            var clarifications = new List<string>();

            if (analysis.DetectedAmbiguities.HasFlag(AmbiguityType.PronounReference))
            {
                clarifications.Add("Who exactly are you referring to?");
                clarifications.Add("Can you specify the subject of your question?");
            }

            if (analysis.DetectedAmbiguities.HasFlag(AmbiguityType.TimeReference))
            {
                clarifications.Add("When exactly are you referring to?");
                clarifications.Add("Can you specify the time period?");
            }

            if (analysis.DetectedAmbiguities.HasFlag(AmbiguityType.LocationReference))
            {
                clarifications.Add("Where exactly are you referring to?");
                clarifications.Add("Can you specify the location?");
            }

            if (analysis.MissingInformation.Any(m => m.InformationType == "Quantity"))
            {
                clarifications.Add("How many/much are you asking about?");
            }

            return clarifications.Distinct().Take(3).ToList();
        }

        private QuestionType DetectQuestionType(List<Token> tokens, SyntaxTree syntaxTree)
        {
            var firstToken = tokens.FirstOrDefault()?.Text?.ToLower();

            return firstToken switch;
            {
                "who" => QuestionType.Who,
                "what" => QuestionType.What,
                "when" => QuestionType.When,
                "where" => QuestionType.Where,
                "why" => QuestionType.Why,
                "how" => QuestionType.How,
                "which" => QuestionType.Which,
                "whose" => QuestionType.Whose,
                "whom" => QuestionType.Whom,
                "do" => QuestionType.YesNo,
                "does" => QuestionType.YesNo,
                "did" => QuestionType.YesNo,
                "is" => QuestionType.YesNo,
                "are" => QuestionType.YesNo,
                "was" => QuestionType.YesNo,
                "were" => QuestionType.YesNo,
                "can" => QuestionType.YesNo,
                "could" => QuestionType.YesNo,
                "will" => QuestionType.YesNo,
                "would" => QuestionType.YesNo,
                "should" => QuestionType.YesNo,
                "may" => QuestionType.YesNo,
                "might" => QuestionType.YesNo,
                _ => QuestionType.Statement;
            };
        }

        private async Task<List<MissingInformation>> DetectMissingInformationAsync(
            List<Token> tokens, SemanticAnalysis semanticAnalysis,
            ConversationContext context, CancellationToken cancellationToken)
        {
            var missingInfo = new List<MissingInformation>();

            // Semantic analize göre eksik bilgileri tespit et;
            var entities = semanticAnalysis.Entities ?? new List<SemanticEntity>();
            var relations = semanticAnalysis.Relations ?? new List<SemanticRelation>();

            // Eksik zaman bilgisi kontrolü;
            if (!entities.Any(e => e.Type == EntityType.DateTime || e.Type == EntityType.Time))
            {
                missingInfo.Add(new MissingInformation;
                {
                    InformationType = "Time",
                    Importance = 0.7,
                    DetectedFrom = "SemanticAnalysis",
                    Suggestion = "Specify time period"
                });
            }

            // Eksik yer bilgisi kontrolü;
            if (!entities.Any(e => e.Type == EntityType.Location || e.Type == EntityType.Place))
            {
                missingInfo.Add(new MissingInformation;
                {
                    InformationType = "Location",
                    Importance = 0.6,
                    DetectedFrom = "SemanticAnalysis",
                    Suggestion = "Specify location"
                });
            }

            // Eksik miktar bilgisi kontrolü;
            if (!entities.Any(e => e.Type == EntityType.Quantity || e.Type == EntityType.Number))
            {
                missingInfo.Add(new MissingInformation;
                {
                    InformationType = "Quantity",
                    Importance = 0.5,
                    DetectedFrom = "SemanticAnalysis",
                    Suggestion = "Specify quantity"
                });
            }

            return missingInfo;
        }

        private AmbiguityType DetectAmbiguities(List<Token> tokens, SemanticAnalysis semanticAnalysis)
        {
            var ambiguities = AmbiguityType.None;

            // Pronoun belirsizliği;
            if (tokens.Any(t => t.PosTag?.StartsWith("PRP") == true))
            {
                ambiguities |= AmbiguityType.PronounReference;
            }

            // Zaman belirsizliği;
            if (tokens.Any(t => _ambiguousTimeWords.Contains(t.Text.ToLower())))
            {
                ambiguities |= AmbiguityType.TimeReference;
            }

            // Yer belirsizliği;
            if (tokens.Any(t => _ambiguousLocationWords.Contains(t.Text.ToLower())))
            {
                ambiguities |= AmbiguityType.LocationReference;
            }

            // Çok anlamlı kelimeler;
            if (tokens.Any(t => t.WordSenseCount > 1))
            {
                ambiguities |= AmbiguityType.WordSense;
            }

            // Belirsiz terimler;
            if (semanticAnalysis?.AmbiguousTerms?.Any() == true)
            {
                ambiguities |= AmbiguityType.AmbiguousTerm;
            }

            return ambiguities;
        }

        private double CalculateContextRelevance(ConversationContext context, SemanticAnalysis semanticAnalysis)
        {
            if (context == null || semanticAnalysis == null)
                return 0.5;

            var relevance = 0.0;

            // Konu benzerliği;
            if (!string.IsNullOrEmpty(context.Topic) &&
                !string.IsNullOrEmpty(semanticAnalysis.MainTopic))
            {
                relevance += 0.3;
            }

            // Entity tutarlılığı;
            var contextEntities = context.Entities ?? new List<string>();
            var currentEntities = semanticAnalysis.Entities?.Select(e => e.Text) ?? new List<string>();

            var commonEntities = contextEntities.Intersect(currentEntities).Count();
            relevance += (commonEntities * 0.1);

            // Zaman uyumu;
            if (context.TimeReference != null && semanticAnalysis.Entities?.Any(e => e.Type == EntityType.DateTime) == true)
            {
                relevance += 0.2;
            }

            return Math.Min(1.0, relevance);
        }

        private readonly HashSet<string> _ambiguousTimeWords = new()
        {
            "soon", "later", "recently", "before", "after", "eventually",
            "sometime", "eventually", "shortly", "momentarily"
        };

        private readonly HashSet<string> _ambiguousLocationWords = new()
        {
            "here", "there", "nearby", "somewhere", "elsewhere",
            "over there", "around", "close"
        };

        private async Task<double> CalculateLexicalAmbiguityAsync(string question, CancellationToken cancellationToken)
        {
            var tokens = await _tokenizer.TokenizeAsync(question, cancellationToken);
            var multiSenseCount = tokens.Count(t => t.WordSenseCount > 1);

            return multiSenseCount / (double)Math.Max(1, tokens.Count);
        }

        private async Task<double> CalculateStructuralAmbiguityAsync(string question, CancellationToken cancellationToken)
        {
            var tree = await _syntaxAnalyzer.ParseAsync(question, cancellationToken);

            // PP attachment belirsizliği kontrolü;
            var ppAttachments = CountPPAttachments(tree);
            var attachmentScore = ppAttachments * 0.2;

            // Coordination scope belirsizliği;
            var coordinations = CountCoordinations(tree);
            var coordinationScore = coordinations * 0.15;

            return Math.Min(1.0, attachmentScore + coordinationScore);
        }

        private async Task<double> CalculateSemanticAmbiguityAsync(string question, CancellationToken cancellationToken)
        {
            var analysis = await _semanticAnalyzer.AnalyzeAsync(question, cancellationToken);

            var score = 0.0;

            // Entity belirsizliği;
            if (analysis.Entities?.Any(e => e.Confidence < 0.7) == true)
            {
                score += 0.3;
            }

            // Relation belirsizliği;
            if (analysis.Relations?.Any(r => r.Confidence < 0.7) == true)
            {
                score += 0.3;
            }

            // Topic belirsizliği;
            if (analysis.TopicCount > 1)
            {
                score += 0.4;
            }

            return Math.Min(1.0, score);
        }

        private async Task<double> CalculatePragmaticAmbiguityAsync(string question, CancellationToken cancellationToken)
        {
            // Speech act belirsizliği;
            var isQuestion = question.EndsWith("?");
            var hasQuestionWord = question.ToLower().Split(' ').FirstOrDefault()?.StartsWith("wh") == true;

            var speechActScore = (isQuestion && !hasQuestionWord) ? 0.3 : 0.0;

            // Implicature belirsizliği;
            var implicatureWords = new[] { "some", "maybe", "perhaps", "possibly" };
            var implicatureCount = implicatureWords.Count(w => question.ToLower().Contains(w));
            var implicatureScore = implicatureCount * 0.15;

            return Math.Min(1.0, speechActScore + implicatureScore);
        }

        private int CountPPAttachments(SyntaxTree tree)
        {
            // Basit PP attachment sayımı (profesyonel implementasyonda daha karmaşık olur)
            return tree.Nodes.Count(n => n.Type == "PP");
        }

        private int CountCoordinations(SyntaxTree tree)
        {
            return tree.Nodes.Count(n => n.Type == "CC");
        }

        private async Task<List<QuestionInterpretation>> GetTimeBasedInterpretationsAsync(
            string question, CancellationToken cancellationToken)
        {
            var interpretations = new List<QuestionInterpretation>();

            // Geçmiş zaman yorumu;
            interpretations.Add(new QuestionInterpretation;
            {
                Interpretation = question.Replace(" is ", " was ")
                                       .Replace(" are ", " were ")
                                       .Replace(" do ", " did "),
                Probability = 0.3,
                VariationType = "Temporal",
                Explanation = "Interpreted as past tense"
            });

            // Gelecek zaman yorumu;
            interpretations.Add(new QuestionInterpretation;
            {
                Interpretation = question.Replace(" is ", " will be ")
                                       .Replace(" are ", " will be ")
                                       .Replace(" do ", " will "),
                Probability = 0.2,
                VariationType = "Temporal",
                Explanation = "Interpreted as future tense"
            });

            return interpretations;
        }

        private async Task<List<QuestionInterpretation>> GetLocationBasedInterpretationsAsync(
            string question, CancellationToken cancellationToken)
        {
            // Lokasyona bağlı yorumlamalar;
            return new List<QuestionInterpretation>();
        }

        private async Task<List<QuestionInterpretation>> GetSubjectBasedInterpretationsAsync(
            string question, CancellationToken cancellationToken)
        {
            // Özneye bağlı yorumlamalar;
            return new List<QuestionInterpretation>();
        }

        private async Task<List<QuestionInterpretation>> GetActionBasedInterpretationsAsync(
            string question, CancellationToken cancellationToken)
        {
            // Eyleme bağlı yorumlamalar;
            return new List<QuestionInterpretation>();
        }

        private async Task<List<QuestionInterpretation>> GetSemanticInterpretationsAsync(
            string question, CancellationToken cancellationToken)
        {
            // Semantik yorumlamalar;
            return new List<QuestionInterpretation>();
        }

        private void NormalizeInterpretationProbabilities(List<QuestionInterpretation> interpretations)
        {
            var total = interpretations.Sum(i => i.Probability);
            if (total > 0)
            {
                foreach (var interpretation in interpretations)
                {
                    interpretation.Probability /= total;
                }
            }
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
                    // Managed kaynakları serbest bırak;
                    _refinementPatterns?.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~QuestionRefiner()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types - Profesyonel Data Modelleri;

    /// <summary>
    /// Soru analiz sonucu;
    /// </summary>
    public class QuestionAnalysisResult;
    {
        public string OriginalQuestion { get; set; }
        public List<Token> Tokens { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public SemanticAnalysis SemanticAnalysis { get; set; }
        public double AmbiguityScore { get; set; }
        public QuestionType QuestionType { get; set; }
        public List<MissingInformation> MissingInformation { get; set; }
        public List<QuestionInterpretation> PossibleInterpretations { get; set; }
        public AmbiguityType DetectedAmbiguities { get; set; }
        public double ContextRelevance { get; set; }
        public DateTime AnalysisTimestamp { get; set; }
        public Guid AnalysisId { get; set; }
    }

    /// <summary>
    /// Netleştirme sonucu;
    /// </summary>
    public class RefinementResult;
    {
        public string OriginalQuestion { get; set; }
        public List<string> RefinedQuestions { get; set; }
        public List<RefinementType> AppliedRefinements { get; set; }
        public double AmbiguityReduction { get; set; }
        public bool IsRefined { get; set; }
        public double ConfidenceScore { get; set; }
        public List<string> SuggestedClarificationQuestions { get; set; }
        public DateTime RefinementTimestamp { get; set; }
    }

    /// <summary>
    /// Soru yorumlaması;
    /// </summary>
    public class QuestionInterpretation;
    {
        public string Interpretation { get; set; }
        public double Probability { get; set; }
        public string VariationType { get; set; }
        public string Explanation { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Eksik bilgi;
    /// </summary>
    public class MissingInformation;
    {
        public string InformationType { get; set; }
        public double Importance { get; set; }
        public string DetectedFrom { get; set; }
        public string Suggestion { get; set; }
        public bool IsCritical { get; set; }
    }

    /// <summary>
    /// Netleştirme pattern'ı;
    /// </summary>
    public class RefinementPattern;
    {
        public string PatternType { get; set; }
        public string DetectionRegex { get; set; }
        public string ReplacementTemplate { get; set; }
        public int Priority { get; set; }
        public QuestionType[] ApplicableQuestionTypes { get; set; }
        public double EffectivenessScore { get; set; } = 1.0;
    }

    /// <summary>
    /// Konuşma bağlamı;
    /// </summary>
    public class ConversationContext;
    {
        public string Topic { get; set; }
        public List<string> Entities { get; set; }
        public DateTime? TimeReference { get; set; }
        public string Location { get; set; }
        public List<string> PreviousQuestions { get; set; }
        public Dictionary<string, object> UserPreferences { get; set; }
        public string ConversationId { get; set; }
    }

    /// <summary>
    /// Soru türleri;
    /// </summary>
    public enum QuestionType;
    {
        Who,
        What,
        When,
        Where,
        Why,
        How,
        Which,
        Whose,
        Whom,
        YesNo,
        Statement,
        HowMany,
        HowMuch,
        HowLong,
        HowFar,
        HowOften;
    }

    /// <summary>
    /// Netleştirme türleri;
    /// </summary>
    public enum RefinementType;
    {
        PronounClarification,
        TermClarification,
        InformationCompletion,
        WordSenseDisambiguation,
        StructureImprovement,
        TimeSpecification,
        LocationSpecification,
        QuantitySpecification;
    }

    /// <summary>
    /// Belirsizlik türleri;
    /// </summary>
    [Flags]
    public enum AmbiguityType;
    {
        None = 0,
        PronounReference = 1,
        TimeReference = 2,
        LocationReference = 4,
        WordSense = 8,
        AmbiguousTerm = 16,
        Structural = 32,
        Semantic = 64,
        Pragmatic = 128;
    }

    /// <summary>
    /// Yapılandırma;
    /// </summary>
    public class QuestionRefinerConfiguration;
    {
        public double MinAmbiguityThreshold { get; set; } = 0.3;
        public double MaxRefinementAttempts { get; set; } = 3;
        public bool EnableDeepAnalysis { get; set; } = true;
        public TimeSpan AnalysisTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public double ConfidenceThreshold { get; set; } = 0.7;

        public static QuestionRefinerConfiguration Default => new()
        {
            MinAmbiguityThreshold = 0.3,
            MaxRefinementAttempts = 3,
            EnableDeepAnalysis = true,
            AnalysisTimeout = TimeSpan.FromSeconds(5),
            ConfidenceThreshold = 0.7;
        };
    }

    #endregion;

    #region Custom Exceptions - Profesyonel Hata Yönetimi;

    /// <summary>
    /// Soru analiz istisnası;
    /// </summary>
    public class QuestionAnalysisException : Exception
    {
        public QuestionAnalysisException(string message) : base(message) { }
        public QuestionAnalysisException(string message, Exception innerException)
            : base(message, innerException) { }

        public string Question { get; set; }
        public DateTime ErrorTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Soru netleştirme istisnası;
    /// </summary>
    public class QuestionRefinementException : Exception
    {
        public QuestionRefinementException(string message) : base(message) { }
        public QuestionRefinementException(string message, Exception innerException)
            : base(message, innerException) { }

        public string OriginalQuestion { get; set; }
        public RefinementResult PartialResult { get; set; }
        public DateTime ErrorTime { get; set; } = DateTime.UtcNow;
    }

    #endregion;
}

// Not: Bu dosya için gerekli bağımlılıklar:
// - NEDA.Brain.NLP_Engine.Tokenization.ITokenizer;
// - NEDA.Brain.NLP_Engine.SyntaxAnalysis.ISyntaxAnalyzer;
// - NEDA.Brain.NLP_Engine.SemanticUnderstanding.ISemanticAnalyzer;
// - NEDA.Communication.DialogSystem.ClarificationEngine.IAmbiguityResolver;
// - Microsoft.Extensions.Logging.ILogger;
// - NEDA.Core.Logging (özel logging utilities)
// - NEDA.Common.Utilities (common utilities)
