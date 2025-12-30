using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.DeepLearning.TrainingPipelines;
{
    /// <summary>
    /// Epoch yönetimi için temel arayüz;
    /// </summary>
    public interface IEpochManager : IDisposable
    {
        // Properties;
        int CurrentEpoch { get; }
        int TotalEpochs { get; }
        EpochManager.TrainingStatus Status { get; }
        double AverageEpochDuration { get; }
        ModelMetrics BestModelMetrics { get; }

        // Events;
        event EventHandler<EpochStartedEventArgs> EpochStarted;
        event EventHandler<EpochCompletedEventArgs> EpochCompleted;
        event EventHandler<CheckpointSavedEventArgs> CheckpointSaved;
        event EventHandler<TrainingProgressEventArgs> ProgressUpdated;
        event EventHandler<EarlyStoppingTriggeredEventArgs> EarlyStoppingTriggered;

        // Methods;
        Task<EpochManager.TrainingResult> StartTrainingAsync(
            int totalEpochs,
            Func<int, Task<ModelMetrics>> trainingAction);

        void PauseTraining();
        void ResumeTraining();
        void CancelTraining();

        EpochManager.TrainingEpoch GetEpochMetrics(int epochNumber);
        IReadOnlyList<EpochManager.TrainingEpoch> GetEpochHistory();
        EpochManager.TrainingEpoch GetBestEpoch();
        EpochManager.TrainingStatistics GetStatistics();
        void ClearHistory();
    }
}
