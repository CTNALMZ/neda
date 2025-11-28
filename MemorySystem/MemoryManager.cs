
namespace NEDA.Core.MemorySystem
{
    public class MemoryManager
    {
        private readonly ShortTermMemory _shortTerm;
        private readonly LongTermMemory _longTerm;

        public MemoryManager()
        {
            _shortTerm = new ShortTermMemory();
            _longTerm = new LongTermMemory();
        }

        /// <summary>
        /// Bilgi kaydetme fonksiyonu
        /// </summary>
        /// <param name="key">Bilgi anahtarı</param>
        /// <param name="value">Bilgi değeri</param>
        /// <param name="longTerm">Uzun süreli hafıza için true</param>
        public void Remember(string key, string value, bool longTerm = false)
        {
            if (longTerm)
            {
                _longTerm.Store(key, value);
            }
            else
            {
                _shortTerm.Store(key, value);
            }
        }

        /// <summary>
        /// Bilgi hatırlama fonksiyonu
        /// </summary>
        /// <param name="key">Bilgi anahtarı</param>
        /// <returns>Bilgi değeri</returns>
        public string Recall(string key)
        {
            string value = _shortTerm.Retrieve(key);
            if (value != null) return value;

            value = _longTerm.Retrieve(key);
            return value ?? "[MemoryManager] Bilgi bulunamadı.";
        }

        /// <summary>
        /// Hafıza temizleme
        /// </summary>
        public void ClearMemory(bool shortTerm = true, bool longTerm = false)
        {
            if (shortTerm) _shortTerm.Clear();
            if (longTerm) _longTerm.Clear();
        }

        /// <summary>
        /// Kısa süreli hafıza içeriğini görüntüle
        /// </summary>
        public void PrintShortTermMemory()
        {
            Console.WriteLine("[MemoryManager] Kısa süreli hafıza:");
            _shortTerm.PrintMemory();
        }

        /// <summary>
        /// Uzun süreli hafıza içeriğini görüntüle
        /// </summary>
        public void PrintLongTermMemory()
        {
            Console.WriteLine("[MemoryManager] Uzun süreli hafıza:");
            _longTerm.PrintMemory();
        }

        internal void RecordEvent(string v)
        {
            throw new NotImplementedException();
        }
    }
}
