using Newtonsoft.Json;
using System.Xml;

namespace NEDA.Core.MemorySystem
{
    public class LongTermMemory
    {
        private readonly Dictionary<string, string> _memory;
        private readonly string _filePath;

        public LongTermMemory()
        {
            _memory = new Dictionary<string, string>();
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.Core\\MemorySystem\\LongTermMemory.json");
            LoadMemory();
        }

        public void Store(string key, string value)
        {
            _memory[key] = value;
            SaveMemory();
            Console.WriteLine($"[LongTermMemory] {key} kaydedildi: {value}");
        }

        public string Retrieve(string key)
        {
            return _memory.ContainsKey(key) ? _memory[key] : null;
        }

        public void Clear()
        {
            _memory.Clear();
            SaveMemory();
            Console.WriteLine("[LongTermMemory] Tüm uzun süreli hafıza temizlendi.");
        }

        private void LoadMemory()
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (data != null)
                {
                    foreach (var kv in data)
                        _memory[kv.Key] = kv.Value;
                }
            }
        }

        private void SaveMemory()
        {
            string json = JsonConvert.SerializeObject(_memory, Formatting.Indented);
            File.WriteAllText(_filePath, json);
        }

        public void PrintMemory()
        {
            if (_memory.Count == 0)
            {
                Console.WriteLine("Uzun süreli hafıza boş.");
                return;
            }

            foreach (var kv in _memory)
            {
                Console.WriteLine($"Key: {kv.Key}, Value: {kv.Value}");
            }
        }
    }
}
