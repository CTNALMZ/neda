using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.NLP_Engine.EntityRecognition;
using NEDA.Interface.TextInput.NaturalLanguageInput;
using NEDA.NeuralNetwork.PatternRecognition.AnomalyDetection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.PatternRecognition.TextPatterns;
{
    /// <summary>
    /// Anlamsal analiz için temel arayüz;
    /// </summary>
    public interface ISemanticAnalyzer : IDisposable
    {
        // Properties;
        SemanticAnalysisConfiguration Configuration { get; }
        AnalysisStatus Status { get; }
        long TotalTextsAnalyzed { get; }
        long TotalEntitiesExtracted { get; }
        long TotalRelationsExtracted { get; }
        AnalysisMetrics LatestMetrics { get; }
        int LoadedOntologies { get; }
        IReadOnlyDictionary<string, SemanticModel> SemanticModels { get; }

        // Events;
        event EventHandler<SemanticAnalysisStartedEventArgs> AnalysisStarted;
        event EventHandler<EntityExtractedEventArgs> EntityExtracted;
        event EventHandler<RelationExtractedEventArgs> RelationExtracted;
        event EventHandler<SemanticRoleLabeledEventArgs> SemanticRoleLabeled;
        event EventHandler<SemanticAnalysisCompletedEventArgs> AnalysisCompleted;
        event EventHandler<InferenceMadeEventArgs> InferenceMade;

        // Methods;
        Task<OntologyLoadResult> LoadOntologyAsync(string ontologyPath, OntologyType ontologyType);
        Task<SemanticAnalysisResult> AnalyzeTextAsync(string text, AnalysisContext context = null);
        Task<BatchSemanticAnalysisResult> AnalyzeTextCollectionAsync(IEnumerable<string> texts, AnalysisContext context = null);
        Task<SemanticSimilarityResult> AnalyzeSemanticSimilarityAsync(string text1, string text2, AnalysisContext context = null);
        Task<InferenceResult> MakeInferencesFromTextAsync(string text, InferenceType inferenceType = InferenceType.Logical, AnalysisContext context = null);
        Task<ModelTrainingResult> TrainSemanticModelAsync(IEnumerable<SemanticTrainingData> trainingData, ModelType modelType = ModelType.NeuralNetwork);
        Task<SemanticSummary> ExtractSemanticSummaryAsync(string text, int maxLength = 500, AnalysisContext context = null);
        Task<SemanticSearchResult> PerformSemanticSearchAsync(string query, IEnumerable<Document> documents, AnalysisContext context = null);
        Task<SemanticVisualization> VisualizeSemanticStructureAsync(string text, VisualizationType visualizationType = VisualizationType.SemanticGraph);
        SemanticModelInfo GetModelInfo(string modelName = null);
        IReadOnlyList<SemanticPattern> GetSemanticPatterns(SemanticPatternType? patternType = null);
        void UpdateConfiguration(SemanticAnalysisConfiguration configuration);
        void ClearCache();
    }
}
