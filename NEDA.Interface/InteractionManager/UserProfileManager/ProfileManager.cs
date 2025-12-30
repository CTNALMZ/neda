using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.Security;
using NEDA.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Logging;
using NEDA.SecurityModules.AdvancedSecurity.Authentication;
using NEDA.SecurityModules.AdvancedSecurity.Authorization;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.Services.UserService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.InteractionManager.UserProfileManager;
{
    /// <summary>
    /// Represents a comprehensive user profile with personalization and preferences;
    /// </summary>
    public class UserProfile;
    {
        /// <summary>
        /// Unique user identifier;
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// Username for display and login;
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// User's full name;
        /// </summary>
        public string FullName { get; set; }

        /// <summary>
        /// User's email address;
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Profile creation timestamp;
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Last profile update timestamp;
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Last login timestamp;
        /// </summary>
        public DateTime? LastLogin { get; set; }

        /// <summary>
        /// User's timezone;
        /// </summary>
        public string Timezone { get; set; }

        /// <summary>
        /// User's preferred language;
        /// </summary>
        public string PreferredLanguage { get; set; }

        /// <summary>
        /// User's locale/culture;
        /// </summary>
        public string Locale { get; set; }

        /// <summary>
        /// Profile status (Active, Suspended, Deleted, etc.)
        /// </summary>
        public ProfileStatus Status { get; set; }

        /// <summary>
        /// User's roles and permissions;
        /// </summary>
        public List<string> Roles { get; set; } = new List<string>();

        /// <summary>
        /// User's assigned permissions;
        /// </summary>
        public List<string> Permissions { get; set; } = new List<string>();

        /// <summary>
        /// Personalization preferences;
        /// </summary>
        public Dictionary<string, object> Preferences { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// User settings for various applications;
        /// </summary>
        public Dictionary<string, Dictionary<string, object>> ApplicationSettings { get; set; } = new Dictionary<string, Dictionary<string, object>>();

        /// <summary>
        /// User's contact information;
        /// </summary>
        public ContactInfo ContactInfo { get; set; } = new ContactInfo();

        /// <summary>
        /// Professional information;
        /// </summary>
        public ProfessionalInfo ProfessionalInfo { get; set; } = new ProfessionalInfo();

        /// <summary>
        /// Biometric profiles (face, voice, etc.)
        /// </summary>
        public List<BiometricProfile> BiometricProfiles { get; set; } = new List<BiometricProfile>();

        /// <summary>
        /// Security settings and policies;
        /// </summary>
        public SecuritySettings SecuritySettings { get; set; } = new SecuritySettings();

        /// <summary>
        /// Usage statistics and analytics;
        /// </summary>
        public UsageStatistics UsageStatistics { get; set; } = new UsageStatistics();

        /// <summary>
        /// Custom metadata for extensibility;
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Tags for categorization;
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Profile version for migration support;
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Checks if profile is active;
        /// </summary>
        public bool IsActive => Status == ProfileStatus.Active;

        /// <summary>
        /// Calculates profile age;
        /// </summary>
        public TimeSpan Age => DateTime.UtcNow - CreatedAt;
    }

    /// <summary>
    /// Profile status enumeration;
    /// </summary>
    public enum ProfileStatus;
    {
        Pending,
        Active,
        Suspended,
        Deactivated,
        Deleted,
        Banned,
        Locked;
    }

    /// <summary>
    /// Contact information;
    /// </summary>
    public class ContactInfo;
    {
        public string Phone { get; set; }
        public string Mobile { get; set; }
        public string Address { get; set; }
        public string City { get; set; }
        public string Country { get; set; }
        public string PostalCode { get; set; }
        public Dictionary<string, string> AdditionalContacts { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Professional information;
    /// </summary>
    public class ProfessionalInfo;
    {
        public string Company { get; set; }
        public string Position { get; set; }
        public string Department { get; set; }
        public List<string> Skills { get; set; } = new List<string>();
        public List<Experience> Experience { get; set; } = new List<Experience>();
        public List<Education> Education { get; set; } = new List<Education>();
        public List<Certification> Certifications { get; set; } = new List<Certification>();
    }

    /// <summary>
    /// Professional experience;
    /// </summary>
    public class Experience;
    {
        public string Company { get; set; }
        public string Position { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Description { get; set; }
        public List<string> Technologies { get; set; } = new List<string>();
    }

    /// <summary>
    /// Education information;
    /// </summary>
    public class Education;
    {
        public string Institution { get; set; }
        public string Degree { get; set; }
        public string FieldOfStudy { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public double? GPA { get; set; }
    }

    /// <summary>
    /// Professional certification;
    /// </summary>
    public class Certification;
    {
        public string Name { get; set; }
        public string Issuer { get; set; }
        public DateTime IssueDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public string CredentialId { get; set; }
    }

    /// <summary>
    /// Biometric profile for authentication;
    /// </summary>
    public class BiometricProfile;
    {
        public string ProfileId { get; set; }
        public BiometricType Type { get; set; }
        public string DataHash { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUsed { get; set; }
        public int UsageCount { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Biometric type enumeration;
    /// </summary>
    public enum BiometricType;
    {
        Face,
        Fingerprint,
        Voice,
        Iris,
        Palm,
        Signature,
        Behavioral;
    }

    /// <summary>
    /// Security settings for user profile;
    /// </summary>
    public class SecuritySettings;
    {
        public bool TwoFactorEnabled { get; set; }
        public string TwoFactorMethod { get; set; }
        public bool BiometricEnabled { get; set; }
        public List<string> AllowedBiometricTypes { get; set; } = new List<string>();
        public int FailedLoginAttempts { get; set; }
        public DateTime? LastFailedLogin { get; set; }
        public List<string> TrustedDevices { get; set; } = new List<string>();
        public List<string> TrustedLocations { get; set; } = new List<string>();
        public bool EmailVerificationRequired { get; set; }
        public bool PhoneVerificationRequired { get; set; }
        public DateTime? LastPasswordChange { get; set; }
        public int PasswordChangeDays { get; set; } = 90;
        public List<SecurityQuestion> SecurityQuestions { get; set; } = new List<SecurityQuestion>();
        public Dictionary<string, object> SecurityPolicies { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Security question for account recovery;
    /// </summary>
    public class SecurityQuestion;
    {
        public string Question { get; set; }
        public string AnswerHash { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Usage statistics for analytics;
    /// </summary>
    public class UsageStatistics;
    {
        public int TotalLogins { get; set; }
        public DateTime? FirstLogin { get; set; }
        public DateTime? LastLogin { get; set; }
        public TimeSpan TotalSessionTime { get; set; }
        public double AverageSessionTimeMinutes => TotalLogins > 0 ? TotalSessionTime.TotalMinutes / TotalLogins : 0;
        public Dictionary<string, int> FeatureUsage { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, TimeSpan> ModuleUsage { get; set; } = new Dictionary<string, TimeSpan>();
        public List<ActivityLog> RecentActivity { get; set; } = new List<ActivityLog>();
        public Dictionary<string, int> DeviceUsage { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Activity log entry
    /// </summary>
    public class ActivityLog;
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; }
        public string Module { get; set; }
        public string Details { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Configuration for ProfileManager;
    /// </summary>
    public class ProfileManagerOptions;
    {
        public TimeSpan CacheTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public int MaxProfilesInCache { get; set; } = 10000;
        public bool EnableProfileCompression { get; set; } = true;
        public bool EnableProfileEncryption { get; set; } = true;
        public TimeSpan ProfileLockTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxFailedAttempts { get; set; } = 5;
        public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
        public List<string> RequiredFields { get; set; } = new List<string> { "Username", "Email" };
        public Dictionary<string, ValidationRule> ValidationRules { get; set; } = new Dictionary<string, ValidationRule>();
        public int MaxBiometricProfiles { get; set; } = 5;
        public bool EnableAuditLogging { get; set; } = true;
        public bool EnableActivityTracking { get; set; } = true;
    }

    /// <summary>
    /// Validation rule for profile fields;
    /// </summary>
    public class ValidationRule;
    {
        public string Pattern { get; set; }
        public int MinLength { get; set; }
        public int MaxLength { get; set; }
        public bool Required { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Search criteria for profile queries;
    /// </summary>
    public class ProfileSearchCriteria;
    {
        public string Username { get; set; }
        public string Email { get; set; }
        public string FullName { get; set; }
        public ProfileStatus? Status { get; set; }
        public string Role { get; set; }
        public string Permission { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
        public DateTime? LastLoginAfter { get; set; }
        public DateTime? LastLoginBefore { get; set; }
        public string Tag { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; } = 100;
        public string OrderBy { get; set; } = "LastUpdated";
        public bool Descending { get; set; } = true;
    }

    /// <summary>
    /// Result of profile validation;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, string> FieldErrors { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Profile statistics;
    /// </summary>
    public class ProfileStatistics;
    {
        public int TotalProfiles { get; set; }
        public int ActiveProfiles { get; set; }
        public int NewProfilesToday { get; set; }
        public Dictionary<ProfileStatus, int> StatusDistribution { get; set; } = new Dictionary<ProfileStatus, int>();
        public Dictionary<string, int> RoleDistribution { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> LanguageDistribution { get; set; } = new Dictionary<string, int>();
        public double AverageProfileAgeDays { get; set; }
        public int ProfilesWithBiometrics { get; set; }
        public int ProfilesWithTwoFactor { get; set; }
    }

    /// <summary>
    /// Health status of ProfileManager;
    /// </summary>
    public class ProfileManagerHealth;
    {
        public bool IsHealthy { get; set; }
        public string Status { get; set; }
        public int TotalProfiles { get; set; }
        public int CachedProfiles { get; set; }
        public long CacheMemoryUsageBytes { get; set; }
        public double CacheHitRatio { get; set; }
        public DateTime? LastBackup { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Profile update operation;
    /// </summary>
    public class ProfileUpdate;
    {
        public string Field { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public string Reason { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// Interface for profile management operations;
    /// </summary>
    public interface IProfileManager;
    {
        /// <summary>
        /// Creates a new user profile;
        /// </summary>
        Task<UserProfile> CreateProfileAsync(string userId, Dictionary<string, object> initialData,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a user profile by ID;
        /// </summary>
        Task<UserProfile> GetProfileAsync(string userId, bool includeSensitive = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a user profile;
        /// </summary>
        Task<UserProfile> UpdateProfileAsync(string userId, Action<UserProfile> updateAction,
            string updateReason = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates specific profile fields;
        /// </summary>
        Task<UserProfile> UpdateProfileFieldsAsync(string userId, Dictionary<string, object> updates,
            string updateReason = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes or deactivates a user profile;
        /// </summary>
        Task<bool> DeleteProfileAsync(string userId, string reason = null, bool permanent = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Searches profiles based on criteria;
        /// </summary>
        Task<IEnumerable<UserProfile>> SearchProfilesAsync(ProfileSearchCriteria criteria,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates profile data;
        /// </summary>
        Task<ValidationResult> ValidateProfileAsync(UserProfile profile,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Merges two profiles (for account merging)
        /// </summary>
        Task<UserProfile> MergeProfilesAsync(string primaryUserId, string secondaryUserId,
            MergeStrategy strategy, CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports profile data;
        /// </summary>
        Task<byte[]> ExportProfileAsync(string userId, ExportFormat format, bool includeSensitive = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Imports profile data;
        /// </summary>
        Task<UserProfile> ImportProfileAsync(string userId, byte[] data, ImportMode mode,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds a biometric profile;
        /// </summary>
        Task<bool> AddBiometricProfileAsync(string userId, BiometricProfile biometricProfile,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes a biometric profile;
        /// </summary>
        Task<bool> RemoveBiometricProfileAsync(string userId, string biometricProfileId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Verifies biometric data;
        /// </summary>
        Task<(bool Verified, double Confidence)> VerifyBiometricAsync(string userId,
            BiometricType type, string biometricData, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates user preferences;
        /// </summary>
        Task<UserProfile> UpdatePreferencesAsync(string userId, string category,
            Dictionary<string, object> preferences, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets user preference;
        /// </summary>
        Task<object> GetPreferenceAsync(string userId, string category, string key,
            object defaultValue = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Records user activity;
        /// </summary>
        Task RecordActivityAsync(string userId, ActivityLog activityLog,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets profile statistics;
        /// </summary>
        Task<ProfileStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets ProfileManager health status;
        /// </summary>
        Task<ProfileManagerHealth> GetHealthAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Backs up profile data;
        /// </summary>
        Task<bool> BackupProfileAsync(string userId, string backupPath = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Restores profile from backup;
        /// </summary>
        Task<bool> RestoreProfileAsync(string userId, string backupPath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Format for exporting profile data;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml,
        Binary,
        EncryptedJson,
        GDPR;
    }

    /// <summary>
    /// Mode for importing profile data;
    /// </summary>
    public enum ImportMode;
    {
        Merge,
        Replace,
        UpdateOnly,
        CreateNew;
    }

    /// <summary>
    /// Strategy for merging profiles;
    /// </summary>
    public enum MergeStrategy;
    {
        KeepPrimary,
        KeepSecondary,
        MergeAll,
        Interactive;
    }

    /// <summary>
    /// Implementation of comprehensive profile management with security and personalization;
    /// </summary>
    public class ProfileManager : IProfileManager, IDisposable;
    {
        private readonly ILogger<ProfileManager> _logger;
        private readonly IAuthService _authService;
        private readonly IRoleManager _roleManager;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IAuditLogger _auditLogger;
        private readonly IUserManager _userManager;
        private readonly ProfileManagerOptions _options;
        private readonly ConcurrentDictionary<string, UserProfile> _profileCache;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileLocks;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        private const string ProfileTableName = "UserProfiles";
        private const string ProfileHistoryTableName = "ProfileHistory";
        private const string BiometricTableName = "BiometricProfiles";
        private const string ActivityTableName = "UserActivity";
        private const int MaxBatchSize = 500;
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Initializes a new instance of ProfileManager;
        /// </summary>
        public ProfileManager(
            ILogger<ProfileManager> logger,
            IAuthService authService,
            IRoleManager roleManager,
            ICryptoEngine cryptoEngine,
            IAuditLogger auditLogger,
            IUserManager userManager,
            IOptions<ProfileManagerOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _roleManager = roleManager ?? throw new ArgumentNullException(nameof(roleManager));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _options = options?.Value ?? new ProfileManagerOptions();

            _profileCache = new ConcurrentDictionary<string, UserProfile>();
            _profileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

            // Initialize cleanup timer for cache maintenance;
            _cleanupTimer = new Timer(CleanupCacheCallback, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            _logger.LogInformation("ProfileManager initialized with {CacheTimeout} cache timeout",
                _options.CacheTimeout);
        }

        /// <inheritdoc/>
        public async Task<UserProfile> CreateProfileAsync(string userId,
            Dictionary<string, object> initialData, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            var profileLock = _profileLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

            if (!await profileLock.WaitAsync(LockTimeout, cancellationToken))
            {
                throw new ProfileManagerException($"Timeout acquiring lock for user {userId}");
            }

            try
            {
                // Check if profile already exists;
                var existingProfile = await GetProfileFromStoreAsync(userId, cancellationToken);
                if (existingProfile != null)
                {
                    throw new ProfileManagerException($"Profile for user {userId} already exists");
                }

                var now = DateTime.UtcNow;
                var profile = new UserProfile;
                {
                    UserId = userId,
                    Username = initialData?.GetValueOrDefault("Username")?.ToString() ?? userId,
                    FullName = initialData?.GetValueOrDefault("FullName")?.ToString(),
                    Email = initialData?.GetValueOrDefault("Email")?.ToString(),
                    CreatedAt = now,
                    LastUpdated = now,
                    LastLogin = null,
                    Timezone = initialData?.GetValueOrDefault("Timezone")?.ToString() ?? "UTC",
                    PreferredLanguage = initialData?.GetValueOrDefault("PreferredLanguage")?.ToString() ?? "en-US",
                    Locale = initialData?.GetValueOrDefault("Locale")?.ToString() ?? "en-US",
                    Status = ProfileStatus.Pending,
                    Roles = new List<string> { "User" },
                    Permissions = new List<string>(),
                    Preferences = new Dictionary<string, object>(),
                    ApplicationSettings = new Dictionary<string, Dictionary<string, object>>(),
                    ContactInfo = new ContactInfo(),
                    ProfessionalInfo = new ProfessionalInfo(),
                    BiometricProfiles = new List<BiometricProfile>(),
                    SecuritySettings = new SecuritySettings;
                    {
                        TwoFactorEnabled = false,
                        BiometricEnabled = false,
                        FailedLoginAttempts = 0,
                        PasswordChangeDays = 90;
                    },
                    UsageStatistics = new UsageStatistics;
                    {
                        FirstLogin = null,
                        LastLogin = null,
                        TotalLogins = 0;
                    },
                    Metadata = new Dictionary<string, object>(),
                    Tags = new List<string> { "new" },
                    Version = 1;
                };

                // Apply initial data;
                if (initialData != null)
                {
                    ApplyInitialData(profile, initialData);
                }

                // Validate profile;
                var validationResult = await ValidateProfileAsync(profile, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new ProfileManagerException($"Profile validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Store profile;
                await StoreProfileAsync(profile, cancellationToken);

                // Add to cache;
                _profileCache[userId] = profile;

                // Create user in auth system;
                await _authService.CreateUserAsync(userId, cancellationToken);

                // Assign default roles;
                await _roleManager.AssignRoleAsync(userId, "User", cancellationToken);

                // Log audit trail;
                await _auditLogger.LogAsync(new AuditEntry
                {
                    Action = "ProfileManager.CreateProfile",
                    EntityId = userId,
                    EntityType = "UserProfile",
                    UserId = userId,
                    Timestamp = now,
                    Details = "Created new user profile"
                }, cancellationToken);

                _logger.LogInformation("Created profile for user {UserId}", userId);

                return profile;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "Failed to create profile for user {UserId}", userId);
                throw new ProfileManagerException("Failed to create profile", ex);
            }
            finally
            {
                profileLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UserProfile> GetProfileAsync(string userId, bool includeSensitive = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            // Try to get from cache first;
            if (_profileCache.TryGetValue(userId, out var cachedProfile) &&
                cachedProfile.LastUpdated > DateTime.UtcNow.Add(-_options.CacheTimeout))
            {
                return includeSensitive ? cachedProfile : SanitizeProfile(cachedProfile);
            }

            var profileLock = _profileLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

            if (!await profileLock.WaitAsync(LockTimeout, cancellationToken))
            {
                throw new ProfileManagerException($"Timeout acquiring lock for user {userId}");
            }

            try
            {
                // Load from persistent storage;
                var profile = await GetProfileFromStoreAsync(userId, cancellationToken);
                if (profile == null)
                {
                    throw new ProfileManagerException($"Profile for user {userId} not found");
                }

                // Update cache;
                _profileCache[userId] = profile;

                return includeSensitive ? profile : SanitizeProfile(profile);
            }
            finally
            {
                profileLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UserProfile> UpdateProfileAsync(string userId, Action<UserProfile> updateAction,
            string updateReason = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            var profileLock = _profileLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

            if (!await profileLock.WaitAsync(LockTimeout, cancellationToken))
            {
                throw new ProfileManagerException($"Timeout acquiring lock for user {userId}");
            }

            try
            {
                var profile = await GetProfileAsync(userId, true, cancellationToken);
                var oldProfile = CloneProfile(profile);

                // Apply update;
                updateAction(profile);
                profile.LastUpdated = DateTime.UtcNow;
                profile.Version++;

                // Validate updated profile;
                var validationResult = await ValidateProfileAsync(profile, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new ProfileManagerException($"Profile validation failed after update: {string.Join(", ", validationResult.Errors)}");
                }

                // Store updated profile;
                await StoreProfileAsync(profile, cancellationToken);

                // Update cache;
                _profileCache[userId] = profile;

                // Record update history;
                await RecordProfileUpdateAsync(userId, oldProfile, profile, updateReason, cancellationToken);

                // Log audit trail;
                await _auditLogger.LogAsync(new AuditEntry
                {
                    Action = "ProfileManager.UpdateProfile",
                    EntityId = userId,
                    EntityType = "UserProfile",
                    UserId = userId,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Updated profile: {updateReason ?? "No reason provided"}"
                }, cancellationToken);

                _logger.LogDebug("Updated profile for user {UserId}", userId);

                return profile;
            }
            finally
            {
                profileLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UserProfile> UpdateProfileFieldsAsync(string userId,
            Dictionary<string, object> updates, string updateReason = null,
            CancellationToken cancellationToken = default)
        {
            if (updates == null || !updates.Any())
                throw new ArgumentException("Updates cannot be null or empty", nameof(updates));

            return await UpdateProfileAsync(userId, profile =>
            {
                foreach (var update in updates)
                {
                    ApplyFieldUpdate(profile, update.Key, update.Value);
                }
            }, updateReason, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteProfileAsync(string userId, string reason = null,
            bool permanent = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            var profileLock = _profileLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

            if (!await profileLock.WaitAsync(LockTimeout, cancellationToken))
            {
                throw new ProfileManagerException($"Timeout acquiring lock for user {userId}");
            }

            try
            {
                var profile = await GetProfileAsync(userId, true, cancellationToken);

                if (permanent)
                {
                    // Permanent deletion;
                    await DeleteProfileFromStoreAsync(userId, cancellationToken);

                    // Remove from auth system;
                    await _authService.DeleteUserAsync(userId, cancellationToken);

                    // Log permanent deletion;
                    await _auditLogger.LogAsync(new AuditEntry
                    {
                        Action = "ProfileManager.DeleteProfile.Permanent",
                        EntityId = userId,
                        EntityType = "UserProfile",
                        UserId = userId,
                        Timestamp = DateTime.UtcNow,
                        Details = $"Permanently deleted profile: {reason ?? "No reason provided"}"
                    }, cancellationToken);

                    _logger.LogWarning("Permanently deleted profile for user {UserId}", userId);
                }
                else;
                {
                    // Soft deletion (deactivation)
                    await UpdateProfileAsync(userId, p =>
                    {
                        p.Status = ProfileStatus.Deleted;
                        p.LastUpdated = DateTime.UtcNow;
                        p.Tags.Add("deleted");

                        // Archive sensitive data;
                        ArchiveSensitiveData(p);
                    }, $"Soft deletion: {reason}", cancellationToken);

                    _logger.LogInformation("Soft deleted profile for user {UserId}", userId);
                }

                // Remove from cache;
                _profileCache.TryRemove(userId, out _);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete profile for user {UserId}", userId);
                return false;
            }
            finally
            {
                profileLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<UserProfile>> SearchProfilesAsync(ProfileSearchCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            await _operationLock.WaitAsync(cancellationToken);

            try
            {
                var query = BuildSearchQuery(criteria, out var parameters);
                var profiles = new List<UserProfile>();

                // Execute search query;
                var userIds = await ExecuteSearchQueryAsync(query, parameters, cancellationToken);

                foreach (var userId in userIds)
                {
                    try
                    {
                        var profile = await GetProfileAsync(userId, false, cancellationToken);
                        if (profile != null)
                        {
                            profiles.Add(profile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to load profile for user {UserId} during search", userId);
                    }
                }

                // Apply ordering and pagination;
                profiles = ApplyOrdering(profiles, criteria.OrderBy, criteria.Descending)
                    .Skip(criteria.Skip)
                    .Take(criteria.Take)
                    .ToList();

                return profiles;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<ValidationResult> ValidateProfileAsync(UserProfile profile,
            CancellationToken cancellationToken = default)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var result = new ValidationResult { IsValid = true };

            // Check required fields;
            foreach (var requiredField in _options.RequiredFields)
            {
                var value = GetFieldValue(profile, requiredField);
                if (string.IsNullOrWhiteSpace(value?.ToString()))
                {
                    result.IsValid = false;
                    result.Errors.Add($"Required field '{requiredField}' is missing or empty");
                    result.FieldErrors[requiredField] = "This field is required";
                }
            }

            // Validate email format;
            if (!string.IsNullOrWhiteSpace(profile.Email))
            {
                if (!IsValidEmail(profile.Email))
                {
                    result.IsValid = false;
                    result.Errors.Add("Invalid email format");
                    result.FieldErrors["Email"] = "Invalid email format";
                }
            }

            // Validate username format;
            if (!string.IsNullOrWhiteSpace(profile.Username))
            {
                if (profile.Username.Length < 3 || profile.Username.Length > 50)
                {
                    result.IsValid = false;
                    result.Errors.Add("Username must be between 3 and 50 characters");
                    result.FieldErrors["Username"] = "Must be between 3 and 50 characters";
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(profile.Username, @"^[a-zA-Z0-9_\.-]+$"))
                {
                    result.IsValid = false;
                    result.Errors.Add("Username contains invalid characters");
                    result.FieldErrors["Username"] = "Only letters, numbers, dots, dashes, and underscores are allowed";
                }
            }

            // Validate custom rules;
            foreach (var rule in _options.ValidationRules)
            {
                var value = GetFieldValue(profile, rule.Key)?.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    if (rule.Value.MinLength > 0 && value.Length < rule.Value.MinLength)
                    {
                        result.IsValid = false;
                        result.Errors.Add(rule.Value.ErrorMessage ?? $"Field '{rule.Key}' is too short");
                        result.FieldErrors[rule.Key] = $"Minimum length is {rule.Value.MinLength} characters";
                    }

                    if (rule.Value.MaxLength > 0 && value.Length > rule.Value.MaxLength)
                    {
                        result.IsValid = false;
                        result.Errors.Add(rule.Value.ErrorMessage ?? $"Field '{rule.Key}' is too long");
                        result.FieldErrors[rule.Key] = $"Maximum length is {rule.Value.MaxLength} characters";
                    }

                    if (!string.IsNullOrEmpty(rule.Value.Pattern) &&
                        !System.Text.RegularExpressions.Regex.IsMatch(value, rule.Value.Pattern))
                    {
                        result.IsValid = false;
                        result.Errors.Add(rule.Value.ErrorMessage ?? $"Field '{rule.Key}' has invalid format");
                        result.FieldErrors[rule.Key] = "Invalid format";
                    }
                }
                else if (rule.Value.Required)
                {
                    result.IsValid = false;
                    result.Errors.Add($"Required field '{rule.Key}' is missing");
                    result.FieldErrors[rule.Key] = "This field is required";
                }
            }

            // Check for duplicate email/username (would require database query)
            // This is simplified - actual implementation would check database;

            return result;
        }

        /// <inheritdoc/>
        public async Task<UserProfile> MergeProfilesAsync(string primaryUserId, string secondaryUserId,
            MergeStrategy strategy, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(primaryUserId) || string.IsNullOrWhiteSpace(secondaryUserId))
                throw new ArgumentException("User IDs cannot be null or empty");

            if (primaryUserId == secondaryUserId)
                throw new ArgumentException("Cannot merge the same profile");

            await _operationLock.WaitAsync(cancellationToken);

            try
            {
                var primaryProfile = await GetProfileAsync(primaryUserId, true, cancellationToken);
                var secondaryProfile = await GetProfileAsync(secondaryUserId, true, cancellationToken);

                if (primaryProfile == null || secondaryProfile == null)
                {
                    throw new ProfileManagerException("One or both profiles not found");
                }

                // Merge profiles based on strategy;
                var mergedProfile = MergeProfiles(primaryProfile, secondaryProfile, strategy);

                // Store merged profile;
                await StoreProfileAsync(mergedProfile, cancellationToken);

                // Update cache;
                _profileCache[primaryUserId] = mergedProfile;

                // Deactivate secondary profile;
                await UpdateProfileAsync(secondaryUserId, p =>
                {
                    p.Status = ProfileStatus.Deactivated;
                    p.Tags.Add("merged");
                    p.Metadata["MergedInto"] = primaryUserId;
                    p.Metadata["MergedAt"] = DateTime.UtcNow;
                }, $"Merged into {primaryUserId}", cancellationToken);

                // Log audit trail;
                await _auditLogger.LogAsync(new AuditEntry
                {
                    Action = "ProfileManager.MergeProfiles",
                    EntityId = primaryUserId,
                    EntityType = "UserProfile",
                    UserId = primaryUserId,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Merged profile {secondaryUserId} into {primaryUserId} using strategy {strategy}"
                }, cancellationToken);

                _logger.LogInformation("Merged profile {SecondaryUserId} into {PrimaryUserId}",
                    secondaryUserId, primaryUserId);

                return mergedProfile;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]> ExportProfileAsync(string userId, ExportFormat format,
            bool includeSensitive = false, CancellationToken cancellationToken = default)
        {
            var profile = await GetProfileAsync(userId, includeSensitive, cancellationToken);

            // Prepare export data (remove or encrypt sensitive data)
            var exportData = PrepareExportData(profile, includeSensitive);

            switch (format)
            {
                case ExportFormat.Json:
                    return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                case ExportFormat.Xml:
                    var xmlSerializer = new System.Xml.Serialization.XmlSerializer(exportData.GetType());
                    using (var stream = new System.IO.MemoryStream())
                    {
                        xmlSerializer.Serialize(stream, exportData);
                        return stream.ToArray();
                    }

                case ExportFormat.EncryptedJson:
                    var jsonBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData);
                    return await _cryptoEngine.EncryptAsync(jsonBytes, cancellationToken);

                case ExportFormat.GDPR:
                    return await ExportGDPRDataAsync(profile, cancellationToken);

                default:
                    throw new NotSupportedException($"Export format {format} is not supported");
            }
        }

        /// <inheritdoc/>
        public async Task<UserProfile> ImportProfileAsync(string userId, byte[] data, ImportMode mode,
            CancellationToken cancellationToken = default)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Import data cannot be null or empty", nameof(data));

            var profileLock = _profileLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

            if (!await profileLock.WaitAsync(LockTimeout, cancellationToken))
            {
                throw new ProfileManagerException($"Timeout acquiring lock for user {userId}");
            }

            try
            {
                // Determine format and deserialize;
                UserProfile importedProfile;
                try
                {
                    importedProfile = DeserializeProfileData(data);
                }
                catch (Exception ex)
                {
                    throw new ProfileManagerException("Failed to deserialize profile data", ex);
                }

                importedProfile.UserId = userId;
                importedProfile.LastUpdated = DateTime.UtcNow;

                // Validate imported profile;
                var validationResult = await ValidateProfileAsync(importedProfile, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new ProfileManagerException($"Import validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                UserProfile resultProfile;

                switch (mode)
                {
                    case ImportMode.Replace:
                        // Completely replace existing profile;
                        await StoreProfileAsync(importedProfile, cancellationToken);
                        _profileCache[userId] = importedProfile;
                        resultProfile = importedProfile;
                        break;

                    case ImportMode.Merge:
                        // Merge with existing profile;
                        var existingProfile = await GetProfileAsync(userId, true, cancellationToken);
                        if (existingProfile == null)
                        {
                            // Create new if doesn't exist;
                            await StoreProfileAsync(importedProfile, cancellationToken);
                            _profileCache[userId] = importedProfile;
                            resultProfile = importedProfile;
                        }
                        else;
                        {
                            MergeProfiles(existingProfile, importedProfile, MergeStrategy.MergeAll);
                            await StoreProfileAsync(existingProfile, cancellationToken);
                            _profileCache[userId] = existingProfile;
                            resultProfile = existingProfile;
                        }
                        break;

                    case ImportMode.UpdateOnly:
                        // Update only existing fields;
                        var currentProfile = await GetProfileAsync(userId, true, cancellationToken);
                        if (currentProfile == null)
                        {
                            throw new ProfileManagerException($"Profile for user {userId} not found for update");
                        }

                        UpdateProfilePartial(currentProfile, importedProfile);
                        await StoreProfileAsync(currentProfile, cancellationToken);
                        _profileCache[userId] = currentProfile;
                        resultProfile = currentProfile;
                        break;

                    case ImportMode.CreateNew:
                        // Create new profile (fail if exists)
                        var checkProfile = await GetProfileFromStoreAsync(userId, cancellationToken);
                        if (checkProfile != null)
                        {
                            throw new ProfileManagerException($"Profile for user {userId} already exists");
                        }
                        await StoreProfileAsync(importedProfile, cancellationToken);
                        _profileCache[userId] = importedProfile;
                        resultProfile = importedProfile;
                        break;

                    default:
                        throw new NotSupportedException($"Import mode {mode} is not supported");
                }

                _logger.LogInformation("Imported profile for user {UserId} in {Mode} mode", userId, mode);
                return resultProfile;
            }
            finally
            {
                profileLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> AddBiometricProfileAsync(string userId, BiometricProfile biometricProfile,
            CancellationToken cancellationToken = default)
        {
            if (biometricProfile == null)
                throw new ArgumentNullException(nameof(biometricProfile));

            return await UpdateProfileAsync(userId, profile =>
            {
                // Check limit;
                if (profile.BiometricProfiles.Count >= _options.MaxBiometricProfiles)
                {
                    throw new ProfileManagerException($"Maximum biometric profiles ({_options.MaxBiometricProfiles}) reached");
                }

                // Set profile ID if not set;
                if (string.IsNullOrEmpty(biometricProfile.ProfileId))
                {
                    biometricProfile.ProfileId = Guid.NewGuid().ToString();
                }

                biometricProfile.CreatedAt = DateTime.UtcNow;
                biometricProfile.LastUsed = DateTime.UtcNow;

                // Hash biometric data for security;
                biometricProfile.DataHash = HashBiometricData(biometricProfile);

                profile.BiometricProfiles.Add(biometricProfile);

                // Enable biometric authentication if not already enabled;
                if (!profile.SecuritySettings.BiometricEnabled)
                {
                    profile.SecuritySettings.BiometricEnabled = true;
                }

                if (!profile.SecuritySettings.AllowedBiometricTypes.Contains(biometricProfile.Type.ToString()))
                {
                    profile.SecuritySettings.AllowedBiometricTypes.Add(biometricProfile.Type.ToString());
                }
            }, "Added biometric profile", cancellationToken) != null;
        }

        /// <inheritdoc/>
        public async Task<bool> RemoveBiometricProfileAsync(string userId, string biometricProfileId,
            CancellationToken cancellationToken = default)
        {
            return await UpdateProfileAsync(userId, profile =>
            {
                var biometricProfile = profile.BiometricProfiles;
                    .FirstOrDefault(bp => bp.ProfileId == biometricProfileId);

                if (biometricProfile == null)
                {
                    throw new ProfileManagerException($"Biometric profile {biometricProfileId} not found");
                }

                profile.BiometricProfiles.Remove(biometricProfile);

                // Disable biometric authentication if no profiles left;
                if (profile.BiometricProfiles.Count == 0)
                {
                    profile.SecuritySettings.BiometricEnabled = false;
                    profile.SecuritySettings.AllowedBiometricTypes.Clear();
                }
            }, "Removed biometric profile", cancellationToken) != null;
        }

        /// <inheritdoc/>
        public async Task<(bool Verified, double Confidence)> VerifyBiometricAsync(string userId,
            BiometricType type, string biometricData, CancellationToken cancellationToken = default)
        {
            var profile = await GetProfileAsync(userId, true, cancellationToken);

            if (!profile.SecuritySettings.BiometricEnabled)
            {
                return (false, 0.0);
            }

            var biometricProfiles = profile.BiometricProfiles;
                .Where(bp => bp.Type == type)
                .ToList();

            if (!biometricProfiles.Any())
            {
                return (false, 0.0);
            }

            // Hash the provided biometric data;
            var providedHash = ComputeHash(biometricData);

            // Find matching profile with highest confidence;
            var bestMatch = biometricProfiles;
                .OrderByDescending(bp => bp.Confidence)
                .FirstOrDefault(bp => VerifyHashMatch(bp.DataHash, providedHash));

            if (bestMatch != null)
            {
                // Update usage statistics;
                bestMatch.LastUsed = DateTime.UtcNow;
                bestMatch.UsageCount++;

                await UpdateProfileAsync(userId, p =>
                {
                    var existingProfile = p.BiometricProfiles;
                        .FirstOrDefault(bp => bp.ProfileId == bestMatch.ProfileId);

                    if (existingProfile != null)
                    {
                        existingProfile.LastUsed = bestMatch.LastUsed;
                        existingProfile.UsageCount = bestMatch.UsageCount;
                    }
                }, "Biometric verification", cancellationToken);

                return (true, bestMatch.Confidence);
            }

            return (false, 0.0);
        }

        /// <inheritdoc/>
        public async Task<UserProfile> UpdatePreferencesAsync(string userId, string category,
            Dictionary<string, object> preferences, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category cannot be null or empty", nameof(category));
            if (preferences == null || !preferences.Any())
                throw new ArgumentException("Preferences cannot be null or empty", nameof(preferences));

            return await UpdateProfileAsync(userId, profile =>
            {
                if (!profile.ApplicationSettings.ContainsKey(category))
                {
                    profile.ApplicationSettings[category] = new Dictionary<string, object>();
                }

                foreach (var preference in preferences)
                {
                    profile.ApplicationSettings[category][preference.Key] = preference.Value;
                }
            }, $"Updated preferences for category '{category}'", cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<object> GetPreferenceAsync(string userId, string category, string key,
            object defaultValue = null, CancellationToken cancellationToken = default)
        {
            var profile = await GetProfileAsync(userId, false, cancellationToken);

            if (profile.ApplicationSettings.TryGetValue(category, out var categorySettings) &&
                categorySettings.TryGetValue(key, out var value))
            {
                return value;
            }

            return defaultValue;
        }

        /// <inheritdoc/>
        public async Task RecordActivityAsync(string userId, ActivityLog activityLog,
            CancellationToken cancellationToken = default)
        {
            if (!_options.EnableActivityTracking)
                return;

            if (activityLog == null)
                throw new ArgumentNullException(nameof(activityLog));

            await UpdateProfileAsync(userId, profile =>
            {
                activityLog.Timestamp = DateTime.UtcNow;
                profile.UsageStatistics.RecentActivity.Add(activityLog);

                // Limit activity log size;
                if (profile.UsageStatistics.RecentActivity.Count > 1000)
                {
                    profile.UsageStatistics.RecentActivity = profile.UsageStatistics.RecentActivity;
                        .OrderByDescending(a => a.Timestamp)
                        .Take(1000)
                        .ToList();
                }

                // Update usage statistics;
                if (!profile.UsageStatistics.FeatureUsage.ContainsKey(activityLog.Module))
                {
                    profile.UsageStatistics.FeatureUsage[activityLog.Module] = 0;
                }
                profile.UsageStatistics.FeatureUsage[activityLog.Module]++;

            }, $"Recorded activity: {activityLog.Action}", cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<ProfileStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken);

            try
            {
                var statistics = new ProfileStatistics;
                {
                    StatusDistribution = new Dictionary<ProfileStatus, int>(),
                    RoleDistribution = new Dictionary<string, int>(),
                    LanguageDistribution = new Dictionary<string, int>()
                };

                // Get all profiles (simplified - would be optimized in production)
                var criteria = new ProfileSearchCriteria { Take = int.MaxValue };
                var allProfiles = await SearchProfilesAsync(criteria, cancellationToken);
                var profilesList = allProfiles.ToList();

                statistics.TotalProfiles = profilesList.Count;
                statistics.ActiveProfiles = profilesList.Count(p => p.IsActive);

                // Today's new profiles;
                var today = DateTime.UtcNow.Date;
                statistics.NewProfilesToday = profilesList;
                    .Count(p => p.CreatedAt.Date == today);

                if (profilesList.Any())
                {
                    // Calculate average profile age;
                    statistics.AverageProfileAgeDays = profilesList;
                        .Average(p => p.Age.TotalDays);

                    // Count profiles with biometrics and 2FA;
                    statistics.ProfilesWithBiometrics = profilesList;
                        .Count(p => p.BiometricProfiles.Any());
                    statistics.ProfilesWithTwoFactor = profilesList;
                        .Count(p => p.SecuritySettings.TwoFactorEnabled);

                    // Status distribution;
                    foreach (var status in Enum.GetValues(typeof(ProfileStatus)).Cast<ProfileStatus>())
                    {
                        statistics.StatusDistribution[status] = profilesList;
                            .Count(p => p.Status == status);
                    }

                    // Role distribution;
                    foreach (var profile in profilesList)
                    {
                        foreach (var role in profile.Roles)
                        {
                            if (!statistics.RoleDistribution.ContainsKey(role))
                            {
                                statistics.RoleDistribution[role] = 0;
                            }
                            statistics.RoleDistribution[role]++;
                        }
                    }

                    // Language distribution;
                    foreach (var group in profilesList.GroupBy(p => p.PreferredLanguage ?? "Unknown"))
                    {
                        statistics.LanguageDistribution[group.Key] = group.Count();
                    }
                }

                return statistics;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<ProfileManagerHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            var health = new ProfileManagerHealth;
            {
                IsHealthy = true,
                Status = "Healthy",
                Warnings = new List<string>(),
                Errors = new List<string>(),
                Metrics = new Dictionary<string, object>()
            };

            try
            {
                // Check cache health;
                health.CachedProfiles = _profileCache.Count;
                health.TotalProfiles = await GetTotalProfileCountAsync(cancellationToken);

                // Estimate memory usage;
                health.CacheMemoryUsageBytes = EstimateMemoryUsage();

                // Check for stale cache entries;
                var staleCacheCount = _profileCache.Count(p =>
                    p.Value.LastUpdated < DateTime.UtcNow.Add(-_options.CacheTimeout * 2));

                if (staleCacheCount > 0)
                {
                    health.Warnings.Add($"{staleCacheCount} stale entries in cache");
                }

                // Check cache capacity;
                if (_profileCache.Count > _options.MaxProfilesInCache * 0.8)
                {
                    health.Warnings.Add($"Profile cache approaching capacity: {_profileCache.Count}/{_options.MaxProfilesInCache}");
                }

                // Check profile locks;
                health.Metrics["ProfileLocks"] = _profileLocks.Count;
                health.Metrics["OperationLockAvailable"] = _operationLock.CurrentCount;

                // Calculate cache hit ratio;
                health.CacheHitRatio = CalculateCacheHitRatio();

                // Check cleanup timer;
                health.Metrics["CleanupTimerEnabled"] = _cleanupTimer != null;

                return health;
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.Status = "Unhealthy";
                health.Errors.Add($"Health check failed: {ex.Message}");
                return health;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> BackupProfileAsync(string userId, string backupPath = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var profile = await GetProfileAsync(userId, true, cancellationToken);
                var backupData = await ExportProfileAsync(userId, ExportFormat.EncryptedJson, true, cancellationToken);

                // Determine backup path;
                var actualBackupPath = backupPath ?? GetDefaultBackupPath(userId);

                // Ensure directory exists;
                var directory = System.IO.Path.GetDirectoryName(actualBackupPath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // Write backup file;
                await System.IO.File.WriteAllBytesAsync(actualBackupPath, backupData, cancellationToken);

                // Record backup in profile metadata;
                await UpdateProfileAsync(userId, p =>
                {
                    if (!p.Metadata.ContainsKey("Backups"))
                    {
                        p.Metadata["Backups"] = new List<Dictionary<string, object>>();
                    }

                    var backups = p.Metadata["Backups"] as List<Dictionary<string, object>>;
                    backups?.Add(new Dictionary<string, object>
                    {
                        ["Path"] = actualBackupPath,
                        ["Timestamp"] = DateTime.UtcNow,
                        ["Size"] = backupData.Length;
                    });
                }, "Created profile backup", cancellationToken);

                _logger.LogInformation("Created backup for user {UserId} at {BackupPath}",
                    userId, actualBackupPath);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup profile for user {UserId}", userId);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> RestoreProfileAsync(string userId, string backupPath,
            CancellationToken cancellationToken = default)
        {
            if (!System.IO.File.Exists(backupPath))
            {
                throw new ProfileManagerException($"Backup file not found: {backupPath}");
            }

            try
            {
                var backupData = await System.IO.File.ReadAllBytesAsync(backupPath, cancellationToken);
                await ImportProfileAsync(userId, backupData, ImportMode.Replace, cancellationToken);

                _logger.LogInformation("Restored profile for user {UserId} from {BackupPath}",
                    userId, backupPath);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore profile for user {UserId} from {BackupPath}",
                    userId, backupPath);
                return false;
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer?.Dispose();
                _operationLock?.Dispose();

                // Dispose all profile locks;
                foreach (var lockObj in _profileLocks.Values)
                {
                    lockObj?.Dispose();
                }

                _profileLocks.Clear();
                _profileCache.Clear();

                _disposed = true;
            }
        }

        #region Private Helper Methods;

        private async Task<UserProfile> GetProfileFromStoreAsync(string userId, CancellationToken cancellationToken)
        {
            // Load from database (simplified)
            // Actual implementation would query database;

            // For now, return from cache or null;
            _profileCache.TryGetValue(userId, out var profile);
            return profile;
        }

        private async Task StoreProfileAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            // Serialize profile data;
            var profileData = SerializeProfileData(profile);

            // Store in database (simplified)
            // Actual implementation would use proper database operations;

            // Update cache;
            _profileCache[profile.UserId] = profile;
        }

        private async Task DeleteProfileFromStoreAsync(string userId, CancellationToken cancellationToken)
        {
            // Remove from database (simplified)
            // Actual implementation would delete from database;

            // Remove from cache;
            _profileCache.TryRemove(userId, out _);

            // Dispose and remove lock;
            if (_profileLocks.TryRemove(userId, out var lockObj))
            {
                lockObj.Dispose();
            }
        }

        private void ApplyInitialData(UserProfile profile, Dictionary<string, object> initialData)
        {
            foreach (var item in initialData)
            {
                ApplyFieldUpdate(profile, item.Key, item.Value);
            }
        }

        private void ApplyFieldUpdate(UserProfile profile, string fieldPath, object value)
        {
            // Support nested field paths (e.g., "ContactInfo.Phone")
            var parts = fieldPath.Split('.');
            object current = profile;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                var property = current.GetType().GetProperty(part);
                if (property == null)
                {
                    throw new ProfileManagerException($"Invalid field path: {fieldPath}");
                }
                current = property.GetValue(current);

                if (current == null)
                {
                    // Create nested object if it doesn't exist;
                    var propertyType = property.PropertyType;
                    current = Activator.CreateInstance(propertyType);
                    property.SetValue(current, current);
                }
            }

            var lastPart = parts[parts.Length - 1];
            var lastProperty = current.GetType().GetProperty(lastPart);
            if (lastProperty == null)
            {
                throw new ProfileManagerException($"Invalid field path: {fieldPath}");
            }

            // Convert value to appropriate type;
            var convertedValue = Convert.ChangeType(value, lastProperty.PropertyType);
            lastProperty.SetValue(current, convertedValue);
        }

        private UserProfile SanitizeProfile(UserProfile profile)
        {
            // Create a sanitized copy for public consumption;
            var sanitized = CloneProfile(profile);

            // Remove or mask sensitive data;
            sanitized.ContactInfo = new ContactInfo(); // Clear contact info;
            sanitized.SecuritySettings = new SecuritySettings // Clear security details;
            {
                TwoFactorEnabled = profile.SecuritySettings.TwoFactorEnabled,
                BiometricEnabled = profile.SecuritySettings.BiometricEnabled;
            };

            // Remove biometric data;
            sanitized.BiometricProfiles = new List<BiometricProfile>();

            // Clear some metadata;
            sanitized.Metadata = new Dictionary<string, object>();

            return sanitized;
        }

        private UserProfile CloneProfile(UserProfile original)
        {
            // Simple clone via serialization (for demonstration)
            var json = System.Text.Json.JsonSerializer.Serialize(original);
            return System.Text.Json.JsonSerializer.Deserialize<UserProfile>(json);
        }

        private async Task RecordProfileUpdateAsync(string userId, UserProfile oldProfile,
            UserProfile newProfile, string reason, CancellationToken cancellationToken)
        {
            if (!_options.EnableAuditLogging)
                return;

            // Compare profiles and record changes;
            var changes = CompareProfiles(oldProfile, newProfile);

            // Store update history (simplified)
            // Actual implementation would store in database;

            // Log detailed changes;
            if (changes.Any())
            {
                _logger.LogDebug("Profile {UserId} updated: {Changes}", userId,
                    string.Join(", ", changes.Select(c => $"{c.Field}: {c.OldValue} -> {c.NewValue}")));
            }
        }

        private List<ProfileUpdate> CompareProfiles(UserProfile oldProfile, UserProfile newProfile)
        {
            var changes = new List<ProfileUpdate>();

            // Compare simple properties;
            var properties = typeof(UserProfile).GetProperties()
                .Where(p => p.CanRead && p.CanWrite && !p.PropertyType.IsClass)
                .ToList();

            foreach (var property in properties)
            {
                var oldValue = property.GetValue(oldProfile);
                var newValue = property.GetValue(newProfile);

                if (!Equals(oldValue, newValue))
                {
                    changes.Add(new ProfileUpdate;
                    {
                        Field = property.Name,
                        OldValue = oldValue,
                        NewValue = newValue,
                        Reason = "Property changed"
                    });
                }
            }

            return changes;
        }

        private string BuildSearchQuery(ProfileSearchCriteria criteria, out List<KeyValuePair<string, object>> parameters)
        {
            parameters = new List<KeyValuePair<string, object>>();
            var conditions = new List<string>();

            if (!string.IsNullOrEmpty(criteria.Username))
            {
                conditions.Add("Username LIKE @Username");
                parameters.Add(new KeyValuePair<string, object>("@Username", $"%{criteria.Username}%"));
            }

            if (!string.IsNullOrEmpty(criteria.Email))
            {
                conditions.Add("Email LIKE @Email");
                parameters.Add(new KeyValuePair<string, object>("@Email", $"%{criteria.Email}%"));
            }

            if (!string.IsNullOrEmpty(criteria.FullName))
            {
                conditions.Add("FullName LIKE @FullName");
                parameters.Add(new KeyValuePair<string, object>("@FullName", $"%{criteria.FullName}%"));
            }

            if (criteria.Status.HasValue)
            {
                conditions.Add("Status = @Status");
                parameters.Add(new KeyValuePair<string, object>("@Status", (int)criteria.Status.Value));
            }

            if (!string.IsNullOrEmpty(criteria.Role))
            {
                conditions.Add("Roles LIKE @Role");
                parameters.Add(new KeyValuePair<string, object>("@Role", $"%{criteria.Role}%"));
            }

            if (!string.IsNullOrEmpty(criteria.Permission))
            {
                conditions.Add("Permissions LIKE @Permission");
                parameters.Add(new KeyValuePair<string, object>("@Permission", $"%{criteria.Permission}%"));
            }

            if (criteria.CreatedAfter.HasValue)
            {
                conditions.Add("CreatedAt >= @CreatedAfter");
                parameters.Add(new KeyValuePair<string, object>("@CreatedAfter", criteria.CreatedAfter.Value));
            }

            if (criteria.CreatedBefore.HasValue)
            {
                conditions.Add("CreatedAt <= @CreatedBefore");
                parameters.Add(new KeyValuePair<string, object>("@CreatedBefore", criteria.CreatedBefore.Value));
            }

            if (criteria.LastLoginAfter.HasValue)
            {
                conditions.Add("LastLogin >= @LastLoginAfter");
                parameters.Add(new KeyValuePair<string, object>("@LastLoginAfter", criteria.LastLoginAfter.Value));
            }

            if (criteria.LastLoginBefore.HasValue)
            {
                conditions.Add("LastLogin <= @LastLoginBefore");
                parameters.Add(new KeyValuePair<string, object>("@LastLoginBefore", criteria.LastLoginBefore.Value));
            }

            if (!string.IsNullOrEmpty(criteria.Tag))
            {
                conditions.Add("Tags LIKE @Tag");
                parameters.Add(new KeyValuePair<string, object>("@Tag", $"%{criteria.Tag}%"));
            }

            var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
            var orderBy = criteria.OrderBy ?? "LastUpdated";
            var orderDirection = criteria.Descending ? "DESC" : "ASC";

            return $@"
                SELECT UserId FROM {ProfileTableName}
                {whereClause}
                ORDER BY {orderBy} {orderDirection}
                LIMIT {criteria.Take + criteria.Skip}";
        }

        private async Task<IEnumerable<string>> ExecuteSearchQueryAsync(string query,
            List<KeyValuePair<string, object>> parameters, CancellationToken cancellationToken)
        {
            // Simplified implementation;
            // Actual implementation would execute database query;

            return _profileCache.Keys.Take(100);
        }

        private List<UserProfile> ApplyOrdering(List<UserProfile> profiles, string orderBy, bool descending)
        {
            var ordered = orderBy?.ToLower() switch;
            {
                "username" => descending ?
                    profiles.OrderByDescending(p => p.Username) :
                    profiles.OrderBy(p => p.Username),
                "email" => descending ?
                    profiles.OrderByDescending(p => p.Email) :
                    profiles.OrderBy(p => p.Email),
                "createdat" => descending ?
                    profiles.OrderByDescending(p => p.CreatedAt) :
                    profiles.OrderBy(p => p.CreatedAt),
                "lastlogin" => descending ?
                    profiles.OrderByDescending(p => p.LastLogin) :
                    profiles.OrderBy(p => p.LastLogin),
                _ => descending ?
                    profiles.OrderByDescending(p => p.LastUpdated) :
                    profiles.OrderBy(p => p.LastUpdated)
            };

            return ordered.ToList();
        }

        private object GetFieldValue(UserProfile profile, string fieldPath)
        {
            var parts = fieldPath.Split('.');
            object current = profile;

            foreach (var part in parts)
            {
                var property = current.GetType().GetProperty(part);
                if (property == null)
                {
                    return null;
                }
                current = property.GetValue(current);

                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        private UserProfile MergeProfiles(UserProfile primary, UserProfile secondary, MergeStrategy strategy)
        {
            var merged = CloneProfile(primary);

            switch (strategy)
            {
                case MergeStrategy.KeepPrimary:
                    // Keep primary as-is;
                    break;

                case MergeStrategy.KeepSecondary:
                    // Replace primary with secondary (except UserId)
                    merged = CloneProfile(secondary);
                    merged.UserId = primary.UserId;
                    break;

                case MergeStrategy.MergeAll:
                    // Merge all fields intelligently;
                    merged.FullName = string.IsNullOrEmpty(merged.FullName) ? secondary.FullName : merged.FullName;
                    merged.Email = string.IsNullOrEmpty(merged.Email) ? secondary.Email : merged.Email;

                    // Merge preferences;
                    foreach (var pref in secondary.Preferences)
                    {
                        if (!merged.Preferences.ContainsKey(pref.Key))
                        {
                            merged.Preferences[pref.Key] = pref.Value;
                        }
                    }

                    // Merge application settings;
                    foreach (var appSettings in secondary.ApplicationSettings)
                    {
                        if (!merged.ApplicationSettings.ContainsKey(appSettings.Key))
                        {
                            merged.ApplicationSettings[appSettings.Key] = appSettings.Value;
                        }
                        else;
                        {
                            foreach (var setting in appSettings.Value)
                            {
                                if (!merged.ApplicationSettings[appSettings.Key].ContainsKey(setting.Key))
                                {
                                    merged.ApplicationSettings[appSettings.Key][setting.Key] = setting.Value;
                                }
                            }
                        }
                    }

                    // Merge biometric profiles;
                    merged.BiometricProfiles.AddRange(secondary.BiometricProfiles);

                    // Merge roles and permissions;
                    merged.Roles = merged.Roles.Union(secondary.Roles).Distinct().ToList();
                    merged.Permissions = merged.Permissions.Union(secondary.Permissions).Distinct().ToList();
                    break;

                case MergeStrategy.Interactive:
                    // This would require user interaction - for now, use MergeAll;
                    return MergeProfiles(primary, secondary, MergeStrategy.MergeAll);
            }

            merged.LastUpdated = DateTime.UtcNow;
            merged.Version++;
            merged.Tags.Add("merged");
            merged.Metadata["MergedFrom"] = secondary.UserId;
            merged.Metadata["MergedAt"] = DateTime.UtcNow;
            merged.Metadata["MergeStrategy"] = strategy.ToString();

            return merged;
        }

        private object PrepareExportData(UserProfile profile, bool includeSensitive)
        {
            var exportProfile = includeSensitive ? profile : SanitizeProfile(profile);

            return new;
            {
                exportProfile.UserId,
                exportProfile.Username,
                exportProfile.FullName,
                exportProfile.Email,
                exportProfile.CreatedAt,
                exportProfile.LastUpdated,
                exportProfile.Timezone,
                exportProfile.PreferredLanguage,
                exportProfile.Locale,
                Status = exportProfile.Status.ToString(),
                exportProfile.Roles,
                exportProfile.Permissions,
                exportProfile.Preferences,
                ApplicationSettings = exportProfile.ApplicationSettings,
                ContactInfo = includeSensitive ? exportProfile.ContactInfo : null,
                ProfessionalInfo = exportProfile.ProfessionalInfo,
                BiometricProfiles = includeSensitive ? exportProfile.BiometricProfiles.Select(bp => new;
                {
                    bp.Type,
                    bp.CreatedAt,
                    bp.LastUsed,
                    bp.UsageCount;
                }) : Enumerable.Empty<object>(),
                SecuritySettings = includeSensitive ? new;
                {
                    exportProfile.SecuritySettings.TwoFactorEnabled,
                    exportProfile.SecuritySettings.BiometricEnabled;
                } : null,
                UsageStatistics = exportProfile.UsageStatistics,
                exportProfile.Tags,
                exportProfile.Version,
                ExportDate = DateTime.UtcNow,
                ExportFormat = includeSensitive ? "Full" : "Sanitized"
            };
        }

        private async Task<byte[]> ExportGDPRDataAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            // Prepare GDPR-compliant export;
            var gdprData = new;
            {
                profile.UserId,
                profile.Username,
                profile.FullName,
                profile.Email,
                profile.CreatedAt,
                profile.LastUpdated,
                profile.PreferredLanguage,
                Status = profile.Status.ToString(),
                ContactInfo = new;
                {
                    profile.ContactInfo.Country
                },
                DataCategories = new[]
                {
                    "Personal Information",
                    "Contact Information",
                    "Preferences",
                    "Activity Logs"
                },
                ExportDate = DateTime.UtcNow,
                ExportPurpose = "GDPR Data Subject Access Request"
            };

            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(gdprData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        private UserProfile DeserializeProfileData(byte[] data)
        {
            try
            {
                // Try JSON first;
                var json = System.Text.Encoding.UTF8.GetString(data);
                return System.Text.Json.JsonSerializer.Deserialize<UserProfile>(json);
            }
            catch
            {
                // Try decryption if it might be encrypted;
                try
                {
                    var decryptedData = _cryptoEngine.DecryptAsync(data, CancellationToken.None).Result;
                    var json = System.Text.Encoding.UTF8.GetString(decryptedData);
                    return System.Text.Json.JsonSerializer.Deserialize<UserProfile>(json);
                }
                catch
                {
                    throw new ProfileManagerException("Unsupported profile data format");
                }
            }
        }

        private byte[] SerializeProfileData(UserProfile profile)
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(profile);
        }

        private void UpdateProfilePartial(UserProfile target, UserProfile source)
        {
            // Update only non-null properties from source;
            if (!string.IsNullOrEmpty(source.Username))
                target.Username = source.Username;

            if (!string.IsNullOrEmpty(source.FullName))
                target.FullName = source.FullName;

            if (!string.IsNullOrEmpty(source.Email))
                target.Email = source.Email;

            if (!string.IsNullOrEmpty(source.Timezone))
                target.Timezone = source.Timezone;

            if (!string.IsNullOrEmpty(source.PreferredLanguage))
                target.PreferredLanguage = source.PreferredLanguage;

            if (!string.IsNullOrEmpty(source.Locale))
                target.Locale = source.Locale;

            // Update preferences;
            foreach (var pref in source.Preferences)
            {
                target.Preferences[pref.Key] = pref.Value;
            }

            target.LastUpdated = DateTime.UtcNow;
            target.Version++;
        }

        private void ArchiveSensitiveData(UserProfile profile)
        {
            // Archive sensitive data before deletion;
            var archive = new;
            {
                profile.Email,
                profile.ContactInfo,
                profile.BiometricProfiles,
                ArchivedAt = DateTime.UtcNow;
            };

            // Store archive in metadata;
            profile.Metadata["ArchivedData"] = archive;

            // Clear sensitive data;
            profile.Email = null;
            profile.ContactInfo = new ContactInfo();
            profile.BiometricProfiles = new List<BiometricProfile>();
        }

        private string HashBiometricData(BiometricProfile biometricProfile)
        {
            // Create a hash of biometric data for security;
            var dataToHash = $"{biometricProfile.Type}:{biometricProfile.DataHash}:{DateTime.UtcNow.Ticks}";
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dataToHash));
            return Convert.ToBase64String(hashBytes);
        }

        private string ComputeHash(string data)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hashBytes);
        }

        private bool VerifyHashMatch(string storedHash, string providedHash)
        {
            // Constant-time comparison to prevent timing attacks;
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(storedHash),
                System.Text.Encoding.UTF8.GetBytes(providedHash));
        }

        private string GetDefaultBackupPath(string userId)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "Backups",
                $"profile_{userId}_{timestamp}.backup");
        }

        private async Task<int> GetTotalProfileCountAsync(CancellationToken cancellationToken)
        {
            // Simplified implementation;
            return _profileCache.Count;
        }

        private long EstimateMemoryUsage()
        {
            long total = 0;

            foreach (var profile in _profileCache.Values)
            {
                // Rough estimation;
                total += 500; // Base overhead;
                total += (profile.Username?.Length ?? 0) * 2;
                total += (profile.Email?.Length ?? 0) * 2;
                total += profile.Preferences.Count * 100;
                total += profile.BiometricProfiles.Count * 200;
            }

            return total;
        }

        private double CalculateCacheHitRatio()
        {
            // Simplified calculation;
            // Actual implementation would track hits and misses;
            return 0.92;
        }

        private async void CleanupCacheCallback(object state)
        {
            try
            {
                await CleanupStaleCacheEntriesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cache cleanup timer");
            }
        }

        private async Task CleanupStaleCacheEntriesAsync(CancellationToken cancellationToken)
        {
            var staleEntries = _profileCache;
                .Where(kvp => kvp.Value.LastUpdated < DateTime.UtcNow.Add(-_options.CacheTimeout * 2))
                .ToList();

            foreach (var entry in staleEntries)
            {
                _profileCache.TryRemove(entry.Key, out _);
            }

            if (staleEntries.Count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} stale cache entries", staleEntries.Count);
            }
        }

        #endregion;
    }

    /// <summary>
    /// Audit entry for tracking profile operations;
    /// </summary>
    internal class AuditEntry
    {
        public string Action { get; set; }
        public string EntityId { get; set; }
        public string EntityType { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Custom exception for ProfileManager operations;
    /// </summary>
    public class ProfileManagerException : Exception
    {
        public ProfileManagerException() { }
        public ProfileManagerException(string message) : base(message) { }
        public ProfileManagerException(string message, Exception innerException) : base(message, innerException) { }
    }
}
