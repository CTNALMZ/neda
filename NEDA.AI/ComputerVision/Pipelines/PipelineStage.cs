using NEDA.Core.NEDA.AI.ComputerVision.Pipelines;
using System;
using System.Threading.Tasks;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Pipeline içindeki tek bir işlem aşaması.
    /// </summary>
    public class PipelineStage
    {
        public string Name { get; }

        public bool Enabled { get; set; } = true;

        public bool IsCritical { get; set; }

        /// <summary>
        /// Aşamanın gerçek işini yapan fonksiyon.
        /// </summary>
        public Func<PipelineData, PipelineContext, Task<PipelineData>> ExecuteAsync { get; }

        public PipelineStage(
            string name,
            Func<PipelineData, PipelineContext, Task<PipelineData>> executeAsync,
            bool isCritical = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            IsCritical = isCritical;
        }
    }
}
