using NEDA.API.Middleware;
using NEDA.ContentCreation.Common;
using NEDA.ContentCreation.Exceptions;
using NEDA.Core.Common.Extensions;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using static NEDA.Brain.NLP_Engine.SyntaxAnalysis.GrammarEngine;

namespace NEDA.ContentCreation.3DModeling.MayaIntegration;
{
    /// <summary>
    /// Maya DCC yazılımı ile entegrasyon sağlayan bağlantı sınıfı.
    /// Maya'nın COM otomasyonu ve Python scripting API'si üzerinden çalışır.
    /// </summary>
    public class MayaConnector : IMayaConnector, IDisposable;
{
    private readonly ILogger _logger;
    private readonly IErrorReporter _errorReporter;
    private readonly MayaConnectionSettings _settings;
    private Process _mayaProcess;
    private dynamic _mayaComObject;
    private bool _isConnected;
    private bool _isInitialized;
    private readonly string _pythonScriptsPath;
    private readonly Dictionary<string, string> _commandScripts;
    private readonly object _syncLock = new object();

    /// <summary>
    /// Maya bağlantı durumu event'i;
    /// </summary>
    public event EventHandler<ConnectionStatusChangedEventArgs> ConnectionStatusChanged;

    /// <summary>
    /// Komut yürütme tamamlandığında tetiklenen event;
    /// </summary>
    public event EventHandler<CommandCompletedEventArgs> CommandCompleted;

    /// <summary>
    /// Maya'dan gelen çıktılar için event;
    /// </summary>
    public event EventHandler<MayaOutputEventArgs> OutputReceived;

    /// <summary>
    /// MayaConnector sınıfı constructor'ı;
    /// </summary>
    /// <param name="logger">Loglama servisi</param>
    /// <param name="errorReporter">Hata raporlama servisi</param>
    /// <param name="settings">Maya bağlantı ayarları</param>
    public MayaConnector(ILogger logger, IErrorReporter errorReporter, MayaConnectionSettings settings)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _pythonScriptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "Scripts", "Maya");

        _commandScripts = new Dictionary<string, string>();
        InitializeCommandScripts();

        _logger.Info("MayaConnector initialized with settings: {@Settings}",
            new { settings.MayaExecutablePath, settings.AutoConnect, settings.TimeoutSeconds });
    }

    /// <summary>
    /// Maya'ya bağlanır;
    /// </summary>
    /// <returns>Bağlantı başarılı ise true</returns>
    public async Task<bool> ConnectAsync()
    {
        try
        {
            lock (_syncLock)
            {
                if (_isConnected)
                {
                    _logger.Warning("Maya is already connected.");
                    return true;
                }

                if (!File.Exists(_settings.MayaExecutablePath))
                {
                    throw new MayaConnectionException(
                        $"Maya executable not found at path: {_settings.MayaExecutablePath}",
                        ErrorCodes.Maya.ExecutableNotFound);
                }

                _logger.Info("Starting Maya connection process...");

                // Maya prosesini başlat;
                StartMayaProcess();

                // COM bağlantısını kur;
                InitializeComConnection();

                // Python ortamını hazırla;
                InitializePythonEnvironment();

                _isConnected = true;
                _isInitialized = true;

                OnConnectionStatusChanged(new ConnectionStatusChangedEventArgs;
                {
                    IsConnected = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Maya connected successfully"
                });

                _logger.Info("Maya connection established successfully.");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to connect to Maya.");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High,
                "Maya Connection Failure");
            throw new MayaConnectionException("Failed to connect to Maya", ex);
        }
    }

    /// <summary>
    /// Maya'dan bağlantıyı keser;
    /// </summary>
    public async Task DisconnectAsync()
    {
        try
        {
            lock (_syncLock)
            {
                if (!_isConnected)
                {
                    _logger.Warning("Maya is not connected.");
                    return;
                }

                _logger.Info("Disconnecting from Maya...");

                // Python scriptlerini temizle;
                ExecutePythonCommand("import maya.cmds as cmds\ncmds.scriptEditorInfo(clearHistory=True)");

                // COM bağlantısını kapat;
                if (_mayaComObject != null)
                {
                    try
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(_mayaComObject);
                        _mayaComObject = null;
                    }
                    catch (Exception comEx)
                    {
                        _logger.Warning(comEx, "Error releasing COM object");
                    }
                }

                // Prosesi kapat;
                if (_mayaProcess != null && !_mayaProcess.HasExited)
                {
                    try
                    {
                        _mayaProcess.CloseMainWindow();

                        if (!_mayaProcess.WaitForExit(5000))
                        {
                            _mayaProcess.Kill();
                        }
                    }
                    catch (Exception procEx)
                    {
                        _logger.Warning(procEx, "Error closing Maya process");
                    }
                    finally
                    {
                        _mayaProcess.Dispose();
                        _mayaProcess = null;
                    }
                }

                _isConnected = false;
                _isInitialized = false;

                OnConnectionStatusChanged(new ConnectionStatusChangedEventArgs;
                {
                    IsConnected = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Maya disconnected"
                });

                _logger.Info("Maya disconnected successfully.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error disconnecting from Maya.");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                "Maya Disconnection Error");
            throw;
        }
    }

    /// <summary>
    /// Maya sahnesini açar;
    /// </summary>
    /// <param name="filePath">Sahne dosya yolu</param>
    /// <param name="force">Zorla aç (mevcut değişiklikleri kaydetme)</param>
    public async Task<bool> OpenSceneAsync(string filePath, bool force = false)
    {
        ValidateConnection();
        ValidateFilePath(filePath);

        try
        {
            _logger.Info($"Opening Maya scene: {filePath}");

            string pythonScript = _commandScripts.ContainsKey("OpenScene")
                ? _commandScripts["OpenScene"]
                : GenerateOpenSceneScript(filePath, force);

            var result = await ExecutePythonCommandAsync(pythonScript);

            if (result.Success)
            {
                _logger.Info($"Scene opened successfully: {filePath}");

                OnCommandCompleted(new CommandCompletedEventArgs;
                {
                    CommandName = "OpenScene",
                    FilePath = filePath,
                    Success = true,
                    ExecutionTime = result.ExecutionTime;
                });

                return true;
            }
            else;
            {
                throw new MayaCommandException($"Failed to open scene: {result.ErrorMessage}",
                    ErrorCodes.Maya.SceneOpenFailed);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Failed to open scene: {filePath}");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High,
                $"Scene Open Failed: {Path.GetFileName(filePath)}");
            throw;
        }
    }

    /// <summary>
    /// Maya sahnesini kaydeder;
    /// </summary>
    /// <param name="filePath">Kaydedilecek dosya yolu</param>
    /// <param name="incrementalSave">Artımlı kaydetme yap</param>
    public async Task<bool> SaveSceneAsync(string filePath, bool incrementalSave = false)
    {
        ValidateConnection();
        ValidateDirectory(Path.GetDirectoryName(filePath));

        try
        {
            _logger.Info($"Saving Maya scene to: {filePath}");

            string pythonScript = _commandScripts.ContainsKey("SaveScene")
                ? _commandScripts["SaveScene"]
                : GenerateSaveSceneScript(filePath, incrementalSave);

            var result = await ExecutePythonCommandAsync(pythonScript);

            if (result.Success)
            {
                _logger.Info($"Scene saved successfully: {filePath}");

                OnCommandCompleted(new CommandCompletedEventArgs;
                {
                    CommandName = "SaveScene",
                    FilePath = filePath,
                    Success = true,
                    ExecutionTime = result.ExecutionTime;
                });

                return true;
            }
            else;
            {
                throw new MayaCommandException($"Failed to save scene: {result.ErrorMessage}",
                    ErrorCodes.Maya.SceneSaveFailed);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Failed to save scene: {filePath}");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High,
                $"Scene Save Failed: {Path.GetFileName(filePath)}");
            throw;
        }
    }

    /// <summary>
    /// OBJ formatında modeli Maya'ya aktarır;
    /// </summary>
    /// <param name="objFilePath">OBJ dosya yolu</param>
    /// <param name="importOptions">İçe aktarma seçenekleri</param>
    public async Task<bool> ImportObjAsync(string objFilePath, ObjImportOptions importOptions = null)
    {
        ValidateConnection();
        ValidateFilePath(objFilePath);

        try
        {
            _logger.Info($"Importing OBJ file: {objFilePath}");

            importOptions = importOptions ?? new ObjImportOptions();
            string pythonScript = GenerateObjImportScript(objFilePath, importOptions);

            var result = await ExecutePythonCommandAsync(pythonScript);

            if (result.Success)
            {
                _logger.Info($"OBJ imported successfully: {objFilePath}");

                OnCommandCompleted(new CommandCompletedEventArgs;
                {
                    CommandName = "ImportObj",
                    FilePath = objFilePath,
                    Success = true,
                    ExecutionTime = result.ExecutionTime,
                    AdditionalData = importOptions;
                });

                return true;
            }
            else;
            {
                throw new MayaCommandException($"Failed to import OBJ: {result.ErrorMessage}",
                    ErrorCodes.Maya.ImportFailed);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Failed to import OBJ: {objFilePath}");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                $"OBJ Import Failed: {Path.GetFileName(objFilePath)}");
            throw;
        }
    }

    /// <summary>
    /// Maya sahnesini OBJ formatında dışa aktarır;
    /// </summary>
    /// <param name="exportPath">Dışa aktarma yolu</param>
    /// <param name="exportOptions">Dışa aktarma seçenekleri</param>
    public async Task<bool> ExportObjAsync(string exportPath, ObjExportOptions exportOptions = null)
    {
        ValidateConnection();
        ValidateDirectory(Path.GetDirectoryName(exportPath));

        try
        {
            _logger.Info($"Exporting to OBJ: {exportPath}");

            exportOptions = exportOptions ?? new ObjExportOptions();
            string pythonScript = GenerateObjExportScript(exportPath, exportOptions);

            var result = await ExecutePythonCommandAsync(pythonScript);

            if (result.Success)
            {
                _logger.Info($"OBJ exported successfully: {exportPath}");

                OnCommandCompleted(new CommandCompletedEventArgs;
                {
                    CommandName = "ExportObj",
                    FilePath = exportPath,
                    Success = true,
                    ExecutionTime = result.ExecutionTime,
                    AdditionalData = exportOptions;
                });

                return true;
            }
            else;
            {
                throw new MayaCommandException($"Failed to export OBJ: {result.ErrorMessage}",
                    ErrorCodes.Maya.ExportFailed);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Failed to export OBJ: {exportPath}");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                $"OBJ Export Failed: {Path.GetFileName(exportPath)}");
            throw;
        }
    }

    /// <summary>
    /// Python komutunu Maya'da yürütür;
    /// </summary>
    /// <param name="pythonCode">Python kodu</param>
    public async Task<PythonExecutionResult> ExecutePythonCommandAsync(string pythonCode)
    {
        ValidateConnection();

        try
        {
            var stopwatch = Stopwatch.StartNew();

            _logger.Debug($"Executing Python command: {pythonCode.Truncate(200)}");

            // COM üzerinden Python komutunu yürüt;
            string result = await Task.Run(() =>
            {
                try
                {
                    dynamic resultObj = _mayaComObject.CommandPort(
                        pythonCode,
                        "python",
                        "",
                        "",
                        false,
                        true;
                    );

                    return resultObj?.ToString() ?? string.Empty;
                }
                catch (Exception comEx)
                {
                    _logger.Error(comEx, "COM error executing Python command");
                    throw new MayaCommandException("COM execution failed", comEx);
                }
            });

            stopwatch.Stop();

            var executionResult = new PythonExecutionResult;
            {
                Success = !string.IsNullOrEmpty(result) && !result.Contains("Error:"),
                Output = result,
                ExecutionTime = stopwatch.Elapsed,
                Command = pythonCode;
            };

            if (!executionResult.Success)
            {
                executionResult.ErrorMessage = ExtractErrorMessage(result);
                _logger.Error($"Python command failed: {executionResult.ErrorMessage}");
            }

            OnOutputReceived(new MayaOutputEventArgs;
            {
                Output = result,
                IsError = !executionResult.Success,
                Timestamp = DateTime.UtcNow,
                Command = pythonCode;
            });

            return executionResult;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to execute Python command");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                "Python Command Execution Failed");
            throw;
        }
    }

    /// <summary>
    /// Maya'dan sahne bilgilerini alır;
    /// </summary>
    public async Task<SceneInfo> GetSceneInfoAsync()
    {
        ValidateConnection();

        try
        {
            _logger.Debug("Getting scene information from Maya");

            string pythonScript = @"
import maya.cmds as cmds;
import json;

scene_info = {
    'name': cmds.file(query=True, sceneName=True, shortName=True) or 'Untitled',
    'path': cmds.file(query=True, sceneName=True) or '',
    'modified': cmds.file(query=True, modified=True),
    'objects': len(cmds.ls(transforms=True)),
    'polygons': cmds.polyEvaluate(all=True),
    'cameras': len(cmds.ls(cameras=True)),
    'lights': len(cmds.ls(lights=True)),
    'renderer': cmds.getAttr('defaultRenderGlobals.currentRenderer'),
    'units': cmds.currentUnit(query=True, linear=True),
    'fps': cmds.currentUnit(query=True, time=True)
}

print(json.dumps(scene_info))
";

            var result = await ExecutePythonCommandAsync(pythonScript);

            if (result.Success)
            {
                try
                {
                    var jsonOutput = ExtractJsonFromOutput(result.Output);
                    var sceneInfo = JsonConvert.DeserializeObject<SceneInfo>(jsonOutput);

                    _logger.Info($"Scene info retrieved: {sceneInfo.Name}");
                    return sceneInfo;
                }
                catch (JsonException jsonEx)
                {
                    _logger.Error(jsonEx, "Failed to parse scene info JSON");
                    throw new MayaDataException("Invalid scene info data format", jsonEx);
                }
            }
            else;
            {
                throw new MayaCommandException("Failed to get scene info",
                    ErrorCodes.Maya.DataRetrievalFailed);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get scene information");
            await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low,
                "Scene Info Retrieval Failed");
            throw;
        }
    }

    /// <summary>
    /// Bağlantı durumunu kontrol eder;
    /// </summary>
    public bool IsConnected => _isConnected && _isInitialized;

    /// <summary>
    /// Maya sürüm bilgisini alır;
    /// </summary>
    public async Task<string> GetMayaVersionAsync()
    {
        ValidateConnection();

        try
        {
            var result = await ExecutePythonCommandAsync(
                "import maya.cmds as cmds\nprint(cmds.about(version=True))");

            if (result.Success && !string.IsNullOrEmpty(result.Output))
            {
                return result.Output.Trim();
            }

            return "Unknown";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get Maya version");
            return "Error";
        }
    }

    #region Private Methods;

    private void StartMayaProcess()
    {
        var processInfo = new ProcessStartInfo;
        {
            FileName = _settings.MayaExecutablePath,
            Arguments = _settings.CommandLineArguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_settings.MayaExecutablePath)
        };

        _mayaProcess = new Process;
        {
            StartInfo = processInfo,
            EnableRaisingEvents = true;
        };

        _mayaProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                OnOutputReceived(new MayaOutputEventArgs;
                {
                    Output = e.Data,
                    IsError = false,
                    Timestamp = DateTime.UtcNow,
                    Source = "MayaProcess"
                });
            }
        };

        _mayaProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.Error($"Maya process error: {e.Data}");
                OnOutputReceived(new MayaOutputEventArgs;
                {
                    Output = e.Data,
                    IsError = true,
                    Timestamp = DateTime.UtcNow,
                    Source = "MayaProcess"
                });
            }
        };

        _mayaProcess.Start();
        _mayaProcess.BeginOutputReadLine();
        _mayaProcess.BeginErrorReadLine();

        // Maya'nın başlaması için kısa bir bekleme;
        Task.Delay(3000).Wait();

        _logger.Info($"Maya process started (PID: {_mayaProcess.Id})");
    }

    private void InitializeComConnection()
    {
        try
        {
            Type mayaType = Type.GetTypeFromProgID("Maya.Application");

            if (mayaType == null)
            {
                throw new MayaConnectionException(
                    "Maya COM interface not found. Make sure Maya is installed.",
                    ErrorCodes.Maya.ComInterfaceNotFound);
            }

            _mayaComObject = Activator.CreateInstance(mayaType);

            if (_mayaComObject == null)
            {
                throw new MayaConnectionException(
                    "Failed to create Maya COM object instance",
                    ErrorCodes.Maya.ComObjectCreationFailed);
            }

            _logger.Info("Maya COM connection established successfully");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize COM connection");
            throw new MayaConnectionException("COM connection initialization failed", ex);
        }
    }

    private void InitializePythonEnvironment()
    {
        try
        {
            // Python script dosyalarını yükle;
            LoadPythonScripts();

            // Maya Python ortamını hazırla;
            string initScript = @"
import sys;
import os;
import json;
import maya.cmds as cmds;
import maya.mel as mel;

print('Maya Python environment initialized')
";

            ExecutePythonCommand(initScript);

            _logger.Info("Maya Python environment initialized");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize Python environment");
            throw new MayaConnectionException("Python environment initialization failed", ex);
        }
    }

    private void InitializeCommandScripts()
    {
        _commandScripts["OpenScene"] = @"
import maya.cmds as cmds;
import sys;

def open_scene(file_path, force=False):
    try:
        cmds.file(file_path, open=True, force=force, prompt=False)
        return True, 'Scene opened successfully'
    except Exception as e:
        return False, str(e)
";

        _commandScripts["SaveScene"] = @"
import maya.cmds as cmds;
import os;

def save_scene(file_path, incremental=False):
    try:
        if incremental:
            cmds.file(save=True, type='mayaAscii')
        else:
            cmds.file(rename=file_path)
            cmds.file(save=True, type='mayaAscii', force=True)
        return True, 'Scene saved successfully'
    except Exception as e:
        return False, str(e)
";
    }

    private void LoadPythonScripts()
    {
        if (!Directory.Exists(_pythonScriptsPath))
        {
            _logger.Warning($"Python scripts directory not found: {_pythonScriptsPath}");
            return;
        }

        try
        {
            var scriptFiles = Directory.GetFiles(_pythonScriptsPath, "*.py");

            foreach (var scriptFile in scriptFiles)
            {
                try
                {
                    string scriptName = Path.GetFileNameWithoutExtension(scriptFile);
                    string scriptContent = File.ReadAllText(scriptFile);

                    // Scripti Maya'ya yükle;
                    ExecutePythonCommand(scriptContent);

                    _logger.Debug($"Loaded Python script: {scriptName}");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, $"Failed to load script: {scriptFile}");
                }
            }

            _logger.Info($"Loaded {scriptFiles.Length} Python scripts");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading Python scripts");
        }
    }

    private void ValidateConnection()
    {
        if (!_isConnected || !_isInitialized)
        {
            throw new MayaConnectionException(
                "Maya is not connected. Call ConnectAsync() first.",
                ErrorCodes.Maya.NotConnected);
        }
    }

    private void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }
    }

    private void ValidateDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be null or empty", nameof(directoryPath));
        }

        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
            _logger.Info($"Created directory: {directoryPath}");
        }
    }

    private string GenerateOpenSceneScript(string filePath, bool force)
    {
        return $@"
import maya.cmds as cmds;

try:
    cmds.file('{filePath.Replace("\\", "\\\\")}', 
              open=True, 
              force={force.ToString().ToLower()}, 
              prompt=False)
    print('SUCCESS: Scene opened')
except Exception as e:
    print(f'ERROR: {{str(e)}}')
";
    }

    private string GenerateSaveSceneScript(string filePath, bool incrementalSave)
    {
        return $@"
import maya.cmds as cmds;
import os;

try:
    if {incrementalSave.ToString().ToLower()}:
        cmds.file(save=True, type='mayaAscii')
    else:
        cmds.file(rename='{filePath.Replace("\\", "\\\\")}')
        cmds.file(save=True, type='mayaAscii', force=True)
    
    print('SUCCESS: Scene saved')
except Exception as e:
    print(f'ERROR: {{str(e)}}')
";
    }

    private string GenerateObjImportScript(string objFilePath, ObjImportOptions options)
    {
        return $@"
import maya.cmds as cmds;

try:
    cmds.file('{objFilePath.Replace("\\", "\\\\")}', 
              i=True, 
              type='OBJ',
              ignoreVersion=True,
              mergeNamespacesOnClash={options.MergeNamespaces.ToString().ToLower()},
              namespace='{options.Namespace}')
    
    if {options.CreateMaterials.ToString().ToLower()}:
        # Material creation logic;
        pass;
    
    print('SUCCESS: OBJ imported')
except Exception as e:
    print(f'ERROR: {{str(e)}}')
";
    }

    private string GenerateObjExportScript(string exportPath, ObjExportOptions options)
    {
        return $@"
import maya.cmds as cmds;

try:
    selection = cmds.ls(selection=True) or cmds.ls(geometry=True)
    
    cmds.file('{exportPath.Replace("\\", "\\\\")}', 
              exportSelected={options.ExportSelected.ToString().ToLower()},
              type='OBJexport',
              force=True,
              options='groups={options.ExportGroups.ToString().ToLower()}; 
                       ptgroups={options.ExportPointGroups.ToString().ToLower()};
                       materials={options.ExportMaterials.ToString().ToLower()};
                       smoothing={options.SmoothingGroups.ToString().ToLower()};
                       normals={options.ExportNormals.ToString().ToLower()}')
    
    print('SUCCESS: OBJ exported')
except Exception as e:
    print(f'ERROR: {{str(e)}}')
";
    }

    private string ExtractErrorMessage(string output)
    {
        if (string.IsNullOrEmpty(output))
            return "Unknown error";

        if (output.Contains("ERROR:"))
        {
            int errorIndex = output.IndexOf("ERROR:");
            return output.Substring(errorIndex + 7).Trim();
        }

        return output.Trim();
    }

    private string ExtractJsonFromOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
            return "{}";

        int jsonStart = output.IndexOf('{');
        int jsonEnd = output.LastIndexOf('}');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            return output.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        return "{}";
    }

    private void OnConnectionStatusChanged(ConnectionStatusChangedEventArgs e)
    {
        ConnectionStatusChanged?.Invoke(this, e);
    }

    private void OnCommandCompleted(CommandCompletedEventArgs e)
    {
        CommandCompleted?.Invoke(this, e);
    }

    private void OnOutputReceived(MayaOutputEventArgs e)
    {
        OutputReceived?.Invoke(this, e);
    }

    private void ExecutePythonCommand(string pythonCode)
    {
        try
        {
            _mayaComObject.CommandPort(pythonCode, "python", "", "", false, true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Sync Python command execution failed");
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
                // Managed resources;
                if (_isConnected)
                {
                    try
                    {
                        DisconnectAsync().Wait(5000);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error during disposal disconnect");
                    }
                }
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~MayaConnector()
    {
        Dispose(false);
    }

    #endregion;
}

#region Supporting Classes and Interfaces;

/// <summary>
/// Maya bağlantı ayarları;
/// </summary>
public class MayaConnectionSettings;
{
    public string MayaExecutablePath { get; set; } = @"C:\Program Files\Autodesk\Maya2023\bin\maya.exe";
    public string CommandLineArguments { get; set; } = "";
    public bool AutoConnect { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public string PythonPath { get; set; } = "";
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// OBJ içe aktarma seçenekleri;
/// </summary>
public class ObjImportOptions;
{
    public bool MergeNamespaces { get; set; } = true;
    public string Namespace { get; set; } = "imported";
    public bool CreateMaterials { get; set; } = true;
    public bool ImportNormals { get; set; } = true;
    public bool ImportUVs { get; set; } = true;
    public float ScaleFactor { get; set; } = 1.0f;
}

/// <summary>
/// OBJ dışa aktarma seçenekleri;
/// </summary>
public class ObjExportOptions;
{
    public bool ExportSelected { get; set; } = false;
    public bool ExportGroups { get; set; } = true;
    public bool ExportPointGroups { get; set; } = false;
    public bool ExportMaterials { get; set; } = true;
    public bool ExportNormals { get; set; } = true;
    public bool SmoothingGroups { get; set; } = true;
    public bool Triangulate { get; set; } = false;
}

/// <summary>
/// Sahne bilgileri;
/// </summary>
public class SceneInfo;
{
    public string Name { get; set; }
    public string Path { get; set; }
    public bool Modified { get; set; }
    public int ObjectCount { get; set; }
    public int PolygonCount { get; set; }
    public int CameraCount { get; set; }
    public int LightCount { get; set; }
    public string Renderer { get; set; }
    public string Units { get; set; }
    public string FPS { get; set; }
}

/// <summary>
/// Python yürütme sonucu;
/// </summary>
public class PythonExecutionResult;
{
    public bool Success { get; set; }
    public string Output { get; set; }
    public string ErrorMessage { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public string Command { get; set; }
}

/// <summary>
/// Bağlantı durumu değişiklik event argümanları;
/// </summary>
public class ConnectionStatusChangedEventArgs : EventArgs;
{
    public bool IsConnected { get; set; }
    public DateTime Timestamp { get; set; }
    public string Message { get; set; }
}

/// <summary>
/// Komut tamamlanma event argümanları;
/// </summary>
public class CommandCompletedEventArgs : EventArgs;
{
    public string CommandName { get; set; }
    public string FilePath { get; set; }
    public bool Success { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public object AdditionalData { get; set; }
}

/// <summary>
/// Maya çıktı event argümanları;
/// </summary>
public class MayaOutputEventArgs : EventArgs;
{
    public string Output { get; set; }
    public bool IsError { get; set; }
    public DateTime Timestamp { get; set; }
    public string Command { get; set; }
    public string Source { get; set; }
}

/// <summary>
/// Maya bağlayıcı interface'i;
/// </summary>
public interface IMayaConnector;
{
    Task<bool> ConnectAsync();
    Task DisconnectAsync();
    Task<bool> OpenSceneAsync(string filePath, bool force = false);
    Task<bool> SaveSceneAsync(string filePath, bool incrementalSave = false);
    Task<bool> ImportObjAsync(string objFilePath, ObjImportOptions importOptions = null);
    Task<bool> ExportObjAsync(string exportPath, ObjExportOptions exportOptions = null);
    Task<PythonExecutionResult> ExecutePythonCommandAsync(string pythonCode);
    Task<SceneInfo> GetSceneInfoAsync();
    Task<string> GetMayaVersionAsync();
    bool IsConnected { get; }

    event EventHandler<ConnectionStatusChangedEventArgs> ConnectionStatusChanged;
    event EventHandler<CommandCompletedEventArgs> CommandCompleted;
    event EventHandler<MayaOutputEventArgs> OutputReceived;
}

#endregion;
}
