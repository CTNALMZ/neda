using Microsoft.Extensions.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.Build.PackageManager.DependencyResolver;

namespace NEDA.Build.PackageManager;
{
    /// <summary>
    /// Dependency resolver for managing package dependencies with conflict resolution;
    /// </summary>
    public interface IDependencyResolver;
    {
        Task<DependencyResolutionResult> ResolveAsync(DependencyGraph graph);
        Task<bool> ValidateDependenciesAsync(IEnumerable<PackageReference> packages);
        Task<ConflictResolutionResult> ResolveConflictsAsync(IEnumerable<DependencyConflict> conflicts);
        Task<IEnumerable<PackageDependency>> GetDependencyTreeAsync(string packageId, Version version);
        Task<bool> CheckCircularDependenciesAsync(DependencyGraph graph);
    }

    /// <summary>
    /// Package dependency information;
    /// </summary>
    public class PackageDependency;
    {
        public string PackageId { get; set; }
        public Version Version { get; set; }
        public Version MinVersion { get; set; }
        public Version MaxVersion { get; set; }
        public DependencyType Type { get; set; }
        public bool IsOptional { get; set; }
        public IList<PackageDependency> Dependencies { get; set; } = new List<PackageDependency>();

        public enum DependencyType;
        {
            Direct,
            Transitive,
            Development,
            Build,
            Runtime;
        }
    }

    /// <summary>
    /// Dependency graph representation;
    /// </summary>
    public class DependencyGraph;
    {
        public string RootPackage { get; set; }
        public Version RootVersion { get; set; }
        public IList<PackageNode> Nodes { get; set; } = new List<PackageNode>();
        public IList<DependencyEdge> Edges { get; set; } = new List<DependencyEdge>();

        public class PackageNode;
        {
            public string Id { get; set; }
            public Version Version { get; set; }
            public PackageSource Source { get; set; }
            public PackageMetadata Metadata { get; set; }
        }

        public class DependencyEdge;
        {
            public string FromPackage { get; set; }
            public string ToPackage { get; set; }
            public Version RequiredVersion { get; set; }
            public VersionRange VersionConstraint { get; set; }
        }
    }

    /// <summary>
    /// Main dependency resolver implementation;
    /// </summary>
    public class DependencyResolver : IDependencyResolver;
    {
        private readonly ILogger<DependencyResolver> _logger;
        private readonly IPackageRepository _packageRepository;
        private readonly IConflictResolutionStrategy _conflictResolver;
        private readonly ICircularDependencyDetector _circularDetector;
        private readonly IEventBus _eventBus;
        private readonly IDiagnosticTool _diagnosticTool;

        // Cache for resolved dependencies;
        private readonly Dictionary<string, CacheEntry> _dependencyCache;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Dependency resolver with all required services;
        /// </summary>
        public DependencyResolver(
            ILogger<DependencyResolver> logger,
            IPackageRepository packageRepository,
            IConflictResolutionStrategy conflictResolver,
            ICircularDependencyDetector circularDetector,
            IEventBus eventBus,
            IDiagnosticTool diagnosticTool)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _packageRepository = packageRepository ?? throw new ArgumentNullException(nameof(packageRepository));
            _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
            _circularDetector = circularDetector ?? throw new ArgumentNullException(nameof(circularDetector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));

            _dependencyCache = new Dictionary<string, CacheEntry>();
        }

        /// <summary>
        /// Resolves all dependencies for a given dependency graph;
        /// </summary>
        public async Task<DependencyResolutionResult> ResolveAsync(DependencyGraph graph)
        {
            using var activity = _diagnosticTool.StartActivity("DependencyResolution");

            try
            {
                _logger.LogInformation("Starting dependency resolution for {RootPackage} v{RootVersion}",
                    graph.RootPackage, graph.RootVersion);

                // Check for circular dependencies first;
                var hasCircular = await CheckCircularDependenciesAsync(graph);
                if (hasCircular)
                {
                    throw new CircularDependencyException(
                        "Circular dependencies detected in the dependency graph");
                }

                // Validate input graph;
                if (graph.Nodes == null || !graph.Nodes.Any())
                {
                    throw new ArgumentException("Dependency graph must contain nodes", nameof(graph));
                }

                // Perform dependency resolution;
                var resolutionResult = await PerformResolutionAsync(graph);

                // Resolve any conflicts;
                if (resolutionResult.Conflicts.Any())
                {
                    _logger.LogWarning("Found {ConflictCount} dependency conflicts",
                        resolutionResult.Conflicts.Count());

                    var conflictResult = await ResolveConflictsAsync(resolutionResult.Conflicts);

                    if (!conflictResult.Success)
                    {
                        resolutionResult.Status = ResolutionStatus.ConflictsUnresolved;
                        resolutionResult.ErrorMessage = "Unable to resolve dependency conflicts";
                    }
                    else;
                    {
                        // Apply conflict resolution;
                        resolutionResult = ApplyConflictResolution(resolutionResult, conflictResult);
                    }
                }

                // Validate final dependency set;
                var isValid = await ValidateDependenciesAsync(
                    resolutionResult.ResolvedPackages.Select(p =>
                        new PackageReference { Id = p.Id, Version = p.Version }));

                if (!isValid)
                {
                    resolutionResult.Status = ResolutionStatus.ValidationFailed;
                    resolutionResult.ErrorMessage = "Dependency validation failed";
                }
                else;
                {
                    resolutionResult.Status = ResolutionStatus.Success;
                    _logger.LogInformation("Dependency resolution completed successfully. " +
                                          "Resolved {PackageCount} packages.",
                                          resolutionResult.ResolvedPackages.Count());
                }

                // Publish resolution event;
                await _eventBus.PublishAsync(new DependencyResolvedEvent;
                {
                    RootPackage = graph.RootPackage,
                    RootVersion = graph.RootVersion,
                    ResolvedCount = resolutionResult.ResolvedPackages.Count(),
                    Status = resolutionResult.Status,
                    Timestamp = DateTime.UtcNow;
                });

                return resolutionResult;
            }
            catch (CircularDependencyException ex)
            {
                _logger.LogError(ex, "Circular dependency detected during resolution");
                throw;
            }
            catch (DependencyResolutionException ex)
            {
                _logger.LogError(ex, "Dependency resolution failed");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unexpected error during dependency resolution");
                throw new DependencyResolutionException(
                    "Failed to resolve dependencies", ex);
            }
        }

        /// <summary>
        /// Validates that all dependencies can be satisfied;
        /// </summary>
        public async Task<bool> ValidateDependenciesAsync(IEnumerable<PackageReference> packages)
        {
            if (packages == null)
                throw new ArgumentNullException(nameof(packages));

            try
            {
                var packageList = packages.ToList();
                _logger.LogDebug("Validating {Count} packages", packageList.Count);

                foreach (var package in packageList)
                {
                    // Check if package exists in repository;
                    var exists = await _packageRepository.PackageExistsAsync(package.Id, package.Version);
                    if (!exists)
                    {
                        _logger.LogWarning("Package {PackageId} v{Version} not found in repository",
                            package.Id, package.Version);
                        return false;
                    }

                    // Check version compatibility;
                    var isCompatible = await CheckVersionCompatibilityAsync(package);
                    if (!isCompatible)
                    {
                        _logger.LogWarning("Package {PackageId} v{Version} has compatibility issues",
                            package.Id, package.Version);
                        return false;
                    }
                }

                // Check for duplicate packages with different versions;
                var duplicates = packageList;
                    .GroupBy(p => p.Id)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key);

                if (duplicates.Any())
                {
                    _logger.LogWarning("Found duplicate packages: {Duplicates}",
                        string.Join(", ", duplicates));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dependency validation failed");
                return false;
            }
        }

        /// <summary>
        /// Resolves conflicts between dependencies;
        /// </summary>
        public async Task<ConflictResolutionResult> ResolveConflictsAsync(
            IEnumerable<DependencyConflict> conflicts)
        {
            if (conflicts == null)
                throw new ArgumentNullException(nameof(conflicts));

            var conflictList = conflicts.ToList();
            if (!conflictList.Any())
                return new ConflictResolutionResult { Success = true };

            _logger.LogInformation("Resolving {Count} dependency conflicts", conflictList.Count);

            var result = new ConflictResolutionResult;
            {
                ResolvedConflicts = new List<ConflictResolution>(),
                UnresolvedConflicts = new List<DependencyConflict>()
            };

            foreach (var conflict in conflictList)
            {
                try
                {
                    var resolution = await _conflictResolver.ResolveAsync(conflict);

                    if (resolution != null && resolution.IsResolved)
                    {
                        result.ResolvedConflicts.Add(resolution);
                        _logger.LogDebug("Resolved conflict for package {PackageId}",
                            conflict.PackageId);
                    }
                    else;
                    {
                        result.UnresolvedConflicts.Add(conflict);
                        _logger.LogWarning("Could not resolve conflict for package {PackageId}",
                            conflict.PackageId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resolving conflict for package {PackageId}",
                        conflict.PackageId);
                    result.UnresolvedConflicts.Add(conflict);
                }
            }

            result.Success = !result.UnresolvedConflicts.Any();

            if (result.Success)
            {
                _logger.LogInformation("Successfully resolved all {Count} conflicts", conflictList.Count);
            }
            else;
            {
                _logger.LogWarning("Failed to resolve {Count} of {Total} conflicts",
                    result.UnresolvedConflicts.Count, conflictList.Count);
            }

            return result;
        }

        /// <summary>
        /// Gets complete dependency tree for a package;
        /// </summary>
        public async Task<IEnumerable<PackageDependency>> GetDependencyTreeAsync(
            string packageId, Version version)
        {
            if (string.IsNullOrWhiteSpace(packageId))
                throw new ArgumentException("Package ID cannot be empty", nameof(packageId));
            if (version == null)
                throw new ArgumentNullException(nameof(version));

            var cacheKey = $"{packageId}@{version}";

            // Check cache first;
            if (_dependencyCache.TryGetValue(cacheKey, out var cacheEntry) &&
                cacheEntry.Expiration > DateTime.UtcNow)
            {
                _logger.LogDebug("Cache hit for package {PackageId} v{Version}", packageId, version);
                return cacheEntry.Dependencies;
            }

            _logger.LogDebug("Building dependency tree for {PackageId} v{Version}", packageId, version);

            try
            {
                var dependencies = new List<PackageDependency>();
                var visited = new HashSet<string>();
                var queue = new Queue<(string Id, Version Version, PackageDependency Parent)>();

                // Start with root package;
                var rootDependency = new PackageDependency;
                {
                    PackageId = packageId,
                    Version = version,
                    Type = PackageDependency.DependencyType.Direct;
                };

                dependencies.Add(rootDependency);
                queue.Enqueue((packageId, version, rootDependency));
                visited.Add($"{packageId}@{version}");

                while (queue.Count > 0)
                {
                    var (currentId, currentVersion, parent) = queue.Dequeue();

                    // Get package dependencies from repository;
                    var packageDeps = await _packageRepository.GetDependenciesAsync(
                        currentId, currentVersion);

                    foreach (var dep in packageDeps)
                    {
                        var depKey = $"{dep.PackageId}@{dep.Version}";

                        if (visited.Contains(depKey))
                            continue;

                        var dependencyNode = new PackageDependency;
                        {
                            PackageId = dep.PackageId,
                            Version = dep.Version,
                            MinVersion = dep.MinVersion,
                            MaxVersion = dep.MaxVersion,
                            Type = PackageDependency.DependencyType.Transitive,
                            IsOptional = dep.IsOptional;
                        };

                        if (parent != null)
                        {
                            parent.Dependencies.Add(dependencyNode);
                        }
                        else;
                        {
                            dependencies.Add(dependencyNode);
                        }

                        visited.Add(depKey);

                        // Only queue non-optional dependencies for further expansion;
                        if (!dep.IsOptional)
                        {
                            queue.Enqueue((dep.PackageId, dep.Version, dependencyNode));
                        }
                    }
                }

                // Update cache;
                _dependencyCache[cacheKey] = new CacheEntry
                {
                    Dependencies = dependencies,
                    Expiration = DateTime.UtcNow.Add(_cacheExpiration)
                };

                _logger.LogInformation("Built dependency tree for {PackageId} with {Count} dependencies",
                    packageId, dependencies.Count);

                return dependencies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get dependency tree for {PackageId} v{Version}",
                    packageId, version);
                throw new DependencyResolutionException(
                    $"Failed to get dependency tree for {packageId}", ex);
            }
        }

        /// <summary>
        /// Checks for circular dependencies in the graph;
        /// </summary>
        public async Task<bool> CheckCircularDependenciesAsync(DependencyGraph graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));

            return await _circularDetector.HasCircularDependenciesAsync(graph);
        }

        #region Private Methods;

        private async Task<DependencyResolutionResult> PerformResolutionAsync(DependencyGraph graph)
        {
            var result = new DependencyResolutionResult;
            {
                RootPackage = graph.RootPackage,
                RootVersion = graph.RootVersion,
                ResolvedPackages = new List<ResolvedPackage>(),
                Conflicts = new List<DependencyConflict>()
            };

            var resolvedVersions = new Dictionary<string, Version>();
            var packageRequirements = new Dictionary<string, List<VersionRequirement>>();

            // Process all nodes in the graph;
            foreach (var node in graph.Nodes)
            {
                // Get dependencies for this package;
                var dependencies = await GetDependencyTreeAsync(node.Id, node.Version);

                // Process each dependency;
                await ProcessDependenciesAsync(dependencies, resolvedVersions,
                    packageRequirements, result);
            }

            return result;
        }

        private async Task ProcessDependenciesAsync(
            IEnumerable<PackageDependency> dependencies,
            Dictionary<string, Version> resolvedVersions,
            Dictionary<string, List<VersionRequirement>> packageRequirements,
            DependencyResolutionResult result)
        {
            foreach (var dep in dependencies)
            {
                // Skip optional dependencies that are not required;
                if (dep.IsOptional && !await IsOptionalRequiredAsync(dep))
                    continue;

                // Check if we already have a version for this package;
                if (resolvedVersions.TryGetValue(dep.PackageId, out var existingVersion))
                {
                    // Check if the new version is compatible;
                    if (!IsVersionCompatible(dep.Version, existingVersion, dep.MinVersion, dep.MaxVersion))
                    {
                        // Record conflict;
                        var conflict = new DependencyConflict;
                        {
                            PackageId = dep.PackageId,
                            ExistingVersion = existingVersion,
                            NewVersion = dep.Version,
                            ConflictType = ConflictType.VersionMismatch;
                        };

                        result.Conflicts.Add(conflict);

                        _logger.LogWarning("Version conflict for {PackageId}: " +
                                          "existing={ExistingVersion}, new={NewVersion}",
                                          dep.PackageId, existingVersion, dep.Version);
                    }
                }
                else;
                {
                    // Add to resolved packages;
                    resolvedVersions[dep.PackageId] = dep.Version;

                    result.ResolvedPackages.Add(new ResolvedPackage;
                    {
                        Id = dep.PackageId,
                        Version = dep.Version,
                        Type = dep.Type,
                        Source = await _packageRepository.GetPackageSourceAsync(dep.PackageId, dep.Version)
                    });
                }

                // Record version requirement;
                if (!packageRequirements.ContainsKey(dep.PackageId))
                {
                    packageRequirements[dep.PackageId] = new List<VersionRequirement>();
                }

                packageRequirements[dep.PackageId].Add(new VersionRequirement;
                {
                    RequiredVersion = dep.Version,
                    MinVersion = dep.MinVersion,
                    MaxVersion = dep.MaxVersion,
                    IsOptional = dep.IsOptional;
                });

                // Process nested dependencies;
                if (dep.Dependencies.Any())
                {
                    await ProcessDependenciesAsync(dep.Dependencies, resolvedVersions,
                        packageRequirements, result);
                }
            }
        }

        private async Task<bool> CheckVersionCompatibilityAsync(PackageReference package)
        {
            try
            {
                // Check platform compatibility;
                var metadata = await _packageRepository.GetMetadataAsync(package.Id, package.Version);

                if (metadata == null)
                    return false;

                // Check .NET version compatibility;
                if (metadata.TargetFrameworks != null && metadata.TargetFrameworks.Any())
                {
                    var currentFramework = Environment.Version;
                    var compatible = metadata.TargetFrameworks.Any(f =>
                        IsFrameworkCompatible(f, currentFramework));

                    if (!compatible)
                    {
                        _logger.LogWarning("Package {PackageId} targets frameworks: {Frameworks}, " +
                                          "current: {CurrentFramework}",
                                          package.Id,
                                          string.Join(", ", metadata.TargetFrameworks),
                                          currentFramework);
                        return false;
                    }
                }

                // Check operating system compatibility;
                if (metadata.SupportedOS != null && metadata.SupportedOS.Any())
                {
                    var currentOS = Environment.OSVersion.Platform.ToString();
                    if (!metadata.SupportedOS.Contains(currentOS, StringComparer.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Package {PackageId} supports OS: {OS}, current: {CurrentOS}",
                                          package.Id,
                                          string.Join(", ", metadata.SupportedOS),
                                          currentOS);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking version compatibility for {PackageId}", package.Id);
                return false;
            }
        }

        private bool IsVersionCompatible(Version newVersion, Version existingVersion,
            Version minVersion, Version maxVersion)
        {
            // Check if new version satisfies existing constraints;
            if (minVersion != null && newVersion < minVersion)
                return false;

            if (maxVersion != null && newVersion > maxVersion)
                return false;

            // Check if existing version satisfies new constraints;
            if (minVersion != null && existingVersion < minVersion)
                return false;

            if (maxVersion != null && existingVersion > maxVersion)
                return false;

            return true;
        }

        private bool IsFrameworkCompatible(string targetFramework, Version currentFramework)
        {
            // Simplified framework compatibility check;
            // In real implementation, parse TFM (Target Framework Moniker)
            return targetFramework.Contains($"net{currentFramework.Major}.{currentFramework.Minor}");
        }

        private async Task<bool> IsOptionalRequiredAsync(PackageDependency dependency)
        {
            // Determine if optional dependency is actually required;
            // Based on configuration, runtime checks, etc.
            return await Task.FromResult(false); // Default: not required;
        }

        private DependencyResolutionResult ApplyConflictResolution(
            DependencyResolutionResult originalResult,
            ConflictResolutionResult conflictResult)
        {
            var updatedResult = originalResult;

            foreach (var resolution in conflictResult.ResolvedConflicts)
            {
                // Remove old version;
                var oldPackage = updatedResult.ResolvedPackages;
                    .FirstOrDefault(p => p.Id == resolution.PackageId &&
                                        p.Version == resolution.ExistingVersion);

                if (oldPackage != null)
                {
                    updatedResult.ResolvedPackages.Remove(oldPackage);
                }

                // Add new version;
                updatedResult.ResolvedPackages.Add(new ResolvedPackage;
                {
                    Id = resolution.PackageId,
                    Version = resolution.SelectedVersion,
                    Type = PackageDependency.DependencyType.Direct,
                    Source = resolution.Source;
                });

                // Remove from conflicts;
                var conflict = updatedResult.Conflicts;
                    .FirstOrDefault(c => c.PackageId == resolution.PackageId);

                if (conflict != null)
                {
                    updatedResult.Conflicts.Remove(conflict);
                }
            }

            return updatedResult;
        }

        #endregion;

        #region Supporting Classes;

        private class CacheEntry
        {
            public IEnumerable<PackageDependency> Dependencies { get; set; }
            public DateTime Expiration { get; set; }
        }

        public class DependencyResolutionResult;
        {
            public string RootPackage { get; set; }
            public Version RootVersion { get; set; }
            public IEnumerable<ResolvedPackage> ResolvedPackages { get; set; }
            public IEnumerable<DependencyConflict> Conflicts { get; set; }
            public ResolutionStatus Status { get; set; }
            public string ErrorMessage { get; set; }
            public TimeSpan ResolutionTime { get; set; }
        }

        public class ResolvedPackage;
        {
            public string Id { get; set; }
            public Version Version { get; set; }
            public PackageDependency.DependencyType Type { get; set; }
            public PackageSource Source { get; set; }
            public DateTime ResolvedAt { get; set; } = DateTime.UtcNow;
        }

        public class DependencyConflict;
        {
            public string PackageId { get; set; }
            public Version ExistingVersion { get; set; }
            public Version NewVersion { get; set; }
            public ConflictType ConflictType { get; set; }
            public string Description { get; set; }
        }

        public class ConflictResolutionResult;
        {
            public bool Success { get; set; }
            public IEnumerable<ConflictResolution> ResolvedConflicts { get; set; }
            public IEnumerable<DependencyConflict> UnresolvedConflicts { get; set; }
        }

        public class ConflictResolution;
        {
            public string PackageId { get; set; }
            public Version ExistingVersion { get; set; }
            public Version SelectedVersion { get; set; }
            public PackageSource Source { get; set; }
            public ConflictResolutionStrategy Strategy { get; set; }
            public bool IsResolved { get; set; }
            public string Reason { get; set; }
        }

        public class VersionRequirement;
        {
            public Version RequiredVersion { get; set; }
            public Version MinVersion { get; set; }
            public Version MaxVersion { get; set; }
            public bool IsOptional { get; set; }
        }

        public class PackageReference;
        {
            public string Id { get; set; }
            public Version Version { get; set; }
        }

        public enum ResolutionStatus;
        {
            Pending,
            InProgress,
            Success,
            ConflictsUnresolved,
            ValidationFailed,
            Error;
        }

        public enum ConflictType;
        {
            VersionMismatch,
            PlatformIncompatible,
            LicenseConflict,
            CircularDependency,
            MissingDependency;
        }

        public enum ConflictResolutionStrategy;
        {
            UseNewest,
            UseOldest,
            UseMostCommon,
            Manual,
            Exclude,
            Downgrade;
        }

        public class DependencyResolvedEvent : IEvent;
        {
            public string RootPackage { get; set; }
            public Version RootVersion { get; set; }
            public int ResolvedCount { get; set; }
            public ResolutionStatus Status { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        #endregion;
    }

    #region Exceptions;

    public class DependencyResolutionException : Exception
    {
        public DependencyResolutionException() { }
        public DependencyResolutionException(string message) : base(message) { }
        public DependencyResolutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class CircularDependencyException : DependencyResolutionException;
    {
        public CircularDependencyException() { }
        public CircularDependencyException(string message) : base(message) { }
        public CircularDependencyException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;

    #region Interfaces for External Services;

    public interface IPackageRepository;
    {
        Task<bool> PackageExistsAsync(string packageId, Version version);
        Task<IEnumerable<PackageDependency>> GetDependenciesAsync(string packageId, Version version);
        Task<PackageMetadata> GetMetadataAsync(string packageId, Version version);
        Task<PackageSource> GetPackageSourceAsync(string packageId, Version version);
    }

    public interface IConflictResolutionStrategy;
    {
        Task<ConflictResolution> ResolveAsync(DependencyConflict conflict);
    }

    public interface ICircularDependencyDetector;
    {
        Task<bool> HasCircularDependenciesAsync(DependencyGraph graph);
        Task<IEnumerable<CircularDependency>> FindCircularDependenciesAsync(DependencyGraph graph);
    }

    public class PackageMetadata;
    {
        public string Id { get; set; }
        public Version Version { get; set; }
        public IEnumerable<string> TargetFrameworks { get; set; }
        public IEnumerable<string> SupportedOS { get; set; }
        public string License { get; set; }
        public string Authors { get; set; }
        public string Description { get; set; }
    }

    public class PackageSource;
    {
        public string Name { get; set; }
        public Uri Uri { get; set; }
        public bool IsTrusted { get; set; }
        public bool RequiresAuthentication { get; set; }
    }

    public class CircularDependency;
    {
        public IEnumerable<string> PackageChain { get; set; }
        public string Description { get; set; }
    }

    public class VersionRange;
    {
        public Version MinVersion { get; set; }
        public Version MaxVersion { get; set; }
        public bool IncludeMin { get; set; } = true;
        public bool IncludeMax { get; set; } = true;
    }

    #endregion;
}
