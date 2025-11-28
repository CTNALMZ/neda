using NEDA.SecurityModules.ActivityLogs;

namespace NEDA.EngineIntegration
{
    public enum NPCType { Enemy, Ally, Neutral, Animal }

    public class NPC
    {
        public string Name { get; set; }
        public NPCType Type { get; set; }
        public (float x, float y, float z) Position { get; set; }
        public bool IsActive { get; set; } = true;
        public int Health { get; set; } = 100;
        public int AggroRange { get; set; } = 15;
        public int DetectionRange { get; set; } = 20;
        public string CurrentTarget { get; set; } = null;
    }

    public class UnrealEngineController
    {
        private readonly string _projectDirectory;
        private readonly Dictionary<string, List<string>> _sceneAssets;
        private readonly Dictionary<string, List<(float x, float y, float z)>> _placedAssets;
        private readonly List<NPC> _npcs;
        private readonly Random _random;

        public UnrealEngineController()
        {
            _projectDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnrealProjects");
            if (!Directory.Exists(_projectDirectory))
                Directory.CreateDirectory(_projectDirectory);

            _sceneAssets = new Dictionary<string, List<string>>();
            _placedAssets = new Dictionary<string, List<(float x, float y, float z)>>();
            _npcs = new List<NPC>();
            _random = new Random();
        }

        #region Game Planning
        public void PlanGame(string gameName, string genre)
        {
            ActivityLogger.LogActivity($"Oyun planlama başlatıldı: {gameName} - {genre}");
            Console.WriteLine($"[UnrealEngine] {gameName} oyunu planlanıyor (Tür: {genre})");

            if (!_sceneAssets.ContainsKey(gameName))
                _sceneAssets[gameName] = new List<string>();

            _sceneAssets[gameName].Add("HeroCharacter");
            _sceneAssets[gameName].Add("EnemyAI");
            _sceneAssets[gameName].Add("LevelMap01");

            ActivityLogger.LogActivity("Oyun planlaması tamamlandı ve gerekli assetler belirlendi.");
        }
        #endregion

        #region Blueprint & Script Generation
        public void GenerateBlueprintsAndScripts(string gameName)
        {
            ActivityLogger.LogActivity($"Blueprint ve C++ script üretimi başlatıldı: {gameName}");
            Console.WriteLine($"[UnrealEngine] {gameName} için Blueprint ve C++ scriptler oluşturuluyor...");

            foreach (var asset in _sceneAssets[gameName])
                Console.WriteLine($"[UnrealEngine] {asset} asset için Blueprint ve Script oluşturuluyor...");

            ActivityLogger.LogActivity($"Blueprint ve script üretimi tamamlandı: {gameName}");
        }
        #endregion

        #region Asset Management
        public async Task AddAssetAsync(string gameName, string assetName)
        {
            Console.WriteLine($"[UnrealEngine] {assetName} asset ekleniyor...");
            ActivityLogger.LogActivity($"Asset ekleme başlatıldı: {assetName}");

            string assetPath = Path.Combine(_projectDirectory, assetName);

            if (!File.Exists(assetPath))
            {
                Console.WriteLine($"[UnrealEngine] {assetName} bulunamadı, indiriliyor...");
                await Task.Delay(500); // Simülasyon
                File.Create(assetPath).Close();
                Console.WriteLine($"[UnrealEngine] {assetName} indirildi ve projeye eklendi.");
            }
            else
                Console.WriteLine($"[UnrealEngine] {assetName} zaten mevcut.");

            if (!_sceneAssets.ContainsKey(gameName))
                _sceneAssets[gameName] = new List<string>();
            _sceneAssets[gameName].Add(assetName);
        }

        public async Task PlaceAssetAsync(string assetName, (float x, float y, float z) position)
        {
            await Task.Delay(10);
            Console.WriteLine($"[UnrealEngine] Asset {assetName} yerleştirildi -> ({position.x}, {position.y}, {position.z})");

            if (!_placedAssets.ContainsKey(assetName))
                _placedAssets[assetName] = new List<(float x, float y, float z)>();
            _placedAssets[assetName].Add(position);
        }
        #endregion

        #region NPC Management
        public async Task PlaceNPCAsync(string npcName, NPCType type, (float x, float y, float z) position)
        {
            _npcs.Add(new NPC { Name = npcName, Type = type, Position = position });
            await Task.Delay(10);
            Console.WriteLine($"[UnrealEngine] NPC {npcName} ({type}) sahneye yerleştirildi -> ({position.x}, {position.y}, {position.z})");
        }

        public async Task UpdateNPCsAsync((float x, float y, float z) playerPosition)
        {
            foreach (var npc in _npcs)
            {
                if (!npc.IsActive) continue;

                var newPos = GetRandomPosition("map");
                if (!IsPositionOccupied(newPos))
                    npc.Position = newPos;

                float distanceToPlayer = GetDistance(npc.Position, playerPosition);

                switch (npc.Type)
                {
                    case NPCType.Enemy:
                        if (distanceToPlayer <= npc.AggroRange)
                        {
                            npc.CurrentTarget = "Player";
                            Console.WriteLine($"[AI] {npc.Name} saldırıya geçiyor!");
                        }
                        break;
                    case NPCType.Ally:
                        if (distanceToPlayer <= npc.DetectionRange)
                        {
                            npc.CurrentTarget = "Player";
                            Console.WriteLine($"[AI] {npc.Name} destek veriyor Player’a.");
                        }
                        break;
                    case NPCType.Animal:
                        if (distanceToPlayer <= npc.DetectionRange)
                        {
                            npc.CurrentTarget = null;
                            npc.Position = GetFleePosition(npc.Position, playerPosition);
                            Console.WriteLine($"[AI] {npc.Name} kaçıyor!");
                        }
                        break;
                    case NPCType.Neutral:
                        npc.CurrentTarget = null;
                        Console.WriteLine($"[AI] {npc.Name} dolaşıyor ve çevreyi inceliyor.");
                        break;
                }

                await Task.Delay(50);
            }
        }

        private (float x, float y, float z) GetFleePosition((float x, float y, float z) npcPos, (float x, float y, float z) threatPos)
        {
            float dx = npcPos.x - threatPos.x;
            float dz = npcPos.z - threatPos.z;
            float magnitude = (float)Math.Sqrt(dx * dx + dz * dz);
            if (magnitude == 0) magnitude = 1;
            return (npcPos.x + dx / magnitude * 5, npcPos.y, npcPos.z + dz / magnitude * 5);
        }

        private float GetDistance((float x, float y, float z) a, (float x, float y, float z) b)
        {
            return (float)Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2) + Math.Pow(a.z - b.z, 2));
        }
        #endregion

        #region Terrain, Vegetation, Props, Water
        public object AnalyzeTerrain(string gameName) => new { Width = 500, Depth = 500, HeightMap = new float[500, 500] };

        public async Task GenerateTerrainAsync(string gameName, object terrainData)
        {
            await Task.Delay(50);
            Console.WriteLine($"[UnrealEngine] Terrain oluşturuldu: {gameName}");
        }

        public List<string> GetVegetationAssets(string gameName) => new List<string> { "Tree_01", "Tree_02", "Grass_01" };
        public List<string> GetPropsAssets(string gameName) => new List<string> { "Rock_01", "Lamp_01", "Bench_01" };
        public List<string> GetWaterAssets(string gameName) => new List<string> { "River_01", "Lake_01" };
        #endregion

        #region Cutscene & Cinematics
        public async Task SetupCutsceneAsync(string sceneName, List<string> involvedAssets)
        {
            Console.WriteLine($"[UnrealEngine] Cutscene hazırlığı: {sceneName}");
            await Task.Delay(500);
            Console.WriteLine($"[UnrealEngine] Cutscene tamamlandı: {sceneName}");
        }

        public async Task TriggerCutsceneAsync(string sceneName, List<string> involvedAssets)
        {
            Console.WriteLine($"[UnrealEngine] Cutscene tetikleniyor: {sceneName}");
            await Task.Delay(500);
            Console.WriteLine($"[UnrealEngine] Cutscene tamamlandı: {sceneName}");
        }
        #endregion

        #region Render & Build
        public async Task RenderSceneAsync(string gameName)
        {
            Console.WriteLine($"[UnrealEngine] {gameName} sahnesi render ediliyor...");
            await Task.Delay(500);
            Console.WriteLine($"[UnrealEngine] {gameName} render tamamlandı.");
        }

        public async Task BuildAndRunProjectAsync(string projectName)
        {
            Console.WriteLine($"[UnrealEngine] {projectName} derleniyor ve çalıştırılıyor...");
            await Task.Delay(1000);
            Console.WriteLine($"[UnrealEngine] {projectName} başarıyla çalıştırıldı.");
        }

        internal (float x, float y, float z) GetRandomPosition(string mapName)
        {
            throw new NotImplementedException();
        }

        internal bool IsPositionOccupied((float x, float y, float z) candidate, float minDistance)
        {
            throw new NotImplementedException();
        }

        internal bool IsPositionValidForAsset(string asset, (float x, float y, float z) candidate)
        {
            throw new NotImplementedException();
        }

        internal (float x, float y, float z) GetSafeFallbackPosition(string mapName)
        {
            throw new NotImplementedException();
        }

        internal async Task<bool> CreateProjectAsync(string projectName, string style)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}