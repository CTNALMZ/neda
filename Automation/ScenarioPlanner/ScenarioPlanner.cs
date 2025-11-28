namespace NEDA.Core.ScenarioPlanner
{
    public class ScenarioPlanner
    {
        private List<GameCharacter> characters;
        private List<GameTask> tasks;
        private List<MapElement> mapElements;
        private KnowledgeBase.KnowledgeDB knowledgeDB;

        public ScenarioPlanner(KnowledgeBase.KnowledgeDB db)
        {
            knowledgeDB = db;
            characters = new List<GameCharacter>();
            tasks = new List<GameTask>();
            mapElements = new List<MapElement>();
        }

        #region Character Management
        public void AddCharacter(string name, string role, string traitsJson)
        {
            var traits = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(traitsJson);
            var character = new GameCharacter
            {
                Name = name,
                Role = role,
                Traits = traits
            };
            characters.Add(character);
        }

        public GameCharacter GetCharacter(string name)
        {
            return characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public List<GameCharacter> GetAllCharacters()
        {
            return characters;
        }
        #endregion

        #region Task Management
        public void AddTask(string title, string description, int priority = 1, List<string> dependencies = null)
        {
            var task = new GameTask
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Description = description,
                Priority = priority,
                Dependencies = dependencies ?? new List<string>(),
                IsCompleted = false
            };
            tasks.Add(task);
        }

        public void CompleteTask(string taskId)
        {
            var task = tasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                task.IsCompleted = true;
            }
        }

        public List<GameTask> GetPendingTasks()
        {
            return tasks.Where(t => !t.IsCompleted).OrderByDescending(t => t.Priority).ToList();
        }
        #endregion

        #region Map & Environment Management
        public void AddMapElement(string type, string name, float x, float y, float z, Dictionary<string, string> properties = null)
        {
            var element = new MapElement
            {
                Type = type,
                Name = name,
                PositionX = x,
                PositionY = y,
                PositionZ = z,
                Properties = properties ?? new Dictionary<string, string>()
            };
            mapElements.Add(element);
        }

        public List<MapElement> GetMapElementsByType(string type)
        {
            return mapElements.Where(e => e.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<MapElement> GetAllMapElements()
        {
            return mapElements;
        }
        #endregion

        #region Scenario Planning & Optimization
        public void GenerateScenarioPlan()
        {
            // Karakterleri, görevleri ve harita öğelerini optimize eder
            OptimizeCharacterRoles();
            OptimizeTaskOrder();
            OptimizeMapPlacement();
            Console.WriteLine("Scenario plan generated successfully.");
        }

        private void OptimizeCharacterRoles()
        {
            // KnowledgeDB’den alınan bilgileri kullanarak karakter görev dağılımını optimize et
        }

        private void OptimizeTaskOrder()
        {
            // Öncelikleri ve bağımlılıkları kontrol ederek görev sırasını optimize et
        }

        private void OptimizeMapPlacement()
        {
            // Harita öğelerinin mantıklı ve dengeli bir şekilde yerleşmesini sağla
        }
        #endregion
    }

    #region Supporting Classes
    public class GameCharacter
    {
        public string Name { get; set; }
        public string Role { get; set; }
        public Dictionary<string, string> Traits { get; set; }
    }

    public class GameTask
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public int Priority { get; set; }
        public List<string> Dependencies { get; set; }
        public bool IsCompleted { get; set; }
    }

    public class MapElement
    {
        public string Type { get; set; } // Örn: "Tree", "House", "River"
        public string Name { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }
    #endregion
}
