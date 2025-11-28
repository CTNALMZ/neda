using HtmlAgilityPack;

namespace NEDA.Core.KnowledgeBase
{
    public class WebScraper
    {
        private HttpClient httpClient;
        private int requestTimeout = 15000; // 15 saniye

        public WebScraper()
        {
            httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMilliseconds(requestTimeout);
        }

        // URL listesi üzerinden asenkron veri çekme
        public async Task<List<LearningContent>> FetchLearningModulesAsync(List<string> urls = null)
        {
            var result = new List<LearningContent>();

            if (urls == null || urls.Count == 0)
            {
                urls = GetDefaultLearningUrls();
            }

            foreach (var url in urls)
            {
                try
                {
                    string htmlContent = await httpClient.GetStringAsync(url);
                    var parsedContents = ParseHtmlForLearningContent(htmlContent, url);
                    result.AddRange(parsedContents);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WebScraper Error for {url}: {ex.Message}");
                }
            }

            return result;
        }

        private List<LearningContent> ParseHtmlForLearningContent(string html, string sourceUrl)
        {
            var contents = new List<LearningContent>();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Örnek: Başlık ve açıklama içeren <article> etiketlerini yakala
            var articles = doc.DocumentNode.SelectNodes("//article");
            if (articles != null)
            {
                foreach (var article in articles)
                {
                    var titleNode = article.SelectSingleNode(".//h1 | .//h2 | .//h3");
                    var descNode = article.SelectSingleNode(".//p");

                    if (titleNode != null && descNode != null)
                    {
                        var content = new LearningContent
                        {
                            Id = Guid.NewGuid().ToString(),
                            Title = titleNode.InnerText.Trim(),
                            Description = descNode.InnerText.Trim(),
                            Category = DetermineCategoryFromUrl(sourceUrl),
                            LastUpdated = DateTime.Now,
                            Tags = ExtractTags(article),
                            ConfidenceScore = 0.95, // Öntanımlı yüksek güven
                            Dependencies = new List<string>(),
                            IsApplied = false
                        };
                        contents.Add(content);
                    }
                }
            }

            return contents;
        }

        private string DetermineCategoryFromUrl(string url)
        {
            if (url.Contains("gamedev")) return "GameDesign";
            if (url.Contains("ai") || url.Contains("ml")) return "AI";
            if (url.Contains("csharp") || url.Contains("cpp")) return "Programming";
            return "General";
        }

        private List<string> ExtractTags(HtmlNode articleNode)
        {
            var tags = new List<string>();
            var tagNodes = articleNode.SelectNodes(".//span[contains(@class,'tag')]");
            if (tagNodes != null)
            {
                foreach (var node in tagNodes)
                    tags.Add(node.InnerText.Trim());
            }
            return tags;
        }

        private List<string> GetDefaultLearningUrls()
        {
            // Manifesto veya config üzerinden güncellenebilir
            return new List<string>
            {
                "https://www.gamedev.net/articles",
                "https://www.c-sharpcorner.com/",
                "https://towardsdatascience.com/"
            };
        }
    }
}
