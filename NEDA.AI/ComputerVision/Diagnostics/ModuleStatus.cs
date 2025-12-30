using System;
#nullable disable
namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Tekil bir vision modülünün sağlık ve durum bilgisi.
    /// </summary>
    public class ModuleStatus
    {
        public string Name { get; set; }

        public bool IsEnabled { get; set; }

        public bool IsHealthy { get; set; }

        public DateTime LastActivity { get; set; }

        public ModuleStatus Clone()
        {
            return new ModuleStatus
            {
                Name = Name,
                IsEnabled = IsEnabled,
                IsHealthy = IsHealthy,
                LastActivity = LastActivity
            };
        }
    }
}
