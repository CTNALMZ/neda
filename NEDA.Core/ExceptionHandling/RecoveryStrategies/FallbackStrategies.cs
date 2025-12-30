using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.AI.MachineLearning;
using NEDA.AI.NeuralNetwork;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Brain.DecisionMaking;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Build.CI_CD;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Engine;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.ExceptionHandling.ErrorReporting;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using NEDA.KnowledgeBase.LocalDB;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.HealthChecks;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.CharacterCreator.MorphTargets.BlendShapeEngine;
using static NEDA.Core.Engine.DecisionEngine;

namespace NEDA.Core.ExceptionHandling.RecoveryStrategies
{
    /// <summary>
    /// Fallback Strategies - Sistem hatası durumunda otomatik kurtarma stratejileri;
    /// Graceful degradation ve fault tolerance sağlar;
    /// </summary>
    public interface IFallbackStrategies : IDisposable
    {
        /// <summary>
        /// Fallback stratejilerini başlat;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Kritik hata durumunda uygulanacak fallback stratejisini seç;
        /// </summary>
        Task<FallbackStrategy> SelectStrategyAsync(
            Exception exception,
            ErrorContext context,
            SystemState systemState);

        /// <summary>
        /// Seçilen fallback stratejisini uygula;
        /// </summary>
        Task<FallbackExecutionResult> ExecuteStrategyAsync(
            FallbackStrategy strategy,
            FallbackExecutionContext context);

        /// <summary>
        /// Birden fazla stratejiyi paralel olarak değerlendir;
        /// </summary>
        Task<List<FallbackStrategy>> EvaluateMultipleStrategiesAsync(
            Exception exception,
            ErrorContext context,
            SystemState systemState);

        /// <summary>
        /// Fallback stratejisinin etkinliğini ölç;
        /// </summary>
        Task<StrategyEffectiveness> MeasureEffectivenessAsync(FallbackStrategy strategy);

        /// <summary>
        /// Fallback learning - stratejileri iyileştir;
        /// </summary>
        Task<LearningResult> ImproveStrategiesAsync(
            List<FallbackExecutionResult> historicalResults);

        /// <summary>
        /// Emergency shutdown protocol'ü başlat;
        /// </summary>
        Task<EmergencyShutdownResult> ExecuteEmergencyShutdownAsync(
            EmergencyCondition condition,
            CancellationToken cancellationToken);

        /// <summary>
        /// Sistem durumunu stabilize et;
        /// </summary>
        Task<StabilizationResult> StabilizeSystemAsync(
            SystemState currentState,
            StabilizationOptions options);

        /// <summary>
        /// Degraded mode'a geç;
        /// </summary>
        Task<DegradedModeResult> EnterDegradedModeAsync(
            DegradationLevel level,
            DegradedModeOptions options);

        /// <summary>
        /// Full recovery yap;
        /// </summary>
        Task<RecoveryResult> PerformFullRecoveryAsync(RecoveryPlan plan);

        /// <summary>
        /// Fallback strateji durumunu getir;
        /// </summary>
        Task<FallbackSystemStatus> GetSystemStatusAsync();

        /// <summary>
        /// Senaryo bazlı fallback planı oluştur;
        /// </summary>
        Task<FallbackScenarioPlan> CreateScenarioPlanAsync(FallbackScenario scenario);

        /// <summary>
        /// Fallback stratejilerini test et;
        /// </summary>
        Task<StrategyTestResult> TestStrategyAsync(
            string strategyId,
            TestEnvironment environment);
    }

    /// <summary>
    /// Fallback Strategies - Implementasyon;
    /// </summary>
    public class FallbackStrategies : IFallbackStrategies
    {
        private readonly ILogger<FallbackStrategies> _logger;
        private readonly AppConfig _appConfig; // FIX: IOptions<AppConfig>.Value ile uyum için
        private readonly ISecurityManager _securityManager;
        private readonly ISystemManager _systemManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IHealthChecker _healthChecker;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IEventBus _eventBus;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly ILongTermMemory _longTermMemory;
        private readonly IDecisionEngine _decisionEngine;

        private readonly StrategyRepository _strategyRepository;
        private readonly StrategyEvaluator _strategyEvaluator;
        private readonly StrategyExecutor _strategyExecutor;
        private readonly StrategyLearner _strategyLearner;
        private readonly EmergencyController _emergencyController;

        private bool _isInitialized = false;
        private bool _isDisposed = false;
        private DegradationLevel _currentDegradationLevel = DegradationLevel.Normal;
        private DateTime _enteredDegradedModeAt = DateTime.MinValue;

        private readonly Dictionary<string, FallbackStrategy> _activeStrategies = new();
        private readonly Dictionary<string, StrategyEffectiveness> _strategyEffectiveness = new();
        private readonly SemaphoreSlim _executionLock = new(1, 1);

        private readonly CancellationTokenSource _backgroundTasksCts = new();
        private Task? _effectivenessMonitoringTask;
        private Task? _strategyOptimizationTask;

        /// <summary>
        /// Constructor;
        /// </summary>
        public FallbackStrategies(
            ILogger<FallbackStrategies> logger,
            IOptions<AppConfig> appConfigOptions,
            ISecurityManager securityManager,
            ISystemManager systemManager,
            IPerformanceMonitor performanceMonitor,
            IHealthChecker healthChecker,
            IDiagnosticTool diagnosticTool,
            IEventBus eventBus,
            IKnowledgeBase knowledgeBase,
            ILongTermMemory longTermMemory,
            IDecisionEngine decisionEngine,
            StrategyRepository strategyRepository,
            StrategyEvaluator strategyEvaluator,
            StrategyExecutor strategyExecutor,
            StrategyLearner strategyLearner,
            EmergencyController emergencyController)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfigOptions?.Value ?? throw new ArgumentNullException(nameof(appConfigOptions));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));

            _strategyRepository = strategyRepository ?? throw new ArgumentNullException(nameof(strategyRepository));
            _strategyEvaluator = strategyEvaluator ?? throw new ArgumentNullException(nameof(strategyEvaluator));
            _strategyExecutor = strategyExecutor ?? throw new ArgumentNullException(nameof(strategyExecutor));
            _strategyLearner = strategyLearner ?? throw new ArgumentNullException(nameof(strategyLearner));
            _emergencyController = emergencyController ?? throw new ArgumentNullException(nameof(emergencyController));

            _logger.LogInformation("FallbackStrategies initialized");
        }

        /// <summary>
        /// Başlatma;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("FallbackStrategies already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Starting FallbackStrategies initialization...");

                // 1. Güvenlik kontrolü;
                await _securityManager.ValidateRecoveryPermissionsAsync(cancellationToken);

                // 2. Strateji repository'sini başlat;
                await _strategyRepository.InitializeAsync(cancellationToken);

                // 3. Temel stratejileri yükle;
                await LoadCoreStrategiesAsync(cancellationToken);

                // 4. Strateji evaluator'ı başlat;
                await _strategyEvaluator.InitializeAsync(cancellationToken);

                // 5. Strateji executor'ı başlat;
                await _strategyExecutor.InitializeAsync(cancellationToken);

                // 6. Strateji learner'ı başlat;
                await _strategyLearner.InitializeAsync(cancellationToken);

                // 7. Emergency controller'ı başlat;
                await _emergencyController.InitializeAsync(cancellationToken);

                // 8. Arka plan görevlerini başlat;
                StartBackgroundTasks();

                // 9. System event'lerine subscribe ol;
                await SubscribeToSystemEventsAsync();

                _isInitialized = true;

                _logger.LogInformation("FallbackStrategies initialization completed successfully");
                _logger.LogInformation("Loaded {StrategyCount} fallback strategies", _activeStrategies.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FallbackStrategies initialization failed");
                throw new FallbackInitializationException(
                    ErrorCodes.System.SYSTEM_INITIALIZATION_FAILED,
                    "Failed to initialize FallbackStrategies",
                    ex);
            }
        }

        /// <summary>
        /// Fallback stratejisi seç;
        /// </summary>
        public async Task<FallbackStrategy> SelectStrategyAsync(
            Exception exception,
            ErrorContext context,
            SystemState systemState)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Selecting fallback strategy for error: {ErrorCode} in component: {Component}",
                    DetermineErrorCode(exception), context.Component);

                // 1. Uygun stratejileri filtrele;
                var suitableStrategies = await FilterSuitableStrategiesAsync(exception, context, systemState);

                if (!suitableStrategies.Any())
                {
                    _logger.LogWarning("No suitable fallback strategies found. Using emergency fallback.");
                    return await GetEmergencyFallbackStrategyAsync();
                }

                // 2. Stratejileri değerlendir;
                var evaluatedStrategies = await EvaluateStrategiesAsync(suitableStrategies, exception, context, systemState);

                // 3. En iyi stratejiyi seç;
                var selectedStrategy = await ChooseBestStrategyAsync(evaluatedStrategies, context);

                // 4. Stratejiyi aktif stratejilere ekle;
                _activeStrategies[selectedStrategy.StrategyId] = selectedStrategy;

                // 5. Event yayınla;
                await _eventBus.PublishAsync(new FallbackStrategySelectedEvent
                {
                    StrategyId = selectedStrategy.StrategyId,
                    StrategyName = selectedStrategy.Name,
                    ErrorCode = DetermineErrorCode(exception),
                    Component = context.Component,
                    Timestamp = DateTime.UtcNow,
                    SelectionReason = selectedStrategy.SelectionReason,
                });

                _logger.LogInformation("Selected fallback strategy: {StrategyName} ({StrategyId})",
                    selectedStrategy.Name, selectedStrategy.StrategyId);

                return selectedStrategy;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to select fallback strategy");
                return await GetEmergencyFallbackStrategyAsync();
            }
        }

        /// <summary>
        /// Fallback stratejisini uygula;
        /// </summary>
        public async Task<FallbackExecutionResult> ExecuteStrategyAsync(
            FallbackStrategy strategy,
            FallbackExecutionContext context)
        {
            ValidateInitialization();

            await _executionLock.WaitAsync();

            try
            {
                var executionId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                _logger.LogInformation("Executing fallback strategy: {StrategyName} ({StrategyId})",
                    strategy.Name, strategy.StrategyId);

                // 1. Execution context'i hazırla;
                var enhancedContext = await EnhanceExecutionContextAsync(context, strategy);

                // 2. Pre-execution kontrolleri;
                var preCheckResult = await PerformPreExecutionChecksAsync(strategy, enhancedContext);
                if (!preCheckResult.CanExecute)
                {
                    _logger.LogWarning("Pre-execution checks failed for strategy: {StrategyId}. Reason: {Reason}",
                        strategy.StrategyId, preCheckResult.FailureReason);

                    return new FallbackExecutionResult
                    {
                        ExecutionId = executionId,
                        StrategyId = strategy.StrategyId,
                        Success = false,
                        StartTime = startTime,
                        EndTime = DateTime.UtcNow,
                        Outcome = FallbackOutcome.FailedPreCheck,
                        ErrorMessage = preCheckResult.FailureReason,
                        Metrics = new ExecutionMetrics()
                    };
                }

                // 3. Stratejiyi çalıştır;
                var executionResult = await _strategyExecutor.ExecuteAsync(strategy, enhancedContext);
                executionResult.ExecutionId = executionId;
                executionResult.StartTime = startTime;
                executionResult.EndTime = DateTime.UtcNow;

                // 4. Post-execution işlemleri;
                await PerformPostExecutionActionsAsync(strategy, executionResult, enhancedContext);

                // 5. Etkinliği ölç;
                var effectiveness = await MeasureStrategyEffectivenessAsync(strategy, executionResult);
                _strategyEffectiveness[strategy.StrategyId] = effectiveness;

                // 6. Execution'ı kaydet;
                await _strategyRepository.SaveExecutionResultAsync(executionResult);

                // 7. Event yayınla;
                await _eventBus.PublishAsync(new FallbackStrategyExecutedEvent
                {
                    ExecutionId = executionId,
                    StrategyId = strategy.StrategyId,
                    Success = executionResult.Success,
                    Outcome = executionResult.Outcome,
                    ExecutionTime = executionResult.EndTime - executionResult.StartTime,
                    Timestamp = DateTime.UtcNow,
                    ErrorMessage = executionResult.ErrorMessage,
                    Component = context.OriginalErrorContext.Component,
                });

                _logger.LogInformation("Fallback strategy execution completed: {StrategyId}, Success: {Success}, Outcome: {Outcome}",
                    strategy.StrategyId, executionResult.Success, executionResult.Outcome);

                return executionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute fallback strategy: {StrategyId}", strategy.StrategyId);

                return new FallbackExecutionResult
                {
                    ExecutionId = Guid.NewGuid().ToString(),
                    StrategyId = strategy.StrategyId,
                    Success = false,
                    StartTime = DateTime.UtcNow,
                    EndTime = DateTime.UtcNow,
                    Outcome = FallbackOutcome.ExecutionError,
                    ErrorMessage = ex.Message,
                    Metrics = new ExecutionMetrics(),
                    Exception = ex,
                };
            }
            finally
            {
                _executionLock.Release();
            }
        }

        /// <summary>
        /// Birden fazla stratejiyi değerlendir;
        /// </summary>
        public async Task<List<FallbackStrategy>> EvaluateMultipleStrategiesAsync(
            Exception exception,
            ErrorContext context,
            SystemState systemState)
        {
            ValidateInitialization();

            try
            {
                _logger.LogDebug("Evaluating multiple fallback strategies for error: {ErrorCode}",
                    DetermineErrorCode(exception));

                // 1. Tüm uygun stratejileri getir;
                var suitableStrategies = await FilterSuitableStrategiesAsync(exception, context, systemState);

                // 2. Her stratejiyi paralel olarak değerlendir;
                var evaluationTasks = suitableStrategies.Select(strategy =>
                    EvaluateStrategyAsync(strategy, exception, context, systemState));

                var evaluatedStrategies = await Task.WhenAll(evaluationTasks);

                // 3. Değerlendirme skoruna göre sırala;
                return evaluatedStrategies
                    .OrderByDescending(s => s.EvaluationScore)
                    .ThenByDescending(s => s.ConfidenceLevel)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate multiple strategies");
                return new List<FallbackStrategy>();
            }
        }

        /// <summary>
        /// Strateji etkinliğini ölç;
        /// </summary>
        public async Task<StrategyEffectiveness> MeasureEffectivenessAsync(FallbackStrategy strategy)
        {
            ValidateInitialization();

            try
            {
                _logger.LogDebug("Measuring effectiveness of strategy: {StrategyId}", strategy.StrategyId);

                // 1. Tarihsel execution'ları getir;
                var historicalResults = await _strategyRepository.GetExecutionHistoryAsync(
                    strategy.StrategyId,
                    TimeSpan.FromDays(30));

                // 2. Etkinlik metriklerini hesapla;
                var effectiveness = await CalculateEffectivenessMetricsAsync(strategy, historicalResults);

                // 3. Strateji etkinlik cache'ini güncelle;
                _strategyEffectiveness[strategy.StrategyId] = effectiveness;

                // 4. Etkinlik raporu oluştur;
                var report = await GenerateEffectivenessReportAsync(strategy, effectiveness);

                // 5. Öğrenme için kaydet;
                await _strategyLearner.LearnFromEffectivenessAsync(strategy, effectiveness, historicalResults);

                return effectiveness;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to measure strategy effectiveness: {StrategyId}", strategy.StrategyId);
                return new StrategyEffectiveness
                {
                    StrategyId = strategy.StrategyId,
                    CalculationTime = DateTime.UtcNow,
                    Confidence = 0.0f,
                    IsReliable = false,
                };
            }
        }

        /// <summary>
        /// Stratejileri iyileştir;
        /// </summary>
        public async Task<LearningResult> ImproveStrategiesAsync(
            List<FallbackExecutionResult> historicalResults)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Improving fallback strategies based on {ResultCount} historical results",
                    historicalResults.Count);

                // 1. Başarılı ve başarısız execution'ları analiz et;
                var analysis = await AnalyzeHistoricalResultsAsync(historicalResults);

                // 2. Strateji optimizasyonu yap;
                var optimizationResult = await _strategyLearner.OptimizeStrategiesAsync(analysis);

                // 3. Yeni stratejiler oluştur;
                var newStrategies = await GenerateNewStrategiesAsync(analysis, optimizationResult);

                // 4. Stratejileri test et;
                var testResults = await TestNewStrategiesAsync(newStrategies);

                // 5. Başarılı stratejileri deploy et;
                var deployedStrategies = await DeploySuccessfulStrategiesAsync(testResults);

                // 6. Eski stratejileri archive et;
                await ArchiveIneffectiveStrategiesAsync(analysis);

                return new LearningResult
                {
                    Success = true,
                    OptimizedStrategies = optimizationResult.OptimizedCount,
                    NewStrategies = newStrategies.Count,
                    DeployedStrategies = deployedStrategies.Count,
                    ArchivedStrategies = analysis.IneffectiveStrategies.Count,
                    LearningTime = DateTime.UtcNow,
                    Improvements = optimizationResult.Improvements,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to improve strategies");
                return new LearningResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    LearningTime = DateTime.UtcNow,
                };
            }
        }

        /// <summary>
        /// Emergency shutdown protocol'ü başlat;
        /// </summary>
        public async Task<EmergencyShutdownResult> ExecuteEmergencyShutdownAsync(
            EmergencyCondition condition,
            CancellationToken cancellationToken)
        {
            ValidateInitialization();

            try
            {
                _logger.LogCritical("Initiating emergency shutdown. Condition: {Condition}, Level: {Level}",
                    condition.Type, condition.SeverityLevel);

                // 1. Emergency controller'ı başlat;
                await _emergencyController.InitializeEmergencyProtocolAsync(condition, cancellationToken);

                // 2. Kritik sistemleri güvenli şekilde kapat;
                var shutdownResults = await _emergencyController.ShutdownCriticalSystemsAsync(cancellationToken);

                // 3. Veri kaybını önle;
                var dataPreservationResult = await _emergencyController.PreserveCriticalDataAsync(cancellationToken);

                // 4. Sistem durumunu kaydet;
                var systemSnapshot = await _emergencyController.CaptureSystemSnapshotAsync(cancellationToken);

                // 5. Log ve audit kayıtlarını güvenli hale getir;
                await _emergencyController.SecureAuditLogsAsync(cancellationToken);

                // 6. Kullanıcılara bildirim gönder;
                await _emergencyController.NotifyUsersAsync(condition, cancellationToken);

                // 7. Sistem tamamen kapat;
                var finalShutdownResult = await _emergencyController.PerformFinalShutdownAsync(cancellationToken);

                // 8. Event yayınla;
                await _eventBus.PublishAsync(new EmergencyShutdownExecutedEvent
                {
                    Condition = condition,
                    Timestamp = DateTime.UtcNow,
                    ShutdownResults = shutdownResults,
                    DataPreserved = dataPreservationResult.Success,
                    SystemSnapshotId = systemSnapshot.SnapshotId,
                });

                _logger.LogCritical("Emergency shutdown completed successfully");

                return new EmergencyShutdownResult
                {
                    Success = true,
                    ShutdownTime = DateTime.UtcNow,
                    Condition = condition,
                    SystemsShutdown = shutdownResults.Count,
                    DataPreserved = dataPreservationResult.Success,
                    SystemSnapshotId = systemSnapshot.SnapshotId,
                    FinalState = EmergencyState.ShutdownComplete,
                };
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Emergency shutdown failed");

                // Fallback: Hard shutdown;
                await PerformHardShutdownAsync();

                return new EmergencyShutdownResult
                {
                    Success = false,
                    ShutdownTime = DateTime.UtcNow,
                    Condition = condition,
                    ErrorMessage = ex.Message,
                    FinalState = EmergencyState.ShutdownFailed,
                    RequiresManualIntervention = true,
                };
            }
        }

        /// <summary>
        /// Sistem durumunu stabilize et;
        /// </summary>
        public async Task<StabilizationResult> StabilizeSystemAsync(
            SystemState currentState,
            StabilizationOptions options)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Stabilizing system. Current health: {HealthScore}",
                    currentState.SystemHealth.Score);

                // 1. Sistem durumunu analiz et;
                var analysis = await AnalyzeSystemStabilityAsync(currentState);

                // 2. Instability nedenlerini belirle;
                var instabilityCauses = await IdentifyInstabilityCausesAsync(analysis);

                // 3. Stabilizasyon planı oluştur;
                var stabilizationPlan = await CreateStabilizationPlanAsync(analysis, instabilityCauses, options);

                // 4. Planı uygula;
                var executionResult = await ExecuteStabilizationPlanAsync(stabilizationPlan);

                // 5. Sonuçları değerlendir;
                var finalState = await _healthChecker.GetSystemHealthAsync();
                var improvement = finalState.Score - currentState.SystemHealth.Score;

                // 6. Event yayınla;
                await _eventBus.PublishAsync(new SystemStabilizedEvent
                {
                    InitialHealth = currentState.SystemHealth.Score,
                    FinalHealth = finalState.Score,
                    Improvement = improvement,
                    Timestamp = DateTime.UtcNow,
                    ActionsTaken = executionResult.ActionsExecuted,
                    Duration = executionResult.Duration,
                });

                _logger.LogInformation("System stabilization completed. Improvement: {Improvement} points", improvement);

                return new StabilizationResult
                {
                    Success = true,
                    InitialHealthScore = currentState.SystemHealth.Score,
                    FinalHealthScore = finalState.Score,
                    Improvement = improvement,
                    Duration = executionResult.Duration,
                    ActionsTaken = executionResult.ActionsExecuted,
                    StabilizationLevel = DetermineStabilizationLevel(improvement),
                    Recommendations = await GenerateStabilizationRecommendationsAsync(analysis, finalState)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System stabilization failed");

                return new StabilizationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    InitialHealthScore = currentState.SystemHealth.Score,
                    FinalHealthScore = currentState.SystemHealth.Score,
                    Improvement = 0,
                    StabilizationLevel = StabilizationLevel.Failed,
                    RequiresManualIntervention = true,
                };
            }
        }

        /// <summary>
        /// Degraded mode'a geç;
        /// </summary>
        public async Task<DegradedModeResult> EnterDegradedModeAsync(
            DegradationLevel level,
            DegradedModeOptions options)
        {
            ValidateInitialization();

            try
            {
                _logger.LogWarning("Entering degraded mode. Level: {Level}, Reason: {Reason}",
                    level, options.Reason);

                // 1. Mevcut durumu kaydet;
                var preDegradationState = await CapturePreDegradationStateAsync();

                // 2. Degraded mode planı oluştur;
                var degradationPlan = await CreateDegradationPlanAsync(level, options);

                // 3. Planı uygula;
                var executionResult = await ExecuteDegradationPlanAsync(degradationPlan);

                // 4. Degraded mode'a geç;
                _currentDegradationLevel = level;
                _enteredDegradedModeAt = DateTime.UtcNow;

                // 5. Sistem davranışını ayarla;
                await AdjustSystemBehaviorForDegradedModeAsync(level, options);

                // 6. Kullanıcıları bilgilendir;
                await NotifyUsersOfDegradedModeAsync(level, options);

                // 7. Monitoring'i güncelle;
                await UpdateMonitoringForDegradedModeAsync(level);

                // 8. Event yayınla;
                await _eventBus.PublishAsync(new DegradedModeEnteredEvent
                {
                    Level = level,
                    EnteredAt = DateTime.UtcNow,
                    Reason = options.Reason,
                    ExpectedDuration = options.ExpectedDuration,
                    ServicesAffected = degradationPlan.AffectedServices,
                    PerformanceImpact = degradationPlan.ExpectedPerformanceImpact,
                });

                _logger.LogWarning("Degraded mode entered successfully. Level: {Level}", level);

                return new DegradedModeResult
                {
                    Success = true,
                    Level = level,
                    EnteredAt = DateTime.UtcNow,
                    PreviousLevel = preDegradationState.DegradationLevel,
                    ServicesReduced = degradationPlan.ServicesToReduce.Count,
                    PerformanceLevel = degradationPlan.ExpectedPerformanceImpact,
                    ExpectedRecoveryTime = options.ExpectedDuration,
                    MonitoringAdjusted = true,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enter degraded mode");

                return new DegradedModeResult
                {
                    Success = false,
                    Level = DegradationLevel.Normal,
                    ErrorMessage = ex.Message,
                    RequiresManualIntervention = true,
                };
            }
        }

        /// <summary>
        /// Full recovery yap;
        /// </summary>
        public async Task<RecoveryResult> PerformFullRecoveryAsync(RecoveryPlan plan)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Performing full recovery. Plan: {PlanId}", plan.PlanId);

                // 1. Recovery öncesi durumu kaydet;
                var preRecoveryState = await CapturePreRecoveryStateAsync();

                // 2. Recovery planını doğrula;
                var validationResult = await ValidateRecoveryPlanAsync(plan);
                if (!validationResult.IsValid)
                {
                    throw new RecoveryValidationException($"Recovery plan validation failed: {validationResult.Errors.First()}");
                }

                // 3. Recovery adımlarını uygula;
                var recoverySteps = await ExecuteRecoveryStepsAsync(plan);

                // 4. Sistem sağlığını kontrol et;
                var postRecoveryHealth = await _healthChecker.GetSystemHealthAsync();

                // 5. Recovery sonrası optimizasyon;
                await PerformPostRecoveryOptimizationAsync(plan, postRecoveryHealth);

                // 6. Degraded mode'dan çık (eğer varsa)
                if (_currentDegradationLevel != DegradationLevel.Normal)
                {
                    await ExitDegradedModeAsync();
                }

                // 7. Event yayınla;
                await _eventBus.PublishAsync(new FullRecoveryCompletedEvent
                {
                    PlanId = plan.PlanId,
                    RecoveryTime = DateTime.UtcNow,
                    StepsExecuted = recoverySteps.Count(s => s.Success),
                    TotalSteps = recoverySteps.Count,
                    PreRecoveryHealth = preRecoveryState.SystemHealth.Score,
                    PostRecoveryHealth = postRecoveryHealth.Score,
                    Duration = DateTime.UtcNow - preRecoveryState.CaptureTime,
                });

                _logger.LogInformation("Full recovery completed successfully. Health improved from {Pre} to {Post}",
                    preRecoveryState.SystemHealth.Score, postRecoveryHealth.Score);

                return new RecoveryResult
                {
                    Success = true,
                    PlanId = plan.PlanId,
                    RecoveryTime = DateTime.UtcNow,
                    StepsExecuted = recoverySteps.Count(s => s.Success),
                    TotalSteps = recoverySteps.Count,
                    PreRecoveryHealth = preRecoveryState.SystemHealth.Score,
                    PostRecoveryHealth = postRecoveryHealth.Score,
                    HealthImprovement = postRecoveryHealth.Score - preRecoveryState.SystemHealth.Score,
                    Duration = DateTime.UtcNow - preRecoveryState.CaptureTime,
                    RecoveryLevel = DetermineRecoveryLevel(postRecoveryHealth.Score)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full recovery failed");

                return new RecoveryResult
                {
                    Success = false,
                    RecoveryTime = DateTime.UtcNow,
                    ErrorMessage = ex.Message,
                    RecoveryLevel = RecoveryLevel.Failed,
                    RequiresManualIntervention = true,
                };
            }
        }

        /// <summary>
        /// Fallback sistem durumunu getir;
        /// </summary>
        public async Task<FallbackSystemStatus> GetSystemStatusAsync()
        {
            var systemHealth = await _healthChecker.GetSystemHealthAsync();
            var activeStrategiesCount = _activeStrategies.Count;

            var status = new FallbackSystemStatus
            {
                IsInitialized = _isInitialized,
                SystemHealth = systemHealth,
                CurrentDegradationLevel = _currentDegradationLevel,
                TimeInDegradedMode = _currentDegradationLevel != DegradationLevel.Normal
                    ? DateTime.UtcNow - _enteredDegradedModeAt
                    : TimeSpan.Zero,
                ActiveStrategiesCount = activeStrategiesCount,
                StrategyEffectivenessCount = _strategyEffectiveness.Count,
                LastStrategyUpdate = await _strategyRepository.GetLastUpdateTimeAsync(),
                EmergencyControllerStatus = await _emergencyController.GetStatusAsync(),
                RecentExecutions = await _strategyRepository.GetRecentExecutionsAsync(TimeSpan.FromHours(1)),
                PerformanceMetrics = await GetFallbackPerformanceMetricsAsync()
            };

            // Strategy effectiveness summary;
            if (_strategyEffectiveness.Any())
            {
                status.AverageEffectiveness = _strategyEffectiveness.Values.Average(s => s.OverallScore);
                status.HighEffectivenessStrategies = _strategyEffectiveness.Values.Count(s => s.OverallScore >= 0.8);
                status.LowEffectivenessStrategies = _strategyEffectiveness.Values.Count(s => s.OverallScore < 0.5);
            }

            return status;
        }

        /// <summary>
        /// Senaryo bazlı fallback planı oluştur;
        /// </summary>
        public async Task<FallbackScenarioPlan> CreateScenarioPlanAsync(FallbackScenario scenario)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Creating fallback plan for scenario: {ScenarioId}", scenario.ScenarioId);

                // 1. Senaryoyu analiz et;
                var scenarioAnalysis = await AnalyzeScenarioAsync(scenario);

                // 2. Potansiyel stratejileri belirle;
                var potentialStrategies = await IdentifyPotentialStrategiesForScenarioAsync(scenarioAnalysis);

                // 3. Strateji kombinasyonlarını oluştur;
                var strategyCombinations = await GenerateStrategyCombinationsAsync(potentialStrategies);

                // 4. En iyi kombinasyonu seç;
                var bestCombination = await SelectBestStrategyCombinationAsync(strategyCombinations, scenarioAnalysis);

                // 5. Detaylı plan oluştur;
                var detailedPlan = await CreateDetailedPlanAsync(bestCombination, scenario);

                // 6. Planı test et (simülasyon)
                var testResult = await TestPlanInSimulationAsync(detailedPlan, scenario);

                // 7. Planı optimize et;
                var optimizedPlan = await OptimizePlanBasedOnTestResultsAsync(detailedPlan, testResult);

                // 8. Plan dokümantasyonu oluştur;
                var documentation = await GeneratePlanDocumentationAsync(optimizedPlan, scenario);

                _logger.LogInformation("Fallback scenario plan created: {PlanId}", optimizedPlan.PlanId);

                return optimizedPlan;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create scenario plan for scenario: {ScenarioId}", scenario.ScenarioId);
                throw new ScenarioPlanCreationException(
                    ErrorCodes.AI.AI_PROCESSING_FAILED,
                    "Failed to create scenario plan",
                    ex);
            }
        }

        /// <summary>
        /// Fallback stratejisini test et;
        /// </summary>
        public async Task<StrategyTestResult> TestStrategyAsync(
            string strategyId,
            TestEnvironment environment)
        {
            ValidateInitialization();

            try
            {
                _logger.LogInformation("Testing strategy: {StrategyId} in environment: {Environment}",
                    strategyId, environment.Name);

                // 1. Test ortamını hazırla;
                await PrepareTestEnvironmentAsync(environment);

                // 2. Test senaryolarını yükle;
                var testScenarios = await LoadTestScenariosForStrategyAsync(strategyId);

                // 3. Testleri çalıştır;
                var testResults = await ExecuteStrategyTestsAsync(strategyId, testScenarios, environment);

                // 4. Sonuçları analiz et;
                var analysis = await AnalyzeTestResultsAsync(testResults);

                // 5. Test ortamını temizle;
                await CleanupTestEnvironmentAsync(environment);

                // 6. Test raporu oluştur;
                var testReport = await GenerateTestReportAsync(strategyId, testResults, analysis);

                _logger.LogInformation("Strategy testing completed: {StrategyId}, Success: {SuccessRate}%",
                    strategyId, analysis.SuccessRate * 100);

                return new StrategyTestResult
                {
                    StrategyId = strategyId,
                    Environment = environment.Name,
                    Success = analysis.OverallSuccess,
                    SuccessRate = analysis.SuccessRate,
                    TestsExecuted = testResults.Count,
                    TestsPassed = testResults.Count(r => r.Passed),
                    TestsFailed = testResults.Count(r => !r.Passed),
                    ExecutionTime = analysis.TotalExecutionTime,
                    TestReport = testReport,
                    Recommendations = analysis.Recommendations,
                    CanBeDeployed = analysis.CanBeDeployed,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to test strategy: {StrategyId}", strategyId);

                return new StrategyTestResult
                {
                    StrategyId = strategyId,
                    Environment = environment.Name,
                    Success = false,
                    ErrorMessage = ex.Message,
                    TestsExecuted = 0,
                    TestsPassed = 0,
                    TestsFailed = 0,
                    CanBeDeployed = false,
                    RequiresManualReview = true,
                };
            }
        }

        #region Private Helper Methods;

        private async Task LoadCoreStrategiesAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Loading core fallback strategies...");

            // 1. Built-in stratejileri yükle;
            var builtInStrategies = await _strategyRepository.LoadBuiltInStrategiesAsync(cancellationToken);
            foreach (var strategy in builtInStrategies)
            {
                _activeStrategies[strategy.StrategyId] = strategy;
            }

            // 2. Dynamic stratejileri yükle;
            var dynamicStrategies = await _strategyRepository.LoadDynamicStrategiesAsync(cancellationToken);
            foreach (var strategy in dynamicStrategies)
            {
                _activeStrategies[strategy.StrategyId] = strategy;
            }

            // 3. Emergency stratejileri yükle;
            var emergencyStrategies = await _strategyRepository.LoadEmergencyStrategiesAsync(cancellationToken);
            foreach (var strategy in emergencyStrategies)
            {
                _activeStrategies[strategy.StrategyId] = strategy;
            }
        }

        private void StartBackgroundTasks()
        {
            // Effectiveness monitoring task;
            _effectivenessMonitoringTask = Task.Run(async () =>
            {
                while (!_backgroundTasksCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await MonitorStrategyEffectivenessAsync(_backgroundTasksCts.Token);
                        await Task.Delay(TimeSpan.FromMinutes(5), _backgroundTasksCts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Effectiveness monitoring task failed");
                    }
                }
            }, _backgroundTasksCts.Token);

            // Strategy optimization task;
            _strategyOptimizationTask = Task.Run(async () =>
            {
                while (!_backgroundTasksCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromHours(1), _backgroundTasksCts.Token);
                        await OptimizeStrategiesBackgroundAsync(_backgroundTasksCts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Strategy optimization task failed");
                    }
                }
            }, _backgroundTasksCts.Token);
        }

        private async Task SubscribeToSystemEventsAsync()
        {
            // System health events;
            await _eventBus.SubscribeAsync<SystemHealthDegradedEvent>(async @event =>
            {
                if (@event.HealthStatus == HealthStatus.Critical)
                {
                    _logger.LogWarning("System health critical, evaluating fallback strategies");

                    var context = new ErrorContext
                    {
                        Component = "System",
                        Operation = "HealthMonitoring",
                        UserId = "System"
                    };

                    var exception = new SystemHealthException(
                        $"System health degraded to critical: {@event.HealthStatus}");

                    var systemState = await CaptureCurrentSystemStateAsync();
                    var strategy = await SelectStrategyAsync(exception, context, systemState);

                    if (strategy != null)
                    {
                        await ExecuteStrategyAsync(strategy, new FallbackExecutionContext
                        {
                            OriginalErrorContext = context,
                            SystemState = systemState,
                            EmergencyLevel = EmergencyLevel.High,
                        });
                    }
                }
            });

            // Performance events;
            await _eventBus.SubscribeAsync<PerformanceThresholdExceededEvent>(async @event =>
            {
                if (@event.Severity == ThresholdSeverity.Critical)
                {
                    _logger.LogWarning("Critical performance threshold exceeded, considering degraded mode");

                    var options = new DegradedModeOptions
                    {
                        Reason = $"Performance threshold exceeded: {@event.MetricName}",
                        ExpectedDuration = TimeSpan.FromHours(1),
                        PreserveCriticalFunctions = true,
                        NotifyUsers = true,
                    };

                    await EnterDegradedModeAsync(DegradationLevel.Moderate, options);
                }
            });
        }

        private async Task<List<FallbackStrategy>> FilterSuitableStrategiesAsync(
            Exception exception,
            ErrorContext context,
            SystemState systemState)
        {
            var errorCode = DetermineErrorCode(exception);
            var errorCategory = ErrorCodes.GetErrorCategory(errorCode);
            var severity = ErrorCodes.GetErrorSeverity(errorCode);

            return _activeStrategies.Values
                .Where(s => IsStrategySuitable(s, errorCode, errorCategory, severity, context, systemState))
                .ToList();
        }

        private bool IsStrategySuitable(
            FallbackStrategy strategy,
            int errorCode,
            ErrorCodes.ErrorCategory errorCategory,
            ErrorCodes.ErrorSeverity severity,
            ErrorContext context,
            SystemState systemState)
        {
            // 1. Error code match;
            if (strategy.ApplicableErrorCodes.Any() &&
                !strategy.ApplicableErrorCodes.Contains(errorCode))
                return false;

            // 2. Error category match;
            if (strategy.ApplicableCategories.Any() &&
                !strategy.ApplicableCategories.Contains(errorCategory))
                return false;

            // 3. Severity match;
            if (strategy.MinimumSeverity > severity)
                return false;

            // 4. Component match;
            if (strategy.ApplicableComponents.Any() &&
                !strategy.ApplicableComponents.Contains(context.Component, StringComparer.OrdinalIgnoreCase))
                return false;

            // 5. System state requirements;
            if (!MeetsSystemRequirements(strategy, systemState))
                return false;

            // 6. Resource availability;
            if (!HasRequiredResources(strategy, systemState))
                return false;

            // 7. Time constraints;
            if (!IsWithinTimeConstraints(strategy))
                return false;

            return true;
        }

        private async Task<List<FallbackStrategy>> EvaluateStrategiesAsync(
            List<FallbackStrategy> strategies,
            Exception exception,
            ErrorContext context,
            SystemState systemState)
        {
            var evaluationTasks = strategies.Select(async strategy =>
            {
                var evaluation = await _strategyEvaluator.EvaluateAsync(strategy, new StrategyEvaluationContext
                {
                    Exception = exception,
                    ErrorContext = context,
                    SystemState = systemState,
                    CurrentDegradationLevel = _currentDegradationLevel,
                    TimeOfDay = DateTime.UtcNow.TimeOfDay,
                });

                strategy.EvaluationScore = evaluation.Score;
                strategy.ConfidenceLevel = evaluation.Confidence;
                strategy.EstimatedRecoveryTime = evaluation.EstimatedRecoveryTime;
                strategy.ResourceImpact = evaluation.ResourceImpact;
                strategy.RiskAssessment = evaluation.RiskAssessment;
                strategy.SelectionReason = evaluation.Recommendation;

                return strategy;
            });

            return (await Task.WhenAll(evaluationTasks)).ToList();
        }

        private async Task<FallbackStrategy> EvaluateStrategyAsync(
            FallbackStrategy strategy,
            Exception exception,
            ErrorContext context,
            SystemState systemState)
        {
            var evaluation = await _strategyEvaluator.EvaluateAsync(strategy, new StrategyEvaluationContext
            {
                Exception = exception,
                ErrorContext = context,
                SystemState = systemState,
                CurrentDegradationLevel = _currentDegradationLevel,
                TimeOfDay = DateTime.UtcNow.TimeOfDay,
            });

            strategy.EvaluationScore = evaluation.Score;
            strategy.ConfidenceLevel = evaluation.Confidence;
            strategy.EstimatedRecoveryTime = evaluation.EstimatedRecoveryTime;
            strategy.ResourceImpact = evaluation.ResourceImpact;
            strategy.RiskAssessment = evaluation.RiskAssessment;
            strategy.SelectionReason = evaluation.Recommendation;

            return strategy;
        }

        private async Task<FallbackStrategy> ChooseBestStrategyAsync(
            List<FallbackStrategy> strategies,
            ErrorContext context)
        {
            if (!strategies.Any())
                return await GetEmergencyFallbackStrategyAsync();

            // Multi-criteria decision making;
            var decisionRequest = new DecisionRequest
            {
                Options = strategies.Select(s => new DecisionOption
                {
                    Id = s.StrategyId,
                    Name = s.Name,
                    Parameters = new Dictionary<string, object>
                    {
                        ["EvaluationScore"] = s.EvaluationScore,
                        ["ConfidenceLevel"] = s.ConfidenceLevel,
                        ["EstimatedRecoveryTime"] = s.EstimatedRecoveryTime.TotalSeconds,
                        ["ResourceImpact"] = (int)s.ResourceImpact,
                        ["RiskLevel"] = (int)s.RiskAssessment.OverallRisk,
                        ["SuccessRate"] = s.HistoricalSuccessRate,
                        ["Complexity"] = (int)s.Complexity
                    }
                }).ToList(),
                Constraints = new DecisionConstraints
                {
                    MaxRecoveryTime = TimeSpan.FromMinutes(5),
                    MaxResourceImpact = ResourceImpact.Medium,
                    MaxRiskLevel = RiskLevel.Medium,
                },
                Context = new DecisionContext
                {
                    Component = context.Component,
                    EmergencyLevel = DetermineEmergencyLevel(context),
                    TimeConstraints = GetTimeConstraints()
                }
            };

            var decision = await _decisionEngine.MakeDecisionAsync(decisionRequest);

            return strategies.First(s => s.StrategyId == decision.SelectedOption);
        }

        private async Task<FallbackStrategy> GetEmergencyFallbackStrategyAsync()
        {
            // Emergency fallback stratejisi;
            return new FallbackStrategy
            {
                StrategyId = "EMERGENCY_FALLBACK",
                Name = "Emergency Fallback",
                Description = "Basic fallback for when no other strategies are suitable",
                Type = StrategyType.Emergency,
                Implementation = new StrategyImplementation
                {
                    Type = ImplementationType.Script,
                    Script = "EmergencyFallback.ps1",
                    Timeout = TimeSpan.FromMinutes(2)
                },
                ApplicableErrorCodes = new List<int>(),
                ApplicableCategories = Enum.GetValues<ErrorCodes.ErrorCategory>().ToList(),
                MinimumSeverity = ErrorCodes.ErrorSeverity.Info,
                Complexity = ComplexityLevel.Low,
                ResourceRequirements = new ResourceRequirements
                {
                    MinimumMemory = 100 * 1024 * 1024, // 100MB,
                    MinimumCpu = 10, // 10%
                    RequiresNetwork = false,
                    RequiresDisk = false,
                }
            };
        }

        private async Task<FallbackExecutionContext> EnhanceExecutionContextAsync(
            FallbackExecutionContext context,
            FallbackStrategy strategy)
        {
            // FIX: context is class, not record => 'with' kullanılamaz.
            var enhancedContext = new FallbackExecutionContext
            {
                OriginalErrorContext = context.OriginalErrorContext,
                EmergencyLevel = context.EmergencyLevel,
                StrategyContext = new StrategyContext
                {
                    StrategyId = strategy.StrategyId,
                    StrategyType = strategy.Type,
                    ExecutionMode = DetermineExecutionMode(strategy, context),
                    Priority = DetermineExecutionPriority(strategy, context),
                    Timeout = strategy.Implementation.Timeout,
                    RetryPolicy = strategy.RetryPolicy,
                },
                SystemState = await UpdateSystemStateAsync(context.SystemState),
                AdditionalData = await GatherAdditionalExecutionDataAsync(strategy, context)
            };

            return enhancedContext;
        }

        private async Task<PreExecutionCheckResult> PerformPreExecutionChecksAsync(
            FallbackStrategy strategy,
            FallbackExecutionContext context)
        {
            var checks = new List<PreExecutionCheck>();

            // 1. Resource availability check;
            var resourceCheck = await CheckResourceAvailabilityAsync(strategy, context.SystemState);
            checks.Add(resourceCheck);

            // 2. Dependency check;
            var dependencyCheck = await CheckDependenciesAsync(strategy);
            checks.Add(dependencyCheck);

            // 3. System state check;
            var systemCheck = await CheckSystemStateAsync(context.SystemState);
            checks.Add(systemCheck);

            // 4. Safety check;
            var safetyCheck = await PerformSafetyCheckAsync(strategy, context);
            checks.Add(safetyCheck);

            // 5. Permission check;
            var permissionCheck = await CheckPermissionsAsync(strategy);
            checks.Add(permissionCheck);

            var result = new PreExecutionCheckResult
            {
                Checks = checks,
                CanExecute = checks.All(c => c.Passed),
                FailureReason = checks.FirstOrDefault(c => !c.Passed)?.FailureReason,
            };

            return result;
        }

        private async Task PerformPostExecutionActionsAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result,
            FallbackExecutionContext context)
        {
            // 1. Cleanup temporary resources;
            if (strategy.CleanupActions != null && strategy.CleanupActions.Any())
            {
                await ExecuteCleanupActionsAsync(strategy.CleanupActions, result, context);
            }

            // 2. Update system configuration if needed;
            if (result.Success && strategy.ConfigurationUpdates != null)
            {
                await ApplyConfigurationUpdatesAsync(strategy.ConfigurationUpdates, context);
            }

            // 3. Update monitoring and alerts;
            await UpdateMonitoringAfterExecutionAsync(strategy, result, context);

            // 4. Notify relevant systems;
            await NotifySystemsOfExecutionResultAsync(strategy, result, context);

            // 5. Archive execution data;
            await ArchiveExecutionDataAsync(strategy, result, context);
        }

        private async Task<StrategyEffectiveness> MeasureStrategyEffectivenessAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result)
        {
            var effectiveness = new StrategyEffectiveness
            {
                StrategyId = strategy.StrategyId,
                CalculationTime = DateTime.UtcNow,
                SuccessRate = result.Success ? 1.0f : 0.0f,
                RecoveryTime = result.EndTime - result.StartTime,
                ResourceUsage = result.Metrics.ResourceUsage,
                ImpactOnSystem = await MeasureSystemImpactAsync(result, strategy),
                UserSatisfaction = await EstimateUserSatisfactionAsync(result, strategy),
                CostEffectiveness = await CalculateCostEffectivenessAsync(strategy, result),
                ReliabilityScore = await CalculateReliabilityScoreAsync(strategy),
                OverallScore = await CalculateOverallEffectivenessScoreAsync(strategy, result)
            };

            effectiveness.Confidence = CalculateConfidenceLevel(effectiveness);
            effectiveness.IsReliable = effectiveness.Confidence >= 0.7f;
            effectiveness.Recommendations = await GenerateEffectivenessRecommendationsAsync(effectiveness);

            return effectiveness;
        }

        private async Task MonitorStrategyEffectivenessAsync(CancellationToken cancellationToken)
        {
            foreach (var strategy in _activeStrategies.Values)
            {
                try
                {
                    var effectiveness = await MeasureEffectivenessAsync(strategy);
                    _strategyEffectiveness[strategy.StrategyId] = effectiveness;

                    // Low effectiveness uyarısı;
                    if (effectiveness.OverallScore < 0.5f && effectiveness.IsReliable)
                    {
                        _logger.LogWarning("Strategy {StrategyId} has low effectiveness: {Score}",
                            strategy.StrategyId, effectiveness.OverallScore);

                        await _eventBus.PublishAsync(new StrategyEffectivenessLowEvent
                        {
                            StrategyId = strategy.StrategyId,
                            EffectivenessScore = effectiveness.OverallScore,
                            Timestamp = DateTime.UtcNow,
                            Recommendations = effectiveness.Recommendations,
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to monitor effectiveness for strategy: {StrategyId}",
                        strategy.StrategyId);
                }
            }
        }

        private async Task OptimizeStrategiesBackgroundAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Son 24 saatteki execution'ları getir;
                var recentResults = await _strategyRepository.GetRecentExecutionResultsAsync(TimeSpan.FromHours(24));

                if (recentResults.Any())
                {
                    await ImproveStrategiesAsync(recentResults);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background strategy optimization failed");
            }
        }

        private async Task PerformHardShutdownAsync()
        {
            _logger.LogCritical("Performing hard shutdown...");

            try
            {
                // Kritik process'leri sonlandır;
                await _systemManager.TerminateCriticalProcessesAsync();

                // Veritabanı bağlantılarını kapat;
                await _systemManager.CloseDatabaseConnectionsAsync();

                // Network bağlantılarını kapat;
                await _systemManager.CloseNetworkConnectionsAsync();

                // File handle'ları serbest bırak;
                await _systemManager.ReleaseFileHandlesAsync();

                _logger.LogCritical("Hard shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Hard shutdown failed");
            }
        }

        private async Task ExitDegradedModeAsync()
        {
            _logger.LogInformation("Exiting degraded mode...");

            try
            {
                // 1. Normal moda geçiş planı oluştur;
                var transitionPlan = await CreateNormalModeTransitionPlanAsync();

                // 2. Planı uygula;
                await ExecuteNormalModeTransitionAsync(transitionPlan);

                // 3. Sistem davranışını normale döndür;
                await RestoreNormalSystemBehaviorAsync();

                // 4. Monitoring'i normale döndür;
                await RestoreNormalMonitoringAsync();

                // 5. Kullanıcıları bilgilendir;
                await NotifyUsersOfNormalModeRestorationAsync();

                // 6. Durumu güncelle;
                _currentDegradationLevel = DegradationLevel.Normal;

                // 7. Event yayınla;
                await _eventBus.PublishAsync(new DegradedModeExitedEvent
                {
                    ExitedAt = DateTime.UtcNow,
                    PreviousLevel = _currentDegradationLevel,
                    TimeInDegradedMode = DateTime.UtcNow - _enteredDegradedModeAt,
                });

                _logger.LogInformation("Successfully exited degraded mode");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to exit degraded mode");
                throw;
            }
        }

        private int DetermineErrorCode(Exception exception)
        {
            return exception switch
            {
                NedaException nedaEx => nedaEx.ErrorCode,
                _ => ErrorCodes.System.RUNTIME_EXCEPTION
            };
        }

        private bool MeetsSystemRequirements(FallbackStrategy strategy, SystemState systemState)
        {
            // Memory check;
            if (strategy.ResourceRequirements.MinimumMemory > 0 &&
                systemState.MemoryUsagePercent > (100 - (strategy.ResourceRequirements.MinimumMemory / (1024 * 1024))))
                return false;

            // CPU check;
            if (strategy.ResourceRequirements.MinimumCpu > 0 &&
                systemState.CpuUsage > (100 - strategy.ResourceRequirements.MinimumCpu))
                return false;

            // Network requirement;
            if (strategy.ResourceRequirements.RequiresNetwork && !systemState.NetworkInfo.IsConnected)
                return false;

            // Disk requirement;
            if (strategy.ResourceRequirements.RequiresDisk && systemState.DiskInfo.FreeSpace < strategy.ResourceRequirements.MinimumDiskSpace)
                return false;

            return true;
        }

        private bool HasRequiredResources(FallbackStrategy strategy, SystemState systemState)
        {
            // Implementation-specific resource check;
            // Burada strategy'ye özel resource kontrolleri yapılabilir;
            return true;
        }

        private bool IsWithinTimeConstraints(FallbackStrategy strategy)
        {
            // Time window check;
            if (strategy.TimeConstraints != null)
            {
                var currentTime = DateTime.UtcNow.TimeOfDay;
                if (strategy.TimeConstraints.StartTime > strategy.TimeConstraints.EndTime)
                {
                    // Overnight window;
                    if (currentTime < strategy.TimeConstraints.StartTime && currentTime > strategy.TimeConstraints.EndTime)
                        return false;
                }
                else
                {
                    // Normal window;
                    if (currentTime < strategy.TimeConstraints.StartTime || currentTime > strategy.TimeConstraints.EndTime)
                        return false;
                }
            }

            return true;
        }

        private EmergencyLevel DetermineEmergencyLevel(ErrorContext context)
        {
            // Context'e göre emergency level belirle;
            return EmergencyLevel.Medium; // Default;
        }

        private TimeConstraints GetTimeConstraints()
        {
            return new TimeConstraints
            {
                IsBusinessHours = IsBusinessHours(),
                IsPeakHours = IsPeakHours(),
                MaintenanceWindow = IsWithinMaintenanceWindow()
            };
        }

        private bool IsBusinessHours()
        {
            var now = DateTime.UtcNow;
            return now.DayOfWeek >= DayOfWeek.Monday && now.DayOfWeek <= DayOfWeek.Friday &&
                   now.Hour >= 9 && now.Hour < 17;
        }

        private bool IsPeakHours()
        {
            var now = DateTime.UtcNow;
            return (now.Hour >= 10 && now.Hour < 12) || (now.Hour >= 14 && now.Hour < 16);
        }

        private bool IsWithinMaintenanceWindow()
        {
            // Haftasonu gece maintenance window'u;
            var now = DateTime.UtcNow;
            return (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday) &&
                   now.Hour >= 1 && now.Hour < 5;
        }

        private ExecutionMode DetermineExecutionMode(FallbackStrategy strategy, FallbackExecutionContext context)
        {
            if (context.EmergencyLevel == EmergencyLevel.Critical)
                return ExecutionMode.Emergency;

            if (_currentDegradationLevel != DegradationLevel.Normal)
                return ExecutionMode.Degraded;

            return ExecutionMode.Normal;
        }

        private ExecutionPriority DetermineExecutionPriority(FallbackStrategy strategy, FallbackExecutionContext context)
        {
            if (strategy.Type == StrategyType.Emergency)
                return ExecutionPriority.Critical;

            if (context.EmergencyLevel == EmergencyLevel.High || context.EmergencyLevel == EmergencyLevel.Critical)
                return ExecutionPriority.High;

            return ExecutionPriority.Normal;
        }

        private StabilizationLevel DetermineStabilizationLevel(double improvement)
        {
            return improvement switch
            {
                > 30 => StabilizationLevel.Excellent,
                > 20 => StabilizationLevel.Good,
                > 10 => StabilizationLevel.Moderate,
                > 0 => StabilizationLevel.Minimal,
                _ => StabilizationLevel.Failed
            };
        }

        private RecoveryLevel DetermineRecoveryLevel(double healthScore)
        {
            return healthScore switch
            {
                >= 90 => RecoveryLevel.Complete,
                >= 75 => RecoveryLevel.Substantial,
                >= 60 => RecoveryLevel.Partial,
                >= 40 => RecoveryLevel.Minimal,
                _ => RecoveryLevel.Failed
            };
        }

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new FallbackInitializationException(
                    ErrorCodes.System.SYSTEM_NOT_INITIALIZED,
                    "FallbackStrategies is not initialized. Call InitializeAsync first.");
            }
        }

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                _backgroundTasksCts.Cancel();
                _backgroundTasksCts.Dispose();

                _executionLock.Dispose();

                // Not: Bu tipler IDisposable değilse, bu satırlar projende ayrıca hata verebilir.
                _strategyRepository?.Dispose();
                _strategyEvaluator?.Dispose();
                _strategyExecutor?.Dispose();
                _strategyLearner?.Dispose();
                _emergencyController?.Dispose();
            }

            _isDisposed = true;
        }

        #endregion;

        #region Placeholder Methods for Future Implementation;

        private async Task<SystemState> CaptureCurrentSystemStateAsync()
        {
            // Sistem durumu yakalama;
            return new SystemState();
        }

        private async Task<SystemState> UpdateSystemStateAsync(SystemState currentState)
        {
            // Sistem durumunu güncelle;
            return currentState;
        }

        private async Task<Dictionary<string, object>> GatherAdditionalExecutionDataAsync(
            FallbackStrategy strategy,
            FallbackExecutionContext context)
        {
            // Ek execution verisi topla;
            return new Dictionary<string, object>();
        }

        private async Task<PreExecutionCheck> CheckResourceAvailabilityAsync(
            FallbackStrategy strategy,
            SystemState systemState)
        {
            // Resource availability check;
            return new PreExecutionCheck
            {
                CheckName = "ResourceAvailability",
                Passed = true,
            };
        }

        private async Task<PreExecutionCheck> CheckDependenciesAsync(FallbackStrategy strategy)
        {
            // Dependency check;
            return new PreExecutionCheck
            {
                CheckName = "Dependencies",
                Passed = true,
            };
        }

        private async Task<PreExecutionCheck> CheckSystemStateAsync(SystemState systemState)
        {
            // System state check;
            return new PreExecutionCheck
            {
                CheckName = "SystemState",
                Passed = true,
            };
        }

        private async Task<PreExecutionCheck> PerformSafetyCheckAsync(
            FallbackStrategy strategy,
            FallbackExecutionContext context)
        {
            // Safety check;
            return new PreExecutionCheck
            {
                CheckName = "Safety",
                Passed = true,
            };
        }

        private async Task<PreExecutionCheck> CheckPermissionsAsync(FallbackStrategy strategy)
        {
            // Permission check;
            return new PreExecutionCheck
            {
                CheckName = "Permissions",
                Passed = true,
            };
        }

        private async Task ExecuteCleanupActionsAsync(
            List<CleanupAction> cleanupActions,
            FallbackExecutionResult result,
            FallbackExecutionContext context)
        {
            // Cleanup actions;
        }

        private async Task ApplyConfigurationUpdatesAsync(
            List<ConfigurationUpdate> configurationUpdates,
            FallbackExecutionContext context)
        {
            // Configuration updates;
        }

        private async Task UpdateMonitoringAfterExecutionAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result,
            FallbackExecutionContext context)
        {
            // Update monitoring;
        }

        private async Task NotifySystemsOfExecutionResultAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result,
            FallbackExecutionContext context)
        {
            // Notify systems;
        }

        private async Task ArchiveExecutionDataAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result,
            FallbackExecutionContext context)
        {
            // Archive data;
        }

        private async Task<double> MeasureSystemImpactAsync(
            FallbackExecutionResult result,
            FallbackStrategy strategy)
        {
            // System impact measurement;
            return 0.5;
        }

        private async Task<double> EstimateUserSatisfactionAsync(
            FallbackExecutionResult result,
            FallbackStrategy strategy)
        {
            // User satisfaction estimation;
            return 0.5;
        }

        private async Task<double> CalculateCostEffectivenessAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result)
        {
            // Cost effectiveness calculation;
            return 0.5;
        }

        private async Task<double> CalculateReliabilityScoreAsync(FallbackStrategy strategy)
        {
            // Reliability score calculation;
            return 0.5;
        }

        private async Task<double> CalculateOverallEffectivenessScoreAsync(
            FallbackStrategy strategy,
            FallbackExecutionResult result)
        {
            // Overall effectiveness calculation;
            return 0.5;
        }

        private float CalculateConfidenceLevel(StrategyEffectiveness effectiveness)
        {
            // Confidence level calculation;
            return 0.8f;
        }

        private async Task<List<EffectivenessRecommendation>> GenerateEffectivenessRecommendationsAsync(
            StrategyEffectiveness effectiveness)
        {
            // Effectiveness recommendations;
            return new List<EffectivenessRecommendation>();
        }

        private async Task<HistoricalResultAnalysis> AnalyzeHistoricalResultsAsync(
            List<FallbackExecutionResult> historicalResults)
        {
            // Historical results analysis;
            return new HistoricalResultAnalysis();
        }

        private async Task<List<FallbackStrategy>> GenerateNewStrategiesAsync(
            HistoricalResultAnalysis analysis,
            StrategyOptimizationResult optimizationResult)
        {
            // New strategy generation;
            return new List<FallbackStrategy>();
        }

        private async Task<List<StrategyTestResult>> TestNewStrategiesAsync(List<FallbackStrategy> newStrategies)
        {
            // New strategy testing;
            return new List<StrategyTestResult>();
        }

        private async Task<List<FallbackStrategy>> DeploySuccessfulStrategiesAsync(List<StrategyTestResult> testResults)
        {
            // Strategy deployment;
            return new List<FallbackStrategy>();
        }

        private async Task ArchiveIneffectiveStrategiesAsync(HistoricalResultAnalysis analysis)
        {
            // Archive ineffective strategies;
        }

        private async Task<SystemStabilityAnalysis> AnalyzeSystemStabilityAsync(SystemState currentState)
        {
            // System stability analysis;
            return new SystemStabilityAnalysis();
        }

        private async Task<List<InstabilityCause>> IdentifyInstabilityCausesAsync(SystemStabilityAnalysis analysis)
        {
            // Instability cause identification;
            return new List<InstabilityCause>();
        }

        private async Task<StabilizationPlan> CreateStabilizationPlanAsync(
            SystemStabilityAnalysis analysis,
            List<InstabilityCause> causes,
            StabilizationOptions options)
        {
            // Stabilization plan creation;
            return new StabilizationPlan();
        }

        private async Task<StabilizationExecutionResult> ExecuteStabilizationPlanAsync(StabilizationPlan plan)
        {
            // Stabilization plan execution;
            return new StabilizationExecutionResult();
        }

        private async Task<List<StabilizationRecommendation>> GenerateStabilizationRecommendationsAsync(
            SystemStabilityAnalysis analysis,
            SystemHealth finalState)
        {
            // Stabilization recommendations;
            return new List<StabilizationRecommendation>();
        }

        private async Task<PreDegradationState> CapturePreDegradationStateAsync()
        {
            // Pre-degradation state capture;
            return new PreDegradationState();
        }

        private async Task<DegradationPlan> CreateDegradationPlanAsync(
            DegradationLevel level,
            DegradedModeOptions options)
        {
            // Degradation plan creation;
            return new DegradationPlan();
        }

        private async Task<DegradationExecutionResult> ExecuteDegradationPlanAsync(DegradationPlan plan)
        {
            // Degradation plan execution;
            return new DegradationExecutionResult();
        }

        private async Task AdjustSystemBehaviorForDegradedModeAsync(
            DegradationLevel level,
            DegradedModeOptions options)
        {
            // System behavior adjustment;
        }

        private async Task NotifyUsersOfDegradedModeAsync(
            DegradationLevel level,
            DegradedModeOptions options)
        {
            // User notification;
        }

        private async Task UpdateMonitoringForDegradedModeAsync(DegradationLevel level)
        {
            // Monitoring update;
        }

        private async Task<PreRecoveryState> CapturePreRecoveryStateAsync()
        {
            // Pre-recovery state capture;
            return new PreRecoveryState();
        }

        private async Task<RecoveryPlanValidationResult> ValidateRecoveryPlanAsync(RecoveryPlan plan)
        {
            // Recovery plan validation;
            return new RecoveryPlanValidationResult
            {
                IsValid = true,
            };
        }

        private async Task<List<RecoveryStepResult>> ExecuteRecoveryStepsAsync(RecoveryPlan plan)
        {
            // Recovery step execution;
            return new List<RecoveryStepResult>();
        }

        private async Task PerformPostRecoveryOptimizationAsync(RecoveryPlan plan, SystemHealth postRecoveryHealth)
        {
            // Post-recovery optimization;
        }

        private async Task<NormalModeTransitionPlan> CreateNormalModeTransitionPlanAsync()
        {
            // Normal mode transition plan;
            return new NormalModeTransitionPlan();
        }

        private async Task ExecuteNormalModeTransitionAsync(NormalModeTransitionPlan plan)
        {
            // Normal mode transition execution;
        }

        private async Task RestoreNormalSystemBehaviorAsync()
        {
            // Restore normal behavior;
        }

        private async Task RestoreNormalMonitoringAsync()
        {
            // Restore normal monitoring;
        }

        private async Task NotifyUsersOfNormalModeRestorationAsync()
        {
            // Notify users;
        }

        private async Task<FallbackPerformanceMetrics> GetFallbackPerformanceMetricsAsync()
        {
            // Performance metrics;
            return new FallbackPerformanceMetrics();
        }

        private async Task<ScenarioAnalysis> AnalyzeScenarioAsync(FallbackScenario scenario)
        {
            // Scenario analysis;
            return new ScenarioAnalysis();
        }

        private async Task<List<FallbackStrategy>> IdentifyPotentialStrategiesForScenarioAsync(ScenarioAnalysis analysis)
        {
            // Potential strategy identification;
            return new List<FallbackStrategy>();
        }

        private async Task<List<StrategyCombination>> GenerateStrategyCombinationsAsync(
            List<FallbackStrategy> potentialStrategies)
        {
            // Strategy combination generation;
            return new List<StrategyCombination>();
        }

        private async Task<StrategyCombination> SelectBestStrategyCombinationAsync(
            List<StrategyCombination> combinations,
            ScenarioAnalysis analysis)
        {
            // Best combination selection;
            return combinations.First();
        }

        private async Task<FallbackScenarioPlan> CreateDetailedPlanAsync(
            StrategyCombination combination,
            FallbackScenario scenario)
        {
            // Detailed plan creation;
            return new FallbackScenarioPlan();
        }

        private async Task<PlanTestResult> TestPlanInSimulationAsync(
            FallbackScenarioPlan plan,
            FallbackScenario scenario)
        {
            // Plan testing in simulation;
            return new PlanTestResult();
        }

        private async Task<FallbackScenarioPlan> OptimizePlanBasedOnTestResultsAsync(
            FallbackScenarioPlan plan,
            PlanTestResult testResult)
        {
            // Plan optimization;
            return plan;
        }

        private async Task<PlanDocumentation> GeneratePlanDocumentationAsync(
            FallbackScenarioPlan plan,
            FallbackScenario scenario)
        {
            // Plan documentation;
            return new PlanDocumentation();
        }

        private async Task PrepareTestEnvironmentAsync(TestEnvironment environment)
        {
            // Test environment preparation;
        }

        private async Task<List<TestScenario>> LoadTestScenariosForStrategyAsync(string strategyId)
        {
            // Test scenario loading;
            return new List<TestScenario>();
        }

        private async Task<List<TestExecutionResult>> ExecuteStrategyTestsAsync(
            string strategyId,
            List<TestScenario> testScenarios,
            TestEnvironment environment)
        {
            // Strategy test execution;
            return new List<TestExecutionResult>();
        }

        private async Task<TestResultAnalysis> AnalyzeTestResultsAsync(List<TestExecutionResult> testResults)
        {
            // Test result analysis;
            return new TestResultAnalysis();
        }

        private async Task CleanupTestEnvironmentAsync(TestEnvironment environment)
        {
            // Test environment cleanup;
        }

        private async Task<TestReport> GenerateTestReportAsync(
            string strategyId,
            List<TestExecutionResult> testResults,
            TestResultAnalysis analysis)
        {
            // Test report generation;
            return new TestReport();
        }

        // PLACEHOLDER: senin kodda vardı ama tanımı burada yoktu (compile için gerekli)
        private async Task<StrategyEffectiveness> CalculateEffectivenessMetricsAsync(FallbackStrategy strategy, List<FallbackExecutionResult> historicalResults)
        {
            return new StrategyEffectiveness { StrategyId = strategy.StrategyId, CalculationTime = DateTime.UtcNow };
        }

        private async Task<object> GenerateEffectivenessReportAsync(FallbackStrategy strategy, StrategyEffectiveness effectiveness)
        {
            return new object();
        }

        #endregion;

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Fallback stratejisi;
    /// </summary>
    public class FallbackStrategy
    {
        public string StrategyId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public StrategyType Type { get; set; }
        public StrategyImplementation Implementation { get; set; } = new();
        public List<int> ApplicableErrorCodes { get; set; } = new();
        public List<ErrorCodes.ErrorCategory> ApplicableCategories { get; set; } = new();
        public ErrorCodes.ErrorSeverity MinimumSeverity { get; set; }
        public List<string> ApplicableComponents { get; set; } = new();
        public ComplexityLevel Complexity { get; set; }
        public ResourceRequirements ResourceRequirements { get; set; } = new();
        public TimeConstraints? TimeConstraints { get; set; }
        public RetryPolicy? RetryPolicy { get; set; }
        public List<CleanupAction> CleanupActions { get; set; } = new();
        public List<ConfigurationUpdate> ConfigurationUpdates { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();

        // Evaluation results (runtime)
        public double EvaluationScore { get; set; }
        public float ConfidenceLevel { get; set; }
        public TimeSpan EstimatedRecoveryTime { get; set; }
        public ResourceImpact ResourceImpact { get; set; }
        public RiskAssessment RiskAssessment { get; set; } = new();
        public string? SelectionReason { get; set; }
        public double HistoricalSuccessRate { get; set; }
        public int HistoricalExecutionCount { get; set; }
    }

    /// <summary>
    /// Strateji tipi;
    /// </summary>
    public enum StrategyType
    {
        GracefulDegradation,
        Failover,
        Retry,
        CircuitBreaker,
        Bulkhead,
        CacheFallback,
        DefaultResponse,
        Emergency,
        Custom
    }

    /// <summary>
    /// Strateji implementasyonu;
    /// </summary>
    public class StrategyImplementation
    {
        public ImplementationType Type { get; set; }
        public string Script { get; set; } = string.Empty;
        public string Assembly { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public List<string> Dependencies { get; set; } = new();
    }

    /// <summary>
    /// Implementasyon tipi;
    /// </summary>
    public enum ImplementationType
    {
        Script,
        Assembly,
        REST,
        gRPC,
        Database,
        FileSystem
    }

    /// <summary>
    /// Resource gereksinimleri;
    /// </summary>
    public class ResourceRequirements
    {
        public long MinimumMemory { get; set; } // bytes;
        public int MinimumCpu { get; set; } // percentage;
        public long MinimumDiskSpace { get; set; } // bytes;
        public bool RequiresNetwork { get; set; }
        public bool RequiresDisk { get; set; }
        public List<string> RequiredServices { get; set; } = new();
        public List<string> RequiredProcesses { get; set; } = new();
    }

    /// <summary>
    /// Zaman kısıtlamaları;
    /// </summary>
    public class TimeConstraints
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public List<DayOfWeek> AllowedDays { get; set; } = new();
        public bool AllowHolidays { get; set; }

        // Bu alanlar GetTimeConstraints() içinde kullanılmıştı
        public bool IsBusinessHours { get; set; }
        public bool IsPeakHours { get; set; }
        public bool MaintenanceWindow { get; set; }
    }

    /// <summary>
    /// Retry politikası;
    /// </summary>
    public class RetryPolicy
    {
        public int MaxRetries { get; set; } = 3;
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
        public double BackoffMultiplier { get; set; } = 2.0;
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
        public List<Type> RetryableExceptions { get; set; } = new();
    }

    /// <summary>
    /// Kompleksite seviyesi;
    /// </summary>
    public enum ComplexityLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Resource impact;
    /// </summary>
    public enum ResourceImpact
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Risk değerlendirmesi;
    /// </summary>
    public class RiskAssessment
    {
        public RiskLevel OverallRisk { get; set; }
        public double DataLossRisk { get; set; }
        public double SystemStabilityRisk { get; set; }
        public double SecurityRisk { get; set; }
        public double PerformanceRisk { get; set; }
        public List<RiskMitigation> Mitigations { get; set; } = new();
    }

    /// <summary>
    /// Risk seviyesi;
    /// </summary>
    public enum RiskLevel
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Risk mitigasyonu;
    /// </summary>
    public class RiskMitigation
    {
        public string Description { get; set; } = string.Empty;
        public MitigationType Type { get; set; }
        public EffectivenessLevel Effectiveness { get; set; }
    }

    /// <summary>
    /// Mitigasyon tipi;
    /// </summary>
    public enum MitigationType
    {
        Prevention,
        Detection,
        Response,
        Recovery
    }

    /// <summary>
    /// Etkinlik seviyesi;
    /// </summary>
    public enum EffectivenessLevel
    {
        Low,
        Medium,
        High,
        Complete
    }

    /// <summary>
    /// Fallback execution context;
    /// </summary>
    public class FallbackExecutionContext
    {
        public ErrorContext OriginalErrorContext { get; set; } = new();
        public SystemState SystemState { get; set; } = new();
        public EmergencyLevel EmergencyLevel { get; set; }
        public StrategyContext StrategyContext { get; set; } = new();
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    /// <summary>
    /// Strateji context'i;
    /// </summary>
    public class StrategyContext
    {
        public string StrategyId { get; set; } = string.Empty;
        public StrategyType StrategyType { get; set; }
        public ExecutionMode ExecutionMode { get; set; }
        public ExecutionPriority Priority { get; set; }
        public TimeSpan Timeout { get; set; }
        public RetryPolicy? RetryPolicy { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    /// <summary>
    /// Emergency level;
    /// </summary>
    public enum EmergencyLevel
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Execution modu;
    /// </summary>
    public enum ExecutionMode
    {
        Normal,
        Degraded,
        Emergency
    }

    /// <summary>
    /// Execution önceliği;
    /// </summary>
    public enum ExecutionPriority
    {
        Low,
        Normal,
        High,
        Critical
    }

    /// <summary>
    /// Fallback execution sonucu;
    /// </summary>
    public class FallbackExecutionResult
    {
        public string ExecutionId { get; set; } = Guid.NewGuid().ToString();
        public string StrategyId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public FallbackOutcome Outcome { get; set; }
        public string? ErrorMessage { get; set; }
        public Exception? Exception { get; set; }
        public ExecutionMetrics Metrics { get; set; } = new();
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    /// <summary>
    /// Fallback outcome;
    /// </summary>
    public enum FallbackOutcome
    {
        Success,
        PartialSuccess,
        Failed,
        FailedPreCheck,
        Timeout,
        ResourceExhausted,
        ExecutionError,
        RolledBack
    }

    /// <summary>
    /// Execution metrikleri;
    /// </summary>
    public class ExecutionMetrics
    {
        public TimeSpan ExecutionTime { get; set; }
        public ResourceUsage ResourceUsage { get; set; } = new();
        public int RetryCount { get; set; }
        public List<ExecutionStep> Steps { get; set; } = new();
        public Dictionary<string, object> CustomMetrics { get; set; } = new();
    }

    /// <summary>
    /// Resource kullanımı;
    /// </summary>
    public class ResourceUsage
    {
        public long MemoryUsed { get; set; }
        public double CpuUsed { get; set; }
        public long DiskRead { get; set; }
        public long DiskWrite { get; set; }
        public long NetworkSent { get; set; }
        public long NetworkReceived { get; set; }
    }

    /// <summary>
    /// Execution step;
    /// </summary>
    public class ExecutionStep
    {
        public string Name { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Strateji etkinliği;
    /// </summary>
    public class StrategyEffectiveness
    {
        public string StrategyId { get; set; } = string.Empty;
        public DateTime CalculationTime { get; set; }
        public float SuccessRate { get; set; }
        public TimeSpan RecoveryTime { get; set; }
        public ResourceUsage ResourceUsage { get; set; } = new();
        public double ImpactOnSystem { get; set; }
        public double UserSatisfaction { get; set; }
        public double CostEffectiveness { get; set; }
        public double ReliabilityScore { get; set; }
        public double OverallScore { get; set; }
        public float Confidence { get; set; }
        public bool IsReliable { get; set; }
        public List<EffectivenessRecommendation> Recommendations { get; set; } = new();
    }

    /// <summary>
    /// Etkinlik önerisi;
    /// </summary>
    public class EffectivenessRecommendation
    {
        public string Description { get; set; } = string.Empty;
        public RecommendationType Type { get; set; }
        public PriorityLevel Priority { get; set; }
        public double ExpectedImprovement { get; set; }
    }

    /// <summary>
    /// Öneri tipi;
    /// </summary>
    public enum RecommendationType
    {
        Optimization,
        Replacement,
        Enhancement,
        Deprecation,
        Testing
    }

    /// <summary>
    /// Öncelik seviyesi;
    /// </summary>
    public enum PriorityLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Learning sonucu;
    /// </summary>
    public class LearningResult
    {
        public bool Success { get; set; }
        public DateTime LearningTime { get; set; }
        public int OptimizedStrategies { get; set; }
        public int NewStrategies { get; set; }
        public int DeployedStrategies { get; set; }
        public int ArchivedStrategies { get; set; }
        public string? ErrorMessage { get; set; }
        public List<StrategyImprovement> Improvements { get; set; } = new();
    }

    /// <summary>
    /// Strateji iyileştirmesi;
    /// </summary>
    public class StrategyImprovement
    {
        public string StrategyId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double ImprovementPercentage { get; set; }
        public List<string> Changes { get; set; } = new();
    }

    /// <summary>
    /// Emergency condition;
    /// </summary>
    public class EmergencyCondition
    {
        public EmergencyType Type { get; set; }
        public EmergencySeverity SeverityLevel { get; set; }
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Emergency tipi;
    /// </summary>
    public enum EmergencyType
    {
        SystemFailure,
        SecurityBreach,
        DataCorruption,
        ResourceExhaustion,
        NetworkOutage,
        HardwareFailure,
        SoftwareBug,
        ExternalAttack
    }

    /// <summary>
    /// Emergency şiddeti;
    /// </summary>
    public enum EmergencySeverity
    {
        Warning,
        Severe,
        Critical,
        Catastrophic
    }

    /// <summary>
    /// Emergency shutdown sonucu;
    /// </summary>
    public class EmergencyShutdownResult
    {
        public bool Success { get; set; }
        public DateTime ShutdownTime { get; set; }
        public EmergencyCondition Condition { get; set; } = new();
        public int SystemsShutdown { get; set; }
        public bool DataPreserved { get; set; }
        public string? SystemSnapshotId { get; set; }
        public EmergencyState FinalState { get; set; }
        public string? ErrorMessage { get; set; }
        public bool RequiresManualIntervention { get; set; }
    }

    /// <summary>
    /// Emergency durumu;
    /// </summary>
    public enum EmergencyState
    {
        Initiated,
        InProgress,
        ShutdownComplete,
        ShutdownFailed,
        RecoveryInitiated
    }

    /// <summary>
    /// Stabilization options;
    /// </summary>
    public class StabilizationOptions
    {
        public bool AggressiveStabilization { get; set; }
        public bool PreserveData { get; set; } = true;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
        public List<string> CriticalServices { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    /// <summary>
    /// Stabilization sonucu;
    /// </summary>
    public class StabilizationResult
    {
        public bool Success { get; set; }
        public double InitialHealthScore { get; set; }
        public double FinalHealthScore { get; set; }
        public double Improvement { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> ActionsTaken { get; set; } = new();
        public StabilizationLevel StabilizationLevel { get; set; }
        public List<StabilizationRecommendation> Recommendations { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public bool RequiresManualIntervention { get; set; }
    }

    public enum StabilizationLevel
    {
        Failed,
        Minimal,
        Moderate,
        Good,
        Excellent
    }

    public enum DegradationLevel
    {
        Normal,
        Minimal,
        Moderate,
        Severe,
        Critical
    }

    public class DegradedModeOptions
    {
        public string Reason { get; set; } = string.Empty;
        public TimeSpan ExpectedDuration { get; set; }
        public bool PreserveCriticalFunctions { get; set; } = true;
        public bool NotifyUsers { get; set; } = true;
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class DegradedModeResult
    {
        public bool Success { get; set; }
        public DegradationLevel Level { get; set; }
        public DateTime EnteredAt { get; set; }
        public DegradationLevel PreviousLevel { get; set; }
        public int ServicesReduced { get; set; }
        public PerformanceLevel PerformanceLevel { get; set; }
        public TimeSpan ExpectedRecoveryTime { get; set; }
        public bool MonitoringAdjusted { get; set; }
        public string? ErrorMessage { get; set; }
        public bool RequiresManualIntervention { get; set; }
    }

    public enum PerformanceLevel
    {
        Normal,
        Reduced,
        Minimal,
        Emergency
    }

    public class RecoveryPlan
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RecoveryType Type { get; set; }
        public List<RecoveryStep> Steps { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public TimeSpan EstimatedDuration { get; set; }
        public List<string> Dependencies { get; set; } = new();
    }

    public enum RecoveryType
    {
        Full,
        Partial,
        Incremental,
        Emergency
    }

    public class RecoveryResult
    {
        public bool Success { get; set; }
        public string PlanId { get; set; } = string.Empty;
        public DateTime RecoveryTime { get; set; }
        public int StepsExecuted { get; set; }
        public int TotalSteps { get; set; }
        public double PreRecoveryHealth { get; set; }
        public double PostRecoveryHealth { get; set; }
        public double HealthImprovement { get; set; }
        public TimeSpan Duration { get; set; }
        public RecoveryLevel RecoveryLevel { get; set; }
        public string? ErrorMessage { get; set; }
        public bool RequiresManualIntervention { get; set; }
    }

    public enum RecoveryLevel
    {
        Failed,
        Minimal,
        Partial,
        Substantial,
        Complete
    }

    public class FallbackSystemStatus
    {
        public bool IsInitialized { get; set; }
        public SystemHealth SystemHealth { get; set; } = new();
        public DegradationLevel CurrentDegradationLevel { get; set; }
        public TimeSpan TimeInDegradedMode { get; set; }
        public int ActiveStrategiesCount { get; set; }
        public int StrategyEffectivenessCount { get; set; }
        public double AverageEffectiveness { get; set; }
        public int HighEffectivenessStrategies { get; set; }
        public int LowEffectivenessStrategies { get; set; }
        public DateTime LastStrategyUpdate { get; set; }
        public EmergencyControllerStatus EmergencyControllerStatus { get; set; } = new();
        public List<FallbackExecutionResult> RecentExecutions { get; set; } = new();
        public FallbackPerformanceMetrics PerformanceMetrics { get; set; } = new();
    }

    public class FallbackScenario
    {
        public string ScenarioId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ScenarioType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public List<string> AffectedComponents { get; set; } = new();
        public SeverityLevel Severity { get; set; }
        public ProbabilityLevel Probability { get; set; }
    }

    public enum ScenarioType
    {
        SystemFailure,
        NetworkOutage,
        DatabaseFailure,
        ServiceFailure,
        SecurityIncident,
        ResourceExhaustion,
        DataCorruption,
        ExternalDependencyFailure
    }

    public enum SeverityLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    public enum ProbabilityLevel
    {
        Rare,
        Unlikely,
        Possible,
        Likely,
        Frequent
    }

    public class FallbackScenarioPlan
    {
        public string PlanId { get; set; } = Guid.NewGuid().ToString();
        public string ScenarioId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<FallbackStrategy> Strategies { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public TimeSpan EstimatedExecutionTime { get; set; }
        public double SuccessProbability { get; set; }
        public List<string> Dependencies { get; set; } = new();
        public PlanDocumentation Documentation { get; set; } = new();
    }

    public class TestEnvironment
    {
        public string Name { get; set; } = string.Empty;
        public EnvironmentType Type { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new();
        public List<string> AvailableServices { get; set; } = new();
        public ResourceLimits ResourceLimits { get; set; } = new();
    }

    public enum EnvironmentType
    {
        Development,
        Staging,
        ProductionClone,
        Simulation,
        Isolated
    }

    public class StrategyTestResult
    {
        public string StrategyId { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public bool Success { get; set; }
        public double SuccessRate { get; set; }
        public int TestsExecuted { get; set; }
        public int TestsPassed { get; set; }
        public int TestsFailed { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public TestReport TestReport { get; set; } = new();
        public List<TestRecommendation> Recommendations { get; set; } = new();
        public bool CanBeDeployed { get; set; }
        public string? ErrorMessage { get; set; }
        public bool RequiresManualReview { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class FallbackInitializationException : NedaException
    {
        public FallbackInitializationException(int errorCode, string message)
            : base(errorCode, message) { }

        public FallbackInitializationException(int errorCode, string message, Exception inner)
            : base(errorCode, message, inner) { }
    }

    public class RecoveryValidationException : Exception
    {
        public RecoveryValidationException(string message) : base(message) { }
        public RecoveryValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ScenarioPlanCreationException : NedaException
    {
        public ScenarioPlanCreationException(int errorCode, string message)
            : base(errorCode, message) { }

        public ScenarioPlanCreationException(int errorCode, string message, Exception inner)
            : base(errorCode, message, inner) { }
    }

    #endregion;

    #region Events;

    public class FallbackStrategySelectedEvent : IEvent
    {
        public string StrategyId { get; set; } = string.Empty;
        public string StrategyName { get; set; } = string.Empty;
        public int ErrorCode { get; set; }
        public string Component { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string? SelectionReason { get; set; }
    }

    public class FallbackStrategyExecutedEvent : IEvent
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public FallbackOutcome Outcome { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime Timestamp { get; set; }
        public string? ErrorMessage { get; set; }
        public string Component { get; set; } = string.Empty;
    }

    public class StrategyEffectivenessLowEvent : IEvent
    {
        public string StrategyId { get; set; } = string.Empty;
        public double EffectivenessScore { get; set; }
        public DateTime Timestamp { get; set; }
        public List<EffectivenessRecommendation> Recommendations { get; set; } = new();
    }

    public class EmergencyShutdownExecutedEvent : IEvent
    {
        public EmergencyCondition Condition { get; set; } = new();
        public DateTime Timestamp { get; set; }
        public Dictionary<string, bool> ShutdownResults { get; set; } = new();
        public bool DataPreserved { get; set; }
        public string? SystemSnapshotId { get; set; }
    }

    public class SystemStabilizedEvent : IEvent
    {
        public double InitialHealth { get; set; }
        public double FinalHealth { get; set; }
        public double Improvement { get; set; }
        public DateTime Timestamp { get; set; }
        public List<string> ActionsTaken { get; set; } = new();
        public TimeSpan Duration { get; set; }
    }

    public class DegradedModeEnteredEvent : IEvent
    {
        public DegradationLevel Level { get; set; }
        public DateTime EnteredAt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public TimeSpan ExpectedDuration { get; set; }
        public List<string> ServicesAffected { get; set; } = new();
        public PerformanceLevel PerformanceImpact { get; set; }
    }

    public class DegradedModeExitedEvent : IEvent
    {
        public DateTime ExitedAt { get; set; }
        public DegradationLevel PreviousLevel { get; set; }
        public TimeSpan TimeInDegradedMode { get; set; }
    }

    public class FullRecoveryCompletedEvent : IEvent
    {
        public string PlanId { get; set; } = string.Empty;
        public DateTime RecoveryTime { get; set; }
        public int StepsExecuted { get; set; }
        public int TotalSteps { get; set; }
        public double PreRecoveryHealth { get; set; }
        public double PostRecoveryHealth { get; set; }
        public TimeSpan Duration { get; set; }
    }

    #endregion;
}
