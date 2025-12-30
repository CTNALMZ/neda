using System;
using System.Collections.Generic;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Vision işlemleri için aşamalı işleme hattı (pipeline).
    /// </summary>
    public class VisionPipeline
    {
        public string Name { get; }

        public IList<PipelineStage> Stages { get; set; }
            = new List<PipelineStage>();

        public VisionPipeline(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
}
