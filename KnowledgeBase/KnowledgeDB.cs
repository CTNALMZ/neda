using Neda.KnowledgeBase;
using Newtonsoft.Json;
using System.Xml;

namespace NEDA.Core.KnowledgeBase
{
    public class KnowledgeDB
    {
        private Dictionary<string, KnowledgeEntry> knowledgeStore;
        private string dbPath = "KnowledgeCache.json";

        public KnowledgeDB()
        {
            LoadKnowledge();
        }

        private void LoadKnowledge()
        {
            if (File.Exists(dbPath))
            {
                string json = File.ReadAllText(dbPath);
                knowledgeStore = JsonConvert.DeserializeObject<Dictionary<string, KnowledgeEntry>>(json);
            }
            else
            {
                knowledgeStore = new Dictionary<string, KnowledgeEntry>();
            }
        }

        public void SaveKnowledge()
        {
            string json = JsonConvert.SerializeObject(knowledgeStore, Formatting.Indented);
            File.WriteAllText(dbPath, json);
        }

        public void AddOrUpdateEntry(string key, KnowledgeEntry entry)
        {
            knowledgeStore[key] = entry;
            SaveKnowledge();
        }

        public KnowledgeEntry GetEntry(string key)
        {
            return knowledgeStore.ContainsKey(key) ? knowledgeStore[key] : null;
        }

        public List<KnowledgeEntry> Search(string keyword)
        {
            var results = new List<KnowledgeEntry>();
            foreach (var entry in knowledgeStore.Values)
            {
                if (entry.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    entry.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                }
            }
            return results;
        }

        public async Task RefreshFromOnlineSources(WebScraper scraper, RepositoryIntegrator repoIntegrator)
        {
            var onlineData = await scraper.FetchLatestKnowledgeAsync();
            var repoData = await repoIntegrator.FetchRepositoryKnowledgeAsync();

            foreach (var entry in onlineData) AddOrUpdateEntry(entry.Id, entry);
            foreach (var entry in repoData) AddOrUpdateEntry(entry.Id, entry);
        }
    }

    public class KnowledgeEntry
    {
        public string Id { get; set; }                // Unique ID
        public string Title { get; set; }             // Başlık
        public string Content { get; set; }           // Açıklama veya veri
        public string Category { get; set; }          // Örn: GameDesign, Programming, AI
        public DateTime LastUpdated { get; set; }     // Güncelleme zamanı
        public List<string> Tags { get; set; }        // Etiketler
        public double ConfidenceScore { get; set; }   // Bilgi güvenilirlik skoru
    }
}
