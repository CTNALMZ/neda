using NEDA.EngineIntegration;
using NEDA.EngineIntegration.AssetManager;
using NEDA.SecurityModules;
using NEDA.SecurityModules.ActivityLogs;
using NEDA.WindowsIntegration;
using System;
using System.Threading.Tasks;
using NEDA.VoiceSystem.TTS;
using Neda.Automation; // <-- TextToSpeech sınıfı için namespace ekledik

namespace NEDA.Core.TrainingModules
{
    /// <summary>
    /// Tüm eğitim modüllerini bir araya getirir ve NEDA'nın kendini eğitme döngüsünü yönetir.
    /// - Görev planlayıcıyı kullanır (TaskPlanner)
    /// - UnrealTrainingModule ve VisualStudioTrainingModule'u orkestre eder
    /// - Activity log ve Memory ile sonuçları kaydeder
    /// </summary>
    public class UnifiedTrainingEngine
    {
        public TaskPlanner Planner { get; }
        public UnrealTrainingModule UnrealTrainer { get; }
        public VisualStudioTrainingModule VSTrainer { get; }
        private readonly MemorySystem.MemoryManager _memory;
        private readonly TextToSpeech _tts; // <-- TTS sınıfını TextToSpeech ile değiştirdik

        public UnifiedTrainingEngine(
            UnrealEngineController ueController,
            AssetManager assetManager,
            RenderManager renderManager,
            BuildManager buildManager,
            VisualStudioController vsController,
            MemorySystem.MemoryManager memory,
            TextToSpeech tts = null) // <-- parametre tipi de TextToSpeech
        {
            Planner = new TaskPlanner();
            _memory = memory;
            _tts = tts;

            UnrealTrainer = new UnrealTrainingModule(ueController, assetManager, renderManager, buildManager, tts, Planner);
            VSTrainer = new VisualStudioTrainingModule(vsController, Planner, tts);

            // Basit başlangıç görevleri (örnek)
            BootstrapDefaultTasks();
        }

        private void BootstrapDefaultTasks()
        {
            // Örnek: Basit dünya oluşturmayı sıraya koy
            UnrealTrainer.EnqueueBasicWorldCreation("NEDA_SampleWorld", "Forest");
            // VS tarafı: UE template oluştur
            VSTrainer.EnqueueGenerateUETemplates("NEDA_SampleWorld_Cpp");
            ActivityLogger.LogActivity("[UnifiedTraining] Bootstrap varsayılan görevler eklendi.");
        }

        public async Task RunTrainingLoopAsync(int cycles = 1)
        {
            ActivityLogger.LogActivity("[UnifiedTraining] Eğitim döngüsü başlatıldı.");
            int cycle = 0;
            while (cycle++ < Math.Max(1, cycles))
            {
                _tts?.Speak($"Eğitim döngüsü {cycle} başlıyor."); // <-- SpeakAsync yerine Speak kullanılabilir

                // Çalıştırılmaya hazır tüm görevleri sırayla çalıştır
                await Planner.ExecuteAllTasksAsync();

                // Basit değerlendirme: tamamlanan görevleri memory'e yaz
                _memory?.RecordEvent($"TrainingCycleCompleted:{DateTime.UtcNow}");

                _tts?.Speak($"Eğitim döngüsü {cycle} tamamlandı.");
                ActivityLogger.LogActivity($"[UnifiedTraining] Döngü {cycle} tamamlandı.");

                // Küçük bekleme veya adaptasyon (kendi kendini öğrenme için kullanılabilir)
                await Task.Delay(200);
            }

            ActivityLogger.LogActivity("[UnifiedTraining] Eğitim döngüsü tamamlandı.");
        }
    }
}
