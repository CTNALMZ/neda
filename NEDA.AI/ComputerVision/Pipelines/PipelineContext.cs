using System;
using System.Collections.Generic;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Pipeline çalışması sırasında kullanılan bağlam.
    /// </summary>
    public class PipelineContext
    {
        public string PipelineName { get; set; }

        public DateTime StartTime { get; set; }

        public string FrameId { get; set; }

        public string CurrentStage { get; set; }

        public bool IsCancelled { get; set; }

        public IDictionary<string, StageResult> StageResults { get; }
            = new Dictionary<string, StageResult>();
    }
}
