using NEDA.Automation.Executors;
using NEDA.Common.Utilities;
using NEDA.EngineIntegration.PluginSystem.PluginManager;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.MediaProcessing.ImageProcessing.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.MediaProcessing.ImageProcessing.GIMPAutomation;
{
    /// <summary>
    /// GIMP otomasyonu ve entegrasyonu sağlayan bağlayıcı sınıf;
    /// </summary>
    public class GIMPConnector : IGIMPConnector, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IScriptRunner _scriptRunner;
        private readonly IPluginManager _pluginManager;

        private Process _gimpProcess;
        private bool _isConnected;
        private string _gimpExecutablePath;
        private readonly List<GIMPCommand> _commandQueue;
        private readonly object _processLock = new object();
        private readonly GIMPSession _currentSession;
        private readonly Dictionary<string, object> _environmentVariables;

        private const string GIMP_DEFAULT_WINDOWS_PATH = @"C:\Program Files\GIMP 2\bin\gimp-console-2.10.exe";
        private const string GIMP_DEFAULT_MAC_PATH = "/Applications/GIMP.app/Contents/MacOS/GIMP";
        private const string GIMP_DEFAULT_LINUX_PATH = "/usr/bin/gimp";
        private const int DEFAULT_TIMEOUT_MS = 30000;
        private const int SCRIPT_EXECUTION_TIMEOUT = 60000;

        #endregion;

        #region Properties;

        /// <summary>
        /// GIMP bağlantı durumu;
        /// </summary>
        public bool IsConnected => _isConnected && _gimpProcess != null && !_gimpProcess.HasExited;

        /// <summary>
        /// GIMP sürüm bilgisi;
        /// </summary>
        public string Version { get; private set; }

        /// <summary>
        /// GIMP çalıştırılabilir dosya yolu;
        /// </summary>
        public string GIMPExecutablePath;
        {
            get => _gimpExecutablePath;
            set;
            {
                if (!string.IsNullOrEmpty(value) && File.Exists(value))
                {
                    _gimpExecutablePath = value;
                }
            }
        }

        /// <summary>
        /// Zaman aşımı süresi (milisaniye)
        /// </summary>
        public int Timeout { get; set; } = DEFAULT_TIMEOUT_MS;

        /// <summary>
        /// Çalışan GIMP işleminin ID'si;
        /// </summary>
        public int ProcessId => _gimpProcess?.Id ?? -1;

        /// <summary>
        /// Kuyruktaki komut sayısı;
        /// </summary>
        public int QueuedCommands => _commandQueue.Count;

        /// <summary>
        /// Kullanılabilir GIMP eklentileri;
        /// </summary>
        public List<GIMPPlugin> AvailablePlugins { get; private set; }

        /// <summary>
        /// GIMP çalışma dizini;
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// Batch modunda çalışıyor mu?
        /// </summary>
        public bool IsBatchMode { get; private set; }

        /// <summary>
        /// Hata ayıklama modu;
        /// </summary>
        public bool DebugMode { get; set; }

        #endregion;

        #region Events;

        /// <summary>
        /// GIMP bağlantısı kurulduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<GIMPConnectedEventArgs> Connected;

        /// <summary>
        /// GIMP bağlantısı kesildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<GIMPDisconnectedEventArgs> Disconnected;

        /// <summary>
        /// Komut yürütüldüğünde tetiklenen event;
        /// </summary>
        public event EventHandler<GIMPCommandExecutedEventArgs> CommandExecuted;

        /// <summary>
        /// Script çalıştırıldığında tetiklenen event;
        /// </summary>
        public event EventHandler<GIMPScriptExecutedEventArgs> ScriptExecuted;

        /// <summary>
        /// GIMP'ten gelen çıktı alındığında tetiklenen event;
        /// </summary>
        public event EventHandler<GIMPOutputReceivedEventArgs> OutputReceived;

        #endregion;

        #region Constructor;

        /// <summary>
        /// GIMPConnector sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public GIMPConnector(
            ILogger logger,
            IErrorHandler errorHandler,
            IScriptRunner scriptRunner,
            IPluginManager pluginManager = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _scriptRunner = scriptRunner ?? throw new ArgumentNullException(nameof(scriptRunner));
            _pluginManager = pluginManager;

            _commandQueue = new List<GIMPCommand>();
            _environmentVariables = new Dictionary<string, object>();
            _currentSession = new GIMPSession();
            AvailablePlugins = new List<GIMPPlugin>();

            // Varsayılan GIMP yolunu tespit et;
            DetectGIMPExecutable();

            _logger.Info("GIMPConnector initialized", GetType().Name);
        }

        #endregion;

        #region Connection Methods;

        /// <summary>
        /// GIMP'e bağlanır;
        /// </summary>
        /// <param name="batchMode">Batch modunda bağlan</param>
        /// <returns>Bağlantı başarılı mı?</returns>
        public async Task<bool> ConnectAsync(bool batchMode = true)
        {
            if (IsConnected)
            {
                _logger.Warning("Already connected to GIMP", GetType().Name);
                return true;
            }

            try
            {
                lock (_processLock)
                {
                    if (string.IsNullOrEmpty(_gimpExecutablePath))
                    {
                        throw new FileNotFoundException("GIMP executable not found. Please install GIMP or specify the path.");
                    }

                    if (!File.Exists(_gimpExecutablePath))
                    {
                        throw new FileNotFoundException($"GIMP executable not found at: {_gimpExecutablePath}");
                    }

                    // GIMP işlemini başlat;
                    var startInfo = new ProcessStartInfo;
                    {
                        FileName = _gimpExecutablePath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                        ErrorDialog = false;
                    };

                    if (batchMode)
                    {
                        startInfo.Arguments = "--batch-interpreter python-fu-eval -b";
                        IsBatchMode = true;
                    }

                    if (!string.IsNullOrEmpty(WorkingDirectory))
                    {
                        startInfo.WorkingDirectory = WorkingDirectory;
                    }

                    _gimpProcess = new Process;
                    {
                        StartInfo = startInfo,
                        EnableRaisingEvents = true;
                    };

                    // Çıktı yakalama event'leri;
                    _gimpProcess.OutputDataReceived += OnOutputDataReceived;
                    _gimpProcess.ErrorDataReceived += OnErrorDataReceived;
                    _gimpProcess.Exited += OnProcessExited;

                    _gimpProcess.Start();
                    _gimpProcess.BeginOutputReadLine();
                    _gimpProcess.BeginErrorReadLine();

                    _isConnected = true;
                    _currentSession.StartTime = DateTime.Now;
                    _currentSession.SessionId = Guid.NewGuid();

                    // GIMP sürümünü al;
                    Task.Run(async () => await GetGIMPVersionAsync()).Wait(5000);

                    // Mevcut eklentileri yükle;
                    Task.Run(async () => await LoadAvailablePluginsAsync()).Wait(10000);
                }

                _logger.Info($"Connected to GIMP (PID: {ProcessId}, Batch Mode: {batchMode})", GetType().Name);

                OnConnected(new GIMPConnectedEventArgs;
                {
                    SessionId = _currentSession.SessionId,
                    ProcessId = ProcessId,
                    Version = Version,
                    IsBatchMode = batchMode;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to connect to GIMP: {ex.Message}", GetType().Name);
                _errorHandler.HandleError(ex, ErrorCodes.GIMP_CONNECTION_FAILED);
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// GIMP bağlantısını keser;
        /// </summary>
        public void Disconnect()
        {
            lock (_processLock)
            {
                if (!IsConnected)
                {
                    return;
                }

                try
                {
                    // GIMP'ten çıkış komutunu gönder;
                    if (_gimpProcess != null && !_gimpProcess.HasExited)
                    {
                        if (IsBatchMode)
                        {
                            SendCommand("(gimp-quit 1)");
                        }

                        // İşlemi kapat;
                        if (!_gimpProcess.HasExited)
                        {
                            _gimpProcess.Kill();
                            Thread.Sleep(1000);
                        }

                        _gimpProcess.Close();
                        _gimpProcess.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Error while disconnecting from GIMP: {ex.Message}", GetType().Name);
                }
                finally
                {
                    _gimpProcess = null;
                    _isConnected = false;
                    _currentSession.EndTime = DateTime.Now;

                    _logger.Info("Disconnected from GIMP", GetType().Name);

                    OnDisconnected(new GIMPDisconnectedEventArgs;
                    {
                        SessionId = _currentSession.SessionId,
                        Duration = _currentSession.Duration,
                        TotalCommands = _currentSession.TotalCommands;
                    });
                }
            }
        }

        /// <summary>
        /// GIMP bağlantısını yeniden başlatır;
        /// </summary>
        public async Task<bool> RestartAsync()
        {
            Disconnect();
            await Task.Delay(2000); // Kısa bir bekleme süresi;
            return await ConnectAsync(IsBatchMode);
        }

        #endregion;

        #region Command Execution;

        /// <summary>
        /// GIMP komutunu yürütür;
        /// </summary>
        /// <param name="command">Yürütülecek komut</param>
        /// <param name="timeout">Zaman aşımı (ms)</param>
        /// <returns>Komut yanıtı</returns>
        public async Task<GIMPResponse> ExecuteCommandAsync(GIMPCommand command, int? timeout = null)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (!IsConnected)
                throw new InvalidOperationException("Not connected to GIMP");

            var executionTimeout = timeout ?? Timeout;
            var cts = new CancellationTokenSource(executionTimeout);

            try
            {
                command.ExecutionStartTime = DateTime.Now;

                // Komutu kuyruğa ekle;
                lock (_commandQueue)
                {
                    _commandQueue.Add(command);
                }

                string response;

                if (command.CommandType == GIMPCommandType.Scheme)
                {
                    response = await ExecuteSchemeCommandAsync(command.Content, cts.Token);
                }
                else if (command.CommandType == GIMPCommandType.Python)
                {
                    response = await ExecutePythonCommandAsync(command.Content, cts.Token);
                }
                else;
                {
                    throw new NotSupportedException($"Command type {command.CommandType} is not supported");
                }

                command.ExecutionEndTime = DateTime.Now;
                command.Status = GIMPCommandStatus.Completed;
                command.Response = response;

                _currentSession.TotalCommands++;
                _currentSession.LastCommandTime = DateTime.Now;

                OnCommandExecuted(new GIMPCommandExecutedEventArgs;
                {
                    Command = command,
                    SessionId = _currentSession.SessionId,
                    ExecutionTime = command.ExecutionTime;
                });

                return new GIMPResponse;
                {
                    Success = true,
                    Result = response,
                    CommandId = command.Id,
                    ExecutionTime = command.ExecutionTime;
                };
            }
            catch (OperationCanceledException)
            {
                command.Status = GIMPCommandStatus.Timeout;
                command.Response = "Command execution timed out";

                _logger.Warning($"Command timed out: {command.Name}", GetType().Name);

                return new GIMPResponse;
                {
                    Success = false,
                    Error = "Command execution timed out",
                    CommandId = command.Id;
                };
            }
            catch (Exception ex)
            {
                command.Status = GIMPCommandStatus.Failed;
                command.Response = ex.Message;

                _logger.Error($"Command failed: {command.Name} - {ex.Message}", GetType().Name);

                return new GIMPResponse;
                {
                    Success = false,
                    Error = ex.Message,
                    CommandId = command.Id;
                };
            }
            finally
            {
                lock (_commandQueue)
                {
                    _commandQueue.Remove(command);
                }

                cts.Dispose();
            }
        }

        /// <summary>
        /// Scheme (Script-Fu) komutunu yürütür;
        /// </summary>
        private async Task<string> ExecuteSchemeCommandAsync(string schemeScript, CancellationToken cancellationToken)
        {
            if (!IsBatchMode)
            {
                throw new InvalidOperationException("Scheme commands require batch mode");
            }

            var fullCommand = $"-b \"{EscapeSchemeScript(schemeScript)}\"";
            return await SendProcessCommandAsync(fullCommand, cancellationToken);
        }

        /// <summary>
        /// Python-Fu komutunu yürütür;
        /// </summary>
        private async Task<string> ExecutePythonCommandAsync(string pythonScript, CancellationToken cancellationToken)
        {
            if (!IsBatchMode)
            {
                throw new InvalidOperationException("Python commands require batch mode");
            }

            // Python script'ini geçici dosyaya yaz;
            var tempScriptFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempScriptFile, pythonScript, cancellationToken);

                var command = $"-b \"import sys; exec(open(r'{tempScriptFile}').read())\"";
                return await SendProcessCommandAsync(command, cancellationToken);
            }
            finally
            {
                if (File.Exists(tempScriptFile))
                {
                    File.Delete(tempScriptFile);
                }
            }
        }

        /// <summary>
        /// Script dosyasını yürütür;
        /// </summary>
        public async Task<GIMPResponse> ExecuteScriptFileAsync(string scriptPath, GIMPScriptType scriptType)
        {
            if (string.IsNullOrEmpty(scriptPath))
                throw new ArgumentException("Script path cannot be null or empty", nameof(scriptPath));

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Script file not found: {scriptPath}");

            var scriptContent = await File.ReadAllTextAsync(scriptPath);
            var command = new GIMPCommand;
            {
                Name = Path.GetFileName(scriptPath),
                Content = scriptContent,
                CommandType = scriptType == GIMPScriptType.Scheme ? GIMPCommandType.Scheme : GIMPCommandType.Python;
            };

            var response = await ExecuteCommandAsync(command);

            OnScriptExecuted(new GIMPScriptExecutedEventArgs;
            {
                ScriptPath = scriptPath,
                ScriptType = scriptType,
                Success = response.Success,
                ExecutionTime = response.ExecutionTime;
            });

            return response;
        }

        /// <summary>
        /// Birden fazla komutu sırayla yürütür;
        /// </summary>
        public async Task<List<GIMPResponse>> ExecuteCommandsAsync(IEnumerable<GIMPCommand> commands)
        {
            var results = new List<GIMPResponse>();

            foreach (var command in commands)
            {
                var result = await ExecuteCommandAsync(command);
                results.Add(result);

                if (!result.Success && command.IsCritical)
                {
                    break;
                }
            }

            return results;
        }

        #endregion;

        #region Image Operations;

        /// <summary>
        /// Görüntüyü GIMP'te açar;
        /// </summary>
        public async Task<GIMPResponse> OpenImageAsync(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
                throw new ArgumentException("Image path cannot be null or empty", nameof(imagePath));

            if (!File.Exists(imagePath))
                throw new FileNotFoundException($"Image file not found: {imagePath}");

            var pythonScript = $@"
import sys;
from gimpfu import *

image = pdb.gimp_file_load('{imagePath}', '{imagePath}')
pdb.gimp_display_new(image)
print('Image opened successfully')
";

            var command = new GIMPCommand;
            {
                Name = "OpenImage",
                Content = pythonScript,
                CommandType = GIMPCommandType.Python,
                Parameters = new Dictionary<string, object> { { "ImagePath", imagePath } }
            };

            return await ExecuteCommandAsync(command);
        }

        /// <summary>
        /// Görüntüye filtre uygular;
        /// </summary>
        public async Task<GIMPResponse> ApplyFilterAsync(string filterName, Dictionary<string, object> parameters = null)
        {
            if (string.IsNullOrEmpty(filterName))
                throw new ArgumentException("Filter name cannot be null or empty", nameof(filterName));

            // GIMP PDB (Prosedür Veritabanı) komutunu oluştur;
            var schemeScript = $"(plug-in-{filterName} 1 0 0)";

            if (parameters != null && parameters.Count > 0)
            {
                // Parametreleri Scheme formatına dönüştür;
                var paramString = string.Join(" ", parameters.Select(p => ConvertToSchemeValue(p.Value)));
                schemeScript = $"(plug-in-{filterName} 1 0 {paramString})";
            }

            var command = new GIMPCommand;
            {
                Name = $"ApplyFilter_{filterName}",
                Content = schemeScript,
                CommandType = GIMPCommandType.Scheme,
                Parameters = parameters;
            };

            return await ExecuteCommandAsync(command);
        }

        /// <summary>
        /// Görüntüyü kaydeder;
        /// </summary>
        public async Task<GIMPResponse> SaveImageAsync(string savePath, string format = "jpg", int quality = 90)
        {
            if (string.IsNullOrEmpty(savePath))
                throw new ArgumentException("Save path cannot be null or empty", nameof(savePath));

            var pythonScript = $@"
import sys;
from gimpfu import *

# Aktif görüntüyü al;
image = gimp.image_list()[0]
drawable = pdb.gimp_image_get_active_drawable(image)

# Kaydet;
if '{format}'.lower() == 'jpg':
    pdb.file_jpeg_save(image, drawable, '{savePath}', '{savePath}', {quality / 100.0}, 0, 1, 0, '', 0, 1, 0, 0)
elif '{format}'.lower() == 'png':
    pdb.file_png_save(image, drawable, '{savePath}', '{savePath}', 0, 9, 1, 0, 0, 1, 1)
else:
    pdb.gimp_file_save(image, drawable, '{savePath}', '{savePath}')

print('Image saved successfully')
";

            var command = new GIMPCommand;
            {
                Name = "SaveImage",
                Content = pythonScript,
                CommandType = GIMPCommandType.Python,
                Parameters = new Dictionary<string, object>
                {
                    { "SavePath", savePath },
                    { "Format", format },
                    { "Quality", quality }
                }
            };

            return await ExecuteCommandAsync(command);
        }

        /// <summary>
        /// Batch görüntü işleme;
        /// </summary>
        public async Task<BatchProcessingResult> ProcessBatchImagesAsync(
            IEnumerable<string> imagePaths,
            IEnumerable<GIMPCommand> operations)
        {
            var result = new BatchProcessingResult;
            {
                StartTime = DateTime.Now,
                TotalImages = imagePaths.Count()
            };

            foreach (var imagePath in imagePaths)
            {
                try
                {
                    // Görüntüyü aç;
                    await OpenImageAsync(imagePath);

                    // İşlemleri uygula;
                    foreach (var operation in operations)
                    {
                        await ExecuteCommandAsync(operation);
                    }

                    // Kaydet;
                    var outputPath = GetOutputPath(imagePath);
                    await SaveImageAsync(outputPath);

                    result.ProcessedImages++;
                    result.SuccessfulImages++;
                }
                catch (Exception ex)
                {
                    result.FailedImages++;
                    result.Errors.Add(new ImageProcessingError;
                    {
                        ImagePath = imagePath,
                        ErrorMessage = ex.Message,
                        Timestamp = DateTime.Now;
                    });

                    _logger.Error($"Failed to process image {imagePath}: {ex.Message}", GetType().Name);
                }
            }

            result.EndTime = DateTime.Now;
            result.IsComplete = true;

            return result;
        }

        #endregion;

        #region Plugin Management;

        /// <summary>
        /// Mevcut GIMP eklentilerini yükler;
        /// </summary>
        public async Task LoadAvailablePluginsAsync()
        {
            try
            {
                var pythonScript = @"
import sys;
import json;
from gimpfu import *

plugins = []
for proc in pdb.procedures():
    plugin_info = {
        'name': proc[0],
        'type': proc[1],
        'params': []
    }
    
    # Parametre bilgilerini al;
    for i in range(2, len(proc)):
        if isinstance(proc[i], tuple) and len(proc[i]) >= 2:
            param_info = {
                'name': proc[i][0],
                'type': proc[i][1]
            }
            plugin_info['params'].append(param_info)
    
    plugins.append(plugin_info)

print(json.dumps(plugins))
";

                var command = new GIMPCommand;
                {
                    Name = "ListPlugins",
                    Content = pythonScript,
                    CommandType = GIMPCommandType.Python;
                };

                var response = await ExecuteCommandAsync(command, SCRIPT_EXECUTION_TIMEOUT);

                if (response.Success && !string.IsNullOrEmpty(response.Result))
                {
                    var plugins = JsonSerializer.Deserialize<List<GIMPPlugin>>(response.Result);
                    AvailablePlugins = plugins ?? new List<GIMPPlugin>();

                    _logger.Info($"Loaded {AvailablePlugins.Count} GIMP plugins", GetType().Name);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load GIMP plugins: {ex.Message}", GetType().Name);
            }
        }

        /// <summary>
        /// Belirli bir eklentinin varlığını kontrol eder;
        /// </summary>
        public bool PluginExists(string pluginName)
        {
            return AvailablePlugins.Any(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Python-Fu script'ini yürütür;
        /// </summary>
        public async Task<GIMPResponse> ExecutePythonFUScriptAsync(string pythonCode)
        {
            var command = new GIMPCommand;
            {
                Name = "PythonFUScript",
                Content = pythonCode,
                CommandType = GIMPCommandType.Python,
                IsCritical = true;
            };

            return await ExecuteCommandAsync(command, SCRIPT_EXECUTION_TIMEOUT);
        }

        /// <summary>
        /// Script-Fu script'ini yürütür;
        /// </summary>
        public async Task<GIMPResponse> ExecuteScriptFUScriptAsync(string schemeCode)
        {
            var command = new GIMPCommand;
            {
                Name = "ScriptFUScript",
                Content = schemeCode,
                CommandType = GIMPCommandType.Scheme,
                IsCritical = true;
            };

            return await ExecuteCommandAsync(command, SCRIPT_EXECUTION_TIMEOUT);
        }

        #endregion;

        #region Utility Methods;

        /// <summary>
        /// GIMP sürümünü alır;
        /// </summary>
        private async Task GetGIMPVersionAsync()
        {
            try
            {
                var command = new GIMPCommand;
                {
                    Name = "GetVersion",
                    Content = "--version",
                    CommandType = GIMPCommandType.Raw;
                };

                var response = await ExecuteCommandAsync(command);
                if (response.Success)
                {
                    Version = response.Result?.Trim() ?? "Unknown";
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to get GIMP version: {ex.Message}", GetType().Name);
                Version = "Unknown";
            }
        }

        /// <summary>
        /// GIMP çalıştırılabilir dosyasını tespit eder;
        /// </summary>
        private void DetectGIMPExecutable()
        {
            var possiblePaths = new List<string>();

            // İşletim sistemine göre olası yollar;
            if (PlatformHelper.IsWindows)
            {
                possiblePaths.Add(GIMP_DEFAULT_WINDOWS_PATH);
                possiblePaths.Add(@"C:\Program Files\GIMP 2\bin\gimp-console.exe");
                possiblePaths.Add(@"C:\Program Files\GIMP 2\bin\gimp-2.10.exe");

                // PATH değişkeninden ara;
                var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';');
                if (pathDirs != null)
                {
                    foreach (var dir in pathDirs)
                    {
                        possiblePaths.Add(Path.Combine(dir, "gimp-console.exe"));
                        possiblePaths.Add(Path.Combine(dir, "gimp-2.10.exe"));
                        possiblePaths.Add(Path.Combine(dir, "gimp.exe"));
                    }
                }
            }
            else if (PlatformHelper.IsMacOS)
            {
                possiblePaths.Add(GIMP_DEFAULT_MAC_PATH);
                possiblePaths.Add("/usr/local/bin/gimp");
            }
            else if (PlatformHelper.IsLinux)
            {
                possiblePaths.Add(GIMP_DEFAULT_LINUX_PATH);
                possiblePaths.Add("/usr/local/bin/gimp");
                possiblePaths.Add("/opt/gimp/bin/gimp");
            }

            // Geçerli bir dosya bul;
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    _gimpExecutablePath = path;
                    _logger.Info($"Detected GIMP at: {path}", GetType().Name);
                    return;
                }
            }

            _logger.Warning("GIMP executable not found automatically. Please specify the path manually.", GetType().Name);
        }

        /// <summary>
        /// İşlem komutunu gönderir;
        /// </summary>
        private async Task<string> SendProcessCommandAsync(string command, CancellationToken cancellationToken)
        {
            if (_gimpProcess == null || _gimpProcess.HasExited)
                throw new InvalidOperationException("GIMP process is not running");

            var outputBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<string>();

            void OnOutputReceived(object sender, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    OnOutputReceivedInternal(e.Data);

                    // Python script'leri genellikle son satırda çıktı verir;
                    if (e.Data.Contains("successfully") || e.Data.Contains("Image processed"))
                    {
                        tcs.TrySetResult(outputBuilder.ToString());
                    }
                }
            }

            _gimpProcess.OutputDataReceived += OnOutputReceived;

            try
            {
                await _gimpProcess.StandardInput.WriteLineAsync(command);

                // Zaman aşımı ile bekleyin;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(Timeout);

                    await Task.Run(() =>
                    {
                        while (!tcs.Task.IsCompleted && !timeoutCts.Token.IsCancellationRequested)
                        {
                            Thread.Sleep(100);
                        }
                    }, timeoutCts.Token);
                }

                if (tcs.Task.IsCompleted)
                {
                    return await tcs.Task;
                }
                else;
                {
                    throw new TimeoutException("Command execution timed out");
                }
            }
            finally
            {
                _gimpProcess.OutputDataReceived -= OnOutputReceived;
            }
        }

        /// <summary>
        /// Scheme script'ini escape'ler;
        /// </summary>
        private string EscapeSchemeScript(string script)
        {
            return script.Replace("\"", "\\\"")
                        .Replace("'", "\\'")
                        .Replace("\\", "\\\\");
        }

        /// <summary>
        /// Değeri Scheme formatına dönüştürür;
        /// </summary>
        private string ConvertToSchemeValue(object value)
        {
            if (value == null)
                return "#f";

            if (value is bool boolValue)
                return boolValue ? "#t" : "#f";

            if (value is int || value is long || value is double || value is float)
                return value.ToString();

            if (value is string stringValue)
                return $"\"{stringValue.Replace("\"", "\\\"")}\"";

            return $"\"{value.ToString()}\"";
        }

        /// <summary>
        /// Çıkış dosya yolu oluşturur;
        /// </summary>
        private string GetOutputPath(string inputPath)
        {
            var directory = Path.GetDirectoryName(inputPath);
            var fileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = Path.GetExtension(inputPath);

            return Path.Combine(directory ?? ".", $"{fileName}_processed{extension}");
        }

        /// <summary>
        /// GIMP'e raw komut gönderir;
        /// </summary>
        private void SendCommand(string command)
        {
            if (_gimpProcess != null && !_gimpProcess.HasExited)
            {
                _gimpProcess.StandardInput.WriteLine(command);
                _gimpProcess.StandardInput.Flush();
            }
        }

        #endregion;

        #region Event Handlers;

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                OnOutputReceivedInternal(e.Data);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.Error($"GIMP Error: {e.Data}", GetType().Name);

                OnOutputReceived(new GIMPOutputReceivedEventArgs;
                {
                    Output = e.Data,
                    IsError = true,
                    Timestamp = DateTime.Now;
                });
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            _logger.Info("GIMP process exited", GetType().Name);
            Disconnect();
        }

        private void OnOutputReceivedInternal(string output)
        {
            if (DebugMode)
            {
                _logger.Debug($"GIMP Output: {output}", GetType().Name);
            }

            OnOutputReceived(new GIMPOutputReceivedEventArgs;
            {
                Output = output,
                IsError = false,
                Timestamp = DateTime.Now;
            });
        }

        #endregion;

        #region Event Invokers;

        protected virtual void OnConnected(GIMPConnectedEventArgs e)
        {
            Connected?.Invoke(this, e);
        }

        protected virtual void OnDisconnected(GIMPDisconnectedEventArgs e)
        {
            Disconnected?.Invoke(this, e);
        }

        protected virtual void OnCommandExecuted(GIMPCommandExecutedEventArgs e)
        {
            CommandExecuted?.Invoke(this, e);
        }

        protected virtual void OnScriptExecuted(GIMPScriptExecutedEventArgs e)
        {
            ScriptExecuted?.Invoke(this, e);
        }

        protected virtual void OnOutputReceived(GIMPOutputReceivedEventArgs e)
        {
            OutputReceived?.Invoke(this, e);
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
                    Disconnect();

                    lock (_commandQueue)
                    {
                        _commandQueue.Clear();
                    }

                    AvailablePlugins.Clear();
                    _environmentVariables.Clear();

                    _logger.Info("GIMPConnector disposed", GetType().Name);
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~GIMPConnector()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// GIMP komut türü;
    /// </summary>
    public enum GIMPCommandType;
    {
        Scheme,
        Python,
        Raw;
    }

    /// <summary>
    /// GIMP script türü;
    /// </summary>
    public enum GIMPScriptType;
    {
        Scheme,
        Python,
        Perl,
        PythonFU,
        ScriptFU;
    }

    /// <summary>
    /// GIMP komut durumu;
    /// </summary>
    public enum GIMPCommandStatus;
    {
        Pending,
        Executing,
        Completed,
        Failed,
        Timeout;
    }

    /// <summary>
    /// GIMP komutu;
    /// </summary>
    public class GIMPCommand;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Content { get; set; }
        public GIMPCommandType CommandType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public GIMPCommandStatus Status { get; set; } = GIMPCommandStatus.Pending;
        public string Response { get; set; }
        public DateTime? ExecutionStartTime { get; set; }
        public DateTime? ExecutionEndTime { get; set; }
        public TimeSpan ExecutionTime => ExecutionStartTime.HasValue && ExecutionEndTime.HasValue;
            ? ExecutionEndTime.Value - ExecutionStartTime.Value;
            : TimeSpan.Zero;
        public bool IsCritical { get; set; }
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// GIMP yanıtı;
    /// </summary>
    public class GIMPResponse;
    {
        public bool Success { get; set; }
        public string Result { get; set; }
        public string Error { get; set; }
        public Guid CommandId { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// GIMP eklentisi;
    /// </summary>
    public class GIMPPlugin;
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public List<PluginParameter> Parameters { get; set; } = new List<PluginParameter>();
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Eklenti parametresi;
    /// </summary>
    public class PluginParameter;
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public object DefaultValue { get; set; }
        public bool IsRequired { get; set; }
    }

    /// <summary>
    /// GIMP oturumu;
    /// </summary>
    public class GIMPSession;
    {
        public Guid SessionId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => StartTime != default && EndTime.HasValue;
            ? EndTime.Value - StartTime;
            : DateTime.Now - StartTime;
        public int TotalCommands { get; set; }
        public DateTime LastCommandTime { get; set; }
        public Dictionary<string, object> SessionData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Batch işleme sonucu;
    /// </summary>
    public class BatchProcessingResult;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int TotalImages { get; set; }
        public int ProcessedImages { get; set; }
        public int SuccessfulImages { get; set; }
        public int FailedImages { get; set; }
        public bool IsComplete { get; set; }
        public List<ImageProcessingError> Errors { get; set; } = new List<ImageProcessingError>();
        public TimeSpan TotalDuration => EndTime - StartTime;
    }

    /// <summary>
    /// Görüntü işleme hatası;
    /// </summary>
    public class ImageProcessingError;
    {
        public string ImagePath { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
    }

    #endregion;

    #region Event Args Classes;

    public class GIMPConnectedEventArgs : EventArgs;
    {
        public Guid SessionId { get; set; }
        public int ProcessId { get; set; }
        public string Version { get; set; }
        public bool IsBatchMode { get; set; }
    }

    public class GIMPDisconnectedEventArgs : EventArgs;
    {
        public Guid SessionId { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalCommands { get; set; }
    }

    public class GIMPCommandExecutedEventArgs : EventArgs;
    {
        public GIMPCommand Command { get; set; }
        public Guid SessionId { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    public class GIMPScriptExecutedEventArgs : EventArgs;
    {
        public string ScriptPath { get; set; }
        public GIMPScriptType ScriptType { get; set; }
        public bool Success { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    public class GIMPOutputReceivedEventArgs : EventArgs;
    {
        public string Output { get; set; }
        public bool IsError { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}
