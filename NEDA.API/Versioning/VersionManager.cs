using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
using System.Threading;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.API.Versioning;

namespace NEDA.API.Versioning;
{
    /// <summary>
    /// Manages API versions, provides version discovery, routing, and lifecycle management;
    /// </summary>
    public class VersionManager : IVersionManager, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly VersionConfiguration _config;
        private readonly VersionStorage _storage;
        private readonly VersionRouter _router;
        private readonly VersionValidator _validator;
        private readonly VersionNotifier _notifier;

        private readonly ConcurrentDictionary<string, APIVersion> _activeVersions;
        private readonly ConcurrentDictionary<string, VersionPolicy> _versionPolicies;
        private readonly SemaphoreSlim _syncSemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private bool _disposed = false;
        private Task _monitoringTask;

        /// <summary>
        /// Current stable version;
        /// </summary>
        public APIVersion CurrentStableVersion { get; private set; }

        /// <summary>
        /// Latest development version;
        /// </summary>
        public APIVersion LatestDevelopmentVersion { get; private set; }

        /// <summary>
        /// All registered versions;
        /// </summary>
        public IReadOnlyDictionary<string, APIVersion> AllVersions => _activeVersions;

        /// <summary>
        /// Default version to use when none specified;
        /// </summary>
        public APIVersion DefaultVersion { get; private set; }

        /// <summary>
        /// Initialize a new VersionManager;
        /// </summary>
        public VersionManager(ILogger logger, VersionConfiguration config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new VersionConfiguration();

            _activeVersions = new ConcurrentDictionary<string, APIVersion>();
            _versionPolicies = new ConcurrentDictionary<string, VersionPolicy>();

            _storage = new VersionStorage(_logger, _config);
            _router = new VersionRouter(_logger);
            _validator = new VersionValidator(_logger);
            _notifier = new VersionNotifier(_logger);

            InitializeAsync().GetAwaiter().GetResult();
            StartMonitoring();
        }

        /// <summary>
        /// Initialize version manager with existing versions;
        /// </summary>
        private async Task InitializeAsync()
        {
            try
            {
                await _syncSemaphore.WaitAsync();

                // Load existing versions from storage;
                var versions = await _storage.LoadAllVersionsAsync();
                foreach (var version in versions)
                {
                    if (await _validator.ValidateVersionAsync(version))
                    {
                        _activeVersions[version.ToString()] = version;

                        // Update special versions;
                        UpdateSpecialVersions(version);
                    }
                    else;
                    {
                        _logger.Warning($"Skipping invalid version during initialization: {version}");
                    }
                }

                // Set default version;
                SetDefaultVersion();

                // Load version policies;
                await LoadVersionPoliciesAsync();

                _logger.Information($"VersionManager initialized with {_activeVersions.Count} versions");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize VersionManager: {ex.Message}", ex);
                throw new VersionManagerException("Failed to initialize VersionManager", ex);
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        /// <summary>
        /// Register a new API version;
        /// </summary>
        public async Task<APIVersion> RegisterVersionAsync(APIVersion version, VersionRegistrationOptions options = null)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));

            try
            {
                await _syncSemaphore.WaitAsync(_cts.Token);

                // Validate version;
                var validationResult = await _validator.ValidateNewVersionAsync(version, _activeVersions.Values);
                if (!validationResult.IsValid)
                {
                    throw new VersionValidationException($"Version validation failed: {validationResult.ErrorMessage}");
                }

                // Apply registration options;
                options ??= new VersionRegistrationOptions();
                ApplyRegistrationOptions(version, options);

                // Check for duplicate;
                var versionKey = version.ToString();
                if (_activeVersions.ContainsKey(versionKey))
                {
                    throw new DuplicateVersionException($"Version {versionKey} already exists");
                }

                // Store version;
                await _storage.SaveVersionAsync(version);

                // Add to active versions;
                _activeVersions[versionKey] = version;

                // Update special versions;
                UpdateSpecialVersions(version);

                // Create version policy;
                var policy = CreateDefaultPolicy(version, options);
                _versionPolicies[versionKey] = policy;
                await _storage.SavePolicyAsync(policy);

                // Notify subscribers;
                await _notifier.NotifyVersionRegisteredAsync(version, options);

                _logger.Information($"Registered new API version: {versionKey} with status {version.Status}");

                return version;
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("Version registration cancelled");
                throw;
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        /// <summary>
        /// Get a specific version by string;
        /// </summary>
        public async Task<APIVersion> GetVersionAsync(string versionString, bool includeDeprecated = false)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                throw new ArgumentException("Version string cannot be null or empty", nameof(versionString));

            // Try exact match first;
            if (_activeVersions.TryGetValue(versionString, out var exactVersion))
            {
                if (!includeDeprecated && exactVersion.IsDeprecated)
                    throw new VersionDeprecatedException($"Version {versionString} is deprecated");

                return exactVersion;
            }

            // Try to parse and find closest match;
            try
            {
                var requestedVersion = APIVersion.Parse(versionString);

                // Find compatible version;
                var compatibleVersion = await FindCompatibleVersionAsync(requestedVersion, includeDeprecated);
                if (compatibleVersion != null)
                {
                    return compatibleVersion;
                }

                throw new VersionNotFoundException($"Version {versionString} not found");
            }
            catch (FormatException ex)
            {
                throw new VersionFormatException($"Invalid version format: {versionString}", ex);
            }
        }

        /// <summary>
        /// Get version for specific route;
        /// </summary>
        public async Task<APIVersion> GetVersionForRouteAsync(string route, string requestedVersion = null)
        {
            if (string.IsNullOrWhiteSpace(route))
                throw new ArgumentException("Route cannot be null or empty", nameof(route));

            return await _router.GetVersionForRouteAsync(route, requestedVersion, _activeVersions.Values, DefaultVersion);
        }

        /// <summary>
        /// Promote a version to new status;
        /// </summary>
        public async Task PromoteVersionAsync(string versionString, APIVersionStatus newStatus, PromotionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                throw new ArgumentException("Version string cannot be null or empty", nameof(versionString));

            try
            {
                await _syncSemaphore.WaitAsync(_cts.Token);

                if (!_activeVersions.TryGetValue(versionString, out var version))
                {
                    throw new VersionNotFoundException($"Version {versionString} not found");
                }

                // Validate promotion;
                var canPromote = await _validator.CanPromoteVersionAsync(version, newStatus);
                if (!canPromote)
                {
                    throw new InvalidOperationException($"Cannot promote version {versionString} from {version.Status} to {newStatus}");
                }

                // Apply promotion;
                version.Promote(newStatus);

                // Apply promotion options;
                options ??= new PromotionOptions();
                if (options.SetReleaseDate)
                {
                    // If promoting to stable, set release date;
                    if (newStatus == APIVersionStatus.Stable)
                    {
                        // Implementation for stable release;
                    }
                }

                // Update storage;
                await _storage.SaveVersionAsync(version);

                // Update special versions;
                UpdateSpecialVersions(version);

                // Update policy if needed;
                if (_versionPolicies.TryGetValue(versionString, out var policy))
                {
                    policy.Status = newStatus;
                    policy.LastModified = DateTime.UtcNow;
                    await _storage.SavePolicyAsync(policy);
                }

                // Notify subscribers;
                await _notifier.NotifyVersionPromotedAsync(version, newStatus, options);

                _logger.Information($"Promoted version {versionString} from {version.Status} to {newStatus}");
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        /// <summary>
        /// Deprecate a version;
        /// </summary>
        public async Task DeprecateVersionAsync(string versionString, DateTime? endOfLifeDate = null, string reason = null)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                throw new ArgumentException("Version string cannot be null or empty", nameof(versionString));

            try
            {
                await _syncSemaphore.WaitAsync(_cts.Token);

                if (!_activeVersions.TryGetValue(versionString, out var version))
                {
                    throw new VersionNotFoundException($"Version {versionString} not found");
                }

                // Set end of life date;
                var eolDate = endOfLifeDate ?? DateTime.UtcNow.AddMonths(_config.DefaultDeprecationPeriodMonths);
                version.SetEndOfLife(eolDate);

                // Update storage;
                await _storage.SaveVersionAsync(version);

                // Update policy;
                if (_versionPolicies.TryGetValue(versionString, out var policy))
                {
                    policy.IsDeprecated = true;
                    policy.DeprecationDate = DateTime.UtcNow;
                    policy.EndOfLifeDate = eolDate;
                    policy.DeprecationReason = reason;
                    policy.LastModified = DateTime.UtcNow;
                    await _storage.SavePolicyAsync(policy);
                }

                // Notify subscribers;
                await _notifier.NotifyVersionDeprecatedAsync(version, eolDate, reason);

                _logger.Information($"Deprecated version {versionString}. End of life: {eolDate:yyyy-MM-dd}");
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        /// <summary>
        /// Get version compatibility information;
        /// </summary>
        public async Task<CompatibilityReport> GetCompatibilityReportAsync(string sourceVersion, string targetVersion)
        {
            if (string.IsNullOrWhiteSpace(sourceVersion))
                throw new ArgumentException("Source version cannot be null or empty", nameof(sourceVersion));

            if (string.IsNullOrWhiteSpace(targetVersion))
                throw new ArgumentException("Target version cannot be null or empty", nameof(targetVersion));

            try
            {
                var source = await GetVersionAsync(sourceVersion, true);
                var target = await GetVersionAsync(targetVersion, true);

                var compatibility = source.GetCompatibilityWith(target);

                var report = new CompatibilityReport;
                {
                    SourceVersion = source.ToString(),
                    TargetVersion = target.ToString(),
                    CompatibilityType = compatibility,
                    AnalysisDate = DateTime.UtcNow,
                    BreakingChanges = await FindBreakingChangesAsync(source, target),
                    MigrationSteps = await GenerateMigrationStepsAsync(source, target),
                    Recommendations = await GenerateRecommendationsAsync(source, target, compatibility)
                };

                return report;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate compatibility report: {ex.Message}", ex);
                throw new CompatibilityAnalysisException("Failed to generate compatibility report", ex);
            }
        }

        /// <summary>
        /// Get all available versions with filtering;
        /// </summary>
        public async Task<IEnumerable<APIVersion>> GetAvailableVersionsAsync(VersionFilter filter = null)
        {
            filter ??= new VersionFilter();

            var versions = _activeVersions.Values.AsEnumerable();

            // Apply filters;
            if (filter.IncludeDeprecated.HasValue && !filter.IncludeDeprecated.Value)
            {
                versions = versions.Where(v => !v.IsDeprecated);
            }

            if (filter.MinStatus.HasValue)
            {
                versions = versions.Where(v => v.Status >= filter.MinStatus.Value);
            }

            if (filter.MaxStatus.HasValue)
            {
                versions = versions.Where(v => v.Status <= filter.MaxStatus.Value);
            }

            if (filter.ReleasedAfter.HasValue)
            {
                versions = versions.Where(v => v.ReleaseDate >= filter.ReleasedAfter.Value);
            }

            if (filter.ReleasedBefore.HasValue)
            {
                versions = versions.Where(v => v.ReleaseDate <= filter.ReleasedBefore.Value);
            }

            // Apply sorting;
            versions = filter.SortOrder switch;
            {
                VersionSortOrder.VersionDescending => versions.OrderByDescending(v => v.Major)
                                                              .ThenByDescending(v => v.Minor)
                                                              .ThenByDescending(v => v.Patch),
                VersionSortOrder.ReleaseDateAscending => versions.OrderBy(v => v.ReleaseDate),
                VersionSortOrder.ReleaseDateDescending => versions.OrderByDescending(v => v.ReleaseDate),
                VersionSortOrder.Status => versions.OrderBy(v => v.Status),
                _ => versions.OrderByDescending(v => v.Major)
                             .ThenByDescending(v => v.Minor)
                             .ThenByDescending(v => v.Patch) // Default: version descending;
            };

            // Apply pagination;
            if (filter.Skip.HasValue)
            {
                versions = versions.Skip(filter.Skip.Value);
            }

            if (filter.Take.HasValue)
            {
                versions = versions.Take(filter.Take.Value);
            }

            return await Task.FromResult(versions.ToList());
        }

        /// <summary>
        /// Get version usage statistics;
        /// </summary>
        public async Task<VersionStatistics> GetVersionStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            startDate ??= DateTime.UtcNow.AddMonths(-1);
            endDate ??= DateTime.UtcNow;

            try
            {
                var stats = new VersionStatistics;
                {
                    PeriodStart = startDate.Value,
                    PeriodEnd = endDate.Value,
                    TotalRequests = 0,
                    VersionUsage = new Dictionary<string, long>(),
                    ErrorRates = new Dictionary<string, double>(),
                    EndpointUsage = new Dictionary<string, long>()
                };

                // Load usage data from storage;
                var usageData = await _storage.LoadUsageDataAsync(startDate.Value, endDate.Value);

                foreach (var usage in usageData)
                {
                    stats.TotalRequests += usage.RequestCount;

                    if (!stats.VersionUsage.ContainsKey(usage.Version))
                    {
                        stats.VersionUsage[usage.Version] = 0;
                    }
                    stats.VersionUsage[usage.Version] += usage.RequestCount;

                    foreach (var endpoint in usage.EndpointUsage)
                    {
                        var endpointKey = $"{usage.Version}:{endpoint.Key}";
                        stats.EndpointUsage[endpointKey] = endpoint.Value;
                    }

                    if (usage.ErrorCount > 0)
                    {
                        var errorRate = (double)usage.ErrorCount / usage.RequestCount * 100;
                        stats.ErrorRates[usage.Version] = errorRate;
                    }
                }

                // Calculate percentages;
                foreach (var version in stats.VersionUsage.Keys.ToList())
                {
                    stats.VersionPercentages[version] = stats.TotalRequests > 0;
                        ? (double)stats.VersionUsage[version] / stats.TotalRequests * 100;
                        : 0;
                }

                return stats;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get version statistics: {ex.Message}", ex);
                throw new StatisticsException("Failed to get version statistics", ex);
            }
        }

        /// <summary>
        /// Clean up old versions based on policy;
        /// </summary>
        public async Task CleanupOldVersionsAsync(CleanupOptions options = null)
        {
            options ??= new CleanupOptions();

            try
            {
                await _syncSemaphore.WaitAsync(_cts.Token);

                var versionsToRemove = new List<string>();

                foreach (var kvp in _activeVersions)
                {
                    var version = kvp.Value;

                    // Check if version should be cleaned up;
                    if (ShouldCleanupVersion(version, options))
                    {
                        versionsToRemove.Add(kvp.Key);

                        // Archive version if requested;
                        if (options.ArchiveBeforeDelete)
                        {
                            await _storage.ArchiveVersionAsync(version);
                        }

                        // Remove from storage;
                        await _storage.DeleteVersionAsync(version.ToString());

                        // Remove policy;
                        _versionPolicies.TryRemove(kvp.Key, out _);

                        _logger.Information($"Cleaned up version: {kvp.Key}");
                    }
                }

                // Remove from active versions;
                foreach (var versionKey in versionsToRemove)
                {
                    _activeVersions.TryRemove(versionKey, out _);
                }

                // Update special versions;
                UpdateSpecialVersions();

                if (versionsToRemove.Count > 0)
                {
                    await _notifier.NotifyVersionsCleanedUpAsync(versionsToRemove, options);
                }
            }
            finally
            {
                _syncSemaphore.Release();
            }
        }

        /// <summary>
        /// Start background monitoring task;
        /// </summary>
        private void StartMonitoring()
        {
            _monitoringTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(_config.MonitoringIntervalMinutes), _cts.Token);
                        await PerformMonitoringAsync();
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected on shutdown;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Version monitoring error: {ex.Message}", ex);
                    }
                }
            }, _cts.Token);
        }

        /// <summary>
        /// Perform monitoring tasks;
        /// </summary>
        private async Task PerformMonitoringAsync()
        {
            // Check for deprecated versions nearing end of life;
            var nearingEol = _activeVersions.Values;
                .Where(v => v.IsDeprecated && v.EndOfLifeDate.HasValue)
                .Where(v => (v.EndOfLifeDate.Value - DateTime.UtcNow).TotalDays <= _config.EolWarningDays)
                .ToList();

            foreach (var version in nearingEol)
            {
                await _notifier.NotifyEolWarningAsync(version, _config.EolWarningDays);
            }

            // Check for version health;
            await CheckVersionHealthAsync();

            // Perform auto-cleanup if enabled;
            if (_config.AutoCleanupEnabled)
            {
                await CleanupOldVersionsAsync(new CleanupOptions;
                {
                    RemoveDeprecatedOlderThanDays = _config.AutoCleanupDays,
                    ArchiveBeforeDelete = true;
                });
            }
        }

        /// <summary>
        /// Update special version references;
        /// </summary>
        private void UpdateSpecialVersions(APIVersion updatedVersion = null)
        {
            if (updatedVersion != null)
            {
                // Update current stable if this version is now stable;
                if (updatedVersion.IsStable &&
                    (CurrentStableVersion == null || updatedVersion.CompareTo(CurrentStableVersion) > 0))
                {
                    CurrentStableVersion = updatedVersion;
                }

                // Update latest development;
                if (updatedVersion.Status == APIVersionStatus.Development ||
                    updatedVersion.Status == APIVersionStatus.Alpha ||
                    updatedVersion.Status == APIVersionStatus.Beta)
                {
                    if (LatestDevelopmentVersion == null || updatedVersion.CompareTo(LatestDevelopmentVersion) > 0)
                    {
                        LatestDevelopmentVersion = updatedVersion;
                    }
                }
            }
            else;
            {
                // Recalculate all special versions;
                CurrentStableVersion = _activeVersions.Values;
                    .Where(v => v.IsStable)
                    .OrderByDescending(v => v.Major)
                    .ThenByDescending(v => v.Minor)
                    .ThenByDescending(v => v.Patch)
                    .FirstOrDefault();

                LatestDevelopmentVersion = _activeVersions.Values;
                    .Where(v => v.Status == APIVersionStatus.Development ||
                               v.Status == APIVersionStatus.Alpha ||
                               v.Status == APIVersionStatus.Beta)
                    .OrderByDescending(v => v.Major)
                    .ThenByDescending(v => v.Minor)
                    .ThenByDescending(v => v.Patch)
                    .FirstOrDefault();
            }

            SetDefaultVersion();
        }

        /// <summary>
        /// Set default version based on configuration;
        /// </summary>
        private void SetDefaultVersion()
        {
            DefaultVersion = _config.DefaultVersionStrategy switch;
            {
                DefaultVersionStrategy.LatestStable => CurrentStableVersion,
                DefaultVersionStrategy.LatestDevelopment => LatestDevelopmentVersion,
                DefaultVersionStrategy.SpecificVersion => _activeVersions.Values;
                    .FirstOrDefault(v => v.ToString() == _config.SpecificDefaultVersion),
                _ => CurrentStableVersion ?? LatestDevelopmentVersion ?? _activeVersions.Values;
                    .OrderByDescending(v => v.Major)
                    .ThenByDescending(v => v.Minor)
                    .ThenByDescending(v => v.Patch)
                    .FirstOrDefault()
            };
        }

        /// <summary>
        /// Find compatible version for requested version;
        /// </summary>
        private async Task<APIVersion> FindCompatibleVersionAsync(APIVersion requestedVersion, bool includeDeprecated)
        {
            var compatibleVersions = _activeVersions.Values;
                .Where(v => includeDeprecated || !v.IsDeprecated)
                .Where(v => v.GetCompatibilityWith(requestedVersion) == CompatibilityType.FullyCompatible ||
                           v.GetCompatibilityWith(requestedVersion) == CompatibilityType.PartiallyCompatible)
                .OrderByDescending(v => v.Major)
                .ThenByDescending(v => v.Minor)
                .ThenByDescending(v => v.Patch)
                .ToList();

            if (compatibleVersions.Any())
            {
                return compatibleVersions.First();
            }

            return null;
        }

        /// <summary>
        /// Find breaking changes between versions;
        /// </summary>
        private async Task<List<BreakingChange>> FindBreakingChangesAsync(APIVersion source, APIVersion target)
        {
            var breakingChanges = new List<BreakingChange>();

            // Compare endpoints;
            foreach (var sourceEndpoint in source.SupportedEndpoints)
            {
                if (!target.SupportedEndpoints.ContainsKey(sourceEndpoint.Key))
                {
                    breakingChanges.Add(new BreakingChange;
                    {
                        Type = BreakingChangeType.EndpointRemoved,
                        Description = $"Endpoint {sourceEndpoint.Key} was removed",
                        Severity = BreakingChangeSeverity.High,
                        AffectedComponent = sourceEndpoint.Key,
                        SourceVersion = source.ToString(),
                        TargetVersion = target.ToString()
                    });
                }
            }

            // Compare endpoint changes;
            foreach (var targetEndpoint in target.SupportedEndpoints)
            {
                if (source.SupportedEndpoints.TryGetValue(targetEndpoint.Key, out var sourceEndpoint))
                {
                    // Check for breaking changes in endpoint;
                    if (sourceEndpoint.RequiresAuthentication != targetEndpoint.Value.RequiresAuthentication)
                    {
                        breakingChanges.Add(new BreakingChange;
                        {
                            Type = BreakingChangeType.AuthenticationChanged,
                            Description = $"Authentication requirement changed for {targetEndpoint.Key}",
                            Severity = BreakingChangeSeverity.Medium,
                            AffectedComponent = targetEndpoint.Key;
                        });
                    }

                    // Check rate limit changes;
                    if ((sourceEndpoint.RateLimit == null && targetEndpoint.Value.RateLimit != null) ||
                        (sourceEndpoint.RateLimit != null && targetEndpoint.Value.RateLimit == null) ||
                        (sourceEndpoint.RateLimit != null && targetEndpoint.Value.RateLimit != null &&
                         (sourceEndpoint.RateLimit.RequestsPerMinute != targetEndpoint.Value.RateLimit.RequestsPerMinute ||
                          sourceEndpoint.RateLimit.RequestsPerHour != targetEndpoint.Value.RateLimit.RequestsPerHour)))
                    {
                        breakingChanges.Add(new BreakingChange;
                        {
                            Type = BreakingChangeType.RateLimitChanged,
                            Description = $"Rate limit changed for {targetEndpoint.Key}",
                            Severity = BreakingChangeSeverity.Low,
                            AffectedComponent = targetEndpoint.Key;
                        });
                    }
                }
            }

            return await Task.FromResult(breakingChanges);
        }

        /// <summary>
        /// Generate migration steps;
        /// </summary>
        private async Task<List<string>> GenerateMigrationStepsAsync(APIVersion source, APIVersion target)
        {
            var steps = new List<string>();

            // Add generic migration steps;
            steps.Add($"Update API client to target version {target}");
            steps.Add("Review breaking changes report");
            steps.Add("Test all affected endpoints");

            // Add version-specific steps based on compatibility;
            var compatibility = source.GetCompatibilityWith(target);
            if (compatibility == CompatibilityType.PartiallyCompatible)
            {
                steps.Add("Some endpoints may require code changes - review endpoint compatibility");
            }
            else if (compatibility == CompatibilityType.Incompatible)
            {
                steps.Add("Major rewrite required - consider phased migration approach");
            }

            return await Task.FromResult(steps);
        }

        /// <summary>
        /// Generate recommendations;
        /// </summary>
        private async Task<List<string>> GenerateRecommendationsAsync(APIVersion source, APIVersion target, CompatibilityType compatibility)
        {
            var recommendations = new List<string>();

            if (compatibility == CompatibilityType.FullyCompatible)
            {
                recommendations.Add("Direct upgrade is safe and recommended");
                recommendations.Add("Update at your convenience");
            }
            else if (compatibility == CompatibilityType.PartiallyCompatible)
            {
                recommendations.Add("Plan for testing and minor code changes");
                recommendations.Add("Consider updating dependencies if needed");
                recommendations.Add("Schedule upgrade during maintenance window");
            }
            else if (compatibility == CompatibilityType.Incompatible)
            {
                recommendations.Add("Major upgrade required - allocate significant time");
                recommendations.Add("Consider hiring migration specialists");
                recommendations.Add("Create comprehensive test plan");
                recommendations.Add("Consider multi-phase migration strategy");
            }

            // Add version-specific recommendations;
            if (target.IsDeprecated)
            {
                recommendations.Add($"WARNING: Target version {target} is deprecated");
                recommendations.Add($"Consider migrating to {CurrentStableVersion} instead");
            }

            return await Task.FromResult(recommendations);
        }

        /// <summary>
        /// Check if version should be cleaned up;
        /// </summary>
        private bool ShouldCleanupVersion(APIVersion version, CleanupOptions options)
        {
            if (!options.RemoveDeprecated && version.IsDeprecated)
                return false;

            if (version.EndOfLifeDate.HasValue &&
                (DateTime.UtcNow - version.EndOfLifeDate.Value).TotalDays > options.RemoveDeprecatedOlderThanDays)
                return true;

            if (options.RemoveUnstable &&
                (version.Status == APIVersionStatus.Development ||
                 version.Status == APIVersionStatus.Alpha ||
                 version.Status == APIVersionStatus.Beta))
                return true;

            return false;
        }

        /// <summary>
        /// Check version health;
        /// </summary>
        private async Task CheckVersionHealthAsync()
        {
            foreach (var version in _activeVersions.Values)
            {
                try
                {
                    // Check if version is still valid;
                    var isValid = await _validator.ValidateVersionAsync(version);
                    if (!isValid)
                    {
                        _logger.Warning($"Version {version} failed health check");
                        // Mark for review or auto-deprecate based on config;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Health check failed for version {version}: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Load version policies;
        /// </summary>
        private async Task LoadVersionPoliciesAsync()
        {
            try
            {
                var policies = await _storage.LoadAllPoliciesAsync();
                foreach (var policy in policies)
                {
                    _versionPolicies[policy.Version] = policy;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load version policies: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create default policy for version;
        /// </summary>
        private VersionPolicy CreateDefaultPolicy(APIVersion version, VersionRegistrationOptions options)
        {
            return new VersionPolicy;
            {
                Version = version.ToString(),
                Status = version.Status,
                CreatedDate = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                IsDeprecated = false,
                AutoDeprecateAfterDays = options.AutoDeprecateAfterDays ?? _config.DefaultAutoDeprecateDays,
                AllowDirectAccess = options.AllowDirectAccess,
                RequiresMigrationPath = options.RequiresMigrationPath,
                NotifyOnDeprecation = options.NotifyOnDeprecation,
                SupportedUntil = DateTime.UtcNow.AddDays(options.AutoDeprecateAfterDays ?? _config.DefaultAutoDeprecateDays)
            };
        }

        /// <summary>
        /// Apply registration options to version;
        /// </summary>
        private void ApplyRegistrationOptions(APIVersion version, VersionRegistrationOptions options)
        {
            if (options.SetReleaseDate)
            {
                // Version release date logic;
            }

            if (options.InitialStatus.HasValue)
            {
                version.Promote(options.InitialStatus.Value);
            }

            if (options.EndOfLifeDate.HasValue)
            {
                version.SetEndOfLife(options.EndOfLifeDate.Value);
            }
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
                    _monitoringTask?.Wait(TimeSpan.FromSeconds(5));
                    _monitoringTask?.Dispose();
                    _cts.Dispose();
                    _syncSemaphore.Dispose();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~VersionManager()
        {
            Dispose(false);
        }
    }

    // Supporting classes and interfaces;

    public interface IVersionManager;
    {
        APIVersion CurrentStableVersion { get; }
        APIVersion LatestDevelopmentVersion { get; }
        IReadOnlyDictionary<string, APIVersion> AllVersions { get; }
        APIVersion DefaultVersion { get; }

        Task<APIVersion> RegisterVersionAsync(APIVersion version, VersionRegistrationOptions options = null);
        Task<APIVersion> GetVersionAsync(string versionString, bool includeDeprecated = false);
        Task<APIVersion> GetVersionForRouteAsync(string route, string requestedVersion = null);
        Task PromoteVersionAsync(string versionString, APIVersionStatus newStatus, PromotionOptions options = null);
        Task DeprecateVersionAsync(string versionString, DateTime? endOfLifeDate = null, string reason = null);
        Task<CompatibilityReport> GetCompatibilityReportAsync(string sourceVersion, string targetVersion);
        Task<IEnumerable<APIVersion>> GetAvailableVersionsAsync(VersionFilter filter = null);
        Task<VersionStatistics> GetVersionStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);
        Task CleanupOldVersionsAsync(CleanupOptions options = null);
    }

    public class VersionConfiguration;
    {
        public string StoragePath { get; set; } = "versions";
        public int DefaultDeprecationPeriodMonths { get; set; } = 6;
        public int DefaultAutoDeprecateDays { get; set; } = 365;
        public int MonitoringIntervalMinutes { get; set; } = 5;
        public int EolWarningDays { get; set; } = 30;
        public bool AutoCleanupEnabled { get; set; } = true;
        public int AutoCleanupDays { get; set; } = 90;
        public DefaultVersionStrategy DefaultVersionStrategy { get; set; } = DefaultVersionStrategy.LatestStable;
        public string SpecificDefaultVersion { get; set; }
        public int MaxVersionsToKeep { get; set; } = 10;
        public bool EnableVersionRouting { get; set; } = true;
        public bool EnableCompatibilityChecking { get; set; } = true;
    }

    public enum DefaultVersionStrategy;
    {
        LatestStable,
        LatestDevelopment,
        SpecificVersion,
        Custom;
    }

    public class VersionRegistrationOptions;
    {
        public bool AllowDirectAccess { get; set; } = true;
        public bool RequiresMigrationPath { get; set; } = false;
        public bool NotifyOnDeprecation { get; set; } = true;
        public bool SetReleaseDate { get; set; } = true;
        public APIVersionStatus? InitialStatus { get; set; }
        public DateTime? EndOfLifeDate { get; set; }
        public int? AutoDeprecateAfterDays { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public class PromotionOptions;
    {
        public bool SetReleaseDate { get; set; } = true;
        public bool NotifyUsers { get; set; } = true;
        public bool CreateBackup { get; set; } = true;
        public Dictionary<string, string> PromotionMetadata { get; set; } = new Dictionary<string, string>();
    }

    public class VersionFilter;
    {
        public bool? IncludeDeprecated { get; set; }
        public APIVersionStatus? MinStatus { get; set; }
        public APIVersionStatus? MaxStatus { get; set; }
        public DateTime? ReleasedAfter { get; set; }
        public DateTime? ReleasedBefore { get; set; }
        public VersionSortOrder SortOrder { get; set; } = VersionSortOrder.VersionDescending;
        public int? Skip { get; set; }
        public int? Take { get; set; }
    }

    public enum VersionSortOrder;
    {
        VersionDescending,
        VersionAscending,
        ReleaseDateDescending,
        ReleaseDateAscending,
        Status;
    }

    public class CleanupOptions;
    {
        public bool RemoveDeprecated { get; set; } = true;
        public int RemoveDeprecatedOlderThanDays { get; set; } = 90;
        public bool RemoveUnstable { get; set; } = false;
        public bool ArchiveBeforeDelete { get; set; } = true;
        public bool ForceDelete { get; set; } = false;
    }

    public class CompatibilityReport;
    {
        public string SourceVersion { get; set; }
        public string TargetVersion { get; set; }
        public CompatibilityType CompatibilityType { get; set; }
        public DateTime AnalysisDate { get; set; }
        public List<BreakingChange> BreakingChanges { get; set; } = new List<BreakingChange>();
        public List<string> MigrationSteps { get; set; } = new List<string>();
        public List<string> Recommendations { get; set; } = new List<string>();
        public double EstimatedMigrationEffortHours { get; set; }
        public RiskLevel MigrationRisk { get; set; }
    }

    public class BreakingChange;
    {
        public BreakingChangeType Type { get; set; }
        public string Description { get; set; }
        public BreakingChangeSeverity Severity { get; set; }
        public string AffectedComponent { get; set; }
        public string SourceVersion { get; set; }
        public string TargetVersion { get; set; }
        public string Mitigation { get; set; }
    }

    public enum BreakingChangeType;
    {
        EndpointRemoved,
        ParameterChanged,
        ResponseChanged,
        AuthenticationChanged,
        RateLimitChanged,
        Deprecation,
        Other;
    }

    public enum BreakingChangeSeverity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum RiskLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public class VersionStatistics;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public long TotalRequests { get; set; }
        public Dictionary<string, long> VersionUsage { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, double> VersionPercentages { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> ErrorRates { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, long> EndpointUsage { get; set; } = new Dictionary<string, long>();
        public TimeSpan AverageResponseTime { get; set; }
        public Dictionary<string, TimeSpan> VersionResponseTimes { get; set; } = new Dictionary<string, TimeSpan>();
    }

    public class VersionPolicy;
    {
        public string Version { get; set; }
        public APIVersionStatus Status { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsDeprecated { get; set; }
        public DateTime? DeprecationDate { get; set; }
        public DateTime? EndOfLifeDate { get; set; }
        public string DeprecationReason { get; set; }
        public int AutoDeprecateAfterDays { get; set; }
        public bool AllowDirectAccess { get; set; }
        public bool RequiresMigrationPath { get; set; }
        public bool NotifyOnDeprecation { get; set; }
        public DateTime SupportedUntil { get; set; }
        public Dictionary<string, string> AccessRules { get; set; } = new Dictionary<string, string>();
    }

    // Internal helper classes;

    internal class VersionStorage;
    {
        private readonly ILogger _logger;
        private readonly VersionConfiguration _config;

        public VersionStorage(ILogger logger, VersionConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task SaveVersionAsync(APIVersion version)
        {
            // Implementation for saving version to storage;
            await Task.Delay(10); // Simulate async operation;
        }

        public async Task<IEnumerable<APIVersion>> LoadAllVersionsAsync()
        {
            // Implementation for loading versions from storage;
            await Task.Delay(10);
            return new List<APIVersion>();
        }

        public async Task DeleteVersionAsync(string versionString)
        {
            // Implementation for deleting version;
            await Task.Delay(10);
        }

        public async Task ArchiveVersionAsync(APIVersion version)
        {
            // Implementation for archiving version;
            await Task.Delay(10);
        }

        public async Task SavePolicyAsync(VersionPolicy policy)
        {
            // Implementation for saving policy;
            await Task.Delay(10);
        }

        public async Task<IEnumerable<VersionPolicy>> LoadAllPoliciesAsync()
        {
            // Implementation for loading policies;
            await Task.Delay(10);
            return new List<VersionPolicy>();
        }

        public async Task<IEnumerable<VersionUsageData>> LoadUsageDataAsync(DateTime startDate, DateTime endDate)
        {
            // Implementation for loading usage data;
            await Task.Delay(10);
            return new List<VersionUsageData>();
        }
    }

    internal class VersionRouter;
    {
        private readonly ILogger _logger;

        public VersionRouter(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<APIVersion> GetVersionForRouteAsync(string route, string requestedVersion,
            IEnumerable<APIVersion> availableVersions, APIVersion defaultVersion)
        {
            // Implementation for routing logic;
            await Task.Delay(1);
            return defaultVersion;
        }
    }

    internal class VersionValidator;
    {
        private readonly ILogger _logger;

        public VersionValidator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<bool> ValidateVersionAsync(APIVersion version)
        {
            // Implementation for version validation;
            await Task.Delay(1);
            return true;
        }

        public async Task<ValidationResult> ValidateNewVersionAsync(APIVersion version, IEnumerable<APIVersion> existingVersions)
        {
            // Implementation for new version validation;
            await Task.Delay(1);
            return new ValidationResult { IsValid = true };
        }

        public async Task<bool> CanPromoteVersionAsync(APIVersion version, APIVersionStatus newStatus)
        {
            // Implementation for promotion validation;
            await Task.Delay(1);
            return true;
        }
    }

    internal class VersionNotifier;
    {
        private readonly ILogger _logger;

        public VersionNotifier(ILogger logger)
        {
            _logger = logger;
        }

        public async Task NotifyVersionRegisteredAsync(APIVersion version, VersionRegistrationOptions options)
        {
            // Implementation for notification;
            await Task.Delay(1);
        }

        public async Task NotifyVersionPromotedAsync(APIVersion version, APIVersionStatus newStatus, PromotionOptions options)
        {
            // Implementation for notification;
            await Task.Delay(1);
        }

        public async Task NotifyVersionDeprecatedAsync(APIVersion version, DateTime eolDate, string reason)
        {
            // Implementation for notification;
            await Task.Delay(1);
        }

        public async Task NotifyVersionsCleanedUpAsync(List<string> versions, CleanupOptions options)
        {
            // Implementation for notification;
            await Task.Delay(1);
        }

        public async Task NotifyEolWarningAsync(APIVersion version, int warningDays)
        {
            // Implementation for notification;
            await Task.Delay(1);
        }
    }

    internal class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal class VersionUsageData;
    {
        public string Version { get; set; }
        public long RequestCount { get; set; }
        public long ErrorCount { get; set; }
        public Dictionary<string, long> EndpointUsage { get; set; } = new Dictionary<string, long>();
    }

    // Custom exceptions;

    public class VersionManagerException : Exception
    {
        public VersionManagerException(string message) : base(message) { }
        public VersionManagerException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class VersionValidationException : VersionManagerException;
    {
        public VersionValidationException(string message) : base(message) { }
    }

    public class DuplicateVersionException : VersionManagerException;
    {
        public DuplicateVersionException(string message) : base(message) { }
    }

    public class VersionNotFoundException : VersionManagerException;
    {
        public VersionNotFoundException(string message) : base(message) { }
    }

    public class VersionDeprecatedException : VersionManagerException;
    {
        public VersionDeprecatedException(string message) : base(message) { }
    }

    public class VersionFormatException : VersionManagerException;
    {
        public VersionFormatException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CompatibilityAnalysisException : VersionManagerException;
    {
        public CompatibilityAnalysisException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class StatisticsException : VersionManagerException;
    {
        public StatisticsException(string message, Exception innerException) : base(message, innerException) { }
    }
}
