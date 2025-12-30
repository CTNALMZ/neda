using NEDA.Core.NEDA.AI.ComputerVision.Pipelines;
using System;
using System.Collections.Generic;
using System.Drawing;
#nullable disable

namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Tek bir vision işleme sonucunu temsil eder.
    /// Pipeline çıktılarını ve istatistiklerini taşır.
    /// </summary>
    public class VisionResult
    {
        /// <summary>
        /// İşlem başarıyla tamamlandı mı?
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Hata varsa açıklaması.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// Pipeline boyunca taşınan zengin veri nesnesi.
        /// </summary>
        public PipelineData Data { get; set; }

        /// <summary>
        /// Hangi pipeline ile işlendi.
        /// </summary>
        public string PipelineName { get; set; }

        /// <summary>
        /// Toplam işlem süresi.
        /// </summary>
        public TimeSpan ProcessingTime { get; set; }

        /// <summary>
        /// Her stage için detaylı sonuçlar.
        /// </summary>
        public IDictionary<string, StageResult> StageResults { get; set; }
            = new Dictionary<string, StageResult>();

        /// <summary>
        /// Bu sonuç normal işleme yerine fallback üzerinden mi üretildi?
        /// </summary>
        public bool IsFallbackResult { get; set; }
    }

    /// <summary>
    /// Toplu (batch) vision işleme sonuçlarını temsil eder.
    /// </summary>
    public class BatchVisionResult
    {
        public int TotalTasks { get; set; }

        public int SuccessfulProcesses { get; set; }

        public int FailedProcesses { get; set; }

        public IList<VisionResult> Results { get; } = new List<VisionResult>();

        public IList<VisionError> Errors { get; } = new List<VisionError>();

        public TimeSpan ProcessingTime { get; set; }
    }

    /// <summary>
    /// Toplu işleme sırasında belirli bir göreve ait hata.
    /// </summary>
    public class VisionError
    {
        public string TaskId { get; set; }

        public string Message { get; set; }

        public VisionError()
        {
        }

        public VisionError(string taskId, string message)
        {
            TaskId = taskId;
            Message = message;
        }
    }

    /// <summary>
    /// Batch işleme için yapılandırma seçenekleri.
    /// </summary>
    public class BatchProcessingOptions
    {
        /// <summary>
        /// Aynı anda çalışacak maksimum paralel görev sayısı.
        /// null ise engine konfigürasyonundaki değer kullanılır.
        /// </summary>
        public int? MaxParallelism { get; set; }

        /// <summary>
        /// Her görev arasında isteğe bağlı bekleme uygulanacak mı?
        /// </summary>
        public bool ThrottleProcessing { get; set; }

        /// <summary>
        /// Throttle aktifse milisaniye cinsinden gecikme.
        /// </summary>
        public int ThrottleDelayMs { get; set; } = 10;

        /// <summary>
        /// Her görev tamamlandığında ilerleme bildirimi için callback.
        /// </summary>
        public Action<int, int, VisionResult> ProgressCallback { get; set; }

        public static BatchProcessingOptions Default =>
            new BatchProcessingOptions
            {
                MaxParallelism = null,
                ThrottleProcessing = false,
                ThrottleDelayMs = 10
            };
    }

    /// <summary>
    /// Tekil bir vision görevi (batch işleme için).
    /// </summary>
    public class VisionTask
    {
        public string TaskId { get; set; }

        public Bitmap Image { get; set; }

        public VisionContext Context { get; set; }
    }

    /// <summary>
    /// VisionEngine konfigürasyonunda kullanılan kalite ön ayarı.
    /// </summary>
    public enum VisionQualityPreset
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

    /// <summary>
    /// Vision engine çalışmasının yüksek seviye durumları.
    /// </summary>
    public enum VisionEngineStateType
    {
        Unknown = 0,
        Initializing = 1,
        Ready = 2,
        Processing = 3,
        ShuttingDown = 4,
        Error = 5
    }

    #region Event Args

    public class VisionProcessingEventArgs : EventArgs
    {
        public VisionResult Result { get; set; }

        public VisionContext Context { get; set; }

        public DateTime Timestamp { get; set; }
    }

    public class VisionPerformanceEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }

        public long FramesProcessed { get; set; }

        public double AverageProcessingTime { get; set; }

        public long MemoryUsageMB { get; set; }

        public int ActiveModules { get; set; }

        public double CacheHitRate { get; set; }
    }

    public class VisionStateChangedEventArgs : EventArgs
    {
        public VisionEngineStateType OldState { get; set; }

        public VisionEngineStateType NewState { get; set; }

        public DateTime Timestamp { get; set; }
    }

    public class VisionAnalyticsEventArgs : EventArgs
    {
        public VisionResult Result { get; set; }

        public DateTime Timestamp { get; set; }

        public IDictionary<string, object> AnalyticsData { get; set; }
            = new Dictionary<string, object>();
    }

    #endregion
}
