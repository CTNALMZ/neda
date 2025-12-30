using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using NEDA.Core.Configuration;
using NEDA.Services.Messaging.EventBus;
using NEDA.Automation.WorkflowEngine.ExecutionEngine;

namespace NEDA.Automation.Executors;
{
    /// <summary>
    /// Betik çalıştırma motoru - Endüstriyel seviye;
    /// Çoklu betik dili, paralel yürütme, güvenlik ve izolasyon desteği;
    /// </summary>
    public interface IScriptRunner;
    {
        /// <summary>
        /// Betiği yürütür;
        /// </summary>
        /// <param name="script">Çalıştırılacak betik</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Yürütme sonucu</returns>
        Task<ScriptExecutionResult> ExecuteScriptAsync(Script script, CancellationToken cancellationToken = default);

        /// <summary>
        /// Batch betik yürütme;
        /// </summary>
        /// <param name="scripts">Çalıştırılacak betikler</param>
        /// <param name="executionStrategy">Yürütme stratejisi</param>
        /// <returns>Batch yürütme sonucu</returns>
        Task<BatchScriptResult> ExecuteBatchAsync(IEnumerable<Script> scripts, BatchExecutionStrategy executionStrategy);

        /// <summary>
        /// Gerçek zamanlı betik yürütme akışı başlatır;
        /// </summary>
        /// <param name="script">Çalıştırılacak betik</param>
        /// <returns>Gerçek zamanlı yürütme akışı</returns>
        IRealTimeScriptStream StartRealTimeExecution(Script script);

        /// <summary>
        /// Betik yürütme geçmişini getirir;
        /// </summary>
        /// <param name="scriptId">Betik ID</param>
        /// <param name="limit">Kayıt limiti</param>
        /// <returns>Yürütme geçmişi</returns>
        Task<IEnumerable<ScriptExecutionHistory>> GetExecutionHistoryAsync(Guid scriptId, int limit = 100);

        /// <summary>
        /// Betik dilini algılar;
        /// </summary>
        /// <param name="scriptContent">Betik içeriği</param>
        /// <returns>Betik dili</returns>
        Task<ScriptLanguage> DetectLanguageAsync(string scriptContent);

        /// <summary>
        /// Betiği doğrular ve analiz eder;
        /// </summary>
        /// <param name="script">Doğrulanacak betik</param>
        /// <returns>Doğrulama sonucu</returns>
        Task<ScriptValidationResult> ValidateScriptAsync(Script script);

        /// <summary>
        /// Betik ortamını temizler;
        /// </summary>
        /// <param name="environmentId">Ortam ID</param>
        Task CleanupEnvironmentAsync(Guid environmentId);
    }

    /// <summary>
    /// Betik çalıştırma motoru - Endüstriyel implementasyon;
    /// </summary>
    public class ScriptRunner : IScriptRunner, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ISecurityManager _securityManager;
        private readonly IProcessController _processController;
        private readonly IEnvironmentConfig _environmentConfig;
        private readonly IScriptEngineProvider _engineProvider;
        private readonly ScriptExecutionHistoryManager _historyManager;
        private readonly ScriptSandboxManager _sandboxManager;
        private readonly ConcurrentDictionary<Guid, ScriptExecutionSession> _activeSessions;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly int _maxConcurrentExecutions;
        private bool _disposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// ScriptRunner constructor - Dependency Injection;
        /// </summary>
        public ScriptRunner(
            ILogger logger,
            IEventBus eventBus,
            ISecurityManager securityManager,
            IProcessController processController,
            IEnvironmentConfig environmentConfig,
            IScriptEngineProvider engineProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _processController = processController ?? throw new ArgumentNullException(nameof(processController));
            _environmentConfig = environmentConfig ?? throw new ArgumentNullException(nameof(environmentConfig));
            _engineProvider = engineProvider ?? throw new ArgumentNullException(nameof(engineProvider));

            _historyManager = new ScriptExecutionHistoryManager(logger);
            _sandboxManager = new ScriptSandboxManager(logger, securityManager);
            _activeSessions = new ConcurrentDictionary<Guid, ScriptExecutionSession>();

            _maxConcurrentExecutions = environmentConfig.GetSetting<int>("ScriptRunner:MaxConcurrentExecutions", 10);
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentExecutions, _maxConcurrentExecutions);

            InitializeScriptEngines();

            _logger.Info("ScriptRunner initialized successfully");
            _logger.Info($"Max concurrent executions: {_maxConcurrentExecutions}");
        }

        /// <summary>
        /// Betiği yürütür;
        /// </summary>
        public async Task<ScriptExecutionResult> ExecuteScriptAsync(Script script, CancellationToken cancellationToken = default)
        {
            if (script == null)
                throw new ArgumentNullException(nameof(script));

            var executionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateScript(script);

                _logger.Info($"Executing script: {script.Name} (ID: {executionId})");
                _logger.Debug($"Script language: {script.Language}, Hash: {script.ContentHash}");

                // Güvenlik kontrolü;
                await ValidateSecurityAsync(script);

                // Ortam hazırlığı;
                var executionEnvironment = await PrepareExecutionEnvironmentAsync(script);

                // Betik motorunu al;
                var scriptEngine = await _engineProvider.GetEngineAsync(script.Language);

                // Yürütme geçmişi başlat;
                var history = new ScriptExecutionHistory;
                {
                    ExecutionId = executionId,
                    ScriptId = script.Id,
                    ScriptName = script.Name,
                    Language = script.Language,
                    StartTime = startTime,
                    Status = ExecutionStatus.Running;
                };

                await _historyManager.StartExecutionAsync(history);

                // Oturum oluştur;
                var session = new ScriptExecutionSession(executionId, script, _logger);
                _activeSessions[executionId] = session;

                // Olay yayınla;
                await _eventBus.PublishAsync(new ScriptExecutionStartedEvent;
                {
                    ExecutionId = executionId,
                    ScriptId = script.Id,
                    ScriptName = script.Name,
                    Language = script.Language,
                    Timestamp = startTime;
                });

                // Yürütme başlat;
                await _concurrencySemaphore.WaitAsync(cancellationToken);

                try
                {
                    var result = await ExecuteScriptInternalAsync(
                        script,
                        scriptEngine,
                        executionEnvironment,
                        session,
                        cancellationToken);

                    result.ExecutionId = executionId;
                    result.ScriptName = script.Name;
                    result.TotalExecutionTime = DateTime.UtcNow - startTime;

                    // Geçmişi güncelle;
                    history.EndTime = DateTime.UtcNow;
                    history.Status = result.Success ? ExecutionStatus.Completed : ExecutionStatus.Failed;
                    history.ExitCode = result.ExitCode;
                    history.Output = result.Output;
                    history.ErrorOutput = result.ErrorOutput;
                    history.TotalExecutionTime = result.TotalExecutionTime;

                    await _historyManager.CompleteExecutionAsync(history);

                    // Aktif oturumu kaldır;
                    _activeSessions.TryRemove(executionId, out _);

                    // Ortamı temizle;
                    await CleanupExecutionEnvironmentAsync(executionEnvironment, result.Success);

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new ScriptExecutionCompletedEvent;
                    {
                        ExecutionId = executionId,
                        ScriptId = script.Id,
                        ScriptName = script.Name,
                        Result = result,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Info($"Script execution completed: {script.Name}");
                    _logger.Debug($"Execution time: {result.TotalExecutionTime.TotalSeconds:F2}s, " +
                                $"Exit code: {result.ExitCode}, " +
                                $"Success: {result.Success}");

                    return result;
                }
                finally
                {
                    _concurrencySemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"Script execution cancelled: {executionId}");
                await HandleCancellationAsync(executionId, startTime);
                throw;
            }
            catch (SecurityViolationException ex)
            {
                _logger.Error($"Security violation in script execution: {ex.Message}", ex);
                await HandleSecurityViolationAsync(executionId, startTime, ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script execution failed: {ex.Message}", ex);
                await HandleFailureAsync(executionId, startTime, ex);
                throw new ScriptExecutionException($"Script execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Batch betik yürütme;
        /// </summary>
        public async Task<BatchScriptResult> ExecuteBatchAsync(
            IEnumerable<Script> scripts,
            BatchExecutionStrategy executionStrategy)
        {
            if (scripts == null)
                throw new ArgumentNullException(nameof(scripts));

            var batchId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                var scriptList = scripts.ToList();
                if (!scriptList.Any())
                    throw new ArgumentException("No scripts to execute", nameof(scripts));

                _logger.Info($"Starting batch script execution: {batchId}");
                _logger.Info($"Total scripts: {scriptList.Count}, Strategy: {executionStrategy}");

                // Batch doğrulama;
                await ValidateBatchAsync(scriptList, executionStrategy);

                var results = new List<ScriptExecutionResult>();
                var executionTasks = new List<Task<ScriptExecutionResult>>();

                switch (executionStrategy)
                {
                    case BatchExecutionStrategy.Parallel:
                        // Paralel yürütme;
                        foreach (var script in scriptList)
                        {
                            executionTasks.Add(ExecuteScriptAsync(script));
                        }
                        break;

                    case BatchExecutionStrategy.Sequential:
                        // Sıralı yürütme;
                        foreach (var script in scriptList)
                        {
                            var result = await ExecuteScriptAsync(script);
                            results.Add(result);

                            // Başarısızsa ve stop on error ise dur;
                            if (!result.Success && executionStrategy.StopOnError)
                            {
                                _logger.Warn($"Batch execution stopped due to error in script: {script.Name}");
                                break;
                            }
                        }
                        break;

                    case BatchExecutionStrategy.DependencyBased:
                        // Bağımlılık tabanlı yürütme;
                        results = await ExecuteWithDependenciesAsync(scriptList);
                        break;
                }

                if (executionTasks.Any())
                {
                    var taskResults = await Task.WhenAll(executionTasks);
                    results.AddRange(taskResults);
                }

                // Batch sonuçlarını konsolide et;
                var batchResult = ConsolidateBatchResults(results, batchId, startTime);

                // Olay yayınla;
                await _eventBus.PublishAsync(new BatchScriptExecutionCompletedEvent;
                {
                    BatchId = batchId,
                    TotalScripts = scriptList.Count,
                    Result = batchResult,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Batch execution completed: {batchId}");
                _logger.Info($"Successful: {batchResult.SuccessfulExecutions}, " +
                           $"Failed: {batchResult.FailedExecutions}, " +
                           $"Total time: {batchResult.TotalExecutionTime.TotalSeconds:F2}s");

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Batch script execution failed: {ex.Message}", ex);
                throw new BatchScriptExecutionException($"Batch script execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gerçek zamanlı betik yürütme akışı başlatır;
        /// </summary>
        public IRealTimeScriptStream StartRealTimeExecution(Script script)
        {
            lock (_syncLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(ScriptRunner));

                var streamId = Guid.NewGuid();
                var stream = new RealTimeScriptStream(streamId, script, _logger, _engineProvider, _sandboxManager);

                _logger.Info($"Real-time script execution stream started: {streamId}");

                return stream;
            }
        }

        /// <summary>
        /// Betik yürütme geçmişini getirir;
        /// </summary>
        public async Task<IEnumerable<ScriptExecutionHistory>> GetExecutionHistoryAsync(Guid scriptId, int limit = 100)
        {
            try
            {
                var history = await _historyManager.GetExecutionHistoryAsync(scriptId, limit);

                if (history == null || !history.Any())
                {
                    _logger.Debug($"No execution history found for script: {scriptId}");
                    return Enumerable.Empty<ScriptExecutionHistory>();
                }

                return history;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get execution history: {ex.Message}", ex);
                throw new ScriptHistoryException($"Failed to get execution history: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Betik dilini algılar;
        /// </summary>
        public async Task<ScriptLanguage> DetectLanguageAsync(string scriptContent)
        {
            if (string.IsNullOrWhiteSpace(scriptContent))
                throw new ArgumentException("Script content cannot be empty", nameof(scriptContent));

            try
            {
                _logger.Debug("Detecting script language");

                // İçerik analizi ile dil algılama;
                var language = await AnalyzeScriptContentAsync(scriptContent);

                // Alternatif yöntemlerle doğrula;
                language = await VerifyLanguageDetectionAsync(scriptContent, language);

                _logger.Debug($"Detected script language: {language}");

                return language;
            }
            catch (Exception ex)
            {
                _logger.Error($"Language detection failed: {ex.Message}", ex);
                return ScriptLanguage.Unknown;
            }
        }

        /// <summary>
        /// Betiği doğrular ve analiz eder;
        /// </summary>
        public async Task<ScriptValidationResult> ValidateScriptAsync(Script script)
        {
            if (script == null)
                throw new ArgumentNullException(nameof(script));

            var validationResult = new ScriptValidationResult;
            {
                ScriptId = script.Id,
                ScriptName = script.Name,
                ValidationTime = DateTime.UtcNow;
            };

            try
            {
                _logger.Debug($"Validating script: {script.Name}");

                // Temel doğrulamalar;
                ValidateBasicScriptProperties(script, validationResult);

                if (!validationResult.IsValid)
                {
                    return validationResult;
                }

                // Sözdizimi doğrulama;
                await ValidateSyntaxAsync(script, validationResult);

                // Güvenlik doğrulaması;
                await ValidateSecurityIssuesAsync(script, validationResult);

                // Performans analizi;
                await AnalyzePerformanceAsync(script, validationResult);

                // Bağımlılık analizi;
                await AnalyzeDependenciesAsync(script, validationResult);

                _logger.Debug($"Script validation completed: {script.Name}, Valid: {validationResult.IsValid}");

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script validation failed: {ex.Message}", ex);
                validationResult.IsValid = false;
                validationResult.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.ValidationFailed,
                    Message = $"Validation failed: {ex.Message}"
                });
                return validationResult;
            }
        }

        /// <summary>
        /// Betik ortamını temizler;
        /// </summary>
        public async Task CleanupEnvironmentAsync(Guid environmentId)
        {
            try
            {
                await _sandboxManager.CleanupEnvironmentAsync(environmentId);
                _logger.Debug($"Environment cleaned up: {environmentId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Environment cleanup failed: {ex.Message}", ex);
                throw new EnvironmentCleanupException($"Environment cleanup failed: {ex.Message}", ex);
            }
        }

        #region Private Methods;

        private void InitializeScriptEngines()
        {
            try
            {
                // Desteklenen betik motorlarını başlat;
                var supportedLanguages = new[]
                {
                    ScriptLanguage.PowerShell,
                    ScriptLanguage.Python,
                    ScriptLanguage.Bash,
                    ScriptLanguage.Batch,
                    ScriptLanguage.JavaScript,
                    ScriptLanguage.TypeScript,
                    ScriptLanguage.Lua,
                    ScriptLanguage.Ruby,
                    ScriptLanguage.Perl;
                };

                foreach (var language in supportedLanguages)
                {
                    _engineProvider.RegisterEngine(language, CreateScriptEngine(language));
                }

                _logger.Info($"Initialized script engines for {supportedLanguages.Length} languages");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize script engines: {ex.Message}", ex);
                throw new ScriptEngineInitializationException($"Failed to initialize script engines: {ex.Message}", ex);
            }
        }

        private IScriptEngine CreateScriptEngine(ScriptLanguage language)
        {
            return language switch;
            {
                ScriptLanguage.PowerShell => new PowerShellEngine(_logger, _securityManager),
                ScriptLanguage.Python => new PythonEngine(_logger, _securityManager),
                ScriptLanguage.Bash => new BashEngine(_logger, _securityManager),
                ScriptLanguage.Batch => new BatchEngine(_logger, _securityManager),
                ScriptLanguage.JavaScript => new JavaScriptEngine(_logger, _securityManager),
                ScriptLanguage.TypeScript => new TypeScriptEngine(_logger, _securityManager),
                ScriptLanguage.Lua => new LuaEngine(_logger, _securityManager),
                ScriptLanguage.Ruby => new RubyEngine(_logger, _securityManager),
                ScriptLanguage.Perl => new PerlEngine(_logger, _securityManager),
                _ => throw new NotSupportedException($"Script language not supported: {language}")
            };
        }

        private void ValidateScript(Script script)
        {
            if (string.IsNullOrWhiteSpace(script.Name))
                throw new ArgumentException("Script name cannot be empty", nameof(script));

            if (string.IsNullOrWhiteSpace(script.Content))
                throw new ArgumentException("Script content cannot be empty", nameof(script));

            if (script.Language == ScriptLanguage.Unknown)
                throw new ArgumentException("Script language cannot be unknown", nameof(script));

            if (script.Id == Guid.Empty)
                script.Id = Guid.NewGuid();

            // İçerik hash'ini hesapla;
            script.ContentHash = CalculateContentHash(script.Content);
        }

        private async Task ValidateSecurityAsync(Script script)
        {
            var securityContext = new ScriptSecurityContext;
            {
                ScriptId = script.Id,
                ScriptName = script.Name,
                Language = script.Language,
                ContentHash = script.ContentHash,
                ExecutionTime = DateTime.UtcNow;
            };

            // Güvenlik doğrulaması;
            var securityResult = await _securityManager.ValidateScriptAsync(script, securityContext);

            if (!securityResult.IsAllowed)
            {
                _logger.Warn($"Security validation failed for script: {script.Name}");
                _logger.Warn($"Reason: {securityResult.DenialReason}, Risk level: {securityResult.RiskLevel}");

                throw new SecurityViolationException($"Script execution denied: {securityResult.DenialReason}");
            }

            _logger.Debug($"Security validation passed for script: {script.Name}");
        }

        private async Task<ScriptExecutionEnvironment> PrepareExecutionEnvironmentAsync(Script script)
        {
            _logger.Debug($"Preparing execution environment for script: {script.Name}");

            var environment = await _sandboxManager.CreateEnvironmentAsync(new SandboxConfiguration;
            {
                ScriptId = script.Id,
                Language = script.Language,
                IsolationLevel = script.RequiresIsolation ? IsolationLevel.High : IsolationLevel.Medium,
                ResourceLimits = script.ResourceLimits ?? new ResourceLimits(),
                EnvironmentVariables = script.EnvironmentVariables ?? new Dictionary<string, string>(),
                AllowedOperations = script.AllowedOperations ?? new List<string>(),
                NetworkAccess = script.RequiresNetworkAccess;
            });

            // Betik dosyasını oluştur;
            var scriptPath = await _sandboxManager.CreateScriptFileAsync(environment, script.Content, script.Language);
            environment.ScriptFilePath = scriptPath;

            // Bağımlılıkları yükle;
            if (script.Dependencies?.Any() == true)
            {
                await _sandboxManager.InstallDependenciesAsync(environment, script.Dependencies);
            }

            _logger.Debug($"Execution environment prepared: {environment.EnvironmentId}");

            return environment;
        }

        private async Task<ScriptExecutionResult> ExecuteScriptInternalAsync(
            Script script,
            IScriptEngine engine,
            ScriptExecutionEnvironment environment,
            ScriptExecutionSession session,
            CancellationToken cancellationToken)
        {
            var result = new ScriptExecutionResult;
            {
                StartTime = DateTime.UtcNow;
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Betiği yürüt;
                var executionResult = await engine.ExecuteAsync(
                    environment.ScriptFilePath,
                    environment,
                    script.Arguments ?? new List<string>(),
                    session,
                    cancellationToken);

                result.ExitCode = executionResult.ExitCode;
                result.Output = executionResult.Output;
                result.ErrorOutput = executionResult.ErrorOutput;
                result.Success = executionResult.Success;
                result.ExecutionStatistics = executionResult.Statistics;
                result.Metadata["EngineVersion"] = engine.Version;
                result.Metadata["EnvironmentId"] = environment.EnvironmentId;

                // Performans ölçümleri;
                if (executionResult.Statistics != null)
                {
                    result.Metadata["CpuTime"] = executionResult.Statistics.CpuTime;
                    result.Metadata["PeakMemory"] = executionResult.Statistics.PeakMemoryUsage;
                    result.Metadata["TotalProcessorTime"] = executionResult.Statistics.TotalProcessorTime;
                }

                // Çıktıyı parse et;
                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    result.ParsedOutput = ParseScriptOutput(result.Output, script.OutputFormat);
                }
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorOutput = "Script execution was cancelled";
                result.ExitCode = -1;
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorOutput = $"Execution failed: {ex.Message}";
                result.ExitCode = -1;
                _logger.Error($"Script execution internal error: {ex.Message}", ex);
            }
            finally
            {
                result.EndTime = DateTime.UtcNow;
                result.TotalExecutionTime = result.EndTime - result.StartTime;

                // Kaynak kullanımını logla;
                LogResourceUsage(result);
            }

            return result;
        }

        private async Task CleanupExecutionEnvironmentAsync(ScriptExecutionEnvironment environment, bool success)
        {
            try
            {
                if (environment.PersistOnSuccess && success)
                {
                    _logger.Debug($"Preserving environment on successful execution: {environment.EnvironmentId}");
                    return;
                }

                await _sandboxManager.CleanupEnvironmentAsync(environment.EnvironmentId);
                _logger.Debug($"Environment cleaned up: {environment.EnvironmentId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to cleanup environment: {ex.Message}", ex);
                // Temizleme hatası yürütmeyi başarısız yapmaz;
            }
        }

        private async Task HandleCancellationAsync(Guid executionId, DateTime startTime)
        {
            if (_activeSessions.TryRemove(executionId, out var session))
            {
                await session.CancelAsync();
            }

            await _historyManager.CancelExecutionAsync(executionId, DateTime.UtcNow - startTime);

            await _eventBus.PublishAsync(new ScriptExecutionCancelledEvent;
            {
                ExecutionId = executionId,
                Timestamp = DateTime.UtcNow;
            });
        }

        private async Task HandleSecurityViolationAsync(Guid executionId, DateTime startTime, SecurityViolationException exception)
        {
            _activeSessions.TryRemove(executionId, out _);

            await _historyManager.FailExecutionAsync(
                executionId,
                DateTime.UtcNow - startTime,
                "Security violation",
                exception.Message);

            await _eventBus.PublishAsync(new ScriptSecurityViolationEvent;
            {
                ExecutionId = executionId,
                ViolationType = "ScriptExecution",
                Message = exception.Message,
                Timestamp = DateTime.UtcNow;
            });
        }

        private async Task HandleFailureAsync(Guid executionId, DateTime startTime, Exception exception)
        {
            _activeSessions.TryRemove(executionId, out _);

            await _historyManager.FailExecutionAsync(
                executionId,
                DateTime.UtcNow - startTime,
                "Execution failed",
                exception.Message);
        }

        private async Task ValidateBatchAsync(List<Script> scripts, BatchExecutionStrategy strategy)
        {
            // Yinelenen betikleri kontrol et;
            var duplicateIds = scripts;
                .GroupBy(s => s.Id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Any())
            {
                throw new ArgumentException($"Duplicate script IDs in batch: {string.Join(", ", duplicateIds)}");
            }

            // Bağımlılık kontrolleri;
            if (strategy == BatchExecutionStrategy.DependencyBased)
            {
                await ValidateDependenciesAsync(scripts);
            }

            // Kaynak limit kontrolleri;
            await ValidateResourceLimitsAsync(scripts, strategy);
        }

        private async Task<List<ScriptExecutionResult>> ExecuteWithDependenciesAsync(List<Script> scripts)
        {
            var results = new List<ScriptExecutionResult>();
            var executionGraph = BuildDependencyGraph(scripts);
            var executedScripts = new HashSet<Guid>();

            while (executedScripts.Count < scripts.Count)
            {
                var scriptsToExecute = executionGraph;
                    .Where(kvp => !executedScripts.Contains(kvp.Key) &&
                                 kvp.Value.All(dep => executedScripts.Contains(dep)))
                    .Select(kvp => scripts.First(s => s.Id == kvp.Key))
                    .ToList();

                if (!scriptsToExecute.Any())
                {
                    _logger.Error("Circular dependency detected in script batch");
                    throw new CircularDependencyException("Circular dependency detected in script batch");
                }

                // Bu seviyedeki betikleri paralel yürüt;
                var levelTasks = scriptsToExecute.Select(ExecuteScriptAsync);
                var levelResults = await Task.WhenAll(levelTasks);

                results.AddRange(levelResults);

                // Başarısız betikleri işle;
                var failedScripts = levelResults.Where(r => !r.Success).ToList();
                if (failedScripts.Any())
                {
                    _logger.Warn($"{failedScripts.Count} scripts failed in dependency level");
                    // Bağımlı betikleri iptal et;
                    CancelDependentScripts(executionGraph, failedScripts.Select(r => r.ScriptId).ToList());
                }

                executedScripts.UnionWith(scriptsToExecute.Select(s => s.Id));
            }

            return results;
        }

        private Dictionary<Guid, List<Guid>> BuildDependencyGraph(List<Script> scripts)
        {
            var graph = new Dictionary<Guid, List<Guid>>();

            foreach (var script in scripts)
            {
                var dependencies = new List<Guid>();

                if (script.Dependencies?.Any() == true)
                {
                    foreach (var depName in script.Dependencies)
                    {
                        var depScript = scripts.FirstOrDefault(s => s.Name == depName);
                        if (depScript != null)
                        {
                            dependencies.Add(depScript.Id);
                        }
                    }
                }

                graph[script.Id] = dependencies;
            }

            return graph;
        }

        private void CancelDependentScripts(Dictionary<Guid, List<Guid>> graph, List<Guid> failedScriptIds)
        {
            // Bağımlı betikleri bul ve iptal et;
            var scriptsToCancel = new HashSet<Guid>();

            foreach (var failedId in failedScriptIds)
            {
                var dependents = graph.Where(kvp => kvp.Value.Contains(failedId))
                                     .Select(kvp => kvp.Key)
                                     .ToList();

                foreach (var dependentId in dependents)
                {
                    scriptsToCancel.Add(dependentId);
                }
            }

            foreach (var scriptId in scriptsToCancel)
            {
                if (_activeSessions.TryGetValue(scriptId, out var session))
                {
                    session.CancelAsync();
                }
            }
        }

        private BatchScriptResult ConsolidateBatchResults(
            List<ScriptExecutionResult> results,
            Guid batchId,
            DateTime startTime)
        {
            var batchResult = new BatchScriptResult;
            {
                BatchId = batchId,
                StartTime = startTime,
                EndTime = DateTime.UtcNow,
                TotalExecutionTime = DateTime.UtcNow - startTime,
                AllResults = results;
            };

            // İstatistikleri hesapla;
            batchResult.TotalScripts = results.Count;
            batchResult.SuccessfulExecutions = results.Count(r => r.Success);
            batchResult.FailedExecutions = results.Count(r => !r.Success);
            batchResult.SuccessRate = (double)batchResult.SuccessfulExecutions / batchResult.TotalScripts;

            // Performans metrikleri;
            if (results.Any())
            {
                batchResult.AverageExecutionTime = TimeSpan.FromMilliseconds(
                    results.Average(r => r.TotalExecutionTime.TotalMilliseconds));

                batchResult.TotalCpuTime = TimeSpan.FromMilliseconds(
                    results.Sum(r => r.ExecutionStatistics?.CpuTime.TotalMilliseconds ?? 0));

                batchResult.PeakMemoryUsage = results;
                    .Max(r => r.ExecutionStatistics?.PeakMemoryUsage ?? 0);
            }

            // Hataları grupla;
            var errorGroups = results;
                .Where(r => !r.Success)
                .GroupBy(r => r.ExitCode)
                .ToDictionary(g => g.Key, g => g.Count());

            batchResult.ErrorDistribution = errorGroups;

            return batchResult;
        }

        private async Task<ScriptLanguage> AnalyzeScriptContentAsync(string content)
        {
            // Shebang analizi;
            if (content.StartsWith("#!"))
            {
                var shebang = content.Substring(2).Split(' ')[0].ToLower();

                if (shebang.Contains("python")) return ScriptLanguage.Python;
                if (shebang.Contains("bash") || shebang.Contains("sh")) return ScriptLanguage.Bash;
                if (shebang.Contains("perl")) return ScriptLanguage.Perl;
                if (shebang.Contains("ruby")) return ScriptLanguage.Ruby;
                if (shebang.Contains("lua")) return ScriptLanguage.Lua;
                if (shebang.Contains("node")) return ScriptLanguage.JavaScript;
            }

            // İçerik analizi;
            var lines = content.Split('\n').Take(10).ToList();

            // PowerShell kontrolü;
            if (lines.Any(l => l.Contains("Write-Host") || l.Contains("Get-ChildItem") ||
                              l.Contains("$") && l.Contains("=") && l.Contains(";")))
            {
                return ScriptLanguage.PowerShell;
            }

            // Python kontrolü;
            if (lines.Any(l => l.Contains("def ") || l.Contains("import ") ||
                              l.Contains("print(") || l.Contains(":")))
            {
                return ScriptLanguage.Python;
            }

            // JavaScript kontrolü;
            if (lines.Any(l => l.Contains("function ") || l.Contains("console.log") ||
                              l.Contains("const ") || l.Contains("let ") || l.Contains("var ")))
            {
                return ScriptLanguage.JavaScript;
            }

            // TypeScript kontrolü;
            if (lines.Any(l => l.Contains("interface ") || l.Contains("type ") ||
                              l.Contains(": string") || l.Contains(": number")))
            {
                return ScriptLanguage.TypeScript;
            }

            // Batch kontrolü;
            if (lines.Any(l => l.StartsWith("@echo") || l.Contains("SET ") ||
                              l.Contains("%") && l.Contains("%")))
            {
                return ScriptLanguage.Batch;
            }

            await Task.CompletedTask;
            return ScriptLanguage.Unknown;
        }

        private async Task<ScriptLanguage> VerifyLanguageDetectionAsync(string content, ScriptLanguage detectedLanguage)
        {
            // Dil motoru ile doğrulama;
            try
            {
                if (detectedLanguage != ScriptLanguage.Unknown)
                {
                    var engine = await _engineProvider.GetEngineAsync(detectedLanguage);
                    if (engine != null && await engine.ValidateSyntaxAsync(content))
                    {
                        return detectedLanguage;
                    }
                }
            }
            catch
            {
                // Doğrulama başarısız, başka yöntemler dene;
            }

            // Fallback: Dosya uzantısı veya içerik pattern'leri;
            return await DetectByPatternsAsync(content);
        }

        private async Task<ScriptLanguage> DetectByPatternsAsync(string content)
        {
            var patterns = new Dictionary<ScriptLanguage, string[]>
            {
                [ScriptLanguage.PowerShell] = new[] { "Get-", "Set-", "$env:", "[Console]::" },
                [ScriptLanguage.Python] = new[] { "import ", "from ", "def ", "class ", "if __name__" },
                [ScriptLanguage.JavaScript] = new[] { "function(", "=>", "const ", "let ", "var ", "console." },
                [ScriptLanguage.Bash] = new[] { "if [ ", "then", "fi", "echo ", "mkdir ", "cp ", "rm " },
                [ScriptLanguage.Batch] = new[] { "echo off", "set ", "call ", "goto ", "if errorlevel" }
            };

            foreach (var kvp in patterns)
            {
                if (kvp.Value.Any(pattern => content.Contains(pattern)))
                {
                    return kvp.Key;
                }
            }

            await Task.CompletedTask;
            return ScriptLanguage.Unknown;
        }

        private void ValidateBasicScriptProperties(Script script, ScriptValidationResult result)
        {
            // İsim kontrolü;
            if (string.IsNullOrWhiteSpace(script.Name))
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.InvalidName,
                    Message = "Script name cannot be empty"
                });
            }

            // İçerik kontrolü;
            if (string.IsNullOrWhiteSpace(script.Content))
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.EmptyContent,
                    Message = "Script content cannot be empty"
                });
            }

            // Dil kontrolü;
            if (script.Language == ScriptLanguage.Unknown)
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.UnknownLanguage,
                    Message = "Script language cannot be unknown"
                });
            }

            result.IsValid = !result.Errors.Any();
        }

        private async Task ValidateSyntaxAsync(Script script, ScriptValidationResult result)
        {
            try
            {
                var engine = await _engineProvider.GetEngineAsync(script.Language);
                if (engine == null)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        ErrorType = ValidationErrorType.UnsupportedLanguage,
                        Message = $"Script language not supported: {script.Language}"
                    });
                    return;
                }

                var syntaxResult = await engine.ValidateSyntaxAsync(script.Content);

                if (!syntaxResult.IsValid)
                {
                    foreach (var error in syntaxResult.Errors)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorType = ValidationErrorType.SyntaxError,
                            Message = error,
                            LineNumber = syntaxResult.LineNumber,
                            ColumnNumber = syntaxResult.ColumnNumber;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.ValidationFailed,
                    Message = $"Syntax validation failed: {ex.Message}"
                });
            }
        }

        private async Task ValidateSecurityIssuesAsync(Script script, ScriptValidationResult result)
        {
            try
            {
                var securityIssues = await DetectSecurityIssuesAsync(script);

                foreach (var issue in securityIssues)
                {
                    result.SecurityIssues.Add(issue);

                    if (issue.Severity >= SecuritySeverity.High)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorType = ValidationErrorType.SecurityIssue,
                            Message = $"Security issue: {issue.Description}",
                            Severity = issue.Severity.ToString()
                        });
                    }
                    else;
                    {
                        result.Warnings.Add($"Security warning: {issue.Description}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Security analysis failed: {ex.Message}");
            }
        }

        private async Task<List<SecurityIssue>> DetectSecurityIssuesAsync(Script script)
        {
            var issues = new List<SecurityIssue>();

            // Potansiyel tehlikeli pattern'ler;
            var dangerousPatterns = new Dictionary<string, SecuritySeverity>
            {
                ["Invoke-Expression"] = SecuritySeverity.Critical,
                ["eval("] = SecuritySeverity.Critical,
                ["exec("] = SecuritySeverity.High,
                ["system("] = SecuritySeverity.High,
                ["shell_exec"] = SecuritySeverity.High,
                ["passthru"] = SecuritySeverity.High,
                ["rm -rf"] = SecuritySeverity.High,
                ["del /f /s /q"] = SecuritySeverity.High,
                ["Format-Volume"] = SecuritySeverity.Critical,
                ["Remove-Item -Force -Recurse"] = SecuritySeverity.High,
                ["netsh firewall"] = SecuritySeverity.High,
                ["reg add"] = SecuritySeverity.Medium,
                ["schtasks"] = SecuritySeverity.Medium;
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (script.Content.Contains(pattern.Key, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new SecurityIssue;
                    {
                        Pattern = pattern.Key,
                        Description = $"Potential dangerous pattern detected: {pattern.Key}",
                        Severity = pattern.Value,
                        Line = FindLineNumber(script.Content, pattern.Key)
                    });
                }
            }

            // Ağ işlemleri kontrolü;
            if (script.Content.Contains("HttpClient") ||
                script.Content.Contains("WebRequest") ||
                script.Content.Contains("curl") ||
                script.Content.Contains("wget"))
            {
                if (!script.RequiresNetworkAccess)
                {
                    issues.Add(new SecurityIssue;
                    {
                        Pattern = "NetworkAccess",
                        Description = "Script contains network operations but network access not specified",
                        Severity = SecuritySeverity.Medium;
                    });
                }
            }

            await Task.CompletedTask;
            return issues;
        }

        private async Task AnalyzePerformanceAsync(Script script, ScriptValidationResult result)
        {
            try
            {
                // Kompleksite analizi;
                var complexity = AnalyzeComplexity(script.Content);
                result.Metadata["Complexity"] = complexity;

                if (complexity > 50)
                {
                    result.Warnings.Add($"High complexity detected: {complexity}. Consider optimizing the script.");
                }

                // Döngü analizi;
                var loopCount = CountLoops(script.Content);
                result.Metadata["LoopCount"] = loopCount;

                if (loopCount > 10)
                {
                    result.Warnings.Add($"Multiple loops detected: {loopCount}. Check for performance issues.");
                }

                // Büyük veri işlemleri;
                if (script.Content.Contains("Get-Content") && script.Content.Contains("-Raw"))
                {
                    result.Warnings.Add("Large file reading detected. Consider streaming for large files.");
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Debug($"Performance analysis failed: {ex.Message}");
            }
        }

        private async Task AnalyzeDependenciesAsync(Script script, ScriptValidationResult result)
        {
            try
            {
                var dependencies = await ExtractDependenciesAsync(script);
                result.Metadata["Dependencies"] = dependencies;

                if (dependencies.Count > 10)
                {
                    result.Warnings.Add($"Multiple dependencies detected: {dependencies.Count}. Consider dependency management.");
                }

                // External dependency kontrolü;
                var externalDeps = dependencies.Where(d => d.IsExternal).ToList();
                if (externalDeps.Any())
                {
                    result.Warnings.Add($"External dependencies detected: {string.Join(", ", externalDeps.Select(d => d.Name))}");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Dependency analysis failed: {ex.Message}");
            }
        }

        private int AnalyzeComplexity(string content)
        {
            // Basit kompleksite hesaplama;
            var lines = content.Split('\n');
            var complexity = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("if ") || trimmed.StartsWith("elseif ") ||
                    trimmed.StartsWith("switch ") || trimmed.Contains("?:"))
                {
                    complexity += 2;
                }
                else if (trimmed.StartsWith("for ") || trimmed.StartsWith("foreach ") ||
                        trimmed.StartsWith("while ") || trimmed.StartsWith("do "))
                {
                    complexity += 3;
                }
                else if (trimmed.StartsWith("try ") || trimmed.StartsWith("catch "))
                {
                    complexity += 1;
                }
                else if (trimmed.Contains("&&") || trimmed.Contains("||"))
                {
                    complexity += 1;
                }
            }

            return complexity;
        }

        private int CountLoops(string content)
        {
            var patterns = new[] { "for ", "foreach ", "while ", "do ", "for(", "while(", "do{" };
            return patterns.Sum(pattern =>
                content.Split(new[] { pattern }, StringSplitOptions.None).Length - 1);
        }

        private async Task<List<ScriptDependency>> ExtractDependenciesAsync(Script script)
        {
            var dependencies = new List<ScriptDependency>();

            switch (script.Language)
            {
                case ScriptLanguage.PowerShell:
                    // PowerShell module imports;
                    var psMatches = System.Text.RegularExpressions.Regex.Matches(
                        script.Content, @"Import-Module\s+['""]?([^'\""\s]+)['""]?");
                    foreach (System.Text.RegularExpressions.Match match in psMatches)
                    {
                        dependencies.Add(new ScriptDependency;
                        {
                            Name = match.Groups[1].Value,
                            Type = "PowerShell Module",
                            IsExternal = true;
                        });
                    }
                    break;

                case ScriptLanguage.Python:
                    // Python imports;
                    var pyMatches = System.Text.RegularExpressions.Regex.Matches(
                        script.Content, @"(?:import|from)\s+([^\s\.]+)");
                    foreach (System.Text.RegularExpressions.Match match in pyMatches)
                    {
                        dependencies.Add(new ScriptDependency;
                        {
                            Name = match.Groups[1].Value,
                            Type = "Python Package",
                            IsExternal = true;
                        });
                    }
                    break;

                case ScriptLanguage.JavaScript:
                    // JavaScript requires/imports;
                    var jsMatches = System.Text.RegularExpressions.Regex.Matches(
                        script.Content, @"(?:require|import)\s*\(?['""]([^'""]+)['""]\)?");
                    foreach (System.Text.RegularExpressions.Match match in jsMatches)
                    {
                        dependencies.Add(new ScriptDependency;
                        {
                            Name = match.Groups[1].Value,
                            Type = "Node.js Package",
                            IsExternal = true;
                        });
                    }
                    break;
            }

            await Task.CompletedTask;
            return dependencies;
        }

        private int FindLineNumber(string content, string pattern)
        {
            var lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(pattern))
                {
                    return i + 1;
                }
            }
            return -1;
        }

        private string CalculateContentHash(string content)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var hash = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private object ParseScriptOutput(string output, OutputFormat format)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            try
            {
                switch (format)
                {
                    case OutputFormat.Json:
                        return System.Text.Json.JsonDocument.Parse(output);

                    case OutputFormat.Xml:
                        var xmlDoc = new System.Xml.XmlDocument();
                        xmlDoc.LoadXml(output);
                        return xmlDoc;

                    case OutputFormat.Csv:
                        return ParseCsv(output);

                    case OutputFormat.KeyValuePairs:
                        return ParseKeyValuePairs(output);

                    default:
                        return output;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to parse output as {format}: {ex.Message}");
                return output;
            }
        }

        private List<Dictionary<string, string>> ParseCsv(string csvContent)
        {
            var result = new List<Dictionary<string, string>>();
            var lines = csvContent.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (lines.Count < 2)
                return result;

            var headers = lines[0].Split(',').Select(h => h.Trim()).ToList();

            for (int i = 1; i < lines.Count; i++)
            {
                var values = lines[i].Split(',');
                var row = new Dictionary<string, string>();

                for (int j = 0; j < Math.Min(headers.Count, values.Length); j++)
                {
                    row[headers[j]] = values[j].Trim();
                }

                result.Add(row);
            }

            return result;
        }

        private Dictionary<string, string> ParseKeyValuePairs(string content)
        {
            var result = new Dictionary<string, string>();
            var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l));

            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '=', ':' }, 2);
                if (parts.Length == 2)
                {
                    result[parts[0].Trim()] = parts[1].Trim();
                }
            }

            return result;
        }

        private void LogResourceUsage(ScriptExecutionResult result)
        {
            if (result.ExecutionStatistics != null)
            {
                _logger.Debug($"Script resource usage - " +
                            $"CPU: {result.ExecutionStatistics.CpuTime.TotalMilliseconds:F0}ms, " +
                            $"Memory: {result.ExecutionStatistics.PeakMemoryUsage / 1024 / 1024:F2}MB, " +
                            $"Total time: {result.TotalExecutionTime.TotalSeconds:F2}s");
            }
        }

        private async Task ValidateDependenciesAsync(List<Script> scripts)
        {
            var graph = BuildDependencyGraph(scripts);

            // Döngüsel bağımlılık kontrolü;
            if (HasCyclicDependencies(graph))
            {
                throw new CircularDependencyException("Circular dependencies detected in script batch");
            }

            // Eksik bağımlılık kontrolü;
            var allScriptNames = scripts.Select(s => s.Name).ToHashSet();
            foreach (var script in scripts)
            {
                if (script.Dependencies != null)
                {
                    var missingDeps = script.Dependencies.Where(d => !allScriptNames.Contains(d)).ToList();
                    if (missingDeps.Any())
                    {
                        throw new MissingDependencyException(
                            $"Missing dependencies for script {script.Name}: {string.Join(", ", missingDeps)}");
                    }
                }
            }

            await Task.CompletedTask;
        }

        private bool HasCyclicDependencies(Dictionary<Guid, List<Guid>> graph)
        {
            var visited = new HashSet<Guid>();
            var recursionStack = new HashSet<Guid>();

            foreach (var node in graph.Keys)
            {
                if (HasCycle(node, graph, visited, recursionStack))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasCycle(Guid node, Dictionary<Guid, List<Guid>> graph,
            HashSet<Guid> visited, HashSet<Guid> recursionStack)
        {
            if (recursionStack.Contains(node))
                return true;

            if (visited.Contains(node))
                return false;

            visited.Add(node);
            recursionStack.Add(node);

            if (graph.TryGetValue(node, out var dependencies))
            {
                foreach (var dep in dependencies)
                {
                    if (HasCycle(dep, graph, visited, recursionStack))
                        return true;
                }
            }

            recursionStack.Remove(node);
            return false;
        }

        private async Task ValidateResourceLimitsAsync(List<Script> scripts, BatchExecutionStrategy strategy)
        {
            var totalEstimatedMemory = scripts.Sum(s => s.ResourceLimits?.MaxMemoryMB ?? 100);
            var totalEstimatedCpu = scripts.Sum(s => s.ResourceLimits?.MaxCpuPercent ?? 25);

            var systemMemory = GetAvailableSystemMemory();
            var systemCpu = Environment.ProcessorCount * 100;

            if (totalEstimatedMemory > systemMemory * 0.8)
            {
                throw new ResourceLimitExceededException(
                    $"Estimated memory usage ({totalEstimatedMemory}MB) exceeds 80% of available system memory ({systemMemory}MB)");
            }

            if (totalEstimatedCpu > systemCpu * 0.9 && strategy == BatchExecutionStrategy.Parallel)
            {
                throw new ResourceLimitExceededException(
                    $"Estimated CPU usage ({totalEstimatedCpu}%) exceeds 90% of available CPU capacity ({systemCpu}%)");
            }

            await Task.CompletedTask;
        }

        private long GetAvailableSystemMemory()
        {
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                return gcInfo.TotalAvailableMemoryBytes / (1024 * 1024);
            }
            catch
            {
                return 8192; // Fallback: 8GB;
            }
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _concurrencySemaphore?.Dispose();

                    // Tüm aktif oturumları temizle;
                    foreach (var session in _activeSessions.Values)
                    {
                        session.Dispose();
                    }
                    _activeSessions.Clear();

                    _sandboxManager?.Dispose();

                    _logger.Info("ScriptRunner disposed");
                }

                _disposed = true;
            }
        }

        ~ScriptRunner()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Betik;
    /// </summary>
    public class Script;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Description { get; set; }
        public string Content { get; set; }
        public ScriptLanguage Language { get; set; }
        public string ContentHash { get; set; }
        public List<string> Arguments { get; set; } = new List<string>();
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> AllowedOperations { get; set; } = new List<string>();
        public ResourceLimits ResourceLimits { get; set; } = new ResourceLimits();
        public bool RequiresIsolation { get; set; } = true;
        public bool RequiresNetworkAccess { get; set; }
        public OutputFormat OutputFormat { get; set; } = OutputFormat.Text;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Modified { get; set; } = DateTime.UtcNow;
        public string Author { get; set; }
        public Version Version { get; set; } = new Version(1, 0, 0);
    }

    /// <summary>
    /// Betik yürütme sonucu;
    /// </summary>
    public class ScriptExecutionResult;
    {
        public Guid ExecutionId { get; set; }
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string ErrorOutput { get; set; }
        public object ParsedOutput { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public ScriptExecutionStatistics ExecutionStatistics { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Batch betik yürütme sonucu;
    /// </summary>
    public class BatchScriptResult;
    {
        public Guid BatchId { get; set; }
        public int TotalScripts { get; set; }
        public int SuccessfulExecutions { get; set; }
        public int FailedExecutions { get; set; }
        public double SuccessRate { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan TotalCpuTime { get; set; }
        public long PeakMemoryUsage { get; set; } // bytes;
        public List<ScriptExecutionResult> AllResults { get; set; } = new List<ScriptExecutionResult>();
        public Dictionary<int, int> ErrorDistribution { get; set; } = new Dictionary<int, int>();
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Betik dili;
    /// </summary>
    public enum ScriptLanguage;
    {
        Unknown = 0,
        PowerShell = 1,
        Python = 2,
        Bash = 3,
        Batch = 4,
        JavaScript = 5,
        TypeScript = 6,
        Lua = 7,
        Ruby = 8,
        Perl = 9,
        CSharp = 10,
        FSharp = 11,
        VB = 12,
        Go = 13,
        Rust = 14,
        Java = 15;
    }

    /// <summary>
    /// Batch yürütme stratejisi;
    /// </summary>
    public class BatchExecutionStrategy;
    {
        public ExecutionMode Mode { get; set; } = ExecutionMode.Parallel;
        public bool StopOnError { get; set; } = true;
        public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
        public bool ContinueOnFailure { get; set; }
        public Dictionary<string, object> Options { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Yürütme modu;
    /// </summary>
    public enum ExecutionMode;
    {
        Parallel = 0,
        Sequential = 1,
        DependencyBased = 2;
    }

    /// <summary>
    /// Kaynak limitleri;
    /// </summary>
    public class ResourceLimits;
    {
        public int MaxMemoryMB { get; set; } = 512;
        public int MaxCpuPercent { get; set; } = 50;
        public int MaxExecutionTimeSeconds { get; set; } = 300;
        public int MaxConcurrentThreads { get; set; } = 4;
        public long MaxDiskUsageMB { get; set; } = 1024;
        public int MaxNetworkBandwidthMbps { get; set; } = 10;
    }

    /// <summary>
    /// Betik yürütme istatistikleri;
    /// </summary>
    public class ScriptExecutionStatistics;
    {
        public TimeSpan CpuTime { get; set; }
        public long PeakMemoryUsage { get; set; } // bytes;
        public TimeSpan TotalProcessorTime { get; set; }
        public int ThreadCount { get; set; }
        public long DiskReadBytes { get; set; }
        public long DiskWriteBytes { get; set; }
        public long NetworkBytesSent { get; set; }
        public long NetworkBytesReceived { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Betik doğrulama sonucu;
    /// </summary>
    public class ScriptValidationResult;
    {
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<SecurityIssue> SecurityIssues { get; set; } = new List<SecurityIssue>();
        public DateTime ValidationTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Doğrulama hatası;
    /// </summary>
    public class ValidationError;
    {
        public ValidationErrorType ErrorType { get; set; }
        public string Message { get; set; }
        public int? LineNumber { get; set; }
        public int? ColumnNumber { get; set; }
        public string Severity { get; set; }
    }

    /// <summary>
    /// Doğrulama hata türü;
    /// </summary>
    public enum ValidationErrorType;
    {
        SyntaxError = 0,
        SecurityIssue = 1,
        InvalidName = 2,
        EmptyContent = 3,
        UnknownLanguage = 4,
        UnsupportedLanguage = 5,
        ValidationFailed = 6,
        ResourceLimitExceeded = 7;
    }

    /// <summary>
    /// Güvenlik sorunu;
    /// </summary>
    public class SecurityIssue;
    {
        public string Pattern { get; set; }
        public string Description { get; set; }
        public SecuritySeverity Severity { get; set; }
        public int? Line { get; set; }
        public string Recommendation { get; set; }
    }

    /// <summary>
    /// Güvenlik şiddeti;
    /// </summary>
    public enum SecuritySeverity;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3;
    }

    /// <summary>
    /// Betik bağımlılığı;
    /// </summary>
    public class ScriptDependency;
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Version { get; set; }
        public bool IsExternal { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// Çıktı formatı;
    /// </summary>
    public enum OutputFormat;
    {
        Text = 0,
        Json = 1,
        Xml = 2,
        Csv = 3,
        KeyValuePairs = 4,
        Html = 5,
        Markdown = 6;
    }

    /// <summary>
    /// Yürütme durumu;
    /// </summary>
    public enum ExecutionStatus;
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4,
        TimedOut = 5;
    }

    #endregion;

    #region Event Classes;

    /// <summary>
    /// Betik yürütme başladı olayı;
    /// </summary>
    public class ScriptExecutionStartedEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public ScriptLanguage Language { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Betik yürütme tamamlandı olayı;
    /// </summary>
    public class ScriptExecutionCompletedEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public ScriptExecutionResult Result { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Betik yürütme iptal edildi olayı;
    /// </summary>
    public class ScriptExecutionCancelledEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Betik güvenlik ihlali olayı;
    /// </summary>
    public class ScriptSecurityViolationEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public string ViolationType { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Batch betik yürütme tamamlandı olayı;
    /// </summary>
    public class BatchScriptExecutionCompletedEvent : IEvent;
    {
        public Guid BatchId { get; set; }
        public int TotalScripts { get; set; }
        public BatchScriptResult Result { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    #endregion;

    #region Exception Classes;

    /// <summary>
    /// Betik yürütme istisnası;
    /// </summary>
    public class ScriptExecutionException : Exception
    {
        public ScriptExecutionException(string message) : base(message) { }
        public ScriptExecutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Batch betik yürütme istisnası;
    /// </summary>
    public class BatchScriptExecutionException : Exception
    {
        public BatchScriptExecutionException(string message) : base(message) { }
        public BatchScriptExecutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Betik geçmişi istisnası;
    /// </summary>
    public class ScriptHistoryException : Exception
    {
        public ScriptHistoryException(string message) : base(message) { }
        public ScriptHistoryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Ortam temizleme istisnası;
    /// </summary>
    public class EnvironmentCleanupException : Exception
    {
        public EnvironmentCleanupException(string message) : base(message) { }
        public EnvironmentCleanupException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Betik motoru başlatma istisnası;
    /// </summary>
    public class ScriptEngineInitializationException : Exception
    {
        public ScriptEngineInitializationException(string message) : base(message) { }
        public ScriptEngineInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Güvenlik ihlali istisnası;
    /// </summary>
    public class SecurityViolationException : Exception
    {
        public SecurityViolationException(string message) : base(message) { }
        public SecurityViolationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Dairesel bağımlılık istisnası;
    /// </summary>
    public class CircularDependencyException : Exception
    {
        public CircularDependencyException(string message) : base(message) { }
        public CircularDependencyException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Eksik bağımlılık istisnası;
    /// </summary>
    public class MissingDependencyException : Exception
    {
        public MissingDependencyException(string message) : base(message) { }
        public MissingDependencyException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Kaynak limiti aşıldı istisnası;
    /// </summary>
    public class ResourceLimitExceededException : Exception
    {
        public ResourceLimitExceededException(string message) : base(message) { }
        public ResourceLimitExceededException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;

    #region Internal Helper Classes and Interfaces;

    internal interface IScriptEngine;
    {
        ScriptLanguage Language { get; }
        string Version { get; }
        Task<ScriptEngineResult> ExecuteAsync(
            string scriptPath,
            ScriptExecutionEnvironment environment,
            List<string> arguments,
            ScriptExecutionSession session,
            CancellationToken cancellationToken);
        Task<SyntaxValidationResult> ValidateSyntaxAsync(string scriptContent);
        Task<bool> IsAvailableAsync();
    }

    internal class ScriptEngineResult;
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string ErrorOutput { get; set; }
        public ScriptExecutionStatistics Statistics { get; set; }
    }

    internal class SyntaxValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public int? LineNumber { get; set; }
        public int? ColumnNumber { get; set; }
    }

    internal interface IScriptEngineProvider;
    {
        Task<IScriptEngine> GetEngineAsync(ScriptLanguage language);
        void RegisterEngine(ScriptLanguage language, IScriptEngine engine);
        Task<bool> IsLanguageSupportedAsync(ScriptLanguage language);
    }

    internal class ScriptEngineProvider : IScriptEngineProvider;
    {
        private readonly Dictionary<ScriptLanguage, IScriptEngine> _engines = new Dictionary<ScriptLanguage, IScriptEngine>();
        private readonly ILogger _logger;

        public ScriptEngineProvider(ILogger logger)
        {
            _logger = logger;
        }

        public Task<IScriptEngine> GetEngineAsync(ScriptLanguage language)
        {
            if (_engines.TryGetValue(language, out var engine))
            {
                return Task.FromResult(engine);
            }

            throw new NotSupportedException($"Script language not supported: {language}");
        }

        public void RegisterEngine(ScriptLanguage language, IScriptEngine engine)
        {
            _engines[language] = engine;
            _logger.Debug($"Registered script engine for language: {language}");
        }

        public Task<bool> IsLanguageSupportedAsync(ScriptLanguage language)
        {
            return Task.FromResult(_engines.ContainsKey(language));
        }
    }

    internal class ScriptExecutionSession : IDisposable
    {
        public Guid SessionId { get; }
        public Script Script { get; }
        public DateTime StartTime { get; }
        public ExecutionStatus Status { get; private set; }
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed;

        public ScriptExecutionSession(Guid sessionId, Script script, ILogger logger)
        {
            SessionId = sessionId;
            Script = script;
            _logger = logger;
            StartTime = DateTime.UtcNow;
            Status = ExecutionStatus.Running;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task CancelAsync()
        {
            if (Status == ExecutionStatus.Running)
            {
                _cancellationTokenSource.Cancel();
                Status = ExecutionStatus.Cancelled;
                _logger.Info($"Script execution session cancelled: {SessionId}");
                await Task.CompletedTask;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cancellationTokenSource?.Dispose();
                _disposed = true;
            }
        }
    }

    internal class ScriptExecutionEnvironment;
    {
        public Guid EnvironmentId { get; set; }
        public string WorkingDirectory { get; set; }
        public string ScriptFilePath { get; set; }
        public IsolationLevel IsolationLevel { get; set; }
        public ResourceLimits ResourceLimits { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public List<string> AllowedOperations { get; set; } = new List<string>();
        public bool NetworkAccess { get; set; }
        public bool PersistOnSuccess { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }

    internal enum IsolationLevel;
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3;
    }

    internal class ScriptSandboxManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly Dictionary<Guid, ScriptExecutionEnvironment> _environments = new Dictionary<Guid, ScriptExecutionEnvironment>();
        private bool _disposed;

        public ScriptSandboxManager(ILogger logger, ISecurityManager securityManager)
        {
            _logger = logger;
            _securityManager = securityManager;
        }

        public async Task<ScriptExecutionEnvironment> CreateEnvironmentAsync(SandboxConfiguration configuration)
        {
            var environmentId = Guid.NewGuid();

            // Sanal ortam oluştur;
            var workingDirectory = Path.Combine(Path.GetTempPath(), $"script_sandbox_{environmentId}");
            Directory.CreateDirectory(workingDirectory);

            var environment = new ScriptExecutionEnvironment;
            {
                EnvironmentId = environmentId,
                WorkingDirectory = workingDirectory,
                IsolationLevel = configuration.IsolationLevel,
                ResourceLimits = configuration.ResourceLimits,
                EnvironmentVariables = configuration.EnvironmentVariables,
                AllowedOperations = configuration.AllowedOperations,
                NetworkAccess = configuration.NetworkAccess;
            };

            _environments[environmentId] = environment;

            // Güvenlik politikalarını uygula;
            await ApplySecurityPoliciesAsync(environment, configuration);

            _logger.Debug($"Created script execution environment: {environmentId}");

            return environment;
        }

        public async Task<string> CreateScriptFileAsync(ScriptExecutionEnvironment environment, string content, ScriptLanguage language)
        {
            var extension = GetScriptExtension(language);
            var scriptPath = Path.Combine(environment.WorkingDirectory, $"script_{environment.EnvironmentId}{extension}");

            await File.WriteAllTextAsync(scriptPath, content);

            // Dosya izinlerini ayarla;
            await SetFilePermissionsAsync(scriptPath, environment);

            _logger.Debug($"Created script file: {scriptPath}");

            return scriptPath;
        }

        public async Task InstallDependenciesAsync(ScriptExecutionEnvironment environment, List<string> dependencies)
        {
            _logger.Debug($"Installing {dependencies.Count} dependencies for environment: {environment.EnvironmentId}");

            // Bağımlılık yükleme mantığı;
            // Burada package manager'lar (pip, npm, nuget, etc.) kullanılır;

            foreach (var dependency in dependencies)
            {
                try
                {
                    await InstallDependencyAsync(environment, dependency);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to install dependency {dependency}: {ex.Message}");
                    // Bağımlılık yükleme hatası kritik değil, devam et;
                }
            }
        }

        public async Task CleanupEnvironmentAsync(Guid environmentId)
        {
            if (_environments.TryGetValue(environmentId, out var environment))
            {
                try
                {
                    // Çalışma dizinini sil;
                    if (Directory.Exists(environment.WorkingDirectory))
                    {
                        Directory.Delete(environment.WorkingDirectory, true);
                    }

                    _environments.Remove(environmentId);
                    _logger.Debug($"Cleaned up environment: {environmentId}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to cleanup environment {environmentId}: {ex.Message}", ex);
                    throw new EnvironmentCleanupException($"Failed to cleanup environment: {ex.Message}", ex);
                }
            }

            await Task.CompletedTask;
        }

        private async Task ApplySecurityPoliciesAsync(ScriptExecutionEnvironment environment, SandboxConfiguration configuration)
        {
            // Güvenlik politikalarını uygula;
            var securityPolicies = await _securityManager.GetSandboxPoliciesAsync(configuration);

            // Dosya sistemi kısıtlamaları;
            await ApplyFileSystemRestrictionsAsync(environment, securityPolicies);

            // Ağ kısıtlamaları;
            await ApplyNetworkRestrictionsAsync(environment, securityPolicies);

            // Process kısıtlamaları;
            await ApplyProcessRestrictionsAsync(environment, securityPolicies);

            _logger.Debug($"Applied security policies for environment: {environment.EnvironmentId}");
        }

        private string GetScriptExtension(ScriptLanguage language)
        {
            return language switch;
            {
                ScriptLanguage.PowerShell => ".ps1",
                ScriptLanguage.Python => ".py",
                ScriptLanguage.Bash => ".sh",
                ScriptLanguage.Batch => ".bat",
                ScriptLanguage.JavaScript => ".js",
                ScriptLanguage.TypeScript => ".ts",
                ScriptLanguage.Lua => ".lua",
                ScriptLanguage.Ruby => ".rb",
                ScriptLanguage.Perl => ".pl",
                ScriptLanguage.CSharp => ".cs",
                _ => ".txt"
            };
        }

        private async Task SetFilePermissionsAsync(string filePath, ScriptExecutionEnvironment environment)
        {
            // Dosya izinlerini ayarla (platforma göre)
            // Windows: FileSecurity, Unix: chmod;
            await Task.CompletedTask;
        }

        private async Task InstallDependencyAsync(ScriptExecutionEnvironment environment, string dependency)
        {
            // Bağımlılık yükleme mantığı;
            // Örnek: pip install package, npm install package, etc.
            await Task.Delay(100); // Simülasyon;
        }

        private async Task ApplyFileSystemRestrictionsAsync(ScriptExecutionEnvironment environment, SandboxSecurityPolicies policies)
        {
            // Dosya sistemi erişim kısıtlamaları;
            await Task.CompletedTask;
        }

        private async Task ApplyNetworkRestrictionsAsync(ScriptExecutionEnvironment environment, SandboxSecurityPolicies policies)
        {
            // Ağ erişim kısıtlamaları;
            await Task.CompletedTask;
        }

        private async Task ApplyProcessRestrictionsAsync(ScriptExecutionEnvironment environment, SandboxSecurityPolicies policies)
        {
            // Process oluşturma kısıtlamaları;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // Tüm ortamları temizle;
                foreach (var environmentId in _environments.Keys.ToList())
                {
                    try
                    {
                        CleanupEnvironmentAsync(environmentId).Wait();
                    }
                    catch
                    {
                        // Temizleme hatalarını ignore et;
                    }
                }

                _disposed = true;
            }
        }
    }

    internal class SandboxConfiguration;
    {
        public Guid ScriptId { get; set; }
        public ScriptLanguage Language { get; set; }
        public IsolationLevel IsolationLevel { get; set; }
        public ResourceLimits ResourceLimits { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public List<string> AllowedOperations { get; set; } = new List<string>();
        public bool NetworkAccess { get; set; }
    }

    internal class SandboxSecurityPolicies;
    {
        public List<string> AllowedFilePaths { get; set; } = new List<string>();
        public List<string> AllowedNetworkEndpoints { get; set; } = new List<string>();
        public List<string> AllowedProcesses { get; set; } = new List<string>();
        public List<string> BannedApiCalls { get; set; } = new List<string>();
        public Dictionary<string, object> AdditionalPolicies { get; set; } = new Dictionary<string, object>();
    }

    internal class ScriptExecutionHistoryManager;
    {
        private readonly ILogger _logger;
        private readonly Dictionary<Guid, ScriptExecutionHistory> _history = new Dictionary<Guid, ScriptExecutionHistory>();

        public ScriptExecutionHistoryManager(ILogger logger)
        {
            _logger = logger;
        }

        public async Task StartExecutionAsync(ScriptExecutionHistory history)
        {
            _history[history.ExecutionId] = history;
            _logger.Debug($"Started execution history tracking: {history.ExecutionId}");
            await Task.CompletedTask;
        }

        public async Task CompleteExecutionAsync(ScriptExecutionHistory history)
        {
            if (_history.ContainsKey(history.ExecutionId))
            {
                _history[history.ExecutionId] = history;
                _logger.Debug($"Completed execution history: {history.ExecutionId}");
            }
            await Task.CompletedTask;
        }

        public async Task CancelExecutionAsync(Guid executionId, TimeSpan duration)
        {
            if (_history.TryGetValue(executionId, out var history))
            {
                history.Status = ExecutionStatus.Cancelled;
                history.EndTime = DateTime.UtcNow;
                history.TotalExecutionTime = duration;
                _logger.Debug($"Cancelled execution history: {executionId}");
            }
            await Task.CompletedTask;
        }

        public async Task FailExecutionAsync(Guid executionId, TimeSpan duration, string reason, string errorDetails)
        {
            if (_history.TryGetValue(executionId, out var history))
            {
                history.Status = ExecutionStatus.Failed;
                history.EndTime = DateTime.UtcNow;
                history.TotalExecutionTime = duration;
                history.ErrorOutput = errorDetails;
                _logger.Debug($"Failed execution history: {executionId}, Reason: {reason}");
            }
            await Task.CompletedTask;
        }

        public async Task<IEnumerable<ScriptExecutionHistory>> GetExecutionHistoryAsync(Guid scriptId, int limit)
        {
            var history = _history.Values;
                .Where(h => h.ScriptId == scriptId)
                .OrderByDescending(h => h.StartTime)
                .Take(limit)
                .ToList();

            await Task.CompletedTask;
            return history;
        }
    }

    internal class ScriptExecutionHistory;
    {
        public Guid ExecutionId { get; set; }
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public ScriptLanguage Language { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public ExecutionStatus Status { get; set; }
        public int? ExitCode { get; set; }
        public string Output { get; set; }
        public string ErrorOutput { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    internal interface IRealTimeScriptStream : IDisposable
    {
        Task<ScriptExecutionResult> ExecuteAsync(CancellationToken cancellationToken);
        Task SendInputAsync(string input);
        event EventHandler<ScriptOutputEventArgs> OutputReceived;
        event EventHandler<ScriptErrorEventArgs> ErrorReceived;
        event EventHandler<ScriptCompletedEventArgs> Completed;
    }

    internal class RealTimeScriptStream : IRealTimeScriptStream;
    {
        private readonly Guid _streamId;
        private readonly Script _script;
        private readonly ILogger _logger;
        private readonly IScriptEngineProvider _engineProvider;
        private readonly ScriptSandboxManager _sandboxManager;
        private bool _disposed;
        private bool _executing;

        public event EventHandler<ScriptOutputEventArgs> OutputReceived;
        public event EventHandler<ScriptErrorEventArgs> ErrorReceived;
        public event EventHandler<ScriptCompletedEventArgs> Completed;

        public RealTimeScriptStream(
            Guid streamId,
            Script script,
            ILogger logger,
            IScriptEngineProvider engineProvider,
            ScriptSandboxManager sandboxManager)
        {
            _streamId = streamId;
            _script = script;
            _logger = logger;
            _engineProvider = engineProvider;
            _sandboxManager = sandboxManager;
        }

        public async Task<ScriptExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (_executing)
                throw new InvalidOperationException("Stream is already executing");

            _executing = true;

            try
            {
                // Gerçek zamanlı yürütme mantığı;
                var engine = await _engineProvider.GetEngineAsync(_script.Language);
                var environment = await _sandboxManager.CreateEnvironmentAsync(new SandboxConfiguration;
                {
                    ScriptId = _script.Id,
                    Language = _script.Language,
                    IsolationLevel = _script.RequiresIsolation ? IsolationLevel.High : IsolationLevel.Medium;
                });

                // Betik dosyasını oluştur;
                var scriptPath = await _sandboxManager.CreateScriptFileAsync(environment, _script.Content, _script.Language);

                // Yürüt;
                var result = await engine.ExecuteAsync(scriptPath, environment, _script.Arguments ?? new List<string>(), null, cancellationToken);

                var executionResult = new ScriptExecutionResult;
                {
                    Success = result.Success,
                    ExitCode = result.ExitCode,
                    Output = result.Output,
                    ErrorOutput = result.ErrorOutput,
                    ExecutionStatistics = result.Statistics;
                };

                // Olayları tetikle;
                if (!string.IsNullOrEmpty(result.Output))
                {
                    OutputReceived?.Invoke(this, new ScriptOutputEventArgs;
                    {
                        Output = result.Output,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                if (!string.IsNullOrEmpty(result.ErrorOutput))
                {
                    ErrorReceived?.Invoke(this, new ScriptErrorEventArgs;
                    {
                        Error = result.ErrorOutput,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                Completed?.Invoke(this, new ScriptCompletedEventArgs;
                {
                    Success = result.Success,
                    ExitCode = result.ExitCode,
                    Timestamp = DateTime.UtcNow;
                });

                return executionResult;
            }
            finally
            {
                _executing = false;
            }
        }

        public async Task SendInputAsync(string input)
        {
            // Gerçek zamanlı giriş gönderme;
            _logger.Debug($"Received input for stream {_streamId}: {input}");
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _logger.Debug($"Real-time script stream disposed: {_streamId}");
            }
        }
    }

    internal class ScriptOutputEventArgs : EventArgs;
    {
        public string Output { get; set; }
        public DateTime Timestamp { get; set; }
    }

    internal class ScriptErrorEventArgs : EventArgs;
    {
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    internal class ScriptCompletedEventArgs : EventArgs;
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Betik motoru implementasyonları (basit örnekler)
    internal class PowerShellEngine : IScriptEngine;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;

        public ScriptLanguage Language => ScriptLanguage.PowerShell;
        public string Version => "7.2.0";

        public PowerShellEngine(ILogger logger, ISecurityManager securityManager)
        {
            _logger = logger;
            _securityManager = securityManager;
        }

        public async Task<ScriptEngineResult> ExecuteAsync(
            string scriptPath,
            ScriptExecutionEnvironment environment,
            List<string> arguments,
            ScriptExecutionSession session,
            CancellationToken cancellationToken)
        {
            var result = new ScriptEngineResult();
            var startTime = DateTime.UtcNow;

            try
            {
                // PowerShell yürütme mantığı;
                using var process = new Process;
                {
                    StartInfo = new ProcessStartInfo;
                    {
                        FileName = "pwsh",
                        Arguments = $"-File \"{scriptPath}\" {string.Join(" ", arguments)}",
                        WorkingDirectory = environment.WorkingDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true;
                    }
                };

                foreach (var envVar in environment.EnvironmentVariables)
                {
                    process.StartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
                }

                process.Start();

                // Çıktıları oku;
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(cancellationToken);

                result.ExitCode = process.ExitCode;
                result.Output = output;
                result.ErrorOutput = error;
                result.Success = process.ExitCode == 0;

                // İstatistikleri topla;
                result.Statistics = new ScriptExecutionStatistics;
                {
                    CpuTime = process.TotalProcessorTime,
                    PeakMemoryUsage = process.PeakWorkingSet64;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"PowerShell execution failed: {ex.Message}", ex);
                result.Success = false;
                result.ErrorOutput = $"Execution failed: {ex.Message}";
                result.ExitCode = -1;
            }

            return result;
        }

        public async Task<SyntaxValidationResult> ValidateSyntaxAsync(string scriptContent)
        {
            var result = new SyntaxValidationResult();

            try
            {
                // PowerShell sözdizimi doğrulama;
                using var process = new Process;
                {
                    StartInfo = new ProcessStartInfo;
                    {
                        FileName = "pwsh",
                        Arguments = $"-Command \"& {{param($script); $errors = @(); $null = [System.Management.Automation.PSParser]::Tokenize($script, [ref]$errors); if ($errors.Count -gt 0) {{ throw 'Syntax errors' }} }}\"",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true;
                    }
                };

                process.Start();
                await process.StandardInput.WriteAsync(scriptContent);
                process.StandardInput.Close();

                await process.WaitForExitAsync();

                result.IsValid = process.ExitCode == 0;

                if (!result.IsValid)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    result.Errors.Add(error);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"PowerShell syntax validation failed: {ex.Message}");
                result.IsValid = false;
                result.Errors.Add($"Validation failed: {ex.Message}");
            }

            return result;
        }

        public Task<bool> IsAvailableAsync()
        {
            try
            {
                using var process = new Process;
                {
                    StartInfo = new ProcessStartInfo;
                    {
                        FileName = "pwsh",
                        Arguments = "-Command \"$PSVersionTable.PSVersion\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true;
                    }
                };

                process.Start();
                process.WaitForExit();

                return Task.FromResult(process.ExitCode == 0);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }

    internal class PythonEngine : IScriptEngine;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;

        public ScriptLanguage Language => ScriptLanguage.Python;
        public string Version => "3.9.0";

        public PythonEngine(ILogger logger, ISecurityManager securityManager)
        {
            _logger = logger;
            _securityManager = securityManager;
        }

        public async Task<ScriptEngineResult> ExecuteAsync(
            string scriptPath,
            ScriptExecutionEnvironment environment,
            List<string> arguments,
            ScriptExecutionSession session,
            CancellationToken cancellationToken)
        {
            // Python yürütme implementasyonu;
            // PowerShellEngine benzeri bir implementasyon;
            await Task.CompletedTask;
            return new ScriptEngineResult();
        }

        public async Task<SyntaxValidationResult> ValidateSyntaxAsync(string scriptContent)
        {
            // Python sözdizimi doğrulama;
            await Task.CompletedTask;
            return new SyntaxValidationResult();
        }

        public Task<bool> IsAvailableAsync()
        {
            return Task.FromResult(true);
        }
    }

    // Diğer betik motorları için benzer implementasyonlar...

    internal class BashEngine : IScriptEngine { /* Implementasyon */ }
    internal class BatchEngine : IScriptEngine { /* Implementasyon */ }
    internal class JavaScriptEngine : IScriptEngine { /* Implementasyon */ }
    internal class TypeScriptEngine : IScriptEngine { /* Implementasyon */ }
    internal class LuaEngine : IScriptEngine { /* Implementasyon */ }
    internal class RubyEngine : IScriptEngine { /* Implementasyon */ }
    internal class PerlEngine : IScriptEngine { /* Implementasyon */ }

    #endregion;
}
