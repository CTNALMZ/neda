using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Animation.SequenceEditor.CinematicDirector;
using NEDA.API.ClientSDK;
using NEDA.CharacterSystems.AI_Behaviors.NPC_Routines;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Security.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using static NEDA.ContentCreation.AssetPipeline.ImportManagers.AssetValidator;

namespace NEDA.Core.Security
{
    /// <summary>
    /// Merkezi erişim kontrol sistemi.
    /// Kullanıcıların kaynaklara erişim haklarını kontrol eder ve yönetir.
    /// Role-Based Access Control (RBAC) ve Attribute-Based Access Control (ABAC) destekler.
    /// </summary>
    public class AccessControl : IAccessControl
    {
        private readonly ILogger<AccessControl> _logger;
        private readonly IMemoryCache _cache;
        private readonly SecurityConfiguration _config;
        private readonly IPermissionRepository _permissionRepository;
        private readonly IRoleManager _roleManager;
        private readonly IResourceManager _resourceManager;
        private readonly IAuditLogger _auditLogger;

        private readonly ConcurrentDictionary<string, AccessPolicy> _policies;
        private readonly ConcurrentDictionary<string, UserAccessContext> _userContexts;

        // Cache keys
        private const string PERMISSIONS_CACHE_KEY = "Permissions_{0}";
        private const string ROLES_CACHE_KEY = "Roles_{0}";
        private const string RESOURCES_CACHE_KEY = "Resources";
        private const string POLICY_CACHE_KEY = "Policy_{0}_{1}";

        /// <summary>
        /// AccessControl constructor
        /// </summary>
        public AccessControl(
            ILogger<AccessControl> logger,
            IMemoryCache cache,
            IOptions<SecurityConfiguration> config,
            IPermissionRepository permissionRepository,
            IRoleManager roleManager,
            IResourceManager resourceManager,
            IAuditLogger auditLogger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _permissionRepository = permissionRepository ?? throw new ArgumentNullException(nameof(permissionRepository));
            _roleManager = roleManager ?? throw new ArgumentNullException(nameof(roleManager));
            _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

            _policies = new ConcurrentDictionary<string, AccessPolicy>();
            _userContexts = new ConcurrentDictionary<string, UserAccessContext>();

            InitializeDefaultPolicies();
        }

        #region Public Methods - RBAC (Role-Based Access Control)

        /// <summary>
        /// Kullanıcının belirli bir kaynağa erişim izni olup olmadığını kontrol eder (RBAC)
        /// </summary>
        public async Task<AccessResult> CheckAccessAsync(
            ClaimsPrincipal user,
            string resource,
            string action,
            Dictionary<string, object> context = null)
        {
            ValidateParameters(user, resource, action);

            try
            {
                var userId = GetUserId(user);
                var userContext = await GetOrCreateUserContextAsync(userId, user);

                // Check cache first
                var cacheKey = string.Format(POLICY_CACHE_KEY, userId, $"{resource}:{action}");
                if (_cache.TryGetValue(cacheKey, out AccessResult cachedResult))
                {
                    _logger.LogDebug("Access check cache hit for user {UserId}, resource {Resource}, action {Action}",
                        userId, resource, action);
                    return cachedResult;
                }

                // Get user roles and permissions
                var roles = await GetUserRolesAsync(userId);
                var permissions = await GetUserPermissionsAsync(userId, roles);

                // Check RBAC
                var rbacResult = CheckRbacAccess(userContext, resource, action, permissions, roles);

                // If RBAC denies, check ABAC
                if (!rbacResult.IsAllowed && _config.EnableAbac)
                {
                    rbacResult = await CheckAbacAccessAsync(userContext, resource, action, context);
                }

                // Apply policies
                var finalResult = ApplyPolicies(rbacResult, userContext, resource, action, context);

                // Cache the result
                if (finalResult.IsAllowed && _config.CacheDuration > 0)
                {
                    _cache.Set(cacheKey, finalResult, TimeSpan.FromMinutes(_config.CacheDuration));
                }

                // Log access attempt
                await LogAccessAttemptAsync(userId, resource, action, finalResult);

                return finalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking access for user {User} to resource {Resource} with action {Action}",
                    user?.Identity?.Name, resource, action);

                // Fail-safe: Deny access on error (security by default)
                return AccessResult.Deny("System error occurred while checking access");
            }
        }

        /// <summary>
        /// Kullanıcıya belirli bir kaynak için izin verir
        /// </summary>
        public async Task<bool> GrantPermissionAsync(
            string userId,
            string resource,
            string action,
            PermissionType type = PermissionType.Allow,
            DateTime? expiryDate = null,
            string grantedBy = null)
        {
            ValidateUserAndResource(userId, resource, action);

            try
            {
                var permission = new UserPermission
                {
                    UserId = userId,
                    Resource = resource,
                    Action = action,
                    PermissionType = type,
                    GrantedDate = DateTime.UtcNow,
                    ExpiryDate = expiryDate,
                    GrantedBy = grantedBy ?? "SYSTEM",
                    IsActive = true
                };

                var success = await _permissionRepository.AddPermissionAsync(permission);

                if (success)
                {
                    // Clear cache for this user
                    ClearUserCache(userId);

                    // Log the permission grant
                    await _auditLogger.LogForUserAsync(
                        grantedBy ?? "SYSTEM",
                        "PERMISSION_GRANTED",
                        $"Granted {type} permission for user {userId} to {resource}:{action}",
                        AuditLogSeverity.Information);

                    _logger.LogInformation("Permission granted: User={UserId}, Resource={Resource}, Action={Action}, Type={Type}",
                        userId, resource, action, type);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to grant permission for user {UserId} to resource {Resource} with action {Action}",
                    userId, resource, action);
                throw new AccessControlException(ErrorCodes.PERMISSION_GRANT_FAILED,
                    $"Failed to grant permission for user {userId}", ex);
            }
        }

        /// <summary>
        /// Kullanıcıdan belirli bir izni geri alır
        /// </summary>
        public async Task<bool> RevokePermissionAsync(
            string userId,
            string resource,
            string action,
            string revokedBy = null)
        {
            ValidateUserAndResource(userId, resource, action);

            try
            {
                var success = await _permissionRepository.RemovePermissionAsync(userId, resource, action);

                if (success)
                {
                    // Clear cache for this user
                    ClearUserCache(userId);

                    // Log the permission revocation
                    await _auditLogger.LogForUserAsync(
                        revokedBy ?? "SYSTEM",
                        "PERMISSION_REVOKED",
                        $"Revoked permission for user {userId} from {resource}:{action}",
                        AuditLogSeverity.Information);

                    _logger.LogInformation("Permission revoked: User={UserId}, Resource={Resource}, Action={Action}",
                        userId, resource, action);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke permission for user {UserId} from resource {Resource} with action {Action}",
                    userId, resource, action);
                throw new AccessControlException(ErrorCodes.PERMISSION_REVOKE_FAILED,
                    $"Failed to revoke permission for user {userId}", ex);
            }
        }

        /// <summary>
        /// Kullanıcının mevcut izinlerini getirir
        /// </summary>
        public async Task<IEnumerable<UserPermission>> GetUserPermissionsAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                var cacheKey = string.Format(PERMISSIONS_CACHE_KEY, userId);

                if (!_cache.TryGetValue(cacheKey, out IEnumerable<UserPermission> permissions))
                {
                    permissions = await _permissionRepository.GetUserPermissionsAsync(userId);

                    if (_config.CacheDuration > 0)
                    {
                        _cache.Set(cacheKey, permissions, TimeSpan.FromMinutes(_config.CacheDuration));
                    }
                }

                return permissions ?? Enumerable.Empty<UserPermission>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get permissions for user {UserId}", userId);
                throw new AccessControlException(ErrorCodes.PERMISSION_RETRIEVAL_FAILED,
                    $"Failed to get permissions for user {userId}", ex);
            }
        }

        /// <summary>
        /// Belirli bir kaynak için tüm izinleri getirir
        /// </summary>
        public async Task<IEnumerable<ResourcePermission>> GetResourcePermissionsAsync(string resource)
        {
            if (string.IsNullOrWhiteSpace(resource))
                throw new ArgumentException("Resource cannot be null or empty", nameof(resource));

            try
            {
                return await _permissionRepository.GetResourcePermissionsAsync(resource);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get permissions for resource {Resource}", resource);
                throw new AccessControlException(ErrorCodes.RESOURCE_PERMISSION_RETRIEVAL_FAILED,
                    $"Failed to get permissions for resource {resource}", ex);
            }
        }

        #endregion

        #region Public Methods - ABAC (Attribute-Based Access Control)

        /// <summary>
        /// ABAC politikası oluşturur
        /// </summary>
        public async Task<AccessPolicy> CreatePolicyAsync(AccessPolicy policy)
        {
            ValidatePolicy(policy);

            try
            {
                // Normalize policy ID
                policy.Id = string.IsNullOrEmpty(policy.Id)
                    ? Guid.NewGuid().ToString()
                    : policy.Id;

                // Validate policy rules
                ValidatePolicyRules(policy.Rules);

                // Add to policies dictionary
                if (!_policies.TryAdd(policy.Id, policy))
                {
                    throw new AccessControlException(ErrorCodes.POLICY_CREATION_FAILED,
                        $"Policy with ID {policy.Id} already exists");
                }

                // Persist to repository if configured
                if (_config.PersistPolicies)
                {
                    await _permissionRepository.SavePolicyAsync(policy);
                }

                // Clear cache as policies changed
                _cache.Remove(RESOURCES_CACHE_KEY);

                await _auditLogger.LogSystemEventAsync(
                    "POLICY_CREATED",
                    $"Access policy created: {policy.Name} (ID: {policy.Id})",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Access policy created: {PolicyName} (ID: {PolicyId})",
                    policy.Name, policy.Id);

                return policy;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create access policy {PolicyName}", policy?.Name);
                throw new AccessControlException(ErrorCodes.POLICY_CREATION_FAILED,
                    $"Failed to create access policy {policy?.Name}", ex);
            }
        }

        /// <summary>
        /// ABAC politikasını günceller
        /// </summary>
        public async Task<bool> UpdatePolicyAsync(string policyId, AccessPolicy policy)
        {
            ValidatePolicyId(policyId);
            ValidatePolicy(policy);

            try
            {
                if (!_policies.ContainsKey(policyId))
                {
                    _logger.LogWarning("Policy {PolicyId} not found for update", policyId);
                    return false;
                }

                // Update policy
                _policies[policyId] = policy;

                // Persist update if configured
                if (_config.PersistPolicies)
                {
                    await _permissionRepository.UpdatePolicyAsync(policyId, policy);
                }

                // Clear cache
                _cache.Remove(RESOURCES_CACHE_KEY);
                ClearAllUserCache();

                await _auditLogger.LogSystemEventAsync(
                    "POLICY_UPDATED",
                    $"Access policy updated: {policy.Name} (ID: {policyId})",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Access policy updated: {PolicyName} (ID: {PolicyId})",
                    policy.Name, policyId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update access policy {PolicyId}", policyId);
                throw new AccessControlException(ErrorCodes.POLICY_UPDATE_FAILED,
                    $"Failed to update access policy {policyId}", ex);
            }
        }

        /// <summary>
        /// Belirli bir kaynak için geçerli politikaları değerlendirir
        /// </summary>
        public async Task<PolicyEvaluationResult> EvaluatePolicyAsync(
            ClaimsPrincipal user,
            string resource,
            Dictionary<string, object> userAttributes,
            Dictionary<string, object> resourceAttributes,
            Dictionary<string, object> environmentAttributes)
        {
            ValidateParameters(user, resource, "evaluate");

            try
            {
                var applicablePolicies = GetApplicablePolicies(resource);
                var results = new List<PolicyRuleResult>();
                var userId = GetUserId(user);

                foreach (var policy in applicablePolicies)
                {
                    foreach (var rule in policy.Rules)
                    {
                        var ruleResult = EvaluateRule(
                            rule,
                            userAttributes,
                            resourceAttributes,
                            environmentAttributes);

                        results.Add(new PolicyRuleResult
                        {
                            PolicyId = policy.Id,
                            PolicyName = policy.Name,
                            RuleId = rule.Id,
                            RuleName = rule.Name,
                            Result = ruleResult,
                            Effect = rule.Effect
                        });
                    }
                }

                // Combine results (first deny wins)
                var finalResult = CombineRuleResults(results);

                // Log evaluation
                await LogPolicyEvaluationAsync(userId, resource, results, finalResult);

                return new PolicyEvaluationResult
                {
                    UserId = userId,
                    Resource = resource,
                    RuleResults = results,
                    FinalDecision = finalResult.Result ? "ALLOW" : "DENY",
                    EvaluatedPolicies = applicablePolicies.Count,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate policies for user {User} on resource {Resource}",
                    user?.Identity?.Name, resource);
                throw new AccessControlException(ErrorCodes.POLICY_EVALUATION_FAILED,
                    $"Failed to evaluate policies for resource {resource}", ex);
            }
        }

        #endregion

        #region Public Methods - Resource Management

        /// <summary>
        /// Yeni bir kaynak tanımlar
        /// </summary>
        public async Task<ResourceDefinition> DefineResourceAsync(
            string resourceName,
            string description,
            ResourceType type,
            Dictionary<string, object> attributes = null)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
                throw new ArgumentException("Resource name cannot be null or empty", nameof(resourceName));

            try
            {
                var resource = new ResourceDefinition
                {
                    Id = GenerateResourceId(resourceName),
                    Name = resourceName,
                    Description = description,
                    Type = type,
                    Attributes = attributes ?? new Dictionary<string, object>(),
                    CreatedDate = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow,
                    IsActive = true
                };

                var success = await _resourceManager.AddResourceAsync(resource);

                if (success)
                {
                    // Clear resources cache
                    _cache.Remove(RESOURCES_CACHE_KEY);

                    await _auditLogger.LogSystemEventAsync(
                        "RESOURCE_DEFINED",
                        $"Resource defined: {resourceName} (Type: {type})",
                        AuditLogSeverity.Information);

                    _logger.LogInformation("Resource defined: {ResourceName} (Type: {Type}, ID: {ResourceId})",
                        resourceName, type, resource.Id);
                }

                return resource;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to define resource {ResourceName}", resourceName);
                throw new AccessControlException(ErrorCodes.RESOURCE_DEFINITION_FAILED,
                    $"Failed to define resource {resourceName}", ex);
            }
        }

        /// <summary>
        /// Tüm tanımlı kaynakları getirir
        /// </summary>
        public async Task<IEnumerable<ResourceDefinition>> GetAllResourcesAsync()
        {
            try
            {
                if (!_cache.TryGetValue(RESOURCES_CACHE_KEY, out IEnumerable<ResourceDefinition> resources))
                {
                    resources = await _resourceManager.GetAllResourcesAsync();

                    if (_config.CacheDuration > 0)
                    {
                        _cache.Set(RESOURCES_CACHE_KEY, resources, TimeSpan.FromMinutes(_config.CacheDuration));
                    }
                }

                return resources ?? Enumerable.Empty<ResourceDefinition>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all resources");
                throw new AccessControlException(ErrorCodes.RESOURCE_RETRIEVAL_FAILED,
                    "Failed to get all resources", ex);
            }
        }

        /// <summary>
        /// Kaynağın erişim kontrol listesini getirir
        /// </summary>
        public async Task<ResourceAcl> GetResourceAclAsync(string resourceId)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("Resource ID cannot be null or empty", nameof(resourceId));

            try
            {
                var acl = await _resourceManager.GetResourceAclAsync(resourceId);

                if (acl == null)
                {
                    _logger.LogWarning("ACL not found for resource {ResourceId}", resourceId);
                    acl = new ResourceAcl { ResourceId = resourceId };
                }

                return acl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get ACL for resource {ResourceId}", resourceId);
                throw new AccessControlException(ErrorCodes.ACL_RETRIEVAL_FAILED,
                    $"Failed to get ACL for resource {resourceId}", ex);
            }
        }

        #endregion

        #region Private Methods - Core Logic

        private async Task<UserAccessContext> GetOrCreateUserContextAsync(string userId, ClaimsPrincipal user)
        {
            if (_userContexts.TryGetValue(userId, out var context))
            {
                // Check if context needs refresh
                if (context.LastUpdated.AddMinutes(_config.UserContextRefreshMinutes) < DateTime.UtcNow)
                {
                    context = await RefreshUserContextAsync(userId, user);
                    _userContexts[userId] = context;
                }
                return context;
            }

            context = await CreateUserContextAsync(userId, user);
            _userContexts[userId] = context;
            return context;
        }

        private async Task<UserAccessContext> CreateUserContextAsync(string userId, ClaimsPrincipal user)
        {
            var roles = await GetUserRolesAsync(userId);
            var permissions = await GetUserPermissionsAsync(userId, roles);

            return new UserAccessContext
            {
                UserId = userId,
                UserName = user?.Identity?.Name,
                Roles = roles.ToList(),
                Permissions = permissions.ToList(),
                SessionId = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Attributes = ExtractUserAttributes(user)
            };
        }

        private async Task<UserAccessContext> RefreshUserContextAsync(string userId, ClaimsPrincipal user)
        {
            var roles = await GetUserRolesAsync(userId);
            var permissions = await GetUserPermissionsAsync(userId, roles);

            return new UserAccessContext
            {
                UserId = userId,
                UserName = user?.Identity?.Name,
                Roles = roles.ToList(),
                Permissions = permissions.ToList(),
                SessionId = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Attributes = ExtractUserAttributes(user)
            };
        }

        private async Task<IEnumerable<string>> GetUserRolesAsync(string userId)
        {
            var cacheKey = string.Format(ROLES_CACHE_KEY, userId);

            if (!_cache.TryGetValue(cacheKey, out IEnumerable<string> roles))
            {
                roles = await _roleManager.GetUserRolesAsync(userId);

                if (_config.CacheDuration > 0)
                {
                    _cache.Set(cacheKey, roles, TimeSpan.FromMinutes(_config.CacheDuration));
                }
            }

            return roles ?? Enumerable.Empty<string>();
        }

        private async Task<IEnumerable<UserPermission>> GetUserPermissionsAsync(string userId, IEnumerable<string> roles)
        {
            // Get direct user permissions
            var userPermissions = await GetUserPermissionsAsync(userId);

            // Get role permissions
            var rolePermissions = new List<UserPermission>();
            foreach (var role in roles)
            {
                var permissions = await _permissionRepository.GetRolePermissionsAsync(role);
                if (permissions != null)
                    rolePermissions.AddRange(permissions);
            }

            // Combine and deduplicate
            var allPermissions = userPermissions
                .Concat(rolePermissions)
                .GroupBy(p => new { p.Resource, p.Action })
                .Select(g => g.OrderByDescending(p => p.Priority).First())
                .Where(p => p.IsActive && (!p.ExpiryDate.HasValue || p.ExpiryDate.Value > DateTime.UtcNow));

            return allPermissions;
        }

        private AccessResult CheckRbacAccess(
            UserAccessContext userContext,
            string resource,
            string action,
            IEnumerable<UserPermission> permissions,
            IEnumerable<string> roles)
        {
            // Check explicit permissions first
            var explicitPermission = permissions.FirstOrDefault(p =>
                p.Resource.Equals(resource, StringComparison.OrdinalIgnoreCase) &&
                p.Action.Equals(action, StringComparison.OrdinalIgnoreCase));

            if (explicitPermission != null)
            {
                return explicitPermission.PermissionType == PermissionType.Allow
                    ? AccessResult.Allow($"Explicit permission")
                    : AccessResult.Deny($"Explicit denial");
            }

            // Check wildcard permissions
            var wildcardPermission = permissions.FirstOrDefault(p =>
                (p.Resource == "*" || MatchesWildcard(p.Resource, resource)) &&
                (p.Action == "*" || MatchesWildcard(p.Action, action)));

            if (wildcardPermission != null)
            {
                return wildcardPermission.PermissionType == PermissionType.Allow
                    ? AccessResult.Allow($"Wildcard permission")
                    : AccessResult.Deny($"Wildcard denial");
            }

            // Check role-based access
            if (_config.DefaultDeny)
            {
                return AccessResult.Deny("No matching permissions found (default deny)");
            }

            return AccessResult.Allow("Access granted by default (no explicit rules)");
        }

        private async Task<AccessResult> CheckAbacAccessAsync(
            UserAccessContext userContext,
            string resource,
            string action,
            Dictionary<string, object> context)
        {
            var applicablePolicies = GetApplicablePolicies(resource);

            if (!applicablePolicies.Any())
            {
                return AccessResult.Deny("No applicable ABAC policies found");
            }

            // Get resource attributes
            var resourceAttributes = await _resourceManager.GetResourceAttributesAsync(resource);

            // Get environment attributes
            var environmentAttributes = GetEnvironmentAttributes();

            // Merge user attributes
            var userAttributes = userContext.Attributes ?? new Dictionary<string, object>();

            // Merge context
            var evaluationContext = new Dictionary<string, object>();
            evaluationContext.Merge(userAttributes);
            evaluationContext.Merge(resourceAttributes);
            evaluationContext.Merge(environmentAttributes);
            if (context != null) evaluationContext.Merge(context);

            // Evaluate policies
            foreach (var policy in applicablePolicies)
            {
                foreach (var rule in policy.Rules)
                {
                    var ruleResult = EvaluateRule(rule, evaluationContext);

                    if (ruleResult && rule.Effect == RuleEffect.Deny)
                    {
                        return AccessResult.Deny($"ABAC policy denied: {policy.Name} - {rule.Name}");
                    }

                    if (ruleResult && rule.Effect == RuleEffect.Allow)
                    {
                        return AccessResult.Allow($"ABAC policy allowed: {policy.Name} - {rule.Name}");
                    }
                }
            }

            return AccessResult.Deny("No ABAC policy matched (implicit deny)");
        }

        private AccessResult ApplyPolicies(
            AccessResult preliminaryResult,
            UserAccessContext userContext,
            string resource,
            string action,
            Dictionary<string, object> context)
        {
            // Apply system-wide policies (e.g., maintenance mode, blacklist)
            if (_config.MaintenanceMode && !userContext.Roles.Contains("Administrator"))
            {
                return AccessResult.Deny("System is in maintenance mode");
            }

            // Check blacklist
            if (IsUserBlacklisted(userContext.UserId))
            {
                return AccessResult.Deny("User is blacklisted");
            }

            // Check time-based restrictions
            if (HasTimeRestriction(resource, action))
            {
                if (!IsWithinAllowedTime(resource, action))
                {
                    return AccessResult.Deny("Access not allowed at this time");
                }
            }

            // Check concurrent sessions
            if (_config.MaxConcurrentSessions > 0)
            {
                if (GetUserSessionCount(userContext.UserId) >= _config.MaxConcurrentSessions)
                {
                    return AccessResult.Deny("Maximum concurrent sessions reached");
                }
            }

            return preliminaryResult;
        }

        private bool EvaluateRule(PolicyRule rule, Dictionary<string, object> context)
        {
            if (rule.Conditions == null || !rule.Conditions.Any())
                return true;

            foreach (var condition in rule.Conditions)
            {
                if (!EvaluateCondition(condition, context))
                    return false;
            }

            return true;
        }

        // Overload used by EvaluatePolicyAsync signature
        private bool EvaluateRule(
            PolicyRule rule,
            Dictionary<string, object> userAttributes,
            Dictionary<string, object> resourceAttributes,
            Dictionary<string, object> environmentAttributes)
        {
            var ctx = new Dictionary<string, object>();
            if (userAttributes != null) ctx.Merge(userAttributes);
            if (resourceAttributes != null) ctx.Merge(resourceAttributes);
            if (environmentAttributes != null) ctx.Merge(environmentAttributes);
            return EvaluateRule(rule, ctx);
        }

        private bool EvaluateCondition(Condition condition, Dictionary<string, object> context)
        {
            if (!context.TryGetValue(condition.Attribute, out var attributeValue))
                return condition.Operator == ConditionOperator.NotExists;

            return condition.Operator switch
            {
                ConditionOperator.Equals => EqualsValue(attributeValue, condition.Value),
                ConditionOperator.NotEquals => !EqualsValue(attributeValue, condition.Value),
                ConditionOperator.GreaterThan => Compare(attributeValue, condition.Value) > 0,
                ConditionOperator.GreaterThanOrEqual => Compare(attributeValue, condition.Value) >= 0,
                ConditionOperator.LessThan => Compare(attributeValue, condition.Value) < 0,
                ConditionOperator.LessThanOrEqual => Compare(attributeValue, condition.Value) <= 0,
                ConditionOperator.Contains => Contains(attributeValue, condition.Value),
                ConditionOperator.NotContains => !Contains(attributeValue, condition.Value),
                ConditionOperator.Exists => true,
                ConditionOperator.NotExists => false,
                ConditionOperator.In => IsIn(attributeValue, condition.Value),
                ConditionOperator.NotIn => !IsIn(attributeValue, condition.Value),
                ConditionOperator.StartsWith => StartsWith(attributeValue, condition.Value),
                ConditionOperator.EndsWith => EndsWith(attributeValue, condition.Value),
                ConditionOperator.MatchesRegex => MatchesRegex(attributeValue, condition.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(condition.Operator), $"Unknown operator: {condition.Operator}")
            };
        }

        #endregion

        #region Private Methods - Helper Functions

        private void InitializeDefaultPolicies()
        {
            // System-wide default deny policy
            var defaultPolicy = new AccessPolicy
            {
                Id = "DEFAULT_DENY",
                Name = "Default Deny All",
                Description = "Default policy that denies all access unless explicitly allowed",
                ResourcePattern = "*",
                Priority = int.MinValue,
                Rules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        Id = "DENY_ALL",
                        Name = "Deny All",
                        Effect = RuleEffect.Deny,
                        Conditions = new List<Condition>()
                    }
                }
            };

            _policies.TryAdd(defaultPolicy.Id, defaultPolicy);

            // Administrator policy
            var adminPolicy = new AccessPolicy
            {
                Id = "ADMIN_ALLOW_ALL",
                Name = "Administrator Full Access",
                Description = "Allows administrators full access to all resources",
                ResourcePattern = "*",
                Priority = 1000,
                Rules = new List<PolicyRule>
                {
                    new PolicyRule
                    {
                        Id = "ALLOW_ADMIN",
                        Name = "Allow Administrators",
                        Effect = RuleEffect.Allow,
                        Conditions = new List<Condition>
                        {
                            new Condition
                            {
                                Attribute = "user.roles",
                                Operator = ConditionOperator.Contains,
                                Value = "Administrator"
                            }
                        }
                    }
                }
            };

            _policies.TryAdd(adminPolicy.Id, adminPolicy);

            _logger.LogInformation("Default access control policies initialized");
        }

        private string GetUserId(ClaimsPrincipal user)
        {
            return user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? user?.FindFirst("sub")?.Value
                   ?? "anonymous";
        }

        private Dictionary<string, object> ExtractUserAttributes(ClaimsPrincipal user)
        {
            var attributes = new Dictionary<string, object>();

            if (user?.Identity?.IsAuthenticated == true)
            {
                attributes["user.authenticated"] = true;
                attributes["user.name"] = user.Identity.Name;

                foreach (var claim in user.Claims)
                {
                    var key = $"user.claim.{claim.Type}";
                    if (attributes.ContainsKey(key))
                    {
                        if (attributes[key] is List<string> list)
                        {
                            list.Add(claim.Value);
                        }
                        else
                        {
                            attributes[key] = new List<string> { attributes[key]?.ToString(), claim.Value };
                        }
                    }
                    else
                    {
                        attributes[key] = claim.Value;
                    }
                }
            }
            else
            {
                attributes["user.authenticated"] = false;
                attributes["user.name"] = "anonymous";
            }

            return attributes;
        }

        private Dictionary<string, object> GetEnvironmentAttributes()
        {
            return new Dictionary<string, object>
            {
                ["environment.time"] = DateTime.UtcNow,
                ["environment.time.hour"] = DateTime.UtcNow.Hour,
                ["environment.time.dayofweek"] = DateTime.UtcNow.DayOfWeek.ToString(),
                ["environment.machine"] = Environment.MachineName,
                ["environment.user"] = Environment.UserName,
                ["environment.domain"] = Environment.UserDomainName,
                ["environment.ip"] = GetClientIpAddress()
            };
        }

        private string GetClientIpAddress()
        {
            // Implementation depends on your application context
            // This is a simplified version
            return System.Net.Dns.GetHostName();
        }

        private List<AccessPolicy> GetApplicablePolicies(string resource)
        {
            return _policies.Values
                .Where(p => p.IsActive && MatchesWildcard(p.ResourcePattern, resource))
                .OrderByDescending(p => p.Priority)
                .ToList();
        }

        private bool MatchesWildcard(string pattern, string input)
        {
            if (pattern == "*") return true;
            if (string.Equals(pattern, input, StringComparison.OrdinalIgnoreCase)) return true;

            // Simple wildcard matching (can be enhanced with regex)
            if (pattern.EndsWith("*", StringComparison.Ordinal) && input.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase))
                return true;

            if (pattern.StartsWith("*", StringComparison.Ordinal) && input.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private PolicyRuleResult CombineRuleResults(List<PolicyRuleResult> results)
        {
            // First deny wins
            var denyResult = results.FirstOrDefault(r => r.Effect == RuleEffect.Deny && r.Result);
            if (denyResult != null)
            {
                return denyResult;
            }

            // Then check for allows
            var allowResult = results.FirstOrDefault(r => r.Effect == RuleEffect.Allow && r.Result);
            if (allowResult != null)
            {
                return allowResult;
            }

            // Default deny
            return new PolicyRuleResult
            {
                Result = false,
                Effect = RuleEffect.Deny
            };
        }

        private bool IsUserBlacklisted(string userId)
        {
            // Implementation would check against blacklist service
            return false;
        }

        private bool HasTimeRestriction(string resource, string action)
        {
            // Implementation would check time restrictions for resource/action
            return false;
        }

        private bool IsWithinAllowedTime(string resource, string action)
        {
            // Implementation would check current time against allowed times
            return true;
        }

        private int GetUserSessionCount(string userId)
        {
            // Implementation would get active session count
            return 1;
        }

        private void ClearUserCache(string userId)
        {
            var permissionsKey = string.Format(PERMISSIONS_CACHE_KEY, userId);
            var rolesKey = string.Format(ROLES_CACHE_KEY, userId);

            _cache.Remove(permissionsKey);
            _cache.Remove(rolesKey);

            // Clear all policy cache entries for this user
            // NOTE: IMemoryCache doesn't support key enumeration by default.
            // If you need real prefix clearing, add a key registry or switch to a cache with enumeration support.
        }

        private void ClearAllUserCache()
        {
            // This would clear all user-related cache entries
            // Implementation depends on your cache strategy
        }

        private string GenerateResourceId(string resourceName)
        {
            return $"RES_{resourceName.ToUpperInvariant().Replace(" ", "_")}_{Guid.NewGuid():N}".Substring(0, Math.Min(4 + resourceName.Length + 1 + 8 + 4, 50));
        }

        #endregion

        #region Private Methods - Validation

        private void ValidateParameters(ClaimsPrincipal user, string resource, string action)
        {
            if (user == null)
                throw new ArgumentNullException(nameof(user));
            if (string.IsNullOrWhiteSpace(resource))
                throw new ArgumentException("Resource cannot be null or empty", nameof(resource));
            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("Action cannot be null or empty", nameof(action));
        }

        private void ValidateUserAndResource(string userId, string resource, string action)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            if (string.IsNullOrWhiteSpace(resource))
                throw new ArgumentException("Resource cannot be null or empty", nameof(resource));
            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("Action cannot be null or empty", nameof(action));
        }

        private void ValidatePolicy(AccessPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));
            if (string.IsNullOrWhiteSpace(policy.Name))
                throw new ArgumentException("Policy name cannot be null or empty", nameof(policy.Name));
            if (string.IsNullOrWhiteSpace(policy.ResourcePattern))
                throw new ArgumentException("Resource pattern cannot be null or empty", nameof(policy.ResourcePattern));
            if (policy.Rules == null || !policy.Rules.Any())
                throw new ArgumentException("Policy must have at least one rule", nameof(policy.Rules));
        }

        private void ValidatePolicyId(string policyId)
        {
            if (string.IsNullOrWhiteSpace(policyId))
                throw new ArgumentException("Policy ID cannot be null or empty", nameof(policyId));
        }

        private void ValidatePolicyRules(IEnumerable<PolicyRule> rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                    throw new ArgumentException("Rule ID cannot be null or empty");
                if (string.IsNullOrWhiteSpace(rule.Name))
                    throw new ArgumentException("Rule name cannot be null or empty");
            }
        }

        #endregion

        #region Private Methods - Logging

        private async Task LogAccessAttemptAsync(string userId, string resource, string action, AccessResult result)
        {
            try
            {
                var severity = result.IsAllowed ? AuditLogSeverity.Information : AuditLogSeverity.Warning;

                await _auditLogger.LogForUserAsync(
                    userId,
                    result.IsAllowed ? "ACCESS_GRANTED" : "ACCESS_DENIED",
                    $"Access {(result.IsAllowed ? "granted" : "denied")} for {resource}:{action}. Reason: {result.Reason}",
                    severity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log access attempt for user {UserId}", userId);
            }
        }

        private async Task LogPolicyEvaluationAsync(
            string userId,
            string resource,
            List<PolicyRuleResult> results,
            PolicyRuleResult finalResult)
        {
            try
            {
                var details = new
                {
                    Resource = resource,
                    TotalRules = results.Count,
                    AllowedRules = results.Count(r => r.Result && r.Effect == RuleEffect.Allow),
                    DeniedRules = results.Count(r => r.Result && r.Effect == RuleEffect.Deny),
                    FinalDecision = finalResult.Result ? "ALLOW" : "DENY",
                    FinalEffect = finalResult.Effect.ToString(),
                    EvaluatedPolicies = results.Select(r => r.PolicyName).Distinct()
                };

                await _auditLogger.LogForUserAsync(
                    userId,
                    "POLICY_EVALUATION",
                    JsonSerializer.Serialize(details),
                    finalResult.Result ? AuditLogSeverity.Information : AuditLogSeverity.Warning);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log policy evaluation for user {UserId}", userId);
            }
        }

        #endregion

        #region Comparison Helper Methods

        private bool EqualsValue(object value1, object value2)
        {
            return object.Equals(value1, value2) ||
                   string.Equals(value1?.ToString(), value2?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private int Compare(object value1, object value2)
        {
            if (value1 is IComparable comparable1 && value2 is IComparable comparable2)
            {
                try
                {
                    return comparable1.CompareTo(comparable2);
                }
                catch
                {
                    // If comparison fails, fall back to string comparison
                }
            }

            return string.Compare(value1?.ToString(), value2?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private bool Contains(object container, object value)
        {
            if (container == null || value == null)
                return false;

            var containerStr = container.ToString();
            var valueStr = value.ToString();

            return containerStr?.IndexOf(valueStr, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsIn(object value, object collection)
        {
            if (collection is IEnumerable<object> enumerable)
            {
                return enumerable.Any(item => EqualsValue(item, value));
            }

            if (collection is IEnumerable<string> enumerableStr)
            {
                return enumerableStr.Any(item => string.Equals(item, value?.ToString(), StringComparison.OrdinalIgnoreCase));
            }

            if (collection is string str)
            {
                var values = str.Split(',', ';', '|');
                return values.Any(v => v.Trim().Equals(value?.ToString(), StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private bool StartsWith(object value, object prefix)
        {
            if (value == null || prefix == null)
                return false;

            return value.ToString().StartsWith(prefix.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private bool EndsWith(object value, object suffix)
        {
            if (value == null || suffix == null)
                return false;

            return value.ToString().EndsWith(suffix.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesRegex(object value, object pattern)
        {
            if (value == null || pattern == null)
                return false;

            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    value.ToString(),
                    pattern.ToString(),
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        #endregion

        #region IDisposable Implementation

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup resources
                    _policies.Clear();
                    _userContexts.Clear();

                    // Optional: Clear cache entries
                    if (_cache is MemoryCache memoryCache)
                    {
                        memoryCache.Compact(1.0);
                    }
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~AccessControl()
        {
            Dispose(false);
        }

        #endregion
    }

    #region Supporting Classes and Interfaces

    /// <summary>
    /// Access control result
    /// </summary>
    public class AccessResult
    {
        public bool IsAllowed { get; }
        public string Reason { get; }
        public DateTime Timestamp { get; }
        public Dictionary<string, object> Context { get; }

        private AccessResult(bool isAllowed, string reason, Dictionary<string, object> context = null)
        {
            IsAllowed = isAllowed;
            Reason = reason ?? (isAllowed ? "Access granted" : "Access denied");
            Timestamp = DateTime.UtcNow;
            Context = context ?? new Dictionary<string, object>();
        }

        public static AccessResult Allow(string reason = null, Dictionary<string, object> context = null)
            => new AccessResult(true, reason, context);

        public static AccessResult Deny(string reason = null, Dictionary<string, object> context = null)
            => new AccessResult(false, reason, context);

        public override string ToString() => IsAllowed ? $"ALLOW: {Reason}" : $"DENY: {Reason}";
    }

    /// <summary>
    /// Access policy for ABAC
    /// </summary>
    public class AccessPolicy
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ResourcePattern { get; set; }
        public int Priority { get; set; } = 0;
        public List<PolicyRule> Rules { get; set; } = new List<PolicyRule>();
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// Policy rule
    /// </summary>
    public class PolicyRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public RuleEffect Effect { get; set; }
        public List<Condition> Conditions { get; set; } = new List<Condition>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rule effect
    /// </summary>
    public enum RuleEffect
    {
        Allow,
        Deny
    }

    /// <summary>
    /// Condition for policy rules
    /// </summary>
    public class Condition
    {
        public string Attribute { get; set; }
        public ConditionOperator Operator { get; set; }
        public object Value { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Condition operators
    /// </summary>
    public enum ConditionOperator
    {
        Equals,
        NotEquals,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Contains,
        NotContains,
        Exists,
        NotExists,
        In,
        NotIn,
        StartsWith,
        EndsWith,
        MatchesRegex
    }

    /// <summary>
    /// User permission
    /// </summary>
    public class UserPermission
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public string Resource { get; set; }
        public string Action { get; set; }
        public PermissionType PermissionType { get; set; }
        public DateTime GrantedDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public string GrantedBy { get; set; }
        public bool IsActive { get; set; }
        public int Priority { get; set; } = 0;
        public Dictionary<string, object> Conditions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Resource permission
    /// </summary>
    public class ResourcePermission
    {
        public string Resource { get; set; }
        public string Action { get; set; }
        public List<string> AllowedUsers { get; set; } = new List<string>();
        public List<string> AllowedRoles { get; set; } = new List<string>();
        public List<string> DeniedUsers { get; set; } = new List<string>();
        public List<string> DeniedRoles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Permission type
    /// </summary>
    public enum PermissionType
    {
        Allow,
        Deny
    }

    /// <summary>
    /// User access context
    /// </summary>
    public class UserAccessContext
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public List<UserPermission> Permissions { get; set; } = new List<UserPermission>();
        public string SessionId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
        public List<AccessHistory> RecentAccess { get; set; } = new List<AccessHistory>();
    }

    /// <summary>
    /// Access history
    /// </summary>
    public class AccessHistory
    {
        public string Resource { get; set; }
        public string Action { get; set; }
        public bool Allowed { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Resource definition
    /// </summary>
    public class ResourceDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ResourceType Type { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsActive { get; set; }
        public string Owner { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Resource type
    /// </summary>
    public enum ResourceType
    {
        File,
        Directory,
        Database,
        ApiEndpoint,
        Service,
        Module,
        Function,
        UIComponent,
        Report,
        Configuration
    }

    /// <summary>
    /// Resource ACL
    /// </summary>
    public class ResourceAcl
    {
        public string ResourceId { get; set; }
        public List<AclEntry> Entries { get; set; } = new List<AclEntry>();
        public AclInheritance Inheritance { get; set; }
        public bool IsProtected { get; set; }
    }

    /// <summary>
    /// ACL entry
    /// </summary>
    public class AclEntry
    {
        public string Principal { get; set; } // User or Role
        public bool IsUser { get; set; }
        public List<string> Permissions { get; set; } = new List<string>();
        public AclEntryType Type { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }

    /// <summary>
    /// ACL entry type
    /// </summary>
    public enum AclEntryType
    {
        Allow,
        Deny
    }

    /// <summary>
    /// ACL inheritance
    /// </summary>
    public enum AclInheritance
    {
        None,
        Container,
        ContainerAndChildren,
        ChildrenOnly
    }

    /// <summary>
    /// Policy evaluation result
    /// </summary>
    public class PolicyEvaluationResult
    {
        public string UserId { get; set; }
        public string Resource { get; set; }
        public List<PolicyRuleResult> RuleResults { get; set; } = new List<PolicyRuleResult>();
        public string FinalDecision { get; set; }
        public int EvaluatedPolicies { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Policy rule result
    /// </summary>
    public class PolicyRuleResult
    {
        public string PolicyId { get; set; }
        public string PolicyName { get; set; }
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public bool Result { get; set; }
        public RuleEffect Effect { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Security configuration
    /// </summary>
    public class SecurityConfiguration
    {
        public bool DefaultDeny { get; set; } = true;
        public bool EnableAbac { get; set; } = true;
        public bool PersistPolicies { get; set; } = true;
        public int CacheDuration { get; set; } = 5; // minutes
        public int UserContextRefreshMinutes { get; set; } = 15;
        public bool MaintenanceMode { get; set; } = false;
        public int MaxConcurrentSessions { get; set; } = 5;
        public List<string> SuperAdminRoles { get; set; } = new List<string> { "Administrator", "SuperAdmin" };
        public Dictionary<string, ResourceDefaultPermissions> ResourceDefaults { get; set; } = new Dictionary<string, ResourceDefaultPermissions>();
    }

    /// <summary>
    /// Resource default permissions
    /// </summary>
    public class ResourceDefaultPermissions
    {
        public List<string> DefaultAllowedRoles { get; set; } = new List<string>();
        public List<string> DefaultAllowedUsers { get; set; } = new List<string>();
        public List<string> DefaultDeniedRoles { get; set; } = new List<string>();
        public List<string> DefaultDeniedUsers { get; set; } = new List<string>();
    }

    /// <summary>
    /// Access control exception
    /// </summary>
    public class AccessControlException : Exception
    {
        public string ErrorCode { get; }

        public AccessControlException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public AccessControlException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Access control interface
    /// </summary>
    public interface IAccessControl : IDisposable
    {
        // RBAC Methods
        Task<AccessResult> CheckAccessAsync(ClaimsPrincipal user, string resource, string action, Dictionary<string, object> context = null);
        Task<bool> GrantPermissionAsync(string userId, string resource, string action, PermissionType type = PermissionType.Allow, DateTime? expiryDate = null, string grantedBy = null);
        Task<bool> RevokePermissionAsync(string userId, string resource, string action, string revokedBy = null);
        Task<IEnumerable<UserPermission>> GetUserPermissionsAsync(string userId);
        Task<IEnumerable<ResourcePermission>> GetResourcePermissionsAsync(string resource);

        // ABAC Methods
        Task<AccessPolicy> CreatePolicyAsync(AccessPolicy policy);
        Task<bool> UpdatePolicyAsync(string policyId, AccessPolicy policy);
        Task<PolicyEvaluationResult> EvaluatePolicyAsync(ClaimsPrincipal user, string resource, Dictionary<string, object> userAttributes, Dictionary<string, object> resourceAttributes, Dictionary<string, object> environmentAttributes);

        // Resource Management
        Task<ResourceDefinition> DefineResourceAsync(string resourceName, string description, ResourceType type, Dictionary<string, object> attributes = null);
        Task<IEnumerable<ResourceDefinition>> GetAllResourcesAsync();
        Task<ResourceAcl> GetResourceAclAsync(string resourceId);
    }

    /// <summary>
    /// Permission repository interface
    /// </summary>
    public interface IPermissionRepository
    {
        Task<bool> AddPermissionAsync(UserPermission permission);
        Task<bool> RemovePermissionAsync(string userId, string resource, string action);
        Task<IEnumerable<UserPermission>> GetUserPermissionsAsync(string userId);
        Task<IEnumerable<UserPermission>> GetRolePermissionsAsync(string role);
        Task<IEnumerable<ResourcePermission>> GetResourcePermissionsAsync(string resource);
        Task SavePolicyAsync(AccessPolicy policy);
        Task UpdatePolicyAsync(string policyId, AccessPolicy policy);
        Task<IEnumerable<AccessPolicy>> GetPoliciesAsync(string resourcePattern = null);
    }

    /// <summary>
    /// Role manager interface
    /// </summary>
    public interface IRoleManager
    {
        Task<IEnumerable<string>> GetUserRolesAsync(string userId);
        Task<bool> UserHasRoleAsync(string userId, string role);
        Task<IEnumerable<string>> GetRolePermissionsAsync(string role);
        Task<bool> AddUserToRoleAsync(string userId, string role);
        Task<bool> RemoveUserFromRoleAsync(string userId, string role);
    }

    /// <summary>
    /// Resource manager interface
    /// </summary>
    public interface IResourceManager
    {
        Task<bool> AddResourceAsync(ResourceDefinition resource);
        Task<IEnumerable<ResourceDefinition>> GetAllResourcesAsync();
        Task<ResourceDefinition> GetResourceAsync(string resourceId);
        Task<Dictionary<string, object>> GetResourceAttributesAsync(string resource);
        Task<ResourceAcl> GetResourceAclAsync(string resourceId);
        Task<bool> UpdateResourceAclAsync(string resourceId, ResourceAcl acl);
    }

    /// <summary>
    /// Audit logger interface
    /// </summary>
    public interface IAuditLogger
    {
        Task LogForUserAsync(string userId, string action, string details, AuditLogSeverity severity = AuditLogSeverity.Information);
        Task LogSystemEventAsync(string eventName, string description, AuditLogSeverity severity = AuditLogSeverity.Information);
    }

    #endregion
}
