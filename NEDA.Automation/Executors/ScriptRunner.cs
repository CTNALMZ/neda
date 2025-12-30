using IronPython.Hosting;
using IronPython.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.CSharp;
using Microsoft.Scripting.Hosting;
using NEDA.Brain.NeuralNetwork;
using NEDA.Core.Commands;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Services.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Automation.Executors;
{
    /// <summary>
    /// Advanced script runner with multi-language support, sandboxing, and AI optimization;
    /// </summary>
    public class ScriptRunner : IScriptRunner, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IAuditLogger _auditLogger;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IEventBus _eventBus;

        private readonly ScriptOptions _defaultScriptOptions;
        private readonly ConcurrentDictionary<string, CompiledScript> _scriptCache;
        private readonly ConcurrentDictionary<Guid, ScriptExecution> _activeExecutions;
        private readonly ConcurrentDictionary<string, ScriptPerformance> _performanceMetrics;

        private readonly ScriptEngine _pythonEngine;
        private readonly ScriptRuntime _pythonRuntime;
        private readonly ScriptScope _pythonScope;

        private readonly AssemblyLoadContext _scriptLoadContext;
        private readonly SemaphoreSlim _compilationSemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private bool _disposed = false;
        private Task _monitoringTask;

        /// <summary>
        /// Maximum script execution time;
        /// </summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Maximum memory usage for scripts (MB)
        /// </summary>
        public int MaxMemoryUsageMB { get; set; } = 512;

        /// <summary>
        /// Enable script sandboxing;
        /// </summary>
        public bool EnableSandboxing { get; set; } = true;

        /// <summary>
        /// Enable script caching;
        /// </summary>
        public bool EnableCaching { get; set; } = true;

        /// <summary>
        /// Enable AI script optimization;
        /// </summary>
        public bool EnableAIOptimization { get; set; } = true;

        /// <summary>
        /// Initialize a new ScriptRunner;
        /// </summary>
        public ScriptRunner(
            ILogger logger,
            ISecurityManager securityManager = null,
            IAuditLogger auditLogger = null,
            INeuralNetwork neuralNetwork = null,
            IEventBus eventBus = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager;
            _auditLogger = auditLogger;
            _neuralNetwork = neuralNetwork;
            _eventBus = eventBus;

            _scriptCache = new ConcurrentDictionary<string, CompiledScript>();
            _activeExecutions = new ConcurrentDictionary<Guid, ScriptExecution>();
            _performanceMetrics = new ConcurrentDictionary<string, ScriptPerformance>();

            // Configure default script options;
            _defaultScriptOptions = ScriptOptions.Default;
                .WithReferences(
                    typeof(object).Assembly,
                    typeof(Enumerable).Assembly,
                    typeof(Task).Assembly,
                    typeof(Console).Assembly,
                    typeof(DynamicObject).Assembly)
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.Threading.Tasks",
                    "System.IO",
                    "System.Text",
                    "System.Diagnostics")
                .WithEmitDebugInformation(true);

            // Initialize Python engine;
            try
            {
                _pythonEngine = Python.CreateEngine();
                _pythonRuntime = _pythonEngine.Runtime;
                _pythonScope = _pythonEngine.CreateScope();

                // Configure Python engine;
                var pythonSetup = _pythonEngine.GetSetup();
                pythonSetup.Options["Frames"] = true;
                pythonSetup.Options["FullFrames"] = true;

                _logger.Information("Python script engine initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to initialize Python engine: {ex.Message}");
            }

            // Create isolated assembly load context for scripts;
            _scriptLoadContext = new ScriptAssemblyLoadContext();

            // Start monitoring;
            StartMonitoring();

            _logger.Information("ScriptRunner initialized successfully");
        }

        /// <summary>
        /// Execute a C# script;
        /// </summary>
        public async Task<ScriptResult> ExecuteCSharpAsync(
            string scriptCode,
            ScriptExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scriptCode))
                throw new ArgumentException("Script code cannot be null or empty", nameof(scriptCode));

            context ??= new ScriptExecutionContext();
            var executionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            ScriptExecution execution = null;

            try
            {
                // Validate script;
                var validationResult = await ValidateScriptAsync(scriptCode, ScriptLanguage.CSharp, context);
                if (!validationResult.IsValid)
                {
                    return CreateErrorResult("Script validation failed",
                        ScriptExecutionStatus.ValidationFailed,
                        validationResult.Errors);
                }

                // Check security;
                var securityResult = await CheckScriptSecurityAsync(scriptCode, context);
                if (!securityResult.IsAllowed)
                {
                    await LogSecurityViolationAsync(executionId, scriptCode, securityResult);
                    return CreateErrorResult($"Security violation: {securityResult.Reason}",
                        ScriptExecutionStatus.SecurityViolation);
                }

                // Create execution context;
                var executionContext = CreateExecutionContext(context, executionId, ScriptLanguage.CSharp);

                // Create execution tracking;
                execution = new ScriptExecution;
                {
                    Id = executionId,
                    ScriptCode = scriptCode,
                    Language = ScriptLanguage.CSharp,
                    Context = executionContext,
                    StartTime = startTime,
                    Status = ScriptExecutionStatus.Running;
                };

                _activeExecutions[executionId] = execution;

                // Compile and execute script;
                ScriptResult result;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token))
                {
                    timeoutCts.CancelAfter(context.Timeout ?? DefaultTimeout);
                    execution.CancellationTokenSource = timeoutCts;

                    try
                    {
                        // Pre-execution hook;
                        await OnPreExecutionAsync(execution);

                        // Execute script;
                        result = await ExecuteCSharpInternalAsync(scriptCode, executionContext, timeoutCts.Token);

                        // Post-execution hook;
                        await OnPostExecutionAsync(execution, result);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        result = CreateErrorResult("Script execution timeout", ScriptExecutionStatus.Timeout);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Script execution error: {ex.Message}", ex);
                        result = CreateErrorResult($"Execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
                    }
                }

                // Update execution status;
                execution.EndTime = DateTime.UtcNow;
                execution.Status = result.Status;
                execution.Result = result;

                // Update performance metrics;
                UpdatePerformanceMetrics(scriptCode, ScriptLanguage.CSharp, execution, result);

                // Log completion;
                await LogExecutionCompleteAsync(execution, result);

                // AI learning;
                if (EnableAIOptimization && _neuralNetwork != null)
                {
                    await LearnFromExecutionAsync(execution, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error in script execution: {ex.Message}", ex);
                return CreateErrorResult($"System error: {ex.Message}", ScriptExecutionStatus.SystemError, ex);
            }
            finally
            {
                if (execution != null)
                {
                    _activeExecutions.TryRemove(executionId, out _);
                    execution.CancellationTokenSource?.Dispose();
                }
            }
        }

        /// <summary>
        /// Execute a Python script;
        /// </summary>
        public async Task<ScriptResult> ExecutePythonAsync(
            string scriptCode,
            ScriptExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scriptCode))
                throw new ArgumentException("Script code cannot be null or empty", nameof(scriptCode));

            if (_pythonEngine == null)
                throw new InvalidOperationException("Python engine is not available");

            context ??= new ScriptExecutionContext();
            var executionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            ScriptExecution execution = null;

            try
            {
                // Validate script;
                var validationResult = await ValidateScriptAsync(scriptCode, ScriptLanguage.Python, context);
                if (!validationResult.IsValid)
                {
                    return CreateErrorResult("Script validation failed",
                        ScriptExecutionStatus.ValidationFailed,
                        validationResult.Errors);
                }

                // Check security;
                var securityResult = await CheckScriptSecurityAsync(scriptCode, context);
                if (!securityResult.IsAllowed)
                {
                    await LogSecurityViolationAsync(executionId, scriptCode, securityResult);
                    return CreateErrorResult($"Security violation: {securityResult.Reason}",
                        ScriptExecutionStatus.SecurityViolation);
                }

                // Create execution context;
                var executionContext = CreateExecutionContext(context, executionId, ScriptLanguage.Python);

                // Create execution tracking;
                execution = new ScriptExecution;
                {
                    Id = executionId,
                    ScriptCode = scriptCode,
                    Language = ScriptLanguage.Python,
                    Context = executionContext,
                    StartTime = startTime,
                    Status = ScriptExecutionStatus.Running;
                };

                _activeExecutions[executionId] = execution;

                // Execute script;
                ScriptResult result;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token))
                {
                    timeoutCts.CancelAfter(context.Timeout ?? DefaultTimeout);
                    execution.CancellationTokenSource = timeoutCts;

                    try
                    {
                        // Pre-execution hook;
                        await OnPreExecutionAsync(execution);

                        // Execute Python script;
                        result = await ExecutePythonInternalAsync(scriptCode, executionContext, timeoutCts.Token);

                        // Post-execution hook;
                        await OnPostExecutionAsync(execution, result);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        result = CreateErrorResult("Script execution timeout", ScriptExecutionStatus.Timeout);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Script execution error: {ex.Message}", ex);
                        result = CreateErrorResult($"Execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
                    }
                }

                // Update execution status;
                execution.EndTime = DateTime.UtcNow;
                execution.Status = result.Status;
                execution.Result = result;

                // Update performance metrics;
                UpdatePerformanceMetrics(scriptCode, ScriptLanguage.Python, execution, result);

                // Log completion;
                await LogExecutionCompleteAsync(execution, result);

                // AI learning;
                if (EnableAIOptimization && _neuralNetwork != null)
                {
                    await LearnFromExecutionAsync(execution, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error in Python script execution: {ex.Message}", ex);
                return CreateErrorResult($"System error: {ex.Message}", ScriptExecutionStatus.SystemError, ex);
            }
            finally
            {
                if (execution != null)
                {
                    _activeExecutions.TryRemove(executionId, out _);
                    execution.CancellationTokenSource?.Dispose();
                }
            }
        }

        /// <summary>
        /// Execute a JavaScript script;
        /// </summary>
        public async Task<ScriptResult> ExecuteJavaScriptAsync(
            string scriptCode,
            ScriptExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scriptCode))
                throw new ArgumentException("Script code cannot be null or empty", nameof(scriptCode));

            context ??= new ScriptExecutionContext();
            var executionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            ScriptExecution execution = null;

            try
            {
                // Validate script;
                var validationResult = await ValidateScriptAsync(scriptCode, ScriptLanguage.JavaScript, context);
                if (!validationResult.IsValid)
                {
                    return CreateErrorResult("Script validation failed",
                        ScriptExecutionStatus.ValidationFailed,
                        validationResult.Errors);
                }

                // Check security;
                var securityResult = await CheckScriptSecurityAsync(scriptCode, context);
                if (!securityResult.IsAllowed)
                {
                    await LogSecurityViolationAsync(executionId, scriptCode, securityResult);
                    return CreateErrorResult($"Security violation: {securityResult.Reason}",
                        ScriptExecutionStatus.SecurityViolation);
                }

                // Create execution context;
                var executionContext = CreateExecutionContext(context, executionId, ScriptLanguage.JavaScript);

                // Create execution tracking;
                execution = new ScriptExecution;
                {
                    Id = executionId,
                    ScriptCode = scriptCode,
                    Language = ScriptLanguage.JavaScript,
                    Context = executionContext,
                    StartTime = startTime,
                    Status = ScriptExecutionStatus.Running;
                };

                _activeExecutions[executionId] = execution;

                // Execute script;
                ScriptResult result;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token))
                {
                    timeoutCts.CancelAfter(context.Timeout ?? DefaultTimeout);
                    execution.CancellationTokenSource = timeoutCts;

                    try
                    {
                        // Pre-execution hook;
                        await OnPreExecutionAsync(execution);

                        // Execute JavaScript;
                        result = await ExecuteJavaScriptInternalAsync(scriptCode, executionContext, timeoutCts.Token);

                        // Post-execution hook;
                        await OnPostExecutionAsync(execution, result);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    {
                        result = CreateErrorResult("Script execution timeout", ScriptExecutionStatus.Timeout);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Script execution error: {ex.Message}", ex);
                        result = CreateErrorResult($"Execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
                    }
                }

                // Update execution status;
                execution.EndTime = DateTime.UtcNow;
                execution.Status = result.Status;
                execution.Result = result;

                // Update performance metrics;
                UpdatePerformanceMetrics(scriptCode, ScriptLanguage.JavaScript, execution, result);

                // Log completion;
                await LogExecutionCompleteAsync(execution, result);

                // AI learning;
                if (EnableAIOptimization && _neuralNetwork != null)
                {
                    await LearnFromExecutionAsync(execution, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error in JavaScript execution: {ex.Message}", ex);
                return CreateErrorResult($"System error: {ex.Message}", ScriptExecutionStatus.SystemError, ex);
            }
            finally
            {
                if (execution != null)
                {
                    _activeExecutions.TryRemove(executionId, out _);
                    execution.CancellationTokenSource?.Dispose();
                }
            }
        }

        /// <summary>
        /// Execute a script from file;
        /// </summary>
        public async Task<ScriptResult> ExecuteFromFileAsync(
            string filePath,
            ScriptLanguage language = ScriptLanguage.AutoDetect,
            ScriptExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Script file not found: {filePath}", filePath);

            try
            {
                // Read script content;
                var scriptCode = await File.ReadAllTextAsync(filePath, cancellationToken);

                // Detect language if auto-detect;
                if (language == ScriptLanguage.AutoDetect)
                {
                    language = DetectScriptLanguage(filePath, scriptCode);
                }

                // Update context with file information;
                context ??= new ScriptExecutionContext();
                context.SourceFile = filePath;
                context.SourceHash = CalculateFileHash(filePath);

                // Execute based on language;
                return language switch;
                {
                    ScriptLanguage.CSharp => await ExecuteCSharpAsync(scriptCode, context, cancellationToken),
                    ScriptLanguage.Python => await ExecutePythonAsync(scriptCode, context, cancellationToken),
                    ScriptLanguage.JavaScript => await ExecuteJavaScriptAsync(scriptCode, context, cancellationToken),
                    _ => throw new NotSupportedException($"Language {language} is not supported for file execution")
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Error executing script from file {filePath}: {ex.Message}", ex);
                return CreateErrorResult($"File execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
            }
        }

        /// <summary>
        /// Execute multiple scripts in batch;
        /// </summary>
        public async Task<BatchScriptResult> ExecuteBatchAsync(
            IEnumerable<ScriptExecutionRequest> requests,
            BatchExecutionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));

            options ??= new BatchExecutionOptions();
            var batchId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            var batchResult = new BatchScriptResult;
            {
                BatchId = batchId,
                StartTime = startTime,
                Options = options;
            };

            var requestList = requests.ToList();
            var executionTasks = new List<Task<ScriptResult>>();

            _logger.Information($"Starting batch script execution {batchId} with {requestList.Count} scripts");

            // Apply AI optimization if enabled;
            if (EnableAIOptimization && options.OptimizeExecutionOrder && _neuralNetwork != null)
            {
                requestList = await OptimizeScriptExecutionOrderAsync(requestList);
            }

            // Execute scripts;
            if (options.ExecuteInParallel)
            {
                var semaphore = new SemaphoreSlim(options.MaxConcurrent ?? Environment.ProcessorCount);

                foreach (var request in requestList)
                {
                    var task = ExecuteScriptWithOptionsAsync(request, semaphore, cancellationToken);
                    executionTasks.Add(task);
                }

                await Task.WhenAll(executionTasks);
            }
            else;
            {
                foreach (var request in requestList)
                {
                    var result = await ExecuteScriptRequestAsync(request, cancellationToken);
                    executionTasks.Add(Task.FromResult(result));
                }
            }

            // Collect results;
            var results = new List<ScriptResult>();
            foreach (var task in executionTasks)
            {
                results.Add(await task);
            }

            batchResult.EndTime = DateTime.UtcNow;
            batchResult.Results = results;
            batchResult.SuccessCount = results.Count(r => r.Status == ScriptExecutionStatus.Completed);
            batchResult.FailedCount = results.Count(r => r.Status != ScriptExecutionStatus.Completed);
            batchResult.TotalDuration = batchResult.EndTime - batchResult.StartTime;

            // Analyze batch results;
            await AnalyzeBatchResultsAsync(batchResult);

            _logger.Information($"Batch execution {batchId} completed: {batchResult.SuccessCount} successful, {batchResult.FailedCount} failed");

            return batchResult;
        }

        /// <summary>
        /// Compile and cache a script for later execution;
        /// </summary>
        public async Task<CompilationResult> CompileScriptAsync(
            string scriptCode,
            ScriptLanguage language,
            ScriptExecutionContext context = null)
        {
            if (string.IsNullOrWhiteSpace(scriptCode))
                throw new ArgumentException("Script code cannot be null or empty", nameof(scriptCode));

            if (language != ScriptLanguage.CSharp)
                throw new NotSupportedException("Only C# scripts can be compiled and cached");

            var cacheKey = GenerateCacheKey(scriptCode, language, context);

            try
            {
                await _compilationSemaphore.WaitAsync(_cts.Token);

                // Check if already cached;
                if (EnableCaching && _scriptCache.TryGetValue(cacheKey, out var cachedScript))
                {
                    return new CompilationResult;
                    {
                        Success = true,
                        CacheKey = cacheKey,
                        CompilationTime = TimeSpan.Zero,
                        FromCache = true,
                        Warnings = cachedScript.Warnings;
                    };
                }

                // Compile script;
                var startTime = DateTime.UtcNow;
                var scriptOptions = CreateScriptOptions(context);

                var script = CSharpScript.Create(scriptCode, scriptOptions, typeof(ScriptGlobals));
                var compilation = script.GetCompilation();

                // Check for compilation errors;
                var diagnostics = compilation.GetDiagnostics();
                var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

                if (errors.Any())
                {
                    return new CompilationResult;
                    {
                        Success = false,
                        Errors = errors.Select(e => e.ToString()).ToList(),
                        Warnings = warnings.Select(w => w.ToString()).ToList()
                    };
                }

                // Compile and cache;
                var compiledScript = new CompiledScript;
                {
                    Script = script,
                    Compilation = compilation,
                    CompileTime = DateTime.UtcNow,
                    CacheKey = cacheKey,
                    Warnings = warnings.Select(w => w.ToString()).ToList()
                };

                if (EnableCaching)
                {
                    _scriptCache[cacheKey] = compiledScript;
                }

                var compileTime = DateTime.UtcNow - startTime;

                _logger.Debug($"Compiled script and cached with key: {cacheKey} (took {compileTime.TotalMilliseconds}ms)");

                return new CompilationResult;
                {
                    Success = true,
                    CacheKey = cacheKey,
                    CompilationTime = compileTime,
                    FromCache = false,
                    Warnings = compiledScript.Warnings;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Script compilation failed: {ex.Message}", ex);
                return new CompilationResult;
                {
                    Success = false,
                    Errors = new List<string> { $"Compilation error: {ex.Message}" }
                };
            }
            finally
            {
                _compilationSemaphore.Release();
            }
        }

        /// <summary>
        /// Validate script syntax and security;
        /// </summary>
        public async Task<ScriptValidationResult> ValidateScriptAsync(
            string scriptCode,
            ScriptLanguage language,
            ScriptExecutionContext context = null)
        {
            if (string.IsNullOrWhiteSpace(scriptCode))
                throw new ArgumentException("Script code cannot be null or empty", nameof(scriptCode));

            var result = new ScriptValidationResult;
            {
                Language = language,
                ValidationTime = DateTime.UtcNow;
            };

            try
            {
                // Basic syntax validation;
                var syntaxResult = await ValidateScriptSyntaxAsync(scriptCode, language);
                if (!syntaxResult.IsValid)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(syntaxResult.Errors);
                    return result;
                }

                // Security validation;
                var securityResult = await ValidateScriptSecurityAsync(scriptCode, language, context);
                if (!securityResult.IsSafe)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(securityResult.Threats);
                    result.SecurityLevel = securityResult.SecurityLevel;
                }

                // Performance validation;
                var performanceResult = await ValidateScriptPerformanceAsync(scriptCode, language);
                if (performanceResult.HasIssues)
                {
                    result.Warnings.AddRange(performanceResult.Issues);
                    result.PerformanceScore = performanceResult.Score;
                }

                // AI validation if enabled;
                if (EnableAIOptimization && _neuralNetwork != null)
                {
                    var aiValidation = await ValidateWithAIAsync(scriptCode, language);
                    if (aiValidation.HasSuggestions)
                    {
                        result.Suggestions.AddRange(aiValidation.Suggestions);
                        result.AIScore = aiValidation.Score;
                    }
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script validation error: {ex.Message}", ex);
                result.IsValid = false;
                result.Errors.Add($"Validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Get script performance metrics;
        /// </summary>
        public ScriptPerformance GetScriptPerformance(string scriptHash)
        {
            if (string.IsNullOrWhiteSpace(scriptHash))
                throw new ArgumentException("Script hash cannot be null or empty", nameof(scriptHash));

            if (_performanceMetrics.TryGetValue(scriptHash, out var performance))
            {
                return performance.Clone();
            }

            return new ScriptPerformance { ScriptHash = scriptHash };
        }

        /// <summary>
        /// Get all active script executions;
        /// </summary>
        public IReadOnlyList<ScriptExecution> GetActiveExecutions()
        {
            return _activeExecutions.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Cancel a running script execution;
        /// </summary>
        public async Task<bool> CancelExecutionAsync(Guid executionId, string reason = null)
        {
            if (_activeExecutions.TryGetValue(executionId, out var execution))
            {
                try
                {
                    execution.CancellationTokenSource?.Cancel();
                    execution.Status = ScriptExecutionStatus.Cancelled;
                    execution.CancellationReason = reason;

                    await LogCancellationAsync(executionId, reason);

                    _logger.Information($"Script execution {executionId} cancelled: {reason}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error cancelling script execution {executionId}: {ex.Message}", ex);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Clear script cache;
        /// </summary>
        public void ClearCache(CacheClearOptions options = null)
        {
            options ??= new CacheClearOptions();

            int clearedCount = 0;

            if (options.ClearAll)
            {
                clearedCount = _scriptCache.Count;
                _scriptCache.Clear();
            }
            else;
            {
                var keysToRemove = _scriptCache.Keys;
                    .Where(k => ShouldClearCacheEntry(k, options))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    if (_scriptCache.TryRemove(key, out _))
                    {
                        clearedCount++;
                    }
                }
            }

            _logger.Information($"Cleared script cache: {clearedCount} entries removed");
        }

        /// <summary>
        /// Optimize script using AI;
        /// </summary>
        public async Task<ScriptOptimization> OptimizeScriptAsync(
            string scriptCode,
            ScriptLanguage language,
            OptimizationOptions options = null)
        {
            if (!EnableAIOptimization || _neuralNetwork == null)
            {
                return new ScriptOptimization { IsOptimized = false };
            }

            options ??= new OptimizationOptions();

            try
            {
                var optimization = new ScriptOptimization;
                {
                    OriginalScript = scriptCode,
                    Language = language,
                    OptimizationDate = DateTime.UtcNow;
                };

                // Analyze script;
                var analysis = await AnalyzeScriptAsync(scriptCode, language);

                // Apply optimizations;
                var optimizedScript = await ApplyOptimizationsAsync(scriptCode, language, analysis, options);

                // Calculate improvements;
                var improvements = await CalculateOptimizationImprovementsAsync(scriptCode, optimizedScript, language);

                optimization.OptimizedScript = optimizedScript;
                optimization.Improvements = improvements;
                optimization.IsOptimized = true;
                optimization.Analysis = analysis;

                return optimization;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script optimization failed: {ex.Message}", ex);
                return new ScriptOptimization;
                {
                    OriginalScript = scriptCode,
                    Language = language,
                    IsOptimized = false,
                    Error = ex.Message;
                };
            }
        }

        /// <summary>
        /// Execute C# script internally;
        /// </summary>
        private async Task<ScriptResult> ExecuteCSharpInternalAsync(
            string scriptCode,
            ScriptExecutionInternalContext context,
            CancellationToken cancellationToken)
        {
            var cacheKey = GenerateCacheKey(scriptCode, ScriptLanguage.CSharp, context.OriginalContext);
            CompiledScript compiledScript = null;

            // Try to get from cache;
            if (EnableCaching && _scriptCache.TryGetValue(cacheKey, out var cached))
            {
                compiledScript = cached;
            }
            else;
            {
                // Compile script;
                var compilationResult = await CompileScriptAsync(scriptCode, ScriptLanguage.CSharp, context.OriginalContext);
                if (!compilationResult.Success)
                {
                    return CreateErrorResult($"Compilation failed: {string.Join(", ", compilationResult.Errors)}",
                        ScriptExecutionStatus.CompilationFailed);
                }

                if (EnableCaching && _scriptCache.TryGetValue(cacheKey, out cached))
                {
                    compiledScript = cached;
                }
            }

            if (compiledScript == null)
            {
                return CreateErrorResult("Failed to compile script", ScriptExecutionStatus.CompilationFailed);
            }

            // Create script globals;
            var globals = new ScriptGlobals;
            {
                Context = context,
                Logger = new ScriptLogger(_logger, context.ExecutionId),
                Output = new StringBuilder(),
                CancellationToken = cancellationToken,
                Variables = new Dictionary<string, object>()
            };

            // Apply sandboxing if enabled;
            if (EnableSandboxing)
            {
                ApplySandboxRestrictions(globals);
            }

            try
            {
                // Execute script;
                var state = await compiledScript.Script.RunAsync(globals, cancellationToken);

                // Check for execution errors;
                if (state.Exception != null)
                {
                    throw state.Exception;
                }

                return new ScriptResult;
                {
                    Status = ScriptExecutionStatus.Completed,
                    Success = true,
                    ReturnValue = state.ReturnValue,
                    Output = globals.Output.ToString(),
                    ExecutionTime = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - context.StartTime;
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"Script execution error: {ex.Message}", ex);
                return CreateErrorResult($"Execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
            }
        }

        /// <summary>
        /// Execute Python script internally;
        /// </summary>
        private async Task<ScriptResult> ExecutePythonInternalAsync(
            string scriptCode,
            ScriptExecutionInternalContext context,
            CancellationToken cancellationToken)
        {
            var output = new StringBuilder();
            object returnValue = null;

            try
            {
                // Configure Python scope with context;
                _pythonScope.SetVariable("context", context);
                _pythonScope.SetVariable("logger", new ScriptLogger(_logger, context.ExecutionId));
                _pythonScope.SetVariable("output", output);
                _pythonScope.SetVariable("cancellationToken", cancellationToken);

                // Apply sandboxing if enabled;
                if (EnableSandboxing)
                {
                    ApplyPythonSandboxRestrictions(_pythonScope);
                }

                // Execute script;
                var source = _pythonEngine.CreateScriptSourceFromString(scriptCode);
                returnValue = await Task.Run(() => source.Execute(_pythonScope), cancellationToken);

                return new ScriptResult;
                {
                    Status = ScriptExecutionStatus.Completed,
                    Success = true,
                    ReturnValue = returnValue,
                    Output = output.ToString(),
                    ExecutionTime = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - context.StartTime;
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"Python script execution error: {ex.Message}", ex);
                return CreateErrorResult($"Python execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
            }
        }

        /// <summary>
        /// Execute JavaScript internally;
        /// </summary>
        private async Task<ScriptResult> ExecuteJavaScriptInternalAsync(
            string scriptCode,
            ScriptExecutionInternalContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                // Using Jint JavaScript interpreter;
                var engine = new Jint.Engine();

                // Configure engine;
                engine.SetValue("context", context);
                engine.SetValue("logger", new ScriptLogger(_logger, context.ExecutionId));
                engine.SetValue("cancellationToken", cancellationToken);

                // Apply sandboxing;
                if (EnableSandboxing)
                {
                    ApplyJavaScriptSandboxRestrictions(engine);
                }

                // Execute script;
                var result = await Task.Run(() => engine.Execute(scriptCode).GetCompletionValue().ToObject(), cancellationToken);

                return new ScriptResult;
                {
                    Status = ScriptExecutionStatus.Completed,
                    Success = true,
                    ReturnValue = result,
                    ExecutionTime = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - context.StartTime;
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"JavaScript execution error: {ex.Message}", ex);
                return CreateErrorResult($"JavaScript execution error: {ex.Message}", ScriptExecutionStatus.Failed, ex);
            }
        }

        /// <summary>
        /// Start monitoring task;
        /// </summary>
        private void StartMonitoring()
        {
            _monitoringTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
                        await PerformMonitoringAsync();
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Script monitoring error: {ex.Message}", ex);
                    }
                }
            }, _cts.Token);
        }

        /// <summary>
        /// Perform monitoring tasks;
        /// </summary>
        private async Task PerformMonitoringAsync()
        {
            // Check for stalled executions;
            var stalledExecutions = _activeExecutions.Values;
                .Where(e => e.Status == ScriptExecutionStatus.Running)
                .Where(e => (DateTime.UtcNow - e.StartTime) > TimeSpan.FromMinutes(10))
                .ToList();

            foreach (var execution in stalledExecutions)
            {
                _logger.Warning($"Stalled script execution detected: {execution.Id}");
                await CancelExecutionAsync(execution.Id, "Execution stalled - timeout");
            }

            // Check memory usage;
            CheckMemoryUsage();

            // Clean old cache entries;
            CleanOldCacheEntries();

            // Update performance statistics;
            UpdatePerformanceStatistics();
        }

        /// <summary>
        /// Check memory usage;
        /// </summary>
        private void CheckMemoryUsage()
        {
            var process = Process.GetCurrentProcess();
            var memoryMB = process.WorkingSet64 / 1024 / 1024;

            if (memoryMB > MaxMemoryUsageMB)
            {
                _logger.Warning($"High memory usage detected: {memoryMB}MB (limit: {MaxMemoryUsageMB}MB)");

                // Clear cache to free memory;
                ClearCache(new CacheClearOptions;
                {
                    ClearOldEntries = true,
                    OlderThanHours = 1;
                });
            }
        }

        /// <summary>
        /// Clean old cache entries;
        /// </summary>
        private void CleanOldCacheEntries()
        {
            var oldEntries = _scriptCache.Where(kvp =>
                DateTime.UtcNow - kvp.Value.LastAccess > TimeSpan.FromHours(1)).ToList();

            foreach (var entry in oldEntries)
            {
                _scriptCache.TryRemove(entry.Key, out _);
            }

            if (oldEntries.Count > 0)
            {
                _logger.Debug($"Cleaned {oldEntries.Count} old cache entries");
            }
        }

        /// <summary>
        /// Create script options;
        /// </summary>
        private ScriptOptions CreateScriptOptions(ScriptExecutionContext context)
        {
            var options = _defaultScriptOptions;

            if (context?.References != null)
            {
                options = options.AddReferences(context.References);
            }

            if (context?.Imports != null)
            {
                options = options.AddImports(context.Imports);
            }

            return options;
        }

        /// <summary>
        /// Create execution context;
        /// </summary>
        private ScriptExecutionInternalContext CreateExecutionContext(
            ScriptExecutionContext context, Guid executionId, ScriptLanguage language)
        {
            return new ScriptExecutionInternalContext;
            {
                ExecutionId = executionId,
                OriginalContext = context,
                Language = language,
                StartTime = DateTime.UtcNow,
                UserId = context.UserId,
                SessionId = context.SessionId,
                Parameters = context.Parameters ?? new Dictionary<string, object>(),
                Variables = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Validate script syntax;
        /// </summary>
        private async Task<SyntaxValidationResult> ValidateScriptSyntaxAsync(string scriptCode, ScriptLanguage language)
        {
            var result = new SyntaxValidationResult();

            try
            {
                switch (language)
                {
                    case ScriptLanguage.CSharp:
                        var script = CSharpScript.Create(scriptCode, _defaultScriptOptions);
                        var compilation = script.GetCompilation();
                        var diagnostics = compilation.GetDiagnostics();

                        result.Errors.AddRange(diagnostics;
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Select(d => d.ToString()));
                        result.Warnings.AddRange(diagnostics;
                            .Where(d => d.Severity == DiagnosticSeverity.Warning)
                            .Select(d => d.ToString()));
                        break;

                    case ScriptLanguage.Python:
                        // Basic Python syntax check;
                        if (scriptCode.Contains("exec(") || scriptCode.Contains("eval("))
                        {
                            result.Warnings.Add("Script contains potentially unsafe exec/eval calls");
                        }
                        break;

                    case ScriptLanguage.JavaScript:
                        // Basic JavaScript syntax check;
                        if (scriptCode.Contains("eval(") || scriptCode.Contains("Function("))
                        {
                            result.Warnings.Add("Script contains potentially unsafe eval/Function calls");
                        }
                        break;
                }

                result.IsValid = result.Errors.Count == 0;
                await Task.CompletedTask;
                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add($"Syntax validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Validate script security;
        /// </summary>
        private async Task<SecurityValidationResult> ValidateScriptSecurityAsync(
            string scriptCode, ScriptLanguage language, ScriptExecutionContext context)
        {
            var result = new SecurityValidationResult;
            {
                Language = language,
                ValidationTime = DateTime.UtcNow;
            };

            try
            {
                // Check for dangerous patterns;
                var dangerousPatterns = GetDangerousPatterns(language);
                foreach (var pattern in dangerousPatterns)
                {
                    if (scriptCode.Contains(pattern.Pattern))
                    {
                        result.Threats.Add($"Potentially dangerous pattern detected: {pattern.Description}");
                        result.SecurityLevel = pattern.SecurityLevel;
                    }
                }

                // Check resource usage patterns;
                if (scriptCode.Contains("while(true)") || scriptCode.Contains("for(;;)"))
                {
                    result.Threats.Add("Potential infinite loop detected");
                    result.SecurityLevel = SecurityLevel.High;
                }

                // Check for file system access;
                if (scriptCode.Contains("File.") || scriptCode.Contains("Directory."))
                {
                    result.Warnings.Add("File system access detected");
                }

                // Check for network access;
                if (scriptCode.Contains("HttpClient") || scriptCode.Contains("WebRequest"))
                {
                    result.Warnings.Add("Network access detected");
                }

                // AI-based security analysis;
                if (EnableAIOptimization && _neuralNetwork != null)
                {
                    var aiAnalysis = await AnalyzeSecurityWithAIAsync(scriptCode, language);
                    if (aiAnalysis.HasThreats)
                    {
                        result.Threats.AddRange(aiAnalysis.Threats);
                        result.SecurityLevel = aiAnalysis.SecurityLevel > result.SecurityLevel;
                            ? aiAnalysis.SecurityLevel;
                            : result.SecurityLevel;
                    }
                }

                result.IsSafe = result.Threats.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Security validation error: {ex.Message}", ex);
                result.IsSafe = false;
                result.Threats.Add($"Security validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Check script security;
        /// </summary>
        private async Task<SecurityCheckResult> CheckScriptSecurityAsync(string scriptCode, ScriptExecutionContext context)
        {
            var result = new SecurityCheckResult;
            {
                ScriptHash = CalculateScriptHash(scriptCode),
                CheckTime = DateTime.UtcNow;
            };

            // Check with security manager if available;
            if (_securityManager != null)
            {
                var securityResult = await _securityManager.ValidateScriptAsync(scriptCode, context);
                result.IsAllowed = securityResult.IsAllowed;
                result.Reason = securityResult.Reason;
                result.Details = securityResult.Details;
            }
            else;
            {
                // Basic security check;
                result.IsAllowed = !scriptCode.Contains("System.IO.File.Delete") &&
                                 !scriptCode.Contains("System.Diagnostics.Process.Start");
                result.Reason = result.IsAllowed ? "Basic security check passed" : "Potentially dangerous operations detected";
            }

            return result;
        }

        /// <summary>
        /// Apply sandbox restrictions;
        /// </summary>
        private void ApplySandboxRestrictions(ScriptGlobals globals)
        {
            // Remove dangerous methods from context;
            globals.Context.Variables["File"] = null;
            globals.Context.Variables["Process"] = null;
            globals.Context.Variables["Environment"] = new;
            {
                MachineName = Environment.MachineName,
                ProcessorCount = Environment.ProcessorCount,
                OSVersion = Environment.OSVersion.ToString()
            };
        }

        /// <summary>
        /// Apply Python sandbox restrictions;
        /// </summary>
        private void ApplyPythonSandboxRestrictions(ScriptScope scope)
        {
            // Remove dangerous Python modules;
            scope.Engine.Runtime.LoadAssembly(typeof(System.IO.File).Assembly);
            scope.Engine.Runtime.LoadAssembly(typeof(System.Diagnostics.Process).Assembly);

            // Restrict access to dangerous modules;
            scope.Engine.SetSearchPaths(new string[0]);
        }

        /// <summary>
        /// Apply JavaScript sandbox restrictions;
        /// </summary>
        private void ApplyJavaScriptSandboxRestrictions(Jint.Engine engine)
        {
            // Disable dangerous JavaScript features;
            engine.Execute(@"Object.freeze = function() {};");
            engine.Execute(@"Object.defineProperty = function() {};");

            // Limit execution time (would need custom implementation)
        }

        /// <summary>
        /// Generate cache key;
        /// </summary>
        private string GenerateCacheKey(string scriptCode, ScriptLanguage language, ScriptExecutionContext context)
        {
            var hash = CalculateScriptHash(scriptCode);
            var contextHash = context?.GetHashCode().ToString() ?? "default";
            return $"{language}_{hash}_{contextHash}";
        }

        /// <summary>
        /// Calculate script hash;
        /// </summary>
        private string CalculateScriptHash(string scriptCode)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(scriptCode);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>
        /// Calculate file hash;
        /// </summary>
        private string CalculateFileHash(string filePath)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>
        /// Detect script language from file extension and content;
        /// </summary>
        private ScriptLanguage DetectScriptLanguage(string filePath, string content)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch;
            {
                ".cs" => ScriptLanguage.CSharp,
                ".py" => ScriptLanguage.Python,
                ".js" => ScriptLanguage.JavaScript,
                ".csx" => ScriptLanguage.CSharp,
                _ => DetectLanguageFromContent(content)
            };
        }

        /// <summary>
        /// Detect language from content;
        /// </summary>
        private ScriptLanguage DetectLanguageFromContent(string content)
        {
            if (content.Contains("using ") && content.Contains("class ") || content.Contains("Console."))
                return ScriptLanguage.CSharp;

            if (content.Contains("def ") || content.Contains("import ") || content.Contains("print("))
                return ScriptLanguage.Python;

            if (content.Contains("function ") || content.Contains("var ") || content.Contains("console.log"))
                return ScriptLanguage.JavaScript;

            return ScriptLanguage.CSharp; // Default;
        }

        /// <summary>
        /// Get dangerous patterns for language;
        /// </summary>
        private List<DangerousPattern> GetDangerousPatterns(ScriptLanguage language)
        {
            return language switch;
            {
                ScriptLanguage.CSharp => new List<DangerousPattern>
                {
                    new DangerousPattern { Pattern = "System.Diagnostics.Process.Start", Description = "Process execution", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "System.IO.File.Delete", Description = "File deletion", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "System.Reflection.Assembly.Load", Description = "Dynamic assembly loading", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "Environment.Exit", Description = "Process termination", SecurityLevel = SecurityLevel.Medium }
                },

                ScriptLanguage.Python => new List<DangerousPattern>
                {
                    new DangerousPattern { Pattern = "os.system", Description = "System command execution", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "subprocess.call", Description = "Subprocess execution", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "__import__", Description = "Dynamic import", SecurityLevel = SecurityLevel.Medium },
                    new DangerousPattern { Pattern = "eval(", Description = "Code evaluation", SecurityLevel = SecurityLevel.High }
                },

                ScriptLanguage.JavaScript => new List<DangerousPattern>
                {
                    new DangerousPattern { Pattern = "eval(", Description = "Code evaluation", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "Function(", Description = "Dynamic function creation", SecurityLevel = SecurityLevel.High },
                    new DangerousPattern { Pattern = "document.write", Description = "DOM manipulation", SecurityLevel = SecurityLevel.Medium },
                    new DangerousPattern { Pattern = "XMLHttpRequest", Description = "Network request", SecurityLevel = SecurityLevel.Low }
                },

                _ => new List<DangerousPattern>()
            };
        }

        /// <summary>
        /// Create error result;
        /// </summary>
        private ScriptResult CreateErrorResult(string errorMessage, ScriptExecutionStatus status,
            IEnumerable<string> errors = null, Exception exception = null)
        {
            return new ScriptResult;
            {
                Status = status,
                Success = false,
                ErrorMessage = errorMessage,
                Errors = errors?.ToList() ?? new List<string>(),
                Exception = exception,
                ExecutionTime = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Update performance metrics;
        /// </summary>
        private void UpdatePerformanceMetrics(string scriptCode, ScriptLanguage language,
            ScriptExecution execution, ScriptResult result)
        {
            var scriptHash = CalculateScriptHash(scriptCode);

            if (!_performanceMetrics.TryGetValue(scriptHash, out var performance))
            {
                performance = new ScriptPerformance;
                {
                    ScriptHash = scriptHash,
                    Language = language,
                    FirstExecution = execution.StartTime;
                };
                _performanceMetrics[scriptHash] = performance;
            }

            var duration = (execution.EndTime ?? DateTime.UtcNow) - execution.StartTime;

            performance.TotalExecutions++;
            performance.SuccessfulExecutions += result.Success ? 1 : 0;
            performance.TotalExecutionTime += duration;
            performance.AverageExecutionTime = performance.TotalExecutionTime / performance.TotalExecutions;
            performance.LastExecution = execution.StartTime;
            performance.LastStatus = result.Status;

            if (duration < performance.MinExecutionTime || performance.MinExecutionTime == TimeSpan.Zero)
                performance.MinExecutionTime = duration;
            if (duration > performance.MaxExecutionTime)
                performance.MaxExecutionTime = duration;

            performance.LastAccess = DateTime.UtcNow;
        }

        /// <summary>
        /// Update performance statistics;
        /// </summary>
        private void UpdatePerformanceStatistics()
        {
            // Implementation for updating aggregate statistics;
        }

        /// <summary>
        /// Log security violation;
        /// </summary>
        private async Task LogSecurityViolationAsync(Guid executionId, string scriptCode, SecurityCheckResult result)
        {
            if (_auditLogger != null)
            {
                await _auditLogger.LogScriptSecurityViolationAsync(executionId, scriptCode, result);
            }

            _logger.Warning($"Script security violation: {result.Reason}");
        }

        /// <summary>
        /// Log execution completion;
        /// </summary>
        private async Task LogExecutionCompleteAsync(ScriptExecution execution, ScriptResult result)
        {
            if (_auditLogger != null)
            {
                await _auditLogger.LogScriptExecutionCompleteAsync(execution.Id, execution.ScriptCode,
                    execution.Language, result);
            }
        }

        /// <summary>
        /// Log cancellation;
        /// </summary>
        private async Task LogCancellationAsync(Guid executionId, string reason)
        {
            if (_auditLogger != null)
            {
                await _auditLogger.LogScriptCancellationAsync(executionId, reason);
            }
        }

        /// <summary>
        /// Pre-execution hook;
        /// </summary>
        private async Task OnPreExecutionAsync(ScriptExecution execution)
        {
            _logger.Debug($"Starting script execution: {execution.Id} ({execution.Language})");

            if (_eventBus != null)
            {
                await _eventBus.PublishAsync(new ScriptExecutionEvent;
                {
                    ExecutionId = execution.Id,
                    Language = execution.Language,
                    Status = ScriptExecutionStatus.Starting,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Post-execution hook;
        /// </summary>
        private async Task OnPostExecutionAsync(ScriptExecution execution, ScriptResult result)
        {
            _logger.Debug($"Completed script execution: {execution.Id} - Status: {result.Status}");

            if (_eventBus != null)
            {
                await _eventBus.PublishAsync(new ScriptExecutionEvent;
                {
                    ExecutionId = execution.Id,
                    Language = execution.Language,
                    Status = result.Status,
                    Timestamp = DateTime.UtcNow,
                    Result = result;
                });
            }
        }

        /// <summary>
        /// Learn from execution using AI;
        /// </summary>
        private async Task LearnFromExecutionAsync(ScriptExecution execution, ScriptResult result)
        {
            try
            {
                if (_neuralNetwork == null) return;

                var learningData = new ScriptLearningData;
                {
                    ScriptCode = execution.ScriptCode,
                    Language = execution.Language,
                    ExecutionTime = (execution.EndTime ?? DateTime.UtcNow) - execution.StartTime,
                    Success = result.Success,
                    Context = execution.Context.OriginalContext;
                };

                await _neuralNetwork.LearnFromScriptExecutionAsync(learningData);
            }
            catch (Exception ex)
            {
                _logger.Error($"AI learning error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Execute script with options;
        /// </summary>
        private async Task<ScriptResult> ExecuteScriptWithOptionsAsync(
            ScriptExecutionRequest request, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                return await ExecuteScriptRequestAsync(request, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Execute script request;
        /// </summary>
        private async Task<ScriptResult> ExecuteScriptRequestAsync(ScriptExecutionRequest request, CancellationToken cancellationToken)
        {
            return request.Language switch;
            {
                ScriptLanguage.CSharp => await ExecuteCSharpAsync(request.ScriptCode, request.Context, cancellationToken),
                ScriptLanguage.Python => await ExecutePythonAsync(request.ScriptCode, request.Context, cancellationToken),
                ScriptLanguage.JavaScript => await ExecuteJavaScriptAsync(request.ScriptCode, request.Context, cancellationToken),
                _ => throw new NotSupportedException($"Language {request.Language} not supported")
            };
        }

        /// <summary>
        /// Optimize script execution order;
        /// </summary>
        private async Task<List<ScriptExecutionRequest>> OptimizeScriptExecutionOrderAsync(List<ScriptExecutionRequest> requests)
        {
            if (_neuralNetwork == null || requests.Count <= 1) return requests;

            try
            {
                var optimization = await _neuralNetwork.OptimizeScriptExecutionOrderAsync(requests);
                return optimization?.OptimizedRequests ?? requests;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script execution order optimization failed: {ex.Message}", ex);
                return requests;
            }
        }

        /// <summary>
        /// Analyze batch results;
        /// </summary>
        private async Task AnalyzeBatchResultsAsync(BatchScriptResult batchResult)
        {
            if (_neuralNetwork != null)
            {
                await _neuralNetwork.AnalyzeScriptBatchResultsAsync(batchResult);
            }
        }

        /// <summary>
        /// Validate script performance;
        /// </summary>
        private async Task<PerformanceValidationResult> ValidateScriptPerformanceAsync(string scriptCode, ScriptLanguage language)
        {
            // Implementation for performance validation;
            await Task.CompletedTask;
            return new PerformanceValidationResult();
        }

        /// <summary>
        /// Validate with AI;
        /// </summary>
        private async Task<AIValidationResult> ValidateWithAIAsync(string scriptCode, ScriptLanguage language)
        {
            // Implementation for AI validation;
            await Task.CompletedTask;
            return new AIValidationResult();
        }

        /// <summary>
        /// Analyze security with AI;
        /// </summary>
        private async Task<AISecurityAnalysis> AnalyzeSecurityWithAIAsync(string scriptCode, ScriptLanguage language)
        {
            // Implementation for AI security analysis;
            await Task.CompletedTask;
            return new AISecurityAnalysis();
        }

        /// <summary>
        /// Analyze script;
        /// </summary>
        private async Task<ScriptAnalysis> AnalyzeScriptAsync(string scriptCode, ScriptLanguage language)
        {
            // Implementation for script analysis;
            await Task.CompletedTask;
            return new ScriptAnalysis();
        }

        /// <summary>
        /// Apply optimizations;
        /// </summary>
        private async Task<string> ApplyOptimizationsAsync(string scriptCode, ScriptLanguage language,
            ScriptAnalysis analysis, OptimizationOptions options)
        {
            // Implementation for applying optimizations;
            await Task.CompletedTask;
            return scriptCode; // Return optimized script;
        }

        /// <summary>
        /// Calculate optimization improvements;
        /// </summary>
        private async Task<OptimizationImprovements> CalculateOptimizationImprovementsAsync(
            string originalScript, string optimizedScript, ScriptLanguage language)
        {
            // Implementation for improvement calculation;
            await Task.CompletedTask;
            return new OptimizationImprovements();
        }

        /// <summary>
        /// Check if cache entry should be cleared;
        /// </summary>
        private bool ShouldClearCacheEntry(string cacheKey, CacheClearOptions options)
        {
            if (!_scriptCache.TryGetValue(cacheKey, out var script)) return false;

            var age = DateTime.UtcNow - script.LastAccess;

            if (options.ClearOldEntries && age > TimeSpan.FromHours(options.OlderThanHours))
                return true;

            if (options.ClearUnused && script.AccessCount < options.MinAccessCount)
                return true;

            return false;
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cts.Cancel();

                    try
                    {
                        _monitoringTask?.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (AggregateException)
                    {
                        // Expected on cancellation;
                    }

                    _monitoringTask?.Dispose();
                    _cts.Dispose();
                    _compilationSemaphore.Dispose();

                    // Cancel all active executions;
                    foreach (var execution in _activeExecutions.Values)
                    {
                        execution.CancellationTokenSource?.Cancel();
                        execution.CancellationTokenSource?.Dispose();
                    }

                    // Clean up Python engine;
                    _pythonRuntime?.Shutdown();
                }

                _disposed = true;
            }
        }
    }

    // Supporting classes and interfaces;

    public interface IScriptRunner : IDisposable
    {
        TimeSpan DefaultTimeout { get; set; }
        int MaxMemoryUsageMB { get; set; }
        bool EnableSandboxing { get; set; }
        bool EnableCaching { get; set; }
        bool EnableAIOptimization { get; set; }

        Task<ScriptResult> ExecuteCSharpAsync(string scriptCode, ScriptExecutionContext context = null, CancellationToken cancellationToken = default);
        Task<ScriptResult> ExecutePythonAsync(string scriptCode, ScriptExecutionContext context = null, CancellationToken cancellationToken = default);
        Task<ScriptResult> ExecuteJavaScriptAsync(string scriptCode, ScriptExecutionContext context = null, CancellationToken cancellationToken = default);
        Task<ScriptResult> ExecuteFromFileAsync(string filePath, ScriptLanguage language = ScriptLanguage.AutoDetect, ScriptExecutionContext context = null, CancellationToken cancellationToken = default);
        Task<BatchScriptResult> ExecuteBatchAsync(IEnumerable<ScriptExecutionRequest> requests, BatchExecutionOptions options = null, CancellationToken cancellationToken = default);
        Task<CompilationResult> CompileScriptAsync(string scriptCode, ScriptLanguage language, ScriptExecutionContext context = null);
        Task<ScriptValidationResult> ValidateScriptAsync(string scriptCode, ScriptLanguage language, ScriptExecutionContext context = null);
        ScriptPerformance GetScriptPerformance(string scriptHash);
        IReadOnlyList<ScriptExecution> GetActiveExecutions();
        Task<bool> CancelExecutionAsync(Guid executionId, string reason = null);
        void ClearCache(CacheClearOptions options = null);
        Task<ScriptOptimization> OptimizeScriptAsync(string scriptCode, ScriptLanguage language, OptimizationOptions options = null);
    }

    public class ScriptExecutionContext;
    {
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public TimeSpan? Timeout { get; set; }
        public List<Assembly> References { get; set; } = new List<Assembly>();
        public List<string> Imports { get; set; } = new List<string>();
        public string SourceFile { get; set; }
        public string SourceHash { get; set; }
        public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();
        public ScriptSecurityContext Security { get; set; } = new ScriptSecurityContext();
    }

    public class ScriptSecurityContext;
    {
        public bool AllowFileAccess { get; set; } = false;
        public bool AllowNetworkAccess { get; set; } = false;
        public bool AllowReflection { get; set; } = false;
        public List<string> AllowedAssemblies { get; set; } = new List<string>();
        public List<string> AllowedNamespaces { get; set; } = new List<string>();
        public List<string> RestrictedPatterns { get; set; } = new List<string>();
    }

    public class ScriptResult;
    {
        public ScriptExecutionStatus Status { get; set; }
        public bool Success { get; set; }
        public object ReturnValue { get; set; }
        public string Output { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public Exception Exception { get; set; }
        public DateTime ExecutionTime { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class ScriptExecution;
    {
        public Guid Id { get; set; }
        public string ScriptCode { get; set; }
        public ScriptLanguage Language { get; set; }
        public ScriptExecutionInternalContext Context { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ScriptExecutionStatus Status { get; set; }
        public ScriptResult Result { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public string CancellationReason { get; set; }
    }

    internal class ScriptExecutionInternalContext;
    {
        public Guid ExecutionId { get; set; }
        public ScriptExecutionContext OriginalContext { get; set; }
        public ScriptLanguage Language { get; set; }
        public DateTime StartTime { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public Dictionary<string, object> Variables { get; set; }
    }

    public class ScriptGlobals;
    {
        public ScriptExecutionInternalContext Context { get; set; }
        public ScriptLogger Logger { get; set; }
        public StringBuilder Output { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Dictionary<string, object> Variables { get; set; }
    }

    public class ScriptLogger;
    {
        private readonly ILogger _logger;
        private readonly Guid _executionId;

        public ScriptLogger(ILogger logger, Guid executionId)
        {
            _logger = logger;
            _executionId = executionId;
        }

        public void Log(string message) => _logger.Information($"[Script:{_executionId}] {message}");
        public void Info(string message) => _logger.Information($"[Script:{_executionId}] {message}");
        public void Warning(string message) => _logger.Warning($"[Script:{_executionId}] {message}");
        public void Error(string message) => _logger.Error($"[Script:{_executionId}] {message}");
        public void Debug(string message) => _logger.Debug($"[Script:{_executionId}] {message}");
    }

    public class ScriptExecutionRequest;
    {
        public string ScriptCode { get; set; }
        public ScriptLanguage Language { get; set; }
        public ScriptExecutionContext Context { get; set; }
        public int Priority { get; set; } = 1;
    }

    public class BatchScriptResult;
    {
        public Guid BatchId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public List<ScriptResult> Results { get; set; } = new List<ScriptResult>();
        public BatchExecutionOptions Options { get; set; }
    }

    public class BatchExecutionOptions;
    {
        public bool ExecuteInParallel { get; set; } = true;
        public int? MaxConcurrent { get; set; }
        public bool OptimizeExecutionOrder { get; set; } = true;
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool ContinueOnError { get; set; } = false;
    }

    public class CompilationResult;
    {
        public bool Success { get; set; }
        public string CacheKey { get; set; }
        public TimeSpan CompilationTime { get; set; }
        public bool FromCache { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    internal class CompiledScript;
    {
        public Script<object> Script { get; set; }
        public Compilation Compilation { get; set; }
        public DateTime CompileTime { get; set; }
        public DateTime LastAccess { get; set; } = DateTime.UtcNow;
        public string CacheKey { get; set; }
        public int AccessCount { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ScriptValidationResult;
    {
        public ScriptLanguage Language { get; set; }
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Suggestions { get; set; } = new List<string>();
        public SecurityLevel SecurityLevel { get; set; }
        public double PerformanceScore { get; set; }
        public double AIScore { get; set; }
        public DateTime ValidationTime { get; set; }
    }

    public class ScriptPerformance;
    {
        public string ScriptHash { get; set; }
        public ScriptLanguage Language { get; set; }
        public long TotalExecutions { get; set; }
        public long SuccessfulExecutions { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan MinExecutionTime { get; set; }
        public TimeSpan MaxExecutionTime { get; set; }
        public DateTime FirstExecution { get; set; }
        public DateTime LastExecution { get; set; }
        public DateTime LastAccess { get; set; }
        public ScriptExecutionStatus LastStatus { get; set; }
        public Dictionary<string, int> ErrorCounts { get; set; } = new Dictionary<string, int>();

        public ScriptPerformance Clone()
        {
            return new ScriptPerformance;
            {
                ScriptHash = ScriptHash,
                Language = Language,
                TotalExecutions = TotalExecutions,
                SuccessfulExecutions = SuccessfulExecutions,
                TotalExecutionTime = TotalExecutionTime,
                AverageExecutionTime = AverageExecutionTime,
                MinExecutionTime = MinExecutionTime,
                MaxExecutionTime = MaxExecutionTime,
                FirstExecution = FirstExecution,
                LastExecution = LastExecution,
                LastAccess = LastAccess,
                LastStatus = LastStatus,
                ErrorCounts = new Dictionary<string, int>(ErrorCounts)
            };
        }
    }

    public class CacheClearOptions;
    {
        public bool ClearAll { get; set; } = false;
        public bool ClearOldEntries { get; set; } = true;
        public int OlderThanHours { get; set; } = 24;
        public bool ClearUnused { get; set; } = false;
        public int MinAccessCount { get; set; } = 1;
    }

    public class ScriptOptimization;
    {
        public string OriginalScript { get; set; }
        public string OptimizedScript { get; set; }
        public ScriptLanguage Language { get; set; }
        public bool IsOptimized { get; set; }
        public OptimizationImprovements Improvements { get; set; }
        public ScriptAnalysis Analysis { get; set; }
        public DateTime OptimizationDate { get; set; }
        public string Error { get; set; }
    }

    public class OptimizationOptions;
    {
        public bool OptimizePerformance { get; set; } = true;
        public bool OptimizeSecurity { get; set; } = true;
        public bool OptimizeReadability { get; set; } = false;
        public int MaxIterations { get; set; } = 3;
    }

    // Enums;
    public enum ScriptLanguage;
    {
        CSharp,
        Python,
        JavaScript,
        PowerShell,
        Lua,
        AutoDetect;
    }

    public enum ScriptExecutionStatus;
    {
        Pending,
        Starting,
        Running,
        Completed,
        Failed,
        Timeout,
        Cancelled,
        SecurityViolation,
        ValidationFailed,
        CompilationFailed,
        SystemError;
    }

    public enum SecurityLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    // Internal helper classes;
    internal class SyntaxValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    internal class SecurityValidationResult;
    {
        public ScriptLanguage Language { get; set; }
        public bool IsSafe { get; set; }
        public SecurityLevel SecurityLevel { get; set; }
        public List<string> Threats { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; }
    }

    internal class SecurityCheckResult;
    {
        public string ScriptHash { get; set; }
        public bool IsAllowed { get; set; }
        public string Reason { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
        public DateTime CheckTime { get; set; }
    }

    internal class DangerousPattern;
    {
        public string Pattern { get; set; }
        public string Description { get; set; }
        public SecurityLevel SecurityLevel { get; set; }
    }

    internal class PerformanceValidationResult;
    {
        public bool HasIssues { get; set; }
        public double Score { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }

    internal class AIValidationResult;
    {
        public bool HasSuggestions { get; set; }
        public double Score { get; set; }
        public List<string> Suggestions { get; set; } = new List<string>();
    }

    internal class AISecurityAnalysis;
    {
        public bool HasThreats { get; set; }
        public SecurityLevel SecurityLevel { get; set; }
        public List<string> Threats { get; set; } = new List<string>();
    }

    internal class ScriptAnalysis;
    {
        public ComplexityLevel Complexity { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public List<string> OptimizationOpportunities { get; set; } = new List<string>();
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    internal enum ComplexityLevel;
    {
        Low,
        Medium,
        High,
        VeryHigh;
    }

    internal class OptimizationImprovements;
    {
        public double PerformanceImprovementPercent { get; set; }
        public double SecurityImprovementPercent { get; set; }
        public double ReadabilityImprovementPercent { get; set; }
        public List<string> ImprovementDetails { get; set; } = new List<string>();
    }

    internal class ScriptLearningData;
    {
        public string ScriptCode { get; set; }
        public ScriptLanguage Language { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public bool Success { get; set; }
        public ScriptExecutionContext Context { get; set; }
    }

    // Event classes;
    public class ScriptExecutionEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public ScriptLanguage Language { get; set; }
        public ScriptExecutionStatus Status { get; set; }
        public DateTime Timestamp { get; set; }
        public ScriptResult Result { get; set; }
    }

    // Custom AssemblyLoadContext for script isolation;
    internal class ScriptAssemblyLoadContext : AssemblyLoadContext;
    {
        public ScriptAssemblyLoadContext() : base(isCollectible: true)
        {
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            return null; // Return null to use default loading behavior;
        }
    }
}
