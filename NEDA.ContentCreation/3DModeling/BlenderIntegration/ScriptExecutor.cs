using Microsoft.Extensions.Logging;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Core.Commands;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.KnowledgeBase;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Automation.Executors;
{
    /// <summary>
    /// Script execution engine supporting multiple scripting languages and execution modes;
    /// </summary>
    public class ScriptExecutor : IScriptExecutor, IDisposable;
    {
        private readonly ILogger<ScriptExecutor> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IAppConfig _appConfig;
        private readonly ICommandRegistry _commandRegistry
        private readonly IScriptValidator _scriptValidator;
        private readonly IScriptRuntimeFactory _runtimeFactory;
        private readonly IScriptResultProcessor _resultProcessor;

        private readonly Dictionary<string, Process> _runningProcesses;
        private readonly Dictionary<string, IScriptRuntime> _activeRuntimes;
        private readonly Dictionary<string, ScriptExecutionContext> _executionContexts;
        private readonly object _lock = new object();

        private bool _isDisposed;
        private Timer _monitoringTimer;
        private ScriptExecutionMetrics _metrics;

        /// <summary>
        /// Event triggered when script execution starts;
        /// </summary>
        public event EventHandler<ScriptExecutionEventArgs> ScriptStarted;

        /// <summary>
        /// Event triggered when script execution completes;
        /// </summary>
        public event EventHandler<ScriptExecutionEventArgs> ScriptCompleted;

        /// <summary>
        /// Event triggered when script execution fails;
        /// </summary>
        public event EventHandler<ScriptErrorEventArgs> ScriptError;

        /// <summary>
        /// Event triggered when script output is received;
        /// </summary>
        public event EventHandler<ScriptOutputEventArgs> OutputReceived;

        /// <summary>
        /// ScriptExecutor constructor with dependency injection;
        /// </summary>
        public ScriptExecutor(
            ILogger<ScriptExecutor> logger,
            ISecurityManager securityManager,
            IAppConfig appConfig,
            ICommandRegistry commandRegistry,
            IScriptValidator scriptValidator,
            IScriptRuntimeFactory runtimeFactory,
            IScriptResultProcessor resultProcessor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
            _scriptValidator = scriptValidator ?? throw new ArgumentNullException(nameof(scriptValidator));
            _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _resultProcessor = resultProcessor ?? throw new ArgumentNullException(nameof(resultProcessor));

            _runningProcesses = new Dictionary<string, Process>();
            _activeRuntimes = new Dictionary<string, IScriptRuntime>();
            _executionContexts = new Dictionary<string, ScriptExecutionContext>();
            _metrics = new ScriptExecutionMetrics();

            InitializeMonitoring();
        }

        /// <summary>
        /// Initializes script monitoring system;
        /// </summary>
        private void InitializeMonitoring()
        {
            var monitoringInterval = _appConfig.GetValue<int>("ScriptExecutor:MonitoringInterval", 5000);
            _monitoringTimer = new Timer(MonitorRunningScripts, null, monitoringInterval, monitoringInterval);

            _logger.LogInformation("ScriptExecutor monitoring initialized with {Interval}ms interval", monitoringInterval);
        }

        /// <summary>
        /// Executes a script from file;
        /// </summary>
        /// <param name="scriptPath">Path to script file</param>
        /// <param name="arguments">Script arguments</param>
        /// <param name="executionMode">Execution mode (e.g., Elevated, Sandboxed)</param>
        /// <param name="timeout">Execution timeout in milliseconds</param>
        /// <returns>Script execution result</returns>
        public async Task<ScriptExecutionResult> ExecuteScriptAsync(
            string scriptPath,
            string[] arguments = null,
            ExecutionMode executionMode = ExecutionMode.Normal,
            int timeout = 30000)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new ArgumentException("Script path cannot be null or empty", nameof(scriptPath));

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Script file not found: {scriptPath}");

            var scriptId = GenerateScriptId(scriptPath);
            var context = CreateExecutionContext(scriptId, scriptPath, arguments, executionMode, timeout);

            try
            {
                _logger.LogInformation("Starting script execution: {ScriptId} for {ScriptPath}", scriptId, scriptPath);

                // Validate script security;
                await ValidateScriptSecurityAsync(scriptPath, executionMode);

                // Read and validate script content;
                var scriptContent = await File.ReadAllTextAsync(scriptPath, Encoding.UTF8);
                await ValidateScriptContentAsync(scriptPath, scriptContent);

                // Determine script language;
                var scriptLanguage = DetectScriptLanguage(scriptPath, scriptContent);
                context.ScriptLanguage = scriptLanguage;

                // OnScriptStarted event;
                ScriptStarted?.Invoke(this, new ScriptExecutionEventArgs(scriptId, scriptPath, context));

                // Execute based on language and mode;
                ScriptExecutionResult result;
                switch (scriptLanguage)
                {
                    case ScriptLanguage.PowerShell:
                        result = await ExecutePowerShellScriptAsync(context, scriptContent);
                        break;
                    case ScriptLanguage.Python:
                        result = await ExecutePythonScriptAsync(context, scriptContent);
                        break;
                    case ScriptLanguage.Batch:
                        result = await ExecuteBatchScriptAsync(context, scriptContent);
                        break;
                    case ScriptLanguage.JavaScript:
                        result = await ExecuteJavaScriptAsync(context, scriptContent);
                        break;
                    case ScriptLanguage.CSharp:
                        result = await ExecuteCSharpScriptAsync(context, scriptContent);
                        break;
                    default:
                        throw new NotSupportedException($"Script language not supported: {scriptLanguage}");
                }

                // Process and analyze results;
                await _resultProcessor.ProcessResultAsync(result, context);

                // Update metrics;
                UpdateMetrics(result);

                // OnScriptCompleted event;
                ScriptCompleted?.Invoke(this, new ScriptExecutionEventArgs(scriptId, scriptPath, context, result));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Script execution failed: {ScriptId}", scriptId);

                var errorResult = new ScriptExecutionResult;
                {
                    Success = false,
                    ExitCode = -1,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ExecutionTime = DateTime.UtcNow - context.StartTime;
                };

                ScriptError?.Invoke(this, new ScriptErrorEventArgs(scriptId, scriptPath, ex, context));
                return errorResult;
            }
            finally
            {
                CleanupExecutionContext(scriptId);
            }
        }

        /// <summary>
        /// Executes inline script content;
        /// </summary>
        public async Task<ScriptExecutionResult> ExecuteInlineScriptAsync(
            string scriptContent,
            ScriptLanguage language,
            string[] arguments = null,
            ExecutionMode executionMode = ExecutionMode.Normal,
            int timeout = 30000)
        {
            if (string.IsNullOrWhiteSpace(scriptContent))
                throw new ArgumentException("Script content cannot be null or empty", nameof(scriptContent));

            var scriptId = GenerateScriptId("inline");
            var context = CreateExecutionContext(scriptId, "inline", arguments, executionMode, timeout);
            context.ScriptLanguage = language;

            try
            {
                _logger.LogInformation("Starting inline script execution: {ScriptId} ({Language})", scriptId, language);

                // Validate script security for inline execution;
                await ValidateInlineScriptSecurityAsync(scriptContent, language, executionMode);

                // Validate script content;
                await ValidateScriptContentAsync("inline", scriptContent);

                ScriptStarted?.Invoke(this, new ScriptExecutionEventArgs(scriptId, "inline", context));

                ScriptExecutionResult result = language switch;
                {
                    ScriptLanguage.PowerShell => await ExecutePowerShellScriptAsync(context, scriptContent),
                    ScriptLanguage.Python => await ExecutePythonScriptAsync(context, scriptContent),
                    ScriptLanguage.Batch => await ExecuteBatchScriptAsync(context, scriptContent),
                    ScriptLanguage.JavaScript => await ExecuteJavaScriptAsync(context, scriptContent),
                    ScriptLanguage.CSharp => await ExecuteCSharpScriptAsync(context, scriptContent),
                    _ => throw new NotSupportedException($"Script language not supported: {language}")
                };

                await _resultProcessor.ProcessResultAsync(result, context);
                UpdateMetrics(result);

                ScriptCompleted?.Invoke(this, new ScriptExecutionEventArgs(scriptId, "inline", context, result));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inline script execution failed: {ScriptId}", scriptId);

                var errorResult = new ScriptExecutionResult;
                {
                    Success = false,
                    ExitCode = -1,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ExecutionTime = DateTime.UtcNow - context.StartTime;
                };

                ScriptError?.Invoke(this, new ScriptErrorEventArgs(scriptId, "inline", ex, context));
                return errorResult;
            }
            finally
            {
                CleanupExecutionContext(scriptId);
            }
        }

        /// <summary>
        /// Executes PowerShell script;
        /// </summary>
        private async Task<ScriptExecutionResult> ExecutePowerShellScriptAsync(ScriptExecutionContext context, string scriptContent)
        {
            var result = new ScriptExecutionResult();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            try
            {
                using var powerShell = System.Management.Automation.PowerShell.Create();

                // Add script content;
                powerShell.AddScript(scriptContent);

                // Add arguments if any;
                if (context.Arguments != null && context.Arguments.Length > 0)
                {
                    powerShell.AddParameters(context.Arguments);
                }

                // Configure execution policy based on mode;
                ConfigurePowerShellExecutionPolicy(powerShell, context.ExecutionMode);

                // Execute asynchronously with timeout;
                var task = Task.Factory.StartNew(() => powerShell.Invoke());
                var completedTask = await Task.WhenAny(task, Task.Delay(context.Timeout));

                if (completedTask != task)
                {
                    throw new TimeoutException($"PowerShell script execution timed out after {context.Timeout}ms");
                }

                // Collect results;
                var psResults = task.Result;
                foreach (var psObject in psResults)
                {
                    outputBuilder.AppendLine(psObject.ToString());
                }

                // Collect errors;
                if (powerShell.Streams.Error.Count > 0)
                {
                    foreach (var error in powerShell.Streams.Error)
                    {
                        errorBuilder.AppendLine(error.ToString());
                    }
                }

                result.Success = powerShell.HadErrors == false;
                result.ExitCode = powerShell.HadErrors ? 1 : 0;
                result.Output = outputBuilder.ToString();
                result.ErrorOutput = errorBuilder.ToString();
                result.ExecutionTime = DateTime.UtcNow - context.StartTime;

                // Capture PowerShell specific data;
                result.Metadata["PowerShellVersion"] = powerShell.GetType().Assembly.GetName().Version.ToString();
                result.Metadata["HadErrors"] = powerShell.HadErrors.ToString();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ExitCode = -1;
                result.ErrorMessage = ex.Message;
                result.Exception = ex;
                result.ErrorOutput = errorBuilder.ToString();
                result.ExecutionTime = DateTime.UtcNow - context.StartTime;
            }

            return result;
        }

        /// <summary>
        /// Executes Python script;
        /// </summary>
        private async Task<ScriptExecutionResult> ExecutePythonScriptAsync(ScriptExecutionContext context, string scriptContent)
        {
            var result = new ScriptExecutionResult();
            var pythonPath = GetPythonExecutablePath();

            if (string.IsNullOrEmpty(pythonPath))
                throw new InvalidOperationException("Python executable not found. Please ensure Python is installed.");

            var tempScriptPath = Path.Combine(Path.GetTempPath(), $"neda_script_{Guid.NewGuid():N}.py");

            try
            {
                // Write script to temporary file;
                await File.WriteAllTextAsync(tempScriptPath, scriptContent, Encoding.UTF8);

                // Build arguments;
                var arguments = new List<string> { tempScriptPath };
                if (context.Arguments != null)
                {
                    arguments.AddRange(context.Arguments);
                }

                // Execute process;
                var processResult = await ExecuteExternalProcessAsync(
                    pythonPath,
                    arguments.ToArray(),
                    context.Timeout,
                    context.ExecutionMode);

                result.Success = processResult.ExitCode == 0;
                result.ExitCode = processResult.ExitCode;
                result.Output = processResult.Output;
                result.ErrorOutput = processResult.Error;
                result.ExecutionTime = processResult.ExecutionTime;

                result.Metadata["PythonPath"] = pythonPath;
                result.Metadata["PythonVersion"] = await GetPythonVersionAsync(pythonPath);
            }
            finally
            {
                // Cleanup temporary file;
                try
                {
                    if (File.Exists(tempScriptPath))
                        File.Delete(tempScriptPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary Python script: {Path}", tempScriptPath);
                }
            }

            return result;
        }

        /// <summary>
        /// Executes Batch script;
        /// </summary>
        private async Task<ScriptExecutionResult> ExecuteBatchScriptAsync(ScriptExecutionContext context, string scriptContent)
        {
            var result = new ScriptExecutionResult();
            var tempScriptPath = Path.Combine(Path.GetTempPath(), $"neda_script_{Guid.NewGuid():N}.bat");

            try
            {
                // Write script to temporary file;
                await File.WriteAllTextAsync(tempScriptPath, scriptContent, Encoding.Default);

                // Build arguments;
                var arguments = new List<string> { "/C", tempScriptPath };
                if (context.Arguments != null && context.Arguments.Length > 0)
                {
                    arguments.AddRange(context.Arguments);
                }

                // Execute process;
                var processResult = await ExecuteExternalProcessAsync(
                    "cmd.exe",
                    arguments.ToArray(),
                    context.Timeout,
                    context.ExecutionMode);

                result.Success = processResult.ExitCode == 0;
                result.ExitCode = processResult.ExitCode;
                result.Output = processResult.Output;
                result.ErrorOutput = processResult.Error;
                result.ExecutionTime = processResult.ExecutionTime;

                result.Metadata["IsBatch"] = "true";
                result.Metadata["ScriptTempPath"] = tempScriptPath;
            }
            finally
            {
                // Cleanup temporary file;
                try
                {
                    if (File.Exists(tempScriptPath))
                        File.Delete(tempScriptPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary batch script: {Path}", tempScriptPath);
                }
            }

            return result;
        }

        /// <summary>
        /// Executes JavaScript using Node.js;
        /// </summary>
        private async Task<ScriptExecutionResult> ExecuteJavaScriptAsync(ScriptExecutionContext context, string scriptContent)
        {
            var result = new ScriptExecutionResult();
            var nodePath = GetNodeExecutablePath();

            if (string.IsNullOrEmpty(nodePath))
                throw new InvalidOperationException("Node.js executable not found. Please ensure Node.js is installed.");

            var tempScriptPath = Path.Combine(Path.GetTempPath(), $"neda_script_{Guid.NewGuid():N}.js");

            try
            {
                // Write script to temporary file;
                await File.WriteAllTextAsync(tempScriptPath, scriptContent, Encoding.UTF8);

                // Build arguments;
                var arguments = new List<string> { tempScriptPath };
                if (context.Arguments != null)
                {
                    arguments.AddRange(context.Arguments);
                }

                // Execute process;
                var processResult = await ExecuteExternalProcessAsync(
                    nodePath,
                    arguments.ToArray(),
                    context.Timeout,
                    context.ExecutionMode);

                result.Success = processResult.ExitCode == 0;
                result.ExitCode = processResult.ExitCode;
                result.Output = processResult.Output;
                result.ErrorOutput = processResult.Error;
                result.ExecutionTime = processResult.ExecutionTime;

                result.Metadata["NodePath"] = nodePath;
                result.Metadata["NodeVersion"] = await GetNodeVersionAsync(nodePath);
            }
            finally
            {
                // Cleanup temporary file;
                try
                {
                    if (File.Exists(tempScriptPath))
                        File.Delete(tempScriptPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary JavaScript script: {Path}", tempScriptPath);
                }
            }

            return result;
        }

        /// <summary>
        /// Executes C# script using Roslyn;
        /// </summary>
        private async Task<ScriptExecutionResult> ExecuteCSharpScriptAsync(ScriptExecutionContext context, string scriptContent)
        {
            var result = new ScriptExecutionResult();

            try
            {
                // Use Microsoft.CodeAnalysis.CSharp.Scripting for C# scripting;
                // This requires additional NuGet packages: Microsoft.CodeAnalysis.CSharp.Scripting;

                // Note: In a full implementation, this would use Roslyn scripting engine;
                // For now, we'll use a simplified approach;

                var tempAssemblyPath = await CompileCSharpScriptAsync(scriptContent);

                // Execute compiled assembly;
                var processResult = await ExecuteExternalProcessAsync(
                    "dotnet",
                    new[] { tempAssemblyPath }.Concat(context.Arguments ?? Array.Empty<string>()).ToArray(),
                    context.Timeout,
                    context.ExecutionMode);

                result.Success = processResult.ExitCode == 0;
                result.ExitCode = processResult.ExitCode;
                result.Output = processResult.Output;
                result.ErrorOutput = processResult.Error;
                result.ExecutionTime = processResult.ExecutionTime;

                result.Metadata["IsCSharp"] = "true";
                result.Metadata["DotNetVersion"] = await GetDotNetVersionAsync();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ExitCode = -1;
                result.ErrorMessage = $"C# script execution failed: {ex.Message}";
                result.Exception = ex;
                result.ExecutionTime = DateTime.UtcNow - context.StartTime;
            }

            return result;
        }

        /// <summary>
        /// Executes external process;
        /// </summary>
        private async Task<ProcessExecutionResult> ExecuteExternalProcessAsync(
            string executablePath,
            string[] arguments,
            int timeout,
            ExecutionMode executionMode)
        {
            var startInfo = new ProcessStartInfo;
            {
                FileName = executablePath,
                Arguments = string.Join(" ", arguments.Select(a => $"\"{a.Replace("\"", "\\\"")}\"")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden;
            };

            // Configure execution mode;
            ConfigureProcessSecurity(startInfo, executionMode);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();
            var processId = Guid.NewGuid().ToString("N");

            using var process = new Process { StartInfo = startInfo };

            lock (_lock)
            {
                _runningProcesses[processId] = process;
            }

            try
            {
                // Setup output/error data handlers;
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        OutputReceived?.Invoke(this, new ScriptOutputEventArgs(processId, e.Data, false));
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        OutputReceived?.Invoke(this, new ScriptOutputEventArgs(processId, e.Data, true));
                    }
                };

                // Start process;
                var startTime = DateTime.UtcNow;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for exit with timeout;
                var exited = await Task.Run(() => process.WaitForExit(timeout));

                if (!exited)
                {
                    process.Kill(true);
                    await Task.Delay(100); // Give time for kill to take effect;
                    throw new TimeoutException($"Process execution timed out after {timeout}ms");
                }

                var executionTime = DateTime.UtcNow - startTime;

                return new ProcessExecutionResult;
                {
                    ExitCode = process.ExitCode,
                    Output = outputBuilder.ToString(),
                    Error = errorBuilder.ToString(),
                    ExecutionTime = executionTime,
                    ProcessId = process.Id;
                };
            }
            finally
            {
                lock (_lock)
                {
                    _runningProcesses.Remove(processId);
                }

                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { /* Ignore */ }
                }

                process.Close();
            }
        }

        /// <summary>
        /// Monitors running scripts and handles timeouts;
        /// </summary>
        private void MonitorRunningScripts(object state)
        {
            try
            {
                List<string> processesToKill = new List<string>();
                var now = DateTime.UtcNow;

                lock (_lock)
                {
                    foreach (var kvp in _runningProcesses)
                    {
                        var processId = kvp.Key;
                        var process = kvp.Value;

                        if (!process.HasExited)
                        {
                            var context = _executionContexts.GetValueOrDefault(processId);
                            if (context != null && (now - context.StartTime).TotalMilliseconds > context.Timeout)
                            {
                                processesToKill.Add(processId);
                                _logger.LogWarning("Script execution timeout detected: {ProcessId}", processId);
                            }
                        }
                    }
                }

                // Kill timed out processes;
                foreach (var processId in processesToKill)
                {
                    try
                    {
                        if (_runningProcesses.TryGetValue(processId, out var process))
                        {
                            process.Kill(true);
                            _logger.LogInformation("Killed timed out script process: {ProcessId}", processId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to kill timed out process: {ProcessId}", processId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in script monitoring");
            }
        }

        /// <summary>
        /// Validates script security based on execution mode;
        /// </summary>
        private async Task ValidateScriptSecurityAsync(string scriptPath, ExecutionMode executionMode)
        {
            var scriptHash = await CalculateFileHashAsync(scriptPath);

            // Check if script is blacklisted;
            if (await _securityManager.IsScriptBlacklistedAsync(scriptHash))
            {
                throw new SecurityException($"Script is blacklisted: {scriptPath}");
            }

            // Check digital signature for elevated mode;
            if (executionMode == ExecutionMode.Elevated)
            {
                if (!await _securityManager.VerifyScriptSignatureAsync(scriptPath))
                {
                    throw new SecurityException($"Script not digitally signed for elevated execution: {scriptPath}");
                }
            }

            // Check file permissions;
            var fileInfo = new FileInfo(scriptPath);
            if (!_securityManager.HasFileAccessPermission(fileInfo, FileAccess.Read | FileAccess.Execute))
            {
                throw new UnauthorizedAccessException($"Access denied to script file: {scriptPath}");
            }
        }

        /// <summary>
        /// Detects script language from file extension and content;
        /// </summary>
        private ScriptLanguage DetectScriptLanguage(string scriptPath, string content)
        {
            var extension = Path.GetExtension(scriptPath).ToLowerInvariant();

            return extension switch;
            {
                ".ps1" => ScriptLanguage.PowerShell,
                ".py" => ScriptLanguage.Python,
                ".bat" or ".cmd" => ScriptLanguage.Batch,
                ".js" or ".ts" => ScriptLanguage.JavaScript,
                ".cs" or ".csx" => ScriptLanguage.CSharp,
                _ => DetectLanguageFromContent(content)
            };
        }

        /// <summary>
        /// Creates execution context for script;
        /// </summary>
        private ScriptExecutionContext CreateExecutionContext(
            string scriptId,
            string scriptPath,
            string[] arguments,
            ExecutionMode executionMode,
            int timeout)
        {
            var context = new ScriptExecutionContext;
            {
                ScriptId = scriptId,
                ScriptPath = scriptPath,
                Arguments = arguments,
                ExecutionMode = executionMode,
                Timeout = timeout,
                StartTime = DateTime.UtcNow,
                EnvironmentVariables = GetEnvironmentVariables(),
                WorkingDirectory = GetWorkingDirectory(executionMode),
                UserContext = _securityManager.GetCurrentUserContext()
            };

            lock (_lock)
            {
                _executionContexts[scriptId] = context;
            }

            return context;
        }

        /// <summary>
        /// Gets all currently running scripts;
        /// </summary>
        public IReadOnlyDictionary<string, ScriptExecutionStatus> GetRunningScripts()
        {
            var statuses = new Dictionary<string, ScriptExecutionStatus>();

            lock (_lock)
            {
                foreach (var kvp in _executionContexts)
                {
                    var context = kvp.Value;
                    var isRunning = _runningProcesses.ContainsKey(kvp.Key) ||
                                   _activeRuntimes.ContainsKey(kvp.Key);

                    statuses[kvp.Key] = new ScriptExecutionStatus;
                    {
                        ScriptId = kvp.Key,
                        ScriptPath = context.ScriptPath,
                        StartTime = context.StartTime,
                        IsRunning = isRunning,
                        ExecutionMode = context.ExecutionMode,
                        ElapsedTime = DateTime.UtcNow - context.StartTime;
                    };
                }
            }

            return statuses;
        }

        /// <summary>
        /// Stops a running script;
        /// </summary>
        public bool StopScript(string scriptId)
        {
            bool stopped = false;

            lock (_lock)
            {
                if (_runningProcesses.TryGetValue(scriptId, out var process))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            stopped = true;
                            _logger.LogInformation("Stopped script: {ScriptId}", scriptId);
                        }
                        _runningProcesses.Remove(scriptId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to stop script process: {ScriptId}", scriptId);
                    }
                }

                if (_activeRuntimes.TryGetValue(scriptId, out var runtime))
                {
                    try
                    {
                        runtime.Dispose();
                        stopped = true;
                        _logger.LogInformation("Stopped script runtime: {ScriptId}", scriptId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to stop script runtime: {ScriptId}", scriptId);
                    }
                    finally
                    {
                        _activeRuntimes.Remove(scriptId);
                    }
                }
            }

            return stopped;
        }

        /// <summary>
        /// Gets execution metrics;
        /// </summary>
        public ScriptExecutionMetrics GetMetrics() => _metrics;

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose pattern;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _monitoringTimer?.Dispose();

                    lock (_lock)
                    {
                        // Kill all running processes;
                        foreach (var process in _runningProcesses.Values)
                        {
                            try
                            {
                                if (!process.HasExited)
                                    process.Kill();
                                process.Dispose();
                            }
                            catch { /* Ignore */ }
                        }
                        _runningProcesses.Clear();

                        // Dispose all runtimes;
                        foreach (var runtime in _activeRuntimes.Values)
                        {
                            try { runtime.Dispose(); } catch { /* Ignore */ }
                        }
                        _activeRuntimes.Clear();

                        _executionContexts.Clear();
                    }
                }

                _isDisposed = true;
            }
        }

        // Helper methods;
        private string GenerateScriptId(string baseName) => $"{baseName}_{Guid.NewGuid():N}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        private async Task<string> CalculateFileHashAsync(string filePath) => await _securityManager.CalculateFileHashAsync(filePath);
        private async Task ValidateScriptContentAsync(string scriptPath, string content) => await _scriptValidator.ValidateAsync(scriptPath, content);
        private async Task ValidateInlineScriptSecurityAsync(string content, ScriptLanguage language, ExecutionMode mode) => await _securityManager.ValidateInlineScriptAsync(content, language, mode);
        private void ConfigurePowerShellExecutionPolicy(System.Management.Automation.PowerShell powerShell, ExecutionMode mode) { /* Implementation */ }
        private void ConfigureProcessSecurity(ProcessStartInfo startInfo, ExecutionMode mode) { /* Implementation */ }
        private string GetPythonExecutablePath() => FindExecutableInPath("python") ?? FindExecutableInPath("python3");
        private string GetNodeExecutablePath() => FindExecutableInPath("node");
        private string FindExecutableInPath(string executable) { /* Implementation */ return null; }
        private async Task<string> GetPythonVersionAsync(string pythonPath) { /* Implementation */ return await Task.FromResult("Unknown"); }
        private async Task<string> GetNodeVersionAsync(string nodePath) { /* Implementation */ return await Task.FromResult("Unknown"); }
        private async Task<string> GetDotNetVersionAsync() { /* Implementation */ return await Task.FromResult("Unknown"); }
        private async Task<string> CompileCSharpScriptAsync(string scriptContent) { /* Implementation */ return await Task.FromResult(string.Empty); }
        private ScriptLanguage DetectLanguageFromContent(string content) { /* Implementation */ return ScriptLanguage.Unknown; }
        private Dictionary<string, string> GetEnvironmentVariables() => Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => e.Key.ToString(), e => e.Value?.ToString() ?? string.Empty);
        private string GetWorkingDirectory(ExecutionMode mode) => mode == ExecutionMode.Sandboxed ? Path.Combine(Path.GetTempPath(), "NEDA_Sandbox") : Directory.GetCurrentDirectory();
        private void CleanupExecutionContext(string scriptId) { lock (_lock) { _executionContexts.Remove(scriptId); } }
        private void UpdateMetrics(ScriptExecutionResult result) { lock (_lock) { _metrics.Update(result); } }
    }

    /// <summary>
    /// Script execution modes;
    /// </summary>
    public enum ExecutionMode;
    {
        Normal,
        Elevated,
        Sandboxed,
        Debug;
    }

    /// <summary>
    /// Supported script languages;
    /// </summary>
    public enum ScriptLanguage;
    {
        Unknown,
        PowerShell,
        Python,
        Batch,
        JavaScript,
        CSharp,
        Bash;
    }

    /// <summary>
    /// Script execution result;
    /// </summary>
    public class ScriptExecutionResult;
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string ErrorOutput { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime ExecutionEndTime { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Script execution context;
    /// </summary>
    public class ScriptExecutionContext;
    {
        public string ScriptId { get; set; }
        public string ScriptPath { get; set; }
        public string[] Arguments { get; set; }
        public ExecutionMode ExecutionMode { get; set; }
        public ScriptLanguage ScriptLanguage { get; set; }
        public int Timeout { get; set; }
        public DateTime StartTime { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; }
        public string WorkingDirectory { get; set; }
        public object UserContext { get; set; }
    }

    /// <summary>
    /// Script execution status;
    /// </summary>
    public class ScriptExecutionStatus;
    {
        public string ScriptId { get; set; }
        public string ScriptPath { get; set; }
        public DateTime StartTime { get; set; }
        public bool IsRunning { get; set; }
        public ExecutionMode ExecutionMode { get; set; }
        public TimeSpan ElapsedTime { get; set; }
    }

    /// <summary>
    /// Script execution metrics;
    /// </summary>
    public class ScriptExecutionMetrics;
    {
        public int TotalExecutions { get; private set; }
        public int SuccessfulExecutions { get; private set; }
        public int FailedExecutions { get; private set; }
        public TimeSpan TotalExecutionTime { get; private set; }
        public TimeSpan AverageExecutionTime => TotalExecutions > 0 ? TimeSpan.FromMilliseconds(TotalExecutionTime.TotalMilliseconds / TotalExecutions) : TimeSpan.Zero;
        public Dictionary<ScriptLanguage, int> ExecutionsByLanguage { get; } = new Dictionary<ScriptLanguage, int>();
        public Dictionary<ExecutionMode, int> ExecutionsByMode { get; } = new Dictionary<ExecutionMode, int>();

        public void Update(ScriptExecutionResult result)
        {
            TotalExecutions++;

            if (result.Success)
                SuccessfulExecutions++;
            else;
                FailedExecutions++;

            TotalExecutionTime += result.ExecutionTime;
        }
    }

    /// <summary>
    /// Process execution result;
    /// </summary>
    public class ProcessExecutionResult;
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public int ProcessId { get; set; }
    }

    /// <summary>
    /// Script execution event arguments;
    /// </summary>
    public class ScriptExecutionEventArgs : EventArgs;
    {
        public string ScriptId { get; }
        public string ScriptPath { get; }
        public ScriptExecutionContext Context { get; }
        public ScriptExecutionResult Result { get; }

        public ScriptExecutionEventArgs(string scriptId, string scriptPath, ScriptExecutionContext context)
        {
            ScriptId = scriptId;
            ScriptPath = scriptPath;
            Context = context;
        }

        public ScriptExecutionEventArgs(string scriptId, string scriptPath, ScriptExecutionContext context, ScriptExecutionResult result)
            : this(scriptId, scriptPath, context)
        {
            Result = result;
        }
    }

    /// <summary>
    /// Script error event arguments;
    /// </summary>
    public class ScriptErrorEventArgs : EventArgs;
    {
        public string ScriptId { get; }
        public string ScriptPath { get; }
        public Exception Exception { get; }
        public ScriptExecutionContext Context { get; }

        public ScriptErrorEventArgs(string scriptId, string scriptPath, Exception exception, ScriptExecutionContext context)
        {
            ScriptId = scriptId;
            ScriptPath = scriptPath;
            Exception = exception;
            Context = context;
        }
    }

    /// <summary>
    /// Script output event arguments;
    /// </summary>
    public class ScriptOutputEventArgs : EventArgs;
    {
        public string ProcessId { get; }
        public string Output { get; }
        public bool IsError { get; }

        public ScriptOutputEventArgs(string processId, string output, bool isError)
        {
            ProcessId = processId;
            Output = output;
            IsError = isError;
        }
    }

    /// <summary>
    /// Script executor interface;
    /// </summary>
    public interface IScriptExecutor;
    {
        Task<ScriptExecutionResult> ExecuteScriptAsync(string scriptPath, string[] arguments, ExecutionMode executionMode, int timeout);
        Task<ScriptExecutionResult> ExecuteInlineScriptAsync(string scriptContent, ScriptLanguage language, string[] arguments, ExecutionMode executionMode, int timeout);
        IReadOnlyDictionary<string, ScriptExecutionStatus> GetRunningScripts();
        bool StopScript(string scriptId);
        ScriptExecutionMetrics GetMetrics();

        event EventHandler<ScriptExecutionEventArgs> ScriptStarted;
        event EventHandler<ScriptExecutionEventArgs> ScriptCompleted;
        event EventHandler<ScriptErrorEventArgs> ScriptError;
        event EventHandler<ScriptOutputEventArgs> OutputReceived;
    }

    /// <summary>
    /// Script validator interface;
    /// </summary>
    public interface IScriptValidator;
    {
        Task ValidateAsync(string scriptPath, string content);
    }

    /// <summary>
    /// Script runtime factory interface;
    /// </summary>
    public interface IScriptRuntimeFactory;
    {
        IScriptRuntime CreateRuntime(ScriptLanguage language, ExecutionMode mode);
    }

    /// <summary>
    /// Script runtime interface;
    /// </summary>
    public interface IScriptRuntime : IDisposable
    {
        Task<ScriptExecutionResult> ExecuteAsync(string script, string[] arguments);
    }

    /// <summary>
    /// Script result processor interface;
    /// </summary>
    public interface IScriptResultProcessor;
    {
        Task ProcessResultAsync(ScriptExecutionResult result, ScriptExecutionContext context);
    }
}
