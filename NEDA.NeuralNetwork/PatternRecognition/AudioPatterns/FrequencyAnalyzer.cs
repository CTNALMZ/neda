using NEDA.Communication.MultiModalCommunication.VoiceGestures;
using NEDA.NeuralNetwork.PatternRecognition.AnomalyDetection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.PatternRecognition.AudioPatterns;
{
    /// <summary>
    /// Frekans analizi için temel arayüz;
    /// </summary>
    public interface IFrequencyAnalyzer : IDisposable
    {
        // Properties;
        FrequencyAnalysisConfiguration Configuration { get; }
        AnalysisStatus Status { get; }
        int SampleRate { get; }
        int FftSize { get; }
        double FrequencyResolution { get; }
        double MaxFrequency { get; }
        AnalysisMetrics LatestMetrics { get; }
        IReadOnlyDictionary<string, SpectralModel> LearnedModels { get; }

        // Events;
        event EventHandler<FrequencyAnalysisStartedEventArgs> AnalysisStarted;
        event EventHandler<SpectralPeakDetectedEventArgs> SpectralPeakDetected;
        event EventHandler<FormantDetectedEventArgs> FormantDetected;
        event EventHandler<HarmonicStructureDetectedEventArgs> HarmonicStructureDetected;
        event EventHandler<FrequencyAnalysisCompletedEventArgs> AnalysisCompleted;
        event EventHandler<SpectrogramGeneratedEventArgs> SpectrogramGenerated;

        // Methods;
        void Configure(int sampleRate, int fftSize);
        Task<FrequencySpectrum> AnalyzeAudioAsync(byte[] audioData);
        IFrequencyStreamAnalyzer AnalyzeStream(IAudioStream audioStream);
        Task<MultiChannelAnalysisResult> AnalyzeMultiChannelAsync(MultiChannelAudioData multiChannelData);
        Task<SpectrogramResult> GenerateSpectrogramAsync(byte[] audioData, int windowSize = 1024, int hopSize = 512);
        Task<byte[]> ApplyFrequencyFilterAsync(byte[] audioData, FilterType filterType, params double[] cutoffFrequencies);
        Task<SpectralFeatures> ExtractSpectralFeaturesAsync(FrequencySpectrum frequencySpectrum);
        Task<ModelLearningResult> LearnFrequencyModelsAsync(IEnumerable<FrequencySpectrum> trainingData, ModelType modelType = ModelType.GaussianMixture);
        Task<FrequencyRecognitionResult> RecognizeFrequencyPatternAsync(FrequencySpectrum frequencySpectrum);
        Task<AnomalyDetectionResult> DetectFrequencyAnomaliesAsync(FrequencySpectrum frequencySpectrum);
        IReadOnlyDictionary<string, FrequencyModel> GetFrequencyModels(ModelType? modelType = null);
        void ResetMetrics();
        void ClearModels();
    }
}
