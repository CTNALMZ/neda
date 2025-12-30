using Microsoft.Extensions.Caching.Memory;
using NEDA.API.ClientSDK;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.UserProfiles;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.Core.Security.AccessControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.Security
{
    /// <summary>
    /// Advanced permission validation system with caching, auditing, and real-time policy evaluation;
    /// Supports role-based, claim-based, and resource-based authorization;
    /// </summary>
    public class PermissionValidator : IPermissionValidator, IDisposable
    {
        #region Fields and Properties

        private readonly ILogger _logger;
        private readonly AccessControlManager _accessControl;
        private readonly IMemoryCache _cache;
        private readonly SettingsManager _settingsManager;
        private readonly ConcurrentDictionary<string, PermissionPolicy> _policies;
        private readonly SemaphoreSlim _validationSemaphore;
        private readonly Timer _cacheCleanupTimer;
        private bool _disposed;

        // FIX: IMemoryCache key enumeration yok; kendi key index’imizi tutuyoruz
        private readonly ConcurrentDictionary<string, byte> _cacheKeyIndex = new();

        // Performance counters;
        private long _totalValidations;
        private long _failedValidations;
        private long _cacheHits;

        // Constants;
        private const string CACHE_PREFIX = "PERM_";
        private const int DEFAULT_CACHE_DURATION = 300; // 5 minutes;
        private const int MAX_CONCURRENT_VALIDATIONS = 100;

        #endregion

        #region Nested Types

        /// <summary>
        /// Permission validation result with detailed information;
        /// </summary>
        public class ValidationResult
        {
            public bool IsAllowed { get; set; }
            public string Permission { get; set; } = string.Empty;
            public string? Resource { get; set; }
            public string UserId { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> Context { get; set; }
            public TimeSpan ProcessingTime { get; set; }
            public List<string> AppliedPolicies { get; set; }

            public ValidationResult()
            {
                Timestamp = DateTime.UtcNow;
                Context = new Dictionary<string, object>();
                AppliedPolicies = new List<string>();
            }
        }

        /// <summary>
        /// Permission policy definition;
        /// </summary>
        public class PermissionPolicy
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public PermissionEffect Effect { get; set; }
            public List<string> RequiredRoles { get; set; }
            public List<string> RequiredClaims { get; set; }
            public Dictionary<string, string> RequiredConditions { get; set; }
            public TimeSpan? Expiration { get; set; }
            public int Priority { get; set; }
            public bool IsEnabled { get; set; }

            public PermissionPolicy()
            {
                RequiredRoles = new List<string>();
                RequiredClaims = new List<string>();
                RequiredConditions = new Dictionary<string, string>();
                IsEnabled = true;
                Priority = 100;
            }
        }

        /// <summary>
        /// Permission validation context;
        /// </summary>
        public class ValidationContext
        {
            public IPrincipal User { get; set; } = new ClaimsPrincipal();
            public string? Resource { get; set; }
            public string Action { get; set; } = string.Empty;
            public Dictionary<string, object> Parameters { get; set; }
            public ClaimsIdentity? ClaimsIdentity { get; set; }
            public UserProfile? UserProfile { get; set; }
            public string ClientIp { get; set; } = string.Empty;
            public DateTime RequestTime { get; set; }

            public ValidationContext()
            {
                Parameters = new Dictionary<string, object>();
                RequestTime = DateTime.UtcNow;
            }
        }

        public enum PermissionEffect
        {
            Allow,
            Deny,
            RequireApproval,
            Conditional
        }

        public enum ValidationMode
        {
            Strict,
            Relaxed,
            Adaptive
        }

        #endregion

        #region Constructor and Initialization

        public PermissionValidator(
            ILogger logger,
            AccessControlManager accessControl,
            SettingsManager settingsManager,
            IMemoryCache? memoryCache = null)
        {
            _logger = logger ?? Logger.CreateLogger<PermissionValidator>();
            _accessControl = accessControl ?? throw new ArgumentNullException(nameof(accessControl));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

            _cache = memoryCache ?? new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = 10000,
                ExpirationScanFrequency = TimeSpan.FromMinutes(5)
            });

            _policies = new ConcurrentDictionary<string, PermissionPolicy>();
            _validationSemaphore = new SemaphoreSlim(MAX_CONCURRENT_VALIDATIONS);

            _cacheCleanupTimer = new Timer(CleanupCache, null,
                TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

            LoadDefaultPolicies();
            InitializeSystemPermissions();

            _logger.LogInformation("PermissionValidator initialized successfully");
        }

        private void LoadDefaultPolicies()
        {
            try
            {
                AddPolicy(new PermissionPolicy
                {
                    Name = "SystemAdminFullAccess",
                    Description = "Full system administration access",
                    Effect = PermissionEffect.Allow,
                    RequiredRoles = new List<string> { "SystemAdministrator", "SuperUser" },
                    Priority = 1000
                });

                AddPolicy(new PermissionPolicy
                {
                    Name = "ResourceOwnerAccess",
                    Description = "Access to owned resources",
                    Effect = PermissionEffect.Allow,
                    RequiredClaims = new List<string> { "ResourceOwner" },
                    Priority = 500
                });

                AddPolicy(new PermissionPolicy
                {
                    Name = "AuditLogAccess",
                    Description = "Access to audit logs",
                    Effect = PermissionEffect.Allow,
                    RequiredRoles = new List<string> { "Auditor", "SystemAdministrator" },
                    RequiredClaims = new List<string> { "CanViewAuditLogs" },
                    Priority = 300
                });

                AddPolicy(new PermissionPolicy
                {
                    Name = "EmergencyOverride",
                    Description = "Emergency system override",
                    Effect = PermissionEffect.Allow,
                    RequiredConditions = new Dictionary<string, string>
                    {
                        { "IsEmergency", "true" },
                        { "ApprovalLevel", "Executive" }
                    },
                    Expiration = TimeSpan.FromHours(1),
                    Priority = 2000
                });

                _logger.LogInformation("Loaded {PolicyCount} default permission policies", _policies.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load default permission policies");
                throw new SecurityException(ErrorCodes.Security.POLICY_LOAD_FAILED,
                    "Failed to initialize permission policies", ex);
            }
        }

        private void InitializeSystemPermissions()
        {
            try
            {
                var systemPermissions = new[]
                {
                    "System.Shutdown",
                    "System.Restart",
                    "System.Configure",
                    "System.Monitor",
                    "System.Backup",
                    "System.Restore",
                    "User.Create",
                    "User.Delete",
                    "User.Modify",
                    "Role.Manage",
                    "Permission.Grant",
                    "Permission.Revoke",
                    "Audit.View",
                    "Audit.Export",
                    "Log.View",
                    "Log.Clear",
                    "Configuration.Read",
                    "Configuration.Write",
                    "Database.Access",
                    "Network.Configure"
                };

                foreach (var permission in systemPermissions)
                {
                    _accessControl.RegisterPermission(permission, $"System permission: {permission}");
                }

                _logger.LogInformation("Initialized {Count} system permissions", systemPermissions.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize system permissions");
                throw new SecurityException(ErrorCodes.Security.PERMISSION_INIT_FAILED,
                    "Failed to initialize system permissions", ex);
            }
        }

        #endregion

        #region Public API Methods

        public async Task<ValidationResult> ValidatePermissionAsync(
            IPrincipal user,
            string permission,
            string? resource = null,
            Dictionary<string, object>? context = null)
        {
            if (user == null)
                throw new ArgumentNullException(nameof(user));

            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Permission cannot be null or empty", nameof(permission));

            var startTime = DateTime.UtcNow;
            await _validationSemaphore.WaitAsync();

            try
            {
                Interlocked.Increment(ref _totalValidations);

                var validationContext = CreateValidationContext(user, permission, resource, context);
                var cacheKey = GenerateCacheKey(validationContext);

                if (_cache.TryGetValue(cacheKey, out ValidationResult cachedResult))
                {
                    Interlocked.Increment(ref _cacheHits);
                    cachedResult.ProcessingTime = DateTime.UtcNow - startTime;
                    return cachedResult;
                }

                var result = await PerformValidationAsync(validationContext);
                result.ProcessingTime = DateTime.UtcNow - startTime;

                CacheValidationResult(cacheKey, result);

                await AuditValidationAsync(result);

                if (!result.IsAllowed)
                {
                    Interlocked.Increment(ref _failedValidations);
                    _logger.LogWarning("Permission denied: {Permission} for user {UserId} on resource {Resource}",
                        permission, result.UserId, resource);
                }

                return result;
            }
            finally
            {
                _validationSemaphore.Release();
            }
        }

        public async Task<Dictionary<string, ValidationResult>> ValidatePermissionsAsync(
            IPrincipal user,
            IEnumerable<string> permissions,
            string? resource = null)
        {
            if (permissions == null)
                throw new ArgumentNullException(nameof(permissions));

            var permissionList = permissions as IList<string> ?? permissions.ToList();

            var tasks = permissionList.Select(p => ValidatePermissionAsync(user, p, resource));
            var results = await Task.WhenAll(tasks);

            return permissionList.Zip(results, (p, r) => new { Permission = p, Result = r })
                .ToDictionary(x => x.Permission, x => x.Result);
        }

        public async Task<bool> HasAnyPermissionAsync(
            IPrincipal user,
            IEnumerable<string> permissions,
            string? resource = null)
        {
            var results = await ValidatePermissionsAsync(user, permissions, resource);
            return results.Values.Any(r => r.IsAllowed);
        }

        public async Task<bool> HasAllPermissionsAsync(
            IPrincipal user,
            IEnumerable<string> permissions,
            string? resource = null)
        {
            var results = await ValidatePermissionsAsync(user, permissions, resource);
            return results.Values.All(r => r.IsAllowed);
        }

        public bool AddPolicy(PermissionPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            if (string.IsNullOrWhiteSpace(policy.Name))
                throw new ArgumentException("Policy name cannot be null or empty");

            if (!CanModifyPolicies())
            {
                _logger.LogWarning("Unauthorized attempt to add policy: {PolicyName}", policy.Name);
                throw new SecurityException(ErrorCodes.Security.UNAUTHORIZED_POLICY_MODIFICATION,
                    "Insufficient permissions to modify policies");
            }

            var added = _policies.TryAdd(policy.Name, policy);

            if (added)
            {
                _logger.LogInformation("Added permission policy: {PolicyName}", policy.Name);
                InvalidatePolicyCache();
            }

            return added;
        }

        public bool RemovePolicy(string policyName)
        {
            if (!CanModifyPolicies())
            {
                _logger.LogWarning("Unauthorized attempt to remove policy: {PolicyName}", policyName);
                throw new SecurityException(ErrorCodes.Security.UNAUTHORIZED_POLICY_MODIFICATION,
                    "Insufficient permissions to modify policies");
            }

            var removed = _policies.TryRemove(policyName, out _);

            if (removed)
            {
                _logger.LogInformation("Removed permission policy: {PolicyName}", policyName);
                InvalidatePolicyCache();
            }

            return removed;
        }

        public async Task<bool> EvaluateConditionAsync(string conditionName, ValidationContext context)
        {
            if (string.IsNullOrWhiteSpace(conditionName))
                throw new ArgumentException("Condition name cannot be null or empty", nameof(conditionName));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                switch (conditionName.ToLowerInvariant())
                {
                    case "businesshours":
                        return IsBusinessHours();

                    case "highsecurity":
                        return await IsHighSecurityModeAsync();

                    case "emergency":
                        return await IsEmergencyModeAsync();

                    case "ipwhitelisted":
                        return IsIpWhitelisted(context.ClientIp);

                    case "mfaenabled":
                        return await IsMfaEnabledAsync(context.User);

                    case "deviceregistered":
                        return await IsDeviceRegisteredAsync(context.User);

                    case "sessionfresh":
                        return IsSessionFresh(context.RequestTime);

                    case "lowrisk":
                        return await IsLowRiskOperationAsync(context);

                    default:
                        return await EvaluateCustomConditionAsync(conditionName, context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate condition: {ConditionName}", conditionName);
                return false;
            }
        }

        public ValidationStatistics GetStatistics()
        {
            return new ValidationStatistics
            {
                TotalValidations = Interlocked.Read(ref _totalValidations),
                FailedValidations = Interlocked.Read(ref _failedValidations),
                CacheHits = Interlocked.Read(ref _cacheHits),
                CacheSize = _cacheKeyIndex.Count,
                ActivePolicies = _policies.Count,
                ConcurrentValidations = MAX_CONCURRENT_VALIDATIONS - _validationSemaphore.CurrentCount
            };
        }

        public void ClearCache()
        {
            foreach (var key in _cacheKeyIndex.Keys.ToList())
            {
                _cache.Remove(key);
            }
            _cacheKeyIndex.Clear();

            _logger.LogInformation("Permission cache cleared");
        }

        #endregion

        #region Core Validation Logic

        private async Task<ValidationResult> PerformValidationAsync(ValidationContext context)
        {
            var result = new ValidationResult
            {
                Permission = context.Action,
                Resource = context.Resource,
                UserId = context.User?.Identity?.Name ?? "Unknown"
            };

            try
            {
                var denyPolicies = GetApplicablePolicies(context, PermissionEffect.Deny);
                if (denyPolicies.Any())
                {
                    result.IsAllowed = false;
                    result.Reason = "Explicitly denied by policy";
                    result.AppliedPolicies.AddRange(denyPolicies.Select(p => p.Name));
                    return result;
                }

                if (await IsEmergencyModeAsync())
                {
                    var emergencyPolicy = GetEmergencyOverridePolicy();
                    if (emergencyPolicy != null && EvaluatePolicy(emergencyPolicy, context))
                    {
                        result.IsAllowed = true;
                        result.Reason = "Emergency override granted";
                        result.AppliedPolicies.Add(emergencyPolicy.Name);
                        return result;
                    }
                }

                var allowPolicies = GetApplicablePolicies(context, PermissionEffect.Allow);
                if (allowPolicies.Any())
                {
                    foreach (var policy in allowPolicies.OrderByDescending(p => p.Priority))
                    {
                        if (await EvaluatePolicyAsync(policy, context))
                        {
                            result.IsAllowed = true;
                            result.Reason = $"Allowed by policy: {policy.Name}";
                            result.AppliedPolicies.Add(policy.Name);
                            return result;
                        }
                    }
                }

                var conditionalPolicies = GetApplicablePolicies(context, PermissionEffect.Conditional);
                foreach (var policy in conditionalPolicies.OrderByDescending(p => p.Priority))
                {
                    var conditionResult = await EvaluateConditionalPolicyAsync(policy, context);
                    if (conditionResult.HasValue)
                    {
                        result.IsAllowed = conditionResult.Value;
                        result.Reason = conditionResult.Value
                            ? $"Conditionally allowed by {policy.Name}"
                            : $"Conditionally denied by {policy.Name}";
                        result.AppliedPolicies.Add(policy.Name);
                        return result;
                    }
                }

                result.IsAllowed = false;
                result.Reason = "No matching permission policy found";
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Permission validation failed for user {UserId}, permission {Permission}",
                    result.UserId, result.Permission);

                result.IsAllowed = false;
                result.Reason = $"Validation error: {ex.Message}";
                return result;
            }
        }

        private IEnumerable<PermissionPolicy> GetApplicablePolicies(
            ValidationContext context,
            PermissionEffect? effect = null)
        {
            return _policies.Values
                .Where(p => p.IsEnabled &&
                            (effect == null || p.Effect == effect) &&
                            IsPolicyApplicable(p, context))
                .OrderByDescending(p => p.Priority);
        }

        private bool IsPolicyApplicable(PermissionPolicy policy, ValidationContext context)
        {
            if (!string.IsNullOrEmpty(context.Resource) &&
                policy.RequiredConditions.ContainsKey("ResourcePattern"))
            {
                var pattern = policy.RequiredConditions["ResourcePattern"];
                if (!ResourceMatchesPattern(context.Resource, pattern))
                    return false;
            }

            return true;
        }

        private async Task<bool> EvaluatePolicyAsync(PermissionPolicy policy, ValidationContext context)
        {
            if (policy.RequiredRoles.Any())
            {
                var hasRequiredRole = policy.RequiredRoles.Any(role =>
                    context.User?.IsInRole(role) == true);

                if (!hasRequiredRole)
                    return false;
            }

            if (policy.RequiredClaims.Any() && context.ClaimsIdentity != null)
            {
                var hasRequiredClaims = policy.RequiredClaims.All(claimType =>
                    context.ClaimsIdentity.HasClaim(c => c.Type == claimType));

                if (!hasRequiredClaims)
                    return false;
            }

            if (policy.RequiredConditions.Any())
            {
                foreach (var condition in policy.RequiredConditions)
                {
                    var conditionMet = await EvaluateConditionAsync(condition.Key, context);
                    if (!conditionMet)
                        return false;
                }
            }

            return true;
        }

        private async Task<bool?> EvaluateConditionalPolicyAsync(PermissionPolicy policy, ValidationContext context)
        {
            var conditionResults = new Dictionary<string, bool>();

            foreach (var condition in policy.RequiredConditions)
            {
                conditionResults[condition.Key] = await EvaluateConditionAsync(condition.Key, context);
            }

            if (conditionResults.All(kv => kv.Value))
                return true;

            if (conditionResults.Any(kv => kv.Key.StartsWith("Require", StringComparison.OrdinalIgnoreCase) && !kv.Value))
                return false;

            return null;
        }

        private bool EvaluatePolicy(PermissionPolicy policy, ValidationContext context)
        {
            return EvaluatePolicyAsync(policy, context).GetAwaiter().GetResult();
        }

        #endregion

        #region Condition Evaluation Methods

        private bool IsBusinessHours()
        {
            var now = DateTime.UtcNow;
            var businessStart = new TimeSpan(9, 0, 0);
            var businessEnd = new TimeSpan(17, 0, 0);

            return now.DayOfWeek >= DayOfWeek.Monday &&
                   now.DayOfWeek <= DayOfWeek.Friday &&
                   now.TimeOfDay >= businessStart &&
                   now.TimeOfDay <= businessEnd;
        }

        private async Task<bool> IsHighSecurityModeAsync()
        {
            var settings = await _settingsManager.GetSecuritySettingsAsync();
            return settings?.HighSecurityMode ?? false;
        }

        private async Task<bool> IsEmergencyModeAsync()
        {
            var settings = await _settingsManager.GetSecuritySettingsAsync();
            return settings?.EmergencyMode ?? false;
        }

        private bool IsIpWhitelisted(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return false;

            var whitelist = _settingsManager.GetIpWhitelist();
            return whitelist?.Contains(ipAddress) ?? false;
        }

        private async Task<bool> IsMfaEnabledAsync(IPrincipal user)
        {
            var userId = user?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return false;

            var profile = await _settingsManager.GetUserProfileAsync(userId);
            return profile?.MfaEnabled ?? false;
        }

        private async Task<bool> IsDeviceRegisteredAsync(IPrincipal user)
        {
            var userId = user?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return false;

            var devices = await _settingsManager.GetUserDevicesAsync(userId);
            return devices?.Any() ?? false;
        }

        private bool IsSessionFresh(DateTime requestTime)
        {
            var maxSessionAge = TimeSpan.FromMinutes(_settingsManager.GetSessionTimeout());
            return DateTime.UtcNow - requestTime <= maxSessionAge;
        }

        private async Task<bool> IsLowRiskOperationAsync(ValidationContext context)
        {
            var riskScore = 0;

            if (!IsBusinessHours())
                riskScore += 20;

            if (!IsIpWhitelisted(context.ClientIp))
                riskScore += 30;

            if (!await IsMfaEnabledAsync(context.User))
                riskScore += 25;

            if (!IsSessionFresh(context.RequestTime))
                riskScore += 25;

            return riskScore < 50;
        }

        private async Task<bool> EvaluateCustomConditionAsync(string conditionName, ValidationContext context)
        {
            var customConditions = await _settingsManager.GetCustomConditionsAsync();

            if (customConditions != null &&
                customConditions.TryGetValue(conditionName, out var condition))
            {
                return EvaluateConditionExpression(condition, context);
            }

            return false;
        }

        private bool EvaluateConditionExpression(string expression, ValidationContext context)
        {
            try
            {
                return expression.Contains("Admin", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool ResourceMatchesPattern(string resource, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return true;

            if (pattern == "*")
                return true;

            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                var prefix = pattern.TrimEnd('*');
                return resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(resource, pattern, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Helper Methods

        private ValidationContext CreateValidationContext(
            IPrincipal user,
            string permission,
            string? resource,
            Dictionary<string, object>? context)
        {
            return new ValidationContext
            {
                User = user,
                Action = permission,
                Resource = resource,
                Parameters = context ?? new Dictionary<string, object>(),
                ClaimsIdentity = user?.Identity as ClaimsIdentity,
                UserProfile = GetUserProfile(user),
                ClientIp = GetClientIp(),
                RequestTime = DateTime.UtcNow
            };
        }

        private string GenerateCacheKey(ValidationContext context)
        {
            var keyParts = new List<string>
            {
                CACHE_PREFIX,
                context.User?.Identity?.Name ?? "Anonymous",
                context.Action,
                context.Resource ?? "Global",
                context.RequestTime.ToString("yyyyMMddHH")
            };

            if (context.Parameters != null)
            {
                foreach (var param in context.Parameters.OrderBy(p => p.Key))
                {
                    keyParts.Add($"{param.Key}={param.Value}");
                }
            }

            return string.Join("|", keyParts);
        }

        private void CacheValidationResult(string cacheKey, ValidationResult result)
        {
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(
                    _settingsManager.GetCacheDuration() ?? DEFAULT_CACHE_DURATION),
                Size = 1,
                Priority = CacheItemPriority.Normal
            };

            _cache.Set(cacheKey, result, cacheOptions);
            _cacheKeyIndex.TryAdd(cacheKey, 0);
        }

        private async Task AuditValidationAsync(ValidationResult result)
        {
            try
            {
                var auditData = new
                {
                    result.UserId,
                    result.Permission,
                    result.Resource,
                    result.IsAllowed,
                    result.Reason,
                    result.Timestamp,
                    result.ProcessingTime,
                    AppliedPolicies = string.Join(",", result.AppliedPolicies)
                };

                await _accessControl.LogAccessAttemptAsync(auditData);

                if (!result.IsAllowed && result.AppliedPolicies.Any())
                {
                    await TriggerSecurityAlertAsync(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to audit permission validation");
            }
        }

        private async Task TriggerSecurityAlertAsync(ValidationResult result)
        {
            var failedAttempts = await _accessControl.GetFailedAttemptsAsync(
                result.UserId, TimeSpan.FromMinutes(15));

            if (failedAttempts >= 5)
            {
                await _accessControl.TriggerAlertAsync(
                    "MultiplePermissionFailures",
                    $"User {result.UserId} has {failedAttempts} failed permission attempts",
                    AlertPriority.High);
            }
        }

        private UserProfile? GetUserProfile(IPrincipal user)
        {
            var userId = user?.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return null;

            return _settingsManager.GetUserProfile(userId);
        }

        private string GetClientIp()
        {
            return "127.0.0.1";
        }

        private bool CanModifyPolicies()
        {
            return Thread.CurrentPrincipal?.IsInRole("SystemAdministrator") == true;
        }

        private PermissionPolicy? GetEmergencyOverridePolicy()
        {
            return _policies.Values.FirstOrDefault(p =>
                p.Name == "EmergencyOverride" && p.IsEnabled);
        }

        private void InvalidatePolicyCache()
        {
            var keysToRemove = _cacheKeyIndex.Keys
                .Where(k => k.StartsWith(CACHE_PREFIX, StringComparison.Ordinal))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.Remove(key);
                _cacheKeyIndex.TryRemove(key, out _);
            }

            _logger.LogInformation("Invalidated {Count} cached permission entries", keysToRemove.Count);
        }

        private void CleanupCache(object? state)
        {
            try
            {
                // MemoryCache kendisi expired temizler; biz sadece index’i derleyelim
                var keys = _cacheKeyIndex.Keys.ToList();
                foreach (var key in keys)
                {
                    if (!_cache.TryGetValue(key, out _))
                    {
                        _cacheKeyIndex.TryRemove(key, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup failed");
            }
        }

        #endregion

        #region Public Classes

        public class ValidationStatistics
        {
            public long TotalValidations { get; set; }
            public long FailedValidations { get; set; }
            public long CacheHits { get; set; }
            public long CacheSize { get; set; }
            public int ActivePolicies { get; set; }
            public int ConcurrentValidations { get; set; }

            public double SuccessRate => TotalValidations > 0
                ? (TotalValidations - FailedValidations) / (double)TotalValidations * 100
                : 0;

            public double CacheHitRate => TotalValidations > 0
                ? CacheHits / (double)TotalValidations * 100
                : 0;
        }

        public enum AlertPriority
        {
            Low,
            Medium,
            High,
            Critical
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
                _cacheCleanupTimer.Change(Timeout.Infinite, 0);
                _cacheCleanupTimer.Dispose();
                _validationSemaphore.Dispose();
                (_cache as IDisposable)?.Dispose();
                _cacheKeyIndex.Clear();
            }

            _disposed = true;
        }

        ~PermissionValidator()
        {
            Dispose(false);
        }

        #endregion
    }

    public interface IPermissionValidator : IDisposable
    {
        Task<PermissionValidator.ValidationResult> ValidatePermissionAsync(
            IPrincipal user,
            string permission,
            string? resource = null,
            Dictionary<string, object>? context = null);

        Task<Dictionary<string, PermissionValidator.ValidationResult>> ValidatePermissionsAsync(
            IPrincipal user,
            IEnumerable<string> permissions,
            string? resource = null);

        Task<bool> HasAnyPermissionAsync(
            IPrincipal user,
            IEnumerable<string> permissions,
            string? resource = null);

        Task<bool> HasAllPermissionsAsync(
            IPrincipal user,
            IEnumerable<string> permissions,
            string? resource = null);

        bool AddPolicy(PermissionValidator.PermissionPolicy policy);
        bool RemovePolicy(string policyName);
        void ClearCache();
        PermissionValidator.ValidationStatistics GetStatistics();
    }

    public class SecurityException : Exception
    {
        public string ErrorCode { get; }
        public int Severity { get; }

        public SecurityException(string errorCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Severity = 5;
        }

        public SecurityException(string errorCode, string message, int severity, Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Severity = severity;
        }
    }
}
