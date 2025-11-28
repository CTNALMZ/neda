using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NEDA.WindowsIntegration;
using NEDA.SecurityModules.ActivityLogs;
using NEDA.VoiceSystem.TTS;
using Neda.Automation; // TextToSpeech için namespace

namespace NEDA.Core.TrainingModules
{
    /// <summary>
    /// Visual Studio (IDE) için eğitim modülü.
    /// Amaç: NEDA'ya VS çözümleri, C++/C# proje yapısı, template üretimi, derleme ve hata analizi öğretmek.
    /// </summary>
    public class VisualStudioTrainingModule
    {
        private readonly VisualStudioController _vs;
        private readonly TaskPlanner _planner;
        private readonly TextToSpeech? _tts; // nullable ve TextToSpeech tipi

        public VisualStudioTrainingModule(VisualStudioController vsController, TaskPlanner planner, TextToSpeech? tts = null)
        {
            _vs = vsController ?? throw new ArgumentNullException(nameof(vsController));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _tts = tts;
        }

        #region Public Tasks

        public void EnqueueCreateAndBuildSampleProject(string projectName, string language = "C++")
        {
            _planner.AddTask(new AutomationTask
            {
                TaskId = $"vs_train_createbuild_{projectName}",
                Description = $"VS - Create & Build Sample Project ({projectName})",
                Priority = TaskPriority.High,
                Execute = async () => await CreateAndBuildWorkflow(projectName, language)
            });

            ActivityLogger.LogActivity($"[VSTraining] Görev eklendi: CreateAndBuild -> {projectName}");
        }

        public void EnqueueGenerateUETemplates(string projectName)
        {
            _planner.AddTask(new AutomationTask
            {
                TaskId = $"vs_train_uetemplates_{projectName}",
                Description = $"VS - Generate UE C++ Templates ({projectName})",
                Priority = TaskPriority.Critical,
                Execute = async () => await GenerateUEProjectTemplates(projectName)
            });

            ActivityLogger.LogActivity($"[VSTraining] Görev eklendi: GenerateUETemplates -> {projectName}");
        }

        #endregion

        #region Workflows

        private async Task<bool> CreateAndBuildWorkflow(string projectName, string language)
        {
            try
            {
                ActivityLogger.LogActivity($"[VSTraining] Başlıyor: CreateAndBuild -> {projectName}");
                _tts?.Speak($"Visual Studio projesi oluşturuluyor: {projectName}");

                // 1) Create
                _vs.CreateProject(projectName, language);

                // 2) Add typical files
                _vs.AddClass(projectName, "GameController", language, "NEDA.Project");
                _vs.AddClass(projectName, "PlayerController", language, "NEDA.Project");
                _vs.AddEnum(projectName, "GameState", new List<string> { "Initializing", "Running", "Paused", "Finished" }, "C#");

                // 3) Build & run
                await _vs.BuildAndRunAsync(projectName);

                ActivityLogger.LogActivity($"[VSTraining] CreateAndBuild tamamlandı: {projectName}");
                _tts?.Speak($"Proje oluşturuldu ve çalıştırıldı: {projectName}");
                return true;
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"[VSTraining] Hata CreateAndBuild: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> GenerateUEProjectTemplates(string projectName)
        {
            try
            {
                ActivityLogger.LogActivity($"[VSTraining] Başlıyor: GenerateUEProjectTemplates -> {projectName}");
                _tts?.Speak($"Unreal Engine C++ template'ları hazırlanıyor: {projectName}");

                // 1) Create VS project if not exists
                _vs.CreateProject(projectName, "C++");

                // 2) Generate standard UE actor classes and headers (template)
                _vs.AddClass(projectName, "UEActor_Base", "C++", "NEDA.Unreal");
                _vs.AddClass(projectName, "UECharacter_Base", "C++", "NEDA.Unreal");
                _vs.AddClass(projectName, "UEAIController", "C++", "NEDA.Unreal");

                // 3) Build step
                await _vs.BuildAndRunAsync(projectName);

                ActivityLogger.LogActivity($"[VSTraining] GenerateUEProjectTemplates tamamlandı: {projectName}");
                _tts?.Speak($"UE template'ları oluşturuldu: {projectName}");
                return true;
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"[VSTraining] Hata GenerateUEProjectTemplates: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
