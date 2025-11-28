using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NEDA.EngineIntegration;
using NEDA.EngineIntegration.AssetManager;
using NEDA.WindowsIntegration;
using NEDA.VoiceSystem.TTS;
using System.Windows.Documents;

namespace Neda.Automation
{
    public class AutomationEngine
    {
        private readonly TaskPlanner taskPlanner;
        private readonly AssetManager assetManager;
        private readonly RenderManager renderManager;
        private readonly BuildManager buildManager;
        private readonly VisualStudioController vsController;
        private readonly UnrealEngineController ueController;
        private readonly TextToSpeech? tts; // Nullable ve opsiyonel

        public AutomationEngine(
            TaskPlanner planner,
            AssetManager assetMgr,
            RenderManager renderMgr,
            BuildManager buildMgr,
            VisualStudioController vsCtrl,
            UnrealEngineController ueCtrl,
            TextToSpeech? ttsSystem = null) // Nullable ve default null
        {
            taskPlanner = planner;
            assetManager = assetMgr;
            renderManager = renderMgr;
            buildManager = buildMgr;
            vsController = vsCtrl;
            ueController = ueCtrl;
            tts = ttsSystem;

            taskPlanner.OnTaskStarted += TaskStartedHandler;
            taskPlanner.OnTaskCompleted += TaskCompletedHandler;
            taskPlanner.OnTaskFailed += TaskFailedHandler;
        }

        #region Event Handlers
        private void TaskStartedHandler(AutomationTask task)
        {
            Console.WriteLine($"Görev başladı: {task.TaskId}");
            tts?.Speak($"Görev {task.Description} başladı.");
        }

        private void TaskCompletedHandler(AutomationTask task)
        {
            Console.WriteLine($"Görev tamamlandı: {task.TaskId}");
            tts?.Speak($"Görev {task.Description} tamamlandı.");
        }

        private void TaskFailedHandler(AutomationTask task, Exception ex)
        {
            Console.WriteLine($"Görev başarısız: {task.TaskId}, Hata: {ex.Message}");
            tts?.Speak($"Dikkat! Görev {task.Description} başarısız oldu.");
        }
        #endregion

        #region Görev Planlama
        public void ScheduleTask(string taskId, string description, Func<Task<bool>> action,
            TaskPriority priority = TaskPriority.Medium, List<string>? dependencies = null)
        {
            var task = new AutomationTask
            {
                TaskId = taskId,
                Description = description,
                Execute = action,
                Priority = priority,
                Dependencies = dependencies ?? []
            };

            taskPlanner.AddTask(task);
        }
        #endregion

        #region Gelişmiş Oyun ve NPC Workflow
        public void ScheduleGameProjectWorkflow(string projectName, string projectType)
        {
            // 1️⃣ Asset Kontrolü ve İndirme
            ScheduleTask(
                $"asset_check_{projectName}",
                "Asset kontrolü ve indirme",
                async () => await assetManager.EnsureAssetsAsync(projectName),
                TaskPriority.Critical
            );
            // 3️⃣ AI tabanlı harita, çevre ve NPC yerleştirme
            ScheduleTask(
                $"map_npc_generate_{projectName}",
                "Harita, çevresel öğeler ve NPC yerleştirme",
                async () => await GenerateMapAndNPCsAsync(projectName),
                TaskPriority.High,
                [$"ue_project_create_{projectName}"]
            );

            // 4️⃣ Blueprint ve C++ üretimi (void -> Task<bool> wrapper)
            ScheduleTask(
                $"ue_blueprint_{projectName}",
                "Blueprint ve C++ üretimi",
                async () =>
                {
                    ueController.GenerateBlueprintsAndScripts(projectName); // void
                    return true; // Task<bool> olarak dönüştürüldü
                },
                TaskPriority.Medium,
                [$"map_npc_generate_{projectName}"]
            );

            // 5️⃣ Visual Studio proje yönetimi
            ScheduleTask(
                $"vs_project_{projectName}",
                "Visual Studio proje kurulumu",
                async () => await vsController.SetupProjectAsync(projectName, projectType),
                TaskPriority.Medium,
                [$"ue_project_create_{projectName}"]
            );

            // 6️⃣ Render ve Build
            ScheduleTask(
                $"render_build_{projectName}",
                "Render ve Build işlemleri",
                async () =>
                {
                    await renderManager.RenderSceneAsync(projectName);
                    await buildManager.BuildUnrealProjectAsync(projectName);
                    return true;
                },
                TaskPriority.High,
                [$"ue_blueprint_{projectName}", $"vs_project_{projectName}"]
            );
        }

        private async Task<bool> GenerateMapAndNPCsAsync(string projectName)
        {
            Console.WriteLine($"[AutomationEngine] Harita ve NPC üretimi başlıyor: {projectName}");

            var mapAssets = await assetManager.GetMapAssetsAsync(projectName);

            foreach (var asset in mapAssets)
            {
                if (!assetManager.AssetExists(asset))
                    await assetManager.DownloadAssetAsync(asset);

                var pos = AdvancedAIPlacement.DetermineOptimalPosition(asset, projectName, ueController);
                await ueController.PlaceAssetAsync(asset, pos);
            }

            await AdvancedAIPlacement.PlaceTerrainAsync(projectName, ueController);
            await AdvancedAIPlacement.PlaceVegetationAsync(projectName, ueController);
            await AdvancedAIPlacement.PlacePropsAsync(projectName, ueController);
            await AdvancedAIPlacement.PlaceWaterBodiesAsync(projectName, ueController);

            var npcList = new List<(string name, NPCType type)>
            {
                ("Enemy01", NPCType.Enemy),
                ("Ally01", NPCType.Ally),
                ("Animal01", NPCType.Animal),
                ("Neutral01", NPCType.Neutral)
            };

            foreach (var (name, type) in npcList)
            {
                var npcPos = ueController.GetRandomPosition(projectName);
                await ueController.PlaceNPCAsync(name, type, npcPos);
            }

            Console.WriteLine($"[AutomationEngine] Harita ve NPC üretimi tamamlandı: {projectName}");
            return true;
        }
        #endregion

        #region Gelişmiş AI Asset Yerleşimi
        public static class AdvancedAIPlacement
        {
            private static readonly Random random = new();

            public static (float x, float y, float z) DetermineOptimalPosition(string asset, string mapName, UnrealEngineController ueController)
            {
                int maxAttempts = 500;
                float minDistance = 3.0f;

                for (int i = 0; i < maxAttempts; i++)
                {
                    var candidate = ueController.GetRandomPosition(mapName);

                    if (!ueController.IsPositionOccupied(candidate, minDistance) &&
                        ueController.IsPositionValidForAsset(asset, candidate))
                    {
                        return candidate;
                    }
                }
                return ueController.GetSafeFallbackPosition(mapName);
            }

            public static async Task PlaceTerrainAsync(string mapName, UnrealEngineController ueController)
            {
                var terrainData = ueController.AnalyzeTerrain(mapName);
                await ueController.GenerateTerrainAsync(mapName, terrainData);
            }

            public static async Task PlaceVegetationAsync(string mapName, UnrealEngineController ueController)
            {
                var vegetationAssets = ueController.GetVegetationAssets(mapName);
                foreach (var veg in vegetationAssets)
                {
                    var pos = DetermineOptimalPosition(veg, mapName, ueController);
                    await ueController.PlaceAssetAsync(veg, pos);
                }
            }

            public static async Task PlacePropsAsync(string mapName, UnrealEngineController ueController)
            {
                var propsAssets = ueController.GetPropsAssets(mapName);
                foreach (var prop in propsAssets)
                {
                    var pos = DetermineOptimalPosition(prop, mapName, ueController);
                    await ueController.PlaceAssetAsync(prop, pos);
                }
            }

            public static async Task PlaceWaterBodiesAsync(string mapName, UnrealEngineController ueController)
            {
                var waterAssets = ueController.GetWaterAssets(mapName);
                foreach (var water in waterAssets)
                {
                    var pos = DetermineOptimalPosition(water, mapName, ueController);
                    await ueController.PlaceAssetAsync(water, pos);
                }
            }
        }
        #endregion

        #region Genel Yönetim
        public async Task RunAllTasksAsync() => await taskPlanner.ExecuteAllTasksAsync();
        public void CancelTask(string taskId) => taskPlanner.CancelTask(taskId);
        public void UpdateTaskPriority(string taskId, TaskPriority newPriority) => taskPlanner.UpdateTaskPriority(taskId, newPriority);
        public void ReportStatus() => taskPlanner.ReportStatus();
        #endregion
    }
}
