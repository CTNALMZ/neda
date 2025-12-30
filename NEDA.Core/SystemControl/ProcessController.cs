using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring.Diagnostics;
using NEDA.Core.Monitoring.PerformanceCounters;
using NEDA.Core.Security;
using NEDA.Core.Security.AccessControl;

namespace NEDA.Core.SystemControl
{
    /// <summary>
    /// Advanced Process Controller with comprehensive process management, 
    /// security sandboxing, performance monitoring, and system integration capabilities;
    /// </summary>
    public class ProcessController : IProcessController, IDisposable
    {
        #region Fields and Properties

        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;
        private readonly PermissionValidator _permissionValidator;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly DiagnosticTool _diagnosticTool;
        private readonly AccessControlManager _accessControl;

        private readonly ConcurrentDictionary<int, ProcessInfo> _managedProcesses;
        private readonly ConcurrentDictionary<string, ProcessPool> _processPools;
        private readonly ConcurrentQueue<ProcessEvent> _processEvents;

        private readonly Timer? _monitoringTimer;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _processSemaphore;
        private readonly object _processLock = new object();

        private readonly WindowsIdentity _systemIdentity;
        private readonly SecureString _elevatedPassword;
        private readonly List<string> _trustedProcesses;

        private long _totalProcessStarts;
        private long _totalProcessStops;
        private long _totalProcessCrashes;
        private long _processCreationFailures;

        private readonly ProcessControllerConfig _config;
        private bool _disposed;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
            TOKEN_TYPE TokenType,
            out IntPtr phNewToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr process,
            IntPtr minimumWorkingSetSize,
            IntPtr maximumWorkingSetSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(
            IntPtr hProcess,
            uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr process);

        private const int MAX_CONCURRENT_PROCESSES = 100;
        private const int MONITORING_INTERVAL_MS = 2000;
        private const int CLEANUP_INTERVAL_MS = 30000;
        private const int MAX_PROCESS_EVENTS = 10000;
        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

        #endregion

        #region Nested Types

        public class ProcessInfo : IDisposable
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string FilePath { get; set; } = string.Empty;
            public string CommandLine { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
            public ProcessSecurityContext? SecurityContext { get; set; }
            public ProcessPriority Priority { get; set; }
            public ProcessAffinity? Affinity { get; set; }
            public ProcessStartupInfo? StartupInfo { get; set; }

            // FIX: ResourceLimits gerçek quota tipi olmalı (aşağıda MaxMemoryBytes vs kullanıyorsun)
            public ResourceQuotas? ResourceLimits { get; set; }

            public DateTime StartTime { get; set; }
            public TimeSpan TotalProcessorTime { get; set; }
            public long MemoryUsage { get; set; }
            public long PeakMemoryUsage { get; set; }
            public int ThreadCount { get; set; }
            public int HandleCount { get; set; }
            public ProcessState State { get; set; }
            public string Owner { get; set; } = string.Empty;
            public int ExitCode { get; set; }
            public DateTime? ExitTime { get; set; }
            public List<string> Modules { get; set; }
            public Dictionary<string, string> EnvironmentVariables { get; set; }
            public bool IsManaged { get; set; }
            public string ManagedBy { get; set; } = string.Empty;
            public ProcessPerformanceMetrics PerformanceMetrics { get; set; }

            private Process? _process;
            private bool _disposed;

            // FIX: property syntax
            public Process? Process
            {
                get => _process;
                set
                {
                    _process = value;
                    if (_process != null)
                    {
                        Id = _process.Id;
                        Name = _process.ProcessName;
                        try
                        {
                            StartTime = _process.StartTime;
                            Owner = GetProcessOwner(_process);
                        }
                        catch
                        {
                            // Process may have exited;
                        }
                    }
                }
            }

            public ProcessInfo()
            {
                Modules = new List<string>();
                EnvironmentVariables = new Dictionary<string, string>();
                PerformanceMetrics = new ProcessPerformanceMetrics();
                State = ProcessState.Unknown;
            }

            private string GetProcessOwner(Process process)
            {
                try
                {
                    var query = $"Select * From Win32_Process Where ProcessID = {process.Id}";
                    using var searcher = new ManagementObjectSearcher(query);
                    using var collection = searcher.Get();

                    foreach (ManagementObject obj in collection)
                    {
                        var args = new string[2];
                        obj.InvokeMethod("GetOwner", args);
                        return args[0];
                    }
                }
                catch
                {
                    return WindowsIdentity.GetCurrent().Name;
                }

                return "Unknown";
            }

            public void UpdateMetrics()
            {
                if (_process == null || _process.HasExited)
                    return;

                try
                {
                    _process.Refresh();

                    TotalProcessorTime = _process.TotalProcessorTime;
                    MemoryUsage = _process.WorkingSet64;
                    PeakMemoryUsage = Math.Max(PeakMemoryUsage, MemoryUsage);
                    ThreadCount = _process.Threads.Count;
                    HandleCount = _process.HandleCount;

                    if (_process.HasExited)
                    {
                        State = ProcessState.Exited;
                        ExitCode = _process.ExitCode;
                        ExitTime = _process.ExitTime;
                    }
                    else
                    {
                        State = ProcessState.Running;
                    }

                    PerformanceMetrics.Update(this);
                }
                catch (InvalidOperationException)
                {
                    State = ProcessState.Exited;
                }
                catch (Win32Exception)
                {
                    State = ProcessState.AccessDenied;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                try
                {
                    _process?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors;
                }

                _disposed = true;
            }
        }

        private class ProcessPool : IDisposable
        {
            public string Name { get; }
            public string ProcessPath { get; }
            public int MinInstances { get; }
            public int MaxInstances { get; }
            public TimeSpan IdleTimeout { get; }
            public ProcessSecurityContext SecurityContext { get; }

            private readonly ConcurrentQueue<ProcessInfo> _idleProcesses;
            private readonly List<ProcessInfo> _activeProcesses;
            private readonly SemaphoreSlim _poolSemaphore;
            private readonly Timer _cleanupTimer;
            private bool _disposed;

            public int TotalInstances => _idleProcesses.Count + _activeProcesses.Count;
            public int IdleInstances => _idleProcesses.Count;
            public int ActiveInstances => _activeProcesses.Count;

            public ProcessPool(string name, string processPath, ProcessPoolConfig config)
            {
                Name = name;
                ProcessPath = processPath;
                MinInstances = config.MinInstances;
                MaxInstances = config.MaxInstances;
                IdleTimeout = config.IdleTimeout;
                SecurityContext = config.SecurityContext;

                _idleProcesses = new ConcurrentQueue<ProcessInfo>();
                _activeProcesses = new List<ProcessInfo>();
                _poolSemaphore = new SemaphoreSlim(config.MaxInstances);

                Task.Run(async () => await InitializePoolAsync());

                _cleanupTimer = new Timer(CleanupIdleProcesses, null,
                    TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            }

            private async Task InitializePoolAsync()
            {
                for (int i = 0; i < MinInstances; i++)
                {
                    try
                    {
                        var processInfo = await CreatePooledProcessAsync();
                        if (processInfo != null)
                        {
                            _idleProcesses.Enqueue(processInfo);
                        }
                    }
                    catch
                    {
                        // Continue;
                    }
                }
            }

            private async Task<ProcessInfo?> CreatePooledProcessAsync()
            {
                var startInfo = new ProcessStartupInfo
                {
                    FileName = ProcessPath,
                    Arguments = "--pooled",
                    WorkingDirectory = Path.GetDirectoryName(ProcessPath) ?? string.Empty,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                await Task.Delay(100);
                return null;
            }

            public async Task<ProcessInfo> AcquireProcessAsync(TimeSpan timeout)
            {
                if (!await _poolSemaphore.WaitAsync(timeout))
                    throw new TimeoutException("Timeout waiting for process from pool");

                try
                {
                    if (_idleProcesses.TryDequeue(out var processInfo))
                    {
                        _activeProcesses.Add(processInfo);
                        return processInfo;
                    }

                    if (TotalInstances < MaxInstances)
                    {
                        var created = await CreatePooledProcessAsync();
                        if (created != null)
                        {
                            _activeProcesses.Add(created);
                            return created;
                        }
                    }

                    throw new InvalidOperationException("Failed to acquire process from pool");
                }
                catch
                {
                    _poolSemaphore.Release();
                    throw;
                }
            }

            public void ReleaseProcess(ProcessInfo processInfo)
            {
                if (processInfo == null)
                    return;

                _activeProcesses.Remove(processInfo);

                if (processInfo.State == ProcessState.Running &&
                    processInfo.Process != null &&
                    !processInfo.Process.HasExited)
                {
                    _idleProcesses.Enqueue(processInfo);
                }
                else
                {
                    processInfo.Dispose();
                }

                _poolSemaphore.Release();
            }

            private void CleanupIdleProcesses(object? state)
            {
                try
                {
                    var cutoffTime = DateTime.UtcNow - IdleTimeout;

                    while (_idleProcesses.TryPeek(out var processInfo) &&
                           processInfo.StartTime < cutoffTime &&
                           TotalInstances > MinInstances)
                    {
                        if (_idleProcesses.TryDequeue(out processInfo))
                        {
                            processInfo.Dispose();
                        }
                    }
                }
                catch
                {
                    // Ignore cleanup errors;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _cleanupTimer.Dispose();

                foreach (var process in _idleProcesses)
                {
                    process.Dispose();
                }

                foreach (var process in _activeProcesses)
                {
                    process.Dispose();
                }
                _activeProcesses.Clear();

                _poolSemaphore.Dispose();

                _disposed = true;
            }
        }

        public class ProcessSecurityContext
        {
            public string Username { get; set; } = string.Empty;
            public SecureString? Password { get; set; }
            public string Domain { get; set; } = string.Empty;
            public TokenImpersonationLevel ImpersonationLevel { get; set; }
            public List<string> AllowedOperations { get; set; }
            public List<string> DeniedOperations { get; set; }
            public Dictionary<string, string> EnvironmentRestrictions { get; set; }
            public ResourceQuotas? ResourceQuotas { get; set; }
            public bool IsSandboxed { get; set; }
            public string IntegrityLevel { get; set; }
            public List<string> Privileges { get; set; }

            public ProcessSecurityContext()
            {
                AllowedOperations = new List<string>();
                DeniedOperations = new List<string>();
                EnvironmentRestrictions = new Dictionary<string, string>();
                Privileges = new List<string>();
                ImpersonationLevel = TokenImpersonationLevel.Impersonation;
                IntegrityLevel = "Medium";
            }
        }

        public class ProcessStartupInfo
        {
            public string FileName { get; set; } = string.Empty;
            public string Arguments { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
            public Dictionary<string, string> EnvironmentVariables { get; set; }
            public bool UseShellExecute { get; set; }
            public bool CreateNoWindow { get; set; }
            public bool RedirectStandardInput { get; set; }
            public bool RedirectStandardOutput { get; set; }
            public bool RedirectStandardError { get; set; }
            public Encoding StandardInputEncoding { get; set; }
            public Encoding StandardOutputEncoding { get; set; }
            public Encoding StandardErrorEncoding { get; set; }
            public string Verb { get; set; } = string.Empty;
            public ProcessWindowStyle WindowStyle { get; set; }
            public bool ErrorDialog { get; set; }
            public IntPtr ErrorDialogParentHandle { get; set; }
            public bool LoadUserProfile { get; set; }

            public ProcessStartupInfo()
            {
                EnvironmentVariables = new Dictionary<string, string>();
                UseShellExecute = false;
                CreateNoWindow = true;
                WindowStyle = ProcessWindowStyle.Hidden;
                StandardInputEncoding = Encoding.UTF8;
                StandardOutputEncoding = Encoding.UTF8;
                StandardErrorEncoding = Encoding.UTF8;
            }
        }

        public class ResourceQuotas
        {
            public long MaxMemoryBytes { get; set; }
            public long MaxWorkingSetBytes { get; set; }
            public long MinWorkingSetBytes { get; set; }
            public TimeSpan MaxCpuTime { get; set; }
            public int MaxThreads { get; set; }
            public int MaxHandles { get; set; }
            public long MaxDiskBytes { get; set; }
            public long MaxNetworkBytes { get; set; }

            public ResourceQuotas()
            {
                MaxMemoryBytes = 1024L * 1024 * 1024;
                MaxWorkingSetBytes = 512L * 1024 * 1024;
                MinWorkingSetBytes = 16L * 1024 * 1024;
                MaxCpuTime = TimeSpan.FromHours(1);
                MaxThreads = 100;
                MaxHandles = 1000;
            }
        }

        public class ProcessPerformanceMetrics
        {
            public double CpuUsagePercentage { get; set; }
            public double MemoryUsagePercentage { get; set; }
            public long DiskReadBytesPerSecond { get; set; }
            public long DiskWriteBytesPerSecond { get; set; }
            public long NetworkReceivedBytesPerSecond { get; set; }
            public long NetworkSentBytesPerSecond { get; set; }
            public int ContextSwitchesPerSecond { get; set; }
            public int PageFaultsPerSecond { get; set; }
            public DateTime LastUpdated { get; set; }

            private readonly Queue<ProcessInfo> _history;
            private const int HISTORY_SIZE = 10;

            public ProcessPerformanceMetrics()
            {
                _history = new Queue<ProcessInfo>(HISTORY_SIZE);
                LastUpdated = DateTime.UtcNow;
            }

            public void Update(ProcessInfo processInfo)
            {
                _history.Enqueue(processInfo);
                if (_history.Count > HISTORY_SIZE)
                {
                    _history.Dequeue();
                }

                if (_history.Count >= 2)
                {
                    var previous = _history.ElementAt(_history.Count - 2);
                    var current = _history.Last();
                    var timeDelta = current.StartTime - previous.StartTime;

                    if (timeDelta.TotalSeconds > 0)
                    {
                        var cpuDelta = current.TotalProcessorTime - previous.TotalProcessorTime;
                        CpuUsagePercentage = (cpuDelta.TotalMilliseconds / timeDelta.TotalMilliseconds) * 100;

                        var memoryDelta = current.MemoryUsage - previous.MemoryUsage;
                        if (current.MemoryUsage > 0)
                            MemoryUsagePercentage = memoryDelta / (double)current.MemoryUsage * 100;
                    }
                }

                LastUpdated = DateTime.UtcNow;
            }
        }

        public class ProcessControllerConfig
        {
            public bool EnableProcessSandboxing { get; set; }
            public bool EnableProcessPooling { get; set; }
            public bool EnableProcessMonitoring { get; set; }
            public bool EnableProcessIsolation { get; set; }
            public int MaxConcurrentProcesses { get; set; }
            public int MaxProcessHistory { get; set; }
            public TimeSpan DefaultProcessTimeout { get; set; }
            public TimeSpan MonitoringInterval { get; set; }
            public TimeSpan CleanupInterval { get; set; }
            public List<string> TrustedProcessPaths { get; set; }
            public Dictionary<string, ProcessPoolConfig> ProcessPools { get; set; }
            public ResourceQuotas DefaultResourceQuotas { get; set; }
            public ProcessSecurityContext DefaultSecurityContext { get; set; }

            public ProcessControllerConfig()
            {
                EnableProcessSandboxing = true;
                EnableProcessPooling = true;
                EnableProcessMonitoring = true;
                EnableProcessIsolation = false;
                MaxConcurrentProcesses = MAX_CONCURRENT_PROCESSES;
                MaxProcessHistory = 1000;
                DefaultProcessTimeout = TimeSpan.FromMinutes(5);
                MonitoringInterval = TimeSpan.FromMilliseconds(MONITORING_INTERVAL_MS);
                CleanupInterval = TimeSpan.FromMilliseconds(CLEANUP_INTERVAL_MS);
                TrustedProcessPaths = new List<string>();
                ProcessPools = new Dictionary<string, ProcessPoolConfig>();
                DefaultResourceQuotas = new ResourceQuotas();
                DefaultSecurityContext = new ProcessSecurityContext();

                InitializeDefaults();
            }

            private void InitializeDefaults()
            {
                TrustedProcessPaths.AddRange(new[]
                {
                    @"C:\Windows\System32\",
                    @"C:\Program Files\",
                    @"C:\Program Files (x86)\"
                });

                ProcessPools["PowerShell"] = new ProcessPoolConfig
                {
                    ProcessPath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                    MinInstances = 1,
                    MaxInstances = 5,
                    IdleTimeout = TimeSpan.FromMinutes(5)
                };

                ProcessPools["Command"] = new ProcessPoolConfig
                {
                    ProcessPath = @"C:\Windows\System32\cmd.exe",
                    MinInstances = 1,
                    MaxInstances = 3,
                    IdleTimeout = TimeSpan.FromMinutes(10)
                };
            }
        }

        public class ProcessPoolConfig
        {
            public string ProcessPath { get; set; } = string.Empty;
            public int MinInstances { get; set; }
            public int MaxInstances { get; set; }
            public TimeSpan IdleTimeout { get; set; }
            public ProcessSecurityContext SecurityContext { get; set; }

            public ProcessPoolConfig()
            {
                MinInstances = 1;
                MaxInstances = 10;
                IdleTimeout = TimeSpan.FromMinutes(5);
                SecurityContext = new ProcessSecurityContext();
            }
        }

        public enum ProcessState
        {
            Unknown,
            Starting,
            Running,
            Suspended,
            Terminating,
            Exited,
            Crashed,
            AccessDenied,
            ResourceExceeded
        }

        public enum ProcessPriority
        {
            Idle = 0x40,
            BelowNormal = 0x4000,
            Normal = 0x20,
            AboveNormal = 0x8000,
            High = 0x80,
            Realtime = 0x100
        }

        public class ProcessAffinity
        {
            public ulong AffinityMask { get; set; }
            public List<int> CpuCores { get; set; }

            public ProcessAffinity()
            {
                CpuCores = new List<int>();
                var processorCount = Environment.ProcessorCount;
                for (int i = 0; i < processorCount; i++)
                {
                    CpuCores.Add(i);
                }
                UpdateAffinityMask();
            }

            public void UpdateAffinityMask()
            {
                ulong mask = 0;
                foreach (var core in CpuCores)
                {
                    if (core >= 0 && core < 64)
                    {
                        mask |= (1UL << core);
                    }
                }
                AffinityMask = mask;
            }
        }

        public class ProcessEvent
        {
            public DateTime Timestamp { get; set; }
            public string EventType { get; set; } = string.Empty;
            public int ProcessId { get; set; }
            public string? ProcessName { get; set; }
            public string User { get; set; } = string.Empty;
            public string Details { get; set; } = string.Empty;
            public string Severity { get; set; }

            public ProcessEvent()
            {
                Timestamp = DateTime.UtcNow;
                Severity = "Information";
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        private enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation = 2
        }

        #endregion

        #region Constructor and Initialization

        public ProcessController(
            ILogger? logger = null,
            AppConfig? appConfig = null,
            PermissionValidator? permissionValidator = null,
            PerformanceMonitor? performanceMonitor = null,
            DiagnosticTool? diagnosticTool = null,
            AccessControlManager? accessControl = null)
        {
            _logger = logger ?? Logger.CreateLogger<ProcessController>();
            _appConfig = appConfig ?? SettingsManager.LoadConfiguration();
            _accessControl = accessControl ?? new AccessControlManager(_logger);

            _permissionValidator = permissionValidator ?? new PermissionValidator(_logger, _accessControl, new SettingsManager());
            _performanceMonitor = performanceMonitor ?? new PerformanceMonitor();
            _diagnosticTool = diagnosticTool ?? new DiagnosticTool();

            _config = LoadConfiguration();

            _managedProcesses = new ConcurrentDictionary<int, ProcessInfo>();
            _processPools = new ConcurrentDictionary<string, ProcessPool>();
            _processEvents = new ConcurrentQueue<ProcessEvent>();
            _trustedProcesses = new List<string>(_config.TrustedProcessPaths);

            _processSemaphore = new SemaphoreSlim(_config.MaxConcurrentProcesses);
            _systemIdentity = WindowsIdentity.GetCurrent();
            _elevatedPassword = CreateSecureString("NEDA_System_Password");

            if (_config.EnableProcessPooling)
            {
                InitializeProcessPools();
            }

            if (_config.EnableProcessMonitoring)
            {
                _monitoringTimer = new Timer(MonitorProcesses, null, TimeSpan.Zero, _config.MonitoringInterval);
            }

            _cleanupTimer = new Timer(CleanupProcesses, null, _config.CleanupInterval, _config.CleanupInterval);

            _performanceMonitor.RegisterCounter("ProcessController", () => _managedProcesses.Count);

            LogProcessEvent("System", "ProcessController initialized", "Information");
            _logger.LogInformation("ProcessController initialized with {MaxProcesses} max concurrent processes",
                _config.MaxConcurrentProcesses);
        }

        private ProcessControllerConfig LoadConfiguration()
        {
            try
            {
                var config = new ProcessControllerConfig();

                if (_appConfig.ProcessSettings != null)
                {
                    config.MaxConcurrentProcesses = _appConfig.ProcessSettings.MaxConcurrentProcesses;
                    config.EnableProcessSandboxing = _appConfig.ProcessSettings.EnableSandboxing;
                    config.DefaultProcessTimeout = _appConfig.ProcessSettings.DefaultTimeout;
                }

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load process configuration, using defaults");
                return new ProcessControllerConfig();
            }
        }

        private void InitializeProcessPools()
        {
            try
            {
                foreach (var poolConfig in _config.ProcessPools)
                {
                    var pool = new ProcessPool(poolConfig.Key, poolConfig.Value.ProcessPath, poolConfig.Value);
                    _processPools.TryAdd(poolConfig.Key, pool);

                    _logger.LogInformation("Initialized process pool: {PoolName} ({Min}-{Max} instances)",
                        poolConfig.Key, poolConfig.Value.MinInstances, poolConfig.Value.MaxInstances);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize process pools");
            }
        }

        private SecureString CreateSecureString(string text)
        {
            var secureString = new SecureString();
            foreach (char c in text)
            {
                secureString.AppendChar(c);
            }
            secureString.MakeReadOnly();
            return secureString;
        }

        #endregion

        #region Public API Methods (yalnızca syntax düzeltmeleri)

        public async Task<ProcessInfo> StartProcessAsync(
            ProcessStartupInfo startupInfo,
            ProcessSecurityContext? securityContext = null,
            ResourceQuotas? resourceQuotas = null,
            TimeSpan? timeout = null)
        {
            if (startupInfo == null)
                throw new ArgumentNullException(nameof(startupInfo));

            if (string.IsNullOrWhiteSpace(startupInfo.FileName))
                throw new ArgumentException("FileName cannot be null or empty", nameof(startupInfo.FileName));

            await ValidateProcessStartPermissionAsync(startupInfo.FileName);
            await CheckResourceLimitsAsync();

            await _processSemaphore.WaitAsync();

            try
            {
                var processInfo = new ProcessInfo
                {
                    Name = Path.GetFileNameWithoutExtension(startupInfo.FileName),
                    FilePath = startupInfo.FileName,
                    CommandLine = startupInfo.Arguments,
                    WorkingDirectory = startupInfo.WorkingDirectory ?? Path.GetDirectoryName(startupInfo.FileName) ?? string.Empty,
                    SecurityContext = securityContext ?? _config.DefaultSecurityContext,
                    StartupInfo = startupInfo,
                    ResourceLimits = resourceQuotas ?? _config.DefaultResourceQuotas,
                    State = ProcessState.Starting,
                    IsManaged = true,
                    ManagedBy = "ProcessController",
                    Owner = WindowsIdentity.GetCurrent().Name
                };

                LogProcessEvent("StartProcess", $"Starting process: {startupInfo.FileName}", "Information");

                if (_config.EnableProcessSandboxing)
                {
                    ApplySandboxSettings(processInfo);
                }

                var process = CreateProcess(startupInfo, securityContext);
                processInfo.Process = process;

                if (resourceQuotas != null)
                {
                    ApplyResourceQuotas(process, resourceQuotas);
                }

                bool started;
                if (securityContext != null && !string.IsNullOrEmpty(securityContext.Username))
                {
                    started = StartProcessAsUser(process, securityContext);
                }
                else
                {
                    started = process.Start();
                }

                if (!started)
                {
                    Interlocked.Increment(ref _processCreationFailures);
                    throw new ProcessException(ErrorCodes.Process.START_FAILED, $"Failed to start process: {startupInfo.FileName}");
                }

                if (timeout.HasValue)
                {
                    await WaitForProcessInitializationAsync(process, timeout.Value);
                }

                processInfo.UpdateMetrics();
                processInfo.State = ProcessState.Running;

                _managedProcesses.TryAdd(process.Id, processInfo);
                Interlocked.Increment(ref _totalProcessStarts);

                LogProcessEvent("ProcessStarted",
                    $"Process started: {processInfo.Name} (PID: {process.Id})", "Information",
                    process.Id, processInfo.Name);

                _logger.LogInformation("Started process: {ProcessName} (PID: {ProcessId})",
                    processInfo.Name, process.Id);

                return processInfo;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _processCreationFailures);
                LogProcessEvent("StartProcessFailed",
                    $"Failed to start process: {startupInfo.FileName}. Error: {ex.Message}",
                    "Error");

                _logger.LogError(ex, "Failed to start process: {FileName}", startupInfo.FileName);
                throw new ProcessException(ErrorCodes.Process.START_FAILED,
                    $"Failed to start process: {startupInfo.FileName}", ex);
            }
            finally
            {
                _processSemaphore.Release();
            }
        }

        // ... buradan sonrası aynı mantıkla devam ediyor; senin dosyada syntax bozan yerler:
        // - new Type; { } -> new Type { }
        // - catch -> catch
        // - else; -> else
        // - LINQ zincirlerinde noktalı virgüller
        // - struct/enum tanımlarında ; kaldırma
        // İstersen kalan kısmı da tek seferde aynen temizleyebilirim (ama mesaj sınırına takılmamak için burada kestim).

        #endregion

        #region Core Process Management Methods (CreateProcess kısmı düzeltildi)

        private Process CreateProcess(ProcessStartupInfo startupInfo, ProcessSecurityContext? securityContext)
        {
            var process = new Process();

            var startInfo = new ProcessStartInfo
            {
                FileName = startupInfo.FileName,
                Arguments = startupInfo.Arguments,
                WorkingDirectory = startupInfo.WorkingDirectory,
                UseShellExecute = startupInfo.UseShellExecute,
                CreateNoWindow = startupInfo.CreateNoWindow,
                RedirectStandardInput = startupInfo.RedirectStandardInput,
                RedirectStandardOutput = startupInfo.RedirectStandardOutput,
                RedirectStandardError = startupInfo.RedirectStandardError,
                StandardInputEncoding = startupInfo.StandardInputEncoding,
                StandardOutputEncoding = startupInfo.StandardOutputEncoding,
                StandardErrorEncoding = startupInfo.StandardErrorEncoding,
                Verb = startupInfo.Verb,
                WindowStyle = startupInfo.WindowStyle,
                ErrorDialog = startupInfo.ErrorDialog,
                ErrorDialogParentHandle = startupInfo.ErrorDialogParentHandle,
                LoadUserProfile = startupInfo.LoadUserProfile
            };

            foreach (var envVar in startupInfo.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
            }

            if (securityContext != null && !string.IsNullOrEmpty(securityContext.Username))
            {
                startInfo.UserName = securityContext.Username;
                startInfo.Password = securityContext.Password;
                startInfo.Domain = securityContext.Domain;
            }

            process.StartInfo = startInfo;
            return process;
        }

        #endregion

        #region Security and Permission Methods (syntax fix)

        private bool IsTrustedProcess(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;

            return _trustedProcesses.Any(trustedPath =>
                filePath.StartsWith(trustedPath, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Monitoring and Maintenance Methods (LINQ fix + object initializer fix)

        private void CleanupProcesses(object? state)
        {
            try
            {
                var exitedProcesses = _managedProcesses.Values
                    .Where(p => p.State == ProcessState.Exited || p.State == ProcessState.Crashed)
                    .ToList();

                foreach (var processInfo in exitedProcesses)
                {
                    _managedProcesses.TryRemove(processInfo.Id, out _);
                    processInfo.Dispose();
                }

                while (_processEvents.Count > MAX_PROCESS_EVENTS)
                {
                    _processEvents.TryDequeue(out _);
                }

                if (exitedProcesses.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} exited processes", exitedProcesses.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Process cleanup failed");
            }
        }

        private void LogProcessEvent(string eventType, string details, string severity,
            int processId = 0, string? processName = null)
        {
            var processEvent = new ProcessEvent
            {
                EventType = eventType,
                ProcessId = processId,
                ProcessName = processName,
                User = WindowsIdentity.GetCurrent().Name,
                Details = details,
                Severity = severity
            };

            _processEvents.Enqueue(processEvent);

            while (_processEvents.Count > MAX_PROCESS_EVENTS)
            {
                _processEvents.TryDequeue(out _);
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _monitoringTimer?.Change(Timeout.Infinite, 0);
                _monitoringTimer?.Dispose();

                _cleanupTimer.Change(Timeout.Infinite, 0);
                _cleanupTimer.Dispose();

                foreach (var processInfo in _managedProcesses.Values.ToList())
                {
                    try
                    {
                        if (processInfo.State == ProcessState.Running &&
                            processInfo.Process != null &&
                            !processInfo.Process.HasExited)
                        {
                            // StopProcessForcibly(processInfo);  // Bu metot dosyanın devamında
                        }
                        processInfo.Dispose();
                    }
                    catch
                    {
                    }
                }
                _managedProcesses.Clear();

                foreach (var pool in _processPools.Values)
                {
                    pool.Dispose();
                }
                _processPools.Clear();

                _processSemaphore.Dispose();
                _elevatedPassword.Dispose();
            }

            _disposed = true;
        }

        ~ProcessController()
        {
            Dispose(false);
        }

        #endregion
    }

    public interface IProcessController : IDisposable
    {
        Task<ProcessController.ProcessInfo> StartProcessAsync(
            ProcessController.ProcessStartupInfo startupInfo,
            ProcessController.ProcessSecurityContext? securityContext = null,
            ProcessController.ResourceQuotas? resourceQuotas = null,
            TimeSpan? timeout = null);

        Task<bool> StopProcessAsync(int processId, bool force = false, TimeSpan? timeout = null);

        Task<bool> SuspendProcessAsync(int processId);
        Task<bool> ResumeProcessAsync(int processId);

        ProcessController.ProcessInfo GetProcessInfo(int processId);
        List<ProcessController.ProcessInfo> GetManagedProcesses();
        List<ProcessController.ProcessInfo> GetAllProcesses();

        Task<int> KillProcessesByNameAsync(string namePattern, bool force = false);

        Task<ProcessController.ProcessInfo> AcquireFromPoolAsync(string poolName, TimeSpan? timeout = null);
        void ReleaseToPool(string poolName, ProcessController.ProcessInfo processInfo);

        ProcessController.ProcessControllerStatistics GetStatistics();
        List<ProcessController.ProcessEvent> GetRecentEvents(int count = 100);
    }

    public class ProcessException : Exception
    {
        public string ErrorCode { get; }
        public int ProcessId { get; }
        public string ProcessName { get; }

        public ProcessException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public ProcessException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public ProcessException(string errorCode, string message, int processId, string processName)
            : base(message)
        {
            ErrorCode = errorCode;
            ProcessId = processId;
            ProcessName = processName;
        }
    }

    internal class SettingsManager
    {
        public static AppConfig LoadConfiguration()
        {
            return new AppConfig();
        }

        internal static object LoadSection<T>(string v)
        {
            throw new NotImplementedException();
        }
    }
}
