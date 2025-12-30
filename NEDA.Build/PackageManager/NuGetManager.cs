// NEDA.Build/PackageManager/NuGetManager.cs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json;
using NLog;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Common;

namespace NEDA.Build.PackageManager;
{
    /// <summary>
    /// Industrial-grade NuGet package manager for NEDA build system;
    /// Handles package restoration, resolution, caching, and dependency management;
    /// </summary>
    public sealed class NuGetManager : INuGetManager, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly HttpClient _httpClient;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly PackageCacheManager _cacheManager;

        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _restoreSemaphore;
        private readonly Dictionary<string, PackageInfo> _packageCache;
        private readonly Dictionary<string, PackageRestoration> _activeRestorations;

        private bool _isInitialized;
        private bool _isDisposed;
        private Timer _cleanupTimer;
        private Timer _healthCheckTimer;

        public NuGetConfiguration Configuration { get; private set; }
        public ManagerStatus Status { get; private set; }
        public int CachedPackagesCount => _packageCache.Count;
        public long CacheSize => _cacheManager?.GetCacheSize() ?? 0;
        public int ActiveRestorations => _activeRestorations.Count;

        public event EventHandler<PackageRestoredEventArgs> PackageRestored;
        public event EventHandler<CacheUpdatedEventArgs> CacheUpdated;
        public event EventHandler<DependencyResolvedEventArgs> DependencyResolved;

        #endregion;

        #region Constructor;

        public NuGetManager(
            ILogger logger,
            IExceptionHandler exceptionHandler,
            HttpClient httpClient = null,
            PerformanceMonitor performanceMonitor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _httpClient = httpClient ?? new HttpClient();
            _performanceMonitor = performanceMonitor ?? new PerformanceMonitor();

            _cacheManager = new PackageCacheManager(logger);
            _packageCache = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
            _activeRestorations = new Dictionary<string, PackageRestoration>();
            _restoreSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);

            Configuration = new NuGetConfiguration();
            Status = ManagerStatus.Stopped;

            _logger.Info("NuGet Manager initialized");
        }

        #endregion;

        #region Public Methods;

        public async Task InitializeAsync(NuGetConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                throw new InvalidOperationException("NuGet Manager already initialized");

            try
            {
                _logger.Info("Initializing NuGet Manager...");
                Status = ManagerStatus.Initializing;

                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ValidateConfiguration();

                // Initialize cache manager;
                await _cacheManager.InitializeAsync(Configuration.CacheDirectory, Configuration.MaxCacheSize, cancellationToken);

                // Load existing package cache;
                await LoadPackageCacheAsync(cancellationToken);

                // Setup timers;
                _cleanupTimer = new Timer(CleanupCacheCallback, null,
                    TimeSpan.FromHours(1), TimeSpan.FromHours(1));
                _healthCheckTimer = new Timer(HealthCheckCallback, null,
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

                // Warm up cache with frequently used packages;
                if (Configuration.PreloadCommonPackages)
                {
                    await PreloadCommonPackagesAsync(cancellationToken);
                }

                _isInitialized = true;
                Status = ManagerStatus.Running;

                _logger.Info($"NuGet Manager initialized. Cache location: {Configuration.CacheDirectory}, Max size: {FormatBytes(Configuration.MaxCacheSize)}");
            }
            catch (Exception ex)
            {
                Status = ManagerStatus.Error;
                _logger.Error($"Failed to initialize NuGet Manager: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NuGetManager.Initialize");
                throw new NuGetManagerInitializationException("Failed to initialize NuGet Manager", ex);
            }
        }

        public async Task<RestoreResult> RestorePackagesAsync(
            string projectPath,
            RestoreOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Project path cannot be empty", nameof(projectPath));

            if (!File.Exists(projectPath))
                throw new FileNotFoundException($"Project file not found: {projectPath}");

            var restorationId = GenerateRestorationId(projectPath);
            var restoration = new PackageRestoration(restorationId, projectPath, options);

            try
            {
                lock (_syncLock)
                {
                    _activeRestorations[restorationId] = restoration;
                }

                _logger.Info($"Starting package restoration for project: {projectPath}");
                restoration.Status = RestoreStatus.Running;

                using var perfScope = _performanceMonitor.StartMeasurement($"NuGetRestore.{Path.GetFileName(projectPath)}");

                // Acquire semaphore for parallel execution control;
                await _restoreSemaphore.WaitAsync(cancellationToken);

                try
                {
                    // Step 1: Parse project file and get dependencies;
                    var dependencies = await ParseProjectDependenciesAsync(projectPath, cancellationToken);
                    restoration.TotalPackages = dependencies.Count;

                    _logger.Debug($"Found {dependencies.Count} dependencies in project");

                    // Step 2: Resolve dependency graph;
                    var resolvedGraph = await ResolveDependencyGraphAsync(dependencies, cancellationToken);
                    restoration.ResolvedPackages = resolvedGraph.Count;

                    // Step 3: Download missing packages;
                    var downloadResults = await DownloadPackagesAsync(resolvedGraph, cancellationToken);

                    // Step 4: Extract packages to local cache;
                    var extractResults = await ExtractPackagesAsync(downloadResults, cancellationToken);

                    // Step 5: Generate restore lock file;
                    var lockFile = await GenerateLockFileAsync(resolvedGraph, projectPath, cancellationToken);

                    restoration.Status = RestoreStatus.Completed;
                    restoration.CompletionTime = DateTime.UtcNow;

                    var result = new RestoreResult;
                    {
                        RestorationId = restorationId,
                        ProjectPath = projectPath,
                        Success = true,
                        TotalPackages = restoration.TotalPackages,
                        ResolvedPackages = restoration.ResolvedPackages,
                        DownloadedPackages = downloadResults.Count(r => r.Status == DownloadStatus.Success),
                        CacheHits = downloadResults.Count(r => r.Status == DownloadStatus.Cached),
                        LockFile = lockFile,
                        Duration = restoration.Duration,
                        DownloadedSize = downloadResults.Sum(r => r.Size),
                        CacheSize = _cacheManager.GetCacheSize()
                    };

                    // Raise event;
                    OnPackageRestored(new PackageRestoredEventArgs;
                    {
                        RestorationId = restorationId,
                        ProjectPath = projectPath,
                        Result = result,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Info($"Package restoration completed for {projectPath}: {result.ResolvedPackages} packages restored");

                    return result;
                }
                finally
                {
                    _restoreSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                restoration.Status = RestoreStatus.Cancelled;
                _logger.Warn($"Package restoration cancelled for project: {projectPath}");
                throw;
            }
            catch (Exception ex)
            {
                restoration.Status = RestoreStatus.Failed;
                _logger.Error($"Package restoration failed for {projectPath}: {ex.Message}", ex);

                var errorResult = new RestoreResult;
                {
                    RestorationId = restorationId,
                    ProjectPath = projectPath,
                    Success = false,
                    ErrorMessage = ex.Message,
                    ErrorDetails = ex.ToString(),
                    Duration = restoration.Duration;
                };

                await _exceptionHandler.HandleExceptionAsync(ex, $"NuGetManager.RestorePackages.{restorationId}");
                return errorResult;
            }
            finally
            {
                lock (_syncLock)
                {
                    _activeRestorations.Remove(restorationId);
                }
            }
        }

        public async Task<PackageInfo> GetPackageInfoAsync(
            string packageId,
            string version = null,
            bool includePrerelease = false,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(packageId))
                throw new ArgumentException("Package ID cannot be empty", nameof(packageId));

            var cacheKey = $"{packageId.ToLowerInvariant()}_{version?.ToLowerInvariant() ?? "latest"}";

            // Check cache first;
            lock (_syncLock)
            {
                if (_packageCache.TryGetValue(cacheKey, out var cachedInfo))
                {
                    _logger.Debug($"Cache hit for package: {packageId}@{version}");
                    return cachedInfo;
                }
            }

            try
            {
                _logger.Debug($"Fetching package info for: {packageId}@{version ?? "latest"}");

                // Query NuGet API for package information;
                var packageInfo = await QueryPackageInfoAsync(packageId, version, includePrerelease, cancellationToken);

                // Update cache;
                lock (_syncLock)
                {
                    _packageCache[cacheKey] = packageInfo;
                }

                // Raise event;
                OnDependencyResolved(new DependencyResolvedEventArgs;
                {
                    PackageId = packageId,
                    Version = packageInfo.Version,
                    Source = packageInfo.Source,
                    Timestamp = DateTime.UtcNow;
                });

                return packageInfo;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get package info for {packageId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NuGetManager.GetPackageInfo.{packageId}");
                throw new PackageNotFoundException($"Package not found: {packageId}", ex);
            }
        }

        public async Task<PackageDownloadResult> DownloadPackageAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(packageId))
                throw new ArgumentException("Package ID cannot be empty", nameof(packageId));

            if (string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("Version cannot be empty", nameof(version));

            try
            {
                _logger.Debug($"Downloading package: {packageId}@{version}");

                // Check if package already exists in cache;
                var cacheResult = await _cacheManager.GetPackageFromCacheAsync(packageId, version, cancellationToken);
                if (cacheResult != null)
                {
                    _logger.Debug($"Package found in cache: {packageId}@{version}");
                    return new PackageDownloadResult;
                    {
                        PackageId = packageId,
                        Version = version,
                        Status = DownloadStatus.Cached,
                        LocalPath = cacheResult.Path,
                        Size = cacheResult.Size,
                        Cached = true;
                    };
                }

                // Download from configured sources;
                foreach (var source in Configuration.PackageSources)
                {
                    try
                    {
                        var downloadUrl = await GetPackageDownloadUrlAsync(packageId, version, source, cancellationToken);
                        if (string.IsNullOrWhiteSpace(downloadUrl))
                            continue;

                        var downloadResult = await DownloadFromSourceAsync(packageId, version, downloadUrl, source, cancellationToken);

                        if (downloadResult.Status == DownloadStatus.Success)
                        {
                            // Add to cache;
                            await _cacheManager.AddToCacheAsync(
                                packageId,
                                version,
                                downloadResult.LocalPath,
                                cancellationToken);

                            // Update package cache;
                            var packageInfo = new PackageInfo;
                            {
                                Id = packageId,
                                Version = version,
                                Source = source,
                                DownloadUrl = downloadUrl,
                                LocalPath = downloadResult.LocalPath,
                                Size = downloadResult.Size,
                                DownloadedAt = DateTime.UtcNow;
                            };

                            lock (_syncLock)
                            {
                                _packageCache[$"{packageId.ToLowerInvariant()}_{version.ToLowerInvariant()}"] = packageInfo;
                            }

                            // Raise cache update event;
                            OnCacheUpdated(new CacheUpdatedEventArgs;
                            {
                                PackageId = packageId,
                                Version = version,
                                Action = CacheAction.Added,
                                Size = downloadResult.Size,
                                Timestamp = DateTime.UtcNow;
                            });

                            return downloadResult;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to download from source {source}: {ex.Message}");
                        continue;
                    }
                }

                throw new PackageDownloadException($"Failed to download package {packageId}@{version} from any source");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to download package {packageId}@{version}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NuGetManager.DownloadPackage.{packageId}");
                throw;
            }
        }

        public async Task<bool> ClearCacheAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                _logger.Info("Clearing NuGet cache...");

                lock (_syncLock)
                {
                    _packageCache.Clear();
                }

                var result = await _cacheManager.ClearCacheAsync(cancellationToken);

                _logger.Info($"Cache cleared. Result: {result}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clear cache: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NuGetManager.ClearCache");
                throw;
            }
        }

        public async Task<CacheAnalysisResult> AnalyzeCacheAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                _logger.Debug("Analyzing cache...");

                var analysis = await _cacheManager.AnalyzeCacheAsync(cancellationToken);

                return new CacheAnalysisResult;
                {
                    TotalSize = analysis.TotalSize,
                    PackageCount = analysis.PackageCount,
                    OldestPackage = analysis.OldestPackage,
                    NewestPackage = analysis.NewestPackage,
                    LargestPackage = analysis.LargestPackage,
                    MostUsedPackage = analysis.MostUsedPackage,
                    AnalysisTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to analyze cache: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NuGetManager.AnalyzeCache");
                throw;
            }
        }

        public async Task<IEnumerable<PackageInfo>> SearchPackagesAsync(
            string searchTerm,
            int take = 20,
            bool includePrerelease = false,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(searchTerm))
                throw new ArgumentException("Search term cannot be empty", nameof(searchTerm));

            try
            {
                _logger.Debug($"Searching for packages: {searchTerm}");

                var results = new List<PackageInfo>();

                // Search in cache first;
                lock (_syncLock)
                {
                    var cachedResults = _packageCache.Values;
                        .Where(p => p.Id.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                                   (p.Description?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                        .Take(take / 2)
                        .ToList();

                    results.AddRange(cachedResults);
                }

                // If we need more results, query NuGet API;
                if (results.Count < take)
                {
                    var apiResults = await SearchNuGetApiAsync(searchTerm, take - results.Count, includePrerelease, cancellationToken);
                    results.AddRange(apiResults);
                }

                return results.DistinctBy(p => $"{p.Id}_{p.Version}").Take(take);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to search packages: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NuGetManager.SearchPackages.{searchTerm}");
                return Enumerable.Empty<PackageInfo>();
            }
        }

        public async Task<PackageUpdateCheckResult> CheckForUpdatesAsync(
            string projectPath,
            bool includePrerelease = false,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("Project path cannot be empty", nameof(projectPath));

            if (!File.Exists(projectPath))
                throw new FileNotFoundException($"Project file not found: {projectPath}");

            try
            {
                _logger.Debug($"Checking for updates in project: {projectPath}");

                var dependencies = await ParseProjectDependenciesAsync(projectPath, cancellationToken);
                var updateResults = new List<PackageUpdate>();

                foreach (var dependency in dependencies)
                {
                    try
                    {
                        var latestVersion = await GetLatestVersionAsync(dependency.Id, includePrerelease, cancellationToken);

                        if (latestVersion != null && latestVersion != dependency.Version)
                        {
                            var update = new PackageUpdate;
                            {
                                PackageId = dependency.Id,
                                CurrentVersion = dependency.Version,
                                LatestVersion = latestVersion,
                                IsPrerelease = latestVersion.Contains("-"),
                                ReleaseNotes = await GetReleaseNotesAsync(dependency.Id, latestVersion, cancellationToken)
                            };

                            updateResults.Add(update);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to check updates for {dependency.Id}: {ex.Message}");
                    }
                }

                return new PackageUpdateCheckResult;
                {
                    ProjectPath = projectPath,
                    TotalPackages = dependencies.Count,
                    UpdatesAvailable = updateResults.Count,
                    Updates = updateResults,
                    CheckTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to check for updates: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NuGetManager.CheckForUpdates.{projectPath}");
                throw;
            }
        }

        public async Task<PackageResolutionResult> ResolveDependenciesAsync(
            IEnumerable<PackageDependency> dependencies,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (dependencies == null)
                throw new ArgumentNullException(nameof(dependencies));

            try
            {
                _logger.Debug($"Resolving dependencies for {dependencies.Count()} packages");

                var resolvedGraph = await ResolveDependencyGraphAsync(dependencies.ToList(), cancellationToken);

                return new PackageResolutionResult;
                {
                    RootDependencies = dependencies.ToList(),
                    ResolvedPackages = resolvedGraph,
                    TotalPackages = resolvedGraph.Count,
                    HasConflicts = resolvedGraph.Any(p => p.HasVersionConflict),
                    ResolutionTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to resolve dependencies: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NuGetManager.ResolveDependencies");
                throw new DependencyResolutionException("Failed to resolve dependencies", ex);
            }
        }

        public RestoreStatus GetRestoreStatus(string restorationId)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(restorationId))
                throw new ArgumentException("Restoration ID cannot be empty", nameof(restorationId));

            lock (_syncLock)
            {
                if (_activeRestorations.TryGetValue(restorationId, out var restoration))
                {
                    return new RestoreStatus;
                    {
                        RestorationId = restorationId,
                        Status = restoration.Status,
                        Progress = restoration.Progress,
                        TotalPackages = restoration.TotalPackages,
                        ResolvedPackages = restoration.ResolvedPackages,
                        StartTime = restoration.StartTime,
                        Duration = restoration.Duration;
                    };
                }
            }

            throw new RestorationNotFoundException($"Restoration not found: {restorationId}");
        }

        public IReadOnlyList<PackageInfo> GetCachedPackages()
        {
            ValidateInitialized();

            lock (_syncLock)
            {
                return _packageCache.Values.ToList();
            }
        }

        #endregion;

        #region Private Methods;

        private async Task<List<PackageDependency>> ParseProjectDependenciesAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            var dependencies = new List<PackageDependency>();

            try
            {
                var fileExtension = Path.GetExtension(projectPath).ToLowerInvariant();

                switch (fileExtension)
                {
                    case ".csproj":
                        dependencies.AddRange(await ParseCsProjDependenciesAsync(projectPath, cancellationToken));
                        break;
                    case ".fsproj":
                        dependencies.AddRange(await ParseFsProjDependenciesAsync(projectPath, cancellationToken));
                        break;
                    case ".vbproj":
                        dependencies.AddRange(await ParseVbProjDependenciesAsync(projectPath, cancellationToken));
                        break;
                    case ".json":
                        dependencies.AddRange(await ParsePackageJsonDependenciesAsync(projectPath, cancellationToken));
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported project file type: {fileExtension}");
                }

                return dependencies;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to parse project dependencies: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<List<PackageDependency>> ParseCsProjDependenciesAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            var dependencies = new List<PackageDependency>();

            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(projectPath);

                var nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
                nsManager.AddNamespace("msbuild", "http://schemas.microsoft.com/developer/msbuild/2003");

                var packageReferenceNodes = xmlDoc.SelectNodes("//PackageReference", nsManager) ??
                                           xmlDoc.SelectNodes("//msbuild:PackageReference", nsManager);

                if (packageReferenceNodes != null)
                {
                    foreach (XmlNode node in packageReferenceNodes)
                    {
                        var include = node.Attributes?["Include"]?.Value;
                        var version = node.Attributes?["Version"]?.Value;

                        if (!string.IsNullOrWhiteSpace(include) && !string.IsNullOrWhiteSpace(version))
                        {
                            dependencies.Add(new PackageDependency;
                            {
                                Id = include.Trim(),
                                Version = version.Trim(),
                                IsDirectReference = true;
                            });
                        }
                    }
                }

                // Also check for PackageReference items in ItemGroup;
                var itemGroupNodes = xmlDoc.SelectNodes("//ItemGroup", nsManager);
                if (itemGroupNodes != null)
                {
                    foreach (XmlNode itemGroup in itemGroupNodes)
                    {
                        foreach (XmlNode child in itemGroup.ChildNodes)
                        {
                            if (child.Name == "PackageReference")
                            {
                                var include = child.Attributes?["Include"]?.Value;
                                var version = child.Attributes?["Version"]?.Value ?? child.InnerText;

                                if (!string.IsNullOrWhiteSpace(include) && !string.IsNullOrWhiteSpace(version))
                                {
                                    dependencies.Add(new PackageDependency;
                                    {
                                        Id = include.Trim(),
                                        Version = version.Trim(),
                                        IsDirectReference = true;
                                    });
                                }
                            }
                        }
                    }
                }

                _logger.Debug($"Parsed {dependencies.Count} dependencies from {projectPath}");
                return dependencies;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to parse csproj dependencies: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<Dictionary<string, ResolvedPackage>> ResolveDependencyGraphAsync(
            List<PackageDependency> dependencies,
            CancellationToken cancellationToken)
        {
            var resolvedGraph = new Dictionary<string, ResolvedPackage>();
            var resolutionQueue = new Queue<PackageDependency>(dependencies);

            while (resolutionQueue.Count > 0)
            {
                var dependency = resolutionQueue.Dequeue();
                var packageKey = $"{dependency.Id.ToLowerInvariant()}_{dependency.Version}";

                // Skip if already resolved;
                if (resolvedGraph.ContainsKey(packageKey))
                    continue;

                try
                {
                    // Get package info;
                    var packageInfo = await GetPackageInfoAsync(dependency.Id, dependency.Version, false, cancellationToken);

                    var resolvedPackage = new ResolvedPackage;
                    {
                        Id = packageInfo.Id,
                        Version = packageInfo.Version,
                        Source = packageInfo.Source,
                        Dependencies = packageInfo.Dependencies,
                        IsDirectReference = dependency.IsDirectReference,
                        ResolutionTime = DateTime.UtcNow;
                    };

                    resolvedGraph[packageKey] = resolvedPackage;

                    // Add dependencies to queue;
                    foreach (var dep in packageInfo.Dependencies)
                    {
                        resolutionQueue.Enqueue(new PackageDependency;
                        {
                            Id = dep.Id,
                            Version = dep.Version,
                            IsDirectReference = false;
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to resolve dependency {dependency.Id}@{dependency.Version}: {ex.Message}");

                    // Add as unresolved package;
                    resolvedGraph[packageKey] = new ResolvedPackage;
                    {
                        Id = dependency.Id,
                        Version = dependency.Version,
                        IsDirectReference = dependency.IsDirectReference,
                        HasResolutionError = true,
                        ResolutionError = ex.Message;
                    };
                }
            }

            return resolvedGraph;
        }

        private async Task<List<PackageDownloadResult>> DownloadPackagesAsync(
            Dictionary<string, ResolvedPackage> resolvedGraph,
            CancellationToken cancellationToken)
        {
            var downloadResults = new List<PackageDownloadResult>();
            var downloadTasks = new List<Task<PackageDownloadResult>>();

            // Create download tasks for all packages;
            foreach (var kvp in resolvedGraph)
            {
                if (kvp.Value.HasResolutionError)
                    continue;

                var task = DownloadPackageWithRetryAsync(
                    kvp.Value.Id,
                    kvp.Value.Version,
                    Configuration.DownloadRetryCount,
                    Configuration.DownloadRetryDelay,
                    cancellationToken);

                downloadTasks.Add(task);
            }

            // Execute downloads in parallel with limit;
            var downloadSemaphore = new SemaphoreSlim(Configuration.MaxParallelDownloads, Configuration.MaxParallelDownloads);
            var downloadResultsArray = await Task.WhenAll(
                downloadTasks.Select(async task =>
                {
                    await downloadSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        return await task;
                    }
                    finally
                    {
                        downloadSemaphore.Release();
                    }
                }));

            downloadResults.AddRange(downloadResultsArray);
            return downloadResults;
        }

        private async Task<PackageDownloadResult> DownloadPackageWithRetryAsync(
            string packageId,
            string version,
            int maxRetries,
            TimeSpan retryDelay,
            CancellationToken cancellationToken)
        {
            var retryCount = 0;

            while (retryCount <= maxRetries)
            {
                try
                {
                    return await DownloadPackageAsync(packageId, version, cancellationToken);
                }
                catch (Exception ex) when (retryCount < maxRetries)
                {
                    retryCount++;
                    _logger.Warn($"Download failed for {packageId}@{version}, retry {retryCount}/{maxRetries}: {ex.Message}");

                    if (retryCount <= maxRetries)
                    {
                        await Task.Delay(retryDelay, cancellationToken);
                    }
                }
            }

            throw new PackageDownloadException($"Failed to download package {packageId}@{version} after {maxRetries} retries");
        }

        private async Task<List<PackageExtractResult>> ExtractPackagesAsync(
            List<PackageDownloadResult> downloadResults,
            CancellationToken cancellationToken)
        {
            var extractResults = new List<PackageExtractResult>();

            foreach (var downloadResult in downloadResults)
            {
                if (downloadResult.Status != DownloadStatus.Success && downloadResult.Status != DownloadStatus.Cached)
                    continue;

                try
                {
                    var extractPath = await _cacheManager.ExtractPackageAsync(
                        downloadResult.PackageId,
                        downloadResult.Version,
                        downloadResult.LocalPath,
                        cancellationToken);

                    extractResults.Add(new PackageExtractResult;
                    {
                        PackageId = downloadResult.PackageId,
                        Version = downloadResult.Version,
                        Success = true,
                        ExtractPath = extractPath,
                        ExtractTime = DateTime.UtcNow;
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to extract package {downloadResult.PackageId}: {ex.Message}", ex);

                    extractResults.Add(new PackageExtractResult;
                    {
                        PackageId = downloadResult.PackageId,
                        Version = downloadResult.Version,
                        Success = false,
                        Error = ex.Message;
                    });
                }
            }

            return extractResults;
        }

        private async Task<LockFile> GenerateLockFileAsync(
            Dictionary<string, ResolvedPackage> resolvedGraph,
            string projectPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var lockFile = new LockFile;
                {
                    Version = "3.0.0",
                    ProjectPath = projectPath,
                    GeneratedAt = DateTime.UtcNow,
                    Packages = resolvedGraph.Values;
                        .Where(p => !p.HasResolutionError)
                        .Select(p => new LockFilePackage;
                        {
                            Id = p.Id,
                            Version = p.Version,
                            Source = p.Source,
                            IsDirectReference = p.IsDirectReference,
                            Dependencies = p.Dependencies;
                        })
                        .ToList()
                };

                // Calculate hash for change detection;
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var json = JsonConvert.SerializeObject(lockFile.Packages.OrderBy(p => p.Id));
                lockFile.Hash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(json)));

                // Save lock file;
                var lockFilePath = Path.Combine(Path.GetDirectoryName(projectPath), "packages.lock.json");
                await File.WriteAllTextAsync(lockFilePath, JsonConvert.SerializeObject(lockFile, Formatting.Indented), cancellationToken);

                return lockFile;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate lock file: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<PackageInfo> QueryPackageInfoAsync(
            string packageId,
            string version,
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            foreach (var source in Configuration.PackageSources)
            {
                try
                {
                    var packageInfo = await QueryPackageInfoFromSourceAsync(packageId, version, source, includePrerelease, cancellationToken);
                    if (packageInfo != null)
                        return packageInfo;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to query package info from {source}: {ex.Message}");
                }
            }

            throw new PackageNotFoundException($"Package {packageId}@{version} not found in any source");
        }

        private async Task<PackageInfo> QueryPackageInfoFromSourceAsync(
            string packageId,
            string version,
            string source,
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            try
            {
                var queryUrl = string.IsNullOrEmpty(version)
                    ? $"{source.TrimEnd('/')}/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json"
                    : $"{source.TrimEnd('/')}/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}/{packageId.ToLowerInvariant()}.nuspec";

                var response = await _httpClient.GetAsync(queryUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (string.IsNullOrEmpty(version))
                    {
                        // Parse index.json to get latest version;
                        var indexData = JsonConvert.DeserializeObject<PackageIndex>(content);
                        var versions = indexData?.Versions ?? new List<string>();

                        if (!includePrerelease)
                        {
                            versions = versions.Where(v => !v.Contains("-")).ToList();
                        }

                        var latestVersion = versions.LastOrDefault();
                        if (latestVersion != null)
                        {
                            return await QueryPackageInfoFromSourceAsync(packageId, latestVersion, source, includePrerelease, cancellationToken);
                        }
                    }
                    else;
                    {
                        // Parse nuspec file;
                        return ParseNuspecPackageInfo(packageId, version, source, content);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to query package info from {source}: {ex.Message}");
            }

            return null;
        }

        private PackageInfo ParseNuspecPackageInfo(string packageId, string version, string source, string nuspecContent)
        {
            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(nuspecContent);

                var nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
                nsManager.AddNamespace("ns", "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd");

                var metadataNode = xmlDoc.SelectSingleNode("//ns:metadata", nsManager);
                if (metadataNode == null)
                    return null;

                var dependencies = new List<PackageDependency>();
                var dependenciesNode = metadataNode.SelectSingleNode("ns:dependencies", nsManager);

                if (dependenciesNode != null)
                {
                    foreach (XmlNode groupNode in dependenciesNode.SelectNodes("ns:group", nsManager))
                    {
                        foreach (XmlNode depNode in groupNode.SelectNodes("ns:dependency", nsManager))
                        {
                            var depId = depNode.Attributes?["id"]?.Value;
                            var depVersion = depNode.Attributes?["version"]?.Value;

                            if (!string.IsNullOrWhiteSpace(depId) && !string.IsNullOrWhiteSpace(depVersion))
                            {
                                dependencies.Add(new PackageDependency;
                                {
                                    Id = depId,
                                    Version = depVersion;
                                });
                            }
                        }
                    }

                    // Also check for direct dependencies (without group)
                    foreach (XmlNode depNode in dependenciesNode.SelectNodes("ns:dependency", nsManager))
                    {
                        var depId = depNode.Attributes?["id"]?.Value;
                        var depVersion = depNode.Attributes?["version"]?.Value;

                        if (!string.IsNullOrWhiteSpace(depId) && !string.IsNullOrWhiteSpace(depVersion))
                        {
                            dependencies.Add(new PackageDependency;
                            {
                                Id = depId,
                                Version = depVersion;
                            });
                        }
                    }
                }

                return new PackageInfo;
                {
                    Id = packageId,
                    Version = version,
                    Source = source,
                    Title = metadataNode.SelectSingleNode("ns:title", nsManager)?.InnerText,
                    Description = metadataNode.SelectSingleNode("ns:description", nsManager)?.InnerText,
                    Authors = metadataNode.SelectSingleNode("ns:authors", nsManager)?.InnerText,
                    LicenseUrl = metadataNode.SelectSingleNode("ns:licenseUrl", nsManager)?.InnerText,
                    ProjectUrl = metadataNode.SelectSingleNode("ns:projectUrl", nsManager)?.InnerText,
                    IconUrl = metadataNode.SelectSingleNode("ns:iconUrl", nsManager)?.InnerText,
                    Tags = metadataNode.SelectSingleNode("ns:tags", nsManager)?.InnerText,
                    Dependencies = dependencies,
                    Published = DateTime.UtcNow // Would parse from feed;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to parse nuspec: {ex.Message}", ex);
                return null;
            }
        }

        private async Task<string> GetPackageDownloadUrlAsync(
            string packageId,
            string version,
            string source,
            CancellationToken cancellationToken)
        {
            try
            {
                var packageIdLower = packageId.ToLowerInvariant();
                var versionLower = version.ToLowerInvariant();

                // Construct download URL for NuGet v3 API;
                var downloadUrl = $"{source.TrimEnd('/')}/v3-flatcontainer/{packageIdLower}/{versionLower}/{packageIdLower}.{versionLower}.nupkg";

                // Verify the URL exists;
                var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
                var response = await _httpClient.SendAsync(headRequest, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return downloadUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to get download URL from {source}: {ex.Message}");
            }

            return null;
        }

        private async Task<PackageDownloadResult> DownloadFromSourceAsync(
            string packageId,
            string version,
            string downloadUrl,
            string source,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.Debug($"Downloading from {source}: {packageId}@{version}");

                var tempPath = Path.Combine(Path.GetTempPath(), "neda-nuget", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempPath);

                var fileName = $"{packageId}.{version}.nupkg";
                var filePath = Path.Combine(tempPath, fileName);

                // Download the package;
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fileStream, cancellationToken);

                var fileInfo = new FileInfo(filePath);

                _logger.Debug($"Download completed: {packageId}@{version} ({FormatBytes(fileInfo.Length)})");

                return new PackageDownloadResult;
                {
                    PackageId = packageId,
                    Version = version,
                    Status = DownloadStatus.Success,
                    Source = source,
                    LocalPath = filePath,
                    Size = fileInfo.Length,
                    DownloadTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to download from {source}: {ex.Message}", ex);
                return new PackageDownloadResult;
                {
                    PackageId = packageId,
                    Version = version,
                    Status = DownloadStatus.Failed,
                    Source = source,
                    Error = ex.Message;
                };
            }
        }

        private async Task LoadPackageCacheAsync(CancellationToken cancellationToken)
        {
            try
            {
                var cacheInfo = await _cacheManager.GetCacheInfoAsync(cancellationToken);

                lock (_syncLock)
                {
                    foreach (var package in cacheInfo.Packages)
                    {
                        var cacheKey = $"{package.Id.ToLowerInvariant()}_{package.Version.ToLowerInvariant()}";
                        _packageCache[cacheKey] = package;
                    }
                }

                _logger.Info($"Loaded {cacheInfo.Packages.Count} packages from cache");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load package cache: {ex.Message}");
            }
        }

        private async Task PreloadCommonPackagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var commonPackages = new[]
                {
                    new PackageDependency { Id = "Newtonsoft.Json", Version = "13.0.3" },
                    new PackageDependency { Id = "Microsoft.Extensions.Logging", Version = "7.0.0" },
                    new PackageDependency { Id = "Microsoft.Extensions.DependencyInjection", Version = "7.0.0" },
                    new PackageDependency { Id = "Microsoft.Extensions.Configuration", Version = "7.0.0" },
                    new PackageDependency { Id = "System.Text.Json", Version = "7.0.3" }
                };

                _logger.Info("Preloading common packages...");

                var downloadTasks = commonPackages.Select(p =>
                    DownloadPackageWithRetryAsync(p.Id, p.Version, 2, TimeSpan.FromSeconds(1), cancellationToken));

                await Task.WhenAll(downloadTasks);

                _logger.Info("Common packages preloaded");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to preload common packages: {ex.Message}");
            }
        }

        private async Task<string> GetLatestVersionAsync(string packageId, bool includePrerelease, CancellationToken cancellationToken)
        {
            try
            {
                var packageInfo = await GetPackageInfoAsync(packageId, null, includePrerelease, cancellationToken);
                return packageInfo?.Version;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to get latest version for {packageId}: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetReleaseNotesAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            try
            {
                var packageInfo = await GetPackageInfoAsync(packageId, version, false, cancellationToken);
                return packageInfo?.ReleaseNotes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task<List<PackageInfo>> SearchNuGetApiAsync(
            string searchTerm,
            int take,
            bool includePrerelease,
            CancellationToken cancellationToken)
        {
            var results = new List<PackageInfo>();

            foreach (var source in Configuration.PackageSources)
            {
                try
                {
                    var searchUrl = $"{source.TrimEnd('/')}/v3/search?q={Uri.EscapeDataString(searchTerm)}&take={take}&prerelease={includePrerelease}";
                    var response = await _httpClient.GetAsync(searchUrl, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(cancellationToken);
                        var searchResult = JsonConvert.DeserializeObject<NuGetSearchResult>(content);

                        if (searchResult?.Data != null)
                        {
                            foreach (var item in searchResult.Data)
                            {
                                results.Add(new PackageInfo;
                                {
                                    Id = item.Id,
                                    Version = item.Version,
                                    Description = item.Description,
                                    Authors = item.Authors,
                                    TotalDownloads = item.TotalDownloads,
                                    Source = source;
                                });
                            }
                        }
                    }

                    if (results.Count >= take)
                        break;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to search API {source}: {ex.Message}");
                }
            }

            return results.Take(take).ToList();
        }

        private async Task<List<PackageDependency>> ParsePackageJsonDependenciesAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            var dependencies = new List<PackageDependency>();

            try
            {
                var jsonContent = await File.ReadAllTextAsync(projectPath, cancellationToken);
                var packageJson = JsonConvert.DeserializeObject<PackageJson>(jsonContent);

                if (packageJson?.Dependencies != null)
                {
                    foreach (var kvp in packageJson.Dependencies)
                    {
                        dependencies.Add(new PackageDependency;
                        {
                            Id = kvp.Key,
                            Version = kvp.Value,
                            IsDirectReference = true;
                        });
                    }
                }

                _logger.Debug($"Parsed {dependencies.Count} dependencies from package.json");
                return dependencies;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to parse package.json: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<List<PackageDependency>> ParseFsProjDependenciesAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            // F# projects use the same format as C# projects;
            return await ParseCsProjDependenciesAsync(projectPath, cancellationToken);
        }

        private async Task<List<PackageDependency>> ParseVbProjDependenciesAsync(
            string projectPath,
            CancellationToken cancellationToken)
        {
            // VB.NET projects use the same format as C# projects;
            return await ParseCsProjDependenciesAsync(projectPath, cancellationToken);
        }

        private void CleanupCacheCallback(object state)
        {
            if (!_isInitialized || _isDisposed)
                return;

            try
            {
                _logger.Debug("Running cache cleanup...");

                var cleanupTask = _cacheManager.CleanupOldPackagesAsync(TimeSpan.FromDays(Configuration.CacheRetentionDays), CancellationToken.None);
                cleanupTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Cache cleanup failed: {ex.Message}", ex);
            }
        }

        private void HealthCheckCallback(object state)
        {
            if (!_isInitialized || _isDisposed)
                return;

            try
            {
                var cacheSize = _cacheManager.GetCacheSize();
                var packageCount = _packageCache.Count;
                var activeRestorations = _activeRestorations.Count;

                _logger.Debug($"NuGet Manager health check: Cache={FormatBytes(cacheSize)}, Packages={packageCount}, Active={activeRestorations}");

                // Check cache size;
                if (cacheSize > Configuration.MaxCacheSize * 0.9)
                {
                    _logger.Warn($"Cache size is at {cacheSize * 100 / Configuration.MaxCacheSize}% of limit");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Health check failed: {ex.Message}", ex);
            }
        }

        private void ValidateConfiguration()
        {
            if (Configuration.PackageSources == null || !Configuration.PackageSources.Any())
                throw new ConfigurationException("At least one package source must be configured");

            if (Configuration.MaxCacheSize <= 0)
                throw new ConfigurationException("MaxCacheSize must be greater than 0");

            if (Configuration.MaxParallelDownloads <= 0)
                throw new ConfigurationException("MaxParallelDownloads must be greater than 0");

            if (Configuration.CacheRetentionDays <= 0)
                throw new ConfigurationException("CacheRetentionDays must be greater than 0");

            if (string.IsNullOrWhiteSpace(Configuration.CacheDirectory))
            {
                Configuration.CacheDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NEDA", "NuGetCache");
            }

            if (!Directory.Exists(Configuration.CacheDirectory))
            {
                Directory.CreateDirectory(Configuration.CacheDirectory);
                _logger.Info($"Created cache directory: {Configuration.CacheDirectory}");
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("NuGet Manager not initialized. Call InitializeAsync first.");

            if (Status != ManagerStatus.Running)
                throw new InvalidOperationException($"NuGet Manager is not running. Current status: {Status}");
        }

        private string GenerateRestorationId(string projectPath)
        {
            var fileName = Path.GetFileNameWithoutExtension(projectPath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            return $"{fileName}-{timestamp}-{random}";
        }

        private string FormatBytes(long bytes)
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

        #endregion;

        #region Event Handlers;

        private void OnPackageRestored(PackageRestoredEventArgs e)
        {
            PackageRestored?.Invoke(this, e);
        }

        private void OnCacheUpdated(CacheUpdatedEventArgs e)
        {
            CacheUpdated?.Invoke(this, e);
        }

        private void OnDependencyResolved(DependencyResolvedEventArgs e)
        {
            DependencyResolved?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                Status = ManagerStatus.Stopping;

                _cleanupTimer?.Dispose();
                _healthCheckTimer?.Dispose();
                _restoreSemaphore?.Dispose();
                _httpClient?.Dispose();
                _cacheManager?.Dispose();

                Status = ManagerStatus.Stopped;
                _isDisposed = true;

                _logger.Info("NuGet Manager disposed");
            }
        }

        ~NuGetManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface INuGetManager;
    {
        Task<RestoreResult> RestorePackagesAsync(string projectPath, RestoreOptions options = null, CancellationToken cancellationToken = default);
        Task<PackageInfo> GetPackageInfoAsync(string packageId, string version = null, bool includePrerelease = false, CancellationToken cancellationToken = default);
        Task<PackageDownloadResult> DownloadPackageAsync(string packageId, string version, CancellationToken cancellationToken = default);
        Task<bool> ClearCacheAsync(CancellationToken cancellationToken = default);
        Task<CacheAnalysisResult> AnalyzeCacheAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<PackageInfo>> SearchPackagesAsync(string searchTerm, int take = 20, bool includePrerelease = false, CancellationToken cancellationToken = default);
        Task<PackageUpdateCheckResult> CheckForUpdatesAsync(string projectPath, bool includePrerelease = false, CancellationToken cancellationToken = default);
        Task<PackageResolutionResult> ResolveDependenciesAsync(IEnumerable<PackageDependency> dependencies, CancellationToken cancellationToken = default);
        RestoreStatus GetRestoreStatus(string restorationId);
        IReadOnlyList<PackageInfo> GetCachedPackages();
    }

    public class NuGetConfiguration;
    {
        public List<string> PackageSources { get; set; } = new List<string>
        {
            "https://api.nuget.org/v3/index.json",
            "https://pkgs.dev.azure.com/dotnet/public/_packaging/dotnet-public/nuget/v3/index.json"
        };

        public string CacheDirectory { get; set; }
        public long MaxCacheSize { get; set; } = 10L * 1024 * 1024 * 1024; // 10GB;
        public int CacheRetentionDays { get; set; } = 30;
        public int MaxParallelDownloads { get; set; } = 10;
        public int DownloadRetryCount { get; set; } = 3;
        public TimeSpan DownloadRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
        public bool PreloadCommonPackages { get; set; } = true;
        public bool EnablePackageSignatureValidation { get; set; } = true;
        public bool EnableDependencyResolutionCaching { get; set; } = true;
        public Dictionary<string, string> SourceCredentials { get; set; } = new Dictionary<string, string>();
    }

    public class RestoreOptions;
    {
        public bool ForceRestore { get; set; } = false;
        public bool UseLockFile { get; set; } = true;
        public bool CleanPackages { get; set; } = false;
        public List<string> AdditionalSources { get; set; } = new List<string>();
        public Dictionary<string, string> PackageVersions { get; set; } = new Dictionary<string, string>();
        public string RuntimeIdentifier { get; set; }
        public string Framework { get; set; }
        public bool NoCache { get; set; } = false;
        public bool Verbose { get; set; } = false;
    }

    public class RestoreResult;
    {
        public string RestorationId { get; set; }
        public string ProjectPath { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorDetails { get; set; }
        public int TotalPackages { get; set; }
        public int ResolvedPackages { get; set; }
        public int DownloadedPackages { get; set; }
        public int CacheHits { get; set; }
        public long DownloadedSize { get; set; }
        public long CacheSize { get; set; }
        public LockFile LockFile { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class LockFile;
    {
        public string Version { get; set; }
        public string ProjectPath { get; set; }
        public string Hash { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<LockFilePackage> Packages { get; set; } = new List<LockFilePackage>();
    }

    public class LockFilePackage;
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public bool IsDirectReference { get; set; }
        public List<PackageDependency> Dependencies { get; set; } = new List<PackageDependency>();
    }

    public class PackageInfo;
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Authors { get; set; }
        public string LicenseUrl { get; set; }
        public string ProjectUrl { get; set; }
        public string IconUrl { get; set; }
        public string Tags { get; set; }
        public string ReleaseNotes { get; set; }
        public string Source { get; set; }
        public string DownloadUrl { get; set; }
        public string LocalPath { get; set; }
        public long Size { get; set; }
        public long TotalDownloads { get; set; }
        public DateTime Published { get; set; }
        public DateTime DownloadedAt { get; set; }
        public List<PackageDependency> Dependencies { get; set; } = new List<PackageDependency>();
    }

    public class PackageDependency;
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public bool IsDirectReference { get; set; }
    }

    public class ResolvedPackage;
    {
        public string Id { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public List<PackageDependency> Dependencies { get; set; } = new List<PackageDependency>();
        public bool IsDirectReference { get; set; }
        public bool HasVersionConflict { get; set; }
        public bool HasResolutionError { get; set; }
        public string ResolutionError { get; set; }
        public DateTime ResolutionTime { get; set; }
    }

    public class PackageDownloadResult;
    {
        public string PackageId { get; set; }
        public string Version { get; set; }
        public DownloadStatus Status { get; set; }
        public string Source { get; set; }
        public string LocalPath { get; set; }
        public long Size { get; set; }
        public bool Cached { get; set; }
        public string Error { get; set; }
        public DateTime DownloadTime { get; set; }
    }

    public enum DownloadStatus;
    {
        Pending,
        Downloading,
        Success,
        Failed,
        Cached,
        Skipped;
    }

    public class PackageExtractResult;
    {
        public string PackageId { get; set; }
        public string Version { get; set; }
        public bool Success { get; set; }
        public string ExtractPath { get; set; }
        public string Error { get; set; }
        public DateTime ExtractTime { get; set; }
    }

    public class CacheAnalysisResult;
    {
        public long TotalSize { get; set; }
        public int PackageCount { get; set; }
        public PackageInfo OldestPackage { get; set; }
        public PackageInfo NewestPackage { get; set; }
        public PackageInfo LargestPackage { get; set; }
        public PackageInfo MostUsedPackage { get; set; }
        public DateTime AnalysisTime { get; set; }
    }

    public class PackageUpdateCheckResult;
    {
        public string ProjectPath { get; set; }
        public int TotalPackages { get; set; }
        public int UpdatesAvailable { get; set; }
        public List<PackageUpdate> Updates { get; set; } = new List<PackageUpdate>();
        public DateTime CheckTime { get; set; }
    }

    public class PackageUpdate;
    {
        public string PackageId { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool IsPrerelease { get; set; }
        public string ReleaseNotes { get; set; }
        public string BreakingChanges { get; set; }
    }

    public class PackageResolutionResult;
    {
        public List<PackageDependency> RootDependencies { get; set; }
        public Dictionary<string, ResolvedPackage> ResolvedPackages { get; set; }
        public int TotalPackages { get; set; }
        public bool HasConflicts { get; set; }
        public DateTime ResolutionTime { get; set; }
    }

    public class RestoreStatus;
    {
        public string RestorationId { get; set; }
        public RestoreStatusType Status { get; set; }
        public int Progress { get; set; }
        public int TotalPackages { get; set; }
        public int ResolvedPackages { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public enum RestoreStatusType;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled;
    }

    public enum ManagerStatus;
    {
        Stopped,
        Initializing,
        Running,
        Stopping,
        Error;
    }

    // Event Arguments;
    public class PackageRestoredEventArgs : EventArgs;
    {
        public string RestorationId { get; set; }
        public string ProjectPath { get; set; }
        public RestoreResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CacheUpdatedEventArgs : EventArgs;
    {
        public string PackageId { get; set; }
        public string Version { get; set; }
        public CacheAction Action { get; set; }
        public long Size { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum CacheAction;
    {
        Added,
        Removed,
        Updated,
        Cleaned;
    }

    public class DependencyResolvedEventArgs : EventArgs;
    {
        public string PackageId { get; set; }
        public string Version { get; set; }
        public string Source { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // JSON Models;
    public class PackageIndex;
    {
        [JsonProperty("versions")]
        public List<string> Versions { get; set; } = new List<string>();
    }

    public class NuGetSearchResult;
    {
        [JsonProperty("data")]
        public List<NuGetSearchItem> Data { get; set; } = new List<NuGetSearchItem>();
    }

    public class NuGetSearchItem;
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("authors")]
        public string Authors { get; set; }

        [JsonProperty("totalDownloads")]
        public long TotalDownloads { get; set; }
    }

    public class PackageJson;
    {
        [JsonProperty("dependencies")]
        public Dictionary<string, string> Dependencies { get; set; } = new Dictionary<string, string>();
    }

    // Internal Classes;
    internal class PackageRestoration;
    {
        public string RestorationId { get; }
        public string ProjectPath { get; }
        public RestoreOptions Options { get; }
        public RestoreStatusType Status { get; set; }
        public int Progress { get; set; }
        public int TotalPackages { get; set; }
        public int ResolvedPackages { get; set; }
        public DateTime StartTime { get; }
        public DateTime? CompletionTime { get; set; }

        public TimeSpan Duration => (CompletionTime ?? DateTime.UtcNow) - StartTime;

        public PackageRestoration(string restorationId, string projectPath, RestoreOptions options)
        {
            RestorationId = restorationId;
            ProjectPath = projectPath;
            Options = options ?? new RestoreOptions();
            Status = RestoreStatusType.Pending;
            StartTime = DateTime.UtcNow;
        }
    }

    // Exceptions;
    public class NuGetManagerInitializationException : Exception
    {
        public NuGetManagerInitializationException(string message) : base(message) { }
        public NuGetManagerInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class PackageNotFoundException : Exception
    {
        public PackageNotFoundException(string message) : base(message) { }
        public PackageNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class PackageDownloadException : Exception
    {
        public PackageDownloadException(string message) : base(message) { }
        public PackageDownloadException(string message, Exception inner) : base(message, inner) { }
    }

    public class DependencyResolutionException : Exception
    {
        public DependencyResolutionException(string message) : base(message) { }
        public DependencyResolutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationException : Exception
    {
        public ConfigurationException(string message) : base(message) { }
    }

    public class RestorationNotFoundException : Exception
    {
        public RestorationNotFoundException(string message) : base(message) { }
    }

    #endregion;
}
