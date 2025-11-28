namespace NEDA.EngineIntegration.AssetManager
{
    public class AssetManager
    {
        private readonly string offlineAssetPath;
        private readonly string onlineAssetBaseUrl;
        private readonly HttpClient httpClient;
        private readonly Random randomizer;

        public AssetManager(string offlinePath, string onlineBaseUrl)
        {
            offlineAssetPath = offlinePath;
            onlineAssetBaseUrl = onlineBaseUrl;
            httpClient = new HttpClient();
            randomizer = new Random();
        }

        #region Asset Kontrol ve İndirme
        public async Task<bool> EnsureAssetsAsync(string projectName)
        {
            var requiredAssets = await GetProjectRequiredAssetsAsync(projectName);
            foreach (var asset in requiredAssets)
            {
                if (!AssetExists(asset))
                {
                    Console.WriteLine($"Eksik asset bulundu: {asset}. İndiriliyor...");
                    bool downloaded = await DownloadAssetAsync(asset);
                    if (!downloaded)
                    {
                        Console.WriteLine($"Asset indirilemedi: {asset}");
                        return false;
                    }
                }
            }
            Console.WriteLine("Tüm gerekli assetler mevcut.");
            return true;
        }

        public bool AssetExists(string assetName)
        {
            string filePath = Path.Combine(offlineAssetPath, assetName);
            return File.Exists(filePath);
        }

        public async Task<bool> DownloadAssetAsync(string assetName)
        {
            try
            {
                string url = $"{onlineAssetBaseUrl}/{assetName}";
                var response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return false;

                byte[] data = await response.Content.ReadAsByteArrayAsync();
                string savePath = Path.Combine(offlineAssetPath, assetName);

                await File.WriteAllBytesAsync(savePath, data);
                Console.WriteLine($"Asset indirildi: {assetName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Asset indirilemedi: {assetName}, Hata: {ex.Message}");
                return false;
            }
        }
        #endregion

        #region Gelişmiş AI Tabanlı Asset Yerleşim
        public class SceneArea
        {
            public float Width;
            public float Height;
            public float Depth;
            public float[,] TerrainHeightMap; // Arazi yüksekliği bilgisi
            public List<(string AssetName, float x, float y, float z)> PlacedAssets = new List<(string, float, float, float)>();
            public List<(string AssetName, float Width, float Depth)> AssetDimensions = new List<(string, float, float)>();
        }

        public async Task<bool> PlaceAssetsAIAsync(List<string> assetNames, SceneArea scene)
        {
            foreach (var asset in assetNames)
            {
                var position = await ComputeOptimalPositionAsync(scene, asset);
                await PlaceAssetInSceneAsync(asset, position);
                scene.PlacedAssets.Add((asset, position.x, position.y, position.z));
            }

            Console.WriteLine("Tüm assetler gelişmiş AI ile sahneye yerleştirildi.");
            return true;
        }

        private async Task<(float x, float y, float z)> ComputeOptimalPositionAsync(SceneArea scene, string asset)
        {
            // AI tabanlı yerleşim optimizasyonu
            float minDistance = 2.0f;
            int maxAttempts = 500;
            (float x, float y, float z) bestPosition = (0, 0, 0);
            float bestScore = float.MinValue;

            for (int i = 0; i < maxAttempts; i++)
            {
                float x = (float)(randomizer.NextDouble() * scene.Width);
                float z = (float)(randomizer.NextDouble() * scene.Depth);
                float y = GetTerrainHeight(scene, x, z);

                if (!IsPlacementValid(scene, asset, x, y, z, minDistance))
                    continue;

                float score = EvaluatePlacement(scene, asset, x, y, z);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPosition = (x, y, z);
                }
            }

            // Eğer uygun pozisyon bulunamazsa güvenli alana yerleştir
            if (bestScore == float.MinValue)
                bestPosition = FindSafeEmptySpot(scene);

            return bestPosition;
        }

        private float GetTerrainHeight(SceneArea scene, float x, float z)
        {
            int ix = Math.Min((int)x, scene.TerrainHeightMap.GetLength(0) - 1);
            int iz = Math.Min((int)z, scene.TerrainHeightMap.GetLength(1) - 1);
            return scene.TerrainHeightMap[ix, iz];
        }

        private bool IsPlacementValid(SceneArea scene, string asset, float x, float y, float z, float minDistance)
        {
            foreach (var placed in scene.PlacedAssets)
            {
                float dist = (float)Math.Sqrt(Math.Pow(x - placed.x, 2) + Math.Pow(y - placed.y, 2) + Math.Pow(z - placed.z, 2));
                if (dist < minDistance)
                    return false;
            }

            // Asset tipine özel kurallar
            if (asset.StartsWith("Tree") && y < 0) return false;
            if (asset.StartsWith("House") && y > 10) return false;
            return true;
        }

        private float EvaluatePlacement(SceneArea scene, string asset, float x, float y, float z)
        {
            // Gelişmiş AI: Assetlerin çevresel uygunluğu
            float score = 0f;

            foreach (var placed in scene.PlacedAssets)
            {
                float dist = (float)Math.Sqrt(Math.Pow(x - placed.x, 2) + Math.Pow(y - placed.y, 2) + Math.Pow(z - placed.z, 2));
                score += dist; // Daha uzaksa daha iyi
            }

            // Örnek: Bina ve ağaç kombinasyonu, ses ve ışık uyumu, terrain eğimi vs. etkiler
            if (asset.StartsWith("House") && y < 2) score += 5f; // evler alçak, stabil arazide olsun
            if (asset.StartsWith("Tree") && y > 0) score += 3f; // ağaçlar suya batmasın

            return score;
        }

        private (float x, float y, float z) FindSafeEmptySpot(SceneArea scene)
        {
            for (int ix = 0; ix < scene.Width; ix++)
            {
                for (int iz = 0; iz < scene.Depth; iz++)
                {
                    float y = GetTerrainHeight(scene, ix, iz);
                    if (IsPlacementValid(scene, "Fallback", ix, y, iz, 2.0f))
                        return (ix, y, iz);
                }
            }
            return (0, 0, 0);
        }

        private async Task PlaceAssetInSceneAsync(string assetName, (float x, float y, float z) position)
        {
            await Task.Delay(5);
            Console.WriteLine($"AI yerleşimi: {assetName} -> ({position.x},{position.y},{position.z})");
        }
        #endregion

        #region Map ve Proje Assetleri
        public async Task<List<string>> GetMapAssetsAsync(string projectName)
        {
            await Task.Delay(10);
            return new List<string>
            {
                "Tree_01.fbx",
                "House_01.fbx",
                "River_01.fbx",
                "Rock_01.fbx",
                "Grass_01.fbx",
                "Sound_Ambience.mp3"
            };
        }

        private async Task<List<string>> GetProjectRequiredAssetsAsync(string projectName)
        {
            await Task.Delay(10);
            return new List<string>
            {
                "Tree_01.fbx",
                "House_01.fbx",
                "Rock_01.fbx",
                "Grass_01.fbx"
            };
        }
        #endregion
    }
}
