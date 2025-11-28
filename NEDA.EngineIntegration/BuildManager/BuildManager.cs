using Neda.Automation;
using NEDA.SecurityModules.ActivityLogs;

namespace NEDA.EngineIntegration
{
    public class BuildManager
    {
        private readonly TaskPlanner taskPlanner;
        private readonly SecurityManifest securityManifest;

        private Dictionary<string, BuildSettings> projectBuildSettings;

        public BuildManager(TaskPlanner planner, SecurityManifest manifest)
        {
            taskPlanner = planner;
            securityManifest = manifest;
            projectBuildSettings = new Dictionary<string, BuildSettings>();
        }

        // Build ayarlarını yapılandır
        public void ConfigureBuild(string projectName, bool cleanBuild = false, string platform = "Win64", string configuration = "Development", int maxConcurrentTasks = 4)
        {
            projectBuildSettings[projectName] = new BuildSettings
            {
                CleanBuild = cleanBuild,
                Platform = platform,
                Configuration = configuration,
                MaxConcurrentTasks = maxConcurrentTasks
            };
        }

        // Unreal Engine projesini derle ve çalıştır (AI tabanlı kaynak optimizasyonlu)
        public async Task<bool> BuildUnrealProjectAsync(string projectName)
        {
            if (!projectBuildSettings.ContainsKey(projectName))
            {
                Console.WriteLine($"[BuildManager] Uyarı: {projectName} için build ayarı bulunamadı. Varsayılan değerler kullanılacak.");
                ConfigureBuild(projectName);
            }

            var settings = projectBuildSettings[projectName];

            try
            {
                Console.WriteLine($"[BuildManager] {projectName} Unreal Engine projesi derleniyor. Platform: {settings.Platform}, Konfigürasyon: {settings.Configuration}, CleanBuild: {settings.CleanBuild}");

                // Donanım ve kaynak optimizasyonu
                if (!CheckHardwareResources(settings))
                {
                    Console.WriteLine("[BuildManager] Donanım kaynakları yetersiz, AI tabanlı optimizasyon başlatılıyor...");
                    await AIResourceOptimizer.OptimizeBuildAsync(settings);
                }

                // Unreal Engine build işlemi (API ile entegre)
                bool buildSuccess = await UnrealEngineAPI.BuildProjectAsync(projectName, settings);

                LogBuild(projectName, settings, buildSuccess, buildSuccess ? null : new Exception("Build hatası"));

                Console.WriteLine(buildSuccess
                    ? $"[BuildManager] {projectName} Unreal Engine build tamamlandı."
                    : $"[BuildManager] {projectName} Unreal Engine build başarısız oldu.");

                return buildSuccess;
            }
            catch (Exception ex)
            {
                LogBuild(projectName, settings, false, ex);
                Console.WriteLine($"[BuildManager] Hata: {ex.Message}");
                return false;
            }
        }

        // Visual Studio projesini derle ve çalıştır
        public async Task<bool> BuildVSProjectAsync(string projectPath)
        {
            try
            {
                Console.WriteLine($"[BuildManager] Visual Studio projesi derleniyor: {projectPath}");

                if (!CheckHardwareResources())
                {
                    Console.WriteLine("[BuildManager] Donanım kaynakları yetersiz, build bekletiliyor...");
                    await Task.Delay(2000);
                }

                bool buildSuccess = await VSAPI.BuildProjectAsync(projectPath);

                LogBuild(projectPath, null, buildSuccess, buildSuccess ? null : new Exception("VS Build hatası"));

                Console.WriteLine(buildSuccess
                    ? "[BuildManager] VS Build tamamlandı."
                    : "[BuildManager] VS Build başarısız oldu.");

                return buildSuccess;
            }
            catch (Exception ex)
            {
                LogBuild(projectPath, null, false, ex);
                return false;
            }
        }

        // Donanım ve kaynak kontrolü
        private bool CheckHardwareResources(BuildSettings settings = null)
        {
            if (HardwareMonitor.RAMAvailable < 4096) return false;
            if (HardwareMonitor.CPULoad > 90) return false;
            return true;
        }

        // Build loglama
        private void LogBuild(string projectName, BuildSettings settings, bool success, Exception ex)
        {
            var log = new BuildLog
            {
                ProjectName = projectName,
                Platform = settings?.Platform,
                Configuration = settings?.Configuration,
                CleanBuild = settings?.CleanBuild ?? false,
                MaxConcurrentTasks = settings?.MaxConcurrentTasks ?? 1,
                Success = success,
                ErrorMessage = ex?.Message,
                Timestamp = DateTime.Now
            };

            ActivityLogger.LogBuild(log);
        }
    }

    public class BuildSettings
    {
        public bool CleanBuild { get; set; }
        public string Platform { get; set; }
        public string Configuration { get; set; }
        public int MaxConcurrentTasks { get; set; } = 1; // AI optimize edilmiş eşzamanlı build sayısı
    }

    public class BuildLog
    {
        public string ProjectName { get; set; }
        public bool CleanBuild { get; set; }
        public string Platform { get; set; }
        public string Configuration { get; set; }
        public int MaxConcurrentTasks { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public static class HardwareMonitor
    {
        public static int GPUMemoryAvailable => 4096; // MB
        public static int CPULoad => 15; // %
        public static int RAMAvailable => 8192; // MB
    }

    public static class UnrealEngineAPI
    {
        public static async Task<bool> BuildProjectAsync(string projectName, BuildSettings settings)
        {
            // Gerçek UE API çağrıları burada olacak
            await Task.Delay(1000); // Simülasyon
            return true;
        }
    }

    public static class VSAPI
    {
        public static async Task<bool> BuildProjectAsync(string projectPath)
        {
            await Task.Delay(500); // Simülasyon
            return true;
        }
    }

    // AI tabanlı kaynak optimizasyon modülü
    public static class AIResourceOptimizer
    {
        public static async Task OptimizeBuildAsync(BuildSettings settings)
        {
            Console.WriteLine($"[AIResourceOptimizer] Build kaynakları optimize ediliyor. Max eşzamanlı görev: {settings.MaxConcurrentTasks}");
            await Task.Delay(500); // Simülasyon
            Console.WriteLine("[AIResourceOptimizer] Optimizasyon tamamlandı.");
        }
    }
}
