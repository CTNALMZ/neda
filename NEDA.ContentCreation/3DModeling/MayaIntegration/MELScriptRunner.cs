using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.EngineIntegration.Unreal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.ContentCreation.AnimationTools.MayaIntegration.MELScriptRunner;

namespace NEDA.ContentCreation.AnimationTools.MayaIntegration;
{
    /// <summary>
    /// Maya Embedded Language (MEL) script execution engine for Maya automation;
    /// Provides secure and efficient execution of MEL scripts with advanced error handling;
    /// </summary>
    public class MELScriptRunner : IMELScriptRunner, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly Process _mayaProcess;
        private readonly string _mayaExecutablePath;
        private readonly StringBuilder _outputBuffer;
        private readonly SemaphoreSlim _executionLock;
        private readonly MELScriptValidator _scriptValidator;
        private bool _isDisposed;
        private const int DEFAULT_TIMEOUT_MS = 30000;

        /// <summary>
        /// Event raised when script execution completes;
        /// </summary>
        public event EventHandler<MELExecutionResult> ScriptExecuted;

        /// <summary>
        /// Event raised when script execution fails;
        /// </summary>
        public event EventHandler<MELExecutionError> ScriptExecutionFailed;

        /// <summary>
        /// Event raised for real-time script output;
        /// </summary>
        public event EventHandler<string> OutputReceived;

        /// <summary>
        /// Initializes a new instance of MELScriptRunner;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="securityManager">Security manager for script validation</param>
        /// <param name="mayaPath">Path to Maya executable</param>
        public MELScriptRunner(ILogger logger, ISecurityManager securityManager, string mayaPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));

            _mayaExecutablePath = string.IsNullOrEmpty(mayaPath)
                ? FindMayaInstallation()
                : mayaPath;

            if (string.IsNullOrEmpty(_mayaExecutablePath))
            {
                throw new MayaNotFoundException("Maya installation not found. Please specify Maya path.");
            }

            _outputBuffer = new StringBuilder();
            _executionLock = new SemaphoreSlim(1, 1);
            _scriptValidator = new MELScriptValidator();
            _mayaProcess = InitializeMayaProcess();

            _logger.LogInformation($"MELScriptRunner initialized with Maya at: {_mayaExecutablePath}");
        }

        /// <summary>
        /// Executes a MEL script file;
        /// </summary>
        /// <param name="scriptPath">Path to the MEL script file</param>
        /// <param name="arguments">Script execution arguments</param>
        /// <param name="timeoutMs">Execution timeout in milliseconds</param>
        /// <returns>Execution result with output and status</returns>
        public async Task<MELExecutionResult> ExecuteScriptFileAsync(string scriptPath,
            Dictionary<string, object> arguments = null,
            int timeoutMs = DEFAULT_TIMEOUT_MS)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                throw new ArgumentException("Script path cannot be null or empty", nameof(scriptPath));

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"MEL script file not found: {scriptPath}");

            await _executionLock.WaitAsync();
            try
            {
                _logger.LogDebug($"Executing MEL script file: {scriptPath}");

                // Security validation;
                var securityResult = await ValidateScriptSecurityAsync(scriptPath);
                if (!securityResult.IsAllowed)
                {
                    throw new SecurityException($"Script security validation failed: {securityResult.Reason}");
                }

                // Read and validate script;
                var scriptContent = await File.ReadAllTextAsync(scriptPath, Encoding.UTF8);
                var validationResult = _scriptValidator.ValidateScript(scriptContent);

                if (!validationResult.IsValid)
                {
                    throw new InvalidMELScriptException($"Script validation failed: {validationResult.ErrorMessage}");
                }

                // Prepare execution command;
                var command = BuildScriptExecutionCommand(scriptPath, arguments);

                // Execute script;
                return await ExecuteCommandInternalAsync(command, timeoutMs, scriptPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to execute MEL script file: {scriptPath}");
                OnScriptExecutionFailed(new MELExecutionError(scriptPath, ex));
                throw new MELExecutionException($"Failed to execute MEL script: {scriptPath}", ex);
            }
            finally
            {
                _executionLock.Release();
            }
        }

        /// <summary>
        /// Executes MEL script content directly;
        /// </summary>
        /// <param name="scriptContent">MEL script content</param>
        /// <param name="scriptName">Name for the script (for logging)</param>
        /// <param name="timeoutMs">Execution timeout in milliseconds</param>
        /// <returns>Execution result with output and status</returns>
        public async Task<MELExecutionResult> ExecuteScriptContentAsync(string scriptContent,
            string scriptName = "InlineScript",
            int timeoutMs = DEFAULT_TIMEOUT_MS)
        {
            if (string.IsNullOrWhiteSpace(scriptContent))
                throw new ArgumentException("Script content cannot be null or empty", nameof(scriptContent));

            await _executionLock.WaitAsync();
            try
            {
                _logger.LogDebug($"Executing inline MEL script: {scriptName}");

                // Security validation;
                var securityResult = await ValidateScriptContentSecurityAsync(scriptContent);
                if (!securityResult.IsAllowed)
                {
                    throw new SecurityException($"Script content security validation failed: {securityResult.Reason}");
                }

                // Validate script syntax;
                var validationResult = _scriptValidator.ValidateScript(scriptContent);
                if (!validationResult.IsValid)
                {
                    throw new InvalidMELScriptException($"Script validation failed: {validationResult.ErrorMessage}");
                }

                // Create temporary script file;
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"mel_script_{Guid.NewGuid()}.mel");
                await File.WriteAllTextAsync(tempScriptPath, scriptContent, Encoding.UTF8);

                try
                {
                    // Execute temporary script;
                    var command = BuildInlineScriptCommand(tempScriptPath);
                    var result = await ExecuteCommandInternalAsync(command, timeoutMs, scriptName);

                    // Clean up;
                    File.Delete(tempScriptPath);

                    return result;
                }
                catch
                {
                    // Ensure cleanup on error;
                    if (File.Exists(tempScriptPath))
                        File.Delete(tempScriptPath);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to execute inline MEL script: {scriptName}");
                OnScriptExecutionFailed(new MELExecutionError(scriptName, ex));
                throw new MELExecutionException($"Failed to execute inline MEL script: {scriptName}", ex);
            }
            finally
            {
                _executionLock.Release();
            }
        }

        /// <summary>
        /// Executes multiple MEL scripts in batch;
        /// </summary>
        /// <param name="scripts">Collection of scripts to execute</param>
        /// <param name="parallelExecution">Whether to execute scripts in parallel</param>
        /// <returns>Collection of execution results</returns>
        public async Task<IEnumerable<MELExecutionResult>> ExecuteBatchAsync(
            IEnumerable<MELScriptBatchItem> scripts,
            bool parallelExecution = false)
        {
            if (scripts == null)
                throw new ArgumentNullException(nameof(scripts));

            var scriptList = scripts.ToList();
            if (!scriptList.Any())
                return Enumerable.Empty<MELExecutionResult>();

            _logger.LogInformation($"Executing batch of {scriptList.Count} MEL scripts");

            if (parallelExecution)
            {
                var tasks = scriptList.Select(script =>
                    ExecuteBatchItemAsync(script));
                return await Task.WhenAll(tasks);
            }
            else;
            {
                var results = new List<MELExecutionResult>();
                foreach (var script in scriptList)
                {
                    var result = await ExecuteBatchItemAsync(script);
                    results.Add(result);
                }
                return results;
            }
        }

        /// <summary>
        /// Validates MEL script syntax;
        /// </summary>
        /// <param name="scriptContent">MEL script content to validate</param>
        /// <returns>Validation result with details</returns>
        public MELValidationResult ValidateMELScript(string scriptContent)
        {
            return _scriptValidator.ValidateScript(scriptContent);
        }

        /// <summary>
        /// Gets Maya version information;
        /// </summary>
        /// <returns>Maya version details</returns>
        public async Task<MayaVersionInfo> GetMayaVersionAsync()
        {
            await _executionLock.WaitAsync();
            try
            {
                var versionScript = "about -version;";
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"version_check_{Guid.NewGuid()}.mel");
                await File.WriteAllTextAsync(tempScriptPath, versionScript, Encoding.UTF8);

                try
                {
                    var command = $"-command \"source \\\"{tempScriptPath.Replace("\\", "\\\\")}\\\"\"";
                    var result = await ExecuteCommandInternalAsync(command, 10000, "VersionCheck");

                    return ParseVersionInfo(result.Output);
                }
                finally
                {
                    if (File.Exists(tempScriptPath))
                        File.Delete(tempScriptPath);
                }
            }
            finally
            {
                _executionLock.Release();
            }
        }

        /// <summary>
        /// Checks if Maya is running and responsive;
        /// </summary>
        /// <returns>True if Maya is running, false otherwise</returns>
        public async Task<bool> IsMayaRunningAsync()
        {
            try
            {
                var testScript = "print \"Maya is responsive\\n\";";
                var result = await ExecuteScriptContentAsync(testScript, "HealthCheck", 5000);
                return result.Success && result.Output.Contains("Maya is responsive");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Executes Python commands within Maya (Maya's Python)
        /// </summary>
        /// <param name="pythonCode">Python code to execute</param>
        /// <param name="timeoutMs">Execution timeout</param>
        /// <returns>Execution result</returns>
        public async Task<MELExecutionResult> ExecutePythonInMayaAsync(string pythonCode, int timeoutMs = DEFAULT_TIMEOUT_MS)
        {
            if (string.IsNullOrWhiteSpace(pythonCode))
                throw new ArgumentException("Python code cannot be null or empty", nameof(pythonCode));

            // Convert Python to MEL python command;
            var melCommand = $@"python(""{EscapePythonCode(pythonCode)}"");";
            return await ExecuteScriptContentAsync(melCommand, "PythonInMaya", timeoutMs);
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private Process InitializeMayaProcess()
        {
            return new Process;
            {
                StartInfo = new ProcessStartInfo;
                {
                    FileName = _mayaExecutablePath,
                    Arguments = "-batch", // Run Maya in batch mode;
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_mayaExecutablePath)
                },
                EnableRaisingEvents = true;
            };
        }

        private async Task<MELExecutionResult> ExecuteCommandInternalAsync(string command,
            int timeoutMs,
            string scriptIdentifier)
        {
            _outputBuffer.Clear();

            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    if (!_mayaProcess.Start())
                    {
                        throw new MELExecutionException("Failed to start Maya process");
                    }

                    // Write command to Maya's standard input;
                    await _mayaProcess.StandardInput.WriteLineAsync(command);
                    await _mayaProcess.StandardInput.WriteLineAsync("quit;"); // Exit Maya after execution;
                    await _mayaProcess.StandardInput.FlushAsync();

                    // Read output asynchronously;
                    var outputTask = ReadOutputAsync(_mayaProcess.StandardOutput, cts.Token);
                    var errorTask = ReadOutputAsync(_mayaProcess.StandardError, cts.Token);

                    await Task.WhenAll(outputTask, errorTask);

                    // Wait for process exit;
                    await _mayaProcess.WaitForExitAsync(cts.Token);

                    var output = _outputBuffer.ToString();
                    var success = _mayaProcess.ExitCode == 0 && !output.Contains("Error:");

                    var result = new MELExecutionResult;
                    {
                        ScriptIdentifier = scriptIdentifier,
                        Success = success,
                        Output = output,
                        ExitCode = _mayaProcess.ExitCode,
                        ExecutionTime = DateTime.UtcNow // Will be set by caller;
                    };

                    OnScriptExecuted(result);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    _mayaProcess.Kill(true);
                    throw new MELExecutionTimeoutException($"MEL script execution timed out after {timeoutMs}ms");
                }
                catch (Exception ex)
                {
                    _mayaProcess.Kill(true);
                    throw new MELExecutionException($"Failed to execute MEL command: {ex.Message}", ex);
                }
            }
        }

        private async Task ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            try
            {
                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null)
                    {
                        _outputBuffer.AppendLine(line);
                        OnOutputReceived(line);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading process output");
            }
        }

        private string BuildScriptExecutionCommand(string scriptPath, Dictionary<string, object> arguments)
        {
            var escapedPath = scriptPath.Replace("\\", "\\\\");
            var commandBuilder = new StringBuilder();

            commandBuilder.Append($@"-command ""source \\""{escapedPath}\\""""");

            if (arguments != null && arguments.Any())
            {
                foreach (var arg in arguments)
                {
                    commandBuilder.Append($@" {arg.Key} {FormatArgumentValue(arg.Value)}");
                }
            }

            return commandBuilder.ToString();
        }

        private string BuildInlineScriptCommand(string scriptPath)
        {
            var escapedPath = scriptPath.Replace("\\", "\\\\");
            return $@"-command ""source \\""{escapedPath}\\""""";
        }

        private string FormatArgumentValue(object value)
        {
            if (value == null) return "\"\"";

            return value switch;
            {
                string str => $"\"{EscapeStringArgument(str)}\"",
                bool b => b ? "true" : "false",
                int i => i.ToString(),
                float f => f.ToString("F6"),
                double d => d.ToString("F12"),
                _ => $"\"{value}\""
            };
        }

        private string EscapeStringArgument(string input)
        {
            return input;
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("'", "\\'")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private string EscapePythonCode(string pythonCode)
        {
            return pythonCode;
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("'", "\\'");
        }

        private string FindMayaInstallation()
        {
            var possiblePaths = new[]
            {
                @"C:\Program Files\Autodesk\Maya2024\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2023\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2022\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2021\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2020\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2019\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2018\bin\maya.exe",
                @"/usr/autodesk/maya2024/bin/maya",
                @"/usr/autodesk/maya2023/bin/maya",
                @"/Applications/Autodesk/maya2024/Maya.app/Contents/bin/maya",
                @"/Applications/Autodesk/maya2023/Maya.app/Contents/bin/maya"
            };

            return possiblePaths.FirstOrDefault(File.Exists);
        }

        private async Task<MELScriptBatchItemResult> ExecuteBatchItemAsync(MELScriptBatchItem batchItem)
        {
            try
            {
                var result = await ExecuteScriptFileAsync(batchItem.ScriptPath, batchItem.Arguments, batchItem.TimeoutMs);
                return new MELScriptBatchItemResult;
                {
                    BatchItem = batchItem,
                    Result = result,
                    Success = result.Success;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to execute batch item: {batchItem.ScriptPath}");
                return new MELScriptBatchItemResult;
                {
                    BatchItem = batchItem,
                    Error = ex,
                    Success = false;
                };
            }
        }

        private async Task<ScriptSecurityResult> ValidateScriptSecurityAsync(string scriptPath)
        {
            var scriptContent = await File.ReadAllTextAsync(scriptPath, Encoding.UTF8);
            return await _securityManager.ValidateScriptAsync(scriptContent, ScriptType.MEL);
        }

        private async Task<ScriptSecurityResult> ValidateScriptContentSecurityAsync(string scriptContent)
        {
            return await _securityManager.ValidateScriptAsync(scriptContent, ScriptType.MEL);
        }

        private MayaVersionInfo ParseVersionInfo(string versionOutput)
        {
            // Parse Maya version from output;
            // Example: "Autodesk Maya 2024"
            var lines = versionOutput.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("Maya") && line.Any(char.IsDigit))
                {
                    var versionMatch = System.Text.RegularExpressions.Regex.Match(line, @"\d{4}");
                    if (versionMatch.Success && int.TryParse(versionMatch.Value, out int year))
                    {
                        return new MayaVersionInfo;
                        {
                            Version = year.ToString(),
                            FullVersion = line.Trim(),
                            IsSupported = year >= 2018;
                        };
                    }
                }
            }

            return new MayaVersionInfo;
            {
                Version = "Unknown",
                FullVersion = versionOutput,
                IsSupported = false;
            };
        }

        private void OnScriptExecuted(MELExecutionResult result)
        {
            ScriptExecuted?.Invoke(this, result);
        }

        private void OnScriptExecutionFailed(MELExecutionError error)
        {
            ScriptExecutionFailed?.Invoke(this, error);
        }

        private void OnOutputReceived(string output)
        {
            OutputReceived?.Invoke(this, output);
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _mayaProcess?.Dispose();
                    _executionLock?.Dispose();
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Supporting Classes and Interfaces;

        /// <summary>
        /// Interface for MEL script runner;
        /// </summary>
        public interface IMELScriptRunner;
        {
            Task<MELExecutionResult> ExecuteScriptFileAsync(string scriptPath,
                Dictionary<string, object> arguments = null,
                int timeoutMs = DEFAULT_TIMEOUT_MS);

            Task<MELExecutionResult> ExecuteScriptContentAsync(string scriptContent,
                string scriptName = "InlineScript",
                int timeoutMs = DEFAULT_TIMEOUT_MS);

            Task<IEnumerable<MELExecutionResult>> ExecuteBatchAsync(
                IEnumerable<MELScriptBatchItem> scripts,
                bool parallelExecution = false);
        }

        /// <summary>
        /// MEL script execution result;
        /// </summary>
        public class MELExecutionResult;
        {
            public string ScriptIdentifier { get; set; }
            public bool Success { get; set; }
            public string Output { get; set; }
            public int ExitCode { get; set; }
            public DateTime ExecutionTime { get; set; }
            public TimeSpan Duration { get; set; }
        }

        /// <summary>
        /// MEL script execution error;
        /// </summary>
        public class MELExecutionError;
        {
            public string ScriptIdentifier { get; }
            public Exception Exception { get; }
            public DateTime ErrorTime { get; }

            public MELExecutionError(string scriptIdentifier, Exception exception)
            {
                ScriptIdentifier = scriptIdentifier;
                Exception = exception;
                ErrorTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// MEL script validation result;
        /// </summary>
        public class MELValidationResult;
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
            public List<MELValidationIssue> Issues { get; set; } = new List<MELValidationIssue>();
        }

        /// <summary>
        /// MEL validation issue;
        /// </summary>
        public class MELValidationIssue;
        {
            public int LineNumber { get; set; }
            public int ColumnNumber { get; set; }
            public string IssueType { get; set; }
            public string Description { get; set; }
            public string Severity { get; set; } // Error, Warning, Info;
        }

        /// <summary>
        /// Batch item for MEL script execution;
        /// </summary>
        public class MELScriptBatchItem;
        {
            public string ScriptPath { get; set; }
            public Dictionary<string, object> Arguments { get; set; }
            public int TimeoutMs { get; set; } = DEFAULT_TIMEOUT_MS;
            public int Priority { get; set; } = 1;
        }

        /// <summary>
        /// Batch item execution result;
        /// </summary>
        public class MELScriptBatchItemResult;
        {
            public MELScriptBatchItem BatchItem { get; set; }
            public MELExecutionResult Result { get; set; }
            public Exception Error { get; set; }
            public bool Success { get; set; }
        }

        /// <summary>
        /// Maya version information;
        /// </summary>
        public class MayaVersionInfo;
        {
            public string Version { get; set; }
            public string FullVersion { get; set; }
            public bool IsSupported { get; set; }
        }

        /// <summary>
        /// Script security validation result;
        /// </summary>
        public class ScriptSecurityResult;
        {
            public bool IsAllowed { get; set; }
            public string Reason { get; set; }
            public List<string> SecurityConcerns { get; set; } = new List<string>();
        }

        /// <summary>
        /// Script types for security validation;
        /// </summary>
        public enum ScriptType;
        {
            MEL,
            Python,
            JavaScript;
        }

        #endregion;

        #region Custom Exceptions;

        /// <summary>
        /// Base exception for MEL execution errors;
        /// </summary>
        public class MELExecutionException : Exception
        {
            public MELExecutionException(string message) : base(message) { }
            public MELExecutionException(string message, Exception innerException) : base(message, innerException) { }
        }

        /// <summary>
        /// Exception for MEL script syntax errors;
        /// </summary>
        public class InvalidMELScriptException : MELExecutionException;
        {
            public InvalidMELScriptException(string message) : base(message) { }
        }

        /// <summary>
        /// Exception for MEL script execution timeout;
        /// </summary>
        public class MELExecutionTimeoutException : MELExecutionException;
        {
            public MELExecutionTimeoutException(string message) : base(message) { }
        }

        /// <summary>
        /// Exception when Maya installation is not found;
        /// </summary>
        public class MayaNotFoundException : MELExecutionException;
        {
            public MayaNotFoundException(string message) : base(message) { }
        }

        #endregion;
    }

    /// <summary>
    /// MEL script validator for syntax and security checking;
    /// </summary>
    internal class MELScriptValidator;
    {
        private readonly List<string> _dangerousCommands = new List<string>
        {
            "system",
            "exec",
            "eval",
            "shell",
            "python(\"import os;",
            "python(\"__import__",
            "python(\"open(",
            "python(\"file(",
            "python(\"exec(",
            "python(\"eval(",
            "deleteFile",
            "savePreferences"
        };

        private readonly List<string> _requiredSemicolons = new List<string>
        {
            "proc",
            "global proc",
            "string $",
            "int $",
            "float $",
            "vector $"
        };

        public MELValidationResult ValidateScript(string scriptContent)
        {
            var result = new MELValidationResult();
            var lines = scriptContent.Split('\n');

            // Check for dangerous commands;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var lineNumber = i + 1;

                foreach (var dangerousCmd in _dangerousCommands)
                {
                    if (line.Contains(dangerousCmd, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add(new MELValidationIssue;
                        {
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf(dangerousCmd, StringComparison.OrdinalIgnoreCase) + 1,
                            IssueType = "Security",
                            Description = $"Potentially dangerous command detected: {dangerousCmd}",
                            Severity = "Error"
                        });
                    }
                }

                // Check for missing semicolons;
                if (!string.IsNullOrWhiteSpace(line) &&
                    !line.Trim().StartsWith("//") && // Not a comment;
                    !line.Trim().StartsWith("#") && // Not a Python comment;
                    !line.Contains("{") && // Not start of block;
                    !line.Contains("}") && // Not end of block;
                    !line.EndsWith(";") && // Missing semicolon;
                    !line.EndsWith("{") && // Not block start;
                    !line.EndsWith("}"))   // Not block end;
                {
                    // Check if this line should have a semicolon;
                    var shouldHaveSemicolon = _requiredSemicolons.Any(cmd =>
                        line.Trim().StartsWith(cmd, StringComparison.OrdinalIgnoreCase));

                    if (shouldHaveSemicolon)
                    {
                        result.Issues.Add(new MELValidationIssue;
                        {
                            LineNumber = lineNumber,
                            ColumnNumber = line.Length,
                            IssueType = "Syntax",
                            Description = "Missing semicolon at end of statement",
                            Severity = "Warning"
                        });
                    }
                }
            }

            // Basic syntax validation;
            if (!scriptContent.Contains(";") && scriptContent.Length > 0)
            {
                result.Issues.Add(new MELValidationIssue;
                {
                    LineNumber = 1,
                    ColumnNumber = 1,
                    IssueType = "Syntax",
                    Description = "Script appears to have no valid MEL statements",
                    Severity = "Warning"
                });
            }

            result.IsValid = !result.Issues.Any(i => i.Severity == "Error");
            result.ErrorMessage = result.IsValid ? null :
                $"Script validation failed with {result.Issues.Count(i => i.Severity == "Error")} error(s)";

            return result;
        }
    }
}
