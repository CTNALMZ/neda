using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NEDA.Build.CI_CD;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.EngineIntegration.BuildManager.Interfaces;
using NEDA.EngineIntegration.Unreal;
using NEDA.EngineIntegration.VisualStudio;
using NEDA.ExceptionHandling;
using NEDA.ExceptionHandling.RecoveryStrategies;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services;
using NEDA.SystemControl;
using NEDA.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.EngineIntegration.BuildManager;
{
    /// <summary>
    /// BuildEngine sınıfı - Çoklu platform ve motor için yapı süreçlerini yönetir;
    /// </summary>
    public class BuildEngine : IBuildEngine, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IFileService _fileService;
        private readonly ICompiler _compiler;
        private readonly IDeployment _deployment;
        private readonly IUnrealEngine _unrealEngine;
        private readonly IVSSolution _vsSolution;
        private readonly ISystemManager _systemManager;
        private readonly IRecoveryEngine _recoveryEngine;
        private readonly ICICDEngine _cicdEngine;

        private BuildConfiguration _currentConfig;
        private BuildStatus _buildStatus;
        private CancellationTokenSource _cancellationTokenSource;
        private Process _currentBuildProcess;
        private BuildMetrics _currentMetrics;
        private readonly object _buildLock = new object();
        private readonly Dictionary<string, BuildTarget> _registeredTargets;
        private readonly List<IBuildEventListener> _eventListeners;

        /// <summary>
        /// Geçerli yapı konfigürasyonu;
        /// </summary>
        public BuildConfiguration CurrentConfig;
        {
            get => _currentConfig;
            set;
            {
                if (_currentConfig != value)
                {
                    _currentConfig = value;
                    OnConfigurationChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Yapı durumu;
        /// </summary>
        public BuildStatus BuildStatus;
        {
            get => _buildStatus;
            private set;
            {
                if (_buildStatus != value)
                {
                    _buildStatus = value;
                    OnBuildStatusChanged?.Invoke(this, new BuildStatusChangedEventArgs(value));
                }
            }
        }

        /// <summary>
        /// Yapı metrikleri;
        /// </summary>
        public BuildMetrics CurrentMetrics => _currentMetrics;

        /// <summary>
        /// Kayıtlı hedefler;
        /// </summary>
        public IReadOnlyDictionary<string, BuildTarget> RegisteredTargets => _registeredTargets;

        /// <summary>
        /// Yapı çıktı dizini;
        /// </summary>
        public string OutputDirectory { get; private set; }

        /// <summary>
        /// Yapı log dizini;
        /// </summary>
        public string LogDirectory { get; private set; }

        /// <summary>
        /// Cache dizini;
        /// </summary>
        public string CacheDirectory { get; private set; }

        // Events;
        public event EventHandler OnBuildStarted;
        public event EventHandler<BuildCompletedEventArgs> OnBuildCompleted;
        public event EventHandler<BuildProgressEventArgs> OnBuildProgress;
        public event EventHandler<BuildErrorEventArgs> OnBuildError;
        public event EventHandler<BuildWarningEventArgs> OnBuildWarning;
        public event EventHandler<BuildStatusChangedEventArgs> OnBuildStatusChanged;
        public event EventHandler OnConfigurationChanged;
        public event EventHandler<BuildTargetEventArgs> OnTargetCompleted;

        /// <summary>
        /// BuildEngine constructor;
        /// </summary>
        public BuildEngine(
            ILogger logger,
            IPerformanceMonitor performanceMonitor,
            IFileService fileService,
            ICompiler compiler,
            IDeployment deployment,
            IUnrealEngine unrealEngine,
            IVSSolution vsSolution,
            ISystemManager systemManager,
            IRecoveryEngine recoveryEngine,
            ICICDEngine cicdEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
            _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
            _unrealEngine = unrealEngine ?? throw new ArgumentNullException(nameof(unrealEngine));
            _vsSolution = vsSolution ?? throw new ArgumentNullException(nameof(vsSolution));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _recoveryEngine = recoveryEngine ?? throw new ArgumentNullException(nameof(recoveryEngine));
            _cicdEngine = cicdEngine ?? throw new ArgumentNullException(nameof(cicdEngine));

            Initialize();
        }

        private void Initialize()
        {
            _logger.LogInformation("BuildEngine başlatılıyor...");

            _registeredTargets = new Dictionary<string, BuildTarget>();
            _eventListeners = new List<IBuildEventListener>();
            _currentMetrics = new BuildMetrics();
            _buildStatus = BuildStatus.Idle;

            // Dizinleri oluştur;
            InitializeDirectories();

            // Varsayılan konfigürasyon;
            _currentConfig = new BuildConfiguration;
            {
                Configuration = BuildConfigurationType.Development,
                Platform = BuildPlatform.Windows64,
                Optimization = OptimizationLevel.Debug,
                EnableDebugging = true,
                EnableProfiling = false,
                EnableCodeAnalysis = true,
                ParallelBuild = true,
                MaxParallelJobs = Environment.ProcessorCount,
                CleanBuild = false,
                GenerateDocumentation = false,
                AdditionalOptions = new Dictionary<string, string>()
            };

            // Varsayılan hedefleri kaydet;
            RegisterDefaultTargets();

            _logger.LogInformation("BuildEngine başlatıldı. Platform: {Platform}, CPU Çekirdekleri: {Cores}",
                _currentConfig.Platform, Environment.ProcessorCount);
        }

        private void InitializeDirectories()
        {
            try
            {
                var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEDA", "Build");

                OutputDirectory = Path.Combine(basePath, "Output");
                LogDirectory = Path.Combine(basePath, "Logs");
                CacheDirectory = Path.Combine(basePath, "Cache");

                Directory.CreateDirectory(OutputDirectory);
                Directory.CreateDirectory(LogDirectory);
                Directory.CreateDirectory(CacheDirectory);

                _logger.LogDebug("Build dizinleri oluşturuldu: {BasePath}", basePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Build dizinleri oluşturulamadı");
                throw new BuildEngineException("Build dizinleri başlatılamadı", ex);
            }
        }

        private void RegisterDefaultTargets()
        {
            try
            {
                // .NET Build Target;
                RegisterTarget(new BuildTarget;
                {
                    Name = "BuildDotNet",
                    DisplayName = ".NET Derleme",
                    Description = ".NET projelerini derler",
                    TargetType = BuildTargetType.Compilation,
                    Platform = BuildPlatform.Any,
                    Dependencies = Array.Empty<string>(),
                    Action = BuildDotNetProject;
                });

                // C++ Build Target;
                RegisterTarget(new BuildTarget;
                {
                    Name = "BuildCpp",
                    DisplayName = "C++ Derleme",
                    Description = "C++ projelerini derler",
                    TargetType = BuildTargetType.Compilation,
                    Platform = BuildPlatform.Windows64,
                    Dependencies = Array.Empty<string>(),
                    Action = BuildCppProject;
                });

                // Unreal Engine Build Target;
                RegisterTarget(new BuildTarget;
                {
                    Name = "BuildUnreal",
                    DisplayName = "Unreal Engine Derleme",
                    Description = "Unreal Engine projelerini derler",
                    TargetType = BuildTargetType.Compilation,
                    Platform = BuildPlatform.Windows64,
                    Dependencies = new[] { "BuildCpp" },
                    Action = BuildUnrealProject;
                });

                // Unit Test Target;
                RegisterTarget(new BuildTarget;
                {
                    Name = "RunUnitTests",
                    DisplayName = "Birim Testleri Çalıştır",
                    Description = "Birim testlerini çalıştırır",
                    TargetType = BuildTargetType.Testing,
                    Platform = BuildPlatform.Any,
                    Dependencies = new[] { "BuildDotNet" },
                    Action = RunUnitTests;
                });

                // Packaging Target;
                RegisterTarget(new BuildTarget;
                {
                    Name = "PackageApplication",
                    DisplayName = "Uygulama Paketleme",
                    Description = "Uygulamayı dağıtım için paketler",
                    TargetType = BuildTargetType.Packaging,
                    Platform = BuildPlatform.Windows64,
                    Dependencies = new[] { "BuildDotNet", "RunUnitTests" },
                    Action = PackageApplication;
                });

                // Deployment Target;
                RegisterTarget(new BuildTarget;
                {
                    Name = "DeployApplication",
                    DisplayName = "Uygulama Dağıtımı",
                    Description = "Uygulamayı hedef ortama dağıtır",
                    TargetType = BuildTargetType.Deployment,
                    Platform = BuildPlatform.Any,
                    Dependencies = new[] { "PackageApplication" },
                    Action = DeployApplication;
                });

                _logger.LogInformation("{Count} varsayılan hedef kaydedildi", _registeredTargets.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varsayılan hedefler kaydedilemedi");
            }
        }

        /// <summary>
        /// Yapı hedefini kaydeder;
        /// </summary>
        public void RegisterTarget(BuildTarget target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (string.IsNullOrWhiteSpace(target.Name))
                throw new ArgumentException("Hedef adı boş olamaz");

            lock (_buildLock)
            {
                if (_registeredTargets.ContainsKey(target.Name))
                {
                    _logger.LogWarning("Hedef zaten kayıtlı: {TargetName}", target.Name);
                    return;
                }

                _registeredTargets[target.Name] = target;
                _logger.LogDebug("Hedef kaydedildi: {TargetName}", target.Name);
            }
        }

        /// <summary>
        /// Yapı hedefini kaldırır;
        /// </summary>
        public bool UnregisterTarget(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return false;

            lock (_buildLock)
            {
                var removed = _registeredTargets.Remove(targetName);
                if (removed)
                {
                    _logger.LogDebug("Hedef kaldırıldı: {TargetName}", targetName);
                }
                return removed;
            }
        }

        /// <summary>
        /// Yapı hedefini çalıştırır;
        /// </summary>
        public async Task<BuildResult> ExecuteTargetAsync(string targetName, BuildOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                throw new ArgumentException("Hedef adı boş olamaz");

            if (!_registeredTargets.TryGetValue(targetName, out var target))
                throw new KeyNotFoundException($"Hedef bulunamadı: {targetName}");

            _logger.LogInformation("Hedef çalıştırılıyor: {TargetName}", targetName);

            var result = new BuildResult;
            {
                TargetName = targetName,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Bağımlılıkları çalıştır;
                if (target.Dependencies?.Any() == true)
                {
                    foreach (var dependency in target.Dependencies)
                    {
                        var depResult = await ExecuteTargetAsync(dependency, options);
                        if (!depResult.Success)
                        {
                            result.Success = false;
                            result.ErrorMessage = $"Bağımlılık başarısız: {dependency}";
                            return result;
                        }
                    }
                }

                // Hedefi çalıştır;
                result = await target.Action(target, options ?? new BuildOptions());
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                OnTargetCompleted?.Invoke(this, new BuildTargetEventArgs(target, result));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hedef çalıştırma hatası: {TargetName}", targetName);

                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                OnBuildError?.Invoke(this, new BuildErrorEventArgs(
                    $"Hedef çalıştırma hatası: {targetName}",
                    ex,
                    BuildErrorType.ExecutionError));

                return result;
            }
        }

        /// <summary>
        /// Çoklu hedefleri çalıştırır;
        /// </summary>
        public async Task<MultiBuildResult> ExecuteTargetsAsync(IEnumerable<string> targetNames, BuildOptions options = null)
        {
            var targets = targetNames?.ToList() ?? new List<string>();
            if (!targets.Any())
                throw new ArgumentException("En az bir hedef belirtilmelidir");

            var result = new MultiBuildResult;
            {
                StartTime = DateTime.UtcNow;
            };

            _logger.LogInformation("Çoklu hedefler çalıştırılıyor: {Targets}", string.Join(", ", targets));

            try
            {
                var tasks = targets.Select(targetName => ExecuteTargetAsync(targetName, options));
                var results = await Task.WhenAll(tasks);

                result.Results.AddRange(results);
                result.Success = results.All(r => r.Success);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                _logger.LogInformation("Çoklu hedefler tamamlandı. Başarılı: {SuccessCount}/{Total}",
                    results.Count(r => r.Success), results.Length);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Çoklu hedefler çalıştırma hatası");

                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
        }

        /// <summary>
        /// Tam yapı işlemini başlatır;
        /// </summary>
        public async Task<BuildResult> BuildAsync(string projectPath, BuildConfiguration config = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu boş olamaz");

            if (!File.Exists(projectPath) && !Directory.Exists(projectPath))
                throw new FileNotFoundException("Proje bulunamadı", projectPath);

            lock (_buildLock)
            {
                if (BuildStatus == BuildStatus.Building)
                    throw new BuildEngineException("Yapı işlemi zaten devam ediyor");

                BuildStatus = BuildStatus.Building;
                _cancellationTokenSource = new CancellationTokenSource();
            }

            var buildConfig = config ?? _currentConfig;
            var result = new BuildResult;
            {
                ProjectPath = projectPath,
                Configuration = buildConfig.Configuration,
                Platform = buildConfig.Platform,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                _logger.LogInformation("Yapı başlatılıyor: {Project}, Konfig: {Config}, Platform: {Platform}",
                    Path.GetFileName(projectPath), buildConfig.Configuration, buildConfig.Platform);

                OnBuildStarted?.Invoke(this, EventArgs.Empty);

                // Metrikleri sıfırla;
                ResetMetrics();
                _currentMetrics.ProjectName = Path.GetFileNameWithoutExtension(projectPath);
                _currentMetrics.Configuration = buildConfig.Configuration.ToString();
                _currentMetrics.Platform = buildConfig.Platform.ToString();

                // Performans izlemeyi başlat;
                _performanceMonitor.StartMonitoring("BuildProcess");

                // Proje türüne göre yapı stratejisi seç;
                BuildResult buildResult;
                if (IsDotNetProject(projectPath))
                {
                    buildResult = await BuildDotNetProjectAsync(projectPath, buildConfig);
                }
                else if (IsCppProject(projectPath))
                {
                    buildResult = await BuildCppProjectAsync(projectPath, buildConfig);
                }
                else if (IsUnrealProject(projectPath))
                {
                    buildResult = await BuildUnrealProjectAsync(projectPath, buildConfig);
                }
                else;
                {
                    throw new BuildEngineException($"Desteklenmeyen proje türü: {projectPath}");
                }

                result.Merge(buildResult);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                // Metrikleri güncelle;
                UpdateMetrics(result);

                if (result.Success)
                {
                    BuildStatus = BuildStatus.Success;
                    _logger.LogInformation("Yapı başarıyla tamamlandı. Süre: {Duration}, Boyut: {Size}",
                        result.Duration, FormatFileSize(result.OutputSize));
                }
                else;
                {
                    BuildStatus = BuildStatus.Failed;
                    _logger.LogWarning("Yapı başarısız. Hata: {Error}", result.ErrorMessage);
                }

                OnBuildCompleted?.Invoke(this, new BuildCompletedEventArgs(result));

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Yapı işlemi kullanıcı tarafından iptal edildi");

                result.Success = false;
                result.ErrorMessage = "Yapı işlemi iptal edildi";
                BuildStatus = BuildStatus.Cancelled;

                OnBuildCompleted?.Invoke(this, new BuildCompletedEventArgs(result));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Yapı işlemi sırasında beklenmeyen hata");

                result.Success = false;
                result.ErrorMessage = ex.Message;
                BuildStatus = BuildStatus.Failed;

                OnBuildError?.Invoke(this, new BuildErrorEventArgs(
                    "Yapı işlemi hatası",
                    ex,
                    BuildErrorType.UnexpectedError));
                OnBuildCompleted?.Invoke(this, new BuildCompletedEventArgs(result));

                return result;
            }
            finally
            {
                _performanceMonitor.StopMonitoring("BuildProcess");
                _currentBuildProcess?.Dispose();
                _currentBuildProcess = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// Yapı işlemini iptal eder;
        /// </summary>
        public void CancelBuild()
        {
            lock (_buildLock)
            {
                if (BuildStatus != BuildStatus.Building || _cancellationTokenSource == null)
                {
                    _logger.LogWarning("İptal edilecek aktif yapı işlemi yok");
                    return;
                }

                _logger.LogInformation("Yapı işlemi iptal ediliyor...");

                try
                {
                    _cancellationTokenSource.Cancel();

                    if (_currentBuildProcess != null && !_currentBuildProcess.HasExited)
                    {
                        _currentBuildProcess.Kill(true);
                    }

                    BuildStatus = BuildStatus.Cancelled;
                    _logger.LogInformation("Yapı işlemi başarıyla iptal edildi");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Yapı işlemi iptal edilirken hata oluştu");
                }
            }
        }

        /// <summary>
        /// Temiz yapı (clean build) yapar;
        /// </summary>
        public async Task<BuildResult> CleanBuildAsync(string projectPath, BuildConfiguration config = null)
        {
            _logger.LogInformation("Temiz yapı başlatılıyor: {Project}", Path.GetFileName(projectPath));

            try
            {
                // Çıktı dizinini temizle;
                var outputDir = GetOutputDirectory(projectPath, config ?? _currentConfig);
                if (Directory.Exists(outputDir))
                {
                    _logger.LogDebug("Çıktı dizini temizleniyor: {OutputDir}", outputDir);
                    Directory.Delete(outputDir, true);
                }

                // Ara dosyaları temizle;
                await CleanIntermediateFilesAsync(projectPath);

                // Normal yapıyı çalıştır;
                var buildConfig = config ?? _currentConfig;
                buildConfig.CleanBuild = true;

                return await BuildAsync(projectPath, buildConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Temiz yapı hatası");
                throw new BuildEngineException("Temiz yapı başarısız", ex);
            }
        }

        /// <summary>
        /// .NET projesi derler;
        /// </summary>
        private async Task<BuildResult> BuildDotNetProject(BuildTarget target, BuildOptions options)
        {
            var projectPath = options?.ProjectPath;
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu gereklidir");

            _logger.LogInformation(".NET projesi derleniyor: {Project}", Path.GetFileName(projectPath));

            var result = new BuildResult;
            {
                TargetName = target.Name,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var config = options?.Configuration ?? _currentConfig;
                var buildResult = await BuildDotNetProjectAsync(projectPath, config);

                result.Merge(buildResult);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ".NET projesi derleme hatası");
                throw;
            }
        }

        private async Task<BuildResult> BuildDotNetProjectAsync(string projectPath, BuildConfiguration config)
        {
            var result = new BuildResult();
            var processStartInfo = new ProcessStartInfo;
            {
                FileName = "dotnet",
                Arguments = BuildDotNetArguments(projectPath, config),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true;
            };

            _logger.LogDebug("dotnet komutu: {Command} {Args}", processStartInfo.FileName, processStartInfo.Arguments);

            try
            {
                using var process = new Process { StartInfo = processStartInfo };
                _currentBuildProcess = process;

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        OnBuildProgress?.Invoke(this, new BuildProgressEventArgs(e.Data, ProgressType.Output));

                        if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
                        {
                            OnBuildError?.Invoke(this, new BuildErrorEventArgs(e.Data, null, BuildErrorType.CompilationError));
                        }
                        else if (e.Data.Contains("warning", StringComparison.OrdinalIgnoreCase))
                        {
                            OnBuildWarning?.Invoke(this, new BuildWarningEventArgs(e.Data, BuildWarningType.CompilerWarning));
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        OnBuildError?.Invoke(this, new BuildErrorEventArgs(e.Data, null, BuildErrorType.CompilationError));
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(_cancellationTokenSource.Token);

                result.ExitCode = process.ExitCode;
                result.Output = outputBuilder.ToString();
                result.ErrorOutput = errorBuilder.ToString();
                result.Success = process.ExitCode == 0;

                if (!result.Success)
                {
                    result.ErrorMessage = $"dotnet build failed with exit code {process.ExitCode}";
                }
                else;
                {
                    // Çıktı dosyasını bul;
                    var outputFile = FindOutputFile(projectPath, config);
                    if (File.Exists(outputFile))
                    {
                        result.OutputPath = outputFile;
                        result.OutputSize = new FileInfo(outputFile).Length;
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "dotnet build process hatası");
                throw new BuildEngineException("dotnet build işlemi başarısız", ex);
            }
        }

        private string BuildDotNetArguments(string projectPath, BuildConfiguration config)
        {
            var args = new StringBuilder("build");

            // Proje dosyası;
            args.Append($" \"{projectPath}\"");

            // Konfigürasyon;
            args.Append($" --configuration {config.Configuration}");

            // Platform;
            if (config.Platform != BuildPlatform.Any)
            {
                args.Append($" --runtime {GetRuntimeIdentifier(config.Platform)}");
            }

            // Output dizini;
            var outputDir = GetOutputDirectory(projectPath, config);
            args.Append($" --output \"{outputDir}\"");

            // Optimizasyon;
            if (config.Optimization != OptimizationLevel.Debug)
            {
                args.Append($" -p:Optimize=true");
            }

            // Paralel derleme;
            if (config.ParallelBuild)
            {
                args.Append($" --max-cpu-count:{config.MaxParallelJobs}");
            }

            // Temiz yapı;
            if (config.CleanBuild)
            {
                args.Append(" --no-incremental");
            }

            // Debug sembolleri;
            if (!config.EnableDebugging)
            {
                args.Append(" -p:DebugType=None");
            }

            // Ek seçenekler;
            foreach (var option in config.AdditionalOptions)
            {
                args.Append($" -p:{option.Key}={option.Value}");
            }

            return args.ToString();
        }

        /// <summary>
        /// C++ projesi derler;
        /// </summary>
        private async Task<BuildResult> BuildCppProject(BuildTarget target, BuildOptions options)
        {
            var projectPath = options?.ProjectPath;
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu gereklidir");

            _logger.LogInformation("C++ projesi derleniyor: {Project}", Path.GetFileName(projectPath));

            var result = new BuildResult;
            {
                TargetName = target.Name,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var config = options?.Configuration ?? _currentConfig;
                var buildResult = await _compiler.CompileAsync(projectPath, config);

                result.Merge(buildResult);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "C++ projesi derleme hatası");
                throw;
            }
        }

        private async Task<BuildResult> BuildCppProjectAsync(string projectPath, BuildConfiguration config)
        {
            return await _compiler.CompileAsync(projectPath, config);
        }

        /// <summary>
        /// Unreal Engine projesi derler;
        /// </summary>
        private async Task<BuildResult> BuildUnrealProject(BuildTarget target, BuildOptions options)
        {
            var projectPath = options?.ProjectPath;
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu gereklidir");

            _logger.LogInformation("Unreal Engine projesi derleniyor: {Project}", Path.GetFileName(projectPath));

            var result = new BuildResult;
            {
                TargetName = target.Name,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var config = options?.Configuration ?? _currentConfig;
                var buildResult = await _unrealEngine.BuildProjectAsync(projectPath, config);

                result.Merge(buildResult);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unreal Engine projesi derleme hatası");
                throw;
            }
        }

        private async Task<BuildResult> BuildUnrealProjectAsync(string projectPath, BuildConfiguration config)
        {
            return await _unrealEngine.BuildProjectAsync(projectPath, config);
        }

        /// <summary>
        /// Birim testlerini çalıştırır;
        /// </summary>
        private async Task<BuildResult> RunUnitTests(BuildTarget target, BuildOptions options)
        {
            var projectPath = options?.ProjectPath;
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu gereklidir");

            _logger.LogInformation("Birim testleri çalıştırılıyor: {Project}", Path.GetFileName(projectPath));

            var result = new BuildResult;
            {
                TargetName = target.Name,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var processStartInfo = new ProcessStartInfo;
                {
                    FileName = "dotnet",
                    Arguments = $"test \"{projectPath}\" --verbosity normal",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true;
                };

                using var process = new Process { StartInfo = processStartInfo };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        OnBuildProgress?.Invoke(this, new BuildProgressEventArgs(e.Data, ProgressType.TestOutput));
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                result.ExitCode = process.ExitCode;
                result.Output = outputBuilder.ToString();
                result.ErrorOutput = errorBuilder.ToString();
                result.Success = process.ExitCode == 0;

                if (!result.Success)
                {
                    result.ErrorMessage = "Birim testleri başarısız";
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Birim testleri çalıştırma hatası");
                throw;
            }
        }

        /// <summary>
        /// Uygulamayı paketler;
        /// </summary>
        private async Task<BuildResult> PackageApplication(BuildTarget target, BuildOptions options)
        {
            var projectPath = options?.ProjectPath;
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Proje yolu gereklidir");

            _logger.LogInformation("Uygulama paketleniyor: {Project}", Path.GetFileName(projectPath));

            var result = new BuildResult;
            {
                TargetName = target.Name,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var config = options?.Configuration ?? _currentConfig;

                // .NET publish komutu;
                var processStartInfo = new ProcessStartInfo;
                {
                    FileName = "dotnet",
                    Arguments = $"publish \"{projectPath}\" --configuration {config.Configuration} --output \"{OutputDirectory}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true;
                };

                using var process = new Process { StartInfo = processStartInfo };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        OnBuildProgress?.Invoke(this, new BuildProgressEventArgs(e.Data, ProgressType.Packaging));
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                result.ExitCode = process.ExitCode;
                result.Output = outputBuilder.ToString();
                result.ErrorOutput = errorBuilder.ToString();
                result.Success = process.ExitCode == 0;

                if (result.Success)
                {
                    result.OutputPath = OutputDirectory;
                    result.OutputSize = CalculateDirectorySize(OutputDirectory);
                }
                else;
                {
                    result.ErrorMessage = "Paketleme başarısız";
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Uygulama paketleme hatası");
                throw;
            }
        }

        /// <summary>
        /// Uygulamayı dağıtır;
        /// </summary>
        private async Task<BuildResult> DeployApplication(BuildTarget target, BuildOptions options)
        {
            _logger.LogInformation("Uygulama dağıtılıyor");

            var result = new BuildResult;
            {
                TargetName = target.Name,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var deployResult = await _deployment.DeployAsync(OutputDirectory, options?.DeploymentTarget);

                result.Merge(deployResult);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Uygulama dağıtım hatası");
                throw;
            }
        }

        /// <summary>
        /// Proje türünü kontrol eder;
        /// </summary>
        private bool IsDotNetProject(string path)
        {
            var extension = Path.GetExtension(path).ToLower();
            return extension == ".csproj" || extension == ".fsproj" || extension == ".vbproj";
        }

        private bool IsCppProject(string path)
        {
            var extension = Path.GetExtension(path).ToLower();
            return extension == ".vcxproj" || extension == ".cppproj";
        }

        private bool IsUnrealProject(string path)
        {
            var extension = Path.GetExtension(path).ToLower();
            return extension == ".uproject";
        }

        /// <summary>
        /// Çıktı dizinini alır;
        /// </summary>
        private string GetOutputDirectory(string projectPath, BuildConfiguration config)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var configName = config.Configuration.ToString();
            var platformName = config.Platform.ToString();

            return Path.Combine(OutputDirectory, projectName, configName, platformName);
        }

        /// <summary>
        /// Runtime identifier alır;
        /// </summary>
        private string GetRuntimeIdentifier(BuildPlatform platform)
        {
            return platform switch;
            {
                BuildPlatform.Windows64 => "win-x64",
                BuildPlatform.Windows32 => "win-x86",
                BuildPlatform.Linux64 => "linux-x64",
                BuildPlatform.LinuxArm64 => "linux-arm64",
                BuildPlatform.MacOS64 => "osx-x64",
                BuildPlatform.MacOSArm64 => "osx-arm64",
                _ => "any"
            };
        }

        /// <summary>
        /// Çıktı dosyasını bulur;
        /// </summary>
        private string FindOutputFile(string projectPath, BuildConfiguration config)
        {
            var outputDir = GetOutputDirectory(projectPath, config);
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var extension = IsDotNetProject(projectPath) ? ".dll" : ".exe";

            var possiblePaths = new[]
            {
                Path.Combine(outputDir, $"{projectName}{extension}"),
                Path.Combine(outputDir, $"{projectName}.dll"),
                Path.Combine(outputDir, $"{projectName}.exe")
            };

            return possiblePaths.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// Ara dosyaları temizler;
        /// </summary>
        private async Task CleanIntermediateFilesAsync(string projectPath)
        {
            try
            {
                var projectDir = Path.GetDirectoryName(projectPath);
                var intermediateDirs = new[]
                {
                    Path.Combine(projectDir, "bin"),
                    Path.Combine(projectDir, "obj"),
                    Path.Combine(projectDir, "Build"),
                    Path.Combine(projectDir, "Intermediate")
                };

                foreach (var dir in intermediateDirs)
                {
                    if (Directory.Exists(dir))
                    {
                        await Task.Run(() => Directory.Delete(dir, true));
                        _logger.LogDebug("Ara dizin temizlendi: {Dir}", dir);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ara dosyalar temizlenirken hata");
            }
        }

        /// <summary>
        /// Dizin boyutunu hesaplar;
        /// </summary>
        private long CalculateDirectorySize(string directory)
        {
            if (!Directory.Exists(directory))
                return 0;

            long size = 0;
            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch
                {
                    // İzin hatası olabilir, görmezden gel;
                }
            }

            return size;
        }

        /// <summary>
        /// Dosya boyutunu formatlar;
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Metrikleri sıfırlar;
        /// </summary>
        private void ResetMetrics()
        {
            _currentMetrics = new BuildMetrics;
            {
                StartTime = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Metrikleri günceller;
        /// </summary>
        private void UpdateMetrics(BuildResult result)
        {
            _currentMetrics.EndTime = DateTime.UtcNow;
            _currentMetrics.Duration = _currentMetrics.EndTime - _currentMetrics.StartTime;
            _currentMetrics.Success = result.Success;
            _currentMetrics.ErrorCount = result.Success ? 0 : 1;
            _currentMetrics.OutputSize = result.OutputSize;

            // Performans metriklerini al;
            var perfMetrics = _performanceMonitor.GetMetrics("BuildProcess");
            if (perfMetrics != null)
            {
                _currentMetrics.CpuUsage = perfMetrics.CpuUsage;
                _currentMetrics.MemoryUsage = perfMetrics.MemoryUsage;
                _currentMetrics.DiskUsage = perfMetrics.DiskUsage;
            }
        }

        /// <summary>
        /// Build event listener ekler;
        /// </summary>
        public void AddEventListener(IBuildEventListener listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            lock (_eventListeners)
            {
                if (!_eventListeners.Contains(listener))
                {
                    _eventListeners.Add(listener);
                    _logger.LogDebug("Build event listener eklendi: {Type}", listener.GetType().Name);
                }
            }
        }

        /// <summary>
        /// Build event listener kaldırır;
        /// </summary>
        public void RemoveEventListener(IBuildEventListener listener)
        {
            if (listener == null)
                return;

            lock (_eventListeners)
            {
                _eventListeners.Remove(listener);
                _logger.LogDebug("Build event listener kaldırıldı: {Type}", listener.GetType().Name);
            }
        }

        /// <summary>
        /// Build cache'ini temizler;
        /// </summary>
        public async Task ClearCacheAsync()
        {
            _logger.LogInformation("Build cache temizleniyor...");

            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    await Task.Run(() => Directory.Delete(CacheDirectory, true));
                    Directory.CreateDirectory(CacheDirectory);
                    _logger.LogInformation("Build cache başarıyla temizlendi");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Build cache temizleme hatası");
                throw new BuildEngineException("Build cache temizlenemedi", ex);
            }
        }

        /// <summary>
        /// Build loglarını alır;
        /// </summary>
        public IEnumerable<string> GetBuildLogs(string buildId = null)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return Enumerable.Empty<string>();

                var logFiles = string.IsNullOrEmpty(buildId)
                    ? Directory.GetFiles(LogDirectory, "*.log")
                    : Directory.GetFiles(LogDirectory, $"*{buildId}*.log");

                return logFiles.Select(File.ReadAllText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Build logları alınamadı");
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// CI/CD pipeline'ını başlatır;
        /// </summary>
        public async Task<CICDResult> StartCICDPipelineAsync(string pipelineName, Dictionary<string, string> parameters = null)
        {
            _logger.LogInformation("CI/CD pipeline başlatılıyor: {Pipeline}", pipelineName);

            try
            {
                var result = await _cicdEngine.ExecutePipelineAsync(pipelineName, parameters);

                if (result.Success)
                {
                    _logger.LogInformation("CI/CD pipeline başarıyla tamamlandı: {Pipeline}", pipelineName);
                }
                else;
                {
                    _logger.LogWarning("CI/CD pipeline başarısız: {Pipeline}, Hata: {Error}",
                        pipelineName, result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CI/CD pipeline başlatma hatası");
                throw;
            }
        }

        /// <summary>
        /// Dispose pattern;
        /// </summary>
        public void Dispose()
        {
            _logger.LogInformation("BuildEngine kapatılıyor...");

            try
            {
                CancelBuild();

                _cancellationTokenSource?.Dispose();
                _currentBuildProcess?.Dispose();

                _registeredTargets.Clear();
                _eventListeners.Clear();

                GC.SuppressFinalize(this);

                _logger.LogInformation("BuildEngine başarıyla kapatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BuildEngine kapatılırken hata oluştu");
            }
        }
    }

    #region Supporting Types;

    /// <summary>
    /// Build konfigürasyon türleri;
    /// </summary>
    public enum BuildConfigurationType;
    {
        Debug,
        Development,
        Testing,
        Staging,
        Production,
        Release;
    }

    /// <summary>
    /// Build platformları;
    /// </summary>
    public enum BuildPlatform;
    {
        Any,
        Windows32,
        Windows64,
        Linux64,
        LinuxArm64,
        MacOS64,
        MacOSArm64,
        Android,
        iOS,
        WebGL;
    }

    /// <summary>
    /// Optimizasyon seviyeleri;
    /// </summary>
    public enum OptimizationLevel;
    {
        Debug,
        Development,
        Release,
        Performance,
        Size;
    }

    /// <summary>
    /// Build durumu;
    /// </summary>
    public enum BuildStatus;
    {
        Idle,
        Building,
        Success,
        Failed,
        Cancelled,
        Warning;
    }

    /// <summary>
    /// Build target türleri;
    /// </summary>
    public enum BuildTargetType;
    {
        Compilation,
        Testing,
        Packaging,
        Deployment,
        Custom;
    }

    /// <summary>
    /// Build hata türleri;
    /// </summary>
    public enum BuildErrorType;
    {
        CompilationError,
        LinkerError,
        FileNotFound,
        PermissionDenied,
        OutOfMemory,
        Timeout,
        UnexpectedError,
        ExecutionError;
    }

    /// <summary>
    /// Build uyarı türleri;
    /// </summary>
    public enum BuildWarningType;
    {
        CompilerWarning,
        DeprecatedApi,
        PerformanceWarning,
        SecurityWarning,
        CodeStyleWarning;
    }

    /// <summary>
    /// İlerleme türleri;
    /// </summary>
    public enum ProgressType;
    {
        Compilation,
        Linking,
        Testing,
        Packaging,
        Deployment,
        Output,
        TestOutput,
        Packaging;
    }

    /// <summary>
    /// Build konfigürasyon sınıfı;
    /// </summary>
    public class BuildConfiguration;
    {
        public BuildConfigurationType Configuration { get; set; }
        public BuildPlatform Platform { get; set; }
        public OptimizationLevel Optimization { get; set; }
        public bool EnableDebugging { get; set; }
        public bool EnableProfiling { get; set; }
        public bool EnableCodeAnalysis { get; set; }
        public bool ParallelBuild { get; set; }
        public int MaxParallelJobs { get; set; }
        public bool CleanBuild { get; set; }
        public bool GenerateDocumentation { get; set; }
        public Dictionary<string, string> AdditionalOptions { get; set; }

        public BuildConfiguration()
        {
            AdditionalOptions = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Build target sınıfı;
    /// </summary>
    public class BuildTarget;
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public BuildTargetType TargetType { get; set; }
        public BuildPlatform Platform { get; set; }
        public string[] Dependencies { get; set; }
        public Func<BuildTarget, BuildOptions, Task<BuildResult>> Action { get; set; }

        public BuildTarget()
        {
            Dependencies = Array.Empty<string>();
        }
    }

    /// <summary>
    /// Build seçenekleri;
    /// </summary>
    public class BuildOptions;
    {
        public string ProjectPath { get; set; }
        public BuildConfiguration Configuration { get; set; }
        public string DeploymentTarget { get; set; }
        public Dictionary<string, string> Parameters { get; set; }

        public BuildOptions()
        {
            Parameters = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Build sonucu;
    /// </summary>
    public class BuildResult;
    {
        public bool Success { get; set; }
        public string ProjectPath { get; set; }
        public string TargetName { get; set; }
        public BuildConfigurationType Configuration { get; set; }
        public BuildPlatform Platform { get; set; }
        public string OutputPath { get; set; }
        public long OutputSize { get; set; }
        public string Output { get; set; }
        public string ErrorOutput { get; set; }
        public string ErrorMessage { get; set; }
        public int ExitCode { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public BuildResult()
        {
            Metadata = new Dictionary<string, object>();
        }

        public void Merge(BuildResult other)
        {
            if (other == null) return;

            Success = other.Success;
            OutputPath = other.OutputPath ?? OutputPath;
            OutputSize = other.OutputSize;
            Output = other.Output ?? Output;
            ErrorOutput = other.ErrorOutput ?? ErrorOutput;
            ErrorMessage = other.ErrorMessage ?? ErrorMessage;
            ExitCode = other.ExitCode;

            foreach (var kvp in other.Metadata)
            {
                Metadata[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Çoklu build sonucu;
    /// </summary>
    public class MultiBuildResult : BuildResult;
    {
        public List<BuildResult> Results { get; set; }

        public MultiBuildResult()
        {
            Results = new List<BuildResult>();
        }
    }

    /// <summary>
    /// Build metrikleri;
    /// </summary>
    public class BuildMetrics;
    {
        public string ProjectName { get; set; }
        public string Configuration { get; set; }
        public string Platform { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public long OutputSize { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double DiskUsage { get; set; }
        public int FileCount { get; set; }

        public BuildMetrics()
        {
            StartTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Build event listener interface;
    /// </summary>
    public interface IBuildEventListener;
    {
        void OnBuildStarted(object sender, EventArgs e);
        void OnBuildCompleted(object sender, BuildCompletedEventArgs e);
        void OnBuildProgress(object sender, BuildProgressEventArgs e);
        void OnBuildError(object sender, BuildErrorEventArgs e);
        void OnBuildWarning(object sender, BuildWarningEventArgs e);
    }

    /// <summary>
    /// Build tamamlandı event args;
    /// </summary>
    public class BuildCompletedEventArgs : EventArgs;
    {
        public BuildResult Result { get; }

        public BuildCompletedEventArgs(BuildResult result)
        {
            Result = result;
        }
    }

    /// <summary>
    /// Build ilerleme event args;
    /// </summary>
    public class BuildProgressEventArgs : EventArgs;
    {
        public string Message { get; }
        public ProgressType ProgressType { get; }
        public DateTime Timestamp { get; }

        public BuildProgressEventArgs(string message, ProgressType progressType)
        {
            Message = message;
            ProgressType = progressType;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Build hata event args;
    /// </summary>
    public class BuildErrorEventArgs : EventArgs;
    {
        public string Message { get; }
        public Exception Exception { get; }
        public BuildErrorType ErrorType { get; }
        public DateTime Timestamp { get; }

        public BuildErrorEventArgs(string message, Exception exception, BuildErrorType errorType)
        {
            Message = message;
            Exception = exception;
            ErrorType = errorType;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Build uyarı event args;
    /// </summary>
    public class BuildWarningEventArgs : EventArgs;
    {
        public string Message { get; }
        public BuildWarningType WarningType { get; }
        public DateTime Timestamp { get; }

        public BuildWarningEventArgs(string message, BuildWarningType warningType)
        {
            Message = message;
            WarningType = warningType;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Build durumu değişti event args;
    /// </summary>
    public class BuildStatusChangedEventArgs : EventArgs;
    {
        public BuildStatus Status { get; }
        public DateTime Timestamp { get; }

        public BuildStatusChangedEventArgs(BuildStatus status)
        {
            Status = status;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Build target event args;
    /// </summary>
    public class BuildTargetEventArgs : EventArgs;
    {
        public BuildTarget Target { get; }
        public BuildResult Result { get; }

        public BuildTargetEventArgs(BuildTarget target, BuildResult result)
        {
            Target = target;
            Result = result;
        }
    }

    /// <summary>
    /// CI/CD sonucu;
    /// </summary>
    public class CICDResult;
    {
        public bool Success { get; set; }
        public string PipelineName { get; set; }
        public string Output { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public Dictionary<string, object> Artifacts { get; set; }

        public CICDResult()
        {
            Artifacts = new Dictionary<string, object>();
            StartTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Build engine exception;
    /// </summary>
    public class BuildEngineException : Exception
    {
        public BuildErrorType ErrorType { get; }

        public BuildEngineException(string message) : base(message)
        {
            ErrorType = BuildErrorType.UnexpectedError;
        }

        public BuildEngineException(string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorType = BuildErrorType.UnexpectedError;
        }

        public BuildEngineException(string message, BuildErrorType errorType)
            : base(message)
        {
            ErrorType = errorType;
        }

        public BuildEngineException(string message, Exception innerException, BuildErrorType errorType)
            : base(message, innerException)
        {
            ErrorType = errorType;
        }
    }

    #endregion;
}
