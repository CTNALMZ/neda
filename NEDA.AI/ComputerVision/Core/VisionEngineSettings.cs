#nullable disable

namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// AppSettings üzerinden yüklenen VisionEngine konfigürasyon modeli.
    /// VisionEngineConfig ile bire bir aynı alanlara sahip.
    /// </summary>
    public class VisionEngineSettings
    {
        public int MaxConcurrentOperations { get; set; } = 4;
        public bool EnableHardwareAcceleration { get; set; } = true;
        public long MemoryUsageLimitMB { get; set; } = 2048;
        public int ProcessingTimeoutMs { get; set; } = 30000;
        public bool EnableCaching { get; set; } = true;
        public int CacheSizeMB { get; set; } = 512;
        public string DefaultPipeline { get; set; } = "Default";
        public VisionQualityPreset QualityPreset { get; set; } = VisionQualityPreset.High;
        public bool EnableAnalytics { get; set; } = true;
        public int FrameBufferSize { get; set; } = 100;
    }
}
