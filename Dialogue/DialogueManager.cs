using Newtonsoft.Json;

namespace NEDA.Core.Dialogue
{
    public class DialogueManager
    {
        private readonly Dictionary<string, string> _personalityResponses;

        public DialogueManager()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.Core\\Dialogue\\PersonalityProfile.json");

            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                _personalityResponses = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            }
            else
            {
                Console.WriteLine("[DialogueManager] PersonalityProfile.json bulunamadı! Varsayılan yanıtlar yüklendi.");
                _personalityResponses = new Dictionary<string, string>()
                {
                    {"greeting", "Merhaba! Ben NEDA, nasıl yardımcı olabilirim?"},
                    {"unknown", "Üzgünüm, bunu anlayamadım. Biraz daha detay verebilir misin?"}
                };
            }
        }

        public string GenerateResponse(string intent)
        {
            if (_personalityResponses.ContainsKey(intent.ToLower()))
            {
                return _personalityResponses[intent.ToLower()];
            }
            else
            {
                return _personalityResponses["unknown"];
            }
        }
    }
}
