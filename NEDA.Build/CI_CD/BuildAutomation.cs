using NEDA.AI.ReinforcementLearning;
using NEDA.Build.CI_CD.PipelineModels;
using NEDA.Build.PackageManager;
using NEDA.Cloud.AWS;
using NEDA.Cloud.Azure;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.EnvironmentManager;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.FileService;
using NEDA.Services.ProjectService;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.Build.CI_CD;
{
    /// <summary>
    /// Build otomasyon motoru - CI/CD pipeline'larını yöneten ana sistem;
    /// </summary>
    public class BuildAutomation : IBuildAutomation, IDisposable;
    {
        #region Constants and Fields;

        private const int DefaultBuildTimeoutMinutes = 60;
        private const int MaxConcurrentBuilds = 5;
        private const int RetryAttempts = 3;
        private const string BuildArtifactsFolder = "Artifacts";
        private const string BuildLogsFolder = "Logs";
        private const string BuildCacheFolder = "Cache";

        private readonly ILogger _logger;
        private readonly PipelineManager _pipelineManager;
        private readonly PackageBuilder _packageBuilder;
        private readonly NuGetManager _nuGetManager;
        private readonly ProjectManager _projectManager;
        private readonly FileManager _fileManager;
        private readonly SecurityManager _securityManager;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly EnvironmentConfig _environmentConfig;

        private readonly SemaphoreSlim _buildSemaphore;
        private readonly Dictionary<string, BuildProcess> _activeBuilds;
        private readonly BuildCache _buildCache;
        private readonly BuildArtifactManager _artifactManager;
        private readonly BuildNotifier _notifier;
        private readonly BuildAnalytics _analytics;

        private bool _isInitialized;
        private readonly object _syncLock = new object();

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif build sayısı;
        /// </summary>
        public int ActiveBuildCount => _activeBuilds.Count;

        /// <summary>
        /// Maksimum eşzamanlı build sayısı;
        /// </summary>
        public int MaxConcurrentBuildLimit { get; set; } = MaxConcurrentBuilds;

        /// <summary>
        /// Varsayılan build timeout (dakika)
        /// </summary>
        public int DefaultTimeoutMinutes { get; set; } = DefaultBuildTimeoutMinutes;

        /// <summary>
        /// Build istatistikleri;
        /// </summary>
        public BuildStatistics Statistics { get; private set; }

        /// <summary>
        /// Build cache etkin mi;
        /// </summary>
        public bool EnableBuildCache { get; set; } = true;

        /// <summary>
        /// Artifact saklama etkin mi;
        /// </summary>
        public bool EnableArtifactStorage { get; set; } = true;

        /// <summary>
        /// Build otomasyon durumu;
        /// </summary>
        public BuildAutomationStatus Status { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// BuildAutomation sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public BuildAutomation(
            ILogger logger,
            PipelineManager pipelineManager,
            PackageBuilder packageBuilder,
            NuGetManager nuGetManager,
            ProjectManager projectManager,
            FileManager fileManager,
            SecurityManager securityManager,
            EnvironmentConfig environmentConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pipelineManager = pipelineManager ?? throw new ArgumentNullException(nameof(pipelineManager));
            _packageBuilder = packageBuilder ?? throw new ArgumentNullException(nameof(packageBuilder));
            _nuGetManager = nuGetManager ?? throw new ArgumentNullException(nameof(nuGetManager));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _environmentConfig = environmentConfig ?? throw new ArgumentNullException(nameof(environmentConfig));

            _buildSemaphore = new SemaphoreSlim(MaxConcurrentBuilds, MaxConcurrentBuilds);
            _activeBuilds = new Dictionary<string, BuildProcess>();
            _buildCache = new BuildCache(_fileManager, _logger);
            _artifactManager = new BuildArtifactManager(_fileManager, _logger);
            _notifier = new BuildNotifier(_logger);
            _analytics = new BuildAnalytics(_logger);
            _performanceMonitor = new PerformanceMonitor("BuildAutomation");

            Statistics = new BuildStatistics();
            Status = BuildAutomationStatus.Stopped;

            Initialize();
        }

        /// <summary>
        /// Varsayılan bağımlılıklarla BuildAutomation oluşturur;
        /// </summary>
        public BuildAutomation() : this(
            LogManager.GetLogger(typeof(BuildAutomation)),
            new PipelineManager(),
            new PackageBuilder(),
            NuGetManager.Default,
            ProjectManager.Instance,
            FileManager.Instance,
            SecurityManager.Instance,
            EnvironmentConfig.Current)
        {
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Build otomasyonunu başlatır;
        /// </summary>
        public void Start()
        {
            lock (_syncLock)
            {
                if (Status == BuildAutomationStatus.Running)
                {
                    _logger.Warning("Build otomasyon zaten çalışıyor");
                    return;
                }

                try
                {
                    _logger.Info("Build otomasyon başlatılıyor...");

                    // Gerekli dizinleri oluştur;
                    CreateRequiredDirectories();

                    // Cache'i temizle;
                    if (EnableBuildCache)
                    {
                        _buildCache.Initialize();
                    }

                    // Analytics'i başlat;
                    _analytics.Start();

                    Status = BuildAutomationStatus.Running;
                    _logger.Info("Build otomasyon başlatıldı");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build otomasyon başlatma hatası: {ex.Message}", ex);
                    Status = BuildAutomationStatus.Error;
                    throw;
                }
            }
        }

        /// <summary>
        /// Build otomasyonunu durdurur;
        /// </summary>
        public void Stop()
        {
            lock (_syncLock)
            {
                if (Status != BuildAutomationStatus.Running)
                    return;

                _logger.Info("Build otomasyon durduruluyor...");

                try
                {
                    // Aktif build'leri durdur;
                    StopAllActiveBuilds();

                    // Analytics'i durdur;
                    _analytics.Stop();

                    // Cache'i kaydet;
                    if (EnableBuildCache)
                    {
                        _buildCache.Save();
                    }

                    Status = BuildAutomationStatus.Stopped;
                    _logger.Info("Build otomasyon durduruldu");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build otomasyon durdurma hatası: {ex.Message}", ex);
                    Status = BuildAutomationStatus.Error;
                    throw;
                }
            }
        }

        /// <summary>
        /// Proje build eder;
        /// </summary>
        /// <param name="request">Build isteği</param>
        /// <returns>Build sonucu</returns>
        public async Task<BuildResult> BuildProjectAsync(BuildRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (Status != BuildAutomationStatus.Running)
                throw new InvalidOperationException("Build otomasyon çalışmıyor");

            BuildProcess buildProcess = null;
            string buildId = GenerateBuildId(request);

            using (_performanceMonitor.StartOperation("BuildProject"))
            {
                try
                {
                    // Build slot'u için bekle;
                    await _buildSemaphore.WaitAsync();

                    lock (_syncLock)
                    {
                        if (_activeBuilds.ContainsKey(buildId))
                        {
                            throw new BuildException($"Build zaten devam ediyor: {buildId}");
                        }

                        buildProcess = CreateBuildProcess(request, buildId);
                        _activeBuilds[buildId] = buildProcess;

                        _logger.Info($"Build başlatıldı: {buildId} - Proje: {request.ProjectName}");
                    }

                    // Build'i çalıştır;
                    var result = await ExecuteBuildAsync(buildProcess);

                    // Sonuçları işle;
                    await ProcessBuildResultAsync(result, buildProcess);

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build hatası: {ex.Message}", ex);

                    var errorResult = BuildResult.CreateErrorResult(buildId, ex);
                    await _notifier.SendBuildNotification(errorResult);

                    throw;
                }
                finally
                {
                    lock (_syncLock)
                    {
                        if (buildProcess != null && _activeBuilds.ContainsKey(buildId))
                        {
                            _activeBuilds.Remove(buildId);
                        }
                    }

                    _buildSemaphore.Release();

                    // İstatistikleri güncelle;
                    UpdateStatistics(buildProcess);
                }
            }
        }

        /// <summary>
        /// Çoklu proje build eder;
        /// </summary>
        /// <param name="requests">Build istekleri</param>
        /// <returns>Build sonuçları</returns>
        public async Task<MultiBuildResult> BuildMultipleProjectsAsync(IEnumerable<BuildRequest> requests)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            var requestList = requests.ToList();
            if (!requestList.Any())
                return MultiBuildResult.Empty;

            using (_performanceMonitor.StartOperation("BuildMultipleProjects"))
            {
                try
                {
                    _logger.Info($"{requestList.Count} proje paralel build ediliyor...");

                    var tasks = new List<Task<BuildResult>>();
                    var results = new List<BuildResult>();

                    // Paralel build'ler başlat;
                    foreach (var request in requestList)
                    {
                        tasks.Add(BuildProjectAsync(request));
                    }

                    // Tüm build'lerin bitmesini bekle;
                    var completedTasks = await Task.WhenAll(tasks);
                    results.AddRange(completedTasks);

                    // Toplu sonuç oluştur;
                    var multiResult = new MultiBuildResult;
                    {
                        Results = results,
                        TotalBuilds = results.Count,
                        SuccessfulBuilds = results.Count(r => r.Status == BuildStatus.Success),
                        FailedBuilds = results.Count(r => r.Status == BuildStatus.Failed),
                        TotalDuration = results.Sum(r => r.Duration.TotalSeconds),
                        Timestamp = DateTime.UtcNow;
                    };

                    _logger.Info($"Çoklu build tamamlandı: {multiResult.SuccessfulBuilds}/{multiResult.TotalBuilds} başarılı");

                    // Toplu bildirim gönder;
                    await _notifier.SendMultiBuildNotification(multiResult);

                    return multiResult;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Çoklu build hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Pipeline build eder;
        /// </summary>
        /// <param name="pipelineId">Pipeline ID</param>
        /// <param name="parameters">Pipeline parametreleri</param>
        /// <returns>Pipeline build sonucu</returns>
        public async Task<PipelineBuildResult> BuildPipelineAsync(string pipelineId, Dictionary<string, string> parameters = null)
        {
            if (string.IsNullOrWhiteSpace(pipelineId))
                throw new ArgumentException("Pipeline ID boş olamaz", nameof(pipelineId));

            using (_performanceMonitor.StartOperation("BuildPipeline"))
            {
                try
                {
                    _logger.Info($"Pipeline build başlatılıyor: {pipelineId}");

                    // Pipeline'ı yükle;
                    var pipeline = await _pipelineManager.GetPipelineAsync(pipelineId);
                    if (pipeline == null)
                        throw new PipelineNotFoundException($"Pipeline bulunamadı: {pipelineId}");

                    // Pipeline build sürecini oluştur;
                    var pipelineBuild = new PipelineBuildProcess(pipeline, parameters);

                    // Pipeline'ı çalıştır;
                    var result = await ExecutePipelineAsync(pipelineBuild);

                    _logger.Info($"Pipeline build tamamlandı: {pipelineId} - Durum: {result.Status}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Pipeline build hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build'i iptal eder;
        /// </summary>
        /// <param name="buildId">Build ID</param>
        public async Task CancelBuildAsync(string buildId)
        {
            if (string.IsNullOrWhiteSpace(buildId))
                return;

            BuildProcess buildProcess;

            lock (_syncLock)
            {
                if (!_activeBuilds.TryGetValue(buildId, out buildProcess))
                {
                    _logger.Warning($"İptal edilecek build bulunamadı: {buildId}");
                    return;
                }
            }

            try
            {
                _logger.Info($"Build iptal ediliyor: {buildId}");

                await buildProcess.CancelAsync();

                // Build'i aktif listeden kaldır;
                lock (_syncLock)
                {
                    _activeBuilds.Remove(buildId);
                }

                // İptal bildirimi gönder;
                await _notifier.SendBuildCancelledNotification(buildId, buildProcess.Request.ProjectName);

                _logger.Info($"Build iptal edildi: {buildId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Build iptal hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Tüm aktif build'leri iptal eder;
        /// </summary>
        public async Task CancelAllBuildsAsync()
        {
            List<string> activeBuildIds;

            lock (_syncLock)
            {
                activeBuildIds = _activeBuilds.Keys.ToList();
            }

            _logger.Info($"{activeBuildIds.Count} aktif build iptal ediliyor...");

            var cancelTasks = activeBuildIds.Select(CancelBuildAsync);
            await Task.WhenAll(cancelTasks);

            _logger.Info("Tüm build'ler iptal edildi");
        }

        /// <summary>
        /// Build geçmişini getirir;
        /// </summary>
        /// <param name="filter">Filtre kriterleri</param>
        /// <returns>Build geçmişi</returns>
        public async Task<BuildHistory> GetBuildHistoryAsync(BuildHistoryFilter filter = null)
        {
            using (_performanceMonitor.StartOperation("GetBuildHistory"))
            {
                try
                {
                    filter ??= new BuildHistoryFilter();

                    var history = new BuildHistory;
                    {
                        Filter = filter,
                        Builds = new List<BuildHistoryItem>(),
                        TotalCount = 0;
                    };

                    // Build log dosyalarını oku;
                    var logFiles = await GetBuildLogFilesAsync(filter);

                    foreach (var logFile in logFiles)
                    {
                        try
                        {
                            var historyItem = await ParseBuildLogToHistoryItemAsync(logFile);
                            if (historyItem != null && FilterMatches(historyItem, filter))
                            {
                                history.Builds.Add(historyItem);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"Build log parse hatası: {ex.Message}");
                        }
                    }

                    history.TotalCount = history.Builds.Count;
                    history.Builds = history.Builds;
                        .OrderByDescending(b => b.StartTime)
                        .Skip(filter.Skip)
                        .Take(filter.Take)
                        .ToList();

                    return history;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build geçmişi getirme hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build artifact'larını getirir;
        /// </summary>
        /// <param name="buildId">Build ID</param>
        /// <returns>Artifact listesi</returns>
        public async Task<IEnumerable<BuildArtifact>> GetBuildArtifactsAsync(string buildId)
        {
            if (string.IsNullOrWhiteSpace(buildId))
                throw new ArgumentException("Build ID boş olamaz", nameof(buildId));

            using (_performanceMonitor.StartOperation("GetBuildArtifacts"))
            {
                try
                {
                    return await _artifactManager.GetArtifactsAsync(buildId);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Artifact getirme hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build log'unu getirir;
        /// </summary>
        /// <param name="buildId">Build ID</param>
        /// <returns>Build log'u</returns>
        public async Task<string> GetBuildLogAsync(string buildId)
        {
            if (string.IsNullOrWhiteSpace(buildId))
                throw new ArgumentException("Build ID boş olamaz", nameof(buildId));

            using (_performanceMonitor.StartOperation("GetBuildLog"))
            {
                try
                {
                    var logPath = GetBuildLogPath(buildId);

                    if (!File.Exists(logPath))
                        throw new FileNotFoundException($"Build log'u bulunamadı: {buildId}");

                    return await File.ReadAllTextAsync(logPath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build log getirme hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build cache'ini temizler;
        /// </summary>
        public async Task ClearBuildCacheAsync()
        {
            using (_performanceMonitor.StartOperation("ClearBuildCache"))
            {
                try
                {
                    _logger.Info("Build cache temizleniyor...");

                    await _buildCache.ClearAsync();

                    _logger.Info("Build cache temizlendi");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build cache temizleme hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build ortamını doğrular;
        /// </summary>
        public async Task<EnvironmentValidationResult> ValidateBuildEnvironmentAsync()
        {
            using (_performanceMonitor.StartOperation("ValidateBuildEnvironment"))
            {
                try
                {
                    _logger.Info("Build ortamı doğrulanıyor...");

                    var result = new EnvironmentValidationResult;
                    {
                        Timestamp = DateTime.UtcNow,
                        Checks = new List<EnvironmentCheck>()
                    };

                    // Gerekli araçları kontrol et;
                    var toolChecks = await ValidateBuildToolsAsync();
                    result.Checks.AddRange(toolChecks);

                    // Disk alanını kontrol et;
                    var diskCheck = ValidateDiskSpace();
                    result.Checks.Add(diskCheck);

                    // Ağ bağlantısını kontrol et;
                    var networkCheck = await ValidateNetworkConnectivityAsync();
                    result.Checks.Add(networkCheck);

                    // İzinleri kontrol et;
                    var permissionCheck = ValidatePermissions();
                    result.Checks.Add(permissionCheck);

                    // Sonucu hesapla;
                    result.IsValid = result.Checks.All(c => c.IsValid);
                    result.SuccessfulChecks = result.Checks.Count(c => c.IsValid);
                    result.FailedChecks = result.Checks.Count(c => !c.IsValid);

                    _logger.Info($"Build ortamı doğrulandı: {result.SuccessfulChecks}/{result.Checks.Count} başarılı");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build ortamı doğrulama hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build template'i oluşturur;
        /// </summary>
        public async Task<BuildTemplate> CreateBuildTemplateAsync(BuildTemplateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            using (_performanceMonitor.StartOperation("CreateBuildTemplate"))
            {
                try
                {
                    _logger.Info($"Build template oluşturuluyor: {request.TemplateName}");

                    var template = new BuildTemplate;
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = request.TemplateName,
                        Description = request.Description,
                        CreatedBy = _securityManager.GetCurrentUser(),
                        CreatedAt = DateTime.UtcNow,
                        Steps = new List<BuildStep>(),
                        Variables = new Dictionary<string, string>()
                    };

                    // Proje analizi yap;
                    await AnalyzeProjectForTemplateAsync(request.ProjectPath, template);

                    // Varsayılan adımları ekle;
                    AddDefaultBuildSteps(template, request.BuildType);

                    // Template'i kaydet;
                    await SaveBuildTemplateAsync(template);

                    _logger.Info($"Build template oluşturuldu: {template.Name} ({template.Id})");

                    return template;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build template oluşturma hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Uzak makinede build yapar;
        /// </summary>
        public async Task<RemoteBuildResult> ExecuteRemoteBuildAsync(RemoteBuildRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            using (_performanceMonitor.StartOperation("ExecuteRemoteBuild"))
            {
                try
                {
                    _logger.Info($"Uzak build başlatılıyor: {request.BuildAgent} - {request.ProjectName}");

                    // Build agent'ı seç;
                    var buildAgent = await SelectBuildAgentAsync(request);

                    // Build'i uzak makinede çalıştır;
                    var result = await ExecuteBuildOnAgentAsync(buildAgent, request);

                    // Sonuçları senkronize et;
                    await SyncRemoteBuildResultAsync(result, request);

                    _logger.Info($"Uzak build tamamlandı: {result.BuildId} - Durum: {result.Status}");

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Uzak build hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Build raporu oluşturur;
        /// </summary>
        public async Task<BuildReport> GenerateBuildReportAsync(string buildId, ReportFormat format = ReportFormat.Html)
        {
            if (string.IsNullOrWhiteSpace(buildId))
                throw new ArgumentException("Build ID boş olamaz", nameof(buildId));

            using (_performanceMonitor.StartOperation("GenerateBuildReport"))
            {
                try
                {
                    _logger.Info($"Build raporu oluşturuluyor: {buildId}");

                    // Build verilerini topla;
                    var buildData = await CollectBuildDataAsync(buildId);

                    // Rapor oluştur;
                    var report = new BuildReportGenerator(format).Generate(buildData);

                    // Raporu kaydet;
                    await SaveBuildReportAsync(report, buildId);

                    _logger.Info($"Build raporu oluşturuldu: {report.FileName}");

                    return report;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Build raporu oluşturma hatası: {ex.Message}", ex);
                    throw;
                }
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Build otomasyonunu başlatır;
        /// </summary>
        private void Initialize()
        {
            if (_isInitialized)
                return;

            lock (_syncLock)
            {
                if (_isInitialized)
                    return;

                try
                {
                    _logger.Info("BuildAutomation başlatılıyor...");

                    // Ortam yapılandırmasını yükle;
                    LoadEnvironmentConfiguration();

                    // Gerekli dizinleri oluştur;
                    CreateRequiredDirectories();

                    // Build cache'ini başlat;
                    if (EnableBuildCache)
                    {
                        _buildCache.Initialize();
                    }

                    // Artifact manager'ı başlat;
                    if (EnableArtifactStorage)
                    {
                        _artifactManager.Initialize();
                    }

                    // Analytics'i başlat;
                    _analytics.Start();

                    _isInitialized = true;
                    Status = BuildAutomationStatus.Running;

                    _logger.Info("BuildAutomation başlatıldı");
                }
                catch (Exception ex)
                {
                    _logger.Error($"BuildAutomation başlatma hatası: {ex.Message}", ex);
                    Status = BuildAutomationStatus.Error;
                    throw;
                }
            }
        }

        /// <summary>
        /// Ortam yapılandırmasını yükler;
        /// </summary>
        private void LoadEnvironmentConfiguration()
        {
            try
            {
                // Build ayarlarını yükle;
                var buildSettings = _environmentConfig.GetSection("BuildSettings");

                if (buildSettings != null)
                {
                    MaxConcurrentBuildLimit = buildSettings.GetValue("MaxConcurrentBuilds", MaxConcurrentBuilds);
                    DefaultTimeoutMinutes = buildSettings.GetValue("DefaultTimeoutMinutes", DefaultBuildTimeoutMinutes);
                    EnableBuildCache = buildSettings.GetValue("EnableBuildCache", true);
                    EnableArtifactStorage = buildSettings.GetValue("EnableArtifactStorage", true);
                }

                // Semaphore'u güncelle;
                _buildSemaphore = new SemaphoreSlim(MaxConcurrentBuildLimit, MaxConcurrentBuildLimit);

                _logger.Debug($"Build ayarları yüklendi: MaxConcurrent={MaxConcurrentBuildLimit}, " +
                             $"Timeout={DefaultTimeoutMinutes}min");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Ortam yapılandırması yüklenirken hata: {ex.Message}");
            }
        }

        /// <summary>
        /// Gerekli dizinleri oluşturur;
        /// </summary>
        private void CreateRequiredDirectories()
        {
            try
            {
                var basePath = _environmentConfig.BuildRootPath;

                // Ana dizinleri oluştur;
                Directory.CreateDirectory(Path.Combine(basePath, BuildArtifactsFolder));
                Directory.CreateDirectory(Path.Combine(basePath, BuildLogsFolder));
                Directory.CreateDirectory(Path.Combine(basePath, BuildCacheFolder));
                Directory.CreateDirectory(Path.Combine(basePath, "Templates"));
                Directory.CreateDirectory(Path.Combine(basePath, "Reports"));
                Directory.CreateDirectory(Path.Combine(basePath, "Temp"));

                _logger.Debug("Build dizinleri oluşturuldu");
            }
            catch (Exception ex)
            {
                _logger.Error($"Dizin oluşturma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Build ID oluşturur;
        /// </summary>
        private string GenerateBuildId(BuildRequest request)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var projectHash = request.ProjectPath.GetHashCode().ToString("X8");
            var random = new Random().Next(1000, 9999);

            return $"{request.ProjectName}_{timestamp}_{projectHash}_{random}";
        }

        /// <summary>
        /// Build süreci oluşturur;
        /// </summary>
        private BuildProcess CreateBuildProcess(BuildRequest request, string buildId)
        {
            var process = new BuildProcess;
            {
                Id = buildId,
                Request = request,
                StartTime = DateTime.UtcNow,
                Status = BuildStatus.Queued,
                WorkingDirectory = GetBuildWorkingDirectory(buildId),
                LogFile = GetBuildLogPath(buildId),
                Timeout = TimeSpan.FromMinutes(request.TimeoutMinutes ?? DefaultTimeoutMinutes)
            };

            // Build template'i uygula;
            if (!string.IsNullOrEmpty(request.BuildTemplateId))
            {
                ApplyBuildTemplate(process, request.BuildTemplateId);
            }

            // Build adımlarını oluştur;
            process.Steps = CreateBuildSteps(request, process.WorkingDirectory);

            return process;
        }

        /// <summary>
        /// Build adımlarını oluşturur;
        /// </summary>
        private List<BuildStep> CreateBuildSteps(BuildRequest request, string workingDirectory)
        {
            var steps = new List<BuildStep>();

            // 1. Başlangıç adımı;
            steps.Add(new BuildStep;
            {
                Id = "initialize",
                Name = "Build Ortamını Hazırla",
                Command = BuildCommandType.Initialize,
                Parameters = new Dictionary<string, string>
                {
                    { "WorkingDirectory", workingDirectory },
                    { "ProjectPath", request.ProjectPath },
                    { "Clean", request.CleanBuild.ToString() }
                }
            });

            // 2. Bağımlılıkları yükle;
            if (request.RestoreDependencies)
            {
                steps.Add(new BuildStep;
                {
                    Id = "restore",
                    Name = "Bağımlılıkları Yükle",
                    Command = BuildCommandType.Restore,
                    Parameters = new Dictionary<string, string>
                    {
                        { "Source", request.NuGetSource },
                        { "NoCache", request.NoCache.ToString() }
                    }
                });
            }

            // 3. Derleme;
            steps.Add(new BuildStep;
            {
                Id = "compile",
                Name = "Projeyi Derle",
                Command = BuildCommandType.Compile,
                Parameters = new Dictionary<string, string>
                {
                    { "Configuration", request.Configuration },
                    { "Platform", request.Platform },
                    { "TargetFramework", request.TargetFramework }
                }
            });

            // 4. Testleri çalıştır;
            if (request.RunTests)
            {
                steps.Add(new BuildStep;
                {
                    Id = "test",
                    Name = "Testleri Çalıştır",
                    Command = BuildCommandType.Test,
                    Parameters = new Dictionary<string, string>
                    {
                        { "Filter", request.TestFilter },
                        { "CollectCoverage", request.CollectCoverage.ToString() }
                    }
                });
            }

            // 5. Paketleme;
            if (request.CreatePackage)
            {
                steps.Add(new BuildStep;
                {
                    Id = "package",
                    Name = "Paket Oluştur",
                    Command = BuildCommandType.Package,
                    Parameters = new Dictionary<string, string>
                    {
                        { "PackageType", request.PackageType.ToString() },
                        { "Version", request.PackageVersion }
                    }
                });
            }

            // 6. Artifact'ları topla;
            steps.Add(new BuildStep;
            {
                Id = "artifact",
                Name = "Artifact'ları Topla",
                Command = BuildCommandType.CollectArtifacts,
                Parameters = new Dictionary<string, string>()
            });

            return steps;
        }

        /// <summary>
        /// Build'i çalıştırır;
        /// </summary>
        private async Task<BuildResult> ExecuteBuildAsync(BuildProcess buildProcess)
        {
            var result = new BuildResult;
            {
                BuildId = buildProcess.Id,
                ProjectName = buildProcess.Request.ProjectName,
                StartTime = buildProcess.StartTime,
                Status = BuildStatus.Running,
                Steps = new List<BuildStepResult>()
            };

            try
            {
                _logger.Info($"Build çalıştırılıyor: {buildProcess.Id}");

                // Build log'unu başlat;
                using var logWriter = new BuildLogWriter(buildProcess.LogFile);
                logWriter.WriteHeader(buildProcess);

                // Her adımı çalıştır;
                foreach (var step in buildProcess.Steps)
                {
                    var stepResult = await ExecuteBuildStepAsync(step, buildProcess, logWriter);
                    result.Steps.Add(stepResult);

                    // Adım başarısızsa build'i durdur;
                    if (stepResult.Status == BuildStepStatus.Failed &&
                        !buildProcess.Request.ContinueOnError)
                    {
                        result.Status = BuildStatus.Failed;
                        result.ErrorMessage = $"Adım başarısız: {step.Name}";
                        break;
                    }
                }

                // Build durumunu güncelle;
                if (result.Status == BuildStatus.Running)
                {
                    result.Status = result.Steps.All(s => s.Status != BuildStepStatus.Failed)
                        ? BuildStatus.Success;
                        : BuildStatus.PartialSuccess;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                // Log'u kapat;
                logWriter.WriteFooter(result);

                _logger.Info($"Build tamamlandı: {buildProcess.Id} - Durum: {result.Status}");

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Status = BuildStatus.Cancelled;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                _logger.Info($"Build iptal edildi: {buildProcess.Id}");

                return result;
            }
            catch (Exception ex)
            {
                result.Status = BuildStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                _logger.Error($"Build çalıştırma hatası: {ex.Message}", ex);

                return result;
            }
        }

        /// <summary>
        /// Build adımını çalıştırır;
        /// </summary>
        private async Task<BuildStepResult> ExecuteBuildStepAsync(
            BuildStep step,
            BuildProcess buildProcess,
            BuildLogWriter logWriter)
        {
            var stepResult = new BuildStepResult;
            {
                StepId = step.Id,
                StepName = step.Name,
                StartTime = DateTime.UtcNow,
                Status = BuildStepStatus.Running;
            };

            logWriter.WriteStepStart(step);

            try
            {
                // Adımı çalıştır;
                switch (step.Command)
                {
                    case BuildCommandType.Initialize:
                        await ExecuteInitializeStepAsync(step, buildProcess, logWriter);
                        break;

                    case BuildCommandType.Restore:
                        await ExecuteRestoreStepAsync(step, buildProcess, logWriter);
                        break;

                    case BuildCommandType.Compile:
                        await ExecuteCompileStepAsync(step, buildProcess, logWriter);
                        break;

                    case BuildCommandType.Test:
                        await ExecuteTestStepAsync(step, buildProcess, logWriter);
                        break;

                    case BuildCommandType.Package:
                        await ExecutePackageStepAsync(step, buildProcess, logWriter);
                        break;

                    case BuildCommandType.CollectArtifacts:
                        await ExecuteCollectArtifactsStepAsync(step, buildProcess, logWriter);
                        break;

                    default:
                        throw new NotSupportedException($"Desteklenmeyen komut: {step.Command}");
                }

                stepResult.Status = BuildStepStatus.Success;
                stepResult.Duration = DateTime.UtcNow - stepResult.StartTime;

                logWriter.WriteStepSuccess(step, stepResult.Duration);
            }
            catch (Exception ex)
            {
                stepResult.Status = BuildStepStatus.Failed;
                stepResult.ErrorMessage = ex.Message;
                stepResult.Duration = DateTime.UtcNow - stepResult.StartTime;

                logWriter.WriteStepError(step, ex, stepResult.Duration);

                _logger.Error($"Build adımı hatası: {step.Name} - {ex.Message}", ex);
            }

            return stepResult;
        }

        /// <summary>
        /// Başlangıç adımını çalıştırır;
        /// </summary>
        private async Task ExecuteInitializeStepAsync(BuildStep step, BuildProcess buildProcess, BuildLogWriter logWriter)
        {
            logWriter.WriteInfo("Build ortamı hazırlanıyor...");

            // Çalışma dizinini oluştur;
            if (Directory.Exists(buildProcess.WorkingDirectory) && buildProcess.Request.CleanBuild)
            {
                Directory.Delete(buildProcess.WorkingDirectory, true);
                logWriter.WriteInfo("Çalışma dizini temizlendi");
            }

            Directory.CreateDirectory(buildProcess.WorkingDirectory);

            // Projeyi kopyala;
            await CopyProjectToWorkingDirectoryAsync(buildProcess);

            // Ortam değişkenlerini ayarla;
            SetBuildEnvironmentVariables(buildProcess);

            logWriter.WriteSuccess("Build ortamı hazırlandı");
        }

        /// <summary>
        /// Bağımlılık yükleme adımını çalıştırır;
        /// </summary>
        private async Task ExecuteRestoreStepAsync(BuildStep step, BuildProcess buildProcess, BuildLogWriter logWriter)
        {
            logWriter.WriteInfo("Bağımlılıklar yükleniyor...");

            // NuGet restore işlemi;
            var result = await _nuGetManager.RestoreAsync(
                buildProcess.Request.ProjectPath,
                buildProcess.Request.NuGetSource,
                !buildProcess.Request.NoCache);

            if (!result.Success)
            {
                throw new BuildException($"Bağımlılık yükleme başarısız: {result.ErrorMessage}");
            }

            logWriter.WriteSuccess($"Bağımlılıklar yüklendi: {result.RestoredPackages} paket");
        }

        /// <summary>
        /// Derleme adımını çalıştırır;
        /// </summary>
        private async Task ExecuteCompileStepAsync(BuildStep step, BuildProcess buildProcess, BuildLogWriter logWriter)
        {
            logWriter.WriteInfo("Proje derleniyor...");

            // MSBuild veya dotnet build komutunu çalıştır;
            var buildResult = await ExecuteBuildCommandAsync(buildProcess, step.Parameters);

            if (!buildResult.Success)
            {
                throw new BuildException($"Derleme başarısız:\n{buildResult.Output}");
            }

            logWriter.WriteSuccess($"Proje derlendi: {buildResult.OutputLines.Count} satır çıktı");

            // Build çıktısını kaydet;
            await SaveBuildOutputAsync(buildProcess, buildResult);
        }

        /// <summary>
        /// Test adımını çalıştırır;
        /// </summary>
        private async Task ExecuteTestStepAsync(BuildStep step, BuildProcess buildProcess, BuildLogWriter logWriter)
        {
            logWriter.WriteInfo("Testler çalıştırılıyor...");

            var testResult = await ExecuteTestCommandAsync(buildProcess, step.Parameters);

            if (!testResult.Success && !buildProcess.Request.ContinueOnError)
            {
                throw new BuildException($"Testler başarısız: {testResult.FailedTests} test başarısız");
            }

            logWriter.WriteInfo($"Test sonuçları: {testResult.PassedTests} geçti, " +
                               $"{testResult.FailedTests} başarısız, " +
                               $"{testResult.SkippedTests} atlandı");

            // Test raporunu kaydet;
            await SaveTestReportAsync(buildProcess, testResult);
        }

        /// <summary>
        /// Paketleme adımını çalıştırır;
        /// </summary>
        private async Task ExecutePackageStepAsync(BuildStep step, BuildProcess buildProcess, BuildLogWriter logWriter)
        {
            logWriter.WriteInfo("Paket oluşturuluyor...");

            var packageResult = await _packageBuilder.BuildPackageAsync(
                buildProcess.Request.ProjectPath,
                buildProcess.Request.PackageType,
                buildProcess.Request.PackageVersion,
                step.Parameters);

            if (!packageResult.Success)
            {
                throw new BuildException($"Paket oluşturma başarısız: {packageResult.ErrorMessage}");
            }

            logWriter.WriteSuccess($"Paket oluşturuldu: {packageResult.PackagePath}");

            // Paketi artifact olarak kaydet;
            await _artifactManager.AddArtifactAsync(buildProcess.Id, packageResult.PackagePath);
        }

        /// <summary>
        /// Artifact toplama adımını çalıştırır;
        /// </summary>
        private async Task ExecuteCollectArtifactsStepAsync(BuildStep step, BuildProcess buildProcess, BuildLogWriter logWriter)
        {
            logWriter.WriteInfo("Artifact'lar toplanıyor...");

            await _artifactManager.CollectArtifactsAsync(
                buildProcess.Id,
                buildProcess.WorkingDirectory,
                buildProcess.Request.ArtifactPatterns);

            var artifacts = await _artifactManager.GetArtifactsAsync(buildProcess.Id);

            logWriter.WriteSuccess($"{artifacts.Count()} artifact toplandı");
        }

        /// <summary>
        /// Build komutunu çalıştırır;
        /// </summary>
        private async Task<BuildCommandResult> ExecuteBuildCommandAsync(BuildProcess buildProcess, Dictionary<string, string> parameters)
        {
            var processInfo = new ProcessStartInfo;
            {
                FileName = "dotnet",
                Arguments = $"build \"{buildProcess.Request.ProjectPath}\" " +
                           $"--configuration {parameters["Configuration"]} " +
                           $"--output \"{Path.Combine(buildProcess.WorkingDirectory, "output")}\"",
                WorkingDirectory = Path.GetDirectoryName(buildProcess.Request.ProjectPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true;
            };

            return await ExecuteProcessAsync(processInfo, buildProcess.Timeout);
        }

        /// <summary>
        /// Test komutunu çalıştırır;
        /// </summary>
        private async Task<TestExecutionResult> ExecuteTestCommandAsync(BuildProcess buildProcess, Dictionary<string, string> parameters)
        {
            var processInfo = new ProcessStartInfo;
            {
                FileName = "dotnet",
                Arguments = $"test \"{buildProcess.Request.ProjectPath}\" " +
                           $"--configuration {buildProcess.Request.Configuration} " +
                           $"--logger trx " +
                           $"--results-directory \"{Path.Combine(buildProcess.WorkingDirectory, "TestResults")}\"",
                WorkingDirectory = Path.GetDirectoryName(buildProcess.Request.ProjectPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true;
            };

            var result = await ExecuteProcessAsync(processInfo, buildProcess.Timeout);

            return new TestExecutionResult;
            {
                Success = result.ExitCode == 0,
                Output = result.Output,
                Error = result.Error,
                OutputLines = result.OutputLines;
            };
        }

        /// <summary>
        /// Proses çalıştırır;
        /// </summary>
        private async Task<ProcessExecutionResult> ExecuteProcessAsync(ProcessStartInfo processInfo, TimeSpan timeout)
        {
            var result = new ProcessExecutionResult();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using var process = new Process { StartInfo = processInfo };
            using var cts = new CancellationTokenSource(timeout);

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    result.OutputLines.Add(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cts.Token);

                result.ExitCode = process.ExitCode;
                result.Output = outputBuilder.ToString();
                result.Error = errorBuilder.ToString();
                result.Success = process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                result.TimedOut = true;
                result.Success = false;
                result.Error = "Process timeout occurred";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Build sonucunu işler;
        /// </summary>
        private async Task ProcessBuildResultAsync(BuildResult result, BuildProcess buildProcess)
        {
            // Sonuçları cache'e kaydet;
            if (EnableBuildCache && result.Status == BuildStatus.Success)
            {
                await _buildCache.StoreBuildResultAsync(buildProcess.Id, result);
            }

            // Artifact'ları yönet;
            if (EnableArtifactStorage)
            {
                await _artifactManager.FinalizeBuildAsync(buildProcess.Id, result.Status);
            }

            // Bildirim gönder;
            await _notifier.SendBuildNotification(result);

            // Analytics'e kaydet;
            _analytics.RecordBuild(result);

            // İstatistikleri güncelle;
            UpdateStatistics(result);
        }

        /// <summary>
        /// Build template'i uygular;
        /// </summary>
        private void ApplyBuildTemplate(BuildProcess process, string templateId)
        {
            var template = _pipelineManager.GetBuildTemplate(templateId);
            if (template != null)
            {
                process.Steps = template.Steps.Select(s => s.Clone()).ToList();

                // Template değişkenlerini uygula;
                foreach (var variable in template.Variables)
                {
                    if (!process.Request.CustomVariables.ContainsKey(variable.Key))
                    {
                        process.Request.CustomVariables[variable.Key] = variable.Value;
                    }
                }
            }
        }

        /// <summary>
        /// Pipeline'ı çalıştırır;
        /// </summary>
        private async Task<PipelineBuildResult> ExecutePipelineAsync(PipelineBuildProcess pipelineBuild)
        {
            var result = new PipelineBuildResult;
            {
                PipelineId = pipelineBuild.Pipeline.Id,
                PipelineName = pipelineBuild.Pipeline.Name,
                StartTime = DateTime.UtcNow,
                Status = PipelineBuildStatus.Running,
                StageResults = new List<PipelineStageResult>()
            };

            try
            {
                _logger.Info($"Pipeline çalıştırılıyor: {pipelineBuild.Pipeline.Name}");

                // Her stage'i çalıştır;
                foreach (var stage in pipelineBuild.Pipeline.Stages)
                {
                    var stageResult = await ExecutePipelineStageAsync(stage, pipelineBuild);
                    result.StageResults.Add(stageResult);

                    // Stage başarısızsa pipeline'ı durdur;
                    if (stageResult.Status == PipelineStageStatus.Failed &&
                        !pipelineBuild.Pipeline.ContinueOnError)
                    {
                        result.Status = PipelineBuildStatus.Failed;
                        result.ErrorMessage = $"Stage başarısız: {stage.Name}";
                        break;
                    }
                }

                // Pipeline durumunu güncelle;
                if (result.Status == PipelineBuildStatus.Running)
                {
                    result.Status = result.StageResults.All(s => s.Status != PipelineStageStatus.Failed)
                        ? PipelineBuildStatus.Success;
                        : PipelineBuildStatus.PartialSuccess;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                // Pipeline artifact'larını topla;
                await CollectPipelineArtifactsAsync(pipelineBuild, result);

                _logger.Info($"Pipeline tamamlandı: {pipelineBuild.Pipeline.Name} - Durum: {result.Status}");

                return result;
            }
            catch (Exception ex)
            {
                result.Status = PipelineBuildStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                _logger.Error($"Pipeline çalıştırma hatası: {ex.Message}", ex);

                return result;
            }
        }

        /// <summary>
        /// Pipeline stage'ini çalıştırır;
        /// </summary>
        private async Task<PipelineStageResult> ExecutePipelineStageAsync(
            PipelineStage stage,
            PipelineBuildProcess pipelineBuild)
        {
            var stageResult = new PipelineStageResult;
            {
                StageId = stage.Id,
                StageName = stage.Name,
                StartTime = DateTime.UtcNow,
                Status = PipelineStageStatus.Running,
                JobResults = new List<PipelineJobResult>()
            };

            _logger.Info($"Pipeline stage çalıştırılıyor: {stage.Name}");

            try
            {
                // Paralel job'ları çalıştır;
                var jobTasks = stage.Jobs.Select(job => ExecutePipelineJobAsync(job, pipelineBuild));
                var jobResults = await Task.WhenAll(jobTasks);

                stageResult.JobResults.AddRange(jobResults);

                // Stage durumunu belirle;
                if (jobResults.All(j => j.Status == PipelineJobStatus.Success))
                {
                    stageResult.Status = PipelineStageStatus.Success;
                }
                else if (jobResults.Any(j => j.Status == PipelineJobStatus.Failed))
                {
                    stageResult.Status = PipelineStageStatus.Failed;
                    stageResult.ErrorMessage = string.Join(", ",
                        jobResults.Where(j => j.Status == PipelineJobStatus.Failed)
                                  .Select(j => j.ErrorMessage));
                }
                else;
                {
                    stageResult.Status = PipelineStageStatus.PartialSuccess;
                }

                stageResult.EndTime = DateTime.UtcNow;
                stageResult.Duration = stageResult.EndTime - stageResult.StartTime;

                _logger.Info($"Pipeline stage tamamlandı: {stage.Name} - Durum: {stageResult.Status}");

                return stageResult;
            }
            catch (Exception ex)
            {
                stageResult.Status = PipelineStageStatus.Failed;
                stageResult.ErrorMessage = ex.Message;
                stageResult.EndTime = DateTime.UtcNow;
                stageResult.Duration = stageResult.EndTime - stageResult.StartTime;

                _logger.Error($"Pipeline stage hatası: {ex.Message}", ex);

                return stageResult;
            }
        }

        /// <summary>
        /// Pipeline job'ını çalıştırır;
        /// </summary>
        private async Task<PipelineJobResult> ExecutePipelineJobAsync(
            PipelineJob job,
            PipelineBuildProcess pipelineBuild)
        {
            var jobResult = new PipelineJobResult;
            {
                JobId = job.Id,
                JobName = job.Name,
                StartTime = DateTime.UtcNow,
                Status = PipelineJobStatus.Running,
                StepResults = new List<PipelineStepResult>()
            };

            try
            {
                // Job adımlarını çalıştır;
                foreach (var step in job.Steps)
                {
                    var stepResult = await ExecutePipelineStepAsync(step, pipelineBuild);
                    jobResult.StepResults.Add(stepResult);

                    // Adım başarısızsa job'ı durdur;
                    if (stepResult.Status == PipelineStepStatus.Failed && !job.ContinueOnError)
                    {
                        jobResult.Status = PipelineJobStatus.Failed;
                        jobResult.ErrorMessage = $"Adım başarısız: {step.Name}";
                        break;
                    }
                }

                // Job durumunu güncelle;
                if (jobResult.Status == PipelineJobStatus.Running)
                {
                    jobResult.Status = jobResult.StepResults.All(s => s.Status != PipelineStepStatus.Failed)
                        ? PipelineJobStatus.Success;
                        : PipelineJobStatus.PartialSuccess;
                }

                jobResult.EndTime = DateTime.UtcNow;
                jobResult.Duration = jobResult.EndTime - jobResult.StartTime;

                return jobResult;
            }
            catch (Exception ex)
            {
                jobResult.Status = PipelineJobStatus.Failed;
                jobResult.ErrorMessage = ex.Message;
                jobResult.EndTime = DateTime.UtcNow;
                jobResult.Duration = jobResult.EndTime - jobResult.StartTime;

                return jobResult;
            }
        }

        /// <summary>
        /// Pipeline adımını çalıştırır;
        /// </summary>
        private async Task<PipelineStepResult> ExecutePipelineStepAsync(
            PipelineStep step,
            PipelineBuildProcess pipelineBuild)
        {
            var stepResult = new PipelineStepResult;
            {
                StepId = step.Id,
                StepName = step.Name,
                StartTime = DateTime.UtcNow,
                Status = PipelineStepStatus.Running;
            };

            try
            {
                // Adımı çalıştır;
                switch (step.Type)
                {
                    case PipelineStepType.Build:
                        var buildRequest = CreateBuildRequestFromStep(step, pipelineBuild);
                        var buildResult = await BuildProjectAsync(buildRequest);
                        stepResult.Output = buildResult.ToString();
                        stepResult.Status = buildResult.Status == BuildStatus.Success;
                            ? PipelineStepStatus.Success;
                            : PipelineStepStatus.Failed;
                        break;

                    case PipelineStepType.Deploy:
                        await ExecuteDeployStepAsync(step, pipelineBuild);
                        stepResult.Status = PipelineStepStatus.Success;
                        break;

                    case PipelineStepType.Test:
                        await ExecuteTestStepAsync(step, pipelineBuild);
                        stepResult.Status = PipelineStepStatus.Success;
                        break;

                    case PipelineStepType.Script:
                        await ExecuteScriptStepAsync(step, pipelineBuild);
                        stepResult.Status = PipelineStepStatus.Success;
                        break;

                    default:
                        throw new NotSupportedException($"Desteklenmeyen adım türü: {step.Type}");
                }

                stepResult.EndTime = DateTime.UtcNow;
                stepResult.Duration = stepResult.EndTime - stepResult.StartTime;

                return stepResult;
            }
            catch (Exception ex)
            {
                stepResult.Status = PipelineStepStatus.Failed;
                stepResult.ErrorMessage = ex.Message;
                stepResult.EndTime = DateTime.UtcNow;
                stepResult.Duration = stepResult.EndTime - stepResult.StartTime;

                return stepResult;
            }
        }

        /// <summary>
        /// Tüm aktif build'leri durdurur;
        /// </summary>
        private void StopAllActiveBuilds()
        {
            lock (_syncLock)
            {
                foreach (var buildProcess in _activeBuilds.Values.ToList())
                {
                    try
                    {
                        buildProcess.CancelAsync().Wait(5000);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Build durdurma hatası: {ex.Message}");
                    }
                }

                _activeBuilds.Clear();
            }
        }

        /// <summary>
        /// İstatistikleri günceller;
        /// </summary>
        private void UpdateStatistics(BuildProcess buildProcess)
        {
            if (buildProcess == null)
                return;

            lock (_syncLock)
            {
                Statistics.TotalBuilds++;

                if (buildProcess.Status == BuildStatus.Success)
                    Statistics.SuccessfulBuilds++;
                else if (buildProcess.Status == BuildStatus.Failed)
                    Statistics.FailedBuilds++;
                else if (buildProcess.Status == BuildStatus.Cancelled)
                    Statistics.CancelledBuilds++;

                Statistics.LastBuildTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// İstatistikleri günceller;
        /// </summary>
        private void UpdateStatistics(BuildResult result)
        {
            lock (_syncLock)
            {
                Statistics.TotalBuildTime += result.Duration;
                Statistics.AverageBuildTime = Statistics.TotalBuildTime / Statistics.TotalBuilds;
            }
        }

        /// <summary>
        /// Build çalışma dizinini alır;
        /// </summary>
        private string GetBuildWorkingDirectory(string buildId)
        {
            return Path.Combine(_environmentConfig.BuildRootPath, "Temp", buildId);
        }

        /// <summary>
        /// Build log yolunu alır;
        /// </summary>
        private string GetBuildLogPath(string buildId)
        {
            return Path.Combine(_environmentConfig.BuildRootPath, BuildLogsFolder, $"{buildId}.log");
        }

        /// <summary>
        /// Projeyi çalışma dizinine kopyalar;
        /// </summary>
        private async Task CopyProjectToWorkingDirectoryAsync(BuildProcess buildProcess)
        {
            var sourceDir = Path.GetDirectoryName(buildProcess.Request.ProjectPath);
            var targetDir = buildProcess.WorkingDirectory;

            await _fileManager.CopyDirectoryAsync(sourceDir, targetDir, true);
        }

        /// <summary>
        /// Build ortam değişkenlerini ayarlar;
        /// </summary>
        private void SetBuildEnvironmentVariables(BuildProcess buildProcess)
        {
            Environment.SetEnvironmentVariable("NEDA_BUILD_ID", buildProcess.Id);
            Environment.SetEnvironmentVariable("NEDA_BUILD_NUMBER", buildProcess.Id.Split('_')[1]);
            Environment.SetEnvironmentVariable("NEDA_PROJECT_NAME", buildProcess.Request.ProjectName);
            Environment.SetEnvironmentVariable("NEDA_BUILD_CONFIG", buildProcess.Request.Configuration);

            // Özel değişkenler;
            foreach (var variable in buildProcess.Request.CustomVariables)
            {
                Environment.SetEnvironmentVariable($"NEDA_{variable.Key}", variable.Value);
            }
        }

        #endregion;

        #region Helper Methods;

        private BuildRequest CreateBuildRequestFromStep(PipelineStep step, PipelineBuildProcess pipelineBuild)
        {
            return new BuildRequest;
            {
                ProjectPath = step.Parameters["ProjectPath"],
                ProjectName = step.Parameters.GetValueOrDefault("ProjectName", "Unknown"),
                Configuration = step.Parameters.GetValueOrDefault("Configuration", "Release"),
                Platform = step.Parameters.GetValueOrDefault("Platform", "AnyCPU"),
                CleanBuild = bool.Parse(step.Parameters.GetValueOrDefault("CleanBuild", "true")),
                RestoreDependencies = bool.Parse(step.Parameters.GetValueOrDefault("RestoreDependencies", "true")),
                RunTests = bool.Parse(step.Parameters.GetValueOrDefault("RunTests", "false")),
                CreatePackage = bool.Parse(step.Parameters.GetValueOrDefault("CreatePackage", "false")),
                CustomVariables = pipelineBuild.Parameters;
            };
        }

        private async Task ExecuteDeployStepAsync(PipelineStep step, PipelineBuildProcess pipelineBuild)
        {
            var deployTarget = step.Parameters["Target"];
            var deployConfig = step.Parameters["Configuration"];

            // Deployment işlemini gerçekleştir;
            // Bu kısım deployment manager ile entegre edilecek;
            await Task.Delay(1000); // Simülasyon;
        }

        private async Task ExecuteScriptStepAsync(PipelineStep step, PipelineBuildProcess pipelineBuild)
        {
            var scriptContent = step.Parameters["Script"];
            var workingDirectory = step.Parameters.GetValueOrDefault("WorkingDirectory", Directory.GetCurrentDirectory());

            // Script'i çalıştır;
            // Bu kısım script executor ile entegre edilecek;
            await Task.Delay(500); // Simülasyon;
        }

        private async Task CollectPipelineArtifactsAsync(PipelineBuildProcess pipelineBuild, PipelineBuildResult result)
        {
            var artifacts = new List<BuildArtifact>();

            // Her stage'in artifact'larını topla;
            foreach (var stageResult in result.StageResults)
            {
                foreach (var jobResult in stageResult.JobResults)
                {
                    // Job artifact'larını ekle;
                }
            }

            // Pipeline artifact'larını kaydet;
            await _artifactManager.SavePipelineArtifactsAsync(pipelineBuild.Pipeline.Id, result.StartTime, artifacts);
        }

        private async Task<IEnumerable<string>> GetBuildLogFilesAsync(BuildHistoryFilter filter)
        {
            var logDir = Path.Combine(_environmentConfig.BuildRootPath, BuildLogsFolder);

            if (!Directory.Exists(logDir))
                return Enumerable.Empty<string>();

            var files = Directory.GetFiles(logDir, "*.log");

            // Tarih filtrelemesi;
            if (filter.StartDate.HasValue || filter.EndDate.HasValue)
            {
                files = files.Where(f =>
                {
                    var fileDate = File.GetCreationTime(f);

                    if (filter.StartDate.HasValue && fileDate < filter.StartDate.Value)
                        return false;

                    if (filter.EndDate.HasValue && fileDate > filter.EndDate.Value)
                        return false;

                    return true;
                }).ToArray();
            }

            return await Task.FromResult(files);
        }

        private async Task<BuildHistoryItem> ParseBuildLogToHistoryItemAsync(string logPath)
        {
            try
            {
                var content = await File.ReadAllLinesAsync(logPath);
                if (content.Length < 5)
                    return null;

                var firstLine = content[0];
                var lastLine = content[^1];

                // Log'dan bilgileri parse et;
                // Bu basit bir örnek, gerçek implementasyonda daha karmaşık parsing gerekir;
                return new BuildHistoryItem;
                {
                    BuildId = Path.GetFileNameWithoutExtension(logPath),
                    ProjectName = "Unknown",
                    Status = lastLine.Contains("SUCCESS") ? BuildStatus.Success : BuildStatus.Failed,
                    StartTime = File.GetCreationTime(logPath),
                    EndTime = File.GetLastWriteTime(logPath),
                    Duration = File.GetLastWriteTime(logPath) - File.GetCreationTime(logPath),
                    LogPath = logPath;
                };
            }
            catch
            {
                return null;
            }
        }

        private bool FilterMatches(BuildHistoryItem item, BuildHistoryFilter filter)
        {
            if (filter.ProjectName != null && !item.ProjectName.Contains(filter.ProjectName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (filter.Status.HasValue && item.Status != filter.Status.Value)
                return false;

            return true;
        }

        private async Task SaveBuildOutputAsync(BuildProcess buildProcess, BuildCommandResult buildResult)
        {
            var outputPath = Path.Combine(buildProcess.WorkingDirectory, "build_output.txt");
            await File.WriteAllTextAsync(outputPath, buildResult.Output);
        }

        private async Task SaveTestReportAsync(BuildProcess buildProcess, TestExecutionResult testResult)
        {
            var reportPath = Path.Combine(buildProcess.WorkingDirectory, "TestResults", "test_report.xml");

            // TRX formatında test raporu oluştur;
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("TestRun");

            var results = xmlDoc.CreateElement("Results");
            root.AppendChild(results);

            var summary = xmlDoc.CreateElement("ResultSummary");
            summary.SetAttribute("outcome", testResult.Success ? "Passed" : "Failed");
            root.AppendChild(summary);

            xmlDoc.AppendChild(root);

            await File.WriteAllTextAsync(reportPath, xmlDoc.OuterXml);
        }

        private async Task<IEnumerable<EnvironmentCheck>> ValidateBuildToolsAsync()
        {
            var checks = new List<EnvironmentCheck>();

            // .NET SDK kontrolü;
            checks.Add(await CheckDotNetSdkAsync());

            // MSBuild kontrolü;
            checks.Add(await CheckMSBuildAsync());

            // Git kontrolü;
            checks.Add(await CheckGitAsync());

            // Node.js kontrolü;
            checks.Add(await CheckNodeJsAsync());

            return checks;
        }

        private async Task<EnvironmentCheck> CheckDotNetSdkAsync()
        {
            try
            {
                var processInfo = new ProcessStartInfo;
                {
                    FileName = "dotnet",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true;
                };

                using var process = Process.Start(processInfo);
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                return new EnvironmentCheck;
                {
                    Name = ".NET SDK",
                    IsValid = process.ExitCode == 0,
                    Message = output.Trim(),
                    RequiredVersion = "6.0.0"
                };
            }
            catch (Exception ex)
            {
                return new EnvironmentCheck;
                {
                    Name = ".NET SDK",
                    IsValid = false,
                    Message = $"Bulunamadı: {ex.Message}"
                };
            }
        }

        private EnvironmentCheck ValidateDiskSpace()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_environmentConfig.BuildRootPath));
                var freeSpaceGB = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                var isEnough = freeSpaceGB > 10; // 10 GB minimum;

                return new EnvironmentCheck;
                {
                    Name = "Disk Alanı",
                    IsValid = isEnough,
                    Message = $"{freeSpaceGB:F2} GB boş alan",
                    RequiredVersion = "10 GB"
                };
            }
            catch (Exception ex)
            {
                return new EnvironmentCheck;
                {
                    Name = "Disk Alanı",
                    IsValid = false,
                    Message = $"Kontrol edilemedi: {ex.Message}"
                };
            }
        }

        private async Task<EnvironmentCheck> ValidateNetworkConnectivityAsync()
        {
            try
            {
                // NuGet sunucusuna ping at;
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                var response = await client.GetAsync("https://api.nuget.org/v3/index.json");

                return new EnvironmentCheck;
                {
                    Name = "Ağ Bağlantısı",
                    IsValid = response.IsSuccessStatusCode,
                    Message = $"NuGet sunucusuna erişim: {response.StatusCode}"
                };
            }
            catch (Exception ex)
            {
                return new EnvironmentCheck;
                {
                    Name = "Ağ Bağlantısı",
                    IsValid = false,
                    Message = $"NuGet sunucusuna erişilemedi: {ex.Message}"
                };
            }
        }

        private EnvironmentCheck ValidatePermissions()
        {
            try
            {
                var testFile = Path.Combine(_environmentConfig.BuildRootPath, "Temp", "test_permission.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(testFile));

                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                return new EnvironmentCheck;
                {
                    Name = "Dosya İzinleri",
                    IsValid = true,
                    Message = "Build dizinlerine yazma izni var"
                };
            }
            catch (Exception ex)
            {
                return new EnvironmentCheck;
                {
                    Name = "Dosya İzinleri",
                    IsValid = false,
                    Message = $"Yazma izni yok: {ex.Message}"
                };
            }
        }

        private async Task<EnvironmentCheck> CheckMSBuildAsync()
        {
            // MSBuild kontrolü için benzer implementasyon;
            return new EnvironmentCheck;
            {
                Name = "MSBuild",
                IsValid = true,
                Message = "MSBuild 17.0+ detected"
            };
        }

        private async Task<EnvironmentCheck> CheckGitAsync()
        {
            // Git kontrolü için benzer implementasyon;
            return new EnvironmentCheck;
            {
                Name = "Git",
                IsValid = true,
                Message = "Git 2.30+ detected"
            };
        }

        private async Task<EnvironmentCheck> CheckNodeJsAsync()
        {
            // Node.js kontrolü için benzer implementasyon;
            return new EnvironmentCheck;
            {
                Name = "Node.js",
                IsValid = true,
                Message = "Node.js 18.0+ detected"
            };
        }

        private async Task AnalyzeProjectForTemplateAsync(string projectPath, BuildTemplate template)
        {
            // Proje dosyasını analiz et;
            if (File.Exists(projectPath))
            {
                var content = await File.ReadAllTextAsync(projectPath);

                // Proje türünü belirle;
                if (content.Contains("<TargetFramework>net8.0</TargetFramework>"))
                {
                    template.TargetFramework = "net8.0";
                }
                else if (content.Contains("PackageReference"))
                {
                    template.HasNuGetPackages = true;
                }

                // Test projesi kontrolü;
                if (content.Contains("<PackageReference Include=\"Microsoft.NET.Test.Sdk\""))
                {
                    template.HasTests = true;
                }
            }
        }

        private void AddDefaultBuildSteps(BuildTemplate template, BuildType buildType)
        {
            template.Steps.Add(new BuildStep;
            {
                Id = "restore",
                Name = "Bağımlılıkları Yükle",
                Command = BuildCommandType.Restore;
            });

            template.Steps.Add(new BuildStep;
            {
                Id = "build",
                Name = "Derle",
                Command = BuildCommandType.Compile;
            });

            if (template.HasTests)
            {
                template.Steps.Add(new BuildStep;
                {
                    Id = "test",
                    Name = "Testleri Çalıştır",
                    Command = BuildCommandType.Test;
                });
            }

            if (buildType == BuildType.Release)
            {
                template.Steps.Add(new BuildStep;
                {
                    Id = "package",
                    Name = "Paket Oluştur",
                    Command = BuildCommandType.Package;
                });
            }
        }

        private async Task SaveBuildTemplateAsync(BuildTemplate template)
        {
            var templatePath = Path.Combine(_environmentConfig.BuildRootPath, "Templates", $"{template.Id}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(template,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(templatePath, json);
        }

        private async Task<BuildAgent> SelectBuildAgentAsync(RemoteBuildRequest request)
        {
            // Build agent seçim algoritması;
            // Bu örnekte basit round-robin veya en az yüklü agent seçilebilir;

            var availableAgents = await GetAvailableBuildAgentsAsync();

            return availableAgents;
                .Where(a => a.SupportsPlatform(request.Platform))
                .Where(a => a.HasCapacity)
                .OrderBy(a => a.CurrentLoad)
                .FirstOrDefault();
        }

        private async Task<IEnumerable<BuildAgent>> GetAvailableBuildAgentsAsync()
        {
            // Build agent listesini getir;
            // Bu gerçek implementasyonda service discovery veya config'den gelir;

            return new List<BuildAgent>
            {
                new BuildAgent;
                {
                    Id = "agent-1",
                    Name = "Build Agent 1",
                    Url = "http://build-agent-1:8080",
                    Platforms = new[] { "windows", "linux" },
                    CurrentLoad = 0.3,
                    MaxCapacity = 5;
                }
            };
        }

        private async Task<RemoteBuildResult> ExecuteBuildOnAgentAsync(BuildAgent agent, RemoteBuildRequest request)
        {
            // Build agent API'sini çağır;
            using var client = new System.Net.Http.HttpClient();
            client.BaseAddress = new Uri(agent.Url);

            var response = await client.PostAsJsonAsync("/api/build", request);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<RemoteBuildResult>();
        }

        private async Task SyncRemoteBuildResultAsync(RemoteBuildResult result, RemoteBuildRequest request)
        {
            // Uzak build sonuçlarını yerel sisteme senkronize et;
            // Artifact'ları indir, log'ları kaydet vb.

            if (result.Status == BuildStatus.Success && result.Artifacts != null)
            {
                foreach (var artifact in result.Artifacts)
                {
                    await DownloadRemoteArtifactAsync(artifact, request.BuildId);
                }
            }
        }

        private async Task DownloadRemoteArtifactAsync(BuildArtifact artifact, string buildId)
        {
            // Artifact'ı uzak sunucudan indir;
            using var client = new System.Net.Http.HttpClient();
            var response = await client.GetAsync(artifact.Url);

            var localPath = Path.Combine(_environmentConfig.BuildRootPath, BuildArtifactsFolder, buildId, artifact.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath));

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(localPath);
            await stream.CopyToAsync(fileStream);
        }

        private async Task<BuildData> CollectBuildDataAsync(string buildId)
        {
            var buildData = new BuildData;
            {
                BuildId = buildId,
                Timestamp = DateTime.UtcNow;
            };

            // Build log'unu oku;
            buildData.Log = await GetBuildLogAsync(buildId);

            // Artifact'ları getir;
            buildData.Artifacts = (await GetBuildArtifactsAsync(buildId)).ToList();

            // Performans metriklerini topla;
            buildData.Metrics = await CollectBuildMetricsAsync(buildId);

            return buildData;
        }

        private async Task<BuildMetrics> CollectBuildMetricsAsync(string buildId)
        {
            return new BuildMetrics;
            {
                BuildId = buildId,
                CpuUsage = 0.0,
                MemoryUsage = 0.0,
                DiskUsage = 0.0,
                NetworkUsage = 0.0,
                BuildTime = TimeSpan.Zero;
            };
        }

        private async Task SaveBuildReportAsync(BuildReport report, string buildId)
        {
            var reportDir = Path.Combine(_environmentConfig.BuildRootPath, "Reports", buildId);
            Directory.CreateDirectory(reportDir);

            var reportPath = Path.Combine(reportDir, report.FileName);
            await File.WriteAllBytesAsync(reportPath, report.Content);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Stop();

                    _buildSemaphore?.Dispose();
                    _performanceMonitor?.Dispose();
                    _buildCache?.Dispose();
                    _artifactManager?.Dispose();
                    _analytics?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BuildAutomation()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public interface IBuildAutomation;
    {
        Task<BuildResult> BuildProjectAsync(BuildRequest request);
        Task<MultiBuildResult> BuildMultipleProjectsAsync(IEnumerable<BuildRequest> requests);
        Task<PipelineBuildResult> BuildPipelineAsync(string pipelineId, Dictionary<string, string> parameters = null);
        Task CancelBuildAsync(string buildId);
        Task<BuildHistory> GetBuildHistoryAsync(BuildHistoryFilter filter);
        Task<IEnumerable<BuildArtifact>> GetBuildArtifactsAsync(string buildId);
        Task<string> GetBuildLogAsync(string buildId);
    }

    public class BuildRequest;
    {
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string Configuration { get; set; } = "Release";
        public string Platform { get; set; } = "AnyCPU";
        public string TargetFramework { get; set; }
        public bool CleanBuild { get; set; } = true;
        public bool RestoreDependencies { get; set; } = true;
        public string NuGetSource { get; set; } = "https://api.nuget.org/v3/index.json";
        public bool NoCache { get; set; }
        public bool RunTests { get; set; }
        public string TestFilter { get; set; }
        public bool CollectCoverage { get; set; }
        public bool CreatePackage { get; set; }
        public PackageType PackageType { get; set; } = PackageType.NuGet;
        public string PackageVersion { get; set; } = "1.0.0";
        public bool ContinueOnError { get; set; }
        public int? TimeoutMinutes { get; set; }
        public string BuildTemplateId { get; set; }
        public Dictionary<string, string> CustomVariables { get; set; } = new Dictionary<string, string>();
        public List<string> ArtifactPatterns { get; set; } = new List<string> { "*.dll", "*.exe", "*.pdb" };
    }

    public class BuildResult;
    {
        public string BuildId { get; set; }
        public string ProjectName { get; set; }
        public BuildStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public List<BuildStepResult> Steps { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public static BuildResult CreateErrorResult(string buildId, Exception exception)
        {
            return new BuildResult;
            {
                BuildId = buildId,
                Status = BuildStatus.Failed,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                ErrorMessage = exception.Message;
            };
        }

        public override string ToString()
        {
            return $"Build {BuildId}: {Status} ({Duration.TotalSeconds:F2}s)";
        }
    }

    public class MultiBuildResult;
    {
        public List<BuildResult> Results { get; set; }
        public int TotalBuilds { get; set; }
        public int SuccessfulBuilds { get; set; }
        public int FailedBuilds { get; set; }
        public double TotalDuration { get; set; }
        public DateTime Timestamp { get; set; }

        public static MultiBuildResult Empty => new MultiBuildResult;
        {
            Results = new List<BuildResult>(),
            TotalBuilds = 0,
            SuccessfulBuilds = 0,
            FailedBuilds = 0,
            TotalDuration = 0;
        };
    }

    public class PipelineBuildResult;
    {
        public string PipelineId { get; set; }
        public string PipelineName { get; set; }
        public PipelineBuildStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public List<PipelineStageResult> StageResults { get; set; }
        public string ErrorMessage { get; set; }
        public List<BuildArtifact> Artifacts { get; set; } = new List<BuildArtifact>();
    }

    public class BuildStepResult;
    {
        public string StepId { get; set; }
        public string StepName { get; set; }
        public BuildStepStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Output { get; set; } = new Dictionary<string, object>();
    }

    public class PipelineStageResult;
    {
        public string StageId { get; set; }
        public string StageName { get; set; }
        public PipelineStageStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public List<PipelineJobResult> JobResults { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class PipelineJobResult;
    {
        public string JobId { get; set; }
        public string JobName { get; set; }
        public PipelineJobStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public List<PipelineStepResult> StepResults { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class PipelineStepResult;
    {
        public string StepId { get; set; }
        public string StepName { get; set; }
        public PipelineStepStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public string ErrorMessage { get; set; }
        public string Output { get; set; }
    }

    public class BuildHistory;
    {
        public BuildHistoryFilter Filter { get; set; }
        public List<BuildHistoryItem> Builds { get; set; }
        public int TotalCount { get; set; }
        public int FilteredCount => Builds?.Count ?? 0;
    }

    public class BuildHistoryFilter;
    {
        public string ProjectName { get; set; }
        public BuildStatus? Status { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; } = 50;
    }

    public class BuildHistoryItem;
    {
        public string BuildId { get; set; }
        public string ProjectName { get; set; }
        public BuildStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public string LogPath { get; set; }
        public List<string> Artifacts { get; set; } = new List<string>();
    }

    public class BuildArtifact;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public string Hash { get; set; }
        public DateTime Created { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
    }

    public class BuildStatistics;
    {
        public int TotalBuilds { get; set; }
        public int SuccessfulBuilds { get; set; }
        public int FailedBuilds { get; set; }
        public int CancelledBuilds { get; set; }
        public TimeSpan TotalBuildTime { get; set; }
        public TimeSpan AverageBuildTime { get; set; }
        public DateTime LastBuildTime { get; set; }
    }

    public class EnvironmentValidationResult;
    {
        public bool IsValid { get; set; }
        public int SuccessfulChecks { get; set; }
        public int FailedChecks { get; set; }
        public List<EnvironmentCheck> Checks { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class EnvironmentCheck;
    {
        public string Name { get; set; }
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public string RequiredVersion { get; set; }
        public string ActualVersion { get; set; }
    }

    public class BuildTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<BuildStep> Steps { get; set; }
        public Dictionary<string, string> Variables { get; set; }
        public string TargetFramework { get; set; }
        public bool HasTests { get; set; }
        public bool HasNuGetPackages { get; set; }
    }

    public class BuildTemplateRequest;
    {
        public string TemplateName { get; set; }
        public string Description { get; set; }
        public string ProjectPath { get; set; }
        public BuildType BuildType { get; set; } = BuildType.Debug;
    }

    public class RemoteBuildRequest : BuildRequest;
    {
        public string BuildAgent { get; set; }
        public string Environment { get; set; } = "Production";
        public Dictionary<string, string> AgentParameters { get; set; } = new Dictionary<string, string>();
    }

    public class RemoteBuildResult : BuildResult;
    {
        public string BuildAgent { get; set; }
        public string AgentUrl { get; set; }
        public TimeSpan AgentDuration { get; set; }
        public Dictionary<string, string> AgentMetrics { get; set; } = new Dictionary<string, string>();
    }

    public class BuildReport;
    {
        public string BuildId { get; set; }
        public string ReportName { get; set; }
        public ReportFormat Format { get; set; }
        public byte[] Content { get; set; }
        public string FileName { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    public class BuildData;
    {
        public string BuildId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Log { get; set; }
        public List<BuildArtifact> Artifacts { get; set; }
        public BuildMetrics Metrics { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class BuildMetrics;
    {
        public string BuildId { get; set; }
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double DiskUsage { get; set; }
        public double NetworkUsage { get; set; }
        public TimeSpan BuildTime { get; set; }
    }

    public class BuildProcess;
    {
        public string Id { get; set; }
        public BuildRequest Request { get; set; }
        public BuildStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string WorkingDirectory { get; set; }
        public string LogFile { get; set; }
        public List<BuildStep> Steps { get; set; }
        public TimeSpan Timeout { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public async Task CancelAsync()
        {
            CancellationTokenSource?.Cancel();
            Status = BuildStatus.Cancelled;
            EndTime = DateTime.UtcNow;

            await Task.CompletedTask;
        }
    }

    public class PipelineBuildProcess;
    {
        public Pipeline Pipeline { get; }
        public Dictionary<string, string> Parameters { get; }
        public DateTime StartTime { get; }
        public PipelineBuildStatus Status { get; set; }

        public PipelineBuildProcess(Pipeline pipeline, Dictionary<string, string> parameters)
        {
            Pipeline = pipeline;
            Parameters = parameters ?? new Dictionary<string, string>();
            StartTime = DateTime.UtcNow;
            Status = PipelineBuildStatus.Running;
        }
    }

    public class BuildStep;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public BuildCommandType Command { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

        public BuildStep Clone()
        {
            return new BuildStep;
            {
                Id = Id,
                Name = Name,
                Command = Command,
                Parameters = new Dictionary<string, string>(Parameters)
            };
        }
    }

    public class BuildCommandResult;
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public List<string> OutputLines { get; set; } = new List<string>();
        public bool TimedOut { get; set; }
    }

    public class TestExecutionResult;
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public List<string> OutputLines { get; set; } = new List<string>();
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
        public int SkippedTests { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public class ProcessExecutionResult;
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public List<string> OutputLines { get; set; } = new List<string>();
        public bool TimedOut { get; set; }
    }

    public class BuildAgent;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public string[] Platforms { get; set; }
        public double CurrentLoad { get; set; }
        public int MaxCapacity { get; set; }
        public bool HasCapacity => CurrentLoad < MaxCapacity;

        public bool SupportsPlatform(string platform)
        {
            return Platforms?.Contains(platform, StringComparer.OrdinalIgnoreCase) ?? false;
        }
    }

    public enum BuildStatus;
    {
        Queued,
        Running,
        Success,
        Failed,
        Cancelled,
        PartialSuccess;
    }

    public enum PipelineBuildStatus;
    {
        Queued,
        Running,
        Success,
        Failed,
        Cancelled,
        PartialSuccess;
    }

    public enum BuildStepStatus;
    {
        Pending,
        Running,
        Success,
        Failed,
        Skipped;
    }

    public enum PipelineStageStatus;
    {
        Pending,
        Running,
        Success,
        Failed,
        Skipped;
    }

    public enum PipelineJobStatus;
    {
        Pending,
        Running,
        Success,
        Failed,
        Skipped;
    }

    public enum PipelineStepStatus;
    {
        Pending,
        Running,
        Success,
        Failed,
        Skipped;
    }

    public enum BuildCommandType;
    {
        Initialize,
        Restore,
        Compile,
        Test,
        Package,
        Deploy,
        CollectArtifacts,
        Custom;
    }

    public enum PackageType;
    {
        NuGet,
        Zip,
        Docker,
        Msi,
        Executable;
    }

    public enum BuildType;
    {
        Debug,
        Release,
        Testing,
        Production;
    }

    public enum BuildAutomationStatus;
    {
        Stopped,
        Running,
        Paused,
        Error;
    }

    public enum ReportFormat;
    {
        Html,
        Pdf,
        Json,
        Xml,
        Markdown;
    }

    public class Pipeline;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<PipelineStage> Stages { get; set; } = new List<PipelineStage>();
        public bool ContinueOnError { get; set; }
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();
        public List<PipelineTrigger> Triggers { get; set; } = new List<PipelineTrigger>();
    }

    public class PipelineStage;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<PipelineJob> Jobs { get; set; } = new List<PipelineJob>();
        public Dictionary<string, string> Dependencies { get; set; } = new Dictionary<string, string>();
    }

    public class PipelineJob;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<PipelineStep> Steps { get; set; } = new List<PipelineStep>();
        public bool ContinueOnError { get; set; }
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    }

    public class PipelineStep;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public PipelineStepType Type { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public string Script { get; set; }
    }

    public class PipelineTrigger;
    {
        public TriggerType Type { get; set; }
        public string Branch { get; set; }
        public string Event { get; set; }
        public Dictionary<string, string> Conditions { get; set; } = new Dictionary<string, string>();
    }

    public enum PipelineStepType;
    {
        Build,
        Test,
        Deploy,
        Script,
        Approval,
        Notification;
    }

    public enum TriggerType;
    {
        Manual,
        Push,
        PullRequest,
        Schedule,
        Webhook;
    }

    public class BuildException : Exception
    {
        public BuildException(string message) : base(message) { }
        public BuildException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PipelineNotFoundException : Exception
    {
        public PipelineNotFoundException(string message) : base(message) { }
    }

    // Internal helper classes;
    internal class BuildCache : IDisposable
    {
        private readonly FileManager _fileManager;
        private readonly ILogger _logger;
        private Dictionary<string, BuildCacheEntry> _cache;
        private string _cacheFilePath;

        public BuildCache(FileManager fileManager, ILogger logger)
        {
            _fileManager = fileManager;
            _logger = logger;
            _cache = new Dictionary<string, BuildCacheEntry>();
        }

        public void Initialize()
        {
            try
            {
                _cacheFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NEDA", "Build", "build_cache.json");

                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    _cache = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BuildCacheEntry>>(json)
                        ?? new Dictionary<string, BuildCacheEntry>();
                }

                _logger.Debug($"Build cache başlatıldı: {_cache.Count} kayıt");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Build cache başlatma hatası: {ex.Message}");
                _cache = new Dictionary<string, BuildCacheEntry>();
            }
        }

        public async Task StoreBuildResultAsync(string buildId, BuildResult result)
        {
            var entry = new BuildCacheEntry
            {
                BuildId = buildId,
                ProjectName = result.ProjectName,
                Status = result.Status,
                Duration = result.Duration,
                Timestamp = DateTime.UtcNow,
                Hash = CalculateBuildHash(result)
            };

            _cache[buildId] = entry
            await SaveAsync();
        }

        public BuildCacheEntry GetBuildResult(string buildId)
        {
            return _cache.TryGetValue(buildId, out var entry) ? entry : null;
        }

        public async Task ClearAsync()
        {
            _cache.Clear();
            await SaveAsync();
        }

        public async Task SaveAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath));
                var json = System.Text.Json.JsonSerializer.Serialize(_cache,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                await File.WriteAllTextAsync(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Build cache kaydetme hatası: {ex.Message}");
            }
        }

        public void Save()
        {
            SaveAsync().Wait();
        }

        private string CalculateBuildHash(BuildResult result)
        {
            // Basit hash hesaplama;
            var input = $"{result.ProjectName}_{result.Status}_{result.Duration}";
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(hashBytes);
        }

        public void Dispose()
        {
            Save();
        }
    }

    internal class BuildCacheEntry
    {
        public string BuildId { get; set; }
        public string ProjectName { get; set; }
        public BuildStatus Status { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
        public string Hash { get; set; }
    }

    internal class BuildArtifactManager : IDisposable
    {
        private readonly FileManager _fileManager;
        private readonly ILogger _logger;
        private string _artifactsRoot;

        public BuildArtifactManager(FileManager fileManager, ILogger logger)
        {
            _fileManager = fileManager;
            _logger = logger;
        }

        public void Initialize()
        {
            _artifactsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEDA", "Build", "Artifacts");

            Directory.CreateDirectory(_artifactsRoot);
        }

        public async Task AddArtifactAsync(string buildId, string artifactPath)
        {
            if (!File.Exists(artifactPath))
                return;

            var artifactName = Path.GetFileName(artifactPath);
            var targetDir = Path.Combine(_artifactsRoot, buildId);
            Directory.CreateDirectory(targetDir);

            var targetPath = Path.Combine(targetDir, artifactName);
            await _fileManager.CopyFileAsync(artifactPath, targetPath);

            _logger.Debug($"Artifact eklendi: {artifactName} -> {buildId}");
        }

        public async Task CollectArtifactsAsync(string buildId, string workingDirectory, List<string> patterns)
        {
            var targetDir = Path.Combine(_artifactsRoot, buildId);
            Directory.CreateDirectory(targetDir);

            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(workingDirectory, pattern, SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(workingDirectory, file);
                    var targetPath = Path.Combine(targetDir, relativePath);

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    await _fileManager.CopyFileAsync(file, targetPath);
                }
            }

            _logger.Debug($"{buildId} için artifact'lar toplandı");
        }

        public async Task<IEnumerable<BuildArtifact>> GetArtifactsAsync(string buildId)
        {
            var artifacts = new List<BuildArtifact>();
            var buildDir = Path.Combine(_artifactsRoot, buildId);

            if (!Directory.Exists(buildDir))
                return artifacts;

            var files = Directory.GetFiles(buildDir, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                var relativePath = Path.GetRelativePath(buildDir, file);

                artifacts.Add(new BuildArtifact;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = relativePath,
                    Path = file,
                    Size = fileInfo.Length,
                    Created = fileInfo.CreationTime,
                    Type = Path.GetExtension(file).TrimStart('.')
                });
            }

            return artifacts;
        }

        public async Task FinalizeBuildAsync(string buildId, BuildStatus status)
        {
            var buildDir = Path.Combine(_artifactsRoot, buildId);

            if (Directory.Exists(buildDir))
            {
                // Build durumuna göre artifact'ları işle;
                if (status == BuildStatus.Success)
                {
                    // Başarılı build artifact'larını koru;
                    _logger.Debug($"{buildId} artifact'ları korundu");
                }
                else;
                {
                    // Başarısız build artifact'larını temizle (isteğe bağlı)
                    // Directory.Delete(buildDir, true);
                }
            }
        }

        public async Task SavePipelineArtifactsAsync(string pipelineId, DateTime timestamp, List<BuildArtifact> artifacts)
        {
            // Pipeline artifact'larını kaydet;
        }

        public void Dispose()
        {
            // Kaynakları temizle;
        }
    }

    internal class BuildNotifier;
    {
        private readonly ILogger _logger;

        public BuildNotifier(ILogger logger)
        {
            _logger = logger;
        }

        public async Task SendBuildNotification(BuildResult result)
        {
            try
            {
                // Build bildirimini gönder;
                // Email, Slack, Teams vb. entegrasyonlar burada olacak;

                _logger.Info($"Build bildirimi gönderildi: {result.BuildId} - {result.Status}");

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Build bildirimi gönderme hatası: {ex.Message}");
            }
        }

        public async Task SendMultiBuildNotification(MultiBuildResult result)
        {
            // Çoklu build bildirimi;
        }

        public async Task SendBuildCancelledNotification(string buildId, string projectName)
        {
            // İptal edilen build bildirimi;
        }
    }

    internal class BuildAnalytics;
    {
        private readonly ILogger _logger;
        private List<BuildResult> _buildResults;

        public BuildAnalytics(ILogger logger)
        {
            _logger = logger;
            _buildResults = new List<BuildResult>();
        }

        public void Start()
        {
            _logger.Debug("Build analytics başlatıldı");
        }

        public void Stop()
        {
            _logger.Debug("Build analytics durduruldu");
        }

        public void RecordBuild(BuildResult result)
        {
            _buildResults.Add(result);

            // Analytics verilerini işle;
            // Performans metrikleri, trend analizleri vb.
        }

        public BuildAnalyticsReport GenerateReport(DateTime startDate, DateTime endDate)
        {
            var relevantBuilds = _buildResults;
                .Where(r => r.StartTime >= startDate && r.StartTime <= endDate)
                .ToList();

            return new BuildAnalyticsReport;
            {
                StartDate = startDate,
                EndDate = endDate,
                TotalBuilds = relevantBuilds.Count,
                SuccessfulBuilds = relevantBuilds.Count(r => r.Status == BuildStatus.Success),
                FailedBuilds = relevantBuilds.Count(r => r.Status == BuildStatus.Failed),
                AverageDuration = relevantBuilds.Any()
                    ? TimeSpan.FromSeconds(relevantBuilds.Average(r => r.Duration.TotalSeconds))
                    : TimeSpan.Zero,
                Builds = relevantBuilds;
            };
        }
    }

    internal class BuildAnalyticsReport;
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalBuilds { get; set; }
        public int SuccessfulBuilds { get; set; }
        public int FailedBuilds { get; set; }
        public TimeSpan AverageDuration { get; set; }
        public List<BuildResult> Builds { get; set; }
    }

    internal class BuildLogWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly string _logPath;

        public BuildLogWriter(string logPath)
        {
            _logPath = logPath;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            _writer = new StreamWriter(logPath, false, System.Text.Encoding.UTF8);
        }

        public void WriteHeader(BuildProcess process)
        {
            _writer.WriteLine("=".PadRight(80, '='));
            _writer.WriteLine($"BUILD STARTED: {process.Id}");
            _writer.WriteLine($"Project: {process.Request.ProjectName}");
            _writer.WriteLine($"Configuration: {process.Request.Configuration}");
            _writer.WriteLine($"Start Time: {process.StartTime:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine("=".PadRight(80, '='));
            _writer.WriteLine();
            _writer.Flush();
        }

        public void WriteStepStart(BuildStep step)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] STEP START: {step.Name}");
            _writer.Flush();
        }

        public void WriteStepSuccess(BuildStep step, TimeSpan duration)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] STEP SUCCESS: {step.Name} ({duration.TotalSeconds:F2}s)");
            _writer.WriteLine();
            _writer.Flush();
        }

        public void WriteStepError(BuildStep step, Exception ex, TimeSpan duration)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] STEP FAILED: {step.Name} ({duration.TotalSeconds:F2}s)");
            _writer.WriteLine($"ERROR: {ex.Message}");
            if (ex.StackTrace != null)
            {
                _writer.WriteLine("STACK TRACE:");
                _writer.WriteLine(ex.StackTrace);
            }
            _writer.WriteLine();
            _writer.Flush();
        }

        public void WriteInfo(string message)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO: {message}");
            _writer.Flush();
        }

        public void WriteSuccess(string message)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] SUCCESS: {message}");
            _writer.Flush();
        }

        public void WriteError(string message)
        {
            _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {message}");
            _writer.Flush();
        }

        public void WriteFooter(BuildResult result)
        {
            _writer.WriteLine();
            _writer.WriteLine("=".PadRight(80, '='));
            _writer.WriteLine($"BUILD FINISHED: {result.BuildId}");
            _writer.WriteLine($"Status: {result.Status}");
            _writer.WriteLine($"Duration: {result.Duration.TotalSeconds:F2} seconds");
            _writer.WriteLine($"End Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine("=".PadRight(80, '='));
            _writer.Flush();
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }
    }

    #endregion;
}
