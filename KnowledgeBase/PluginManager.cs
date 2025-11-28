using Newtonsoft.Json;
using System.Reflection;
using System.Xml;

namespace NEDA.Core.KnowledgeBase
{
    public class PluginManager
    {
        private string pluginFolder = "Plugins";
        private Dictionary<string, PluginInfo> loadedPlugins;
        private string pluginConfigFile = "PluginConfig.json";

        public PluginManager()
        {
            loadedPlugins = new Dictionary<string, PluginInfo>();
            LoadPluginConfig();
            LoadAllPlugins();
        }

        private void LoadPluginConfig()
        {
            if (File.Exists(pluginConfigFile))
            {
                string json = File.ReadAllText(pluginConfigFile);
                var configs = JsonConvert.DeserializeObject<List<PluginInfo>>(json);
                foreach (var plugin in configs)
                {
                    loadedPlugins[plugin.Name] = plugin;
                }
            }
        }

        public void SavePluginConfig()
        {
            string json = JsonConvert.SerializeObject(loadedPlugins.Values, Formatting.Indented);
            File.WriteAllText(pluginConfigFile, json);
        }

        public void LoadAllPlugins()
        {
            if (!Directory.Exists(pluginFolder))
                Directory.CreateDirectory(pluginFolder);

            var pluginFiles = Directory.GetFiles(pluginFolder, "*.dll");
            foreach (var file in pluginFiles)
            {
                LoadPlugin(file);
            }
        }

        public bool LoadPlugin(string path)
        {
            try
            {
                Assembly pluginAssembly = Assembly.LoadFile(Path.GetFullPath(path));
                foreach (Type type in pluginAssembly.GetTypes())
                {
                    if (typeof(INEDAPlugin).IsAssignableFrom(type) && !type.IsInterface)
                    {
                        INEDAPlugin pluginInstance = (INEDAPlugin)Activator.CreateInstance(type);

                        if (ValidatePlugin(pluginInstance))
                        {
                            loadedPlugins[pluginInstance.Name] = new PluginInfo
                            {
                                Name = pluginInstance.Name,
                                Version = pluginInstance.Version,
                                Author = pluginInstance.Author,
                                Description = pluginInstance.Description
                            };
                            pluginInstance.Initialize();
                            Console.WriteLine($"Plugin loaded: {pluginInstance.Name} v{pluginInstance.Version}");
                        }
                    }
                }
                SavePluginConfig();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load plugin {path}: {ex.Message}");
                return false;
            }
        }

        private bool ValidatePlugin(INEDAPlugin plugin)
        {
            // Burada güvenlik manifesto ve izin kontrolleri uygulanır
            // Örn: sadece izin verilen kategoriler veya dijital imza kontrolü
            return true; // Şimdilik tüm pluginler kabul ediliyor
        }

        public INEDAPlugin GetPlugin(string name)
        {
            if (loadedPlugins.ContainsKey(name))
            {
                // Plugin instance'ını tekrar döndürebiliriz veya singleton mantığıyla yönetebiliriz
                return null; // Placeholder
            }
            return null;
        }
    }

    public interface INEDAPlugin
    {
        string Name { get; }
        string Version { get; }
        string Author { get; }
        string Description { get; }
        void Initialize();
    }

    public class PluginInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
    }
}
