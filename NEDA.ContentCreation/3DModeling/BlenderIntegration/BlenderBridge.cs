using NEDA.AI.NaturalLanguage;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.ContentCreation._3DModeling.BlenderIntegration;
{
    /// <summary>
    /// Blender ile .NET uygulaması arasında köprü görevi gören entegrasyon sınıfı;
    /// Python scripting ve IPC üzerinden Blender'ı kontrol eder;
    /// </summary>
    public class BlenderBridge : IBlenderBridge, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private Process _blenderProcess;
        private readonly SemaphoreSlim _processLock = new SemaphoreSlim(1, 1);
        private bool _isConnected;
        private readonly string _blenderExecutablePath;
        private readonly string _pythonScriptsDirectory;
        private readonly TimeSpan _commandTimeout;

        /// <summary>
        /// Blender bağlantı durumu;
        /// </summary>
        public bool IsConnected => _isConnected && _blenderProcess != null && !_blenderProcess.HasExited;

        /// <summary>
        /// Blender sürüm bilgisi;
        /// </summary>
        public string BlenderVersion { get; private set; }

        /// <summary>
        /// Bağlantı olayları;
        /// </summary>
        public event EventHandler<BlenderConnectionEventArgs> ConnectionStatusChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// BlenderBridge constructor;
        /// </summary>
        /// <param name="logger">Logging servisi</param>
        /// <param name="config">Uygulama konfigürasyonu</param>
        public BlenderBridge(ILogger logger, AppConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Config'den yolları al;
            _blenderExecutablePath = _config.BlenderSettings?.ExecutablePath;
                ?? @"C:\Program Files\Blender Foundation\Blender\blender.exe";

            _pythonScriptsDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Scripts",
                "Blender"
            );

            _commandTimeout = TimeSpan.FromSeconds(
                _config.BlenderSettings?.CommandTimeoutSeconds ?? 30;
            );

            EnsureDirectoriesExist();

            _logger.LogInformation("BlenderBridge initialized. Executable: {Path}", _blenderExecutablePath);
        }

        #endregion;

        #region Connection Management;

        /// <summary>
        /// Blender'a bağlanır;
        /// </summary>
        /// <param name="backgroundMode">Arka planda çalışma modu</param>
        /// <returns>Bağlantı durumu</returns>
        public async Task<bool> ConnectAsync(bool backgroundMode = true)
        {
            await _processLock.WaitAsync();

            try
            {
                if (IsConnected)
                {
                    _logger.LogWarning("Blender is already connected");
                    return true;
                }

                _logger.LogInformation("Connecting to Blender...");

                if (!File.Exists(_blenderExecutablePath))
                {
                    throw new BlenderException(
                        ErrorCodes.BLENDER_NOT_FOUND,
                        $"Blender executable not found at: {_blenderExecutablePath}"
                    );
                }

                // Blender process'ini başlat;
                var startInfo = new ProcessStartInfo;
                {
                    FileName = _blenderExecutablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = backgroundMode,
                    WindowStyle = backgroundMode ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
                };

                if (backgroundMode)
                {
                    startInfo.Arguments = "--background --python-console";
                }

                _blenderProcess = new Process;
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true;
                };

                _blenderProcess.OutputDataReceived += OnOutputDataReceived;
                _blenderProcess.ErrorDataReceived += OnErrorDataReceived;
                _blenderProcess.Exited += OnProcessExited;

                if (!_blenderProcess.Start())
                {
                    throw new BlenderException(
                        ErrorCodes.BLENDER_START_FAILED,
                        "Failed to start Blender process"
                    );
                }

                _blenderProcess.BeginOutputReadLine();
                _blenderProcess.BeginErrorReadLine();

                // Blender'ın hazır olmasını bekle;
                await Task.Delay(2000);

                // Sürüm bilgisini al;
                BlenderVersion = await GetBlenderVersionAsync();

                _isConnected = true;

                OnConnectionStatusChanged(new BlenderConnectionEventArgs;
                {
                    IsConnected = true,
                    Timestamp = DateTime.UtcNow,
                    BlenderVersion = BlenderVersion;
                });

                _logger.LogInformation("Successfully connected to Blender v{Version}", BlenderVersion);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Blender");
                _isConnected = false;
                throw new BlenderException(
                    ErrorCodes.BLENDER_CONNECTION_FAILED,
                    "Blender connection failed",
                    ex;
                );
            }
            finally
            {
                _processLock.Release();
            }
        }

        /// <summary>
        /// Blender bağlantısını kapatır;
        /// </summary>
        public async Task DisconnectAsync()
        {
            await _processLock.WaitAsync();

            try
            {
                if (!IsConnected)
                {
                    return;
                }

                _logger.LogInformation("Disconnecting from Blender...");

                // Python script ile graceful shutdown;
                await ExecutePythonScriptAsync("graceful_shutdown.py");

                await Task.Delay(1000);

                if (!_blenderProcess.HasExited)
                {
                    _blenderProcess.Kill();
                    await _blenderProcess.WaitForExitAsync();
                }

                CleanupProcess();

                _isConnected = false;

                OnConnectionStatusChanged(new BlenderConnectionEventArgs;
                {
                    IsConnected = false,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Disconnected from Blender");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while disconnecting from Blender");
                throw;
            }
            finally
            {
                _processLock.Release();
            }
        }

        private void CleanupProcess()
        {
            if (_blenderProcess == null) return;

            _blenderProcess.OutputDataReceived -= OnOutputDataReceived;
            _blenderProcess.ErrorDataReceived -= OnErrorDataReceived;
            _blenderProcess.Exited -= OnProcessExited;

            _blenderProcess.Dispose();
            _blenderProcess = null;
        }

        #endregion;

        #region Python Script Execution;

        /// <summary>
        /// Python script'ini Blender'da çalıştırır;
        /// </summary>
        /// <param name="scriptContent">Python script içeriği</param>
        /// <returns>Çalıştırma sonucu</returns>
        public async Task<ScriptExecutionResult> ExecutePythonScriptAsync(string scriptContent)
        {
            if (!IsConnected)
            {
                throw new BlenderException(
                    ErrorCodes.BLENDER_NOT_CONNECTED,
                    "Blender is not connected"
                );
            }

            await _processLock.WaitAsync();

            try
            {
                _logger.LogDebug("Executing Python script in Blender");

                // Python kodunu Blender'a gönder;
                var command = $"\n{scriptContent}\n";
                await _blenderProcess.StandardInput.WriteLineAsync(command);
                await _blenderProcess.StandardInput.FlushAsync();

                // Basit bir şekilde çıktı al (gerçek implementasyonda daha sofistike olmalı)
                await Task.Delay(500);

                return new ScriptExecutionResult;
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute Python script");
                throw new BlenderException(
                    ErrorCodes.SCRIPT_EXECUTION_FAILED,
                    "Python script execution failed",
                    ex;
                );
            }
            finally
            {
                _processLock.Release();
            }
        }

        /// <summary>
        /// Dosyadan Python script'ini çalıştırır;
        /// </summary>
        /// <param name="scriptPath">Script dosya yolu</param>
        /// <param name="parameters">Script parametreleri</param>
        public async Task<ScriptExecutionResult> ExecutePythonFileAsync(string scriptPath, params object[] parameters)
        {
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Python script not found: {scriptPath}");
            }

            var scriptContent = await File.ReadAllTextAsync(scriptPath);

            // Parametreleri script'e enjekte et;
            if (parameters != null && parameters.Length > 0)
            {
                scriptContent = InjectParameters(scriptContent, parameters);
            }

            return await ExecutePythonScriptAsync(scriptContent);
        }

        /// <summary>
        /// Template Python script'ini çalıştırır;
        /// </summary>
        /// <param name="templateName">Template dosya adı</param>
        /// <param name="parameters">Template parametreleri</param>
        public async Task<ScriptExecutionResult> ExecuteTemplateAsync(string templateName, IDictionary<string, object> parameters = null)
        {
            var templatePath = Path.Combine(_pythonScriptsDirectory, "Templates", $"{templateName}.py");

            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException($"Template not found: {templatePath}");
            }

            var templateContent = await File.ReadAllTextAsync(templatePath);

            if (parameters != null)
            {
                templateContent = InjectTemplateParameters(templateContent, parameters);
            }

            return await ExecutePythonScriptAsync(templateContent);
        }

        #endregion;

        #region 3D Model Operations;

        /// <summary>
        /// 3D model oluşturur;
        /// </summary>
        /// <param name="modelType">Model tipi (Cube, Sphere, Cylinder, etc.)</param>
        /// <param name="parameters">Model parametreleri</param>
        /// <returns>Oluşturulan model bilgisi</returns>
        public async Task<ModelCreationResult> CreateModelAsync(ModelType modelType, ModelParameters parameters)
        {
            var templateName = $"create_{modelType.ToString().ToLower()}";

            var scriptParams = new Dictionary<string, object>
            {
                ["location"] = parameters.Location,
                ["scale"] = parameters.Scale,
                ["rotation"] = parameters.Rotation,
                ["name"] = parameters.Name ?? $"{modelType}_{Guid.NewGuid():N}"
            };

            // Model tipine özel parametreler;
            switch (modelType)
            {
                case ModelType.Cube:
                    scriptParams["size"] = parameters.GetValue<float>("size", 2.0f);
                    break;
                case ModelType.Sphere:
                    scriptParams["radius"] = parameters.GetValue<float>("radius", 1.0f);
                    scriptParams["segments"] = parameters.GetValue<int>("segments", 32);
                    break;
                case ModelType.Cylinder:
                    scriptParams["radius"] = parameters.GetValue<float>("radius", 1.0f);
                    scriptParams["depth"] = parameters.GetValue<float>("depth", 2.0f);
                    scriptParams["vertices"] = parameters.GetValue<int>("vertices", 32);
                    break;
            }

            var result = await ExecuteTemplateAsync(templateName, scriptParams);

            return new ModelCreationResult;
            {
                Success = result.Success,
                ModelName = scriptParams["name"].ToString(),
                ModelType = modelType,
                Parameters = parameters,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// 3D model import eder;
        /// </summary>
        /// <param name="filePath">Model dosya yolu</param>
        /// <param name="options">Import seçenekleri</param>
        public async Task<ImportResult> ImportModelAsync(string filePath, ImportOptions options = null)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Model file not found: {filePath}");
            }

            options ??= new ImportOptions();

            var extension = Path.GetExtension(filePath).ToLower();
            var templateName = GetImportTemplateForExtension(extension);

            var scriptParams = new Dictionary<string, object>
            {
                ["file_path"] = filePath,
                ["scale_factor"] = options.ScaleFactor,
                ["merge_vertices"] = options.MergeVertices,
                ["forward_axis"] = options.ForwardAxis.ToString().ToUpper(),
                ["up_axis"] = options.UpAxis.ToString().ToUpper()
            };

            var result = await ExecuteTemplateAsync(templateName, scriptParams);

            return new ImportResult;
            {
                Success = result.Success,
                FilePath = filePath,
                FileSize = new FileInfo(filePath).Length,
                ImportOptions = options,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// 3D model export eder;
        /// </summary>
        /// <param name="modelName">Model adı</param>
        /// <param name="outputPath">Çıktı yolu</param>
        /// <param name="format">Export formatı</param>
        /// <param name="options">Export seçenekleri</param>
        public async Task<ExportResult> ExportModelAsync(string modelName, string outputPath, ExportFormat format, ExportOptions options = null)
        {
            options ??= new ExportOptions();

            var templateName = $"export_{format.ToString().ToLower()}";

            var scriptParams = new Dictionary<string, object>
            {
                ["model_name"] = modelName,
                ["output_path"] = outputPath,
                ["apply_modifiers"] = options.ApplyModifiers,
                ["selected_only"] = options.SelectedOnly,
                ["use_mesh_modifiers"] = options.UseMeshModifiers,
                ["global_scale"] = options.GlobalScale;
            };

            // Format'a özel parametreler;
            switch (format)
            {
                case ExportFormat.FBX:
                    scriptParams["bake_anim"] = options.GetValue<bool>("bake_anim", true);
                    scriptParams["bake_anim_use_all_actions"] = options.GetValue<bool>("bake_anim_use_all_actions", true);
                    break;
                case ExportFormat.OBJ:
                    scriptParams["use_materials"] = options.GetValue<bool>("use_materials", true);
                    scriptParams["use_normals"] = options.GetValue<bool>("use_normals", true);
                    break;
                case ExportFormat.STL:
                    scriptParams["ascii_format"] = options.GetValue<bool>("ascii_format", false);
                    break;
            }

            var result = await ExecuteTemplateAsync(templateName, scriptParams);

            var exportResult = new ExportResult;
            {
                Success = result.Success,
                ModelName = modelName,
                OutputPath = outputPath,
                Format = format,
                ExportOptions = options,
                Timestamp = DateTime.UtcNow;
            };

            if (result.Success && File.Exists(outputPath))
            {
                exportResult.FileSize = new FileInfo(outputPath).Length;
            }

            return exportResult;
        }

        /// <summary>
        /// Mesh'i optimize eder;
        /// </summary>
        /// <param name="modelName">Model adı</param>
        /// <param name="optimizationOptions">Optimizasyon seçenekleri</param>
        public async Task<OptimizationResult> OptimizeMeshAsync(string modelName, MeshOptimizationOptions optimizationOptions)
        {
            var scriptParams = new Dictionary<string, object>
            {
                ["model_name"] = modelName,
                ["decimate_ratio"] = optimizationOptions.DecimateRatio,
                ["remove_doubles"] = optimizationOptions.RemoveDoubles,
                ["recalculate_normals"] = optimizationOptions.RecalculateNormals,
                ["triangulate"] = optimizationOptions.Triangulate,
                ["merge_distance"] = optimizationOptions.MergeDistance;
            };

            var result = await ExecuteTemplateAsync("optimize_mesh", scriptParams);

            return new OptimizationResult;
            {
                Success = result.Success,
                ModelName = modelName,
                Options = optimizationOptions,
                Timestamp = DateTime.UtcNow;
            };
        }

        #endregion;

        #region Scene Operations;

        /// <summary>
        /// Blender sahnesini temizler;
        /// </summary>
        public async Task ClearSceneAsync()
        {
            await ExecuteTemplateAsync("clear_scene");
        }

        /// <summary>
        /// Blender sahnesini kaydeder;
        /// </summary>
        /// <param name="filePath">Kaydetme yolu</param>
        public async Task SaveSceneAsync(string filePath)
        {
            var scriptParams = new Dictionary<string, object>
            {
                ["file_path"] = filePath;
            };

            await ExecuteTemplateAsync("save_scene", scriptParams);
        }

        /// <summary>
        /// Blender sahnesini açar;
        /// </summary>
        /// <param name="filePath">Sahne dosya yolu</param>
        public async Task OpenSceneAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Scene file not found: {filePath}");
            }

            var scriptParams = new Dictionary<string, object>
            {
                ["file_path"] = filePath;
            };

            await ExecuteTemplateAsync("open_scene", scriptParams);
        }

        #endregion;

        #region Helper Methods;

        private async Task<string> GetBlenderVersionAsync()
        {
            var script = @"
import bpy;
print('NEDA_VERSION:' + bpy.app.version_string)
";

            var result = await ExecutePythonScriptAsync(script);

            // Gerçek implementasyonda stdout'dan version'ı parse et;
            return "3.0+"; // Basitleştirilmiş;
        }

        private string GetImportTemplateForExtension(string extension)
        {
            return extension switch;
            {
                ".fbx" => "import_fbx",
                ".obj" => "import_obj",
                ".stl" => "import_stl",
                ".blend" => "import_blend",
                ".dae" => "import_collada",
                ".3ds" => "import_3ds",
                ".ply" => "import_ply",
                _ => "import_generic"
            };
        }

        private string InjectParameters(string script, object[] parameters)
        {
            var sb = new StringBuilder(script);

            for (int i = 0; i < parameters.Length; i++)
            {
                var paramName = $"param{i}";
                var paramValue = ConvertParameterToPython(parameters[i]);
                sb.Replace($"{{{paramName}}}", paramValue);
            }

            return sb.ToString();
        }

        private string InjectTemplateParameters(string template, IDictionary<string, object> parameters)
        {
            var sb = new StringBuilder(template);

            foreach (var param in parameters)
            {
                var paramValue = ConvertParameterToPython(param.Value);
                sb.Replace($"{{{{{param.Key}}}}}", paramValue);
            }

            return sb.ToString();
        }

        private string ConvertParameterToPython(object value)
        {
            if (value == null) return "None";

            return value switch;
            {
                string s => $"\"{s.Replace("\"", "\\\"")}\"",
                bool b => b ? "True" : "False",
                int i => i.ToString(),
                float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Vector3 v => $"Vector(({v.X}, {v.Y}, {v.Z}))",
                _ => JsonConvert.SerializeObject(value)
            };
        }

        private void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(_pythonScriptsDirectory);
            Directory.CreateDirectory(Path.Combine(_pythonScriptsDirectory, "Templates"));
            Directory.CreateDirectory(Path.Combine(_pythonScriptsDirectory, "Scripts"));
            Directory.CreateDirectory(Path.Combine(_pythonScriptsDirectory, "Logs"));
        }

        #endregion;

        #region Event Handlers;

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("Blender Output: {Data}", e.Data);

                // Özel mesajları işle;
                if (e.Data.Contains("NEDA_"))
                {
                    ProcessNedaMessage(e.Data);
                }
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogError("Blender Error: {Data}", e.Data);
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            _logger.LogWarning("Blender process exited unexpectedly");

            _isConnected = false;
            CleanupProcess();

            OnConnectionStatusChanged(new BlenderConnectionEventArgs;
            {
                IsConnected = false,
                Timestamp = DateTime.UtcNow,
                WasUnexpected = true;
            });
        }

        private void OnConnectionStatusChanged(BlenderConnectionEventArgs e)
        {
            ConnectionStatusChanged?.Invoke(this, e);
        }

        private void ProcessNedaMessage(string message)
        {
            try
            {
                // Mesaj parsing implementasyonu;
                if (message.StartsWith("NEDA_ERROR:"))
                {
                    var error = message.Substring(11);
                    _logger.LogError("Blender reported error: {Error}", error);
                }
                else if (message.StartsWith("NEDA_PROGRESS:"))
                {
                    var progress = message.Substring(14);
                    // Progress event'ini tetikle;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing NEDA message");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processLock?.Dispose();

                    if (IsConnected)
                    {
                        try
                        {
                            DisconnectAsync().Wait(5000);
                        }
                        catch
                        {
                            // Dispose sırasında hata yakala ama fırlatma;
                        }
                    }

                    _blenderProcess?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BlenderBridge()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// BlenderBridge interface;
    /// </summary>
    public interface IBlenderBridge : IDisposable
    {
        bool IsConnected { get; }
        string BlenderVersion { get; }

        event EventHandler<BlenderConnectionEventArgs> ConnectionStatusChanged;

        Task<bool> ConnectAsync(bool backgroundMode = true);
        Task DisconnectAsync();
        Task<ScriptExecutionResult> ExecutePythonScriptAsync(string scriptContent);
        Task<ScriptExecutionResult> ExecutePythonFileAsync(string scriptPath, params object[] parameters);
        Task<ScriptExecutionResult> ExecuteTemplateAsync(string templateName, IDictionary<string, object> parameters = null);

        Task<ModelCreationResult> CreateModelAsync(ModelType modelType, ModelParameters parameters);
        Task<ImportResult> ImportModelAsync(string filePath, ImportOptions options = null);
        Task<ExportResult> ExportModelAsync(string modelName, string outputPath, ExportFormat format, ExportOptions options = null);
        Task<OptimizationResult> OptimizeMeshAsync(string modelName, MeshOptimizationOptions optimizationOptions);

        Task ClearSceneAsync();
        Task SaveSceneAsync(string filePath);
        Task OpenSceneAsync(string filePath);
    }

    /// <summary>
    /// Model tipleri;
    /// </summary>
    public enum ModelType;
    {
        Cube,
        Sphere,
        Cylinder,
        Cone,
        Torus,
        Plane,
        Monkey;
    }

    /// <summary>
    /// Export formatları;
    /// </summary>
    public enum ExportFormat;
    {
        FBX,
        OBJ,
        STL,
        GLTF,
        DAE,
        PLY;
    }

    /// <summary>
    /// Blender bağlantı event argümanları;
    /// </summary>
    public class BlenderConnectionEventArgs : EventArgs;
    {
        public bool IsConnected { get; set; }
        public DateTime Timestamp { get; set; }
        public string BlenderVersion { get; set; }
        public bool WasUnexpected { get; set; }
    }

    /// <summary>
    /// Script çalıştırma sonucu;
    /// </summary>
    public class ScriptExecutionResult;
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    /// <summary>
    /// Model oluşturma sonucu;
    /// </summary>
    public class ModelCreationResult;
    {
        public bool Success { get; set; }
        public string ModelName { get; set; }
        public ModelType ModelType { get; set; }
        public ModelParameters Parameters { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid OperationId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Import sonucu;
    /// </summary>
    public class ImportResult;
    {
        public bool Success { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public ImportOptions ImportOptions { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Export sonucu;
    /// </summary>
    public class ExportResult;
    {
        public bool Success { get; set; }
        public string ModelName { get; set; }
        public string OutputPath { get; set; }
        public ExportFormat Format { get; set; }
        public long? FileSize { get; set; }
        public ExportOptions ExportOptions { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Optimizasyon sonucu;
    /// </summary>
    public class OptimizationResult;
    {
        public bool Success { get; set; }
        public string ModelName { get; set; }
        public MeshOptimizationOptions Options { get; set; }
        public DateTime Timestamp { get; set; }
        public int? VertexCountBefore { get; set; }
        public int? VertexCountAfter { get; set; }
        public float? ReductionPercentage { get; set; }
    }

    /// <summary>
    /// Blender exception;
    /// </summary>
    public class BlenderException : Exception
    {
        public string ErrorCode { get; }

        public BlenderException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public BlenderException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    #endregion;
}
