using System.Collections.Generic;
using NEDA.Core.Monitoring; // SystemHealth enum'unun burada olduğunu varsayıyoruz
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Tüm vision modülleri için sağlık raporu.
    /// </summary>
    public class ModuleHealthReport
    {
        /// <summary>
        /// Modül adına göre durum haritası.
        /// </summary>
        public IDictionary<string, ModuleStatus> ModuleStatus { get; }
            = new Dictionary<string, ModuleStatus>();

        /// <summary>
        /// Sağlıksız modül adları listesi.
        /// </summary>
        public IList<string> UnhealthyModules { get; }
            = new List<string>();

        /// <summary>
        /// Devre dışı bırakılmış modül adları listesi.
        /// </summary>
        public IList<string> DisabledModules { get; }
            = new List<string>();

        /// <summary>
        /// Genel sistem sağlığı.
        /// </summary>
        public SystemHealth OverallHealth { get; set; } = default;
    }
}
