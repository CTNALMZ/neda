using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NEDA.API.Versioning;
{
    /// <summary>
    /// Represents API version information with semantic versioning support;
    /// </summary>
    public class APIVersion;
    {
        /// <summary>
        /// Major version - breaking changes;
        /// </summary>
        [Required]
        [Range(0, int.MaxValue)]
        public int Major { get; private set; }

        /// <summary>
        /// Minor version - new features, backward compatible;
        /// </summary>
        [Required]
        [Range(0, int.MaxValue)]
        public int Minor { get; private set; }

        /// <summary>
        /// Patch version - bug fixes, backward compatible;
        /// </summary>
        [Required]
        [Range(0, int.MaxValue)]
        public int Patch { get; private set; }

        /// <summary>
        /// Pre-release identifier (alpha, beta, rc, etc.)
        /// </summary>
        public string PreRelease { get; private set; }

        /// <summary>
        /// Build metadata;
        /// </summary>
        public string BuildMetadata { get; private set; }

        /// <summary>
        /// API version status;
        /// </summary>
        public APIVersionStatus Status { get; private set; }

        /// <summary>
        /// Release date of this version;
        /// </summary>
        public DateTime ReleaseDate { get; private set; }

        /// <summary>
        /// End of life date for this version;
        /// </summary>
        public DateTime? EndOfLifeDate { get; private set; }

        /// <summary>
        /// Whether this version is deprecated;
        /// </summary>
        public bool IsDeprecated => EndOfLifeDate.HasValue && EndOfLifeDate.Value <= DateTime.UtcNow;

        /// <summary>
        /// Whether this version is stable;
        /// </summary>
        public bool IsStable => Status == APIVersionStatus.Stable && string.IsNullOrEmpty(PreRelease);

        /// <summary>
        /// Change log for this version;
        /// </summary>
        public List<VersionChange> Changes { get; private set; }

        /// <summary>
        /// Supported API endpoints for this version;
        /// </summary>
        public Dictionary<string, EndpointInfo> SupportedEndpoints { get; private set; }

        /// <summary>
        /// Compatibility matrix with other versions;
        /// </summary>
        public Dictionary<string, CompatibilityInfo> Compatibility { get; private set; }

        /// <summary>
        /// Initialize a new API version;
        /// </summary>
        /// <param name="major">Major version</param>
        /// <param name="minor">Minor version</param>
        /// <param name="patch">Patch version</param>
        /// <param name="preRelease">Pre-release identifier</param>
        /// <param name="buildMetadata">Build metadata</param>
        /// <param name="status">Version status</param>
        public APIVersion(int major, int minor, int patch,
                         string preRelease = null,
                         string buildMetadata = null,
                         APIVersionStatus status = APIVersionStatus.Development)
        {
            if (major < 0) throw new ArgumentException("Major version cannot be negative", nameof(major));
            if (minor < 0) throw new ArgumentException("Minor version cannot be negative", nameof(minor));
            if (patch < 0) throw new ArgumentException("Patch version cannot be negative", nameof(patch));

            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease;
            BuildMetadata = buildMetadata;
            Status = status;
            ReleaseDate = DateTime.UtcNow;
            Changes = new List<VersionChange>();
            SupportedEndpoints = new Dictionary<string, EndpointInfo>();
            Compatibility = new Dictionary<string, CompatibilityInfo>();
        }

        /// <summary>
        /// Parse a semantic version string;
        /// </summary>
        public static APIVersion Parse(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                throw new ArgumentException("Version string cannot be null or empty", nameof(versionString));

            var parts = versionString.Split('-');
            var versionPart = parts[0];
            var preReleasePart = parts.Length > 1 ? parts[1] : null;

            var buildParts = preReleasePart?.Split('+');
            var preRelease = buildParts != null && buildParts.Length > 0 ? buildParts[0] : preReleasePart;
            var buildMetadata = buildParts != null && buildParts.Length > 1 ? buildParts[1] : null;

            var versionNumbers = versionPart.Split('.');
            if (versionNumbers.Length != 3)
                throw new FormatException($"Invalid version format: {versionString}");

            return new APIVersion(
                int.Parse(versionNumbers[0]),
                int.Parse(versionNumbers[1]),
                int.Parse(versionNumbers[2]),
                preRelease,
                buildMetadata;
            );
        }

        /// <summary>
        /// Check if this version is compatible with another version;
        /// </summary>
        public CompatibilityType GetCompatibilityWith(APIVersion otherVersion)
        {
            if (otherVersion == null) return CompatibilityType.Incompatible;

            // Same major version with same or lower minor is compatible;
            if (Major == otherVersion.Major && Minor >= otherVersion.Minor)
                return CompatibilityType.FullyCompatible;

            // Different major versions are incompatible;
            if (Major != otherVersion.Major)
                return CompatibilityType.Incompatible;

            // Same major, but lower minor might have limited compatibility;
            if (Major == otherVersion.Major && Minor < otherVersion.Minor)
                return CompatibilityType.PartiallyCompatible;

            return CompatibilityType.Unknown;
        }

        /// <summary>
        /// Compare two API versions;
        /// </summary>
        public int CompareTo(APIVersion other)
        {
            if (other == null) return 1;

            var majorComparison = Major.CompareTo(other.Major);
            if (majorComparison != 0) return majorComparison;

            var minorComparison = Minor.CompareTo(other.Minor);
            if (minorComparison != 0) return minorComparison;

            var patchComparison = Patch.CompareTo(other.Patch);
            if (patchComparison != 0) return patchComparison;

            // Versions with pre-release are considered older than those without;
            if (string.IsNullOrEmpty(PreRelease) && !string.IsNullOrEmpty(other.PreRelease))
                return 1;
            if (!string.IsNullOrEmpty(PreRelease) && string.IsNullOrEmpty(other.PreRelease))
                return -1;

            return 0;
        }

        /// <summary>
        /// Add a change to this version;
        /// </summary>
        public void AddChange(VersionChange change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            Changes.Add(change);
        }

        /// <summary>
        /// Add a supported endpoint;
        /// </summary>
        public void AddEndpoint(string endpoint, EndpointInfo info)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("Endpoint cannot be null or empty", nameof(endpoint));

            if (info == null) throw new ArgumentNullException(nameof(info));

            SupportedEndpoints[endpoint] = info;
        }

        /// <summary>
        /// Set the end of life date for this version;
        /// </summary>
        public void SetEndOfLife(DateTime endOfLifeDate)
        {
            if (endOfLifeDate <= ReleaseDate)
                throw new ArgumentException("End of life date must be after release date", nameof(endOfLifeDate));

            EndOfLifeDate = endOfLifeDate;
            Status = APIVersionStatus.Deprecated;
        }

        /// <summary>
        /// Promote this version to a new status;
        /// </summary>
        public void Promote(APIVersionStatus newStatus)
        {
            if (Status == APIVersionStatus.Deprecated)
                throw new InvalidOperationException("Cannot promote a deprecated version");

            Status = newStatus;
        }

        /// <summary>
        /// Get the semantic version string;
        /// </summary>
        public override string ToString()
        {
            var versionString = $"{Major}.{Minor}.{Patch}";

            if (!string.IsNullOrEmpty(PreRelease))
                versionString += $"-{PreRelease}";

            if (!string.IsNullOrEmpty(BuildMetadata))
                versionString += $"+{BuildMetadata}";

            return versionString;
        }

        /// <summary>
        /// Get API version info for documentation;
        /// </summary>
        public VersionInfo GetVersionInfo()
        {
            return new VersionInfo;
            {
                Version = ToString(),
                Status = Status,
                ReleaseDate = ReleaseDate,
                EndOfLifeDate = EndOfLifeDate,
                IsDeprecated = IsDeprecated,
                IsStable = IsStable,
                ChangeCount = Changes.Count,
                EndpointCount = SupportedEndpoints.Count;
            };
        }

        /// <summary>
        /// Create a deep copy of this version;
        /// </summary>
        public APIVersion Clone()
        {
            var clone = new APIVersion(Major, Minor, Patch, PreRelease, BuildMetadata, Status)
            {
                ReleaseDate = ReleaseDate,
                EndOfLifeDate = EndOfLifeDate;
            };

            foreach (var change in Changes)
                clone.Changes.Add(change.Clone());

            foreach (var endpoint in SupportedEndpoints)
                clone.SupportedEndpoints[endpoint.Key] = endpoint.Value.Clone();

            foreach (var compat in Compatibility)
                clone.Compatibility[compat.Key] = compat.Value.Clone();

            return clone;
        }
    }

    /// <summary>
    /// API version status;
    /// </summary>
    public enum APIVersionStatus;
    {
        /// <summary>
        /// Under active development;
        /// </summary>
        Development = 0,

        /// <summary>
        /// Alpha testing phase;
        /// </summary>
        Alpha = 1,

        /// <summary>
        /// Beta testing phase;
        /// </summary>
        Beta = 2,

        /// <summary>
        /// Release candidate;
        /// </summary>
        ReleaseCandidate = 3,

        /// <summary>
        /// Stable production release;
        /// </summary>
        Stable = 4,

        /// <summary>
        /// Deprecated, scheduled for removal;
        /// </summary>
        Deprecated = 5,

        /// <summary>
        /// End of life, no longer supported;
        /// </summary>
        EndOfLife = 6;
    }

    /// <summary>
    /// Compatibility types between versions;
    /// </summary>
    public enum CompatibilityType;
    {
        /// <summary>
        /// Compatibility unknown;
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Fully compatible;
        /// </summary>
        FullyCompatible = 1,

        /// <summary>
        /// Partially compatible with limitations;
        /// </summary>
        PartiallyCompatible = 2,

        /// <summary>
        /// Not compatible;
        /// </summary>
        Incompatible = 3;
    }

    /// <summary>
    /// Represents a change in a version;
    /// </summary>
    public class VersionChange;
    {
        [Required]
        public string Id { get; set; }

        [Required]
        public ChangeType Type { get; set; }

        [Required]
        public string Description { get; set; }

        public string AffectedEndpoint { get; set; }

        public DateTime ChangeDate { get; set; }

        public string BreakingChangeDetails { get; set; }

        public string MigrationGuide { get; set; }

        public VersionChange Clone()
        {
            return new VersionChange;
            {
                Id = Id,
                Type = Type,
                Description = Description,
                AffectedEndpoint = AffectedEndpoint,
                ChangeDate = ChangeDate,
                BreakingChangeDetails = BreakingChangeDetails,
                MigrationGuide = MigrationGuide;
            };
        }
    }

    /// <summary>
    /// Type of change;
    /// </summary>
    public enum ChangeType;
    {
        /// <summary>
        /// New feature addition;
        /// </summary>
        Feature = 0,

        /// <summary>
        /// Bug fix;
        /// </summary>
        Fix = 1,

        /// <summary>
        /// Performance improvement;
        /// </summary>
        Performance = 2,

        /// <summary>
        /// Security enhancement;
        /// </summary>
        Security = 3,

        /// <summary>
        /// Breaking change;
        /// </summary>
        Breaking = 4,

        /// <summary>
        /// Deprecation notice;
        /// </summary>
        Deprecation = 5;
    }

    /// <summary>
    /// Information about an API endpoint;
    /// </summary>
    public class EndpointInfo;
    {
        [Required]
        public string Path { get; set; }

        [Required]
        public string Method { get; set; }

        public string Description { get; set; }

        public List<string> RequiredPermissions { get; set; } = new List<string>();

        public RateLimitInfo RateLimit { get; set; }

        public bool RequiresAuthentication { get; set; }

        public DateTime AddedInVersion { get; set; }

        public DateTime? DeprecatedInVersion { get; set; }

        public DateTime? RemovedInVersion { get; set; }

        public EndpointInfo Clone()
        {
            return new EndpointInfo;
            {
                Path = Path,
                Method = Method,
                Description = Description,
                RequiredPermissions = new List<string>(RequiredPermissions),
                RateLimit = RateLimit?.Clone(),
                RequiresAuthentication = RequiresAuthentication,
                AddedInVersion = AddedInVersion,
                DeprecatedInVersion = DeprecatedInVersion,
                RemovedInVersion = RemovedInVersion;
            };
        }
    }

    /// <summary>
    /// Rate limiting information;
    /// </summary>
    public class RateLimitInfo;
    {
        public int RequestsPerMinute { get; set; }
        public int RequestsPerHour { get; set; }
        public int RequestsPerDay { get; set; }
        public int BurstLimit { get; set; }

        public RateLimitInfo Clone()
        {
            return new RateLimitInfo;
            {
                RequestsPerMinute = RequestsPerMinute,
                RequestsPerHour = RequestsPerHour,
                RequestsPerDay = RequestsPerDay,
                BurstLimit = BurstLimit;
            };
        }
    }

    /// <summary>
    /// Compatibility information;
    /// </summary>
    public class CompatibilityInfo;
    {
        public string TargetVersion { get; set; }
        public CompatibilityType Compatibility { get; set; }
        public List<string> KnownIssues { get; set; } = new List<string>();
        public List<string> MigrationSteps { get; set; } = new List<string>();

        public CompatibilityInfo Clone()
        {
            return new CompatibilityInfo;
            {
                TargetVersion = TargetVersion,
                Compatibility = Compatibility,
                KnownIssues = new List<string>(KnownIssues),
                MigrationSteps = new List<string>(MigrationSteps)
            };
        }
    }

    /// <summary>
    /// Version information for documentation;
    /// </summary>
    public class VersionInfo;
    {
        public string Version { get; set; }
        public APIVersionStatus Status { get; set; }
        public DateTime ReleaseDate { get; set; }
        public DateTime? EndOfLifeDate { get; set; }
        public bool IsDeprecated { get; set; }
        public bool IsStable { get; set; }
        public int ChangeCount { get; set; }
        public int EndpointCount { get; set; }
    }

    /// <summary>
    /// Interface for decision engine (reference from project structure)
    /// </summary>
    public interface IDecisionEngine;
    {
        // Interface defined for cross-referencing;
    }

    /// <summary>
    /// Interface for risk analyzer (reference from project structure)
    /// </summary>
    public interface IRiskAnalyzer;
    {
        // Interface defined for cross-referencing;
    }

    /// <summary>
    /// Interface for optimization engine (reference from project structure)
    /// </summary>
    public interface IOptimizationEngine;
    {
        // Interface defined for cross-referencing;
    }

    /// <summary>
    /// Interface for ethics engine (reference from project structure)
    /// </summary>
    public interface IEthicsEngine;
    {
        // Interface defined for cross-referencing;
    }

    /// <summary>
    /// Decision history container;
    /// </summary>
    internal class DecisionHistory;
    {
        // Internal implementation for decision engine;
    }

    /// <summary>
    /// Decision cache container;
    /// </summary>
    internal class DecisionCache;
    {
        // Internal implementation for decision engine;
    }

    /// <summary>
    /// ML Model reference;
    /// </summary>
    internal class MLModel;
    {
        // Reference to ML model from AI module;
    }
}
