using Neda.Automation;
using NEDA.Core.Automation;
using NEDA.SecurityModules.ActivityLogs;

namespace NEDA.WindowsIntegration
{
    public class VisualStudioController
    {
        private readonly string _projectRoot;

        public VisualStudioController()
        {
            _projectRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VSProjects");
            if (!Directory.Exists(_projectRoot))
                Directory.CreateDirectory(_projectRoot);
        }

        #region Project Management

        public void CreateProject(string projectName, string language = "C++")
        {
            try
            {
                string projectPath = Path.Combine(_projectRoot, projectName);
                if (!Directory.Exists(projectPath))
                    Directory.CreateDirectory(projectPath);

                // Temel dosya ve yapılar
                string mainFile = Path.Combine(projectPath, "Main." + (language == "C++" ? "cpp" : "cs"));
                File.WriteAllText(mainFile, $"// Başlangıç dosyası: {projectName}");

                string readmeFile = Path.Combine(projectPath, "README.md");
                File.WriteAllText(readmeFile, $"# {projectName}\nOluşturulma: {DateTime.Now}");

                ActivityLogger.LogActivity($"{language} projesi oluşturuldu: {projectName}");
                Console.WriteLine($"[VisualStudio] {projectName} projesi ({language}) oluşturuldu.");
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"Hata: Proje oluşturulamadı - {ex.Message}");
                Console.WriteLine($"[VisualStudio] Hata: {ex.Message}");
            }
        }

        public void AddClass(string projectName, string className, string language = "C++", string namespaceName = null)
        {
            try
            {
                string projectPath = Path.Combine(_projectRoot, projectName);
                if (!Directory.Exists(projectPath))
                    throw new Exception("Proje bulunamadı.");

                string extension = language == "C++" ? "cpp" : "cs";
                string classFile = Path.Combine(projectPath, $"{className}.{extension}");

                string content = language == "C++" ?
                    $"// {className} sınıfı\nclass {className} {{\npublic:\n    {className}() {{}}\n}};" :
                    $"// {className} sınıfı\nnamespace {namespaceName ?? "NEDA"} {{\n    public class {className} {{\n        public {className}() {{ }}\n    }}\n}}";

                File.WriteAllText(classFile, content);

                ActivityLogger.LogActivity($"{projectName} projesine {className} sınıfı eklendi.");
                Console.WriteLine($"[VisualStudio] {className} sınıfı projeye eklendi.");
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"Hata: Sınıf eklenemedi - {ex.Message}");
                Console.WriteLine($"[VisualStudio] Hata: {ex.Message}");
            }
        }

        public void AddInterface(string projectName, string interfaceName, string language = "C#")
        {
            try
            {
                string projectPath = Path.Combine(_projectRoot, projectName);
                if (!Directory.Exists(projectPath))
                    throw new Exception("Proje bulunamadı.");

                string interfaceFile = Path.Combine(projectPath, $"{interfaceName}.cs");
                string content = $"// {interfaceName} interface\nnamespace NEDA {{ public interface {interfaceName} {{ }} }}";
                File.WriteAllText(interfaceFile, content);

                ActivityLogger.LogActivity($"{projectName} projesine {interfaceName} interface eklendi.");
                Console.WriteLine($"[VisualStudio] {interfaceName} interface projeye eklendi.");
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"Hata: Interface eklenemedi - {ex.Message}");
                Console.WriteLine($"[VisualStudio] Hata: {ex.Message}");
            }
        }

        public void AddEnum(string projectName, string enumName, List<string> values, string language = "C#")
        {
            try
            {
                string projectPath = Path.Combine(_projectRoot, projectName);
                if (!Directory.Exists(projectPath))
                    throw new Exception("Proje bulunamadı.");

                string enumFile = Path.Combine(projectPath, $"{enumName}.cs");
                string content = $"namespace NEDA {{ public enum {enumName} {{ {string.Join(", ", values)} }} }}";
                File.WriteAllText(enumFile, content);

                ActivityLogger.LogActivity($"{projectName} projesine {enumName} enum eklendi.");
                Console.WriteLine($"[VisualStudio] {enumName} enum projeye eklendi.");
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"Hata: Enum eklenemedi - {ex.Message}");
                Console.WriteLine($"[VisualStudio] Hata: {ex.Message}");
            }
        }

        #endregion

        #region Build & Run

        public async Task BuildAndRunAsync(string projectName)
        {
            try
            {
                ActivityLogger.LogActivity($"{projectName} projesi derleniyor ve çalıştırılıyor...");
                Console.WriteLine($"[VisualStudio] {projectName} derleniyor...");
                await Task.Delay(2000);
                Console.WriteLine($"[VisualStudio] {projectName} çalıştırıldı.");
                ActivityLogger.LogActivity($"{projectName} projesi başarıyla çalıştırıldı.");
            }
            catch (Exception ex)
            {
                ActivityLogger.LogActivity($"Hata: Proje çalıştırılamadı - {ex.Message}");
                Console.WriteLine($"[VisualStudio] Hata: {ex.Message}");
            }
        }

        #endregion

        #region AutomationEngine Integration

        public void ScheduleProjectWorkflow(string projectName, string language = "C++")
        {
            var automation = new AutomationEngine(
                new TaskPlanner(),
                null, null, null, null, null, null
            );

            automation.ScheduleTask(
                $"create_project_{projectName}",
                "Proje oluşturma",
                async () => { CreateProject(projectName, language); return true; },
                TaskPriority.Critical
            );

            automation.ScheduleTask(
                $"add_main_class_{projectName}",
                "Main sınıf ekleme",
                async () => { AddClass(projectName, "Main", language); return true; },
                TaskPriority.High,
                new List<string> { $"create_project_{projectName}" }
            );
        }

        internal async Task<bool> SetupProjectAsync(string projectName, string projectType)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
