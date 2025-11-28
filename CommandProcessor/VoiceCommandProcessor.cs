using NEDA.EngineIntegration;
using NEDA.SecurityModules.ActivityLogs;
using NEDA.SecurityModules.FaceRecognition;
using NEDA.SecurityModules.VoiceRecognition;
using NEDA.VoiceSystem.ASR;
using NEDA.VoiceSystem.TTS;
using NEDA.WindowsIntegration;

namespace NEDA.Core.CommandProcessor
{
    public class VoiceCommandProcessor
    {
        private readonly SpeechRecognizer _asr;
        private readonly TextToSpeech _tts;
        private readonly FaceRecognizer _faceRecognizer;
        private readonly VoiceRecognizer _voiceRecognizer;
        private readonly UnrealEngineController _unrealEngine;
        private readonly VisualStudioController _visualStudio;
        private readonly SystemController _systemController;

        public VoiceCommandProcessor()
        {
            _tts = new TextToSpeech();
            _asr = new SpeechRecognizer();
            _faceRecognizer = new FaceRecognizer();
            _voiceRecognizer = new VoiceRecognizer();
            _unrealEngine = new UnrealEngineController();
            _visualStudio = new VisualStudioController();
            _systemController = new SystemController();

            _asr.OnCommandRecognized += HandleCommand;

            _tts.Speak("NEDA hazır. Tüm modüller aktif, sesli ve güvenlik sistemleri çalışıyor.");
        }

        private void HandleCommand(string command)
        {
            string cmd = command.ToLower();
            ActivityLogger.LogActivity($"Komut algılandı: {cmd}");

            // Kullanıcı doğrulaması
            string userName = "Bilinmeyen";
            if (!_voiceRecognizer.RecognizeVoice("test_voice", out userName))
                _tts.Speak("Bilinmeyen ses tespit edildi.");
            else
                _tts.Speak($"Hoş geldin, {userName}!");

            // Komut işleme
            switch (cmd)
            {
                case "merhaba":
                    _tts.Speak($"Merhaba {userName}, sana nasıl yardımcı olabilirim?");
                    break;

                case "nasılsın":
                    _tts.Speak("Harikayım! Sen nasılsın?");
                    break;

                case "oyun planla":
                case "oyun senaryosu oluştur":
                    _tts.Speak("Oyun planlaması başlatılıyor.");
                    _unrealEngine.PlanGame();
                    ActivityLogger.LogActivity("Oyun planlama modülü tetiklendi.");
                    break;

                case "Blueprint oluştur":
                    _tts.Speak("Blueprint oluşturuluyor.");
                    _unrealEngine.CreateBlueprint();
                    ActivityLogger.LogActivity("Blueprint oluşturuldu.");
                    break;

                case "C++ proje başlat":
                    _tts.Speak("Visual Studio'da proje başlatılıyor.");
                    _visualStudio.CreateCppProject();
                    ActivityLogger.LogActivity("C++ proje başlatıldı.");
                    break;

                case "dosya oluştur":
                case "dosya aç":
                    _tts.Speak("Dosya yönetimi başlatıldı.");
                    _systemController.HandleFileCommand(cmd);
                    break;

                case "hatırlatıcı ekle":
                case "hatırlatıcı sil":
                    _tts.Speak("Hatırlatıcı yönetimi başlatıldı.");
                    _systemController.HandleReminderCommand(cmd);
                    break;

                case "yabancı algıla":
                    if (!_faceRecognizer.RecognizeFace("unknown_face", out string detected))
                    {
                        _tts.Speak("Bilinmeyen kişi tespit edildi, rapor hazırlanıyor.");
                        ActivityLogger.LogSecurity("Yabancı tespit edildi.");
                        _systemController.CapturePhoto("unknown_face");
                    }
                    else
                    {
                        _tts.Speak($"Hoş geldin {detected}!");
                        ActivityLogger.LogActivity($"Tanımlanan kullanıcı: {detected}");
                    }
                    break;

                case "çıkış":
                    _tts.Speak("NEDA kapanıyor. Görüşürüz!");
                    Environment.Exit(0);
                    break;

                default:
                    _tts.Speak("Komutu anlayamadım, tekrar eder misin?");
                    ActivityLogger.LogActivity($"Tanınmayan komut: {cmd}");
                    break;
            }
        }
    }
}
