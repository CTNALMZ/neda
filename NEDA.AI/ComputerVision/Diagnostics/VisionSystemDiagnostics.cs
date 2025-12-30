using System;
using System.Collections.Generic;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// VisionEngine'in genel sistem teşhis bilgileri.
    /// </summary>
    public class VisionSystemDiagnostics
    {
        public VisionEngineStateType EngineState { get; set; }

        public TimeSpan Uptime { get; set; }

        public long TotalFramesProcessed { get; set; }

        public long MemoryUsageMB { get; set; }

        public double CPUUsage { get; set; }

        public double GPUUsage { get; set; }

        public ModuleHealthReport ModuleHealth { get; set; }

        /// <summary>
        /// Performans metriklerinin özet hali.
        /// Tipini basit tuttuk, gerekirse genişletilebilir.
        /// </summary>
        public VisionPerformanceMetrics PerformanceMetrics { get; set; }

        public int ActiveOperations { get; set; }

        public int FrameBufferSize { get; set; }

        public IList<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// VisionEngine performans metriklerinin özet modeli.
    /// </summary>
    public class VisionPerformanceMetrics
    {
        public double AverageProcessingTimeMs { get; set; }

        public double CacheHitRate { get; set; }

        public long FramesProcessed { get; set; }

        public long MemoryUsageMB { get; set; }
    }
}
