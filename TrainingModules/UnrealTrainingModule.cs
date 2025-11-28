using NEDA.EngineIntegration;
using NEDA.EngineIntegration.AssetManager;
using NEDA.SecurityModules.ActivityLogs;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static Neda.Automation.AutomationEngine;
using NEDA.VoiceSystem.TTS;
using Neda.Automation; // <-- TextToSpeech sınıfı için namespace ekledik

namespace NEDA.Core.TrainingModules
{
    /// <summary>
    /// Unreal Engine öğrenme ve öğretme modülü.
    /// Amaç: NEDA'ya Unreal Editor, Blueprint, asset pipeline, scene/level oluşturma, cutscene ve sinematik yönetimini
    /// "eğitim görevleri" şeklinde öğretebilecek bir API sağlamak.
    /// </summary>
    public class UnrealTrainingModule
    {
        private readonly UnrealEngineController _ue;
        private readonly AssetManager _assetManager;
        private readonly RenderManager _renderManager;
        private readonly BuildManager _buildManager;
        private readonly TextToSpeech _tts; // <-- VoiceSystem.TTS yerine TextToSpeech
        private readonly TaskPlanner _planner;

        public UnrealTrainingModule(
            UnrealEngineController ueController,
            AssetManager assetManager,
            RenderManager renderManager,
            BuildManager buildManager,
            TextToSpeech tts, // <-- parametre tipi TextToSpeech
            TaskPlanner planner)
        {
            _ue = ueController ?? throw new ArgumentNullException(nameof(ueController));
            _assetManager = assetManager;
            _renderManager = renderManager;
            _buildManager = buildManager;
            _tts = tts;
            _planner = planner;
        }

        #region Public Training Scenarios

        public void EnqueueBasicWorldCreation(string projectName, string style = "Forest")
        {
            _planner.AddTask(new AutomationTask
            {
                TaskId = $"ue_train_create_{projectName}",
                Description = $"Unreal - BasicWorldCreation ({projectName})",
                Priority = TaskPriority.High,
                Execute = async () => await BasicWorldCreationWorkflow(projectName, style)
            });
            ActivityLogger.LogActivity($"[UnrealTraining] Görev eklendi: BasicWorldCreation -> {projectName}");
        }

        public void EnqueueAdvancedGamePlayScenario(string projectName, int enemyCount = 5)
        {
            _planner.AddTask(new AutomationTask
            {
                TaskId = $"ue_train_adv_{projectName}",
                Description = $"Unreal - AdvancedGameplayScenario ({projectName})",
                Priority = TaskPriority.Critical,
                Execute = async () => await AdvancedGameplayWorkflow(projectName, enemyCount)
            });
            ActivityLogger.LogActivity($"[UnrealTraining] Görev eklendi: AdvancedGameplayScenario -> {projectName}");
        }

        #endregion

        #region Workflows (implementasyon)

        private async Task<bool> BasicWorldCreationWorkflow(string projectName, string style)
        {
            try
            {
                ActivityLogger.LogActivity($"[UnrealTraining] Başlıyor: BasicWorldCreation -> {projectName} [{style}]");
                _tts?.Speak($"Başlıyorum. {projectName} için temel dünya oluşturuluyor.");

                var required = await _assetManager.GetMapAssetsAsync(projectName);
                foreach (var a in required)
                {
                    if (!_assetManager.AssetExists(a))
                    {
                        ActivityLogger.LogActivity($"[UnrealTraining] Eksik asset tespit edildi: {a}. İndiriliyor...");
                        var ok = await _assetManager.DownloadAssetAsync(a);
                        if (!ok)
                        {
                            ActivityLogger.LogActivity($"[UnrealTraining] Asset indirilemedi: {a}");
                            return false;
                        }
                    }
                }

                bool created = await _ue.CreateProjectAsync(projectName, style);
                if (!created)
                {
                    ActivityLogger.LogActivity($"[UnrealTraining] Proje oluşturulamadı: {projectName}");
                    return false;
                }

                var terrainData = _ue.AnalyzeTerrain(projectName);
                await _ue.GenerateTerrainAsync(projectName, terrainData);

                var mapAssets = await _assetManager.GetMapAssetsAsync(projectName);
                foreach (var asset in mapAssets)
                {
                    var pos = AdvancedAIPlacement.DetermineOptimalPosition(asset, projectName, _ue);
                    await _ue.PlaceAssetAsync(asset, pos);
                }

                await _renderManager.RenderSceneAsync(projectName);

                ActivityLogger.LogActivity($"[UnrealTraining] BasicWorldCreation tamamlandı: {projectName}");
                _tts?.Speak($"{projectName} için temel dünya oluşturuldu. Önizleme tamamlandı.");
                return true;
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"[UnrealTraining] Hata BasicWorldCreation: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> AdvancedGameplayWorkflow(string projectName, int enemyCount)
        {
            try
            {
                ActivityLogger.LogActivity($"[UnrealTraining] Başlıyor: AdvancedGameplay -> {projectName}");
                _tts?.Speak($"Gelişmiş senaryo başlatılıyor. Düşman sayısı: {enemyCount}");

                _ue.GenerateBlueprintsAndScripts(projectName);

                var assets = await _assetManager.GetMapAssetsAsync(projectName);
                foreach (var asset in assets)
                {
                    var pos = AdvancedAIPlacement.DetermineOptimalPosition(asset, projectName, _ue);
                    await _ue.PlaceAssetAsync(asset, pos);
                }

                for (int i = 0; i < enemyCount; i++)
                {
                    var pos = _ue.GetRandomPosition(projectName);
                    await _ue.PlaceNPCAsync($"Enemy_{i:00}", NPCType.Enemy, pos);
                }

                await _ue.SetupCutsceneAsync("EncounterIntro", new List<string> { "HeroCharacter", "Enemy_00" });
                await _ue.TriggerCutsceneAsync("EncounterIntro", new List<string> { "HeroCharacter", "Enemy_00" });

                await _renderManager.RenderSceneAsync(projectName);
                await _buildManager.BuildUnrealProjectAsync(projectName);

                ActivityLogger.LogActivity($"[UnrealTraining] AdvancedGameplay tamamlandı: {projectName}");
                _tts?.Speak($"Gelişmiş senaryo tamamlandı: {projectName}");
                return true;
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"[UnrealTraining] Hata AdvancedGameplay: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
