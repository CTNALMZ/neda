namespace NEDA.Core.MemorySystem
{
    public class ShortTermMemory
    {
        private readonly Dictionary<string, string> _memory;

        public ShortTermMemory()
        {
            _memory = new Dictionary<string, string>();
        }

        public void Store(string key, string value)
        {
            _memory[key] = value;
            Console.WriteLine($"[ShortTermMemory] {key} kaydedildi: {value}");
        }

        public string Retrieve(string key)
        {
            return _memory.ContainsKey(key) ? _memory[key] : null;
        }

        public void Clear()
        {
            _memory.Clear();
            Console.WriteLine("[ShortTermMemory] Tüm kısa süreli hafıza temizlendi.");
        }

        public void PrintMemory()
        {
            if (_memory.Count == 0)
            {
                Console.WriteLine("Kısa süreli hafıza boş.");
                return;
            }

            foreach (var kv in _memory)
            {
                Console.WriteLine($"Key: {kv.Key}, Value: {kv.Value}");
            }
        }
    }
}
