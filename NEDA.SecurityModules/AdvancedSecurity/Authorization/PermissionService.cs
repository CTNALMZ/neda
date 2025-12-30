using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.SecurityModules.Manifest;

namespace NEDA.SecurityModules.AdvancedSecurity.Authorization;
{
    /// <summary>
    /// İleri düzey yetki yönetimi servisi - RBAC, ABAC, PBAC destekli;
    /// Endüstriyel seviyede, yüksek performanslı, hibrit yetkilendirme sistemi;
    /// </summary>
    public class PermissionService : IPermissionService, IDisposable;
    {
        private readonly ILogger<PermissionService> _logger;
        private readonly IRoleManager _roleManager;
        private readonly IPolicyEvaluator _policyEvaluator;
        private readonly IMemoryCache _memoryCache;
        private readonly IConfiguration _configuration;
        private readonly IAuditLogger _auditLogger;
        private readonly IPermissionRepository _repository;
        private readonly ConcurrentDictionary<string, PermissionCacheEntry> _permissionCache;
        private readonly PermissionServiceConfig _config;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private readonly Timer _cacheCleanupTimer;
        private bool _disposed;

        public PermissionService(
            ILogger<PermissionService> logger,
            IRoleManager roleManager,
            IPolicyEvaluator policyEvaluator,
            IMemoryCache memoryCache,
            IConfiguration configuration,
            IAuditLogger auditLogger,
            IPermissionRepository repository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _roleManager = roleManager ?? throw new ArgumentNullException(nameof(roleManager));
            _policyEvaluator = policyEvaluator ?? throw new ArgumentNullException(nameof(policyEvaluator));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            _config = PermissionServiceConfig.LoadFromConfiguration(configuration);
            _permissionCache = new ConcurrentDictionary<string, PermissionCacheEntry>();

            // Cache cleanup timer;
            _cacheCleanupTimer = new Timer(
                _ => CleanupExpiredCacheEntries(),
                null,
                TimeSpan.FromMinutes(_config.CacheCleanupIntervalMinutes),
                TimeSpan.FromMinutes(_config.CacheCleanupIntervalMinutes));

            _logger.LogInformation("PermissionService initialized with {CacheSize} max cache entries",
                _config.MaxCacheEntries);
        }

        #region Core Permission Methods;

        /// <summary>
        /// Kullanıcının bir kaynağa erişim izni olup olmadığını kontrol eder;
        /// </summary>
        public async Task<AuthorizationResult> CheckPermissionAsync(
            ClaimsPrincipal user,
            string permission,
            ResourceContext resourceContext = null,
            AuthorizationContext authContext = null,
            CancellationToken cancellationToken = default)
        {
            if (user == null)
                throw new ArgumentNullException(nameof(user));
            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Permission cannot be empty", nameof(permission));

            authContext ??= new AuthorizationContext();
            resourceContext ??= new ResourceContext();

            try
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var startTime = DateTime.UtcNow;

                // 1. Cache kontrolü;
                var cacheKey = BuildCacheKey(userId, permission, resourceContext, authContext);
                if (_config.EnableCaching)
                {
                    var cachedResult = await GetCachedResultAsync(cacheKey, cancellationToken);
                    if (cachedResult != null)
                    {
                        _logger.LogDebug("Permission check cache hit for user {UserId}, permission {Permission}",
                            userId, permission);
                        return cachedResult;
                    }
                }

                // 2. Temel yetki kontrolü (RBAC)
                var rbacResult = await CheckRbacPermissionAsync(user, permission, resourceContext, cancellationToken);
                if (!rbacResult.IsAuthorized)
                {
                    var result = AuthorizationResult.Denied(
                        rbacResult.DenialReason,
                        rbacResult.RequiredPermissions,
                        AuthorizationMethod.RBAC);

                    await CacheResultAsync(cacheKey, result, cancellationToken);
                    await LogAuthorizationEventAsync(userId, permission, resourceContext, result, startTime, cancellationToken);
                    return result;
                }

                // 3. İleri düzey politika kontrolü (ABAC/PBAC)
                var policyResult = await _policyEvaluator.EvaluateAsync(user, permission, resourceContext, authContext, cancellationToken);
                if (!policyResult.IsAuthorized)
                {
                    var result = AuthorizationResult.Denied(
                        policyResult.DenialReason,
                        policyResult.RequiredConditions,
                        AuthorizationMethod.PBAC,
                        policyResult.DetailedViolations);

                    await CacheResultAsync(cacheKey, result, cancellationToken);
                    await LogAuthorizationEventAsync(userId, permission, resourceContext, result, startTime, cancellationToken);
                    return result;
                }

                // 4. Zaman kısıtlaması kontrolü;
                if (_config.EnableTimeRestrictions)
                {
                    var timeResult = await CheckTimeRestrictionsAsync(userId, permission, resourceContext, cancellationToken);
                    if (!timeResult.IsAllowed)
                    {
                        var result = AuthorizationResult.Denied(
                            $"Permission not allowed at this time: {timeResult.DenialReason}",
                            null,
                            AuthorizationMethod.TimeBased);

                        await CacheResultAsync(cacheKey, result, cancellationToken);
                        await LogAuthorizationEventAsync(userId, permission, resourceContext, result, startTime, cancellationToken);
                        return result;
                    }
                }

                // 5. Coğrafi kısıtlama kontrolü;
                if (_config.EnableGeolocationRestrictions && !string.IsNullOrEmpty(authContext.Geolocation))
                {
                    var geoResult = await CheckGeolocationRestrictionsAsync(userId, permission, authContext.Geolocation, cancellationToken);
                    if (!geoResult.IsAllowed)
                    {
                        var result = AuthorizationResult.Denied(
                            $"Permission not allowed from this location: {geoResult.DenialReason}",
                            null,
                            AuthorizationMethod.GeolocationBased);

                        await CacheResultAsync(cacheKey, result, cancellationToken);
                        await LogAuthorizationEventAsync(userId, permission, resourceContext, result, startTime, cancellationToken);
                        return result;
                    }
                }

                // 6. Risk bazlı yetkilendirme;
                if (_config.EnableRiskBasedAuthorization)
                {
                    var riskResult = await CheckRiskBasedAuthorizationAsync(userId, permission, authContext, cancellationToken);
                    if (!riskResult.IsAllowed)
                    {
                        var result = AuthorizationResult.Denied(
                            $"Permission denied due to risk assessment: {riskResult.DenialReason}",
                            null,
                            AuthorizationMethod.RiskBased,
                            riskResult.AdditionalVerificationRequired ?
                                new[] { "Requires additional verification" } : null);

                        await CacheResultAsync(cacheKey, result, cancellationToken);
                        await LogAuthorizationEventAsync(userId, permission, resourceContext, result, startTime, cancellationToken);
                        return result;
                    }
                }

                // 7. Başarılı yetkilendirme;
                var finalResult = AuthorizationResult.Allowed(
                    rbacResult.GrantedPermissions,
                    policyResult.EffectivePolicies,
                    AuthorizationMethod.Hybrid);

                await CacheResultAsync(cacheKey, finalResult, cancellationToken);
                await LogAuthorizationEventAsync(userId, permission, resourceContext, finalResult, startTime, cancellationToken);

                _logger.LogInformation("Permission granted for user {UserId}, permission {Permission}",
                    userId, permission);

                return finalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking permission {Permission} for user", permission);
                throw new AuthorizationException($"Error checking permission {permission}", ex);
            }
        }

        /// <summary>
        /// Kullanıcının birden fazla izni aynı anda olup olmadığını kontrol eder;
        /// </summary>
        public async Task<BatchAuthorizationResult> CheckPermissionsAsync(
            ClaimsPrincipal user,
            IEnumerable<string> permissions,
            ResourceContext resourceContext = null,
            AuthorizationContext authContext = null,
            BatchAuthorizationMode mode = BatchAuthorizationMode.All,
            CancellationToken cancellationToken = default)
        {
            if (user == null)
                throw new ArgumentNullException(nameof(user));
            if (permissions == null)
                throw new ArgumentNullException(nameof(permissions));

            var permissionList = permissions.ToList();
            if (!permissionList.Any())
                return BatchAuthorizationResult.Empty;

            authContext ??= new AuthorizationContext();
            resourceContext ??= new ResourceContext();

            try
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var startTime = DateTime.UtcNow;
                var results = new Dictionary<string, AuthorizationResult>();
                var tasks = new List<Task>();

                // Paralel olarak tüm izinleri kontrol et;
                foreach (var permission in permissionList)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var result = await CheckPermissionAsync(
                            user, permission, resourceContext, authContext, cancellationToken);
                        results[permission] = result;
                    }, cancellationToken));
                }

                await Task.WhenAll(tasks);

                // Sonuçları mode'a göre değerlendir;
                var allGranted = results.Values.All(r => r.IsAuthorized);
                var anyGranted = results.Values.Any(r => r.IsAuthorized);
                var grantedPermissions = results.Where(r => r.Value.IsAuthorized).Select(r => r.Key).ToList();
                var deniedPermissions = results.Where(r => !r.Value.IsAuthorized).ToDictionary(r => r.Key, r => r.Value);

                var batchResult = new BatchAuthorizationResult;
                {
                    UserId = userId,
                    CheckedAt = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - startTime,
                    AllPermissions = permissionList,
                    GrantedPermissions = grantedPermissions,
                    DeniedPermissions = deniedPermissions,
                    IsFullyAuthorized = mode == BatchAuthorizationMode.All ? allGranted : anyGranted,
                    Mode = mode;
                };

                // Batch audit log;
                await LogBatchAuthorizationEventAsync(userId, batchResult, startTime, cancellationToken);

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking batch permissions for user");
                throw new AuthorizationException("Error checking batch permissions", ex);
            }
        }

        /// <summary>
        /// Dinamik olarak izin verir (runtime permission grant)
        /// </summary>
        public async Task<PermissionGrantResult> GrantPermissionAsync(
            string userId,
            string permission,
            PermissionGrantContext grantContext = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));
            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Permission cannot be empty", nameof(permission));

            grantContext ??= new PermissionGrantContext();

            try
            {
                await _cacheLock.WaitAsync(cancellationToken);

                // 1. Yetki verme yetkisi kontrolü;
                var canGrant = await CanGrantPermissionAsync(grantContext.GrantedBy, permission, grantContext, cancellationToken);
                if (!canGrant.IsAllowed)
                {
                    return PermissionGrantResult.Failure(
                        $"Cannot grant permission: {canGrant.DenialReason}",
                        canGrant.RequiredPrivileges);
                }

                // 2. Yetki zaten var mı kontrol et;
                var existingPermission = await _repository.GetUserPermissionAsync(userId, permission, cancellationToken);
                if (existingPermission != null)
                {
                    // Yetkiyi güncelle;
                    return await UpdateExistingPermissionAsync(userId, permission, grantContext, existingPermission, cancellationToken);
                }

                // 3. Yeni yetki oluştur;
                var permissionEntity = new UserPermissionEntity;
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Permission = permission,
                    GrantedAt = DateTime.UtcNow,
                    GrantedBy = grantContext.GrantedBy,
                    ExpiresAt = grantContext.ExpiresAt,
                    IsActive = true,
                    Metadata = grantContext.Metadata ?? new Dictionary<string, object>(),
                    Conditions = grantContext.Conditions ?? new List<PermissionCondition>(),
                    Scope = grantContext.Scope,
                    Reason = grantContext.Reason,
                    Priority = grantContext.Priority ?? PermissionPriority.Normal;
                };

                // 4. İleri düzey validasyonlar;
                var validation = await ValidatePermissionGrantAsync(permissionEntity, grantContext, cancellationToken);
                if (!validation.IsValid)
                {
                    return PermissionGrantResult.Failure(
                        $"Permission grant validation failed: {validation.ErrorMessage}",
                        validation.Violations);
                }

                // 5. Veritabanına kaydet;
                await _repository.AddUserPermissionAsync(permissionEntity, cancellationToken);

                // 6. Cache'i temizle;
                await ClearUserPermissionCacheAsync(userId, cancellationToken);

                // 7. Audit log;
                await LogPermissionGrantAsync(userId, permission, grantContext, cancellationToken);

                _logger.LogInformation("Permission {Permission} granted to user {UserId} by {GrantedBy}",
                    permission, userId, grantContext.GrantedBy);

                return PermissionGrantResult.Success(
                    permissionEntity.Id,
                    permissionEntity.ExpiresAt,
                    permissionEntity.Conditions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error granting permission {Permission} to user {UserId}",
                    permission, userId);
                throw new AuthorizationException($"Error granting permission {permission} to user {userId}", ex);
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// İzni iptal eder veya kaldırır;
        /// </summary>
        public async Task<PermissionRevokeResult> RevokePermissionAsync(
            string userId,
            string permission,
            PermissionRevokeContext revokeContext = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));
            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Permission cannot be empty", nameof(permission));

            revokeContext ??= new PermissionRevokeContext();

            try
            {
                await _cacheLock.WaitAsync(cancellationToken);

                // 1. İptal yetkisi kontrolü;
                var canRevoke = await CanRevokePermissionAsync(revokeContext.RevokedBy, permission, revokeContext, cancellationToken);
                if (!canRevoke.IsAllowed)
                {
                    return PermissionRevokeResult.Failure(
                        $"Cannot revoke permission: {canRevoke.DenialReason}",
                        canRevoke.RequiredPrivileges);
                }

                // 2. Yetkiyi bul;
                var permissionEntity = await _repository.GetUserPermissionAsync(userId, permission, cancellationToken);
                if (permissionEntity == null)
                {
                    return PermissionRevokeResult.Failure("Permission not found");
                }

                // 3. İptal işlemini gerçekleştir;
                PermissionRevokeResult result;
                switch (revokeContext.RevocationType)
                {
                    case RevocationType.SoftDelete:
                        permissionEntity.IsActive = false;
                        permissionEntity.RevokedAt = DateTime.UtcNow;
                        permissionEntity.RevokedBy = revokeContext.RevokedBy;
                        permissionEntity.RevocationReason = revokeContext.Reason;
                        await _repository.UpdateUserPermissionAsync(permissionEntity, cancellationToken);
                        result = PermissionRevokeResult.Success(permissionEntity.Id, RevocationType.SoftDelete);
                        break;

                    case RevocationType.HardDelete:
                        await _repository.RemoveUserPermissionAsync(permissionEntity.Id, cancellationToken);
                        result = PermissionRevokeResult.Success(permissionEntity.Id, RevocationType.HardDelete);
                        break;

                    case RevocationType.Suspend:
                        permissionEntity.IsSuspended = true;
                        permissionEntity.SuspendedUntil = revokeContext.SuspensionEnd;
                        permissionEntity.SuspendedBy = revokeContext.RevokedBy;
                        permissionEntity.SuspensionReason = revokeContext.Reason;
                        await _repository.UpdateUserPermissionAsync(permissionEntity, cancellationToken);
                        result = PermissionRevokeResult.Success(permissionEntity.Id, RevocationType.Suspend);
                        break;

                    default:
                        throw new ArgumentException($"Invalid revocation type: {revokeContext.RevocationType}");
                }

                // 4. Cache'i temizle;
                await ClearUserPermissionCacheAsync(userId, cancellationToken);

                // 5. Audit log;
                await LogPermissionRevokeAsync(userId, permission, revokeContext, cancellationToken);

                _logger.LogInformation("Permission {Permission} revoked from user {UserId} by {RevokedBy} (Type: {RevocationType})",
                    permission, userId, revokeContext.RevokedBy, revokeContext.RevocationType);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error revoking permission {Permission} from user {UserId}",
                    permission, userId);
                throw new AuthorizationException($"Error revoking permission {permission} from user {userId}", ex);
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Kullanıcının tüm izinlerini getirir;
        /// </summary>
        public async Task<UserPermissions> GetUserPermissionsAsync(
            string userId,
            bool includeInherited = true,
            bool includeExpired = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            try
            {
                var cacheKey = $"UserPermissions_{userId}_{includeInherited}_{includeExpired}";
                if (_config.EnableCaching && _memoryCache.TryGetValue(cacheKey, out UserPermissions cachedPermissions))
                {
                    return cachedPermissions;
                }

                var permissions = new UserPermissions;
                {
                    UserId = userId,
                    RetrievedAt = DateTime.UtcNow,
                    DirectPermissions = new List<UserPermission>(),
                    InheritedPermissions = new List<UserPermission>(),
                    RolePermissions = new List<RolePermission>(),
                    EffectivePermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                };

                // 1. Direkt izinleri getir;
                var directPermissionEntities = await _repository.GetUserPermissionsAsync(userId, includeExpired, cancellationToken);
                permissions.DirectPermissions = directPermissionEntities;
                    .Select(e => e.ToUserPermission())
                    .ToList();

                // 2. Rol bazlı izinleri getir;
                if (includeInherited)
                {
                    var userRoles = await _roleManager.GetUserRolesAsync(userId, cancellationToken);
                    foreach (var role in userRoles)
                    {
                        var rolePermissions = await _roleManager.GetRolePermissionsAsync(role.RoleId, cancellationToken);
                        permissions.RolePermissions.AddRange(rolePermissions);

                        // Inherited permissions olarak ekle;
                        permissions.InheritedPermissions.AddRange(
                            rolePermissions.Select(rp => new UserPermission;
                            {
                                Permission = rp.Permission,
                                GrantedAt = role.AssignedAt,
                                GrantedBy = "System",
                                Source = PermissionSource.Role,
                                SourceId = role.RoleId,
                                IsInherited = true;
                            }));
                    }
                }

                // 3. Grup bazlı izinleri getir (eğer destekleniyorsa)
                if (_config.EnableGroupPermissions && includeInherited)
                {
                    var groupPermissions = await GetGroupPermissionsAsync(userId, cancellationToken);
                    permissions.InheritedPermissions.AddRange(groupPermissions);
                }

                // 4. Etkin izinleri hesapla;
                permissions.EffectivePermissions.UnionWith(
                    permissions.DirectPermissions.Where(p => IsPermissionActive(p)).Select(p => p.Permission));
                permissions.EffectivePermissions.UnionWith(
                    permissions.InheritedPermissions.Where(p => IsPermissionActive(p)).Select(p => p.Permission));
                permissions.EffectivePermissions.UnionWith(
                    permissions.RolePermissions.Where(p => IsPermissionActive(p.ToUserPermission())).Select(p => p.Permission));

                // 5. İzinleri kategorilere ayır;
                permissions.PermissionCategories = CategorizePermissions(permissions.EffectivePermissions);

                // 6. İzin yoğunluğunu hesapla;
                permissions.PermissionDensity = CalculatePermissionDensity(permissions);

                // Cache'e kaydet;
                if (_config.EnableCaching)
                {
                    _memoryCache.Set(cacheKey, permissions, TimeSpan.FromMinutes(_config.UserPermissionsCacheMinutes));
                }

                return permissions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting permissions for user {UserId}", userId);
                throw new AuthorizationException($"Error getting permissions for user {userId}", ex);
            }
        }

        /// <summary>
        /// İzin dağıtımını doğrular ve uyumluluğu kontrol eder;
        /// </summary>
        public async Task<PermissionValidationResult> ValidatePermissionsAsync(
            string userId,
            PermissionValidationContext validationContext = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            validationContext ??= new PermissionValidationContext();

            try
            {
                var result = new PermissionValidationResult;
                {
                    UserId = userId,
                    ValidatedAt = DateTime.UtcNow,
                    Issues = new List<PermissionIssue>(),
                    Recommendations = new List<string>()
                };

                // 1. Kullanıcının tüm izinlerini getir;
                var userPermissions = await GetUserPermissionsAsync(userId, true, false, cancellationToken);

                // 2. Çakışan izinleri kontrol et;
                var conflicts = await FindPermissionConflictsAsync(userPermissions, cancellationToken);
                result.Issues.AddRange(conflicts);

                // 3. Aşırı ayrıcalıklı izinleri kontrol et;
                var excessivePermissions = await FindExcessivePermissionsAsync(userPermissions, cancellationToken);
                result.Issues.AddRange(excessivePermissions);

                // 4. Kullanılmayan izinleri kontrol et;
                if (validationContext.CheckUnusedPermissions)
                {
                    var unusedPermissions = await FindUnusedPermissionsAsync(userId, userPermissions, cancellationToken);
                    result.Issues.AddRange(unusedPermissions);
                }

                // 5. Süresi dolmuş ama aktif izinleri kontrol et;
                var expiredActivePermissions = await FindExpiredActivePermissionsAsync(userPermissions, cancellationToken);
                result.Issues.AddRange(expiredActivePermissions);

                // 6. Role-izin uyumluluğunu kontrol et;
                var roleComplianceIssues = await CheckRoleComplianceAsync(userPermissions, cancellationToken);
                result.Issues.AddRange(roleComplianceIssues);

                // 7. İzin dağılımı analizi;
                result.DistributionAnalysis = await AnalyzePermissionDistributionAsync(userPermissions, cancellationToken);

                // 8. Risk değerlendirmesi;
                result.RiskAssessment = await AssessPermissionRiskAsync(userPermissions, cancellationToken);

                // 9. Önerileri oluştur;
                result.Recommendations = GeneratePermissionRecommendations(result.Issues);

                // 10. Genel durumu belirle;
                result.IsValid = !result.Issues.Any(i => i.Severity >= PermissionIssueSeverity.High);
                result.OverallStatus = DetermineValidationStatus(result.Issues);

                // 11. Audit log;
                await LogPermissionValidationAsync(userId, result, cancellationToken);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating permissions for user {UserId}", userId);
                throw new AuthorizationException($"Error validating permissions for user {userId}", ex);
            }
        }

        #endregion;

        #region Advanced Authorization Methods;

        /// <summary>
        /// Dinamik politika oluşturur ve uygular;
        /// </summary>
        public async Task<PolicyCreationResult> CreateDynamicPolicyAsync(
            DynamicPolicyRequest policyRequest,
            CancellationToken cancellationToken = default)
        {
            if (policyRequest == null)
                throw new ArgumentNullException(nameof(policyRequest));

            try
            {
                // 1. Politika validasyonu;
                var validation = await ValidatePolicyRequestAsync(policyRequest, cancellationToken);
                if (!validation.IsValid)
                {
                    return PolicyCreationResult.Failure(
                        $"Policy validation failed: {validation.ErrorMessage}",
                        validation.Violations);
                }

                // 2. Politika varlığını kontrol et;
                var existingPolicy = await _repository.GetPolicyByNameAsync(policyRequest.Name, cancellationToken);
                if (existingPolicy != null && policyRequest.OverwriteIfExists == false)
                {
                    return PolicyCreationResult.Failure($"Policy '{policyRequest.Name}' already exists");
                }

                // 3. Politika entity'si oluştur;
                var policyEntity = new PolicyEntity;
                {
                    Id = Guid.NewGuid(),
                    Name = policyRequest.Name,
                    Description = policyRequest.Description,
                    Type = policyRequest.Type,
                    Target = policyRequest.Target,
                    Conditions = policyRequest.Conditions ?? new List<PolicyCondition>(),
                    Actions = policyRequest.Actions ?? new List<string>(),
                    Priority = policyRequest.Priority,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = policyRequest.CreatedBy,
                    EffectiveFrom = policyRequest.EffectiveFrom,
                    EffectiveUntil = policyRequest.EffectiveUntil,
                    Metadata = policyRequest.Metadata ?? new Dictionary<string, object>(),
                    Version = 1;
                };

                // 4. Politika çakışmasını kontrol et;
                var conflictCheck = await CheckPolicyConflictsAsync(policyEntity, cancellationToken);
                if (conflictCheck.HasConflicts)
                {
                    return PolicyCreationResult.Failure(
                        $"Policy conflicts detected: {string.Join(", ", conflictCheck.ConflictingPolicies)}",
                        conflictCheck.ConflictingPolicies);
                }

                // 5. Veritabanına kaydet;
                if (existingPolicy != null && policyRequest.OverwriteIfExists == true)
                {
                    // Mevcut politikayı güncelle;
                    existingPolicy.Description = policyEntity.Description;
                    existingPolicy.Conditions = policyEntity.Conditions;
                    existingPolicy.Actions = policyEntity.Actions;
                    existingPolicy.Priority = policyEntity.Priority;
                    existingPolicy.UpdatedAt = DateTime.UtcNow;
                    existingPolicy.UpdatedBy = policyEntity.CreatedBy;
                    existingPolicy.Version++;

                    await _repository.UpdatePolicyAsync(existingPolicy, cancellationToken);
                    policyEntity.Id = existingPolicy.Id;
                }
                else;
                {
                    await _repository.AddPolicyAsync(policyEntity, cancellationToken);
                }

                // 6. Cache'i temizle;
                await ClearPolicyCacheAsync(cancellationToken);

                // 7. Politika değerlendiricisini güncelle;
                await _policyEvaluator.RefreshPoliciesAsync(cancellationToken);

                // 8. Audit log;
                await LogPolicyCreationAsync(policyEntity, cancellationToken);

                _logger.LogInformation("Dynamic policy '{PolicyName}' created/updated by {CreatedBy}",
                    policyEntity.Name, policyEntity.CreatedBy);

                return PolicyCreationResult.Success(
                    policyEntity.Id,
                    policyEntity.Version,
                    policyEntity.EffectiveFrom,
                    policyEntity.EffectiveUntil);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating dynamic policy '{PolicyName}'", policyRequest.Name);
                throw new AuthorizationException($"Error creating dynamic policy '{policyRequest.Name}'", ex);
            }
        }

        /// <summary>
        /// Yetki devri (delegation) işlemi;
        /// </summary>
        public async Task<PermissionDelegationResult> DelegatePermissionAsync(
            string delegatorId,
            string delegateeId,
            string permission,
            DelegationContext delegationContext = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(delegatorId))
                throw new ArgumentException("Delegator ID cannot be empty", nameof(delegatorId));
            if (string.IsNullOrWhiteSpace(delegateeId))
                throw new ArgumentException("Delegatee ID cannot be empty", nameof(delegateeId));
            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Permission cannot be empty", nameof(permission));

            delegationContext ??= new DelegationContext();

            try
            {
                // 1. Yetki devir yetkisi kontrolü;
                var canDelegate = await CanDelegatePermissionAsync(delegatorId, permission, delegationContext, cancellationToken);
                if (!canDelegate.IsAllowed)
                {
                    return PermissionDelegationResult.Failure(
                        $"Cannot delegate permission: {canDelegate.DenialReason}",
                        canDelegate.RequiredPrivileges);
                }

                // 2. Delegasyon limitlerini kontrol et;
                var delegationLimitCheck = await CheckDelegationLimitsAsync(delegatorId, permission, cancellationToken);
                if (!delegationLimitCheck.IsWithinLimits)
                {
                    return PermissionDelegationResult.Failure(
                        $"Delegation limit exceeded: {delegationLimitCheck.LimitReason}");
                }

                // 3. Çakışma kontrolü;
                var conflictCheck = await CheckDelegationConflictAsync(delegatorId, delegateeId, permission, cancellationToken);
                if (conflictCheck.HasConflict)
                {
                    return PermissionDelegationResult.Failure(
                        $"Delegation conflict: {conflictCheck.ConflictReason}");
                }

                // 4. Delegasyon entity'si oluştur;
                var delegationEntity = new PermissionDelegationEntity;
                {
                    Id = Guid.NewGuid(),
                    DelegatorId = delegatorId,
                    DelegateeId = delegateeId,
                    Permission = permission,
                    DelegatedAt = DateTime.UtcNow,
                    EffectiveFrom = delegationContext.EffectiveFrom ?? DateTime.UtcNow,
                    EffectiveUntil = delegationContext.EffectiveUntil,
                    IsActive = true,
                    Constraints = delegationContext.Constraints ?? new List<DelegationConstraint>(),
                    Metadata = delegationContext.Metadata ?? new Dictionary<string, object>(),
                    Reason = delegationContext.Reason,
                    RequiresApproval = delegationContext.RequiresApproval,
                    ApprovalStatus = delegationContext.RequiresApproval ?
                        DelegationApprovalStatus.Pending : DelegationApprovalStatus.AutoApproved;
                };

                // 5. Onay gerekiyorsa onay sürecini başlat;
                if (delegationContext.RequiresApproval)
                {
                    var approvalResult = await StartDelegationApprovalAsync(delegationEntity, delegationContext, cancellationToken);
                    if (!approvalResult.IsStarted)
                    {
                        return PermissionDelegationResult.Failure(
                            $"Approval process failed to start: {approvalResult.ErrorMessage}");
                    }

                    delegationEntity.ApprovalWorkflowId = approvalResult.WorkflowId;
                }

                // 6. Veritabanına kaydet;
                await _repository.AddDelegationAsync(delegationEntity, cancellationToken);

                // 7. Cache'i temizle;
                await ClearDelegationCacheAsync(delegatorId, delegateeId, cancellationToken);

                // 8. Bildirim gönder;
                await SendDelegationNotificationAsync(delegatorId, delegateeId, permission, delegationContext, cancellationToken);

                // 9. Audit log;
                await LogPermissionDelegationAsync(delegatorId, delegateeId, permission, delegationContext, cancellationToken);

                _logger.LogInformation("Permission {Permission} delegated from {DelegatorId} to {DelegateeId}",
                    permission, delegatorId, delegateeId);

                return PermissionDelegationResult.Success(
                    delegationEntity.Id,
                    delegationEntity.ApprovalStatus,
                    delegationEntity.EffectiveUntil);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error delegating permission {Permission} from {DelegatorId} to {DelegateeId}",
                    permission, delegatorId, delegateeId);
                throw new AuthorizationException(
                    $"Error delegating permission {permission} from {delegatorId} to {delegateeId}", ex);
            }
        }

        /// <summary>
        /// İzin kullanım istatistiklerini getirir;
        /// </summary>
        public async Task<PermissionUsageStatistics> GetPermissionUsageStatisticsAsync(
            string permission = null,
            DateTime? from = null,
            DateTime? to = null,
            UsageStatisticsContext context = null,
            CancellationToken cancellationToken = default)
        {
            context ??= new UsageStatisticsContext();

            try
            {
                var cacheKey = $"PermissionUsageStats_{permission}_{from}_{to}_{context.Granularity}";
                if (_config.EnableCaching && _memoryCache.TryGetValue(cacheKey, out PermissionUsageStatistics cachedStats))
                {
                    return cachedStats;
                }

                var statistics = new PermissionUsageStatistics;
                {
                    Permission = permission,
                    PeriodFrom = from ?? DateTime.UtcNow.AddDays(-30),
                    PeriodTo = to ?? DateTime.UtcNow,
                    Granularity = context.Granularity,
                    GeneratedAt = DateTime.UtcNow;
                };

                // 1. Temel istatistikler;
                var basicStats = await _repository.GetPermissionUsageStatsAsync(
                    permission, statistics.PeriodFrom, statistics.PeriodTo, cancellationToken);

                statistics.TotalUsage = basicStats.TotalUsage;
                statistics.UniqueUsers = basicStats.UniqueUsers;
                statistics.AverageUsagePerUser = basicStats.AverageUsagePerUser;
                statistics.PeakUsageTime = basicStats.PeakUsageTime;

                // 2. Kullanım trendleri;
                statistics.UsageTrends = await GetUsageTrendsAsync(
                    permission, statistics.PeriodFrom, statistics.PeriodTo, context.Granularity, cancellationToken);

                // 3. En çok kullanan kullanıcılar;
                statistics.TopUsers = await GetTopPermissionUsersAsync(
                    permission, statistics.PeriodFrom, statistics.PeriodTo, 10, cancellationToken);

                // 4. Kaynak bazlı kullanım;
                statistics.ResourceUsage = await GetResourceBasedUsageAsync(
                    permission, statistics.PeriodFrom, statistics.PeriodTo, cancellationToken);

                // 5. Zaman bazlı pattern'ler;
                statistics.TimePatterns = await AnalyzeTimePatternsAsync(
                    permission, statistics.PeriodFrom, statistics.PeriodTo, cancellationToken);

                // 6. Anomali tespiti;
                statistics.Anomalies = await DetectUsageAnomaliesAsync(statistics, cancellationToken);

                // 7. Öneriler;
                statistics.Recommendations = GenerateUsageRecommendations(statistics);

                // Cache'e kaydet;
                if (_config.EnableCaching)
                {
                    _memoryCache.Set(cacheKey, statistics, TimeSpan.FromMinutes(_config.StatisticsCacheMinutes));
                }

                return statistics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting permission usage statistics for permission {Permission}", permission);
                throw new AuthorizationException($"Error getting usage statistics for permission {permission}", ex);
            }
        }

        #endregion;

        #region Private Helper Methods;

        private async Task<RbacCheckResult> CheckRbacPermissionAsync(
            ClaimsPrincipal user,
            string permission,
            ResourceContext resourceContext,
            CancellationToken cancellationToken)
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var result = new RbacCheckResult;
            {
                IsAuthorized = false,
                GrantedPermissions = new List<string>()
            };

            // 1. Kullanıcının direkt izinlerini kontrol et;
            var directPermissions = await _repository.GetUserPermissionsAsync(userId, false, cancellationToken);
            var hasDirectPermission = directPermissions.Any(p =>
                string.Equals(p.Permission, permission, StringComparison.OrdinalIgnoreCase) &&
                p.IsActive &&
                (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow));

            if (hasDirectPermission)
            {
                result.IsAuthorized = true;
                result.GrantedPermissions.Add(permission);
                result.AuthorizationMethod = "DirectPermission";
                return result;
            }

            // 2. Rol bazlı izinleri kontrol et;
            var userRoles = await _roleManager.GetUserRolesAsync(userId, cancellationToken);
            foreach (var userRole in userRoles)
            {
                var rolePermissions = await _roleManager.GetRolePermissionsAsync(userRole.RoleId, cancellationToken);
                var hasRolePermission = rolePermissions.Any(p =>
                    string.Equals(p.Permission, permission, StringComparison.OrdinalIgnoreCase));

                if (hasRolePermission)
                {
                    result.IsAuthorized = true;
                    result.GrantedPermissions.Add(permission);
                    result.GrantedRoles.Add(userRole.RoleId);
                    result.AuthorizationMethod = "RoleBased";
                    break;
                }
            }

            if (!result.IsAuthorized)
            {
                result.DenialReason = $"User does not have permission '{permission}' directly or through roles";
                result.RequiredPermissions = new List<string> { permission };
            }

            return result;
        }

        private async Task<TimeRestrictionResult> CheckTimeRestrictionsAsync(
            string userId,
            string permission,
            ResourceContext resourceContext,
            CancellationToken cancellationToken)
        {
            var result = new TimeRestrictionResult { IsAllowed = true };

            // Zaman kısıtlamalarını veritabanından getir;
            var restrictions = await _repository.GetTimeRestrictionsAsync(userId, permission, cancellationToken);
            if (!restrictions.Any())
                return result;

            var now = DateTime.UtcNow;
            var currentDay = now.DayOfWeek;
            var currentTime = now.TimeOfDay;

            foreach (var restriction in restrictions)
            {
                // Gün kontrolü;
                if (restriction.DaysOfWeek != null && !restriction.DaysOfWeek.Contains(currentDay))
                {
                    result.IsAllowed = false;
                    result.DenialReason = $"Permission not allowed on {currentDay}";
                    break;
                }

                // Saat aralığı kontrolü;
                if (restriction.StartTime.HasValue && restriction.EndTime.HasValue)
                {
                    if (currentTime < restriction.StartTime.Value || currentTime > restriction.EndTime.Value)
                    {
                        result.IsAllowed = false;
                        result.DenialReason = $"Permission only allowed between {restriction.StartTime} and {restriction.EndTime}";
                        break;
                    }
                }

                // Tarih aralığı kontrolü;
                if (restriction.ValidFrom.HasValue && restriction.ValidTo.HasValue)
                {
                    if (now < restriction.ValidFrom.Value || now > restriction.ValidTo.Value)
                    {
                        result.IsAllowed = false;
                        result.DenialReason = $"Permission only valid from {restriction.ValidFrom} to {restriction.ValidTo}";
                        break;
                    }
                }
            }

            return result;
        }

        private async Task<GeolocationRestrictionResult> CheckGeolocationRestrictionsAsync(
            string userId,
            string permission,
            string currentLocation,
            CancellationToken cancellationToken)
        {
            var result = new GeolocationRestrictionResult { IsAllowed = true };

            // Coğrafi kısıtlamaları getir;
            var restrictions = await _repository.GetGeolocationRestrictionsAsync(userId, permission, cancellationToken);
            if (!restrictions.Any())
                return result;

            // Basit coğrafi kontrol (gerçek implementasyonda coğrafi veritabanı kullanılır)
            var isAllowed = restrictions.Any(r =>
                string.Equals(r.AllowedLocation, currentLocation, StringComparison.OrdinalIgnoreCase) ||
                r.AllowedLocation == "*"); // Wildcard;

            if (!isAllowed)
            {
                result.IsAllowed = false;
                result.DenialReason = $"Permission not allowed from location '{currentLocation}'";
                result.AllowedLocations = restrictions.Select(r => r.AllowedLocation).ToList();
            }

            return result;
        }

        private async Task<RiskBasedAuthorizationResult> CheckRiskBasedAuthorizationAsync(
            string userId,
            string permission,
            AuthorizationContext authContext,
            CancellationToken cancellationToken)
        {
            var result = new RiskBasedAuthorizationResult { IsAllowed = true };

            // Risk faktörlerini değerlendir;
            var riskFactors = await EvaluateRiskFactorsAsync(userId, permission, authContext, cancellationToken);
            var totalRiskScore = riskFactors.Sum(f => f.Score * f.Weight);

            // Risk threshold kontrolü;
            if (totalRiskScore > _config.RiskAuthorizationThreshold)
            {
                result.IsAllowed = false;
                result.DenialReason = $"Risk score too high: {totalRiskScore} (threshold: {_config.RiskAuthorizationThreshold})";
                result.RiskScore = totalRiskScore;
                result.RiskFactors = riskFactors;

                // Ek doğrulama gerekip gerekmediğini belirle;
                result.AdditionalVerificationRequired = totalRiskScore > _config.AdditionalVerificationThreshold;
            }

            return result;
        }

        private string BuildCacheKey(string userId, string permission, ResourceContext resourceContext, AuthorizationContext authContext)
        {
            // Detaylı cache key oluştur;
            var keyParts = new List<string>
            {
                $"UID:{userId}",
                $"PERM:{permission}",
                $"RES:{resourceContext.ResourceId ?? "null"}",
                $"CTX:{authContext.Operation ?? "default"}"
            };

            if (!string.IsNullOrEmpty(authContext.Geolocation))
                keyParts.Add($"LOC:{authContext.Geolocation}");

            if (!string.IsNullOrEmpty(authContext.DeviceId))
                keyParts.Add($"DEV:{authContext.DeviceId}");

            return string.Join("|", keyParts);
        }

        private async Task<AuthorizationResult> GetCachedResultAsync(string cacheKey, CancellationToken cancellationToken)
        {
            if (_permissionCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                if (cacheEntry.ExpiresAt > DateTime.UtcNow)
                {
                    cacheEntry.LastAccessed = DateTime.UtcNow;
                    cacheEntry.AccessCount++;
                    return cacheEntry.Result;
                }

                // Expired entry'yi temizle;
                _permissionCache.TryRemove(cacheKey, out _);
            }

            return null;
        }

        private async Task CacheResultAsync(string cacheKey, AuthorizationResult result, CancellationToken cancellationToken)
        {
            if (!_config.EnableCaching || result == null)
                return;

            await _cacheLock.WaitAsync(cancellationToken);
            try
            {
                // Cache boyutunu kontrol et;
                if (_permissionCache.Count >= _config.MaxCacheEntries)
                {
                    // En az erişilen entry'leri temizle;
                    var entriesToRemove = _permissionCache;
                        .OrderBy(e => e.Value.LastAccessed)
                        .Take(_config.MaxCacheEntries / 4)
                        .Select(e => e.Key)
                        .ToList();

                    foreach (var key in entriesToRemove)
                    {
                        _permissionCache.TryRemove(key, out _);
                    }
                }

                var cacheEntry = new PermissionCacheEntry
                {
                    Result = result,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(_config.CacheExpirationMinutes),
                    AccessCount = 1;
                };

                _permissionCache[cacheKey] = cacheEntry
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private async Task ClearUserPermissionCacheAsync(string userId, CancellationToken cancellationToken)
        {
            var keysToRemove = _permissionCache.Keys;
                .Where(k => k.StartsWith($"UID:{userId}|"))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _permissionCache.TryRemove(key, out _);
            }

            // Memory cache'ten de temizle;
            var memoryCacheKey = $"UserPermissions_{userId}_";
            var keys = _memoryCache.GetKeys<string>().Where(k => k.StartsWith(memoryCacheKey));
            foreach (var key in keys)
            {
                _memoryCache.Remove(key);
            }
        }

        private async Task ClearPolicyCacheAsync(CancellationToken cancellationToken)
        {
            // Policy cache'ini temizle;
            var policyCacheKeys = _permissionCache.Keys;
                .Where(k => k.Contains("POLICY:"))
                .ToList();

            foreach (var key in policyCacheKeys)
            {
                _permissionCache.TryRemove(key, out _);
            }

            await _policyEvaluator.RefreshPoliciesAsync(cancellationToken);
        }

        private async Task ClearDelegationCacheAsync(string delegatorId, string delegateeId, CancellationToken cancellationToken)
        {
            var keysToRemove = _permissionCache.Keys;
                .Where(k => k.Contains($"UID:{delegatorId}") || k.Contains($"UID:{delegateeId}"))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _permissionCache.TryRemove(key, out _);
            }
        }

        private void CleanupExpiredCacheEntries()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _permissionCache;
                .Where(kvp => kvp.Value.ExpiresAt <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _permissionCache.TryRemove(key, out _);
            }

            _logger.LogDebug("Cleaned up {Count} expired cache entries", expiredKeys.Count);
        }

        private bool IsPermissionActive(UserPermission permission)
        {
            if (!permission.IsActive)
                return false;

            if (permission.ExpiresAt.HasValue && permission.ExpiresAt.Value <= DateTime.UtcNow)
                return false;

            if (permission.IsSuspended &&
                (!permission.SuspendedUntil.HasValue || permission.SuspendedUntil.Value > DateTime.UtcNow))
                return false;

            return true;
        }

        private Dictionary<string, List<string>> CategorizePermissions(HashSet<string> permissions)
        {
            var categories = new Dictionary<string, List<string>>();

            foreach (var permission in permissions)
            {
                var parts = permission.Split('.');
                if (parts.Length > 1)
                {
                    var category = parts[0];
                    if (!categories.ContainsKey(category))
                    {
                        categories[category] = new List<string>();
                    }
                    categories[category].Add(permission);
                }
                else;
                {
                    if (!categories.ContainsKey("General"))
                    {
                        categories["General"] = new List<string>();
                    }
                    categories["General"].Add(permission);
                }
            }

            return categories;
        }

        private PermissionDensity CalculatePermissionDensity(UserPermissions permissions)
        {
            var density = new PermissionDensity;
            {
                TotalPermissions = permissions.EffectivePermissions.Count,
                DirectPermissionCount = permissions.DirectPermissions.Count,
                InheritedPermissionCount = permissions.InheritedPermissions.Count,
                RolePermissionCount = permissions.RolePermissions.Count;
            };

            // Permission overlap hesapla;
            var allPermissions = new List<string>();
            allPermissions.AddRange(permissions.DirectPermissions.Select(p => p.Permission));
            allPermissions.AddRange(permissions.InheritedPermissions.Select(p => p.Permission));
            allPermissions.AddRange(permissions.RolePermissions.Select(p => p.Permission));

            density.UniquePermissionCount = allPermissions.Distinct().Count();
            density.OverlapCount = allPermissions.Count - density.UniquePermissionCount;
            density.OverlapPercentage = allPermissions.Count > 0 ?
                (density.OverlapCount * 100.0) / allPermissions.Count : 0;

            // Permission complexity hesapla;
            var avgPermissionLength = permissions.EffectivePermissions.Average(p => p.Length);
            var avgPermissionDepth = permissions.EffectivePermissions.Average(p => p.Count(c => c == '.') + 1);

            density.ComplexityScore = (avgPermissionLength * 0.3) + (avgPermissionDepth * 0.7);

            return density;
        }

        private async Task LogAuthorizationEventAsync(
            string userId,
            string permission,
            ResourceContext resourceContext,
            AuthorizationResult result,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            try
            {
                await _auditLogger.LogCriticalEventAsync(
                    userId,
                    result.IsAuthorized ? "PERMISSION_GRANTED" : "PERMISSION_DENIED",
                    resourceContext.ResourceId ?? permission,
                    result.IsAuthorized ? AuditSeverity.Info : AuditSeverity.Warning,
                    $"Permission check: {permission} - {(result.IsAuthorized ? "Granted" : "Denied")}",
                    new Dictionary<string, object>
                    {
                        ["Permission"] = permission,
                        ["AuthorizationResult"] = result.IsAuthorized ? "Granted" : "Denied",
                        ["AuthorizationMethod"] = result.AuthorizationMethod.ToString(),
                        ["ResourceId"] = resourceContext.ResourceId,
                        ["ResourceType"] = resourceContext.ResourceType,
                        ["Duration"] = (DateTime.UtcNow - startTime).TotalMilliseconds,
                        ["DenialReason"] = result.DenialReason,
                        ["RequiredPermissions"] = result.RequiredPermissions,
                        ["DetailedViolations"] = result.DetailedViolations;
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log authorization event for user {UserId}", userId);
            }
        }

        private async Task LogBatchAuthorizationEventAsync(
            string userId,
            BatchAuthorizationResult result,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            try
            {
                await _auditLogger.LogCriticalEventAsync(
                    userId,
                    "BATCH_PERMISSION_CHECK",
                    "MultipleResources",
                    result.IsFullyAuthorized ? AuditSeverity.Info : AuditSeverity.Warning,
                    $"Batch permission check: {result.GrantedPermissions.Count} granted, {result.DeniedPermissions.Count} denied",
                    new Dictionary<string, object>
                    {
                        ["TotalPermissions"] = result.AllPermissions.Count,
                        ["GrantedCount"] = result.GrantedPermissions.Count,
                        ["DeniedCount"] = result.DeniedPermissions.Count,
                        ["Mode"] = result.Mode.ToString(),
                        ["IsFullyAuthorized"] = result.IsFullyAuthorized,
                        ["Duration"] = result.Duration.TotalMilliseconds,
                        ["GrantedPermissions"] = result.GrantedPermissions,
                        ["DeniedPermissions"] = result.DeniedPermissions.Keys.ToList()
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log batch authorization event for user {UserId}", userId);
            }
        }

        #region Stub Methods (Actual implementation would be provided)

        private async Task<IEnumerable<UserPermission>> GetGroupPermissionsAsync(string userId, CancellationToken cancellationToken)
        {
            // Grup bazlı izinleri getir;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<UserPermission>();
        }

        private async Task<IEnumerable<PermissionIssue>> FindPermissionConflictsAsync(UserPermissions permissions, CancellationToken cancellationToken)
        {
            // Çakışan izinleri bul;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<PermissionIssue>();
        }

        private async Task<IEnumerable<PermissionIssue>> FindExcessivePermissionsAsync(UserPermissions permissions, CancellationToken cancellationToken)
        {
            // Aşırı ayrıcalıklı izinleri bul;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<PermissionIssue>();
        }

        private async Task<IEnumerable<PermissionIssue>> FindUnusedPermissionsAsync(string userId, UserPermissions permissions, CancellationToken cancellationToken)
        {
            // Kullanılmayan izinleri bul;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<PermissionIssue>();
        }

        private async Task<IEnumerable<PermissionIssue>> FindExpiredActivePermissionsAsync(UserPermissions permissions, CancellationToken cancellationToken)
        {
            // Süresi dolmuş ama aktif izinleri bul;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<PermissionIssue>();
        }

        private async Task<IEnumerable<PermissionIssue>> CheckRoleComplianceAsync(UserPermissions permissions, CancellationToken cancellationToken)
        {
            // Role-izin uyumluluğunu kontrol et;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<PermissionIssue>();
        }

        private async Task<PermissionDistributionAnalysis> AnalyzePermissionDistributionAsync(UserPermissions permissions, CancellationToken cancellationToken)
        {
            // İzin dağılımı analizi;
            await Task.Delay(10, cancellationToken);
            return new PermissionDistributionAnalysis();
        }

        private async Task<PermissionRiskAssessment> AssessPermissionRiskAsync(UserPermissions permissions, CancellationToken cancellationToken)
        {
            // İzin risk değerlendirmesi;
            await Task.Delay(10, cancellationToken);
            return new PermissionRiskAssessment();
        }

        private List<string> GeneratePermissionRecommendations(IEnumerable<PermissionIssue> issues)
        {
            // Öneriler oluştur;
            return new List<string>();
        }

        private PermissionValidationStatus DetermineValidationStatus(IEnumerable<PermissionIssue> issues)
        {
            // Genel durumu belirle;
            return PermissionValidationStatus.Valid;
        }

        private async Task<IEnumerable<RiskFactor>> EvaluateRiskFactorsAsync(string userId, string permission, AuthorizationContext context, CancellationToken cancellationToken)
        {
            // Risk faktörlerini değerlendir;
            await Task.Delay(10, cancellationToken);
            return Enumerable.Empty<RiskFactor>();
        }

        private async Task<CanGrantResult> CanGrantPermissionAsync(string granterId, string permission, PermissionGrantContext context, CancellationToken cancellationToken)
        {
            // Yetki verme yetkisi kontrolü;
            await Task.Delay(10, cancellationToken);
            return new CanGrantResult { IsAllowed = true };
        }

        private async Task<PermissionGrantResult> UpdateExistingPermissionAsync(string userId, string permission, PermissionGrantContext context, UserPermissionEntity existing, CancellationToken cancellationToken)
        {
            // Mevcut yetkiyi güncelle;
            await Task.Delay(10, cancellationToken);
            return PermissionGrantResult.Success(existing.Id, existing.ExpiresAt, existing.Conditions);
        }

        private async Task<PermissionValidation> ValidatePermissionGrantAsync(UserPermissionEntity permissionEntity, PermissionGrantContext context, CancellationToken cancellationToken)
        {
            // Yetki verme validasyonu;
            await Task.Delay(10, cancellationToken);
            return new PermissionValidation { IsValid = true };
        }

        private async Task LogPermissionGrantAsync(string userId, string permission, PermissionGrantContext context, CancellationToken cancellationToken)
        {
            // Yetki verme log'u;
            await Task.Delay(10, cancellationToken);
        }

        private async Task<CanRevokeResult> CanRevokePermissionAsync(string revokerId, string permission, PermissionRevokeContext context, CancellationToken cancellationToken)
        {
            // İptal yetkisi kontrolü;
            await Task.Delay(10, cancellationToken);
            return new CanRevokeResult { IsAllowed = true };
        }

        private async Task LogPermissionRevokeAsync(string userId, string permission, PermissionRevokeContext context, CancellationToken cancellationToken)
        {
            // Yetki iptal log'u;
            await Task.Delay(10, cancellationToken);
        }

        private async Task LogPermissionValidationAsync(string userId, PermissionValidationResult result, CancellationToken cancellationToken)
        {
            // İzin doğrulama log'u;
            await Task.Delay(10, cancellationToken);
        }

        private async Task<PolicyValidation> ValidatePolicyRequestAsync(DynamicPolicyRequest request, CancellationToken cancellationToken)
        {
            // Politika validasyonu;
            await Task.Delay(10, cancellationToken);
            return new PolicyValidation { IsValid = true };
        }

        private async Task<PolicyConflictCheck> CheckPolicyConflictsAsync(PolicyEntity policy, CancellationToken cancellationToken)
        {
            // Politika çakışması kontrolü;
            await Task.Delay(10, cancellationToken);
            return new PolicyConflictCheck { HasConflicts = false };
        }

        private async Task LogPolicyCreationAsync(PolicyEntity policy, CancellationToken cancellationToken)
        {
            // Politika oluşturma log'u;
            await Task.Delay(10, cancellationToken);
        }

        private async Task<CanDelegateResult> CanDelegatePermissionAsync(string delegatorId, string permission, DelegationContext context, CancellationToken cancellationToken)
        {
            // Yetki devir yetkisi kontrolü;
            await Task.Delay(10, cancellationToken);
            return new CanDelegateResult { IsAllowed = true };
        }

        private async Task<DelegationLimitCheck> CheckDelegationLimitsAsync(string delegatorId, string permission, CancellationToken cancellationToken)
        {
            // Delegasyon limit kontrolü;
            await Task.Delay(10, cancellationToken);
            return new DelegationLimitCheck { IsWithinLimits = true };
        }

        private async Task<DelegationConflictCheck> CheckDelegationConflictAsync(string delegatorId, string delegateeId, string permission, CancellationToken cancellationToken)
        {
            // Delegasyon çakışma kontrolü;
            await Task.Delay(10, cancellationToken);
            return new DelegationConflictCheck { HasConflict = false };
        }

        private async Task<ApprovalStartResult> StartDelegationApprovalAsync(PermissionDelegationEntity delegation, DelegationContext context, CancellationToken cancellationToken)
        {
            // Onay sürecini başlat;
            await Task.Delay(10, cancellationToken);
            return new ApprovalStartResult { IsStarted = true, WorkflowId = Guid.NewGuid().ToString() };
        }

        private async Task SendDelegationNotificationAsync(string delegatorId, string delegateeId, string permission, DelegationContext context, CancellationToken cancellationToken)
        {
            // Delegasyon bildirimi gönder;
            await Task.Delay(10, cancellationToken);
        }

        private async Task LogPermissionDelegationAsync(string delegatorId, string delegateeId, string permission, DelegationContext context, CancellationToken cancellationToken)
        {
            // Delegasyon log'u;
            await Task.Delay(10, cancellationToken);
        }

        private async Task<List<UsageTrend>> GetUsageTrendsAsync(string permission, DateTime from, DateTime to, StatisticsGranularity granularity, CancellationToken cancellationToken)
        {
            // Kullanım trendlerini getir;
            await Task.Delay(10, cancellationToken);
            return new List<UsageTrend>();
        }

        private async Task<List<TopPermissionUser>> GetTopPermissionUsersAsync(string permission, DateTime from, DateTime to, int topCount, CancellationToken cancellationToken)
        {
            // En çok kullanan kullanıcıları getir;
            await Task.Delay(10, cancellationToken);
            return new List<TopPermissionUser>();
        }

        private async Task<List<ResourceUsage>> GetResourceBasedUsageAsync(string permission, DateTime from, DateTime to, CancellationToken cancellationToken)
        {
            // Kaynak bazlı kullanımı getir;
            await Task.Delay(10, cancellationToken);
            return new List<ResourceUsage>();
        }

        private async Task<TimePatternAnalysis> AnalyzeTimePatternsAsync(string permission, DateTime from, DateTime to, CancellationToken cancellationToken)
        {
            // Zaman pattern'lerini analiz et;
            await Task.Delay(10, cancellationToken);
            return new TimePatternAnalysis();
        }

        private async Task<List<UsageAnomaly>> DetectUsageAnomaliesAsync(PermissionUsageStatistics statistics, CancellationToken cancellationToken)
        {
            // Kullanım anomalilerini tespit et;
            await Task.Delay(10, cancellationToken);
            return new List<UsageAnomaly>();
        }

        private List<string> GenerateUsageRecommendations(PermissionUsageStatistics statistics)
        {
            // Kullanım önerileri oluştur;
            return new List<string>();
        }

        #endregion;

        #endregion;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cacheCleanupTimer?.Dispose();
                    _cacheLock?.Dispose();
                }

                _disposed = true;
            }
        }
    }

    #region Core Interfaces;

    /// <summary>
    /// Permission service interface;
    /// </summary>
    public interface IPermissionService : IDisposable
    {
        Task<AuthorizationResult> CheckPermissionAsync(
            ClaimsPrincipal user,
            string permission,
            ResourceContext resourceContext = null,
            AuthorizationContext authContext = null,
            CancellationToken cancellationToken = default);

        Task<BatchAuthorizationResult> CheckPermissionsAsync(
            ClaimsPrincipal user,
            IEnumerable<string> permissions,
            ResourceContext resourceContext = null,
            AuthorizationContext authContext = null,
            BatchAuthorizationMode mode = BatchAuthorizationMode.All,
            CancellationToken cancellationToken = default);

        Task<PermissionGrantResult> GrantPermissionAsync(
            string userId,
            string permission,
            PermissionGrantContext grantContext = null,
            CancellationToken cancellationToken = default);

        Task<PermissionRevokeResult> RevokePermissionAsync(
            string userId,
            string permission,
            PermissionRevokeContext revokeContext = null,
            CancellationToken cancellationToken = default);

        Task<UserPermissions> GetUserPermissionsAsync(
            string userId,
            bool includeInherited = true,
            bool includeExpired = false,
            CancellationToken cancellationToken = default);

        Task<PermissionValidationResult> ValidatePermissionsAsync(
            string userId,
            PermissionValidationContext validationContext = null,
            CancellationToken cancellationToken = default);

        Task<PolicyCreationResult> CreateDynamicPolicyAsync(
            DynamicPolicyRequest policyRequest,
            CancellationToken cancellationToken = default);

        Task<PermissionDelegationResult> DelegatePermissionAsync(
            string delegatorId,
            string delegateeId,
            string permission,
            DelegationContext delegationContext = null,
            CancellationToken cancellationToken = default);

        Task<PermissionUsageStatistics> GetPermissionUsageStatisticsAsync(
            string permission = null,
            DateTime? from = null,
            DateTime? to = null,
            UsageStatisticsContext context = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Role manager interface;
    /// </summary>
    public interface IRoleManager;
    {
        Task<IEnumerable<UserRole>> GetUserRolesAsync(string userId, CancellationToken cancellationToken = default);
        Task<IEnumerable<RolePermission>> GetRolePermissionsAsync(string roleId, CancellationToken cancellationToken = default);
        Task<Role> GetRoleAsync(string roleId, CancellationToken cancellationToken = default);
        Task<IEnumerable<Role>> GetAllRolesAsync(CancellationToken cancellationToken = default);
        Task<bool> IsUserInRoleAsync(string userId, string roleId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Policy evaluator interface;
    /// </summary>
    public interface IPolicyEvaluator;
    {
        Task<PolicyEvaluationResult> EvaluateAsync(
            ClaimsPrincipal user,
            string permission,
            ResourceContext resourceContext,
            AuthorizationContext authContext,
            CancellationToken cancellationToken = default);

        Task RefreshPoliciesAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<Policy>> GetActivePoliciesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Permission repository interface;
    /// </summary>
    public interface IPermissionRepository;
    {
        // User Permissions;
        Task<UserPermissionEntity> GetUserPermissionAsync(string userId, string permission, CancellationToken cancellationToken = default);
        Task<IEnumerable<UserPermissionEntity>> GetUserPermissionsAsync(string userId, bool includeExpired, CancellationToken cancellationToken = default);
        Task AddUserPermissionAsync(UserPermissionEntity permission, CancellationToken cancellationToken = default);
        Task UpdateUserPermissionAsync(UserPermissionEntity permission, CancellationToken cancellationToken = default);
        Task RemoveUserPermissionAsync(Guid permissionId, CancellationToken cancellationToken = default);

        // Policies;
        Task<PolicyEntity> GetPolicyByNameAsync(string name, CancellationToken cancellationToken = default);
        Task AddPolicyAsync(PolicyEntity policy, CancellationToken cancellationToken = default);
        Task UpdatePolicyAsync(PolicyEntity policy, CancellationToken cancellationToken = default);

        // Delegations;
        Task AddDelegationAsync(PermissionDelegationEntity delegation, CancellationToken cancellationToken = default);

        // Restrictions;
        Task<IEnumerable<TimeRestriction>> GetTimeRestrictionsAsync(string userId, string permission, CancellationToken cancellationToken = default);
        Task<IEnumerable<GeolocationRestriction>> GetGeolocationRestrictionsAsync(string userId, string permission, CancellationToken cancellationToken = default);

        // Statistics;
        Task<PermissionUsageStats> GetPermissionUsageStatsAsync(string permission, DateTime from, DateTime to, CancellationToken cancellationToken = default);
    }

    #endregion;

    #region Configuration and Data Models;

    public class PermissionServiceConfig;
    {
        // Caching;
        public bool EnableCaching { get; set; } = true;
        public int MaxCacheEntries { get; set; } = 10000;
        public int CacheExpirationMinutes { get; set; } = 30;
        public int CacheCleanupIntervalMinutes { get; set; } = 5;
        public int UserPermissionsCacheMinutes { get; set; } = 15;
        public int StatisticsCacheMinutes { get; set; } = 60;

        // Authorization;
        public bool EnableTimeRestrictions { get; set; } = true;
        public bool EnableGeolocationRestrictions { get; set; } = false;
        public bool EnableRiskBasedAuthorization { get; set; } = true;
        public bool EnableGroupPermissions { get; set; } = true;

        // Risk;
        public int RiskAuthorizationThreshold { get; set; } = 70;
        public int AdditionalVerificationThreshold { get; set; } = 85;

        // Validation;
        public int MaxDelegationDepth { get; set; } = 3;
        public int MaxPermissionsPerUser { get; set; } = 500;

        // Performance;
        public int BatchAuthorizationParallelism { get; set; } = 10;
        public int PermissionValidationBatchSize { get; set; } = 100;

        public static PermissionServiceConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var config = new PermissionServiceConfig();

            var section = configuration.GetSection("Security:Authorization:PermissionService");
            if (section.Exists())
            {
                section.Bind(config);
            }

            return config;
        }
    }

    public class ResourceContext;
    {
        public string ResourceId { get; set; }
        public string ResourceType { get; set; }
        public Dictionary<string, object> Attributes { get; set; } = new();
        public string Environment { get; set; } = "Production";
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class AuthorizationContext;
    {
        public string Operation { get; set; } = "Access";
        public string Geolocation { get; set; }
        public string DeviceId { get; set; }
        public string IpAddress { get; set; }
        public string UserAgent { get; set; }
        public bool IsTrustedDevice { get; set; }
        public Dictionary<string, object> CustomContext { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class PermissionGrantContext;
    {
        public string GrantedBy { get; set; } = "System";
        public DateTime? ExpiresAt { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<PermissionCondition> Conditions { get; set; } = new();
        public string Scope { get; set; }
        public string Reason { get; set; }
        public PermissionPriority? Priority { get; set; }
    }

    public class PermissionRevokeContext;
    {
        public string RevokedBy { get; set; } = "System";
        public RevocationType RevocationType { get; set; } = RevocationType.SoftDelete;
        public string Reason { get; set; }
        public DateTime? SuspensionEnd { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class DelegationContext;
    {
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveUntil { get; set; }
        public List<DelegationConstraint> Constraints { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string Reason { get; set; }
        public bool RequiresApproval { get; set; }
        public List<string> Approvers { get; set; } = new();
    }

    public class PermissionValidationContext;
    {
        public bool CheckUnusedPermissions { get; set; } = true;
        public bool CheckPermissionConflicts { get; set; } = true;
        public bool CheckExcessivePermissions { get; set; } = true;
        public bool CheckRoleCompliance { get; set; } = true;
        public Dictionary<string, object> CustomRules { get; set; } = new();
    }

    public class UsageStatisticsContext;
    {
        public StatisticsGranularity Granularity { get; set; } = StatisticsGranularity.Daily;
        public bool IncludeAnomalyDetection { get; set; } = true;
        public bool IncludeRecommendations { get; set; } = true;
        public Dictionary<string, object> Filters { get; set; } = new();
    }

    #endregion;

    #region Result Classes;

    public class AuthorizationResult;
    {
        public bool IsAuthorized { get; set; }
        public string DenialReason { get; set; }
        public List<string> RequiredPermissions { get; set; }
        public AuthorizationMethod AuthorizationMethod { get; set; }
        public IEnumerable<string> DetailedViolations { get; set; }
        public List<string> GrantedPermissions { get; set; } = new();
        public List<Policy> EffectivePolicies { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();

        public static AuthorizationResult Allowed(
            List<string> grantedPermissions = null,
            List<Policy> effectivePolicies = null,
            AuthorizationMethod method = AuthorizationMethod.RBAC)
        {
            return new AuthorizationResult;
            {
                IsAuthorized = true,
                AuthorizationMethod = method,
                GrantedPermissions = grantedPermissions ?? new List<string>(),
                EffectivePolicies = effectivePolicies ?? new List<Policy>()
            };
        }

        public static AuthorizationResult Denied(
            string reason,
            List<string> requiredPermissions = null,
            AuthorizationMethod method = AuthorizationMethod.RBAC,
            IEnumerable<string> detailedViolations = null)
        {
            return new AuthorizationResult;
            {
                IsAuthorized = false,
                DenialReason = reason,
                RequiredPermissions = requiredPermissions ?? new List<string>(),
                AuthorizationMethod = method,
                DetailedViolations = detailedViolations ?? Enumerable.Empty<string>()
            };
        }
    }

    public class BatchAuthorizationResult;
    {
        public string UserId { get; set; }
        public DateTime CheckedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> AllPermissions { get; set; } = new();
        public List<string> GrantedPermissions { get; set; } = new();
        public Dictionary<string, AuthorizationResult> DeniedPermissions { get; set; } = new();
        public bool IsFullyAuthorized { get; set; }
        public BatchAuthorizationMode Mode { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new();

        public static BatchAuthorizationResult Empty => new();
    }

    public class PermissionGrantResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Guid? PermissionId { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<PermissionCondition> Conditions { get; set; } = new();
        public List<string> RequiredPrivileges { get; set; } = new();
        public List<string> Violations { get; set; } = new();

        public static PermissionGrantResult Success(
            Guid permissionId,
            DateTime? expiresAt = null,
            List<PermissionCondition> conditions = null)
        {
            return new PermissionGrantResult;
            {
                Success = true,
                PermissionId = permissionId,
                ExpiresAt = expiresAt,
                Conditions = conditions ?? new List<PermissionCondition>()
            };
        }

        public static PermissionGrantResult Failure(
            string errorMessage,
            List<string> requiredPrivileges = null,
            List<string> violations = null)
        {
            return new PermissionGrantResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                RequiredPrivileges = requiredPrivileges ?? new List<string>(),
                Violations = violations ?? new List<string>()
            };
        }
    }

    public class PermissionRevokeResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Guid? PermissionId { get; set; }
        public RevocationType RevocationType { get; set; }
        public List<string> RequiredPrivileges { get; set; } = new();

        public static PermissionRevokeResult Success(Guid permissionId, RevocationType revocationType)
        {
            return new PermissionRevokeResult;
            {
                Success = true,
                PermissionId = permissionId,
                RevocationType = revocationType;
            };
        }

        public static PermissionRevokeResult Failure(string errorMessage, List<string> requiredPrivileges = null)
        {
            return new PermissionRevokeResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                RequiredPrivileges = requiredPrivileges ?? new List<string>()
            };
        }
    }

    public class UserPermissions;
    {
        public string UserId { get; set; }
        public DateTime RetrievedAt { get; set; }
        public List<UserPermission> DirectPermissions { get; set; } = new();
        public List<UserPermission> InheritedPermissions { get; set; } = new();
        public List<RolePermission> RolePermissions { get; set; } = new();
        public HashSet<string> EffectivePermissions { get; set; } = new();
        public Dictionary<string, List<string>> PermissionCategories { get; set; } = new();
        public PermissionDensity PermissionDensity { get; set; } = new();
    }

    public class PermissionValidationResult;
    {
        public string UserId { get; set; }
        public DateTime ValidatedAt { get; set; }
        public bool IsValid { get; set; }
        public PermissionValidationStatus OverallStatus { get; set; }
        public List<PermissionIssue> Issues { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
        public PermissionDistributionAnalysis DistributionAnalysis { get; set; } = new();
        public PermissionRiskAssessment RiskAssessment { get; set; } = new();
    }

    public class PolicyCreationResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Guid? PolicyId { get; set; }
        public int? Version { get; set; }
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveUntil { get; set; }
        public List<string> ConflictingPolicies { get; set; } = new();
        public List<string> Violations { get; set; } = new();

        public static PolicyCreationResult Success(
            Guid policyId,
            int version,
            DateTime? effectiveFrom = null,
            DateTime? effectiveUntil = null)
        {
            return new PolicyCreationResult;
            {
                Success = true,
                PolicyId = policyId,
                Version = version,
                EffectiveFrom = effectiveFrom,
                EffectiveUntil = effectiveUntil;
            };
        }

        public static PolicyCreationResult Failure(string errorMessage, List<string> conflictingPolicies = null, List<string> violations = null)
        {
            return new PolicyCreationResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                ConflictingPolicies = conflictingPolicies ?? new List<string>(),
                Violations = violations ?? new List<string>()
            };
        }
    }

    public class PermissionDelegationResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Guid? DelegationId { get; set; }
        public DelegationApprovalStatus ApprovalStatus { get; set; }
        public DateTime? EffectiveUntil { get; set; }
        public List<string> RequiredPrivileges { get; set; } = new();

        public static PermissionDelegationResult Success(
            Guid delegationId,
            DelegationApprovalStatus approvalStatus,
            DateTime? effectiveUntil = null)
        {
            return new PermissionDelegationResult;
            {
                Success = true,
                DelegationId = delegationId,
                ApprovalStatus = approvalStatus,
                EffectiveUntil = effectiveUntil;
            };
        }

        public static PermissionDelegationResult Failure(string errorMessage, List<string> requiredPrivileges = null)
        {
            return new PermissionDelegationResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                RequiredPrivileges = requiredPrivileges ?? new List<string>()
            };
        }
    }

    public class PermissionUsageStatistics;
    {
        public string Permission { get; set; }
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public DateTime GeneratedAt { get; set; }
        public StatisticsGranularity Granularity { get; set; }
        public int TotalUsage { get; set; }
        public int UniqueUsers { get; set; }
        public double AverageUsagePerUser { get; set; }
        public DateTime? PeakUsageTime { get; set; }
        public List<UsageTrend> UsageTrends { get; set; } = new();
        public List<TopPermissionUser> TopUsers { get; set; } = new();
        public List<ResourceUsage> ResourceUsage { get; set; } = new();
        public TimePatternAnalysis TimePatterns { get; set; } = new();
        public List<UsageAnomaly> Anomalies { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    #endregion;

    #region Supporting Classes;

    internal class PermissionCacheEntry
    {
        public AuthorizationResult Result { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int AccessCount { get; set; }
    }

    internal class RbacCheckResult;
    {
        public bool IsAuthorized { get; set; }
        public string DenialReason { get; set; }
        public List<string> GrantedPermissions { get; set; } = new();
        public List<string> GrantedRoles { get; set; } = new();
        public List<string> RequiredPermissions { get; set; } = new();
        public string AuthorizationMethod { get; set; }
    }

    internal class TimeRestrictionResult;
    {
        public bool IsAllowed { get; set; }
        public string DenialReason { get; set; }
    }

    internal class GeolocationRestrictionResult;
    {
        public bool IsAllowed { get; set; }
        public string DenialReason { get; set; }
        public List<string> AllowedLocations { get; set; } = new();
    }

    internal class RiskBasedAuthorizationResult;
    {
        public bool IsAllowed { get; set; }
        public string DenialReason { get; set; }
        public double RiskScore { get; set; }
        public IEnumerable<RiskFactor> RiskFactors { get; set; } = new List<RiskFactor>();
        public bool AdditionalVerificationRequired { get; set; }
    }

    // Diğer internal ve data model sınıfları...
    // (UserPermission, RolePermission, Policy, PermissionCondition, vs.)

    #endregion;

    #region Enums;

    public enum AuthorizationMethod;
    {
        RBAC = 0,
        ABAC = 1,
        PBAC = 2,
        TimeBased = 3,
        GeolocationBased = 4,
        RiskBased = 5,
        Hybrid = 6;
    }

    public enum BatchAuthorizationMode;
    {
        All = 0,      // Tüm izinler gerekiyor;
        Any = 1,      // Herhangi bir izin yeterli;
        AtLeast = 2   // En az N izin gerekiyor;
    }

    public enum PermissionPriority;
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3;
    }

    public enum RevocationType;
    {
        SoftDelete = 0,
        HardDelete = 1,
        Suspend = 2;
    }

    public enum PermissionValidationStatus;
    {
        Valid = 0,
        Warning = 1,
        Invalid = 2,
        Critical = 3;
    }

    public enum PermissionIssueSeverity;
    {
        Info = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    public enum PermissionSource;
    {
        Direct = 0,
        Role = 1,
        Group = 2,
        Delegation = 3,
        Policy = 4;
    }

    public enum DelegationApprovalStatus;
    {
        Pending = 0,
        Approved = 1,
        Rejected = 2,
        AutoApproved = 3,
        Expired = 4;
    }

    public enum StatisticsGranularity;
    {
        Hourly = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3;
    }

    #endregion;

    #region Exceptions;

    public class AuthorizationException : Exception
    {
        public AuthorizationException(string message) : base(message) { }
        public AuthorizationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PermissionNotFoundException : AuthorizationException;
    {
        public PermissionNotFoundException(string userId, string permission)
            : base($"Permission '{permission}' not found for user '{userId}'") { }
    }

    public class InsufficientPrivilegesException : AuthorizationException;
    {
        public InsufficientPrivilegesException(string operation, IEnumerable<string> requiredPermissions)
            : base($"Insufficient privileges for operation '{operation}'. Required: {string.Join(", ", requiredPermissions)}") { }
    }

    public class PermissionConflictException : AuthorizationException;
    {
        public PermissionConflictException(string userId, IEnumerable<string> conflictingPermissions)
            : base($"Permission conflicts detected for user '{userId}': {string.Join(", ", conflictingPermissions)}") { }
    }

    #endregion;
}
