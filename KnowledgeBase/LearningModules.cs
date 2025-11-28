using Neda.Automation;
using Newtonsoft.Json;
using System.Xml;

namespace NEDA.Core.KnowledgeBase
{
    public class LearningModules
    {
        private Dictionary<string, LearningContent> contentStore;
        private string contentPath = "LearningCache.json";

        public LearningModules()
        {
            LoadContent();
        }

        private void LoadContent()
        {
            if (File.Exists(contentPath))
            {
                string json = File.ReadAllText(contentPath);
                contentStore = JsonConvert.DeserializeObject<Dictionary<string, LearningContent>>(json);
            }
            else
            {
                contentStore = new Dictionary<string, LearningContent>();
            }
        }

        public void SaveContent()
        {
            string json = JsonConvert.SerializeObject(contentStore, Formatting.Indented);
            File.WriteAllText(contentPath, json);
        }

        public void AddOrUpdateContent(string key, LearningContent content)
        {
            contentStore[key] = content;
            SaveContent();
        }

        public LearningContent GetContent(string key)
        {
            return contentStore.ContainsKey(key) ? contentStore[key] : null;
        }

        public List<LearningContent> Search(string keyword)
        {
            var results = new List<LearningContent>();
            foreach (var content in contentStore.Values)
            {
                if (content.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    content.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(content);
                }
            }
            return results;
        }

        public async Task RefreshContentFromOnline(WebScraper scraper)
        {
            var onlineData = await scraper.FetchLearningModulesAsync();

            foreach (var module in onlineData)
            {
                AddOrUpdateContent(module.Id, module);
            }
        }

        public void ApplyLearningToTask(TaskPlanner taskPlanner)
        {
            // Tüm eğitimli içerikler, görev planlayıcıya öneri olarak uygulanır
            foreach (var content in contentStore.Values)
            {
                taskPlanner.IntegrateLearningModule(content);
            }
        }
    }

    public class LearningContent
    {
        public string Id { get; set; }                  // Benzersiz ID
        public string Title { get; set; }               // Başlık
        public string Description { get; set; }         // Açıklama veya içerik
        public string Category { get; set; }            // Örn: GameDesign, AI, Programming
        public DateTime LastUpdated { get; set; }       // Son güncelleme tarihi
        public List<string> Tags { get; set; }          // Etiketler
        public double ConfidenceScore { get; set; }     // Güvenilirlik skoru
        public List<string> Dependencies { get; set; }  // Kullanması gereken diğer içerik/başlıklar
        public bool IsApplied { get; set; }             // Görev planına uygulanıp uygulanmadığı
    }
}
