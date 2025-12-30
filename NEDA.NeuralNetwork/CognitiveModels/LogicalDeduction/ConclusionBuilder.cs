using NEDA.Brain.DecisionMaking;
using NEDA.Brain.DecisionMaking.LogicProcessor;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.CognitiveModels.LogicalDeduction;
{
    /// <summary>
    /// Mantıksal çıkarım sonuçlarını oluşturan ve yapılandıran gelişmiş sonuç oluşturucu;
    /// </summary>
    public class ConclusionBuilder : IConclusionBuilder, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly IInferenceSystem _inferenceSystem;
        private readonly List<LogicalConclusion> _conclusions;
        private readonly Dictionary<string, double> _confidenceScores;
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        /// <summary>
        /// Sonuç yapılandırma seçenekleri;
        /// </summary>
        public class ConclusionOptions;
        {
            public bool EnableValidation { get; set; } = true;
            public double MinimumConfidenceThreshold { get; set; } = 0.7;
            public int MaximumConclusionDepth { get; set; } = 5;
            public bool IncludeAlternativeConclusions { get; set; } = true;
            public bool EnableCrossReferencing { get; set; } = true;
            public ConclusionFormat OutputFormat { get; set; } = ConclusionFormat.Structured;
        }

        /// <summary>
        /// Sonuç formatı enum'u;
        /// </summary>
        public enum ConclusionFormat;
        {
            Structured,
            NaturalLanguage,
            FormalLogic,
            DecisionTree,
            Probabilistic;
        }

        /// <summary>
        /// Mantıksal sonuç temsil sınıfı;
        /// </summary>
        public class LogicalConclusion;
        {
            public string Id { get; } = Guid.NewGuid().ToString();
            public string Premise { get; set; }
            public string Conclusion { get; set; }
            public double ConfidenceScore { get; set; }
            public List<string> SupportingEvidence { get; } = new List<string>();
            public List<string> ContradictingEvidence { get; } = new List<string>();
            public LogicRule AppliedRule { get; set; }
            public DateTime Timestamp { get; } = DateTime.UtcNow;
            public ConclusionType Type { get; set; }
            public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Mantıksal kural temsil sınıfı;
        /// </summary>
        public class LogicRule;
        {
            public string Name { get; set; }
            public string Pattern { get; set; }
            public string Transformation { get; set; }
            public double Weight { get; set; }
            public RuleType Type { get; set; }
        }

        /// <summary>
        /// Kural tipi enum'u;
        /// </summary>
        public enum RuleType;
        {
            Deductive,
            Inductive,
            Abductive,
            Default,
            Constraint;
        }

        /// <summary>
        /// Sonuç tipi enum'u;
        /// </summary>
        public enum ConclusionType;
        {
            Definitive,
            Probable,
            Possible,
            Contradictory,
            Inconclusive;
        }

        /// <summary>
        /// Sonuç oluşturucuyu başlatır;
        /// </summary>
        public ConclusionBuilder(ILogger logger, IKnowledgeGraph knowledgeGraph, IInferenceSystem inferenceSystem)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _inferenceSystem = inferenceSystem ?? throw new ArgumentNullException(nameof(inferenceSystem));
            _conclusions = new List<LogicalConclusion>();
            _confidenceScores = new Dictionary<string, double>();

            _logger.LogInformation("ConclusionBuilder initialized", GetType().Name);
        }

        /// <summary>
        /// Verilen öncüllerden sonuç oluşturur;
        /// </summary>
        public async Task<LogicalConclusion> BuildConclusionAsync(
            IEnumerable<string> premises,
            ConclusionOptions options = null)
        {
            if (premises == null || !premises.Any())
                throw new ArgumentException("At least one premise is required", nameof(premises));

            options = options ?? new ConclusionOptions();

            try
            {
                _logger.LogDebug($"Building conclusion from {premises.Count()} premises", GetType().Name);

                // Öncülleri analiz et;
                var analyzedPremises = await AnalyzePremisesAsync(premises, options);

                // Mantıksal çıkarım uygula;
                var inferenceResult = await ApplyLogicalInferenceAsync(analyzedPremises, options);

                // Sonuç geçerliliğini doğrula;
                var validationResult = await ValidateConclusionAsync(inferenceResult, options);

                // Alternatif sonuçları oluştur (eğer aktifse)
                List<LogicalConclusion> alternatives = null;
                if (options.IncludeAlternativeConclusions)
                {
                    alternatives = await GenerateAlternativeConclusionsAsync(inferenceResult, options);
                }

                // Sonuç nesnesini oluştur;
                var conclusion = new LogicalConclusion;
                {
                    Premise = string.Join("; ", premises),
                    Conclusion = inferenceResult.Conclusion,
                    ConfidenceScore = validationResult.ConfidenceScore,
                    Type = DetermineConclusionType(validationResult),
                    AppliedRule = inferenceResult.AppliedRule;
                };

                // Destekleyici kanıtları ekle;
                foreach (var evidence in inferenceResult.SupportingEvidence)
                {
                    conclusion.SupportingEvidence.Add(evidence);
                }

                // Metadata ekle;
                conclusion.Metadata["InferenceMethod"] = inferenceResult.Method;
                conclusion.Metadata["ValidationScore"] = validationResult.Score;
                conclusion.Metadata["GenerationTimestamp"] = DateTime.UtcNow;

                lock (_lockObject)
                {
                    _conclusions.Add(conclusion);
                    _confidenceScores[conclusion.Id] = conclusion.ConfidenceScore;
                }

                _logger.LogInformation($"Conclusion built with confidence: {conclusion.ConfidenceScore:P2}",
                    GetType().Name);

                return conclusion;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to build conclusion: {ex.Message}", GetType().Name, ex);
                throw new ConclusionBuildingException($"Conclusion building failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Zincirleme sonuç oluşturur (birden fazla çıkarım seviyesi)
        /// </summary>
        public async Task<List<LogicalConclusion>> BuildChainedConclusionsAsync(
            string initialPremise,
            int depth,
            ConclusionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(initialPremise))
                throw new ArgumentException("Initial premise cannot be empty", nameof(initialPremise));

            if (depth <= 0 || depth > 10)
                throw new ArgumentException("Depth must be between 1 and 10", nameof(depth));

            options = options ?? new ConclusionOptions();
            var chain = new List<LogicalConclusion>();

            try
            {
                string currentPremise = initialPremise;

                for (int i = 0; i < depth; i++)
                {
                    _logger.LogDebug($"Building chain conclusion level {i + 1}/{depth}", GetType().Name);

                    var conclusion = await BuildConclusionAsync(new[] { currentPremise }, options);
                    chain.Add(conclusion);

                    // Zinciri devam ettir;
                    currentPremise = conclusion.Conclusion;

                    // Güven eşiğini kontrol et;
                    if (conclusion.ConfidenceScore < options.MinimumConfidenceThreshold)
                    {
                        _logger.LogWarning($"Chain broken at level {i + 1} due to low confidence", GetType().Name);
                        break;
                    }
                }

                return chain;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chained conclusion building failed: {ex.Message}", GetType().Name, ex);
                throw;
            }
        }

        /// <summary>
        /// Çelişkili öncüllerden sonuç oluşturur;
        /// </summary>
        public async Task<LogicalConclusion> BuildFromContradictionsAsync(
            IEnumerable<string> contradictoryPremises,
            ResolutionStrategy strategy,
            ConclusionOptions options = null)
        {
            if (contradictoryPremises == null || contradictoryPremises.Count() < 2)
                throw new ArgumentException("At least two contradictory premises are required",
                    nameof(contradictoryPremises));

            options = options ?? new ConclusionOptions();

            try
            {
                _logger.LogDebug($"Resolving contradictions using {strategy} strategy", GetType().Name);

                // Çelişkileri analiz et;
                var contradictionAnalysis = await AnalyzeContradictionsAsync(contradictoryPremises);

                // Çözüm stratejisini uygula;
                var resolvedPremise = await ApplyResolutionStrategyAsync(
                    contradictionAnalysis,
                    strategy,
                    options);

                // Çözümlenmiş öncülden sonuç oluştur;
                return await BuildConclusionAsync(new[] { resolvedPremise }, options);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Contradiction resolution failed: {ex.Message}", GetType().Name, ex);
                throw new ConclusionBuildingException($"Failed to resolve contradictions: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Olasılıksal sonuç oluşturur;
        /// </summary>
        public async Task<ProbabilisticConclusion> BuildProbabilisticConclusionAsync(
            IEnumerable<WeightedPremise> premises,
            ConclusionOptions options = null)
        {
            if (premises == null || !premises.Any())
                throw new ArgumentException("At least one weighted premise is required", nameof(premises));

            options = options ?? new ConclusionOptions();

            try
            {
                _logger.LogDebug($"Building probabilistic conclusion from {premises.Count()} weighted premises",
                    GetType().Name);

                // Bayesçi çıkarım uygula;
                var bayesianResult = await ApplyBayesianInferenceAsync(premises, options);

                // Olasılık dağılımını hesapla;
                var distribution = await CalculateProbabilityDistributionAsync(bayesianResult, options);

                // Sonuç nesnesini oluştur;
                var conclusion = new ProbabilisticConclusion;
                {
                    MostProbableConclusion = distribution.MostProbable,
                    Probability = distribution.HighestProbability,
                    AllProbabilities = distribution.Probabilities,
                    ConfidenceInterval = distribution.ConfidenceInterval,
                    Entropy = distribution.Entropy;
                };

                return conclusion;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Probabilistic conclusion building failed: {ex.Message}", GetType().Name, ex);
                throw new ConclusionBuildingException(
                    $"Probabilistic conclusion building failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sonuçları belirtilen formatta dışa aktarır;
        /// </summary>
        public async Task<string> ExportConclusionAsync(
            LogicalConclusion conclusion,
            ConclusionFormat format,
            ExportOptions exportOptions = null)
        {
            if (conclusion == null)
                throw new ArgumentNullException(nameof(conclusion));

            try
            {
                _logger.LogDebug($"Exporting conclusion {conclusion.Id} in {format} format", GetType().Name);

                switch (format)
                {
                    case ConclusionFormat.NaturalLanguage:
                        return await FormatAsNaturalLanguageAsync(conclusion, exportOptions);

                    case ConclusionFormat.FormalLogic:
                        return await FormatAsFormalLogicAsync(conclusion, exportOptions);

                    case ConclusionFormat.DecisionTree:
                        return await FormatAsDecisionTreeAsync(conclusion, exportOptions);

                    case ConclusionFormat.Probabilistic:
                        return await FormatAsProbabilisticAsync(conclusion, exportOptions);

                    case ConclusionFormat.Structured:
                    default:
                        return await FormatAsStructuredAsync(conclusion, exportOptions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Conclusion export failed: {ex.Message}", GetType().Name, ex);
                throw new ConclusionExportException($"Failed to export conclusion: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sonuç geçerliliğini doğrular;
        /// </summary>
        public async Task<ValidationResult> ValidateConclusionAsync(
            LogicalConclusion conclusion,
            ValidationCriteria criteria = null)
        {
            if (conclusion == null)
                throw new ArgumentNullException(nameof(conclusion));

            criteria = criteria ?? new ValidationCriteria();

            try
            {
                _logger.LogDebug($"Validating conclusion {conclusion.Id}", GetType().Name);

                var validationTasks = new List<Task<ValidationMetric>>();

                // Mantıksal geçerlilik kontrolü;
                validationTasks.Add(ValidateLogicalConsistencyAsync(conclusion));

                // Kanıt desteği kontrolü;
                validationTasks.Add(ValidateEvidenceSupportAsync(conclusion, criteria));

                // Çelişki kontrolü;
                validationTasks.Add(CheckForContradictionsAsync(conclusion));

                // Gerçek dünya uygunluğu kontrolü;
                validationTasks.Add(ValidateRealWorldPlausibilityAsync(conclusion));

                await Task.WhenAll(validationTasks);

                // Sonuçları birleştir;
                var metrics = validationTasks.Select(t => t.Result).ToList();
                var overallScore = CalculateOverallValidationScore(metrics);

                var result = new ValidationResult;
                {
                    ConclusionId = conclusion.Id,
                    IsValid = overallScore >= criteria.MinimumValidationScore,
                    OverallScore = overallScore,
                    Metrics = metrics,
                    ValidationTimestamp = DateTime.UtcNow;
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Conclusion validation failed: {ex.Message}", GetType().Name, ex);
                throw new ValidationException($"Conclusion validation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Önceki sonuçları getirir;
        /// </summary>
        public IEnumerable<LogicalConclusion> GetPreviousConclusions(
            int count = 10,
            double minimumConfidence = 0.0)
        {
            lock (_lockObject)
            {
                return _conclusions;
                    .Where(c => c.ConfidenceScore >= minimumConfidence)
                    .OrderByDescending(c => c.Timestamp)
                    .Take(count)
                    .ToList();
            }
        }

        /// <summary>
        /// Sonuç önbelleğini temizler;
        /// </summary>
        public void ClearConclusionCache()
        {
            lock (_lockObject)
            {
                _conclusions.Clear();
                _confidenceScores.Clear();
                _logger.LogInformation("Conclusion cache cleared", GetType().Name);
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
                    ClearConclusionCache();
                    _logger.LogInformation("ConclusionBuilder disposed", GetType().Name);
                }

                _disposed = true;
            }
        }

        #region Private Implementation Methods;

        private async Task<PremiseAnalysis> AnalyzePremisesAsync(
            IEnumerable<string> premises,
            ConclusionOptions options)
        {
            var analysis = new PremiseAnalysis();

            foreach (var premise in premises)
            {
                var premiseInfo = await _knowledgeGraph.AnalyzeStatementAsync(premise);
                analysis.Premises.Add(premiseInfo);

                // Öncül türlerini sınıflandır;
                if (await IsFactualPremiseAsync(premise))
                    analysis.FactualPremises.Add(premiseInfo);
                else if (await IsHypotheticalPremiseAsync(premise))
                    analysis.HypotheticalPremises.Add(premiseInfo);
            }

            return analysis;
        }

        private async Task<InferenceResult> ApplyLogicalInferenceAsync(
            PremiseAnalysis analysis,
            ConclusionOptions options)
        {
            // Uygun çıkarım kurallarını seç;
            var applicableRules = await SelectInferenceRulesAsync(analysis, options);

            // Çıkarım kurallarını uygula;
            var inferenceResults = new List<InferenceResult>();

            foreach (var rule in applicableRules)
            {
                try
                {
                    var result = await _inferenceSystem.ApplyRuleAsync(analysis, rule);
                    inferenceResults.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Rule {rule.Name} application failed: {ex.Message}", GetType().Name);
                }
            }

            // En iyi sonucu seç;
            var bestResult = inferenceResults;
                .OrderByDescending(r => r.ConfidenceScore)
                .FirstOrDefault();

            if (bestResult == null)
                throw new InferenceException("No valid inference results generated");

            return bestResult;
        }

        private async Task<ConclusionValidation> ValidateConclusionAsync(
            InferenceResult inferenceResult,
            ConclusionOptions options)
        {
            var validation = new ConclusionValidation();

            // İç tutarlılık kontrolü;
            validation.InternalConsistency = await CheckInternalConsistencyAsync(inferenceResult);

            // Harici doğrulama;
            if (options.EnableCrossReferencing)
            {
                validation.ExternalValidation = await ValidateExternallyAsync(inferenceResult);
            }

            // Kanıt desteği hesaplama;
            validation.EvidenceSupport = CalculateEvidenceSupport(inferenceResult);

            // Güven skoru hesaplama;
            validation.ConfidenceScore = CalculateConfidenceScore(validation);

            return validation;
        }

        private async Task<List<LogicalConclusion>> GenerateAlternativeConclusionsAsync(
            InferenceResult primaryResult,
            ConclusionOptions options)
        {
            var alternatives = new List<LogicalConclusion>();

            // Alternatif kuralları dene;
            var alternativeRules = await GetAlternativeRulesAsync(primaryResult.AppliedRule);

            foreach (var rule in alternativeRules)
            {
                try
                {
                    var alternativeAnalysis = await ReanalyzeWithAlternativeRuleAsync(
                        primaryResult,
                        rule);

                    var alternativeResult = await _inferenceSystem.ApplyRuleAsync(
                        alternativeAnalysis,
                        rule);

                    if (alternativeResult.ConfidenceScore >= options.MinimumConfidenceThreshold)
                    {
                        var altConclusion = new LogicalConclusion;
                        {
                            Premise = primaryResult.OriginalPremise,
                            Conclusion = alternativeResult.Conclusion,
                            ConfidenceScore = alternativeResult.ConfidenceScore,
                            AppliedRule = rule,
                            Type = ConclusionType.Possible;
                        };

                        alternatives.Add(altConclusion);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Alternative rule {rule.Name} failed: {ex.Message}", GetType().Name);
                }
            }

            return alternatives;
        }

        private async Task<ContradictionAnalysis> AnalyzeContradictionsAsync(
            IEnumerable<string> contradictoryPremises)
        {
            var analysis = new ContradictionAnalysis();

            // Çelişkileri tespit et;
            foreach (var premise in contradictoryPremises)
            {
                var negation = await FindNegationAsync(premise);
                if (contradictoryPremises.Contains(negation))
                {
                    analysis.DirectContradictions.Add(new Tuple<string, string>(premise, negation));
                }
            }

            // Çelişki seviyelerini analiz et;
            analysis.ContradictionLevel = CalculateContradictionLevel(analysis);

            return analysis;
        }

        private ConclusionType DetermineConclusionType(ConclusionValidation validation)
        {
            if (validation.ConfidenceScore >= 0.9)
                return ConclusionType.Definitive;
            else if (validation.ConfidenceScore >= 0.7)
                return ConclusionType.Probable;
            else if (validation.ConfidenceScore >= 0.5)
                return ConclusionType.Possible;
            else if (validation.ContradictionScore > 0.5)
                return ConclusionType.Contradictory;
            else;
                return ConclusionType.Inconclusive;
        }

        private double CalculateConfidenceScore(ConclusionValidation validation)
        {
            // Ağırlıklı ortalama hesapla;
            double score = 0.0;
            double totalWeight = 0.0;

            if (validation.InternalConsistency.HasValue)
            {
                score += validation.InternalConsistency.Value * 0.3;
                totalWeight += 0.3;
            }

            if (validation.ExternalValidation.HasValue)
            {
                score += validation.ExternalValidation.Value * 0.4;
                totalWeight += 0.4;
            }

            if (validation.EvidenceSupport.HasValue)
            {
                score += validation.EvidenceSupport.Value * 0.3;
                totalWeight += 0.3;
            }

            return totalWeight > 0 ? score / totalWeight : 0.0;
        }

        #endregion;

        #region Export Formatting Methods;

        private async Task<string> FormatAsNaturalLanguageAsync(
            LogicalConclusion conclusion,
            ExportOptions options)
        {
            var builder = new StringBuilder();

            builder.AppendLine($"Based on the premise: \"{conclusion.Premise}\"");
            builder.AppendLine();

            if (conclusion.ConfidenceScore >= 0.8)
                builder.Append($"We can confidently conclude that: ");
            else if (conclusion.ConfidenceScore >= 0.6)
                builder.Append($"It is likely that: ");
            else;
                builder.Append($"It is possible that: ");

            builder.AppendLine($"\"{conclusion.Conclusion}\"");
            builder.AppendLine();

            builder.AppendLine($"Confidence level: {conclusion.ConfidenceScore:P2}");

            if (conclusion.SupportingEvidence.Any())
            {
                builder.AppendLine();
                builder.AppendLine("Supporting evidence:");
                foreach (var evidence in conclusion.SupportingEvidence.Take(3))
                {
                    builder.AppendLine($"- {evidence}");
                }
            }

            return builder.ToString();
        }

        private async Task<string> FormatAsFormalLogicAsync(
            LogicalConclusion conclusion,
            ExportOptions options)
        {
            var builder = new StringBuilder();

            builder.AppendLine("FORMAL LOGIC REPRESENTATION");
            builder.AppendLine("===========================");
            builder.AppendLine();

            builder.AppendLine($"Premise(s): {ConvertToLogicalNotation(conclusion.Premise)}");
            builder.AppendLine($"Rule Applied: {conclusion.AppliedRule?.Name ?? "Unknown"}");
            builder.AppendLine($"∴ Conclusion: {ConvertToLogicalNotation(conclusion.Conclusion)}");
            builder.AppendLine();

            builder.AppendLine($"Validity: {conclusion.Type.ToString().ToUpper()}");
            builder.AppendLine($"Confidence: {conclusion.ConfidenceScore:F4}");

            return builder.ToString();
        }

        private async Task<string> FormatAsStructuredAsync(
            LogicalConclusion conclusion,
            ExportOptions options)
        {
            var structuredData = new;
            {
                Id = conclusion.Id,
                Timestamp = conclusion.Timestamp,
                Premise = conclusion.Premise,
                Conclusion = conclusion.Conclusion,
                Confidence = conclusion.ConfidenceScore,
                Type = conclusion.Type.ToString(),
                AppliedRule = conclusion.AppliedRule?.Name,
                SupportingEvidence = conclusion.SupportingEvidence,
                Metadata = conclusion.Metadata;
            };

            return System.Text.Json.JsonSerializer.Serialize(
                structuredData,
                new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = true;
                });
        }

        #endregion;

        #region Helper Classes;

        private class PremiseAnalysis;
        {
            public List<KnowledgeGraph.StatementInfo> Premises { get; } = new List<KnowledgeGraph.StatementInfo>();
            public List<KnowledgeGraph.StatementInfo> FactualPremises { get; } = new List<KnowledgeGraph.StatementInfo>();
            public List<KnowledgeGraph.StatementInfo> HypotheticalPremises { get; } = new List<KnowledgeGraph.StatementInfo>();
        }

        private class InferenceResult;
        {
            public string OriginalPremise { get; set; }
            public string Conclusion { get; set; }
            public double ConfidenceScore { get; set; }
            public LogicRule AppliedRule { get; set; }
            public List<string> SupportingEvidence { get; } = new List<string>();
            public string Method { get; set; }
        }

        private class ConclusionValidation;
        {
            public double? InternalConsistency { get; set; }
            public double? ExternalValidation { get; set; }
            public double? EvidenceSupport { get; set; }
            public double ConfidenceScore { get; set; }
            public double ContradictionScore { get; set; }
        }

        private class ContradictionAnalysis;
        {
            public List<Tuple<string, string>> DirectContradictions { get; } = new List<Tuple<string, string>>();
            public double ContradictionLevel { get; set; }
        }

        public class ProbabilisticConclusion;
        {
            public string MostProbableConclusion { get; set; }
            public double Probability { get; set; }
            public Dictionary<string, double> AllProbabilities { get; set; }
            public Tuple<double, double> ConfidenceInterval { get; set; }
            public double Entropy { get; set; }
        }

        public class WeightedPremise;
        {
            public string Premise { get; set; }
            public double Weight { get; set; }
            public double Reliability { get; set; }
        }

        public class ValidationResult;
        {
            public string ConclusionId { get; set; }
            public bool IsValid { get; set; }
            public double OverallScore { get; set; }
            public List<ValidationMetric> Metrics { get; set; }
            public DateTime ValidationTimestamp { get; set; }
        }

        public class ValidationMetric;
        {
            public string MetricName { get; set; }
            public double Score { get; set; }
            public string Description { get; set; }
        }

        public class ValidationCriteria;
        {
            public double MinimumValidationScore { get; set; } = 0.6;
            public bool RequireExternalValidation { get; set; } = true;
            public int MinimumEvidenceCount { get; set; } = 1;
        }

        public class ExportOptions;
        {
            public bool IncludeMetadata { get; set; } = true;
            public bool IncludeEvidence { get; set; } = true;
            public int MaxEvidenceItems { get; set; } = 5;
            public string LanguageCode { get; set; } = "en";
        }

        public enum ResolutionStrategy;
        {
            MostSupported,
            HighestConfidence,
            MajorityRule,
            ExpertPreference,
            ContextualRelevance;
        }

        #endregion;

        #region Exceptions;

        public class ConclusionBuildingException : Exception
        {
            public ConclusionBuildingException(string message) : base(message) { }
            public ConclusionBuildingException(string message, Exception inner) : base(message, inner) { }
        }

        public class ConclusionExportException : Exception
        {
            public ConclusionExportException(string message) : base(message) { }
            public ConclusionExportException(string message, Exception inner) : base(message, inner) { }
        }

        public class ValidationException : Exception
        {
            public ValidationException(string message) : base(message) { }
            public ValidationException(string message, Exception inner) : base(message, inner) { }
        }

        public class InferenceException : Exception
        {
            public InferenceException(string message) : base(message) { }
            public InferenceException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }

    /// <summary>
    /// Sonuç oluşturucu arayüzü;
    /// </summary>
    public interface IConclusionBuilder;
    {
        Task<ConclusionBuilder.LogicalConclusion> BuildConclusionAsync(
            IEnumerable<string> premises,
            ConclusionBuilder.ConclusionOptions options = null);

        Task<List<ConclusionBuilder.LogicalConclusion>> BuildChainedConclusionsAsync(
            string initialPremise,
            int depth,
            ConclusionBuilder.ConclusionOptions options = null);

        Task<ConclusionBuilder.LogicalConclusion> BuildFromContradictionsAsync(
            IEnumerable<string> contradictoryPremises,
            ConclusionBuilder.ResolutionStrategy strategy,
            ConclusionBuilder.ConclusionOptions options = null);

        Task<ConclusionBuilder.ProbabilisticConclusion> BuildProbabilisticConclusionAsync(
            IEnumerable<ConclusionBuilder.WeightedPremise> premises,
            ConclusionBuilder.ConclusionOptions options = null);

        Task<string> ExportConclusionAsync(
            ConclusionBuilder.LogicalConclusion conclusion,
            ConclusionBuilder.ConclusionFormat format,
            ConclusionBuilder.ExportOptions exportOptions = null);

        Task<ConclusionBuilder.ValidationResult> ValidateConclusionAsync(
            ConclusionBuilder.LogicalConclusion conclusion,
            ConclusionBuilder.ValidationCriteria criteria = null);
    }
}
