using System;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// VisionEngine içinde yürütülen tekil bir işlemi temsil eder.
    /// </summary>
    public class VisionOperation : IDisposable
    {
        public string OperationId { get; set; }

        public string Type { get; set; }

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public TimeSpan Duration { get; set; }

        public VisionContext Context { get; set; }

        public void Dispose()
        {
            // Şimdilik özel bir kaynak yönetimi yok.
            // Gerekirse ileride operation kapsamına bağlı temizleme mantığı eklenebilir.
        }
    }
}
