using Newtonsoft.Json;
using System.Speech.Recognition;

namespace NEDA.VoiceSystem.ASR
{
    public class SpeechRecognizer
    {
        private SpeechRecognitionEngine _recognizer;
        private Choices _choices;

        public event Action<string> OnCommandRecognized;

        public SpeechRecognizer()
        {
            _recognizer = new SpeechRecognitionEngine();
            _recognizer.SetInputToDefaultAudioDevice();
            LoadCommands();

            _recognizer.SpeechRecognized += Recognizer_SpeechRecognized;
            _recognizer.RecognizeAsync(RecognizeMode.Multiple);
        }

        private void LoadCommands()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.VoiceSystem\\ASR\\SpeechCommands.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[ASR] Komut dosyası bulunamadı, varsayılan komutlar yüklendi.");
                _choices = new Choices(new string[] { "merhaba", "oyun başlat", "hatırlatıcı ekle", "bilgi ver", "çıkış" });
            }
            else
            {
                string json = File.ReadAllText(path);
                var data = JsonConvert.DeserializeObject<SpeechCommandList>(json);
                _choices = new Choices(data.Commands);
            }

            var gb = new GrammarBuilder();
            gb.Append(_choices);
            var grammar = new Grammar(gb);

            _recognizer.LoadGrammar(grammar);
        }

        private void Recognizer_SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            string command = e.Result.Text.ToLower();
            Console.WriteLine($"[ASR] Komut tanındı: {command}");
            OnCommandRecognized?.Invoke(command);
        }
    }

    public class SpeechCommandList
    {
        public string[] Commands { get; set; }
    }
}
