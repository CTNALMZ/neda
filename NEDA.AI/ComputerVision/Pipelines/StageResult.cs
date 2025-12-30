using System;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Bir pipeline aşamasının özet sonucu.
    /// </summary>
    public class StageResult
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        public TimeSpan ProcessingTime { get; set; }
    }
}
