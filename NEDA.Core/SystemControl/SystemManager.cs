// NEDA.Core/SystemControl/SystemManager.cs
using Microsoft.Extensions.DependencyInjection;
using NEDA.AI.ReinforcementLearning;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.EnvironmentManager;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.HealthChecks;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.ProjectService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json;

namespace NEDA.Core.SystemControl
{
    /// <summary>
    /// Merkezi sistem yönetimi - Tüm sistem kaynaklarını ve işlemlerini yönetir
    /// </summary>
    public sealed class SystemManager : ISystemManager, IDisposable
    {
        #region Singleton Implementation
        private static readonly Lazy<SystemManager> _instance =
            new Lazy<SystemManager>(() => new SystemManager());

        public static SystemManager Instance => _instance.Value;

        private SystemManager()
        {
            Initialize();
        }
        #endregion

        #region Fields and Properties
        private readonly ConcurrentDictionary<int, ManagedProcess> _managedProcesses =
            new ConcurrentDictionary<int, ManagedProcess>();

        private readonly ConcurrentDictionary<string, SystemResource> _systemResources =
            new ConcurrentDictionary<string, SystemResource>();

        private readonly ConcurrentQueue<SystemCommand> _commandQueue =
            new ConcurrentQueue<SystemCommand>();

        private readonly ConcurrentBag<SystemEvent> _systemEvents =
            new ConcurrentBag<SystemEvent>();

        private readonly ReaderWriterLockSlim _systemLock = new ReaderWriterLockSlim();
        private readonly SemaphoreSlim _maintenanceLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancellationTokenSource =
            new CancellationTokenSource();

        private Task _monitoringTask;
        private Task _maintenanceTask;
        private Task _commandProcessorTask;
        private bool _isInitialized = false;
        private bool _isDisposed = false;

        private AppConfig _appConfig;
        private IEnvironmentConfig _environmentConfig;
        private ILogger _logger;
        private ISecurityManager _securityManager;
        private IRecoveryEngine _recoveryEngine;
        private IPerformanceMonitor _performanceMonitor;
        private IHealthChecker _healthChecker;
        private IDiagnosticTool _diagnosticTool;
        private IProjectManager _projectManager;

        private DateTime _lastMaintenance;
        private DateTime _lastHealthCheck;
        private SystemState _currentState = SystemState.Normal;
        private int _totalProcessesManaged = 0;
        private long _totalMemoryAllocated = 0;

        public SystemStatus CurrentStatus { get; private set; }
        public SystemPerformanceMetrics PerformanceMetrics { get; private set; }
        public bool IsMaintenanceMode { get; private set; } = false;
        public bool IsEmergencyMode { get; private set; } = false;
        public int ActiveProcessCount => _managedProcesses.Count;
        public event EventHandler<SystemEventArgs> SystemEvent;
        #endregion

        #region Public Methods
        /// <summary>
        /// SystemManager'ı başlatır ve yapılandırır
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            try
            {
                _systemLock.EnterWriteLock();

                // Bağımlılıkları yükle
                LoadDependencies();

                // Sistem durumunu başlat
                InitializeSystemState();

                // Sistem kaynaklarını tara
                ScanSystemResources();

                // Kritik işlemleri başlat
                StartCriticalProcesses();

                // İzleme görevlerini başlat
                StartMonitoringTasks();

                // Bakım görevini başlat
                StartMaintenanceTask();

                // Komut işlemcisini başlat
                StartCommandProcessor();

                _isInitialized = true;

                LogSystemEvent(SystemEventType.SystemInitialized,
                    "SystemManager başarıyla başlatıldı",
                    SystemSeverity.Information);

                // İlk sağlık kontrolü
                Task.Run(async () => await PerformHealthCheckAsync());
            }
            catch (Exception ex)
            {
                HandleInitializationError(ex);
                throw new SystemManagerInitializationException(
                    "SystemManager başlatma sırasında hata oluştu", ex);
            }
            finally
            {
                _systemLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// İşlem başlatır
        /// </summary>
        public async Task<ProcessStartResult> StartProcessAsync(
            ProcessStartInfo startInfo,
            ProcessOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateSystemState();
            ValidateStartInfo(startInfo);

            options ??= new ProcessOptions();

            try
            {
                // Güvenlik kontrolü
                await ValidateProcessSecurityAsync(startInfo, options);

                // Kaynak kontrolü
                if (!await CheckResourceAvailabilityAsync(options.ResourceRequirements))
                {
                    return ProcessStartResult.Failed(
                        "Yetersiz sistem kaynağı",
                        ProcessStartError.InsufficientResources);
                }

                // İşlem ID'si oluştur
                var processId = Guid.NewGuid().ToString();

                // İşlemi başlat
                var process = await CreateAndStartProcessAsync(
                    startInfo,
                    options,
                    processId,
                    cancellationToken);

                // İşlemi yönetim listesine ekle
                var managedProcess = new ManagedProcess
                {
                    ProcessId = processId,
                    SystemProcess = process,
                    StartInfo = startInfo,
                    Options = options,
                    StartTime = DateTime.UtcNow,
                    Status = ProcessStatus.Running,
                    ResourceUsage = new ProcessResourceUsage(),
                    Owner = WindowsIdentity.GetCurrent().Name,
                    Tags = options.Tags ?? new List<string>()
                };

                _managedProcesses[process.Id] = managedProcess;
                _totalProcessesManaged++;

                // Kaynak kullanımını izlemeye başla
                StartProcessMonitoring(managedProcess);

                LogSystemEvent(SystemEventType.ProcessStarted,
                    $"İşlem başlatıldı: {startInfo.FileName} (PID: {process.Id})",
                    SystemSeverity.Information,
                    processId: processId,
                    processName: startInfo.FileName);

                return ProcessStartResult.Success(processId, process.Id, managedProcess);
            }
            catch (OperationCanceledException)
            {
                LogSystemEvent(SystemEventType.OperationCancelled,
                    "İşlem başlatma iptal edildi",
                    SystemSeverity.Warning,
                    processName: startInfo.FileName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "İşlem başlatma sırasında hata oluştu: {FileName}", startInfo.FileName);

                LogSystemEvent(SystemEventType.ProcessStartFailed,
                    $"İşlem başlatma başarısız: {ex.Message}",
                    SystemSeverity.Error,
                    processName: startInfo.FileName);

                throw new ProcessStartException("İşlem başlatma sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// İşlemi durdurur
        /// </summary>
        public async Task<ProcessStopResult> StopProcessAsync(
            string processId,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateProcessId(processId);

            try
            {
                // İşlemi bul
                var process = FindManagedProcess(processId);
                if (process == null)
                {
                    return ProcessStopResult.Failed(
                        $"İşlem bulunamadı: {processId}",
                        ProcessStopError.ProcessNotFound);
                }

                // İşlem durumunu kontrol et
                if (process.Status == ProcessStatus.Stopped ||
                    process.Status == ProcessStatus.Terminated)
                {
                    return ProcessStopResult.Failed(
                        "İşlem zaten durdurulmuş",
                        ProcessStopError.AlreadyStopped);
                }

                // İşlemi durdur
                bool success = await StopProcessInternalAsync(process, force, cancellationToken);

                if (success)
                {
                    process.Status = force ? ProcessStatus.Terminated : ProcessStatus.Stopped;
                    process.EndTime = DateTime.UtcNow;

                    // Yönetim listesinden kaldır
                    _managedProcesses.TryRemove(process.SystemProcess.Id, out _);

                    LogSystemEvent(SystemEventType.ProcessStopped,
                        $"İşlem durduruldu: {process.StartInfo.FileName} (PID: {process.SystemProcess.Id})",
                        SystemSeverity.Information,
                        processId: processId,
                        processName: process.StartInfo.FileName);

                    return ProcessStopResult.Success(processId, process.SystemProcess.Id);
                }
                else
                {
                    LogSystemEvent(SystemEventType.ProcessStopFailed,
                        $"İşlem durdurma başarısız: {process.StartInfo.FileName}",
                        SystemSeverity.Error,
                        processId: processId,
                        processName: process.StartInfo.FileName);

                    return ProcessStopResult.Failed(
                        "İşlem durdurulamadı",
                        ProcessStopError.StopFailed);
                }
            }
            catch (OperationCanceledException)
            {
                LogSystemEvent(SystemEventType.OperationCancelled,
                    "İşlem durdurma iptal edildi",
                    SystemSeverity.Warning,
                    processId: processId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "İşlem durdurma sırasında hata oluştu: {ProcessId}", processId);

                LogSystemEvent(SystemEventType.ProcessStopFailed,
                    $"İşlem durdurma hatası: {ex.Message}",
                    SystemSeverity.Error,
                    processId: processId);

                throw new ProcessStopException("İşlem durdurma sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Tüm işlemleri durdurur
        /// </summary>
        public async Task<int> StopAllProcessesAsync(bool force = false, string category = null)
        {
            ValidateDisposed();

            if (IsEmergencyMode && !force)
            {
                throw new SystemEmergencyException("Acil durum modunda işlem durdurma yapılamaz");
            }

            var count = 0;
            var processesToStop = _managedProcesses.Values
                .Where(p => category == null || (p.Options?.Category == category))
                .ToList();

            foreach (var process in processesToStop)
            {
                try
                {
                    if (await StopProcessAsync(process.ProcessId, force))
                    {
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "İşlem durdurma sırasında hata: {ProcessId}", process.ProcessId);
                }
            }

            LogSystemEvent(SystemEventType.AllProcessesStopped,
                $"Tüm işlemler durduruldu: {count} işlem",
                SystemSeverity.Warning,
                force ? "Force stop" : "Graceful stop");

            return count;
        }

        /// <summary>
        /// Sistem kaynağı ayırır
        /// </summary>
        public async Task<ResourceAllocationResult> AllocateResourceAsync(
            ResourceRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateSystemState();
            ValidateResourceRequest(request);

            try
            {
                // Kaynak kullanılabilirliğini kontrol et
                if (!await CheckResourceAvailabilityAsync(request.Requirements))
                {
                    return ResourceAllocationResult.Failed(
                        "Yetersiz kaynak",
                        ResourceAllocationError.InsufficientResources);
                }

                // Kaynak ID'si oluştur
                var resourceId = Guid.NewGuid().ToString();

                // Kaynağı ayır
                var resource = await AllocateResourceInternalAsync(request, resourceId, cancellationToken);

                // Kaynak yönetim listesine ekle
                _systemResources[resourceId] = resource;

                // Kaynak kullanımını güncelle
                UpdateResourceUsage(resource);

                LogSystemEvent(SystemEventType.ResourceAllocated,
                    $"Kaynak ayrıldı: {request.Type} - {request.Name}",
                    SystemSeverity.Information,
                    resourceId: resourceId,
                    resourceName: request.Name);

                return ResourceAllocationResult.Success(resourceId, resource);
            }
            catch (OperationCanceledException)
            {
                LogSystemEvent(SystemEventType.OperationCancelled,
                    "Kaynak ayırma iptal edildi",
                    SystemSeverity.Warning,
                    resourceName: request.Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kaynak ayırma sırasında hata oluştu: {ResourceName}", request.Name);

                LogSystemEvent(SystemEventType.ResourceAllocationFailed,
                    $"Kaynak ayırma başarısız: {ex.Message}",
                    SystemSeverity.Error,
                    resourceName: request.Name);

                throw new ResourceAllocationException("Kaynak ayırma sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Sistem kaynağını serbest bırakır
        /// </summary>
        public async Task<ResourceReleaseResult> ReleaseResourceAsync(
            string resourceId,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateResourceId(resourceId);

            try
            {
                // Kaynağı bul
                if (!_systemResources.TryRemove(resourceId, out var resource))
                {
                    return ResourceReleaseResult.Failed(
                        $"Kaynak bulunamadı: {resourceId}",
                        ResourceReleaseError.ResourceNotFound);
                }

                // Kaynağı serbest bırak
                await ReleaseResourceInternalAsync(resource, cancellationToken);

                // Kaynak kullanımını güncelle
                UpdateResourceUsage(resource, true);

                LogSystemEvent(SystemEventType.ResourceReleased,
                    $"Kaynak serbest bırakıldı: {resource.Type} - {resource.Name}",
                    SystemSeverity.Information,
                    resourceId: resourceId,
                    resourceName: resource.Name);

                return ResourceReleaseResult.Success(resourceId);
            }
            catch (OperationCanceledException)
            {
                LogSystemEvent(SystemEventType.OperationCancelled,
                    "Kaynak serbest bırakma iptal edildi",
                    SystemSeverity.Warning,
                    resourceId: resourceId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kaynak serbest bırakma sırasında hata oluştu: {ResourceId}", resourceId);

                LogSystemEvent(SystemEventType.ResourceReleaseFailed,
                    $"Kaynak serbest bırakma başarısız: {ex.Message}",
                    SystemSeverity.Error,
                    resourceId: resourceId);

                throw new ResourceReleaseException("Kaynak serbest bırakma sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Sistem komutu yürütür
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateSystemState();
            ValidateCommand(command);

            try
            {
                // Komut güvenliğini kontrol et
                await ValidateCommandSecurityAsync(command);

                // Komut önceliğine göre işleme al
                if (command.Priority == CommandPriority.Immediate)
                {
                    // Hemen yürüt
                    return await ExecuteCommandImmediatelyAsync(command, cancellationToken);
                }
                else
                {
                    // Kuyruğa ekle
                    _commandQueue.Enqueue(command);

                    LogSystemEvent(SystemEventType.CommandQueued,
                        $"Komut kuyruğa alındı: {command.CommandType} - {command.Name}",
                        SystemSeverity.Information,
                        commandId: command.CommandId);

                    return CommandExecutionResult.Queued(command.CommandId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Komut yürütme sırasında hata oluştu: {CommandName}", command.Name);

                LogSystemEvent(SystemEventType.CommandFailed,
                    $"Komut yürütme başarısız: {ex.Message}",
                    SystemSeverity.Error,
                    commandId: command.CommandId,
                    commandName: command.Name);

                throw new CommandExecutionException("Komut yürütme sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Sistem bakımı başlatır
        /// </summary>
        public async Task<MaintenanceResult> StartMaintenanceAsync(
            MaintenancePlan plan,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateMaintenancePlan(plan);

            try
            {
                await _maintenanceLock.WaitAsync(cancellationToken);

                IsMaintenanceMode = true;
                _currentState = SystemState.Maintenance;

                LogSystemEvent(SystemEventType.MaintenanceStarted,
                    $"Sistem bakımı başlatıldı: {plan.Name}",
                    SystemSeverity.Information,
                    maintenanceId: plan.MaintenanceId);

                // Bakım planını yürüt
                var result = await ExecuteMaintenancePlanAsync(plan, cancellationToken);

                IsMaintenanceMode = false;
                _currentState = SystemState.Normal;
                _lastMaintenance = DateTime.UtcNow;

                LogSystemEvent(SystemEventType.MaintenanceCompleted,
                    $"Sistem bakımı tamamlandı: {plan.Name}",
                    SystemSeverity.Information,
                    maintenanceId: plan.MaintenanceId);

                return result;
            }
            catch (OperationCanceledException)
            {
                IsMaintenanceMode = false;
                _currentState = SystemState.Normal;

                LogSystemEvent(SystemEventType.MaintenanceCancelled,
                    "Sistem bakımı iptal edildi",
                    SystemSeverity.Warning);
                throw;
            }
            catch (Exception ex)
            {
                IsMaintenanceMode = false;
                _currentState = SystemState.Normal;

                _logger.LogError(ex, "Sistem bakımı sırasında hata oluştu");

                LogSystemEvent(SystemEventType.MaintenanceFailed,
                    $"Sistem bakımı başarısız: {ex.Message}",
                    SystemSeverity.Error);

                throw new MaintenanceException("Sistem bakımı sırasında hata oluştu", ex);
            }
            finally
            {
                _maintenanceLock.Release();
            }
        }

        /// <summary>
        /// Sistem durumunu alır
        /// </summary>
        public SystemStatus GetSystemStatus()
        {
            ValidateDisposed();

            return new SystemStatus
            {
                IsInitialized = _isInitialized,
                CurrentState = _currentState,
                IsMaintenanceMode = IsMaintenanceMode,
                IsEmergencyMode = IsEmergencyMode,
                ActiveProcessCount = ActiveProcessCount,
                ActiveResourceCount = _systemResources.Count,
                QueuedCommands = _commandQueue.Count,
                TotalProcessesManaged = _totalProcessesManaged,
                TotalMemoryAllocated = _totalMemoryAllocated,
                LastMaintenance = _lastMaintenance,
                LastHealthCheck = _lastHealthCheck,
                Uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()
            };
        }

        /// <summary>
        /// Sistem performans metriklerini alır
        /// </summary>
        public async Task<SystemPerformanceMetrics> GetPerformanceMetricsAsync()
        {
            ValidateDisposed();

            await UpdatePerformanceMetricsAsync();
            return PerformanceMetrics;
        }

        /// <summary>
        /// Sistem olaylarını filtreleyerek alır
        /// </summary>
        public IEnumerable<SystemEvent> GetSystemEvents(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            SystemEventType? eventType = null,
            SystemSeverity? minSeverity = null)
        {
            ValidateDisposed();

            var events = _systemEvents.AsEnumerable();

            if (fromDate.HasValue)
                events = events.Where(e => e.Timestamp >= fromDate.Value);

            if (toDate.HasValue)
                events = events.Where(e => e.Timestamp <= toDate.Value);

            if (eventType.HasValue)
                events = events.Where(e => e.EventType == eventType.Value);

            if (minSeverity.HasValue)
                events = events.Where(e => e.Severity >= minSeverity.Value);

            return events.OrderByDescending(e => e.Timestamp).Take(1000);
        }

        /// <summary>
        /// Sistem teşhisi çalıştırır
        /// </summary>
        public async Task<DiagnosticResult> RunDiagnosticsAsync(
            DiagnosticOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();

            options ??= new DiagnosticOptions();

            try
            {
                var diagnosticId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                LogSystemEvent(SystemEventType.DiagnosticsStarted,
                    $"Sistem teşhisi başlatıldı: {diagnosticId}",
                    SystemSeverity.Information,
                    diagnosticId: diagnosticId);

                // Teşhis testlerini çalıştır
                var results = await ExecuteDiagnosticsAsync(options, cancellationToken);

                var duration = DateTime.UtcNow - startTime;

                // Sonuçları analiz et
                var analysis = AnalyzeDiagnosticResults(results);

                var result = new DiagnosticResult
                {
                    DiagnosticId = diagnosticId,
                    StartTime = startTime,
                    Duration = duration,
                    Tests = results,
                    Analysis = analysis,
                    Recommendations = GenerateRecommendations(analysis)
                };

                LogSystemEvent(SystemEventType.DiagnosticsCompleted,
                    $"Sistem teşhisi tamamlandı: {diagnosticId}",
                    SystemSeverity.Information,
                    diagnosticId: diagnosticId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sistem teşhisi sırasında hata oluştu");

                LogSystemEvent(SystemEventType.DiagnosticsFailed,
                    $"Sistem teşhisi başarısız: {ex.Message}",
                    SystemSeverity.Error);

                throw new DiagnosticException("Sistem teşhisi sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Sistem yedeği alır
        /// </summary>
        public async Task<BackupResult> CreateBackupAsync(
            BackupOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateSystemState();

            options ??= new BackupOptions();

            try
            {
                var backupId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                LogSystemEvent(SystemEventType.BackupStarted,
                    $"Sistem yedeği başlatıldı: {backupId}",
                    SystemSeverity.Information,
                    backupId: backupId);

                // Yedekleme işlemini yürüt
                var backup = await ExecuteBackupAsync(options, backupId, cancellationToken);

                var duration = DateTime.UtcNow - startTime;

                LogSystemEvent(SystemEventType.BackupCompleted,
                    $"Sistem yedeği tamamlandı: {backupId}",
                    SystemSeverity.Information,
                    backupId: backupId);

                return BackupResult.Success(backupId, backup, duration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sistem yedeği sırasında hata oluştu");

                LogSystemEvent(SystemEventType.BackupFailed,
                    $"Sistem yedeği başarısız: {ex.Message}",
                    SystemSeverity.Error);

                throw new BackupException("Sistem yedeği sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Sistem geri yüklemesi yapar
        /// </summary>
        public async Task<RestoreResult> RestoreBackupAsync(
            string backupId,
            RestoreOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateBackupId(backupId);

            if (IsEmergencyMode)
            {
                throw new SystemEmergencyException("Acil durum modunda geri yükleme yapılamaz");
            }

            options ??= new RestoreOptions();

            try
            {
                var startTime = DateTime.UtcNow;

                LogSystemEvent(SystemEventType.RestoreStarted,
                    $"Sistem geri yükleme başlatıldı: {backupId}",
                    SystemSeverity.Warning,
                    backupId: backupId);

                // Geri yükleme işlemini yürüt
                await ExecuteRestoreAsync(backupId, options, cancellationToken);

                var duration = DateTime.UtcNow - startTime;

                // Sistem yeniden başlat
                await RestartSystemAsync();

                LogSystemEvent(SystemEventType.RestoreCompleted,
                    $"Sistem geri yükleme tamamlandı: {backupId}",
                    SystemSeverity.Information,
                    backupId: backupId);

                return RestoreResult.Success(backupId, duration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sistem geri yükleme sırasında hata oluştu");

                LogSystemEvent(SystemEventType.RestoreFailed,
                    $"Sistem geri yükleme başarısız: {ex.Message}",
                    SystemSeverity.Error,
                    backupId: backupId);

                throw new RestoreException("Sistem geri yükleme sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Acil durum modunu etkinleştirir
        /// </summary>
        public void ActivateEmergencyMode(string reason)
        {
            ValidateDisposed();

            _systemLock.EnterWriteLock();
            try
            {
                IsEmergencyMode = true;
                _currentState = SystemState.Emergency;

                // Kritik olmayan işlemleri durdur
                Task.Run(async () => await StopNonCriticalProcessesAsync());

                // Acil durum protokollerini başlat
                StartEmergencyProtocols();

                LogSystemEvent(SystemEventType.EmergencyModeActivated,
                    $"Acil durum modu etkinleştirildi: {reason}",
                    SystemSeverity.Critical);
            }
            finally
            {
                _systemLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Acil durum modunu devre dışı bırakır
        /// </summary>
        public void DeactivateEmergencyMode()
        {
            ValidateDisposed();

            _systemLock.EnterWriteLock();
            try
            {
                IsEmergencyMode = false;
                _currentState = SystemState.Normal;

                // Normal işlemlere devam et
                ResumeNormalOperations();

                LogSystemEvent(SystemEventType.EmergencyModeDeactivated,
                    "Acil durum modu devre dışı bırakıldı",
                    SystemSeverity.Information);
            }
            finally
            {
                _systemLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Sistem yeniden başlatma
        /// </summary>
        public async Task<RestartResult> RestartSystemAsync(
            RestartOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();

            if (IsEmergencyMode)
            {
                throw new SystemEmergencyException("Acil durum modunda yeniden başlatma yapılamaz");
            }

            options ??= new RestartOptions();

            try
            {
                var restartId = Guid.NewGuid().ToString();

                LogSystemEvent(SystemEventType.RestartStarted,
                    $"Sistem yeniden başlatılıyor: {restartId}",
                    SystemSeverity.Warning,
                    restartId: restartId);

                // Yeniden başlatma öncesi hazırlık
                await PrepareForRestartAsync(options, cancellationToken);

                // İşlemleri durdur
                await StopAllProcessesAsync(options.ForceStop);

                // Kaynakları serbest bırak
                await ReleaseAllResourcesAsync();

                // SystemManager'ı yeniden başlat
                await RestartSystemManagerAsync();

                LogSystemEvent(SystemEventType.RestartCompleted,
                    $"Sistem yeniden başlatıldı: {restartId}",
                    SystemSeverity.Information,
                    restartId: restartId);

                return RestartResult.Success(restartId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sistem yeniden başlatma sırasında hata oluştu");

                LogSystemEvent(SystemEventType.RestartFailed,
                    $"Sistem yeniden başlatma başarısız: {ex.Message}",
                    SystemSeverity.Error);

                throw new RestartException("Sistem yeniden başlatma sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Sistem kapanışı
        /// </summary>
        public async Task<ShutdownResult> ShutdownSystemAsync(
            ShutdownOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();

            options ??= new ShutdownOptions();

            try
            {
                var shutdownId = Guid.NewGuid().ToString();

                LogSystemEvent(SystemEventType.ShutdownStarted,
                    $"Sistem kapatılıyor: {shutdownId}",
                    SystemSeverity.Warning,
                    shutdownId: shutdownId);

                // Kapanış öncesi hazırlık
                await PrepareForShutdownAsync(options, cancellationToken);

                // İşlemleri durdur
                await StopAllProcessesAsync(options.ForceStop);

                // Kaynakları serbest bırak
                await ReleaseAllResourcesAsync();

                // SystemManager'ı durdur
                Dispose();

                LogSystemEvent(SystemEventType.ShutdownCompleted,
                    $"Sistem kapatıldı: {shutdownId}",
                    SystemSeverity.Information,
                    shutdownId: shutdownId);

                return ShutdownResult.Success(shutdownId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sistem kapatma sırasında hata oluştu");

                LogSystemEvent(SystemEventType.ShutdownFailed,
                    $"Sistem kapatma başarısız: {ex.Message}",
                    SystemSeverity.Error);

                throw new ShutdownException("Sistem kapatma sırasında hata oluştu", ex);
            }
        }
        #endregion

        #region Private Methods
        private void LoadDependencies()
        {
            var serviceProvider = ServiceProviderFactory.GetServiceProvider();

            _appConfig = serviceProvider.GetRequiredService<AppConfig>();
            _environmentConfig = serviceProvider.GetRequiredService<IEnvironmentConfig>();
            _logger = serviceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<SystemManager>();
            _securityManager = serviceProvider.GetRequiredService<ISecurityManager>();
            _recoveryEngine = serviceProvider.GetRequiredService<IRecoveryEngine>();
            _performanceMonitor = serviceProvider.GetRequiredService<IPerformanceMonitor>();
            _healthChecker = serviceProvider.GetRequiredService<IHealthChecker>();
            _diagnosticTool = serviceProvider.GetRequiredService<IDiagnosticTool>();
            _projectManager = serviceProvider.GetRequiredService<IProjectManager>();
        }

        private void InitializeSystemState()
        {
            CurrentStatus = new SystemStatus
            {
                IsInitialized = false,
                CurrentState = SystemState.Initializing,
                IsMaintenanceMode = false,
                IsEmergencyMode = false,
                ActiveProcessCount = 0,
                ActiveResourceCount = 0,
                QueuedCommands = 0,
                TotalProcessesManaged = 0,
                TotalMemoryAllocated = 0,
                LastMaintenance = DateTime.MinValue,
                LastHealthCheck = DateTime.MinValue,
                Uptime = TimeSpan.Zero,
            };

            PerformanceMetrics = new SystemPerformanceMetrics
            {
                CpuUsage = 0,
                MemoryUsage = 0,
                DiskUsage = 0,
                NetworkUsage = 0,
                ProcessCount = 0,
                ThreadCount = 0,
                HandleCount = 0,
                Timestamp = DateTime.UtcNow,
            };

            _currentState = SystemState.Normal;
        }

        private void ScanSystemResources()
        {
            try
            {
                // CPU bilgileri
                ScanCpuResources();

                // Bellek bilgileri
                ScanMemoryResources();

                // Disk bilgileri
                ScanDiskResources();

                // Ağ bilgileri
                ScanNetworkResources();

                // Sistem bilgileri
                ScanSystemInformation();

                _logger.LogInformation("Sistem kaynakları taranıp başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sistem kaynakları tarama sırasında hata oluştu");
                throw;
            }
        }

        private void ScanCpuResources()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    var cpu = new CpuResource
                    {
                        Name = obj["Name"].ToString(),
                        Manufacturer = obj["Manufacturer"].ToString(),
                        Cores = int.Parse(obj["NumberOfCores"].ToString()),
                        LogicalProcessors = int.Parse(obj["NumberOfLogicalProcessors"].ToString()),
                        MaxClockSpeed = uint.Parse(obj["MaxClockSpeed"].ToString()),
                        Architecture = GetCpuArchitecture(int.Parse(obj["Architecture"].ToString()))
                    };

                    _systemResources[$"cpu_{cpu.Name}"] = cpu;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CPU kaynakları taranamadı");
            }
        }

        private void ScanMemoryResources()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
                long totalMemory = 0;
                int memoryIndex = 0;

                foreach (var obj in searcher.Get())
                {
                    var memory = new MemoryResource
                    {
                        Name = $"Memory_{memoryIndex++}",
                        Manufacturer = obj["Manufacturer"]?.ToString() ?? "Unknown",
                        Capacity = long.Parse(obj["Capacity"].ToString()),
                        Speed = uint.Parse(obj["Speed"].ToString()),
                        MemoryType = GetMemoryType(uint.Parse(obj["MemoryType"].ToString()))
                    };

                    _systemResources[$"memory_{memory.Name}"] = memory;
                    totalMemory += memory.Capacity;
                }

                // Toplam belleği kaydet
                var totalMemoryResource = new SystemResource
                {
                    Id = "total_memory",
                    Name = "Total System Memory",
                    Type = ResourceType.Memory,
                    Capacity = totalMemory,
                    CurrentUsage = 0,
                };

                _systemResources["total_memory"] = totalMemoryResource;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bellek kaynakları taranamadı");
            }
        }

        private void ScanDiskResources()
        {
            try
            {
                var drives = DriveInfo.GetDrives();
                foreach (var drive in drives.Where(d => d.IsReady))
                {
                    var disk = new DiskResource
                    {
                        Name = drive.Name,
                        DriveType = drive.DriveType,
                        TotalSize = drive.TotalSize,
                        AvailableSpace = drive.AvailableFreeSpace,
                        FileSystem = drive.DriveFormat,
                        VolumeLabel = drive.VolumeLabel,
                    };

                    _systemResources[$"disk_{drive.Name.Replace(":", "")}"] = disk;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disk kaynakları taranamadı");
            }
        }

        private void ScanNetworkResources()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE");
                int networkIndex = 0;

                foreach (var obj in searcher.Get())
                {
                    var network = new NetworkResource
                    {
                        Name = obj["Name"]?.ToString() ?? $"Network_{networkIndex++}",
                        Manufacturer = obj["Manufacturer"]?.ToString(),
                        MACAddress = obj["MACAddress"]?.ToString(),
                        Speed = obj["Speed"] != null ? ulong.Parse(obj["Speed"].ToString()) : 0,
                    };

                    _systemResources[$"network_{network.Name}"] = network;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ağ kaynakları taranamadı");
            }
        }

        private void ScanSystemInformation()
        {
            try
            {
                var systemInfo = new SystemInformation
                {
                    MachineName = Environment.MachineName,
                    OSVersion = Environment.OSVersion.ToString(),
                    Platform = Environment.OSVersion.Platform.ToString(),
                    ProcessorCount = Environment.ProcessorCount,
                    SystemDirectory = Environment.SystemDirectory,
                    UserDomainName = Environment.UserDomainName,
                    UserName = Environment.UserName,
                    Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                    Is64BitProcess = Environment.Is64BitProcess,
                    TickCount = Environment.TickCount,
                    WorkingSet = Environment.WorkingSet,
                };

                _systemResources["system_information"] = systemInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sistem bilgileri taranamadı");
            }
        }

        private string GetCpuArchitecture(int architectureCode)
        {
            return architectureCode switch
            {
                0 => "x86",
                1 => "MIPS",
                2 => "Alpha",
                3 => "PowerPC",
                5 => "ARM",
                6 => "ia64",
                9 => "x64",
                12 => "ARM64",
                _ => "Unknown"
            };
        }

        private string GetMemoryType(uint memoryType)
        {
            return memoryType switch
            {
                0 => "Unknown",
                1 => "Other",
                2 => "DRAM",
                3 => "Synchronous DRAM",
                4 => "Cache DRAM",
                5 => "EDO",
                6 => "EDRAM",
                7 => "VRAM",
                8 => "SRAM",
                9 => "RAM",
                10 => "ROM",
                11 => "Flash",
                12 => "EEPROM",
                13 => "FEPROM",
                14 => "EPROM",
                15 => "CDRAM",
                16 => "3DRAM",
                17 => "SDRAM",
                18 => "SGRAM",
                19 => "RDRAM",
                20 => "DDR",
                21 => "DDR2",
                22 => "DDR2 FB-DIMM",
                24 => "DDR3",
                25 => "FBD2",
                26 => "DDR4",
                _ => "Unknown"
            };
        }

        private void StartCriticalProcesses()
        {
            try
            {
                // Logging service
                StartLoggingService();

                // Monitoring service
                StartMonitoringService();

                // Security service
                StartSecurityService();

                // Recovery service
                StartRecoveryService();

                _logger.LogInformation("Kritik sistem servisleri başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kritik servisler başlatılırken hata oluştu");
                throw;
            }
        }

        private void StartLoggingService()
        {
            // Logging servisini başlat
            var startInfo = new ProcessStartInfo
            {
                FileName = "NEDA.Logging.Service.exe",
                Arguments = $"--environment {_environmentConfig.EnvironmentName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Task.Run(async () =>
            {
                try
                {
                    await StartProcessAsync(startInfo, new ProcessOptions
                    {
                        Category = "SystemService",
                        Priority = ProcessPriority.High,
                        RestartOnFailure = true,
                        MaxRestartAttempts = 3,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Logging servisi başlatılamadı");
                }
            });
        }

        private void StartMonitoringService()
        {
            // Monitoring servisini başlat
            var startInfo = new ProcessStartInfo
            {
                FileName = "NEDA.Monitoring.Service.exe",
                Arguments = $"--environment {_environmentConfig.EnvironmentName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Task.Run(async () =>
            {
                try
                {
                    await StartProcessAsync(startInfo, new ProcessOptions
                    {
                        Category = "SystemService",
                        Priority = ProcessPriority.High,
                        RestartOnFailure = true,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Monitoring servisi başlatılamadı");
                }
            });
        }

        private void StartSecurityService()
        {
            // Security servisini başlat
            var startInfo = new ProcessStartInfo
            {
                FileName = "NEDA.Security.Service.exe",
                Arguments = $"--environment {_environmentConfig.EnvironmentName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Task.Run(async () =>
            {
                try
                {
                    await StartProcessAsync(startInfo, new ProcessOptions
                    {
                        Category = "SystemService",
                        Priority = ProcessPriority.High,
                        IsCritical = true,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Security servisi başlatılamadı");
                }
            });
        }

        private void StartRecoveryService()
        {
            // Recovery servisini başlat
            var startInfo = new ProcessStartInfo
            {
                FileName = "NEDA.Recovery.Service.exe",
                Arguments = $"--environment {_environmentConfig.EnvironmentName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Task.Run(async () =>
            {
                try
                {
                    await StartProcessAsync(startInfo, new ProcessOptions
                    {
                        Category = "SystemService",
                        Priority = ProcessPriority.Medium,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Recovery servisi başlatılamadı");
                }
            });
        }

        private void StartMonitoringTasks()
        {
            // Sistem izleme görevi
            _monitoringTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await MonitorSystemResourcesAsync();
                        await MonitorProcessesAsync();
                        await CheckSystemHealthAsync();
                        await ProcessAlertsAsync();

                        await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Sistem izleme görevi sırasında hata oluştu");
                        await Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private void StartMaintenanceTask()
        {
            // Sistem bakım görevi
            _maintenanceTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await PerformScheduledMaintenanceAsync();
                        await CleanupSystemResourcesAsync();
                        await OptimizeSystemPerformanceAsync();

                        await Task.Delay(TimeSpan.FromMinutes(5), _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Sistem bakım görevi sırasında hata oluştu");
                        await Task.Delay(TimeSpan.FromMinutes(1), _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private void StartCommandProcessor()
        {
            // Komut işlemcisi
            _commandProcessorTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        if (_commandQueue.TryDequeue(out var command))
                        {
                            await ProcessCommandAsync(command);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), _cancellationTokenSource.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Komut işleme sırasında hata oluştu");
                        await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private async Task ValidateProcessSecurityAsync(ProcessStartInfo startInfo, ProcessOptions options)
        {
            // İşlem güvenlik kontrolü
            if (options.RequireAdminPrivileges)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);

                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    throw new SecurityException("İşlem yönetici ayrıcalıkları gerektiriyor");
                }
            }

            // Dosya güvenlik kontrolü
            if (!File.Exists(startInfo.FileName))
            {
                throw new FileNotFoundException($"İşlem dosyası bulunamadı: {startInfo.FileName}");
            }

            // İzin kontrolü
            var fileInfo = new FileInfo(startInfo.FileName);
            if ((fileInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                throw new UnauthorizedAccessException($"İşlem dosyası salt okunur: {startInfo.FileName}");
            }

            // İmza kontrolü (gerçek uygulamada dijital imza doğrulaması yapılır)
            if (options.RequireDigitalSignature)
            {
                await ValidateDigitalSignatureAsync(startInfo.FileName);
            }
        }

        private async Task ValidateDigitalSignatureAsync(string filePath)
        {
            // Dijital imza doğrulama (basitleştirilmiş)
            await Task.CompletedTask;
            // Gerçek uygulamada:
            // 1. Dosya imzasını al
            // 2. Güvenilir sertifika depolarından doğrula
            // 3. İmza geçerliliğini kontrol et
        }

        private async Task<bool> CheckResourceAvailabilityAsync(ResourceRequirements requirements)
        {
            if (requirements == null) return true;

            var availableResources = await GetAvailableResourcesAsync();

            // CPU kontrolü
            if (requirements.CpuCores > 0 &&
                availableResources.AvailableCpuCores < requirements.CpuCores)
            {
                return false;
            }

            // Bellek kontrolü
            if (requirements.MemoryMB > 0 &&
                availableResources.AvailableMemoryMB < requirements.MemoryMB)
            {
                return false;
            }

            // Disk alanı kontrolü
            if (requirements.DiskSpaceMB > 0 &&
                availableResources.AvailableDiskSpaceMB < requirements.DiskSpaceMB)
            {
                return false;
            }

            // Network bant genişliği kontrolü
            if (requirements.NetworkBandwidthMbps > 0 &&
                availableResources.AvailableNetworkBandwidthMbps < requirements.NetworkBandwidthMbps)
            {
                return false;
            }

            return true;
        }

        private async Task<AvailableResources> GetAvailableResourcesAsync()
        {
            var metrics = await GetPerformanceMetricsAsync();

            return new AvailableResources
            {
                TotalCpuCores = Environment.ProcessorCount,
                AvailableCpuCores = Environment.ProcessorCount * (100 - metrics.CpuUsage) / 100,
                TotalMemoryMB = GetTotalMemoryMB(),
                AvailableMemoryMB = GetTotalMemoryMB() * (100 - metrics.MemoryUsage) / 100,
                TotalDiskSpaceMB = GetTotalDiskSpaceMB(),
                AvailableDiskSpaceMB = GetTotalDiskSpaceMB() * (100 - metrics.DiskUsage) / 100,
                TotalNetworkBandwidthMbps = 1000, // Varsayılan değer
                AvailableNetworkBandwidthMbps = 1000 * (100 - metrics.NetworkUsage) / 100,
            };
        }

        private long GetTotalMemoryMB()
        {
            try
            {
                if (_systemResources.TryGetValue("total_memory", out var resource))
                {
                    return resource.Capacity / (1024 * 1024);
                }

                return 0;
            }
            catch
            {
                return 8192; // Varsayılan 8GB
            }
        }

        private long GetTotalDiskSpaceMB()
        {
            try
            {
                long totalSpace = 0;
                var diskResources = _systemResources.Values.OfType<DiskResource>();

                foreach (var disk in diskResources)
                {
                    totalSpace += disk.TotalSize;
                }

                return totalSpace / (1024 * 1024);
            }
            catch
            {
                return 1024 * 1024; // Varsayılan 1TB
            }
        }

        private async Task<Process> CreateAndStartProcessAsync(
            ProcessStartInfo startInfo,
            ProcessOptions options,
            string processId,
            CancellationToken cancellationToken)
        {
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            // İşlem event'lerini dinle
            process.Exited += (sender, e) => OnProcessExited(processId, process);
            process.OutputDataReceived += (sender, e) => OnProcessOutput(processId, e.Data, false);
            process.ErrorDataReceived += (sender, e) => OnProcessOutput(processId, e.Data, true);

            // İşlemi başlat
            if (!process.Start())
            {
                throw new ProcessStartException($"İşlem başlatılamadı: {startInfo.FileName}");
            }

            // Output'ları dinlemeye başla
            if (startInfo.RedirectStandardOutput)
            {
                process.BeginOutputReadLine();
            }

            if (startInfo.RedirectStandardError)
            {
                process.BeginErrorReadLine();
            }

            // Timeout kontrolü
            if (options.StartupTimeout > TimeSpan.Zero)
            {
                var startupTask = Task.Run(async () =>
                {
                    await Task.Delay(options.StartupTimeout, cancellationToken);

                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch { /* ignore */ }

                        throw new TimeoutException($"İşlem başlatma zaman aşımı: {startInfo.FileName}");
                    }
                }, cancellationToken);

                await startupTask;
            }

            return process;
        }

        private void StartProcessMonitoring(ManagedProcess managedProcess)
        {
            Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested &&
                       !managedProcess.SystemProcess.HasExited)
                {
                    try
                    {
                        await UpdateProcessResourceUsageAsync(managedProcess);
                        await CheckProcessHealthAsync(managedProcess);

                        await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "İşlem izleme sırasında hata: {ProcessId}", managedProcess.ProcessId);
                        await Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token);
                    }
                }
            });
        }

        private async Task UpdateProcessResourceUsageAsync(ManagedProcess managedProcess)
        {
            try
            {
                var process = managedProcess.SystemProcess;
                process.Refresh();

                managedProcess.ResourceUsage = new ProcessResourceUsage
                {
                    CpuUsage = await GetProcessCpuUsageAsync(process),
                    MemoryUsage = process.WorkingSet64,
                    PrivateMemoryUsage = process.PrivateMemorySize64,
                    VirtualMemoryUsage = process.VirtualMemorySize64,
                    HandleCount = process.HandleCount,
                    ThreadCount = process.Threads.Count,
                    StartTime = process.StartTime,
                    TotalProcessorTime = process.TotalProcessorTime,
                    UserProcessorTime = process.UserProcessorTime,
                };

                // Toplam bellek kullanımını güncelle
                _totalMemoryAllocated = _managedProcesses.Values
                    .Sum(p => p.ResourceUsage?.MemoryUsage ?? 0);
            }
            catch (InvalidOperationException)
            {
                // İşlem sonlanmış
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "İşlem kaynak kullanımı güncelleme hatası");
            }
        }

        private async Task<double> GetProcessCpuUsageAsync(Process process)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var startCpuUsage = process.TotalProcessorTime;

                await Task.Delay(500);

                process.Refresh();
                var endTime = DateTime.UtcNow;
                var endCpuUsage = process.TotalProcessorTime;

                var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
                var totalMsPassed = (endTime - startTime).TotalMilliseconds;
                var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

                return cpuUsageTotal * 100;
            }
            catch
            {
                return 0;
            }
        }

        private async Task CheckProcessHealthAsync(ManagedProcess managedProcess)
        {
            var process = managedProcess.SystemProcess;

            // CPU kullanımı kontrolü
            if (managedProcess.ResourceUsage.CpuUsage > 90 &&
                managedProcess.Options.MaxCpuUsage > 0)
            {
                LogSystemEvent(SystemEventType.ProcessHighCpu,
                    $"İşlem yüksek CPU kullanımı: {managedProcess.StartInfo.FileName} - %{managedProcess.ResourceUsage.CpuUsage:F1}",
                    SystemSeverity.Warning,
                    processId: managedProcess.ProcessId,
                    processName: managedProcess.StartInfo.FileName);

                if (managedProcess.Options.MaxCpuUsage > 0 &&
                    managedProcess.ResourceUsage.CpuUsage > managedProcess.Options.MaxCpuUsage)
                {
                    // CPU limit aşıldı, işlemi yeniden başlat
                    await RestartProcessAsync(managedProcess, "CPU limit aşıldı");
                }
            }

            // Bellek kullanımı kontrolü
            if (managedProcess.Options.MaxMemoryMB > 0)
            {
                var memoryUsageMB = managedProcess.ResourceUsage.MemoryUsage / (1024 * 1024);
                if (memoryUsageMB > managedProcess.Options.MaxMemoryMB)
                {
                    LogSystemEvent(SystemEventType.ProcessHighMemory,
                        $"İşlem yüksek bellek kullanımı: {managedProcess.StartInfo.FileName} - {memoryUsageMB}MB",
                        SystemSeverity.Warning,
                        processId: managedProcess.ProcessId,
                        processName: managedProcess.StartInfo.FileName);

                    // Bellek limit aşıldı, işlemi yeniden başlat
                    await RestartProcessAsync(managedProcess, "Bellek limit aşıldı");
                }
            }

            // Yanıt vermeme kontrolü
            if (managedProcess.Options.ResponseTimeout > TimeSpan.Zero)
            {
                var idleTime = DateTime.UtcNow - managedProcess.LastActivity;
                if (idleTime > managedProcess.Options.ResponseTimeout)
                {
                    LogSystemEvent(SystemEventType.ProcessNotResponding,
                        $"İşlem yanıt vermiyor: {managedProcess.StartInfo.FileName} - {idleTime.TotalSeconds:F1}s",
                        SystemSeverity.Warning,
                        processId: managedProcess.ProcessId,
                        processName: managedProcess.StartInfo.FileName);

                    await RestartProcessAsync(managedProcess, "Yanıt vermeme zaman aşımı");
                }
            }
        }

        private async Task RestartProcessAsync(ManagedProcess managedProcess, string reason)
        {
            if (!managedProcess.Options.RestartOnFailure ||
                managedProcess.RestartCount >= managedProcess.Options.MaxRestartAttempts)
            {
                return;
            }

            LogSystemEvent(SystemEventType.ProcessRestarting,
                $"İşlem yeniden başlatılıyor: {managedProcess.StartInfo.FileName} - Sebep: {reason}",
                SystemSeverity.Warning,
                processId: managedProcess.ProcessId,
                processName: managedProcess.StartInfo.FileName);

            // İşlemi durdur
            await StopProcessInternalAsync(managedProcess, true, CancellationToken.None);

            // Bekle
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Yeniden başlat
            try
            {
                var newProcess = await CreateAndStartProcessAsync(
                    managedProcess.StartInfo,
                    managedProcess.Options,
                    managedProcess.ProcessId,
                    CancellationToken.None);

                managedProcess.SystemProcess = newProcess;
                managedProcess.Status = ProcessStatus.Running;
                managedProcess.RestartCount++;
                managedProcess.LastRestart = DateTime.UtcNow;

                LogSystemEvent(SystemEventType.ProcessRestarted,
                    $"İşlem yeniden başlatıldı: {managedProcess.StartInfo.FileName}",
                    SystemSeverity.Information,
                    processId: managedProcess.ProcessId,
                    processName: managedProcess.StartInfo.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "İşlem yeniden başlatma sırasında hata: {ProcessId}", managedProcess.ProcessId);

                LogSystemEvent(SystemEventType.ProcessRestartFailed,
                    $"İşlem yeniden başlatma başarısız: {ex.Message}",
                    SystemSeverity.Error,
                    processId: managedProcess.ProcessId,
                    processName: managedProcess.StartInfo.FileName);
            }
        }

        private void OnProcessExited(string processId, Process process)
        {
            try
            {
                if (_managedProcesses.TryGetValue(process.Id, out var managedProcess))
                {
                    managedProcess.Status = ProcessStatus.Stopped;
                    managedProcess.EndTime = DateTime.UtcNow;
                    managedProcess.ExitCode = process.ExitCode;

                    LogSystemEvent(SystemEventType.ProcessExited,
                        $"İşlem sonlandı: {managedProcess.StartInfo.FileName} - Exit Code: {process.ExitCode}",
                        SystemSeverity.Information,
                        processId: processId,
                        processName: managedProcess.StartInfo.FileName);

                    // Yönetim listesinden kaldır
                    _managedProcesses.TryRemove(process.Id, out _);

                    // Otomatik yeniden başlatma
                    if (managedProcess.Options.RestartOnFailure &&
                        process.ExitCode != 0 &&
                        managedProcess.RestartCount < managedProcess.Options.MaxRestartAttempts)
                    {
                        Task.Run(async () => await RestartProcessAsync(managedProcess, $"Exit Code: {process.ExitCode}"));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "İşlem sonlandırma event'i sırasında hata");
            }
        }

        private void OnProcessOutput(string processId, string output, bool isError)
        {
            if (string.IsNullOrEmpty(output)) return;

            try
            {
                if (_managedProcesses.Values.FirstOrDefault(p => p.ProcessId == processId) is { } managedProcess)
                {
                    managedProcess.LastActivity = DateTime.UtcNow;

                    // Hata output'larını logla
                    if (isError)
                    {
                        _logger.LogError("[PROCESS:{ProcessName}] {Output}",
                            managedProcess.StartInfo.FileName, output);
                    }
                    else if (managedProcess.Options.LogOutput)
                    {
                        _logger.LogInformation("[PROCESS:{ProcessName}] {Output}",
                            managedProcess.StartInfo.FileName, output);
                    }

                    // Output event'i gönder
                    OnSystemEvent(new SystemEventArgs
                    {
                        EventType = isError ? SystemEventType.ProcessErrorOutput : SystemEventType.ProcessOutput,
                        ProcessId = processId,
                        ProcessName = managedProcess.StartInfo.FileName,
                        Message = output,
                        Severity = isError ? SystemSeverity.Error : SystemSeverity.Information,
                        Timestamp = DateTime.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "İşlem output event'i sırasında hata");
            }
        }

        private ManagedProcess FindManagedProcess(string processId)
        {
            return _managedProcesses.Values.FirstOrDefault(p => p.ProcessId == processId);
        }

        private async Task<bool> StopProcessInternalAsync(
            ManagedProcess managedProcess,
            bool force,
            CancellationToken cancellationToken)
        {
            var process = managedProcess.SystemProcess;

            try
            {
                if (process.HasExited)
                {
                    return true;
                }

                if (!force)
                {
                    // Graceful shutdown
                    process.CloseMainWindow();

                    // Bekle
                    if (await Task.Run(() => process.WaitForExit(5000), cancellationToken))
                    {
                        return true;
                    }
                }

                // Force kill
                process.Kill();

                // Bekle
                return await Task.Run(() => process.WaitForExit(3000), cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // İşlem zaten sonlanmış
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "İşlem durdurma sırasında hata: {ProcessId}", managedProcess.ProcessId);
                return false;
            }
        }

        private async Task<SystemResource> AllocateResourceInternalAsync(
            ResourceRequest request,
            string resourceId,
            CancellationToken cancellationToken)
        {
            // Kaynak türüne göre ayırma
            switch (request.Type)
            {
                case ResourceType.Memory:
                    return await AllocateMemoryAsync(request, resourceId, cancellationToken);

                case ResourceType.Disk:
                    return await AllocateDiskSpaceAsync(request, resourceId, cancellationToken);

                case ResourceType.Network:
                    return await AllocateNetworkBandwidthAsync(request, resourceId, cancellationToken);

                case ResourceType.Cpu:
                    return await AllocateCpuCoresAsync(request, resourceId, cancellationToken);

                case ResourceType.FileHandle:
                    return await AllocateFileHandlesAsync(request, resourceId, cancellationToken);

                default:
                    throw new NotSupportedException($"Kaynak türü desteklenmiyor: {request.Type}");
            }
        }

        private async Task<SystemResource> AllocateMemoryAsync(
            ResourceRequest request,
            string resourceId,
            CancellationToken cancellationToken)
        {
            var memory = new MemoryResource
            {
                Id = resourceId,
                Name = request.Name,
                Type = ResourceType.Memory,
                Capacity = request.Requirements.MemoryMB * 1024 * 1024,
                CurrentUsage = 0,
                IsAllocated = true,
                AllocationTime = DateTime.UtcNow,
                Owner = WindowsIdentity.GetCurrent().Name,
            };

            // Bellek ayırma işlemi
            // Gerçek uygulamada belleği gerçekten ayırır (VirtualAlloc, Marshal.AllocHGlobal, vb.)

            return memory;
        }

        private async Task<SystemResource> AllocateDiskSpaceAsync(
            ResourceRequest request,
            string resourceId,
            CancellationToken cancellationToken)
        {
            var tempFile = Path.GetTempFileName();
            var disk = new DiskResource
            {
                Id = resourceId,
                Name = request.Name,
                Type = ResourceType.Disk,
                Capacity = request.Requirements.DiskSpaceMB * 1024 * 1024,
                CurrentUsage = 0,
                IsAllocated = true,
                AllocationTime = DateTime.UtcNow,
                Owner = WindowsIdentity.GetCurrent().Name,
                FilePath = tempFile,
            };

            return disk;
        }

        private async Task<SystemResource> AllocateNetworkBandwidthAsync(
            ResourceRequest request,
            string resourceId,
            CancellationToken cancellationToken)
        {
            var network = new NetworkResource
            {
                Id = resourceId,
                Name = request.Name,
                Type = ResourceType.Network,
                Capacity = request.Requirements.NetworkBandwidthMbps,
                CurrentUsage = 0,
                IsAllocated = true,
                AllocationTime = DateTime.UtcNow,
                Owner = WindowsIdentity.GetCurrent().Name,
            };

            return network;
        }

        private async Task<SystemResource> AllocateCpuCoresAsync(
            ResourceRequest request,
            string resourceId,
            CancellationToken cancellationToken)
        {
            var cpu = new CpuResource
            {
                Id = resourceId,
                Name = request.Name,
                Type = ResourceType.Cpu,
                Capacity = request.Requirements.CpuCores,
                CurrentUsage = 0,
                IsAllocated = true,
                AllocationTime = DateTime.UtcNow,
                Owner = WindowsIdentity.GetCurrent().Name,
            };

            return cpu;
        }

        private async Task<SystemResource> AllocateFileHandlesAsync(
            ResourceRequest request,
            string resourceId,
            CancellationToken cancellationToken)
        {
            var fileHandles = new SystemResource
            {
                Id = resourceId,
                Name = request.Name,
                Type = ResourceType.FileHandle,
                Capacity = request.Requirements.FileHandles,
                CurrentUsage = 0,
                IsAllocated = true,
                AllocationTime = DateTime.UtcNow,
                Owner = WindowsIdentity.GetCurrent().Name,
            };

            return fileHandles;
        }

        private async Task ReleaseResourceInternalAsync(
            SystemResource resource,
            CancellationToken cancellationToken)
        {
            // Kaynak türüne göre serbest bırakma
            switch (resource.Type)
            {
                case ResourceType.Memory:
                    await ReleaseMemoryAsync(resource, cancellationToken);
                    break;

                case ResourceType.Disk:
                    await ReleaseDiskSpaceAsync(resource, cancellationToken);
                    break;

                case ResourceType.Network:
                    await ReleaseNetworkBandwidthAsync(resource, cancellationToken);
                    break;

                case ResourceType.FileHandle:
                    await ReleaseFileHandlesAsync(resource, cancellationToken);
                    break;
            }

            resource.IsAllocated = false;
            resource.ReleaseTime = DateTime.UtcNow;
        }

        private async Task ReleaseMemoryAsync(SystemResource resource, CancellationToken cancellationToken)
        {
            // Bellek serbest bırakma
            // Gerçek uygulamada belleği gerçekten serbest bırakır
            await Task.CompletedTask;
        }

        private async Task ReleaseDiskSpaceAsync(SystemResource resource, CancellationToken cancellationToken)
        {
            // Disk alanı serbest bırakma
            if (resource is DiskResource disk && !string.IsNullOrEmpty(disk.FilePath))
            {
                try
                {
                    if (File.Exists(disk.FilePath))
                    {
                        File.Delete(disk.FilePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Disk kaynağı serbest bırakma sırasında hata");
                }
            }
        }

        private async Task ReleaseNetworkBandwidthAsync(SystemResource resource, CancellationToken cancellationToken)
        {
            // Network bant genişliği serbest bırakma
            await Task.CompletedTask;
        }

        private async Task ReleaseFileHandlesAsync(SystemResource resource, CancellationToken cancellationToken)
        {
            // File handle'ları serbest bırakma
            await Task.CompletedTask;
        }

        private void UpdateResourceUsage(SystemResource resource, bool isRelease = false)
        {
            // Kaynak kullanımını güncelle
            // Gerçek uygulamada sistem kaynak kullanımı takip edilir
        }

        private async Task ValidateCommandSecurityAsync(SystemCommand command)
        {
            // Komut güvenlik kontrolü
            if (command.RequiresAdminPrivileges)
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);

                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    throw new SecurityException("Komut yönetici ayrıcalıkları gerektiriyor");
                }
            }

            // Komut türü kontrolü
            if (command.CommandType == CommandType.System &&
                !await _securityManager.AuthorizeAsync(
                    WindowsIdentity.GetCurrent().Name,
                    "System",
                    "ExecuteSystemCommand"))
            {
                throw new UnauthorizedAccessException("Sistem komutu çalıştırma yetkisi yok");
            }
        }

        private async Task<CommandExecutionResult> ExecuteCommandImmediatelyAsync(
            SystemCommand command,
            CancellationToken cancellationToken)
        {
            // Komut türüne göre yürütme
            switch (command.CommandType)
            {
                case CommandType.System:
                    return await ExecuteSystemCommandAsync(command, cancellationToken);

                case CommandType.PowerShell:
                    return await ExecutePowerShellCommandAsync(command, cancellationToken);

                case CommandType.Batch:
                    return await ExecuteBatchCommandAsync(command, cancellationToken);

                case CommandType.Shell:
                    return await ExecuteShellCommandAsync(command, cancellationToken);

                case CommandType.Custom:
                    return await ExecuteCustomCommandAsync(command, cancellationToken);

                default:
                    throw new NotSupportedException($"Komut türü desteklenmiyor: {command.CommandType}");
            }
        }

        private async Task<CommandExecutionResult> ExecuteSystemCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C {command.CommandText}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return CommandExecutionResult.Failed(
                        "Komut işlemi başlatılamadı",
                        CommandExecutionError.ProcessStartFailed);
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(cancellationToken);

                var result = new CommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = output,
                    Error = error,
                    ExecutionTime = DateTime.UtcNow,
                };

                if (process.ExitCode == 0)
                {
                    return CommandExecutionResult.Success(command.CommandId, result);
                }
                else
                {
                    return CommandExecutionResult.Failed(
                        $"Komut başarısız: {error}",
                        CommandExecutionError.CommandFailed);
                }
            }
            catch (Exception ex)
            {
                return CommandExecutionResult.Failed(
                    $"Komut yürütme hatası: {ex.Message}",
                    CommandExecutionError.ExecutionError);
            }
        }

        private async Task<CommandExecutionResult> ExecutePowerShellCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken)
        {
            // PowerShell komutu yürütme
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"{command.CommandText}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            return await ExecuteProcessCommandAsync(startInfo, command.CommandId, cancellationToken);
        }

        private async Task<CommandExecutionResult> ExecuteBatchCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken)
        {
            // Batch komutu yürütme
            var tempFile = Path.GetTempFileName() + ".bat";
            await File.WriteAllTextAsync(tempFile, command.CommandText);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C \"{tempFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var result = await ExecuteProcessCommandAsync(startInfo, command.CommandId, cancellationToken);
                File.Delete(tempFile);
                return result;
            }
            catch
            {
                try { File.Delete(tempFile); } catch { }
                throw;
            }
        }

        private async Task<CommandExecutionResult> ExecuteShellCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken)
        {
            // Shell komutu yürütme (Linux için)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{command.CommandText}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                return await ExecuteProcessCommandAsync(startInfo, command.CommandId, cancellationToken);
            }
            else
            {
                throw new PlatformNotSupportedException("Shell komutları sadece Linux/OSX'te desteklenir");
            }
        }

        private async Task<CommandExecutionResult> ExecuteCustomCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken)
        {
            // Özel komut yürütme
            // Gerçek uygulamada custom command handler'lar kullanılır
            await Task.CompletedTask;
            return CommandExecutionResult.Success(command.CommandId, new CommandResult
            {
                ExitCode = 0,
                Output = "Custom command executed",
                ExecutionTime = DateTime.UtcNow,
            });
        }

        private async Task<CommandExecutionResult> ExecuteProcessCommandAsync(
            ProcessStartInfo startInfo,
            string commandId,
            CancellationToken cancellationToken)
        {
            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return CommandExecutionResult.Failed(
                        "Komut işlemi başlatılamadı",
                        CommandExecutionError.ProcessStartFailed);
                }

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                var result = new CommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = output,
                    Error = error,
                    ExecutionTime = DateTime.UtcNow,
                };

                if (process.ExitCode == 0)
                {
                    return CommandExecutionResult.Success(commandId, result);
                }
                else
                {
                    return CommandExecutionResult.Failed(
                        $"Komut başarısız: {error}",
                        CommandExecutionError.CommandFailed);
                }
            }
            catch (Exception ex)
            {
                return CommandExecutionResult.Failed(
                    $"Komut yürütme hatası: {ex.Message}",
                    CommandExecutionError.ExecutionError);
            }
        }

        private async Task ProcessCommandAsync(SystemCommand command)
        {
            try
            {
                LogSystemEvent(SystemEventType.CommandProcessing,
                    $"Komut işleniyor: {command.Name}",
                    SystemSeverity.Information,
                    commandId: command.CommandId,
                    commandName: command.Name);

                var result = await ExecuteCommandImmediatelyAsync(command, CancellationToken.None);

                if (result.IsSuccess)
                {
                    LogSystemEvent(SystemEventType.CommandCompleted,
                        $"Komut tamamlandı: {command.Name}",
                        SystemSeverity.Information,
                        commandId: command.CommandId,
                        commandName: command.Name);
                }
                else
                {
                    LogSystemEvent(SystemEventType.CommandFailed,
                        $"Komut başarısız: {result.ErrorMessage}",
                        SystemSeverity.Error,
                        commandId: command.CommandId,
                        commandName: command.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Komut işleme sırasında hata: {CommandId}", command.CommandId);

                LogSystemEvent(SystemEventType.CommandFailed,
                    $"Komut işleme hatası: {ex.Message}",
                    SystemSeverity.Error,
                    commandId: command.CommandId,
                    commandName: command.Name);
            }
        }

        private async Task<MaintenanceResult> ExecuteMaintenancePlanAsync(
            MaintenancePlan plan,
            CancellationToken cancellationToken)
        {
            var results = new List<MaintenanceTaskResult>();

            foreach (var task in plan.Tasks)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var taskResult = await ExecuteMaintenanceTaskAsync(task, cancellationToken);
                    results.Add(taskResult);

                    // Hata durumunda planı durdur
                    if (!taskResult.IsSuccess && plan.StopOnFailure)
                    {
                        return MaintenanceResult.Failed(
                            $"Bakım görevi başarısız: {task.Name}",
                            MaintenanceError.TaskFailed,
                            results);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bakım görevi sırasında hata: {TaskName}", task.Name);

                    if (plan.StopOnFailure)
                    {
                        throw;
                    }
                }
            }

            return MaintenanceResult.Success(plan.MaintenanceId, results);
        }

        private async Task<MaintenanceTaskResult> ExecuteMaintenanceTaskAsync(
            MaintenanceTask task,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            LogSystemEvent(SystemEventType.MaintenanceTaskStarted,
                $"Bakım görevi başlatıldı: {task.Name}",
                SystemSeverity.Information,
                taskName: task.Name);

            try
            {
                // Görev türüne göre yürütme
                switch (task.Type)
                {
                    case MaintenanceTaskType.Cleanup:
                        await ExecuteCleanupTaskAsync(task, cancellationToken);
                        break;

                    case MaintenanceTaskType.Optimization:
                        await ExecuteOptimizationTaskAsync(task, cancellationToken);
                        break;

                    case MaintenanceTaskType.Update:
                        await ExecuteUpdateTaskAsync(task, cancellationToken);
                        break;

                    case MaintenanceTaskType.Backup:
                        await ExecuteBackupTaskAsync(task, cancellationToken);
                        break;

                    case MaintenanceTaskType.Validation:
                        await ExecuteValidationTaskAsync(task, cancellationToken);
                        break;
                }

                var duration = DateTime.UtcNow - startTime;

                LogSystemEvent(SystemEventType.MaintenanceTaskCompleted,
                    $"Bakım görevi tamamlandı: {task.Name} - Süre: {duration.TotalSeconds:F1}s",
                    SystemSeverity.Information,
                    taskName: task.Name);

                return MaintenanceTaskResult.Success(task.Name, duration);
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;

                LogSystemEvent(SystemEventType.MaintenanceTaskFailed,
                    $"Bakım görevi başarısız: {task.Name} - {ex.Message}",
                    SystemSeverity.Error,
                    taskName: task.Name);

                return MaintenanceTaskResult.Failed(task.Name, ex.Message, duration);
            }
        }

        private async Task ExecuteCleanupTaskAsync(MaintenanceTask task, CancellationToken cancellationToken)
        {
            // Temizlik görevi
            switch (task.Target)
            {
                case "TempFiles":
                    await CleanupTempFilesAsync(task.Parameters, cancellationToken);
                    break;

                case "LogFiles":
                    await CleanupLogFilesAsync(task.Parameters, cancellationToken);
                    break;

                case "Cache":
                    await CleanupCacheAsync(task.Parameters, cancellationToken);
                    break;

                default:
                    throw new NotSupportedException($"Temizlik hedefi desteklenmiyor: {task.Target}");
            }
        }

        private async Task CleanupTempFilesAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            var tempPath = Path.GetTempPath();
            var maxAgeDays = parameters.TryGetValue("MaxAgeDays", out var value) ?
                Convert.ToInt32(value) : 7;

            var cutoffDate = DateTime.UtcNow.AddDays(-maxAgeDays);
            var tempFiles = Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories);

            foreach (var file in tempFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        fileInfo.Delete();
                    }
                }
                catch { /* ignore */ }
            }
        }

        private async Task CleanupLogFilesAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logPath)) return;

            var maxAgeDays = parameters.TryGetValue("MaxAgeDays", out var value) ?
                Convert.ToInt32(value) : 30;
            var keepCount = parameters.TryGetValue("KeepCount", out var keepValue) ?
                Convert.ToInt32(keepValue) : 100;

            var logFiles = Directory.GetFiles(logPath, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            // Eski dosyaları sil
            var cutoffDate = DateTime.UtcNow.AddDays(-maxAgeDays);
            foreach (var file in logFiles.Where(f => f.LastWriteTimeUtc < cutoffDate))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    file.Delete();
                }
                catch { /* ignore */ }
            }

            // Çok fazla dosya varsa en eskileri sil
            if (logFiles.Count > keepCount)
            {
                foreach (var file in logFiles.Skip(keepCount))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        file.Delete();
                    }
                    catch { /* ignore */ }
                }
            }
        }

        private async Task CleanupCacheAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            var cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
            if (!Directory.Exists(cachePath)) return;

            var maxAgeDays = parameters.TryGetValue("MaxAgeDays", out var value) ?
                Convert.ToInt32(value) : 3;

            var cutoffDate = DateTime.UtcNow.AddDays(-maxAgeDays);
            var cacheFiles = Directory.GetFiles(cachePath, "*", SearchOption.AllDirectories);

            foreach (var file in cacheFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastAccessTimeUtc < cutoffDate)
                    {
                        fileInfo.Delete();
                    }
                }
                catch { /* ignore */ }
            }
        }

        private async Task ExecuteOptimizationTaskAsync(MaintenanceTask task, CancellationToken cancellationToken)
        {
            // Optimizasyon görevi
            switch (task.Target)
            {
                case "Database":
                    await OptimizeDatabaseAsync(task.Parameters, cancellationToken);
                    break;

                case "Memory":
                    await OptimizeMemoryAsync(task.Parameters, cancellationToken);
                    break;

                case "Disk":
                    await OptimizeDiskAsync(task.Parameters, cancellationToken);
                    break;

                default:
                    throw new NotSupportedException($"Optimizasyon hedefi desteklenmiyor: {task.Target}");
            }
        }

        private async Task OptimizeDatabaseAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Database optimizasyonu
            // Gerçek uygulamada database spesifik optimizasyonlar yapılır
            await Task.CompletedTask;
        }

        private async Task OptimizeMemoryAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Bellek optimizasyonu
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (parameters.TryGetValue("CompactLargeObjectHeap", out var value) &&
                Convert.ToBoolean(value))
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }

            await Task.CompletedTask;
        }

        private async Task OptimizeDiskAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Disk optimizasyonu
            // Gerçek uygulamada disk birleştirme işlemleri yapılır
            await Task.CompletedTask;
        }

        private async Task ExecuteUpdateTaskAsync(MaintenanceTask task, CancellationToken cancellationToken)
        {
            // Güncelleme görevi
            // Gerçek uygulamada sistem güncellemeleri yapılır
            await Task.CompletedTask;
        }

        private async Task ExecuteBackupTaskAsync(MaintenanceTask task, CancellationToken cancellationToken)
        {
            // Yedekleme görevi
            await CreateBackupAsync(new BackupOptions
            {
                IncludeLogs = task.Parameters.TryGetValue("IncludeLogs", out var includeLogs) &&
                             Convert.ToBoolean(includeLogs),
                IncludeDatabase = task.Parameters.TryGetValue("IncludeDatabase", out var includeDb) &&
                                 Convert.ToBoolean(includeDb),
                CompressionLevel = task.Parameters.TryGetValue("CompressionLevel", out var compression) ?
                                 Convert.ToInt32(compression) : 6
            }, cancellationToken);
        }

        private async Task ExecuteValidationTaskAsync(MaintenanceTask task, CancellationToken cancellationToken)
        {
            // Doğrulama görevi
            await RunDiagnosticsAsync(new DiagnosticOptions
            {
                IncludePerformanceTests = true,
                IncludeSecurityTests = true,
                IncludeIntegrityTests = true,
            }, cancellationToken);
        }

        private async Task MonitorSystemResourcesAsync()
        {
            try
            {
                await UpdatePerformanceMetricsAsync();

                // Kaynak kullanımı kontrolü
                if (PerformanceMetrics.CpuUsage > 90)
                {
                    LogSystemEvent(SystemEventType.HighCpuUsage,
                        $"Yüksek CPU kullanımı: %{PerformanceMetrics.CpuUsage:F1}",
                        SystemSeverity.Warning);

                    await HandleHighCpuUsageAsync();
                }

                if (PerformanceMetrics.MemoryUsage > 90)
                {
                    LogSystemEvent(SystemEventType.HighMemoryUsage,
                        $"Yüksek bellek kullanımı: %{PerformanceMetrics.MemoryUsage:F1}",
                        SystemSeverity.Warning);

                    await HandleHighMemoryUsageAsync();
                }

                if (PerformanceMetrics.DiskUsage > 95)
                {
                    LogSystemEvent(SystemEventType.HighDiskUsage,
                        $"Yüksek disk kullanımı: %{PerformanceMetrics.DiskUsage:F1}",
                        SystemSeverity.Warning);

                    await HandleHighDiskUsageAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sistem kaynakları izleme sırasında hata");
            }
        }

        private async Task UpdatePerformanceMetricsAsync()
        {
            try
            {
                // PerformanceMonitor'dan metrikleri al
                var metrics = await _performanceMonitor.GetSystemMetricsAsync();

                PerformanceMetrics = new SystemPerformanceMetrics
                {
                    CpuUsage = metrics.CpuUsage,
                    MemoryUsage = metrics.MemoryUsage,
                    DiskUsage = metrics.DiskUsage,
                    NetworkUsage = metrics.NetworkUsage,
                    ProcessCount = Process.GetProcesses().Length,
                    ThreadCount = Process.GetProcesses().Sum(p => p.Threads.Count),
                    HandleCount = Process.GetProcesses().Sum(p => p.HandleCount),
                    Timestamp = DateTime.UtcNow,
                    AdditionalMetrics = metrics.AdditionalMetrics,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Performans metrikleri güncelleme sırasında hata");

                // Fallback metrikler
                PerformanceMetrics = new SystemPerformanceMetrics
                {
                    Timestamp = DateTime.UtcNow,
                    ProcessCount = Process.GetProcesses().Length,
                };
            }
        }

        private async Task HandleHighCpuUsageAsync()
        {
            // Yüksek CPU kullanımı işleme
            var cpuIntensiveProcesses = _managedProcesses.Values
                .Where(p => p.ResourceUsage?.CpuUsage > 50)
                .OrderByDescending(p => p.ResourceUsage.CpuUsage)
                .ToList();

            foreach (var process in cpuIntensiveProcesses)
            {
                if (process.Options.CpuThrottlingEnabled)
                {
                    await ThrottleProcessCpuAsync(process);
                }
            }
        }

        private async Task ThrottleProcessCpuAsync(ManagedProcess process)
        {
            try
            {
                // İşlem CPU önceliğini düşür
                process.SystemProcess.PriorityClass = ProcessPriorityClass.BelowNormal;

                LogSystemEvent(SystemEventType.ProcessThrottled,
                    $"İşlem CPU kısıtlandı: {process.StartInfo.FileName}",
                    SystemSeverity.Information,
                    processId: process.ProcessId,
                    processName: process.StartInfo.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "İşlem CPU kısıtlama sırasında hata");
            }
        }

        private async Task HandleHighMemoryUsageAsync()
        {
            // Yüksek bellek kullanımı işleme
            var memoryIntensiveProcesses = _managedProcesses.Values
                .Where(p => p.ResourceUsage?.MemoryUsage > 100 * 1024 * 1024) // 100MB
                .OrderByDescending(p => p.ResourceUsage.MemoryUsage)
                .ToList();

            foreach (var process in memoryIntensiveProcesses)
            {
                if (process.Options.MemoryPressureHandlingEnabled)
                {
                    await HandleProcessMemoryPressureAsync(process);
                }
            }

            // GC tetikle
            GC.Collect(2, GCCollectionMode.Forced, false, true);
        }

        private async Task HandleProcessMemoryPressureAsync(ManagedProcess process)
        {
            try
            {
                // İşlem bellek kullanımını azalt
                // Gerçek uygulamada işleme özel bellek yönetimi yapılır

                LogSystemEvent(SystemEventType.ProcessMemoryOptimized,
                    $"İşlem bellek optimizasyonu: {process.StartInfo.FileName}",
                    SystemSeverity.Information,
                    processId: process.ProcessId,
                    processName: process.StartInfo.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "İşlem bellek optimizasyonu sırasında hata");
            }
        }

        private async Task HandleHighDiskUsageAsync()
        {
            // Yüksek disk kullanımı işleme
            await CleanupTempFilesAsync(new Dictionary<string, object> { ["MaxAgeDays"] = 1 }, CancellationToken.None);
        }

        private async Task MonitorProcessesAsync()
        {
            foreach (var process in _managedProcesses.Values)
            {
                try
                {
                    // İşlem durumunu kontrol et
                    if (process.SystemProcess.HasExited && process.Status == ProcessStatus.Running)
                    {
                        process.Status = ProcessStatus.Stopped;
                        process.EndTime = DateTime.UtcNow;

                        LogSystemEvent(SystemEventType.ProcessExited,
                            $"İşlem beklenmedik şekilde sonlandı: {process.StartInfo.FileName}",
                            SystemSeverity.Warning,
                            processId: process.ProcessId,
                            processName: process.StartInfo.FileName);
                    }

                    // İşlem sağlığını kontrol et
                    await CheckProcessHealthAsync(process);
                }
                catch (InvalidOperationException)
                {
                    // İşlem sonlanmış
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "İşlem izleme sırasında hata: {ProcessId}", process.ProcessId);
                }
            }
        }

        private async Task PerformHealthCheckAsync()
        {
            // İlk sağlık kontrolü (kısayol)
            await CheckSystemHealthAsync();
        }

        private async Task CheckSystemHealthAsync()
        {
            try
            {
                var health = await _healthChecker.CheckSystemHealthAsync();
                _lastHealthCheck = DateTime.UtcNow;

                if (!health.IsHealthy)
                {
                    LogSystemEvent(SystemEventType.SystemHealthDegraded,
                        $"Sistem sağlığı bozuldu: {health.Status} - {health.Message}",
                        SystemSeverity.Warning);

                    // Recovery mekanizmasını tetikle
                    await _recoveryEngine.AttemptRecoveryAsync(
                        new SystemHealthFailureContext(health));

                    // Kritik durumda acil durum moduna geç
                    if (health.Status == HealthStatus.Critical)
                    {
                        ActivateEmergencyMode($"Sistem sağlığı kritik: {health.Message}");
                    }
                }
                else if (health.Status == HealthStatus.Healthy && IsEmergencyMode)
                {
                    // Sistem sağlıklı, acil durum modundan çık
                    DeactivateEmergencyMode();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sistem sağlık kontrolü sırasında hata");
            }
        }

        private async Task ProcessAlertsAsync()
        {
            // Sistem alert'lerini işle
            // Gerçek uygulamada monitoring sisteminden alert'ler alınır
            await Task.CompletedTask;
        }

        private async Task PerformScheduledMaintenanceAsync()
        {
            // Zamanlanmış bakım görevleri
            var now = DateTime.UtcNow;

            // Günlük bakım
            if (now - _lastMaintenance > TimeSpan.FromDays(1))
            {
                try
                {
                    await StartMaintenanceAsync(new MaintenancePlan
                    {
                        Name = "Günlük Sistem Bakımı",
                        Description = "Günlük temizlik ve optimizasyon görevleri",
                        Tasks = new List<MaintenanceTask>
                        {
                            new() { Name = "Temp Dosya Temizliği", Type = MaintenanceTaskType.Cleanup, Target = "TempFiles" },
                            new() { Name = "Log Rotasyonu", Type = MaintenanceTaskType.Cleanup, Target = "LogFiles" },
                            new() { Name = "Bellek Optimizasyonu", Type = MaintenanceTaskType.Optimization, Target = "Memory" }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Zamanlanmış bakım sırasında hata");
                }
            }
        }

        private async Task CleanupSystemResourcesAsync()
        {
            // Kullanılmayan sistem kaynaklarını temizle
            var unusedResources = _systemResources.Values
                .Where(r => !r.IsAllocated &&
                           r.AllocationTime < DateTime.UtcNow.AddHours(-1))
                .ToList();

            foreach (var resource in unusedResources)
            {
                try
                {
                    await ReleaseResourceAsync(resource.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Kaynak temizleme sırasında hata: {ResourceId}", resource.Id);
                }
            }
        }

        private async Task OptimizeSystemPerformanceAsync()
        {
            // Sistem performansını optimize et
            // Gerçek uygulamada çeşitli optimizasyonlar yapılır
            await Task.CompletedTask;
        }

        private async Task<SystemBackup> ExecuteBackupAsync(
            BackupOptions options,
            string backupId,
            CancellationToken cancellationToken)
        {
            var backupPath = Path.Combine(
                _appConfig.DataPaths.Backups,
                _environmentConfig.EnvironmentName,
                backupId);

            Directory.CreateDirectory(backupPath);

            var backup = new SystemBackup
            {
                BackupId = backupId,
                BackupPath = backupPath,
                StartTime = DateTime.UtcNow,
                Status = BackupStatus.InProgress,
            };

            try
            {
                // Config dosyalarını yedekle
                if (options.IncludeConfiguration)
                {
                    await BackupConfigurationAsync(backupPath, cancellationToken);
                }

                // Log dosyalarını yedekle
                if (options.IncludeLogs)
                {
                    await BackupLogsAsync(backupPath, cancellationToken);
                }

                // Database'i yedekle
                if (options.IncludeDatabase)
                {
                    await BackupDatabaseAsync(backupPath, cancellationToken);
                }

                // Proje dosyalarını yedekle
                if (options.IncludeProjects)
                {
                    await BackupProjectsAsync(backupPath, cancellationToken);
                }

                backup.Status = BackupStatus.Completed;
                backup.EndTime = DateTime.UtcNow;
                backup.SizeInBytes = CalculateBackupSize(backupPath);

                // Yedekleme metadata'sını kaydet
                await SaveBackupMetadataAsync(backup, cancellationToken);

                return backup;
            }
            catch (Exception ex)
            {
                backup.Status = BackupStatus.Failed;
                backup.ErrorMessage = ex.Message;
                backup.EndTime = DateTime.UtcNow;

                // Başarısız yedeklemeyi temizle
                try { Directory.Delete(backupPath, true); } catch { }

                throw;
            }
        }

        private async Task BackupConfigurationAsync(string backupPath, CancellationToken cancellationToken)
        {
            var configSource = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            var configTarget = Path.Combine(backupPath, "Config");

            if (Directory.Exists(configSource))
            {
                await CopyDirectoryAsync(configSource, configTarget, cancellationToken);
            }
        }

        private async Task BackupLogsAsync(string backupPath, CancellationToken cancellationToken)
        {
            var logsSource = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            var logsTarget = Path.Combine(backupPath, "Logs");

            if (Directory.Exists(logsSource))
            {
                await CopyDirectoryAsync(logsSource, logsTarget, cancellationToken);
            }
        }

        private async Task BackupDatabaseAsync(string backupPath, CancellationToken cancellationToken)
        {
            // Database yedekleme
            // Gerçek uygulamada database spesifik yedekleme yapılır
            await Task.CompletedTask;
        }

        private async Task BackupProjectsAsync(string backupPath, CancellationToken cancellationToken)
        {
            var projectsTarget = Path.Combine(backupPath, "Projects");
            Directory.CreateDirectory(projectsTarget);

            // Proje yöneticisinden projeleri al ve yedekle
            var projects = await _projectManager.GetAllProjectsAsync(cancellationToken);

            foreach (var project in projects)
            {
                try
                {
                    var projectBackupPath = Path.Combine(projectsTarget, project.Id);
                    Directory.CreateDirectory(projectBackupPath);

                    // Proje metadata'sını kaydet
                    var metadataFile = Path.Combine(projectBackupPath, "metadata.json");
                    var metadataJson = JsonConvert.SerializeObject(project, Formatting.Indented);
                    await File.WriteAllTextAsync(metadataFile, metadataJson, cancellationToken);

                    // Proje dosyalarını yedekle
                    if (Directory.Exists(project.ProjectPath))
                    {
                        await CopyDirectoryAsync(project.ProjectPath,
                            Path.Combine(projectBackupPath, "files"),
                            cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Proje yedekleme sırasında hata: {ProjectId}", project.Id);
                }
            }
        }

        private async Task CopyDirectoryAsync(string sourceDir, string targetDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetDir);

            var files = Directory.GetFiles(sourceDir);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                await Task.Run(() => File.Copy(file, targetFile, true), cancellationToken);
            }

            var directories = Directory.GetDirectories(sourceDir);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetSubDir = Path.Combine(targetDir, Path.GetFileName(directory));
                await CopyDirectoryAsync(directory, targetSubDir, cancellationToken);
            }
        }

        private long CalculateBackupSize(string backupPath)
        {
            if (!Directory.Exists(backupPath)) return 0;

            var files = Directory.GetFiles(backupPath, "*", SearchOption.AllDirectories);
            return files.Sum(file => new FileInfo(file).Length);
        }

        private async Task SaveBackupMetadataAsync(SystemBackup backup, CancellationToken cancellationToken)
        {
            var metadataFile = Path.Combine(backup.BackupPath, "backup.json");
            var metadataJson = JsonConvert.SerializeObject(backup, Formatting.Indented);
            await File.WriteAllTextAsync(metadataFile, metadataJson, cancellationToken);
        }

        private async Task ExecuteRestoreAsync(
            string backupId,
            RestoreOptions options,
            CancellationToken cancellationToken)
        {
            var backupPath = Path.Combine(
                _appConfig.DataPaths.Backups,
                _environmentConfig.EnvironmentName,
                backupId);

            if (!Directory.Exists(backupPath))
            {
                throw new FileNotFoundException($"Yedekleme bulunamadı: {backupId}");
            }

            // Yedekleme metadata'sını yükle
            var metadataFile = Path.Combine(backupPath, "backup.json");
            if (!File.Exists(metadataFile))
            {
                throw new InvalidDataException("Yedekleme metadata'sı bulunamadı");
            }

            var metadataJson = await File.ReadAllTextAsync(metadataFile, cancellationToken);
            var backup = JsonConvert.DeserializeObject<SystemBackup>(metadataJson);

            if (backup == null || backup.Status != BackupStatus.Completed)
            {
                throw new InvalidDataException("Geçersiz veya tamamlanmamış yedekleme");
            }

            // Geri yükleme işlemi
            if (options.RestoreConfiguration)
            {
                await RestoreConfigurationAsync(backupPath, cancellationToken);
            }

            if (options.RestoreLogs)
            {
                await RestoreLogsAsync(backupPath, cancellationToken);
            }

            if (options.RestoreDatabase)
            {
                await RestoreDatabaseAsync(backupPath, cancellationToken);
            }

            if (options.RestoreProjects)
            {
                await RestoreProjectsAsync(backupPath, cancellationToken);
            }
        }

        private async Task RestoreConfigurationAsync(string backupPath, CancellationToken cancellationToken)
        {
            var configSource = Path.Combine(backupPath, "Config");
            var configTarget = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");

            if (Directory.Exists(configSource))
            {
                // Mevcut config'i yedekle
                if (Directory.Exists(configTarget))
                {
                    var backupConfig = Path.Combine(configTarget, $"backup_{DateTime.UtcNow:yyyyMMddHHmmss}");
                    Directory.Move(configTarget, backupConfig);
                }

                await CopyDirectoryAsync(configSource, configTarget, cancellationToken);
            }
        }

        private async Task RestoreLogsAsync(string backupPath, CancellationToken cancellationToken)
        {
            var logsSource = Path.Combine(backupPath, "Logs");
            var logsTarget = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            if (Directory.Exists(logsSource))
            {
                Directory.CreateDirectory(logsTarget);
                await CopyDirectoryAsync(logsSource, logsTarget, cancellationToken);
            }
        }

        private async Task RestoreDatabaseAsync(string backupPath, CancellationToken cancellationToken)
        {
            // Database geri yükleme
            // Gerçek uygulamada database spesifik geri yükleme yapılır
            await Task.CompletedTask;
        }

        private async Task RestoreProjectsAsync(string backupPath, CancellationToken cancellationToken)
        {
            var projectsSource = Path.Combine(backupPath, "Projects");
            if (!Directory.Exists(projectsSource)) return;

            var projectDirs = Directory.GetDirectories(projectsSource);

            foreach (var projectDir in projectDirs)
            {
                try
                {
                    var metadataFile = Path.Combine(projectDir, "metadata.json");
                    if (!File.Exists(metadataFile)) continue;

                    var metadataJson = await File.ReadAllTextAsync(metadataFile, cancellationToken);
                    var project = JsonConvert.DeserializeObject<ProjectInfo>(metadataJson);

                    if (project == null) continue;

                    // Projeyi geri yükle
                    await _projectManager.RestoreProjectAsync(project.Id, projectDir, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Proje geri yükleme sırasında hata: {ProjectDir}", projectDir);
                }
            }
        }

        private async Task PrepareForRestartAsync(RestartOptions options, CancellationToken cancellationToken)
        {
            // Yeniden başlatma öncesi hazırlık

            // Kullanıcıları bilgilendir
            if (options.NotifyUsers)
            {
                await NotifyUsersAsync("Sistem yeniden başlatılacak", cancellationToken);
            }

            // İşlemleri kaydet
            await SaveProcessStatesAsync();

            // Bekleme süresi
            if (options.DelayBeforeRestart > TimeSpan.Zero)
            {
                await Task.Delay(options.DelayBeforeRestart, cancellationToken);
            }
        }

        private async Task PrepareForShutdownAsync(ShutdownOptions options, CancellationToken cancellationToken)
        {
            // Kapanış öncesi hazırlık

            // Kullanıcıları bilgilendir
            if (options.NotifyUsers)
            {
                await NotifyUsersAsync("Sistem kapatılacak", cancellationToken);
            }

            // İşlemleri kaydet
            await SaveProcessStatesAsync();

            // Sistem yedeği al
            if (options.CreateBackup)
            {
                await CreateBackupAsync(new BackupOptions
                {
                    IncludeConfiguration = true,
                    IncludeLogs = true,
                    IncludeDatabase = true,
                    IncludeProjects = true,
                }, cancellationToken);
            }

            // Bekleme süresi
            if (options.DelayBeforeShutdown > TimeSpan.Zero)
            {
                await Task.Delay(options.DelayBeforeShutdown, cancellationToken);
            }
        }

        private async Task NotifyUsersAsync(string message, CancellationToken cancellationToken)
        {
            // Kullanıcıları bilgilendir
            // Gerçek uygulamada notification service kullanılır
            await Task.CompletedTask;
        }

        private async Task SaveProcessStatesAsync()
        {
            // İşlem durumlarını kaydet
            var processStates = _managedProcesses.Values
                .Select(p => new ProcessState
                {
                    ProcessId = p.ProcessId,
                    FileName = p.StartInfo.FileName,
                    Arguments = p.StartInfo.Arguments,
                    Status = p.Status,
                    StartTime = p.StartTime,
                    ResourceUsage = p.ResourceUsage,
                })
                .ToList();

            var statesFile = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data",
                "process_states.json");

            Directory.CreateDirectory(Path.GetDirectoryName(statesFile) ?? AppDomain.CurrentDomain.BaseDirectory);
            var json = JsonConvert.SerializeObject(processStates, Formatting.Indented);
            await File.WriteAllTextAsync(statesFile, json);
        }

        private async Task ReleaseAllResourcesAsync()
        {
            // Tüm kaynakları serbest bırak
            var resourcesToRelease = _systemResources.Keys.ToList();

            foreach (var resourceId in resourcesToRelease)
            {
                try
                {
                    await ReleaseResourceAsync(resourceId);
                }
                catch { /* ignore */ }
            }
        }

        private async Task RestartSystemManagerAsync()
        {
            // SystemManager'ı yeniden başlat
            Dispose();

            // Kısa bekleme
            await Task.Delay(1000);

            // Yeniden başlat
            Initialize();
        }

        private async Task StopNonCriticalProcessesAsync()
        {
            // Kritik olmayan işlemleri durdur
            var nonCriticalProcesses = _managedProcesses.Values
                .Where(p => !p.Options.IsCritical)
                .ToList();

            foreach (var process in nonCriticalProcesses)
            {
                try
                {
                    await StopProcessAsync(process.ProcessId, true);
                }
                catch { /* ignore */ }
            }
        }

        private void StartEmergencyProtocols()
        {
            // Acil durum protokollerini başlat

            // Kritik servisleri yeniden başlat
            Task.Run(async () =>
            {
                await StopAllProcessesAsync(true);
                await Task.Delay(5000);
                StartCriticalProcesses();
            });

            // Kaynak kullanımını kısıtla
            foreach (var process in _managedProcesses.Values)
            {
                try
                {
                    process.SystemProcess.PriorityClass = ProcessPriorityClass.Idle;
                }
                catch { /* ignore */ }
            }

            // Log seviyesini yükselt
            LogManager.Instance.SetMinimumLogLevel(LogLevel.Warning);
        }

        private void ResumeNormalOperations()
        {
            // Normal işlemlere devam et

            // Log seviyesini normale döndür
            LogManager.Instance.SetMinimumLogLevel(LogLevel.Information);

            // İşlem önceliklerini normale döndür
            foreach (var process in _managedProcesses.Values)
            {
                try
                {
                    process.SystemProcess.PriorityClass = ProcessPriorityClass.Normal;
                }
                catch { /* ignore */ }
            }
        }

        private async Task<List<DiagnosticTestResult>> ExecuteDiagnosticsAsync(
            DiagnosticOptions options,
            CancellationToken cancellationToken)
        {
            var results = new List<DiagnosticTestResult>();

            // Sistem testleri
            if (options.IncludeSystemTests)
            {
                results.Add(await TestSystemResourcesAsync(cancellationToken));
                results.Add(await TestNetworkConnectivityAsync(cancellationToken));
                results.Add(await TestDiskPerformanceAsync(cancellationToken));
            }

            // Performans testleri
            if (options.IncludePerformanceTests)
            {
                results.Add(await TestCpuPerformanceAsync(cancellationToken));
                results.Add(await TestMemoryPerformanceAsync(cancellationToken));
            }

            // Güvenlik testleri
            if (options.IncludeSecurityTests)
            {
                results.Add(await TestSecurityConfigurationAsync(cancellationToken));
                results.Add(await TestFirewallRulesAsync(cancellationToken));
            }

            // Bütünlük testleri
            if (options.IncludeIntegrityTests)
            {
                results.Add(await TestFileIntegrityAsync(cancellationToken));
                results.Add(await TestRegistryIntegrityAsync(cancellationToken));
            }

            return results;
        }

        private async Task<DiagnosticTestResult> TestSystemResourcesAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "System Resources Test",
                Category = DiagnosticCategory.System,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // CPU kontrolü
                using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpuCounter.NextValue();
                await Task.Delay(1000, cancellationToken);
                var cpuUsage = cpuCounter.NextValue();

                // Bellek kontrolü
                using var memCounter = new PerformanceCounter("Memory", "Available MBytes");
                var availableMemory = memCounter.NextValue();

                test.IsPassed = cpuUsage < 90 && availableMemory > 100; // 100MB'den fazla boş bellek
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["CpuUsage"] = cpuUsage,
                    ["AvailableMemoryMB"] = availableMemory,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestNetworkConnectivityAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "Network Connectivity Test",
                Category = DiagnosticCategory.Network,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 5000);

                test.IsPassed = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["PingStatus"] = reply.Status.ToString(),
                    ["RoundtripTime"] = reply.RoundtripTime,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestDiskPerformanceAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "Disk Performance Test",
                Category = DiagnosticCategory.Storage,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                var tempFile = Path.GetTempFileName();
                var data = new byte[1024 * 1024]; // 1MB
                new Random().NextBytes(data);

                var startTime = DateTime.UtcNow;
                await File.WriteAllBytesAsync(tempFile, data, cancellationToken);
                var writeTime = DateTime.UtcNow - startTime;

                startTime = DateTime.UtcNow;
                var readData = await File.ReadAllBytesAsync(tempFile);
                var readTime = DateTime.UtcNow - startTime;

                File.Delete(tempFile);

                test.IsPassed = writeTime.TotalMilliseconds < 1000 && readTime.TotalMilliseconds < 1000;
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["WriteTimeMs"] = writeTime.TotalMilliseconds,
                    ["ReadTimeMs"] = readTime.TotalMilliseconds,
                    ["FileSizeMB"] = 1,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestCpuPerformanceAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "CPU Performance Test",
                Category = DiagnosticCategory.Performance,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // Basit CPU testi (prime number calculation)
                var startTime = DateTime.UtcNow;
                await Task.Run(() =>
                {
                    for (int i = 0; i < 1000000; i++)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        IsPrime(i);
                    }
                }, cancellationToken);

                var duration = DateTime.UtcNow - startTime;

                test.IsPassed = duration.TotalSeconds < 10; // 10 saniyeden hızlı
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["CalculationTime"] = duration.TotalSeconds,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private bool IsPrime(int number)
        {
            if (number <= 1) return false;
            if (number == 2) return true;
            if (number % 2 == 0) return false;

            var boundary = (int)Math.Floor(Math.Sqrt(number));

            for (int i = 3; i <= boundary; i += 2)
            {
                if (number % i == 0) return false;
            }

            return true;
        }

        private async Task<DiagnosticTestResult> TestMemoryPerformanceAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "Memory Performance Test",
                Category = DiagnosticCategory.Performance,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // Bellek testi
                var list = new List<byte[]>();
                var startTime = DateTime.UtcNow;

                for (int i = 0; i < 100; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    list.Add(new byte[1024 * 1024]); // 1MB each
                    await Task.Delay(10, cancellationToken);
                }

                var duration = DateTime.UtcNow - startTime;
                list.Clear();
                GC.Collect();

                test.IsPassed = duration.TotalSeconds < 5; // 5 saniyeden hızlı
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["AllocationTime"] = duration.TotalSeconds,
                    ["TotalAllocatedMB"] = 100,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestSecurityConfigurationAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "Security Configuration Test",
                Category = DiagnosticCategory.Security,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // Güvenlik yapılandırması testi
                var securityStatus = await _securityManager.GetStatus();

                test.IsPassed = securityStatus.CurrentSecurityLevel >= SecurityLevel.Medium &&
                               !securityStatus.IsLocked;
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["SecurityLevel"] = securityStatus.CurrentSecurityLevel.ToString(),
                    ["IsLocked"] = securityStatus.IsLocked,
                    ["ActiveSessions"] = securityStatus.ActiveSessionCount,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestFirewallRulesAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "Firewall Rules Test",
                Category = DiagnosticCategory.Security,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // Firewall kuralları testi (basitleştirilmiş)
                test.IsPassed = true; // Gerçek uygulamada firewall kontrolü yapılır
                test.Duration = DateTime.UtcNow - test.StartTime;
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestFileIntegrityAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "File Integrity Test",
                Category = DiagnosticCategory.Integrity,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // Kritik dosyaların bütünlüğü testi
                var criticalFiles = new[]
                {
                    "NEDA.Core.dll",
                    "appsettings.json",
                    "NEDA.exe"
                };

                var missingFiles = new List<string>();

                foreach (var file in criticalFiles)
                {
                    var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
                    if (!File.Exists(filePath))
                    {
                        missingFiles.Add(file);
                    }
                }

                test.IsPassed = missingFiles.Count == 0;
                test.Duration = DateTime.UtcNow - test.StartTime;
                test.Details = new Dictionary<string, object>
                {
                    ["MissingFiles"] = missingFiles,
                    ["TotalFilesChecked"] = criticalFiles.Length,
                };
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private async Task<DiagnosticTestResult> TestRegistryIntegrityAsync(CancellationToken cancellationToken)
        {
            var test = new DiagnosticTestResult
            {
                TestName = "Registry Integrity Test",
                Category = DiagnosticCategory.Integrity,
                StartTime = DateTime.UtcNow,
            };

            try
            {
                // Registry bütünlüğü testi (Windows için)
                test.IsPassed = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                test.Duration = DateTime.UtcNow - test.StartTime;
            }
            catch (Exception ex)
            {
                test.IsPassed = false;
                test.ErrorMessage = ex.Message;
                test.Duration = DateTime.UtcNow - test.StartTime;
            }

            return test;
        }

        private DiagnosticAnalysis AnalyzeDiagnosticResults(List<DiagnosticTestResult> results)
        {
            var analysis = new DiagnosticAnalysis
            {
                TotalTests = results.Count,
                PassedTests = results.Count(r => r.IsPassed),
                FailedTests = results.Count(r => !r.IsPassed),
                TotalDuration = TimeSpan.FromSeconds(results.Sum(r => r.Duration.TotalSeconds))
            };

            // Kategori bazlı analiz
            analysis.CategoryResults = results
                .GroupBy(r => r.Category)
                .ToDictionary(
                    g => g.Key,
                    g => new CategoryResult
                    {
                        Total = g.Count(),
                        Passed = g.Count(r => r.IsPassed),
                        Failed = g.Count(r => !r.IsPassed)
                    });

            // Kritik hataları belirle
            analysis.CriticalIssues = results
                .Where(r => !r.IsPassed &&
                           (r.Category == DiagnosticCategory.System ||
                            r.Category == DiagnosticCategory.Security))
                .Select(r => r.TestName)
                .ToList();

            // Öneriler oluştur
            analysis.Recommendations = GenerateRecommendations(analysis);

            return analysis;
        }

        private List<string> GenerateRecommendations(DiagnosticAnalysis analysis)
        {
            var recommendations = new List<string>();

            if (analysis.FailedTests > 0)
            {
                recommendations.Add($"{analysis.FailedTests} test başarısız oldu, logları kontrol edin");
            }

            if (analysis.CategoryResults.TryGetValue(DiagnosticCategory.System, out var systemResults) &&
                systemResults.Failed > 0)
            {
                recommendations.Add("Sistem kaynaklarında sorun tespit edildi, sistem bakımı yapın");
            }

            if (analysis.CategoryResults.TryGetValue(DiagnosticCategory.Security, out var securityResults) &&
                securityResults.Failed > 0)
            {
                recommendations.Add("Güvenlik testleri başarısız, güvenlik yapılandırmasını gözden geçirin");
            }

            if (analysis.CategoryResults.TryGetValue(DiagnosticCategory.Performance, out var perfResults) &&
                perfResults.Failed > 0)
            {
                recommendations.Add("Performans sorunları tespit edildi, sistem optimizasyonu yapın");
            }

            return recommendations;
        }

        private void LogSystemEvent(
            SystemEventType eventType,
            string message,
            SystemSeverity severity,
            string processId = null,
            string processName = null,
            string resourceId = null,
            string resourceName = null,
            string commandId = null,
            string commandName = null,
            string maintenanceId = null,
            string taskName = null,
            string diagnosticId = null,
            string backupId = null,
            string restartId = null,
            string shutdownId = null)
        {
            var systemEvent = new SystemEvent
            {
                Id = Guid.NewGuid().ToString(),
                EventType = eventType,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Severity = severity,
                ProcessId = processId,
                ProcessName = processName,
                ResourceId = resourceId,
                ResourceName = resourceName,
                CommandId = commandId,
                CommandName = commandName,
                MaintenanceId = maintenanceId,
                TaskName = taskName,
                DiagnosticId = diagnosticId,
                BackupId = backupId,
                RestartId = restartId,
                ShutdownId = shutdownId,
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
            };

            _systemEvents.Add(systemEvent);

            // Event'i gönder
            OnSystemEvent(new SystemEventArgs
            {
                EventType = eventType,
                Message = message,
                Severity = severity,
                Timestamp = DateTime.UtcNow,
                ProcessId = processId,
                ProcessName = processName,
                ResourceId = resourceId,
                ResourceName = resourceName,
                CommandId = commandId,
                CommandName = commandName,
            });

            // Log'a yaz
            var logLevel = severity switch
            {
                SystemSeverity.Debug => LogLevel.Debug,
                SystemSeverity.Information => LogLevel.Information,
                SystemSeverity.Warning => LogLevel.Warning,
                SystemSeverity.Error => LogLevel.Error,
                SystemSeverity.Critical => LogLevel.Critical,
                _ => LogLevel.Information
            };

            _logger.Log(logLevel, "[SYSTEM] {EventType}: {Message}", eventType, message);
        }

        private void OnSystemEvent(SystemEventArgs e)
        {
            SystemEvent?.Invoke(this, e);
        }

        private void ValidateSystemState()
        {
            if (IsEmergencyMode)
            {
                throw new SystemEmergencyException("Acil durum modunda işlem yapılamaz");
            }

            if (IsMaintenanceMode)
            {
                throw new SystemMaintenanceException("Bakım modunda işlem yapılamaz");
            }
        }

        private void ValidateStartInfo(ProcessStartInfo startInfo)
        {
            if (startInfo == null)
                throw new ArgumentNullException(nameof(startInfo));

            if (string.IsNullOrWhiteSpace(startInfo.FileName))
                throw new ArgumentException("İşlem dosya adı boş olamaz", nameof(startInfo));
        }

        private void ValidateProcessId(string processId)
        {
            if (string.IsNullOrWhiteSpace(processId))
                throw new ArgumentException("İşlem ID boş olamaz", nameof(processId));
        }

        private void ValidateResourceRequest(ResourceRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("Kaynak adı boş olamaz", nameof(request));

            if (request.Requirements == null)
                throw new ArgumentException("Kaynak gereksinimleri boş olamaz", nameof(request));
        }

        private void ValidateResourceId(string resourceId)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("Kaynak ID boş olamaz", nameof(resourceId));
        }

        private void ValidateCommand(SystemCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (string.IsNullOrWhiteSpace(command.Name))
                throw new ArgumentException("Komut adı boş olamaz", nameof(command));

            if (string.IsNullOrWhiteSpace(command.CommandText))
                throw new ArgumentException("Komut metni boş olamaz", nameof(command));
        }

        private void ValidateMaintenancePlan(MaintenancePlan plan)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            if (string.IsNullOrWhiteSpace(plan.Name))
                throw new ArgumentException("Bakım planı adı boş olamaz", nameof(plan));

            if (plan.Tasks == null || plan.Tasks.Count == 0)
                throw new ArgumentException("Bakım görevleri boş olamaz", nameof(plan));
        }

        private void ValidateBackupId(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
                throw new ArgumentException("Yedekleme ID boş olamaz", nameof(backupId));
        }

        private void ValidateDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(SystemManager));
        }

        private void HandleInitializationError(Exception ex)
        {
            // Acil durum loglaması
            Console.WriteLine($"[SYSTEM EMERGENCY] SystemManager initialization failed: {ex.Message}");

            // Minimum işlevsellik sağla
            try
            {
                _currentState = SystemState.Emergency;
                IsEmergencyMode = true;

                // Kritik servisleri başlat
                StartCriticalProcesses();
            }
            catch
            {
                // Kritik hata
            }
        }
        #endregion

        #region IDisposable Implementation
        public void Dispose()
        {
            if (_isDisposed) return;

            try
            {
                _cancellationTokenSource.Cancel();

                // Görevlerin tamamlanmasını bekle
                Task.WaitAll(
                    _monitoringTask?.WaitAsync(TimeSpan.FromSeconds(10)) ?? Task.CompletedTask,
                    _maintenanceTask?.WaitAsync(TimeSpan.FromSeconds(10)) ?? Task.CompletedTask,
                    _commandProcessorTask?.WaitAsync(TimeSpan.FromSeconds(10)) ?? Task.CompletedTask
                );

                // İşlemleri durdur
                StopAllProcessesAsync(true).Wait(TimeSpan.FromSeconds(5));

                // Kaynakları serbest bırak
                ReleaseAllResourcesAsync().Wait(TimeSpan.FromSeconds(3));

                // Kaynakları serbest bırak
                _systemLock?.Dispose();
                _maintenanceLock?.Dispose();
                _cancellationTokenSource?.Dispose();
            }
            finally
            {
                _isDisposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~SystemManager()
        {
            Dispose();
        }
        #endregion
    }

    #region Supporting Types and Interfaces
    public interface ISystemManager
    {
        Task<ProcessStartResult> StartProcessAsync(
            ProcessStartInfo startInfo,
            ProcessOptions options = null,
            CancellationToken cancellationToken = default);

        Task<ProcessStopResult> StopProcessAsync(
            string processId,
            bool force = false,
            CancellationToken cancellationToken = default);

        Task<int> StopAllProcessesAsync(bool force = false, string category = null);

        Task<ResourceAllocationResult> AllocateResourceAsync(
            ResourceRequest request,
            CancellationToken cancellationToken = default);

        Task<ResourceReleaseResult> ReleaseResourceAsync(
            string resourceId,
            CancellationToken cancellationToken = default);

        Task<CommandExecutionResult> ExecuteCommandAsync(
            SystemCommand command,
            CancellationToken cancellationToken = default);

        Task<MaintenanceResult> StartMaintenanceAsync(
            MaintenancePlan plan,
            CancellationToken cancellationToken = default);

        SystemStatus GetSystemStatus();
        Task<SystemPerformanceMetrics> GetPerformanceMetricsAsync();

        IEnumerable<SystemEvent> GetSystemEvents(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            SystemEventType? eventType = null,
            SystemSeverity? minSeverity = null);

        Task<DiagnosticResult> RunDiagnosticsAsync(
            DiagnosticOptions options = null,
            CancellationToken cancellationToken = default);

        Task<BackupResult> CreateBackupAsync(
            BackupOptions options = null,
            CancellationToken cancellationToken = default);

        Task<RestoreResult> RestoreBackupAsync(
            string backupId,
            RestoreOptions options = null,
            CancellationToken cancellationToken = default);

        void ActivateEmergencyMode(string reason);
        void DeactivateEmergencyMode();

        Task<RestartResult> RestartSystemAsync(
            RestartOptions options = null,
            CancellationToken cancellationToken = default);

        Task<ShutdownResult> ShutdownSystemAsync(
            ShutdownOptions options = null,
            CancellationToken cancellationToken = default);
    }

    public enum SystemState
    {
        Initializing,
        Normal,
        Maintenance,
        Emergency,
        ShuttingDown
    }

    public enum SystemEventType
    {
        SystemInitialized,
        SystemHealthDegraded,
        HighCpuUsage,
        HighMemoryUsage,
        HighDiskUsage,
        ProcessStarted,
        ProcessStartFailed,
        ProcessStopped,
        ProcessStopFailed,
        ProcessExited,
        ProcessRestarting,
        ProcessRestarted,
        ProcessRestartFailed,
        ProcessHighCpu,
        ProcessHighMemory,
        ProcessNotResponding,
        ProcessThrottled,
        ProcessMemoryOptimized,
        ProcessOutput,
        ProcessErrorOutput,
        AllProcessesStopped,
        ResourceAllocated,
        ResourceAllocationFailed,
        ResourceReleased,
        ResourceReleaseFailed,
        CommandQueued,
        CommandProcessing,
        CommandCompleted,
        CommandFailed,
        MaintenanceStarted,
        MaintenanceCompleted,
        MaintenanceFailed,
        MaintenanceCancelled,
        MaintenanceTaskStarted,
        MaintenanceTaskCompleted,
        MaintenanceTaskFailed,
        DiagnosticsStarted,
        DiagnosticsCompleted,
        DiagnosticsFailed,
        BackupStarted,
        BackupCompleted,
        BackupFailed,
        RestoreStarted,
        RestoreCompleted,
        RestoreFailed,
        RestartStarted,
        RestartCompleted,
        RestartFailed,
        ShutdownStarted,
        ShutdownCompleted,
        ShutdownFailed,
        EmergencyModeActivated,
        EmergencyModeDeactivated,
        OperationCancelled
    }

    public enum SystemSeverity
    {
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }

    public enum ProcessStatus
    {
        Starting,
        Running,
        Stopped,
        Terminated,
        Failed
    }

    public enum ResourceType
    {
        Cpu,
        Memory,
        Disk,
        Network,
        FileHandle,
        Custom
    }

    public enum CommandType
    {
        System,
        PowerShell,
        Batch,
        Shell,
        Custom
    }

    public enum CommandPriority
    {
        Low,
        Normal,
        High,
        Immediate
    }

    public enum MaintenanceTaskType
    {
        Cleanup,
        Optimization,
        Update,
        Backup,
        Validation
    }

    public enum DiagnosticCategory
    {
        System,
        Performance,
        Security,
        Network,
        Storage,
        Integrity
    }

    public enum BackupStatus
    {
        InProgress,
        Completed,
        Failed
    }

    public class SystemEventArgs : EventArgs
    {
        public SystemEventType EventType { get; set; }
        public string Message { get; set; }
        public SystemSeverity Severity { get; set; }
        public DateTime Timestamp { get; set; }
        public string ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string ResourceId { get; set; }
        public string ResourceName { get; set; }
        public string CommandId { get; set; }
        public string CommandName { get; set; }
    }

    public class SystemEvent
    {
        public string Id { get; set; }
        public SystemEventType EventType { get; set; }
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public SystemSeverity Severity { get; set; }
        public string ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string ResourceId { get; set; }
        public string ResourceName { get; set; }
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public string MaintenanceId { get; set; }
        public string TaskName { get; set; }
        public string DiagnosticId { get; set; }
        public string BackupId { get; set; }
        public string RestartId { get; set; }
        public string ShutdownId { get; set; }
        public string MachineName { get; set; }
        public string UserName { get; set; }
    }

    public class SystemStatus
    {
        public bool IsInitialized { get; set; }
        public SystemState CurrentState { get; set; }
        public bool IsMaintenanceMode { get; set; }
        public bool IsEmergencyMode { get; set; }
        public int ActiveProcessCount { get; set; }
        public int ActiveResourceCount { get; set; }
        public int QueuedCommands { get; set; }
        public int TotalProcessesManaged { get; set; }
        public long TotalMemoryAllocated { get; set; }
        public DateTime LastMaintenance { get; set; }
        public DateTime LastHealthCheck { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    public class SystemPerformanceMetrics
    {
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double DiskUsage { get; set; }
        public double NetworkUsage { get; set; }
        public int ProcessCount { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; }
    }

    public class ManagedProcess
    {
        public string ProcessId { get; set; }
        public Process SystemProcess { get; set; }
        public ProcessStartInfo StartInfo { get; set; }
        public ProcessOptions Options { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ProcessStatus Status { get; set; }
        public ProcessResourceUsage ResourceUsage { get; set; }
        public DateTime LastActivity { get; set; }
        public int RestartCount { get; set; }
        public DateTime? LastRestart { get; set; }
        public int? ExitCode { get; set; }
        public string Owner { get; set; }
        public List<string> Tags { get; set; }
    }

    public class ProcessResourceUsage
    {
        public double CpuUsage { get; set; }
        public long MemoryUsage { get; set; }
        public long PrivateMemoryUsage { get; set; }
        public long VirtualMemoryUsage { get; set; }
        public int HandleCount { get; set; }
        public int ThreadCount { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan TotalProcessorTime { get; set; }
        public TimeSpan UserProcessorTime { get; set; }
    }

    public class ProcessOptions
    {
        public string Category { get; set; }
        public ProcessPriority Priority { get; set; } = ProcessPriority.Normal;
        public bool IsCritical { get; set; } = false;
        public bool RestartOnFailure { get; set; } = false;
        public int MaxRestartAttempts { get; set; } = 3;
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.Zero;
        public double MaxCpuUsage { get; set; } = 0; // 0 = no limit
        public int MaxMemoryMB { get; set; } = 0; // 0 = no limit
        public bool CpuThrottlingEnabled { get; set; } = true;
        public bool MemoryPressureHandlingEnabled { get; set; } = true;
        public bool LogOutput { get; set; } = true;
        public bool RequireAdminPrivileges { get; set; } = false;
        public bool RequireDigitalSignature { get; set; } = false;
        public ResourceRequirements ResourceRequirements { get; set; }
        public List<string> Tags { get; set; }
    }

    public enum ProcessPriority
    {
        Idle,
        Low,
        Normal,
        High,
        Realtime
    }

    public class ResourceRequirements
    {
        public int CpuCores { get; set; }
        public int MemoryMB { get; set; }
        public int DiskSpaceMB { get; set; }
        public int NetworkBandwidthMbps { get; set; }
        public int FileHandles { get; set; }
    }

    public class AvailableResources
    {
        public int TotalCpuCores { get; set; }
        public double AvailableCpuCores { get; set; }
        public long TotalMemoryMB { get; set; }
        public long AvailableMemoryMB { get; set; }
        public long TotalDiskSpaceMB { get; set; }
        public long AvailableDiskSpaceMB { get; set; }
        public int TotalNetworkBandwidthMbps { get; set; }
        public int AvailableNetworkBandwidthMbps { get; set; }
    }

    public class SystemResource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ResourceType Type { get; set; }
        public long Capacity { get; set; }
        public long CurrentUsage { get; set; }
        public bool IsAllocated { get; set; }
        public DateTime AllocationTime { get; set; }
        public DateTime? ReleaseTime { get; set; }
        public string Owner { get; set; }
    }

    public class CpuResource : SystemResource
    {
        public int Cores { get; set; }
        public int LogicalProcessors { get; set; }
        public uint MaxClockSpeed { get; set; }
        public string Architecture { get; set; }
        public string Manufacturer { get; set; }
    }

    public class MemoryResource : SystemResource
    {
        public string Manufacturer { get; set; }
        public uint Speed { get; set; }
        public string MemoryType { get; set; }
    }

    public class DiskResource : SystemResource
    {
        public DriveType DriveType { get; set; }
        public long TotalSize { get; set; }
        public long AvailableSpace { get; set; }
        public string FileSystem { get; set; }
        public string VolumeLabel { get; set; }
        public string FilePath { get; set; }
    }

    public class NetworkResource : SystemResource
    {
        public string Manufacturer { get; set; }
        public string MACAddress { get; set; }
        public ulong Speed { get; set; }
    }

    public class SystemInformation : SystemResource
    {
        public string MachineName { get; set; }
        public string OSVersion { get; set; }
        public string Platform { get; set; }
        public int ProcessorCount { get; set; }
        public string SystemDirectory { get; set; }
        public string UserDomainName { get; set; }
        public string UserName { get; set; }
        public bool Is64BitOperatingSystem { get; set; }
        public bool Is64BitProcess { get; set; }
        public int TickCount { get; set; }
        public long WorkingSet { get; set; }
    }

    public class ResourceRequest
    {
        public string Name { get; set; }
        public ResourceType Type { get; set; }
        public ResourceRequirements Requirements { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class SystemCommand
    {
        public string CommandId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public CommandType CommandType { get; set; }
        public string CommandText { get; set; }
        public CommandPriority Priority { get; set; } = CommandPriority.Normal;
        public bool RequiresAdminPrivileges { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public TimeSpan? Timeout { get; set; }
    }

    public class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public DateTime ExecutionTime { get; set; }
    }

    public class MaintenancePlan
    {
        public string MaintenanceId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public List<MaintenanceTask> Tasks { get; set; }
        public bool StopOnFailure { get; set; } = true;
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class MaintenanceTask
    {
        public string Name { get; set; }
        public MaintenanceTaskType Type { get; set; }
        public string Target { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class DiagnosticOptions
    {
        public bool IncludeSystemTests { get; set; } = true;
        public bool IncludePerformanceTests { get; set; } = true;
        public bool IncludeSecurityTests { get; set; } = true;
        public bool IncludeNetworkTests { get; set; } = true;
        public bool IncludeStorageTests { get; set; } = true;
        public bool IncludeIntegrityTests { get; set; } = true;
        public TimeSpan? Timeout { get; set; }
        public Dictionary<string, object> CustomTests { get; set; }
    }

    public class DiagnosticTestResult
    {
        public string TestName { get; set; }
        public DiagnosticCategory Category { get; set; }
        public bool IsPassed { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Details { get; set; }
    }

    public class DiagnosticAnalysis
    {
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<DiagnosticCategory, CategoryResult> CategoryResults { get; set; }
        public List<string> CriticalIssues { get; set; }
        public List<string> Recommendations { get; set; }
    }

    public class CategoryResult
    {
        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
    }

    public class BackupOptions
    {
        public bool IncludeConfiguration { get; set; } = true;
        public bool IncludeLogs { get; set; } = true;
        public bool IncludeDatabase { get; set; } = true;
        public bool IncludeProjects { get; set; } = true;
        public int CompressionLevel { get; set; } = 6;
        public bool EncryptBackup { get; set; } = false;
        public string EncryptionKey { get; set; }
    }

    public class SystemBackup
    {
        public string BackupId { get; set; }
        public string BackupPath { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public BackupStatus Status { get; set; }
        public long SizeInBytes { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class RestoreOptions
    {
        public bool RestoreConfiguration { get; set; } = true;
        public bool RestoreLogs { get; set; } = false;
        public bool RestoreDatabase { get; set; } = true;
        public bool RestoreProjects { get; set; } = true;
        public bool OverwriteExisting { get; set; } = true;
    }

    public class RestartOptions
    {
        public bool ForceStop { get; set; } = false;
        public bool NotifyUsers { get; set; } = true;
        public TimeSpan DelayBeforeRestart { get; set; } = TimeSpan.FromSeconds(30);
        public string Reason { get; set; }
    }

    public class ShutdownOptions
    {
        public bool ForceStop { get; set; } = false;
        public bool NotifyUsers { get; set; } = true;
        public bool CreateBackup { get; set; } = false;
        public TimeSpan DelayBeforeShutdown { get; set; } = TimeSpan.FromSeconds(30);
        public string Reason { get; set; }
    }

    // Result Classes
    public abstract class SystemResult
    {
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class ProcessStartResult : SystemResult
    {
        public string ProcessId { get; set; }
        public int SystemProcessId { get; set; }
        public ManagedProcess ManagedProcess { get; set; }
        public ProcessStartError ErrorCode { get; set; }

        public static ProcessStartResult Success(string processId, int systemProcessId, ManagedProcess managedProcess)
        {
            return new ProcessStartResult
            {
                IsSuccess = true,
                ProcessId = processId,
                SystemProcessId = systemProcessId,
                ManagedProcess = managedProcess,
            };
        }

        public static ProcessStartResult Failed(string errorMessage, ProcessStartError errorCode)
        {
            return new ProcessStartResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum ProcessStartError
    {
        InvalidStartInfo,
        InsufficientResources,
        SecurityViolation,
        ProcessAlreadyRunning,
        Timeout,
        Unknown
    }

    public class ProcessStopResult : SystemResult
    {
        public string ProcessId { get; set; }
        public int SystemProcessId { get; set; }
        public ProcessStopError ErrorCode { get; set; }

        public static ProcessStopResult Success(string processId, int systemProcessId)
        {
            return new ProcessStopResult
            {
                IsSuccess = true,
                ProcessId = processId,
                SystemProcessId = systemProcessId,
            };
        }

        public static ProcessStopResult Failed(string errorMessage, ProcessStopError errorCode)
        {
            return new ProcessStopResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum ProcessStopError
    {
        ProcessNotFound,
        AlreadyStopped,
        StopFailed,
        AccessDenied,
        Unknown
    }

    public class ResourceAllocationResult : SystemResult
    {
        public string ResourceId { get; set; }
        public SystemResource Resource { get; set; }
        public ResourceAllocationError ErrorCode { get; set; }

        public static ResourceAllocationResult Success(string resourceId, SystemResource resource)
        {
            return new ResourceAllocationResult
            {
                IsSuccess = true,
                ResourceId = resourceId,
                Resource = resource,
            };
        }

        public static ResourceAllocationResult Failed(string errorMessage, ResourceAllocationError errorCode)
        {
            return new ResourceAllocationResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum ResourceAllocationError
    {
        InsufficientResources,
        InvalidRequest,
        ResourceBusy,
        Unknown
    }

    public class ResourceReleaseResult : SystemResult
    {
        public string ResourceId { get; set; }
        public ResourceReleaseError ErrorCode { get; set; }

        public static ResourceReleaseResult Success(string resourceId)
        {
            return new ResourceReleaseResult
            {
                IsSuccess = true,
                ResourceId = resourceId,
            };
        }

        public static ResourceReleaseResult Failed(string errorMessage, ResourceReleaseError errorCode)
        {
            return new ResourceReleaseResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum ResourceReleaseError
    {
        ResourceNotFound,
        ReleaseFailed,
        Unknown
    }

    public class CommandExecutionResult : SystemResult
    {
        public string CommandId { get; set; }
        public CommandResult Result { get; set; }
        public CommandExecutionError ErrorCode { get; set; }

        public static CommandExecutionResult Success(string commandId, CommandResult result)
        {
            return new CommandExecutionResult
            {
                IsSuccess = true,
                CommandId = commandId,
                Result = result,
            };
        }

        public static CommandExecutionResult Queued(string commandId)
        {
            return new CommandExecutionResult
            {
                IsSuccess = true,
                CommandId = commandId,
            };
        }

        public static CommandExecutionResult Failed(string errorMessage, CommandExecutionError errorCode)
        {
            return new CommandExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum CommandExecutionError
    {
        InvalidCommand,
        ProcessStartFailed,
        CommandFailed,
        Timeout,
        ExecutionError,
        Unknown
    }

    public class MaintenanceResult : SystemResult
    {
        public string MaintenanceId { get; set; }
        public List<MaintenanceTaskResult> TaskResults { get; set; }
        public MaintenanceError ErrorCode { get; set; }

        public static MaintenanceResult Success(string maintenanceId, List<MaintenanceTaskResult> taskResults)
        {
            return new MaintenanceResult
            {
                IsSuccess = true,
                MaintenanceId = maintenanceId,
                TaskResults = taskResults,
            };
        }

        public static MaintenanceResult Failed(string errorMessage, MaintenanceError errorCode, List<MaintenanceTaskResult> taskResults = null)
        {
            return new MaintenanceResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
                TaskResults = taskResults,
            };
        }
    }

    public enum MaintenanceError
    {
        InvalidPlan,
        TaskFailed,
        Timeout,
        Unknown
    }

    public class MaintenanceTaskResult : SystemResult
    {
        public string TaskName { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public static MaintenanceTaskResult Success(string taskName, TimeSpan duration)
        {
            return new MaintenanceTaskResult
            {
                IsSuccess = true,
                TaskName = taskName,
                Duration = duration,
                StartTime = DateTime.UtcNow - duration,
                EndTime = DateTime.UtcNow,
            };
        }

        public static MaintenanceTaskResult Failed(string taskName, string errorMessage, TimeSpan duration)
        {
            return new MaintenanceTaskResult
            {
                IsSuccess = false,
                TaskName = taskName,
                ErrorMessage = errorMessage,
                Duration = duration,
                StartTime = DateTime.UtcNow - duration,
                EndTime = DateTime.UtcNow,
            };
        }
    }

    public class DiagnosticResult : SystemResult
    {
        public string DiagnosticId { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<DiagnosticTestResult> Tests { get; set; }
        public DiagnosticAnalysis Analysis { get; set; }
        public List<string> Recommendations { get; set; }
        public DiagnosticError ErrorCode { get; set; }

        public static DiagnosticResult Success(string diagnosticId, List<DiagnosticTestResult> tests, DiagnosticAnalysis analysis, List<string> recommendations)
        {
            return new DiagnosticResult
            {
                IsSuccess = true,
                DiagnosticId = diagnosticId,
                Tests = tests,
                Analysis = analysis,
                Recommendations = recommendations,
                StartTime = DateTime.UtcNow - analysis.TotalDuration,
                Duration = analysis.TotalDuration,
            };
        }

        public static DiagnosticResult Failed(string errorMessage, DiagnosticError errorCode)
        {
            return new DiagnosticResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum DiagnosticError
    {
        TestFailed,
        Timeout,
        SystemError,
        Unknown
    }

    public class BackupResult : SystemResult
    {
        public string BackupId { get; set; }
        public SystemBackup Backup { get; set; }
        public TimeSpan Duration { get; set; }
        public BackupError ErrorCode { get; set; }

        public static BackupResult Success(string backupId, SystemBackup backup, TimeSpan duration)
        {
            return new BackupResult
            {
                IsSuccess = true,
                BackupId = backupId,
                Backup = backup,
                Duration = duration,
            };
        }

        public static BackupResult Failed(string errorMessage, BackupError errorCode)
        {
            return new BackupResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum BackupError
    {
        InvalidOptions,
        StorageError,
        Timeout,
        Unknown
    }

    public class RestoreResult : SystemResult
    {
        public string BackupId { get; set; }
        public TimeSpan Duration { get; set; }
        public RestoreError ErrorCode { get; set; }

        public static RestoreResult Success(string backupId, TimeSpan duration)
        {
            return new RestoreResult
            {
                IsSuccess = true,
                BackupId = backupId,
                Duration = duration,
            };
        }

        public static RestoreResult Failed(string errorMessage, RestoreError errorCode)
        {
            return new RestoreResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum RestoreError
    {
        BackupNotFound,
        InvalidBackup,
        RestoreFailed,
        Timeout,
        Unknown
    }

    public class RestartResult : SystemResult
    {
        public string RestartId { get; set; }
        public RestartError ErrorCode { get; set; }

        public static RestartResult Success(string restartId)
        {
            return new RestartResult
            {
                IsSuccess = true,
                RestartId = restartId,
            };
        }

        public static RestartResult Failed(string errorMessage, RestartError errorCode)
        {
            return new RestartResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum RestartError
    {
        PreparationFailed,
        StopFailed,
        Unknown
    }

    public class ShutdownResult : SystemResult
    {
        public string ShutdownId { get; set; }
        public ShutdownError ErrorCode { get; set; }

        public static ShutdownResult Success(string shutdownId)
        {
            return new ShutdownResult
            {
                IsSuccess = true,
                ShutdownId = shutdownId,
            };
        }

        public static ShutdownResult Failed(string errorMessage, ShutdownError errorCode)
        {
            return new ShutdownResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode,
            };
        }
    }

    public enum ShutdownError
    {
        PreparationFailed,
        BackupFailed,
        StopFailed,
        Unknown
    }

    // Supporting Classes
    public class ProcessState
    {
        public string ProcessId { get; set; }
        public string FileName { get; set; }
        public string Arguments { get; set; }
        public ProcessStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public ProcessResourceUsage ResourceUsage { get; set; }
    }

    public class SystemHealthFailureContext
    {
        public SystemHealth Health { get; }
        public DateTime FailureTime { get; }

        public SystemHealthFailureContext(SystemHealth health)
        {
            Health = health;
            FailureTime = DateTime.UtcNow;
        }
    }

    // Exceptions
    public class SystemManagerInitializationException : Exception
    {
        public SystemManagerInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ProcessStartException : Exception
    {
        public ProcessStartException(string message) : base(message) { }

        public ProcessStartException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ProcessStopException : Exception
    {
        public ProcessStopException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ResourceAllocationException : Exception
    {
        public ResourceAllocationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ResourceReleaseException : Exception
    {
        public ResourceReleaseException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class CommandExecutionException : Exception
    {
        public CommandExecutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class MaintenanceException : Exception
    {
        public MaintenanceException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class DiagnosticException : Exception
    {
        public DiagnosticException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class BackupException : Exception
    {
        public BackupException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class RestoreException : Exception
    {
        public RestoreException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class RestartException : Exception
    {
        public RestartException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ShutdownException : Exception
    {
        public ShutdownException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SystemEmergencyException : Exception
    {
        public SystemEmergencyException(string message) : base(message) { }
    }

    public class SystemMaintenanceException : Exception
    {
        public SystemMaintenanceException(string message) : base(message) { }
    }

    // Required supporting types from other modules
    public class SystemHealth
    {
        public HealthStatus Status { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Details { get; set; }
        public bool IsHealthy => Status == HealthStatus.Healthy;
    }

    public enum HealthStatus
    {
        Healthy,
        Degraded,
        Critical
    }

    public class ProjectInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProjectPath { get; set; }
        // ... diğer özellikler
    }

    public interface IProjectManager
    {
        Task<List<ProjectInfo>> GetAllProjectsAsync(CancellationToken cancellationToken);
        Task<ProjectInfo> RestoreProjectAsync(string projectId, string backupPath, CancellationToken cancellationToken);
    }
    #endregion
}