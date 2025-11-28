using Newtonsoft.Json;
using System.Xml;

namespace NEDA.SecurityModules.VoiceRecognition
{
    public class VoiceRecognizer
    {
        private Dictionary<string, string> _voiceDb;

        public VoiceRecognizer()
        {
            LoadVoiceDatabase();
        }

        private void LoadVoiceDatabase()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.SecurityModules\\VoiceRecognition\\VoiceDatabase.json");
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                _voiceDb = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            }
            else
            {
                _voiceDb = new Dictionary<string, string>();
            }
        }

        public bool RecognizeVoice(string voiceId, out string userName)
        {
            if (_voiceDb.ContainsKey(voiceId))
            {
                userName = _voiceDb[voiceId];
                return true;
            }
            else
            {
                userName = "Bilinmeyen";
                return false;
            }
        }

        public void AddVoice(string voiceId, string userName)
        {
            _voiceDb[voiceId] = userName;
            SaveDatabase();
        }

        private void SaveDatabase()
        {
            string json = JsonConvert.SerializeObject(_voiceDb, Formatting.Indented);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.SecurityModules\\VoiceRecognition\\VoiceDatabase.json"), json);
        }
    }
}
