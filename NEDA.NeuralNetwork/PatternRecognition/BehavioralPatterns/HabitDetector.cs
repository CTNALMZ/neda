using NEDA.Brain.IntentRecognition.PriorityAssigner;
using NEDA.Interface.InteractionManager.PreferenceLearner;
using NEDA.NeuralNetwork.PatternRecognition.AnomalyDetection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
{
    /// <summary>
    /// Alışkanlık tespiti için temel arayüz;
    /// </summary>
    public interface IHabitDetector : IDisposable
    {
        // Properties;
        HabitDetectionConfiguration Configuration { get; }
        DetectionStatus Status { get; }
        long TotalActivitiesAnalyzed { get; }
        int DetectedHabits { get; }
        int LearnedProfiles { get; }
        DetectionMetrics LatestMetrics { get; }
        IReadOnlyDictionary<string, HabitModel> HabitModels { get; }

        // Events;
        event EventHandler<HabitDetectedEventArgs> HabitDetected;
        event EventHandler<HabitChangedEventArgs> HabitChanged;
        event EventHandler<RoutineFormedEventArgs> RoutineFormed;
        event EventHandler<AnomalousBehaviorDetectedEventArgs> AnomalousBehaviorDetected;
        event EventHandler<HabitPredictionMadeEventArgs> HabitPredictionMade;
        event EventHandler<ProfileUpdatedEventArgs> ProfileUpdated;

        // Methods;
        Task<LearningResult> LearnUserHabitsAsync(
            IEnumerable<UserActivity> userActivities,
            string userId,
            LearningContext context = null);

        IHabitStreamAnalyzer AnalyzeActivityStream(
            IObservable<UserActivity> activityStream,
            string userId);

        Task<HabitDetectionResult> DetectHabitFromActivityAsync(UserActivity activity);

        Task<HabitChangeAnalysisResult> DetectHabitChangesAsync(
            string userId,
            TimeRange timeRange);

        Task<HabitPredictionResult> PredictFutureActivitiesAsync(
            string userId,
            TimeSpan predictionHorizon,
            PredictionContext context = null);

        Task<AnomalyDetectionResult> DetectAnomalousBehaviorAsync(
            string userId,
            IEnumerable<UserActivity> recentActivities);

        Task<UserHabitProfile> GetUserHabitProfileAsync(string userId);

        Task<HabitStatistics> GetHabitStatisticsAsync(string userId = null, HabitType? habitType = null);

        Task<HabitRecommendations> GenerateHabitRecommendationsAsync(string userId, HabitGoal goal = null);

        Task<ModelTrainingResult> TrainHabitModelAsync(
            IEnumerable<TrainingData> trainingData,
            ModelType modelType = ModelType.NeuralNetwork);

        void UpdateConfiguration(HabitDetectionConfiguration configuration);
        void ClearData();
    }
}
