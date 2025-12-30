using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace NEDA.SecurityModules.AdvancedSecurity.Authorization;
{
    /// <summary>
    /// Role management operations;
    /// </summary>
    public enum RoleOperation;
    {
        Create = 0,
        Update = 1,
        Delete = 2,
        Assign = 3,
        Revoke = 4,
        Enable = 5,
        Disable = 6,
        Archive = 7,
        Restore = 8;
    }

    /// <summary>
    /// Role types;
    /// </summary>
    public enum RoleType;
    {
        System = 0,       // System-level roles (admin, superuser)
        Business = 1,     // Business function roles (manager, analyst)
        Functional = 2,   // Functional roles (read-only, write)
        Custom = 3,       // Custom user-defined roles;
        Temporary = 4,    // Temporary/contextual roles;
        Delegated = 5     // Delegated/acting roles;
    }

    /// <summary>
    /// Role inheritance mode;
    /// </summary>
    public enum InheritanceMode;
    {
        None = 0,
        Single = 1,
        Multiple = 2,
        All = 3;
    }

    /// <summary>
    /// Main RoleManager interface;
    /// </summary>
    public interface IRoleManager : IDisposable
    {
        /// <summary>
        /// Create new role with specified permissions;
        /// </summary>
        Task<Role> CreateRoleAsync(RoleCreateRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update existing role;
        /// </summary>
        Task<Role> UpdateRoleAsync(Guid roleId, RoleUpdateRequest request);

        /// <summary>
        /// Delete role (soft or hard delete)
        /// </summary>
        Task<bool> DeleteRoleAsync(Guid roleId, bool permanent = false);

        /// <summary>
        /// Assign role to user or group;
        /// </summary>
        Task<RoleAssignmentResult> AssignRoleAsync(RoleAssignmentRequest request);

        /// <summary>
        /// Revoke role from user or group;
        /// </summary>
        Task<bool> RevokeRoleAsync(Guid assignmentId, RevokeReason reason = RevokeReason.Manual);

        /// <summary>
        /// Get role with permissions and assignments;
        /// </summary>
        Task<Role> GetRoleAsync(Guid roleId, bool includePermissions = true, bool includeAssignments = false);

        /// <summary>
        /// Get all roles with filtering options;
        /// </summary>
        Task<RoleListResult> GetRolesAsync(RoleQueryOptions options = null);

        /// <summary>
        /// Get user's roles and effective permissions;
        /// </summary>
        Task<UserRoleInfo> GetUserRolesAsync(string userId, bool includeInherited = true);

        /// <summary>
        /// Check if user has specific permission;
        /// </summary>
        Task<PermissionCheckResult> CheckPermissionAsync(string userId, string permission, ResourceContext context = null);

        /// <summary>
        /// Add permission to role;
        /// </summary>
        Task<bool> AddPermissionToRoleAsync(Guid roleId, PermissionAddRequest request);

        /// <summary>
        /// Remove permission from role;
        /// </summary>
        Task<bool> RemovePermissionFromRoleAsync(Guid roleId, string permissionId);

        /// <summary>
        /// Create role hierarchy (parent-child relationship)
        /// </summary>
        Task<bool> CreateRoleHierarchyAsync(RoleHierarchyRequest request);

        /// <summary>
        /// Get role hierarchy tree;
        /// </summary>
        Task<RoleHierarchy> GetRoleHierarchyAsync(Guid? rootRoleId = null);

        /// <summary>
        /// Get role statistics and metrics;
        /// </summary>
        Task<RoleStatistics> GetRoleStatisticsAsync(TimeSpan? timeframe = null);

        /// <summary>
        /// Validate role assignments for compliance;
        /// </summary>
        Task<RoleComplianceResult> ValidateRoleComplianceAsync(ComplianceCheckOptions options = null);

        /// <summary>
        /// Perform role cleanup (orphaned roles, stale assignments)
        /// </summary>
        Task<RoleCleanupResult> CleanupRolesAsync(RoleCleanupOptions options = null);

        /// <summary>
        /// Export role definitions;
        /// </summary>
        Task<RoleExport> ExportRolesAsync(RoleExportOptions options);

        /// <summary>
        /// Import role definitions;
        /// </summary>
        Task<RoleImportResult> ImportRolesAsync(RoleImportRequest request);

        /// <summary>
        /// Get role audit trail;
        /// </summary>
        Task<RoleAuditTrail> GetRoleAuditTrailAsync(Guid roleId, AuditTrailOptions options = null);

        /// <summary>
        /// Get role health status;
        /// </summary>
        Task<RoleHealthStatus> GetHealthStatusAsync();
    }

    /// <summary>
    /// Main RoleManager implementation with comprehensive role-based access control;
    /// </summary>
    public class RoleManager : IRoleManager;
    {
        private readonly ILogger<RoleManager> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly IPermissionService _permissionService;
        private readonly IMemoryCache _memoryCache;
        private readonly RoleManagerConfig _config;
        private readonly IRoleRepository _repository;
        private readonly SemaphoreSlim _roleOperationLock = new SemaphoreSlim(1, 1);
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        // Cache keys;
        private const string ROLE_CACHE_PREFIX = "role_";
        private const string USER_ROLES_CACHE_PREFIX = "user_roles_";
        private const string PERMISSION_CACHE_PREFIX = "permission_check_";

        // Role evaluation engine;
        private readonly RoleEvaluationEngine _evaluationEngine;

        // Role hierarchy manager;
        private readonly RoleHierarchyManager _hierarchyManager;

        public RoleManager(
            ILogger<RoleManager> logger,
            IAuditLogger auditLogger,
            IPermissionService permissionService,
            IMemoryCache memoryCache,
            IOptions<RoleManagerConfig> configOptions,
            IRoleRepository repository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _permissionService = permissionService ?? throw new ArgumentNullException(nameof(permissionService));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            _evaluationEngine = new RoleEvaluationEngine(_logger, _repository);
            _hierarchyManager = new RoleHierarchyManager(_repository);

            // Setup periodic cleanup;
            if (_config.EnableAutomaticCleanup)
            {
                _cleanupTimer = new Timer(
                    ExecuteScheduledCleanupAsync,
                    null,
                    TimeSpan.FromHours(_config.CleanupIntervalHours),
                    TimeSpan.FromHours(_config.CleanupIntervalHours));
            }

            InitializeSystemRoles();

            _logger.LogInformation("RoleManager initialized with cache TTL: {CacheTTL} minutes",
                _config.CacheTTLMinutes);
        }

        private void InitializeSystemRoles()
        {
            try
            {
                // Ensure system roles exist;
                var systemRoles = new[]
                {
                    new RoleCreateRequest;
                    {
                        Name = "System Administrator",
                        Code = "SYS_ADMIN",
                        Description = "Full system access with all permissions",
                        RoleType = RoleType.System,
                        IsSystemRole = true,
                        IsEnabled = true,
                        Permissions = new List<string> { "*" } // Wildcard permission;
                    },
                    new RoleCreateRequest;
                    {
                        Name = "Security Administrator",
                        Code = "SEC_ADMIN",
                        Description = "Security and access management",
                        RoleType = RoleType.System,
                        IsSystemRole = true,
                        IsEnabled = true,
                        Permissions = new List<string>
                        {
                            "security.*",
                            "user.manage",
                            "role.manage",
                            "audit.view"
                        }
                    },
                    new RoleCreateRequest;
                    {
                        Name = "Auditor",
                        Code = "AUDITOR",
                        Description = "Audit and compliance review",
                        RoleType = RoleType.Business,
                        IsSystemRole = true,
                        IsEnabled = true,
                        Permissions = new List<string>
                        {
                            "audit.*",
                            "report.view",
                            "log.view"
                        }
                    }
                };

                // Create system roles if they don't exist;
                foreach (var roleRequest in systemRoles)
                {
                    if (!_repository.RoleExistsAsync(roleRequest.Code).GetAwaiter().GetResult())
                    {
                        _ = CreateRoleAsync(roleRequest, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }

                _logger.LogInformation("System roles initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize system roles");
                throw new RoleException("System role initialization failed", ex);
            }
        }

        public async Task<Role> CreateRoleAsync(RoleCreateRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _roleOperationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Creating new role: {RoleName} ({RoleCode})", request.Name, request.Code);

                // Validate request;
                var validationResult = await ValidateRoleCreateRequestAsync(request);
                if (!validationResult.IsValid)
                {
                    throw new RoleValidationException($"Invalid role creation request: {string.Join(", ", validationResult.Errors)}");
                }

                // Check if role with same code already exists;
                if (await _repository.RoleExistsAsync(request.Code))
                {
                    throw new RoleConflictException($"Role with code '{request.Code}' already exists");
                }

                // Validate permissions;
                var permissionValidation = await ValidatePermissionsAsync(request.Permissions);
                if (!permissionValidation.IsValid)
                {
                    throw new PermissionException($"Invalid permissions: {string.Join(", ", permissionValidation.InvalidPermissions)}");
                }

                // Create role entity;
                var role = new Role;
                {
                    Id = Guid.NewGuid(),
                    Name = request.Name,
                    Code = request.Code.ToUpperInvariant(),
                    Description = request.Description,
                    RoleType = request.RoleType,
                    IsSystemRole = request.IsSystemRole,
                    IsEnabled = request.IsEnabled,
                    IsArchived = false,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = request.CreatedBy ?? "system",
                    UpdatedAt = DateTime.UtcNow,
                    UpdatedBy = request.CreatedBy ?? "system",
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Version = 1;
                };

                // Store role;
                await _repository.CreateRoleAsync(role);

                // Add permissions;
                if (request.Permissions != null && request.Permissions.Any())
                {
                    foreach (var permission in request.Permissions.Distinct())
                    {
                        await _repository.AddRolePermissionAsync(role.Id, permission, request.PermissionScope);
                    }
                }

                // Add to hierarchy if parent specified;
                if (request.ParentRoleId.HasValue)
                {
                    await _hierarchyManager.AddToHierarchyAsync(role.Id, request.ParentRoleId.Value);
                }

                // Clear relevant caches;
                ClearRoleCaches();

                // Log role creation;
                await _auditLogger.LogRoleOperationAsync(new RoleAuditRecord;
                {
                    RoleId = role.Id,
                    RoleName = role.Name,
                    RoleCode = role.Code,
                    Operation = RoleOperation.Create,
                    PerformedBy = request.CreatedBy ?? "system",
                    Timestamp = DateTime.UtcNow,
                    Details = JsonConvert.SerializeObject(new;
                    {
                        Permissions = request.Permissions,
                        ParentRole = request.ParentRoleId,
                        Metadata = request.Metadata;
                    })
                });

                _logger.LogInformation("Role created successfully: {RoleId} ({RoleCode})", role.Id, role.Code);

                return role;
            }
            catch (Exception ex) when (!(ex is RoleException))
            {
                _logger.LogError(ex, "Failed to create role: {RoleName}", request.Name);
                throw new RoleException("Role creation failed", ex);
            }
            finally
            {
                _roleOperationLock.Release();
            }
        }

        public async Task<Role> UpdateRoleAsync(Guid roleId, RoleUpdateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await using var transaction = await _repository.BeginTransactionAsync();
            try
            {
                _logger.LogInformation("Updating role: {RoleId}", roleId);

                // Get existing role;
                var existingRole = await _repository.GetRoleAsync(roleId);
                if (existingRole == null)
                {
                    throw new RoleNotFoundException($"Role not found: {roleId}");
                }

                // Check if role can be modified;
                if (existingRole.IsSystemRole && !request.AllowSystemRoleModification)
                {
                    throw new RoleOperationException("System roles cannot be modified");
                }

                // Validate update request;
                var validationResult = ValidateRoleUpdateRequest(request);
                if (!validationResult.IsValid)
                {
                    throw new RoleValidationException($"Invalid role update: {string.Join(", ", validationResult.Errors)}");
                }

                // Update role properties;
                var originalRole = existingRole.Clone();

                if (!string.IsNullOrWhiteSpace(request.Name))
                    existingRole.Name = request.Name;

                if (!string.IsNullOrWhiteSpace(request.Description))
                    existingRole.Description = request.Description;

                if (request.IsEnabled.HasValue)
                    existingRole.IsEnabled = request.IsEnabled.Value;

                if (request.Metadata != null)
                {
                    foreach (var kvp in request.Metadata)
                    {
                        existingRole.Metadata[kvp.Key] = kvp.Value;
                    }
                }

                existingRole.UpdatedAt = DateTime.UtcNow;
                existingRole.UpdatedBy = request.UpdatedBy ?? "system";
                existingRole.Version++;

                // Update permissions if specified;
                if (request.PermissionsToAdd != null && request.PermissionsToAdd.Any())
                {
                    var permissionValidation = await ValidatePermissionsAsync(request.PermissionsToAdd);
                    if (!permissionValidation.IsValid)
                    {
                        throw new PermissionException($"Invalid permissions to add: {string.Join(", ", permissionValidation.InvalidPermissions)}");
                    }

                    foreach (var permission in request.PermissionsToAdd.Distinct())
                    {
                        await _repository.AddRolePermissionAsync(existingRole.Id, permission, request.PermissionScope);
                    }
                }

                if (request.PermissionsToRemove != null && request.PermissionsToRemove.Any())
                {
                    foreach (var permission in request.PermissionsToRemove.Distinct())
                    {
                        await _repository.RemoveRolePermissionAsync(existingRole.Id, permission);
                    }
                }

                // Update hierarchy if specified;
                if (request.ParentRoleId.HasValue)
                {
                    await _hierarchyManager.UpdateParentAsync(existingRole.Id, request.ParentRoleId.Value);
                }
                else if (request.ClearParentRole)
                {
                    await _hierarchyManager.RemoveFromHierarchyAsync(existingRole.Id);
                }

                // Save changes;
                await _repository.UpdateRoleAsync(existingRole);
                await transaction.CommitAsync();

                // Clear caches;
                ClearRoleCache(existingRole.Id);
                ClearUserRoleCachesForRole(existingRole.Id);

                // Log role update;
                await _auditLogger.LogRoleOperationAsync(new RoleAuditRecord;
                {
                    RoleId = existingRole.Id,
                    RoleName = existingRole.Name,
                    RoleCode = existingRole.Code,
                    Operation = RoleOperation.Update,
                    PerformedBy = request.UpdatedBy ?? "system",
                    Timestamp = DateTime.UtcNow,
                    Details = JsonConvert.SerializeObject(new;
                    {
                        Original = originalRole,
                        Updated = existingRole,
                        PermissionsAdded = request.PermissionsToAdd,
                        PermissionsRemoved = request.PermissionsToRemove;
                    })
                });

                _logger.LogInformation("Role updated successfully: {RoleId} (v{Version})", existingRole.Id, existingRole.Version);

                return existingRole;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to update role: {RoleId}", roleId);
                throw new RoleException("Role update failed", ex);
            }
        }

        public async Task<bool> DeleteRoleAsync(Guid roleId, bool permanent = false)
        {
            await _roleOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Deleting role: {RoleId}, Permanent: {Permanent}", roleId, permanent);

                // Get role;
                var role = await _repository.GetRoleAsync(roleId);
                if (role == null)
                {
                    _logger.LogWarning("Role not found for deletion: {RoleId}", roleId);
                    return false;
                }

                // Check if role can be deleted;
                if (role.IsSystemRole)
                {
                    throw new RoleOperationException("System roles cannot be deleted");
                }

                // Check if role has active assignments;
                var activeAssignments = await _repository.GetRoleAssignmentsCountAsync(roleId, activeOnly: true);
                if (activeAssignments > 0)
                {
                    throw new RoleOperationException($"Cannot delete role with {activeAssignments} active assignments");
                }

                if (permanent)
                {
                    // Permanent delete;
                    await _repository.DeleteRolePermanentlyAsync(roleId);

                    // Log permanent deletion;
                    await _auditLogger.LogRoleOperationAsync(new RoleAuditRecord;
                    {
                        RoleId = roleId,
                        RoleName = role.Name,
                        RoleCode = role.Code,
                        Operation = RoleOperation.Delete,
                        PerformedBy = "system",
                        Timestamp = DateTime.UtcNow,
                        Details = "Permanent deletion"
                    });
                }
                else;
                {
                    // Soft delete (archive)
                    role.IsArchived = true;
                    role.IsEnabled = false;
                    role.UpdatedAt = DateTime.UtcNow;
                    role.UpdatedBy = "system";

                    await _repository.UpdateRoleAsync(role);

                    // Log archival;
                    await _auditLogger.LogRoleOperationAsync(new RoleAuditRecord;
                    {
                        RoleId = roleId,
                        RoleName = role.Name,
                        RoleCode = role.Code,
                        Operation = RoleOperation.Archive,
                        PerformedBy = "system",
                        Timestamp = DateTime.UtcNow,
                        Details = "Role archived"
                    });
                }

                // Clear caches;
                ClearRoleCache(roleId);
                ClearUserRoleCachesForRole(roleId);

                _logger.LogInformation("Role {Action}: {RoleId} ({RoleName})",
                    permanent ? "permanently deleted" : "archived", roleId, role.Name);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete role: {RoleId}", roleId);
                throw new RoleException("Role deletion failed", ex);
            }
            finally
            {
                _roleOperationLock.Release();
            }
        }

        public async Task<RoleAssignmentResult> AssignRoleAsync(RoleAssignmentRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await using var transaction = await _repository.BeginTransactionAsync();
            try
            {
                _logger.LogInformation("Assigning role {RoleId} to {AssigneeType}: {AssigneeId}",
                    request.RoleId, request.AssigneeType, request.AssigneeId);

                // Validate request;
                var validationResult = ValidateRoleAssignmentRequest(request);
                if (!validationResult.IsValid)
                {
                    throw new RoleValidationException($"Invalid role assignment: {string.Join(", ", validationResult.Errors)}");
                }

                // Check if role exists and is active;
                var role = await _repository.GetRoleAsync(request.RoleId);
                if (role == null)
                {
                    throw new RoleNotFoundException($"Role not found: {request.RoleId}");
                }

                if (!role.IsEnabled || role.IsArchived)
                {
                    throw new RoleOperationException($"Role is not active: {role.Name}");
                }

                // Check if assignment already exists;
                var existingAssignment = await _repository.GetRoleAssignmentAsync(
                    request.AssigneeId, request.AssigneeType, request.RoleId);

                if (existingAssignment != null)
                {
                    if (existingAssignment.IsActive && !request.Force)
                    {
                        throw new RoleConflictException($"Active assignment already exists for {request.AssigneeId}");
                    }

                    // Reactivate existing assignment;
                    existingAssignment.IsActive = true;
                    existingAssignment.UpdatedAt = DateTime.UtcNow;
                    existingAssignment.UpdatedBy = request.AssignedBy ?? "system";
                    existingAssignment.ExpiresAt = request.ExpiresAt;
                    existingAssignment.Metadata = request.Metadata ?? new Dictionary<string, object>();

                    await _repository.UpdateRoleAssignmentAsync(existingAssignment);

                    _logger.LogDebug("Reactivated existing role assignment: {AssignmentId}", existingAssignment.Id);

                    return new RoleAssignmentResult;
                    {
                        Success = true,
                        AssignmentId = existingAssignment.Id,
                        Message = "Existing assignment reactivated",
                        Assignment = existingAssignment;
                    };
                }

                // Check assignment limits;
                await CheckAssignmentLimitsAsync(request);

                // Create new assignment;
                var assignment = new RoleAssignment;
                {
                    Id = Guid.NewGuid(),
                    RoleId = request.RoleId,
                    AssigneeId = request.AssigneeId,
                    AssigneeType = request.AssigneeType,
                    AssignedBy = request.AssignedBy ?? "system",
                    AssignedAt = DateTime.UtcNow,
                    IsActive = true,
                    ExpiresAt = request.ExpiresAt,
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                };

                await _repository.CreateRoleAssignmentAsync(assignment);
                await transaction.CommitAsync();

                // Clear user role cache;
                ClearUserRoleCache(request.AssigneeId);

                // Log assignment;
                await _auditLogger.LogRoleAssignmentAsync(new RoleAssignmentAuditRecord;
                {
                    AssignmentId = assignment.Id,
                    RoleId = request.RoleId,
                    RoleName = role.Name,
                    AssigneeId = request.AssigneeId,
                    AssigneeType = request.AssigneeType,
                    AssignedBy = request.AssignedBy ?? "system",
                    AssignedAt = DateTime.UtcNow,
                    ExpiresAt = request.ExpiresAt;
                });

                _logger.LogInformation("Role assigned successfully: {AssignmentId}", assignment.Id);

                return new RoleAssignmentResult;
                {
                    Success = true,
                    AssignmentId = assignment.Id,
                    Message = "Role assigned successfully",
                    Assignment = assignment;
                };
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to assign role: {RoleId} to {AssigneeId}",
                    request.RoleId, request.AssigneeId);
                throw new RoleException("Role assignment failed", ex);
            }
        }

        public async Task<bool> RevokeRoleAsync(Guid assignmentId, RevokeReason reason = RevokeReason.Manual)
        {
            try
            {
                _logger.LogInformation("Revoking role assignment: {AssignmentId}, Reason: {Reason}",
                    assignmentId, reason);

                // Get assignment;
                var assignment = await _repository.GetRoleAssignmentByIdAsync(assignmentId);
                if (assignment == null)
                {
                    _logger.LogWarning("Role assignment not found: {AssignmentId}", assignmentId);
                    return false;
                }

                if (!assignment.IsActive)
                {
                    _logger.LogWarning("Role assignment already inactive: {AssignmentId}", assignmentId);
                    return false;
                }

                // Get role info for logging;
                var role = await _repository.GetRoleAsync(assignment.RoleId);

                // Revoke assignment;
                assignment.IsActive = false;
                assignment.RevokedAt = DateTime.UtcNow;
                assignment.RevokeReason = reason;
                assignment.UpdatedAt = DateTime.UtcNow;

                await _repository.UpdateRoleAssignmentAsync(assignment);

                // Clear user role cache;
                ClearUserRoleCache(assignment.AssigneeId);

                // Log revocation;
                await _auditLogger.LogRoleRevocationAsync(new RoleRevocationAuditRecord;
                {
                    AssignmentId = assignmentId,
                    RoleId = assignment.RoleId,
                    RoleName = role?.Name,
                    AssigneeId = assignment.AssigneeId,
                    RevokedAt = DateTime.UtcNow,
                    Reason = reason,
                    RevokedBy = "system"
                });

                _logger.LogInformation("Role assignment revoked: {AssignmentId}", assignmentId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke role assignment: {AssignmentId}", assignmentId);
                throw new RoleException("Role revocation failed", ex);
            }
        }

        public async Task<Role> GetRoleAsync(Guid roleId, bool includePermissions = true, bool includeAssignments = false)
        {
            try
            {
                // Try cache first;
                var cacheKey = $"{ROLE_CACHE_PREFIX}{roleId}_perms{includePermissions}_assign{includeAssignments}";
                if (_memoryCache.TryGetValue(cacheKey, out Role cachedRole))
                {
                    _logger.LogDebug("Cache hit for role: {RoleId}", roleId);
                    return cachedRole;
                }

                _logger.LogDebug("Cache miss for role: {RoleId}, fetching from repository", roleId);

                // Get role from repository;
                var role = await _repository.GetRoleAsync(roleId);
                if (role == null)
                {
                    return null;
                }

                // Load permissions if requested;
                if (includePermissions)
                {
                    role.Permissions = await _repository.GetRolePermissionsAsync(roleId);
                }

                // Load assignments if requested;
                if (includeAssignments)
                {
                    role.Assignments = await _repository.GetRoleAssignmentsAsync(roleId);
                }

                // Load hierarchy info;
                role.HierarchyInfo = await _hierarchyManager.GetRoleHierarchyInfoAsync(roleId);

                // Cache the result;
                var cacheOptions = new MemoryCacheEntryOptions;
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_config.CacheTTLMinutes),
                    Size = 1 // Relative size for cache eviction;
                };

                _memoryCache.Set(cacheKey, role, cacheOptions);

                return role;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role: {RoleId}", roleId);
                throw new RoleException("Failed to retrieve role", ex);
            }
        }

        public async Task<RoleListResult> GetRolesAsync(RoleQueryOptions options = null)
        {
            options ??= new RoleQueryOptions();

            try
            {
                // Build cache key based on options;
                var cacheKey = BuildRolesCacheKey(options);
                if (_memoryCache.TryGetValue(cacheKey, out RoleListResult cachedResult))
                {
                    _logger.LogDebug("Cache hit for roles query");
                    return cachedResult;
                }

                _logger.LogDebug("Cache miss for roles query, fetching from repository");

                // Get roles from repository;
                var roles = await _repository.GetRolesAsync(options);

                // Get total count for pagination;
                var totalCount = await _repository.GetRolesCountAsync(options);

                // Apply sorting;
                roles = SortRoles(roles, options.SortBy, options.SortDirection);

                // Apply pagination;
                var pagedRoles = roles;
                    .Skip((options.PageNumber - 1) * options.PageSize)
                    .Take(options.PageSize)
                    .ToList();

                // Load additional data if requested;
                if (options.IncludePermissions)
                {
                    foreach (var role in pagedRoles)
                    {
                        role.Permissions = await _repository.GetRolePermissionsAsync(role.Id);
                    }
                }

                if (options.IncludeAssignmentCounts)
                {
                    foreach (var role in pagedRoles)
                    {
                        role.ActiveAssignmentCount = await _repository.GetRoleAssignmentsCountAsync(role.Id, activeOnly: true);
                        role.TotalAssignmentCount = await _repository.GetRoleAssignmentsCountAsync(role.Id, activeOnly: false);
                    }
                }

                var result = new RoleListResult;
                {
                    Roles = pagedRoles,
                    TotalCount = totalCount,
                    PageNumber = options.PageNumber,
                    PageSize = options.PageSize,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)options.PageSize)
                };

                // Cache the result;
                var cacheOptions = new MemoryCacheEntryOptions;
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_config.CacheTTLMinutes),
                    Size = pagedRoles.Count + 1;
                };

                _memoryCache.Set(cacheKey, result, cacheOptions);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get roles");
                throw new RoleException("Failed to retrieve roles", ex);
            }
        }

        public async Task<UserRoleInfo> GetUserRolesAsync(string userId, bool includeInherited = true)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            try
            {
                // Try cache first;
                var cacheKey = $"{USER_ROLES_CACHE_PREFIX}{userId}_inherited{includeInherited}";
                if (_memoryCache.TryGetValue(cacheKey, out UserRoleInfo cachedInfo))
                {
                    _logger.LogDebug("Cache hit for user roles: {UserId}", userId);
                    return cachedInfo;
                }

                _logger.LogDebug("Cache miss for user roles: {UserId}, fetching from repository", userId);

                // Get direct role assignments;
                var assignments = await _repository.GetUserRoleAssignmentsAsync(userId, activeOnly: true);

                // Get role details;
                var roles = new List<Role>();
                var effectivePermissions = new HashSet<string>();

                foreach (var assignment in assignments)
                {
                    var role = await GetRoleAsync(assignment.RoleId, includePermissions: true);
                    if (role != null && role.IsEnabled)
                    {
                        roles.Add(role);

                        // Add direct permissions;
                        if (role.Permissions != null)
                        {
                            foreach (var permission in role.Permissions)
                            {
                                effectivePermissions.Add(permission);
                            }
                        }

                        // Add inherited permissions if requested;
                        if (includeInherited && role.HierarchyInfo?.ParentRoles != null)
                        {
                            foreach (var parentRoleId in role.HierarchyInfo.ParentRoles)
                            {
                                var parentRole = await GetRoleAsync(parentRoleId, includePermissions: true);
                                if (parentRole?.Permissions != null)
                                {
                                    foreach (var permission in parentRole.Permissions)
                                    {
                                        effectivePermissions.Add(permission);
                                    }
                                }
                            }
                        }
                    }
                }

                // Get group memberships and their roles;
                var groupRoles = await GetGroupRolesForUserAsync(userId);
                roles.AddRange(groupRoles);

                foreach (var groupRole in groupRoles)
                {
                    if (groupRole.Permissions != null)
                    {
                        foreach (var permission in groupRole.Permissions)
                        {
                            effectivePermissions.Add(permission);
                        }
                    }
                }

                var userRoleInfo = new UserRoleInfo;
                {
                    UserId = userId,
                    Roles = roles.DistinctBy(r => r.Id).ToList(),
                    EffectivePermissions = effectivePermissions.ToList(),
                    LastUpdated = DateTime.UtcNow;
                };

                // Cache the result;
                var cacheOptions = new MemoryCacheEntryOptions;
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_config.CacheTTLMinutes),
                    Size = roles.Count + effectivePermissions.Count + 1;
                };

                _memoryCache.Set(cacheKey, userRoleInfo, cacheOptions);

                return userRoleInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user roles: {UserId}", userId);
                throw new RoleException("Failed to retrieve user roles", ex);
            }
        }

        public async Task<PermissionCheckResult> CheckPermissionAsync(
            string userId,
            string permission,
            ResourceContext context = null)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            if (string.IsNullOrWhiteSpace(permission))
                throw new ArgumentException("Permission cannot be empty", nameof(permission));

            try
            {
                // Try cache first;
                var cacheKey = $"{PERMISSION_CACHE_PREFIX}{userId}_{permission}_{ComputeContextHash(context)}";
                if (_memoryCache.TryGetValue(cacheKey, out PermissionCheckResult cachedResult))
                {
                    _logger.LogDebug("Cache hit for permission check: {UserId} -> {Permission}", userId, permission);
                    return cachedResult;
                }

                _logger.LogDebug("Checking permission: {UserId} -> {Permission}", userId, permission);

                // Get user roles and permissions;
                var userRoleInfo = await GetUserRolesAsync(userId, includeInherited: true);

                // Check if user has the permission;
                var hasPermission = userRoleInfo.EffectivePermissions.Any(p =>
                    PermissionMatcher.Matches(p, permission, context));

                // Get roles that grant this permission;
                var grantingRoles = userRoleInfo.Roles.Where(r =>
                    r.Permissions?.Any(p => PermissionMatcher.Matches(p, permission, context)) == true).ToList();

                var result = new PermissionCheckResult;
                {
                    UserId = userId,
                    Permission = permission,
                    HasPermission = hasPermission,
                    GrantingRoles = grantingRoles.Select(r => r.Name).ToList(),
                    CheckedAt = DateTime.UtcNow,
                    Context = context;
                };

                // Apply permission policies;
                await ApplyPermissionPoliciesAsync(result, userId, permission, context);

                // Cache the result (shorter TTL for permission checks)
                var cacheOptions = new MemoryCacheEntryOptions;
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), // Shorter TTL for permissions;
                    Size = 1;
                };

                _memoryCache.Set(cacheKey, result, cacheOptions);

                // Log permission check if configured;
                if (_config.LogPermissionChecks)
                {
                    await _auditLogger.LogPermissionCheckAsync(new PermissionAuditRecord;
                    {
                        UserId = userId,
                        Permission = permission,
                        HasPermission = result.HasPermission,
                        CheckedAt = DateTime.UtcNow,
                        Context = context?.ToString()
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Permission check failed: {UserId} -> {Permission}", userId, permission);
                return new PermissionCheckResult;
                {
                    UserId = userId,
                    Permission = permission,
                    HasPermission = false,
                    Error = ex.Message,
                    CheckedAt = DateTime.UtcNow;
                };
            }
        }

        public async Task<bool> AddPermissionToRoleAsync(Guid roleId, PermissionAddRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _roleOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Adding permission to role: {RoleId}, Permission: {Permission}",
                    roleId, request.Permission);

                // Validate request;
                if (string.IsNullOrWhiteSpace(request.Permission))
                {
                    throw new RoleValidationException("Permission cannot be empty");
                }

                // Check if role exists;
                var role = await _repository.GetRoleAsync(roleId);
                if (role == null)
                {
                    throw new RoleNotFoundException($"Role not found: {roleId}");
                }

                // Check if permission already exists;
                var existingPermissions = await _repository.GetRolePermissionsAsync(roleId);
                if (existingPermissions.Contains(request.Permission))
                {
                    _logger.LogWarning("Permission already exists for role: {RoleId} -> {Permission}",
                        roleId, request.Permission);
                    return false;
                }

                // Validate permission;
                var validation = await ValidatePermissionAsync(request.Permission);
                if (!validation.IsValid)
                {
                    throw new PermissionException($"Invalid permission: {validation.Error}");
                }

                // Add permission;
                await _repository.AddRolePermissionAsync(roleId, request.Permission, request.Scope);

                // Clear caches;
                ClearRoleCache(roleId);
                ClearUserRoleCachesForRole(roleId);

                // Log permission addition;
                await _auditLogger.LogRolePermissionChangeAsync(new RolePermissionAuditRecord;
                {
                    RoleId = roleId,
                    RoleName = role.Name,
                    Permission = request.Permission,
                    Operation = "ADD",
                    PerformedBy = request.ModifiedBy ?? "system",
                    Timestamp = DateTime.UtcNow,
                    Scope = request.Scope;
                });

                _logger.LogInformation("Permission added to role: {RoleId} -> {Permission}", roleId, request.Permission);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add permission to role: {RoleId}", roleId);
                throw new RoleException("Failed to add permission to role", ex);
            }
            finally
            {
                _roleOperationLock.Release();
            }
        }

        public async Task<bool> RemovePermissionFromRoleAsync(Guid roleId, string permissionId)
        {
            if (string.IsNullOrWhiteSpace(permissionId))
                throw new ArgumentException("Permission ID cannot be empty", nameof(permissionId));

            await _roleOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Removing permission from role: {RoleId}, Permission: {Permission}",
                    roleId, permissionId);

                // Check if role exists;
                var role = await _repository.GetRoleAsync(roleId);
                if (role == null)
                {
                    throw new RoleNotFoundException($"Role not found: {roleId}");
                }

                // Check if role is system role;
                if (role.IsSystemRole && permissionId == "*")
                {
                    throw new RoleOperationException("Cannot remove wildcard permission from system role");
                }

                // Remove permission;
                var removed = await _repository.RemoveRolePermissionAsync(roleId, permissionId);

                if (removed)
                {
                    // Clear caches;
                    ClearRoleCache(roleId);
                    ClearUserRoleCachesForRole(roleId);

                    // Log permission removal;
                    await _auditLogger.LogRolePermissionChangeAsync(new RolePermissionAuditRecord;
                    {
                        RoleId = roleId,
                        RoleName = role.Name,
                        Permission = permissionId,
                        Operation = "REMOVE",
                        PerformedBy = "system",
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Permission removed from role: {RoleId} -> {Permission}", roleId, permissionId);
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove permission from role: {RoleId}", roleId);
                throw new RoleException("Failed to remove permission from role", ex);
            }
            finally
            {
                _roleOperationLock.Release();
            }
        }

        public async Task<bool> CreateRoleHierarchyAsync(RoleHierarchyRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Creating role hierarchy: {ParentRoleId} -> {ChildRoleId}",
                    request.ParentRoleId, request.ChildRoleId);

                // Validate hierarchy request;
                var validationResult = await ValidateRoleHierarchyAsync(request);
                if (!validationResult.IsValid)
                {
                    throw new RoleValidationException($"Invalid hierarchy: {string.Join(", ", validationResult.Errors)}");
                }

                // Create hierarchy relationship;
                var success = await _hierarchyManager.CreateHierarchyAsync(request);

                if (success)
                {
                    // Clear caches for affected roles;
                    ClearRoleCache(request.ParentRoleId);
                    ClearRoleCache(request.ChildRoleId);
                    ClearUserRoleCachesForRole(request.ParentRoleId);
                    ClearUserRoleCachesForRole(request.ChildRoleId);

                    // Log hierarchy creation;
                    await _auditLogger.LogRoleHierarchyChangeAsync(new RoleHierarchyAuditRecord;
                    {
                        ParentRoleId = request.ParentRoleId,
                        ChildRoleId = request.ChildRoleId,
                        Operation = "CREATE",
                        PerformedBy = request.CreatedBy ?? "system",
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Role hierarchy created: {ParentRoleId} -> {ChildRoleId}",
                        request.ParentRoleId, request.ChildRoleId);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create role hierarchy");
                throw new RoleException("Failed to create role hierarchy", ex);
            }
        }

        public async Task<RoleHierarchy> GetRoleHierarchyAsync(Guid? rootRoleId = null)
        {
            try
            {
                _logger.LogDebug("Getting role hierarchy, Root: {RootRoleId}", rootRoleId);

                var hierarchy = await _hierarchyManager.GetHierarchyAsync(rootRoleId);

                _logger.LogDebug("Retrieved hierarchy with {RoleCount} roles", hierarchy?.AllRoles?.Count ?? 0);

                return hierarchy;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role hierarchy");
                throw new RoleException("Failed to retrieve role hierarchy", ex);
            }
        }

        public async Task<RoleStatistics> GetRoleStatisticsAsync(TimeSpan? timeframe = null)
        {
            try
            {
                var startTime = timeframe.HasValue;
                    ? DateTime.UtcNow.Subtract(timeframe.Value)
                    : DateTime.UtcNow.AddDays(-30); // Default 30 days;

                _logger.LogDebug("Getting role statistics from {StartTime}", startTime);

                var stats = new RoleStatistics;
                {
                    GeneratedAt = DateTime.UtcNow,
                    TimeframeStart = startTime,
                    TimeframeEnd = DateTime.UtcNow;
                };

                // Get basic counts;
                stats.TotalRoles = await _repository.GetTotalRolesCountAsync();
                stats.ActiveRoles = await _repository.GetActiveRolesCountAsync();
                stats.SystemRoles = await _repository.GetSystemRolesCountAsync();
                stats.CustomRoles = await _repository.GetCustomRolesCountAsync();

                // Get assignment statistics;
                stats.TotalAssignments = await _repository.GetTotalAssignmentsCountAsync();
                stats.ActiveAssignments = await _repository.GetActiveAssignmentsCountAsync();
                stats.UsersWithRoles = await _repository.GetUniqueUsersWithRolesCountAsync();

                // Get permission statistics;
                stats.TotalPermissions = await _repository.GetTotalPermissionsCountAsync();
                stats.UniquePermissions = await _repository.GetUniquePermissionsCountAsync();

                // Get activity statistics;
                stats.RolesCreated = await _repository.GetRolesCreatedCountAsync(startTime);
                stats.RolesUpdated = await _repository.GetRolesUpdatedCountAsync(startTime);
                stats.AssignmentsCreated = await _repository.GetAssignmentsCreatedCountAsync(startTime);
                stats.AssignmentsRevoked = await _repository.GetAssignmentsRevokedCountAsync(startTime);

                // Get top roles by assignment count;
                stats.TopRolesByAssignment = await _repository.GetTopRolesByAssignmentCountAsync(10);

                // Get role type distribution;
                stats.RoleTypeDistribution = await _repository.GetRoleTypeDistributionAsync();

                // Calculate average permissions per role;
                if (stats.ActiveRoles > 0)
                {
                    stats.AveragePermissionsPerRole = (double)stats.TotalPermissions / stats.ActiveRoles;
                }

                // Calculate average roles per user;
                if (stats.UsersWithRoles > 0)
                {
                    stats.AverageRolesPerUser = (double)stats.ActiveAssignments / stats.UsersWithRoles;
                }

                _logger.LogDebug("Generated role statistics: {TotalRoles} roles, {ActiveAssignments} active assignments",
                    stats.TotalRoles, stats.ActiveAssignments);

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role statistics");
                throw new RoleException("Failed to retrieve role statistics", ex);
            }
        }

        public async Task<RoleComplianceResult> ValidateRoleComplianceAsync(ComplianceCheckOptions options = null)
        {
            options ??= new ComplianceCheckOptions();

            try
            {
                _logger.LogInformation("Starting role compliance validation");

                var result = new RoleComplianceResult;
                {
                    ValidationId = Guid.NewGuid(),
                    StartedAt = DateTime.UtcNow;
                };

                // Check for orphaned roles (no assignments)
                var orphanedRoles = await _repository.GetOrphanedRolesAsync();
                result.OrphanedRoles = orphanedRoles.Select(r => r.Id).ToList();
                result.OrphanedRoleCount = orphanedRoles.Count;

                // Check for roles with excessive permissions;
                var rolesWithManyPermissions = await _repository.GetRolesWithExcessivePermissionsAsync(
                    options.MaxPermissionsPerRole ?? 50);
                result.RolesWithExcessivePermissions = rolesWithManyPermissions.Select(r => r.Id).ToList();

                // Check for users with too many roles;
                var usersWithManyRoles = await _repository.GetUsersWithExcessiveRolesAsync(
                    options.MaxRolesPerUser ?? 10);
                result.UsersWithExcessiveRoles = usersWithManyRoles;

                // Check for separation of duties violations;
                if (options.CheckSeparationOfDuties)
                {
                    var sodViolations = await CheckSeparationOfDutiesAsync(options.SodRules);
                    result.SeparationOfDutyViolations = sodViolations;
                }

                // Check for stale assignments;
                if (options.MaxAssignmentAge.HasValue)
                {
                    var staleAssignments = await _repository.GetStaleAssignmentsAsync(options.MaxAssignmentAge.Value);
                    result.StaleAssignments = staleAssignments;
                }

                // Check role naming conventions;
                if (options.EnforceNamingConventions)
                {
                    var namingViolations = await CheckRoleNamingConventionsAsync();
                    result.NamingConventionViolations = namingViolations;
                }

                // Calculate compliance score;
                result.ComplianceScore = CalculateComplianceScore(result);
                result.ComplianceLevel = DetermineComplianceLevel(result.ComplianceScore);

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Log compliance check;
                await _auditLogger.LogRoleComplianceCheckAsync(new RoleComplianceAuditRecord;
                {
                    ValidationId = result.ValidationId,
                    ComplianceScore = result.ComplianceScore,
                    IssuesFound = result.GetTotalIssues(),
                    StartedAt = result.StartedAt,
                    CompletedAt = result.CompletedAt;
                });

                _logger.LogInformation("Role compliance validation completed: Score {Score}%, Issues: {Issues}",
                    result.ComplianceScore, result.GetTotalIssues());

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Role compliance validation failed");
                throw new RoleException("Compliance validation failed", ex);
            }
        }

        public async Task<RoleCleanupResult> CleanupRolesAsync(RoleCleanupOptions options = null)
        {
            options ??= new RoleCleanupOptions();

            try
            {
                _logger.LogInformation("Starting role cleanup with options: {Options}", JsonConvert.SerializeObject(options));

                var result = new RoleCleanupResult;
                {
                    CleanupId = Guid.NewGuid(),
                    StartedAt = DateTime.UtcNow;
                };

                // Cleanup orphaned roles;
                if (options.CleanupOrphanedRoles)
                {
                    result.OrphanedRolesRemoved = await _repository.RemoveOrphanedRolesAsync(
                        options.OrphanedForAtLeast ?? TimeSpan.FromDays(90));
                }

                // Cleanup stale assignments;
                if (options.CleanupStaleAssignments)
                {
                    result.StaleAssignmentsRemoved = await _repository.RemoveStaleAssignmentsAsync(
                        options.StaleForAtLeast ?? TimeSpan.FromDays(180));
                }

                // Cleanup archived roles;
                if (options.CleanupArchivedRoles)
                {
                    result.ArchivedRolesRemoved = await _repository.RemoveArchivedRolesAsync(
                        options.ArchivedForAtLeast ?? TimeSpan.FromDays(365));
                }

                // Cleanup expired assignments;
                if (options.CleanupExpiredAssignments)
                {
                    result.ExpiredAssignmentsRemoved = await _repository.RemoveExpiredAssignmentsAsync();
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Clear caches;
                ClearAllCaches();

                // Log cleanup;
                await _auditLogger.LogRoleCleanupAsync(new RoleCleanupAuditRecord;
                {
                    CleanupId = result.CleanupId,
                    OrphanedRolesRemoved = result.OrphanedRolesRemoved,
                    StaleAssignmentsRemoved = result.StaleAssignmentsRemoved,
                    StartedAt = result.StartedAt,
                    CompletedAt = result.CompletedAt;
                });

                _logger.LogInformation("Role cleanup completed: {Orphaned} orphaned roles, {Stale} stale assignments removed",
                    result.OrphanedRolesRemoved, result.StaleAssignmentsRemoved);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Role cleanup failed");
                throw new RoleException("Role cleanup failed", ex);
            }
        }

        public async Task<RoleExport> ExportRolesAsync(RoleExportOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            try
            {
                _logger.LogInformation("Exporting roles with options: {Options}", JsonConvert.SerializeObject(options));

                var export = new RoleExport;
                {
                    ExportId = Guid.NewGuid(),
                    ExportedAt = DateTime.UtcNow,
                    Format = options.Format,
                    Options = options;
                };

                // Get roles based on options;
                var roles = new List<Role>();

                if (options.RoleIds != null && options.RoleIds.Any())
                {
                    foreach (var roleId in options.RoleIds)
                    {
                        var role = await GetRoleAsync(roleId,
                            includePermissions: options.IncludePermissions,
                            includeAssignments: options.IncludeAssignments);
                        if (role != null)
                        {
                            roles.Add(role);
                        }
                    }
                }
                else;
                {
                    var queryOptions = new RoleQueryOptions;
                    {
                        IncludeArchived = options.IncludeArchived,
                        RoleTypes = options.RoleTypes,
                        IsSystemRole = options.IncludeSystemRoles ? (bool?)null : false;
                    };

                    var roleResult = await GetRolesAsync(queryOptions);
                    roles = roleResult.Roles.ToList();
                }

                export.Roles = roles;
                export.RoleCount = roles.Count;

                // Include hierarchy if requested;
                if (options.IncludeHierarchy)
                {
                    export.Hierarchy = await GetRoleHierarchyAsync();
                }

                // Generate export data;
                export.Data = GenerateExportData(export, options.Format);

                // Log export;
                await _auditLogger.LogRoleExportAsync(new RoleExportAuditRecord;
                {
                    ExportId = export.ExportId,
                    RoleCount = export.RoleCount,
                    Format = options.Format,
                    ExportedBy = options.ExportedBy,
                    ExportedAt = DateTime.UtcNow;
                });

                _logger.LogInformation("Role export completed: {RoleCount} roles exported", export.RoleCount);

                return export;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Role export failed");
                throw new RoleException("Role export failed", ex);
            }
        }

        public async Task<RoleImportResult> ImportRolesAsync(RoleImportRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _roleOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Importing roles from {Source}, Mode: {ImportMode}",
                    request.Source, request.ImportMode);

                var result = new RoleImportResult;
                {
                    ImportId = Guid.NewGuid(),
                    StartedAt = DateTime.UtcNow,
                    ImportMode = request.ImportMode;
                };

                // Parse import data;
                var importData = ParseImportData(request.Data, request.Format);

                // Validate import data;
                var validationResult = await ValidateImportDataAsync(importData);
                if (!validationResult.IsValid)
                {
                    result.Success = false;
                    result.Errors = validationResult.Errors;
                    return result;
                }

                // Process import based on mode;
                switch (request.ImportMode)
                {
                    case ImportMode.CreateOnly:
                        result = await ImportCreateOnlyAsync(importData, result, request);
                        break;

                    case ImportMode.UpdateOnly:
                        result = await ImportUpdateOnlyAsync(importData, result, request);
                        break;

                    case ImportMode.CreateOrUpdate:
                        result = await ImportCreateOrUpdateAsync(importData, result, request);
                        break;

                    case ImportMode.ReplaceAll:
                        result = await ImportReplaceAllAsync(importData, result, request);
                        break;
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Clear caches;
                ClearAllCaches();

                // Log import;
                await _auditLogger.LogRoleImportAsync(new RoleImportAuditRecord;
                {
                    ImportId = result.ImportId,
                    ImportMode = request.ImportMode,
                    RolesCreated = result.RolesCreated,
                    RolesUpdated = result.RolesUpdated,
                    RolesSkipped = result.RolesSkipped,
                    ImportedBy = request.ImportedBy,
                    StartedAt = result.StartedAt,
                    CompletedAt = result.CompletedAt;
                });

                _logger.LogInformation("Role import completed: {Created} created, {Updated} updated, {Skipped} skipped",
                    result.RolesCreated, result.RolesUpdated, result.RolesSkipped);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Role import failed");
                throw new RoleException("Role import failed", ex);
            }
            finally
            {
                _roleOperationLock.Release();
            }
        }

        public async Task<RoleAuditTrail> GetRoleAuditTrailAsync(Guid roleId, AuditTrailOptions options = null)
        {
            if (roleId == Guid.Empty)
                throw new ArgumentException("Role ID cannot be empty", nameof(roleId));

            options ??= new AuditTrailOptions();

            try
            {
                _logger.LogDebug("Getting audit trail for role: {RoleId}", roleId);

                var auditTrail = new RoleAuditTrail;
                {
                    RoleId = roleId,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Get role operations audit;
                auditTrail.RoleOperations = await _auditLogger.GetRoleOperationsAsync(roleId,
                    options.StartTime, options.EndTime, options.OperationTypes);

                // Get role assignment audit;
                auditTrail.AssignmentOperations = await _auditLogger.GetRoleAssignmentOperationsAsync(roleId,
                    options.StartTime, options.EndTime);

                // Get permission change audit;
                auditTrail.PermissionChanges = await _auditLogger.GetRolePermissionChangesAsync(roleId,
                    options.StartTime, options.EndTime);

                // Get hierarchy change audit;
                auditTrail.HierarchyChanges = await _auditLogger.GetRoleHierarchyChangesAsync(roleId,
                    options.StartTime, options.EndTime);

                auditTrail.TotalEntries = auditTrail.RoleOperations.Count +
                                         auditTrail.AssignmentOperations.Count +
                                         auditTrail.PermissionChanges.Count +
                                         auditTrail.HierarchyChanges.Count;

                _logger.LogDebug("Retrieved audit trail with {EntryCount} entries for role: {RoleId}",
                    auditTrail.TotalEntries, roleId);

                return auditTrail;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audit trail for role: {RoleId}", roleId);
                throw new RoleException("Failed to retrieve audit trail", ex);
            }
        }

        public async Task<RoleHealthStatus> GetHealthStatusAsync()
        {
            try
            {
                var status = new RoleHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ServiceStatus = ServiceStatus.Healthy;
                };

                // Check repository connection;
                status.RepositoryHealthy = await _repository.CheckHealthAsync();

                // Check audit logger connection;
                status.AuditLoggerHealthy = await _auditLogger.CheckHealthAsync();

                // Check cache health;
                status.CacheHealthy = _memoryCache is MemoryCache;

                // Get statistics;
                var stats = await GetRoleStatisticsAsync(TimeSpan.FromHours(1));

                status.RolesCreatedLastHour = stats.RolesCreated;
                status.RolesUpdatedLastHour = stats.RolesUpdated;
                status.AssignmentsCreatedLastHour = stats.AssignmentsCreated;
                status.AssignmentsRevokedLastHour = stats.AssignmentsRevoked;

                // Check for anomalies;
                status.Anomalies = await DetectHealthAnomaliesAsync(stats);

                // Check system roles;
                var systemRoles = await _repository.GetSystemRolesAsync();
                status.SystemRolesHealthy = systemRoles.All(r => r.IsEnabled);
                status.MissingSystemRoles = systemRoles.Where(r => !r.IsEnabled).Select(r => r.Name).ToList();

                // Determine overall status;
                if (!status.RepositoryHealthy || !status.AuditLoggerHealthy)
                {
                    status.ServiceStatus = ServiceStatus.Critical;
                }
                else if (!status.SystemRolesHealthy || status.Anomalies.Any())
                {
                    status.ServiceStatus = ServiceStatus.Degraded;
                }

                return status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get role health status");
                return new RoleHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ServiceStatus = ServiceStatus.Unhealthy,
                    HealthCheckError = ex.Message;
                };
            }
        }

        #region Private Helper Methods;

        private async Task<ValidationResult> ValidateRoleCreateRequestAsync(RoleCreateRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.Name))
                errors.Add("Role name is required");

            if (string.IsNullOrWhiteSpace(request.Code))
                errors.Add("Role code is required");

            if (request.Code.Length > 50)
                errors.Add("Role code cannot exceed 50 characters");

            if (!IsValidRoleCode(request.Code))
                errors.Add("Role code can only contain letters, numbers, and underscores");

            if (request.Permissions != null && request.Permissions.Count > _config.MaxPermissionsPerRole)
                errors.Add($"Maximum {_config.MaxPermissionsPerRole} permissions per role exceeded");

            // Check parent role if specified;
            if (request.ParentRoleId.HasValue)
            {
                var parentRole = await _repository.GetRoleAsync(request.ParentRoleId.Value);
                if (parentRole == null)
                {
                    errors.Add("Parent role not found");
                }
                else if (parentRole.IsArchived || !parentRole.IsEnabled)
                {
                    errors.Add("Parent role is not active");
                }
            }

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private ValidationResult ValidateRoleUpdateRequest(RoleUpdateRequest request)
        {
            var errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(request.Name) && request.Name.Length > 200)
                errors.Add("Role name cannot exceed 200 characters");

            if (request.PermissionsToAdd != null && request.PermissionsToAdd.Count > 100)
                errors.Add("Too many permissions to add at once (max 100)");

            if (request.PermissionsToRemove != null && request.PermissionsToRemove.Count > 100)
                errors.Add("Too many permissions to remove at once (max 100)");

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private async Task<PermissionValidationResult> ValidatePermissionsAsync(IEnumerable<string> permissions)
        {
            var result = new PermissionValidationResult();

            if (permissions == null)
                return result;

            foreach (var permission in permissions)
            {
                var validation = await ValidatePermissionAsync(permission);
                if (!validation.IsValid)
                {
                    result.InvalidPermissions.Add(permission);
                    result.Errors.Add(validation.Error);
                }
                else;
                {
                    result.ValidPermissions.Add(permission);
                }
            }

            result.IsValid = !result.InvalidPermissions.Any();
            return result;
        }

        private async Task<PermissionValidation> ValidatePermissionAsync(string permission)
        {
            // Check if permission is valid format;
            if (string.IsNullOrWhiteSpace(permission))
                return PermissionValidation.Invalid("Permission cannot be empty");

            // Wildcard permission is always valid;
            if (permission == "*")
                return PermissionValidation.Valid();

            // Check with permission service;
            var isValid = await _permissionService.ValidatePermissionAsync(permission);

            return isValid;
                ? PermissionValidation.Valid()
                : PermissionValidation.Invalid($"Invalid permission format: {permission}");
        }

        private ValidationResult ValidateRoleAssignmentRequest(RoleAssignmentRequest request)
        {
            var errors = new List<string>();

            if (request.RoleId == Guid.Empty)
                errors.Add("Role ID is required");

            if (string.IsNullOrWhiteSpace(request.AssigneeId))
                errors.Add("Assignee ID is required");

            if (!Enum.IsDefined(typeof(AssigneeType), request.AssigneeType))
                errors.Add("Invalid assignee type");

            if (request.ExpiresAt.HasValue && request.ExpiresAt <= DateTime.UtcNow)
                errors.Add("Expiration date must be in the future");

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private async Task CheckAssignmentLimitsAsync(RoleAssignmentRequest request)
        {
            // Check max roles per user;
            if (request.AssigneeType == AssigneeType.User)
            {
                var userRoleCount = await _repository.GetUserRoleAssignmentsCountAsync(request.AssigneeId, activeOnly: true);
                if (userRoleCount >= _config.MaxRolesPerUser)
                {
                    throw new RoleLimitException($"User already has {userRoleCount} roles (max: {_config.MaxRolesPerUser})");
                }
            }

            // Check max users per role;
            var roleAssignmentCount = await _repository.GetRoleAssignmentsCountAsync(request.RoleId, activeOnly: true);
            if (roleAssignmentCount >= _config.MaxAssignmentsPerRole)
            {
                throw new RoleLimitException($"Role already has {roleAssignmentCount} assignments (max: {_config.MaxAssignmentsPerRole})");
            }
        }

        private async Task<ValidationResult> ValidateRoleHierarchyAsync(RoleHierarchyRequest request)
        {
            var errors = new List<string>();

            // Check for self-reference;
            if (request.ParentRoleId == request.ChildRoleId)
            {
                errors.Add("Role cannot be parent of itself");
            }

            // Check if roles exist;
            var parentRole = await _repository.GetRoleAsync(request.ParentRoleId);
            var childRole = await _repository.GetRoleAsync(request.ChildRoleId);

            if (parentRole == null) errors.Add("Parent role not found");
            if (childRole == null) errors.Add("Child role not found");

            if (parentRole != null && childRole != null)
            {
                // Check for circular references;
                var wouldCreateCycle = await _hierarchyManager.WouldCreateCycleAsync(request.ParentRoleId, request.ChildRoleId);
                if (wouldCreateCycle)
                {
                    errors.Add("Hierarchy would create a circular reference");
                }

                // Check if child is a system role;
                if (childRole.IsSystemRole && !request.AllowSystemRoleInheritance)
                {
                    errors.Add("Cannot make system role a child in hierarchy");
                }
            }

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private async Task<List<Role>> GetGroupRolesForUserAsync(string userId)
        {
            // This would typically query a group membership service;
            // For now, return empty list - implementation would be service-specific;
            return new List<Role>();
        }

        private async Task ApplyPermissionPoliciesAsync(
            PermissionCheckResult result,
            string userId,
            string permission,
            ResourceContext context)
        {
            // Apply time-based restrictions;
            if (_config.TimeBasedRestrictionsEnabled)
            {
                var timePolicyResult = await ApplyTimeBasedPolicyAsync(userId, permission);
                if (!timePolicyResult.Allowed)
                {
                    result.HasPermission = false;
                    result.DeniedByPolicies.Add(timePolicyResult);
                }
            }

            // Apply location-based restrictions;
            if (_config.LocationBasedRestrictionsEnabled && context?.Location != null)
            {
                var locationPolicyResult = await ApplyLocationBasedPolicyAsync(userId, permission, context.Location);
                if (!locationPolicyResult.Allowed)
                {
                    result.HasPermission = false;
                    result.DeniedByPolicies.Add(locationPolicyResult);
                }
            }

            // Apply separation of duties;
            if (_config.SeparationOfDutiesEnabled)
            {
                var sodResult = await CheckSeparationOfDutiesForUserAsync(userId, permission);
                if (!sodResult.Allowed)
                {
                    result.HasPermission = false;
                    result.DeniedByPolicies.Add(sodResult);
                }
            }
        }

        private async Task<List<SeparationOfDutyViolation>> CheckSeparationOfDutiesAsync(List<SodRule> sodRules)
        {
            var violations = new List<SeparationOfDutyViolation>();

            if (sodRules == null || !sodRules.Any())
                return violations;

            foreach (var rule in sodRules)
            {
                var usersInViolation = await _repository.GetUsersViolatingSodRuleAsync(rule);
                if (usersInViolation.Any())
                {
                    violations.Add(new SeparationOfDutyViolation;
                    {
                        RuleId = rule.Id,
                        RuleName = rule.Name,
                        ConflictingRoles = rule.ConflictingRoles,
                        ViolatingUsers = usersInViolation;
                    });
                }
            }

            return violations;
        }

        private async Task<List<RoleNamingViolation>> CheckRoleNamingConventionsAsync()
        {
            var violations = new List<RoleNamingViolation>();

            var roles = await _repository.GetAllRolesAsync();

            foreach (var role in roles)
            {
                // Check naming convention;
                if (!IsValidRoleName(role.Name))
                {
                    violations.Add(new RoleNamingViolation;
                    {
                        RoleId = role.Id,
                        RoleName = role.Name,
                        Rule = "Role name must start with capital letter and contain only letters, numbers, and spaces",
                        SuggestedName = SuggestRoleName(role.Name)
                    });
                }

                // Check code convention;
                if (!IsValidRoleCode(role.Code))
                {
                    violations.Add(new RoleNamingViolation;
                    {
                        RoleId = role.Id,
                        RoleCode = role.Code,
                        Rule = "Role code must be uppercase with underscores",
                        SuggestedCode = SuggestRoleCode(role.Code)
                    });
                }
            }

            return violations;
        }

        private double CalculateComplianceScore(RoleComplianceResult result)
        {
            double score = 100.0;

            // Deductions for each issue type;
            score -= result.OrphanedRoleCount * 5;
            score -= result.RolesWithExcessivePermissions.Count * 3;
            score -= result.UsersWithExcessiveRoles.Count * 2;
            score -= result.SeparationOfDutyViolations.Count * 10;
            score -= result.StaleAssignments.Count * 1;
            score -= result.NamingConventionViolations.Count * 0.5;

            // Ensure score doesn't go below 0;
            return Math.Max(0, Math.Min(100, score));
        }

        private ComplianceLevel DetermineComplianceLevel(double score)
        {
            return score switch;
            {
                >= 90 => ComplianceLevel.Excellent,
                >= 75 => ComplianceLevel.Good,
                >= 60 => ComplianceLevel.Fair,
                >= 40 => ComplianceLevel.Poor,
                _ => ComplianceLevel.Critical;
            };
        }

        private async Task<List<string>> DetectHealthAnomaliesAsync(RoleStatistics stats)
        {
            var anomalies = new List<string>();

            // Check for unusual role creation rate;
            var historicalAvgCreation = await _repository.GetHistoricalRoleCreationRateAsync(TimeSpan.FromDays(7));
            if (stats.RolesCreated > historicalAvgCreation * 3)
            {
                anomalies.Add($"Unusually high role creation rate: {stats.RolesCreated} in last hour");
            }

            // Check for unusual assignment rate;
            var historicalAvgAssignment = await _repository.GetHistoricalAssignmentRateAsync(TimeSpan.FromDays(7));
            if (stats.AssignmentsCreated > historicalAvgAssignment * 3)
            {
                anomalies.Add($"Unusually high assignment rate: {stats.AssignmentsCreated} in last hour");
            }

            // Check for high revocation rate;
            if (stats.AssignmentsRevoked > stats.AssignmentsCreated * 0.5) // More than 50% revocation rate;
            {
                anomalies.Add($"High role revocation rate: {stats.AssignmentsRevoked} revocations in last hour");
            }

            return anomalies;
        }

        private void ClearRoleCache(Guid roleId)
        {
            // Clear specific role cache;
            var cacheKey = $"{ROLE_CACHE_PREFIX}{roleId}";
            _memoryCache.Remove(cacheKey);

            // Clear all variations;
            _memoryCache.Remove($"{cacheKey}_perms_true_assign_false");
            _memoryCache.Remove($"{cacheKey}_perms_true_assign_true");
            _memoryCache.Remove($"{cacheKey}_perms_false_assign_true");
        }

        private void ClearUserRoleCache(string userId)
        {
            var cacheKey = $"{USER_ROLES_CACHE_PREFIX}{userId}";
            _memoryCache.Remove(cacheKey);
            _memoryCache.Remove($"{cacheKey}_inherited_true");
            _memoryCache.Remove($"{cacheKey}_inherited_false");
        }

        private void ClearUserRoleCachesForRole(Guid roleId)
        {
            // This would typically clear caches for all users with this role;
            // Implementation depends on how user-role relationships are stored;
            // For simplicity, we'll clear the entire user roles cache;
            // In production, you might want a more targeted approach;

            // Clear all permission check caches for this role's permissions;
            // This is a simplified approach;
        }

        private void ClearRoleCaches()
        {
            // Clear all role-related caches;
            // In production, you might use cache tags or more sophisticated invalidation;
            var keysToRemove = new List<string>();

            // This is a simplified approach - in reality, you'd track cache keys;
            // or use a distributed cache with tag support;
        }

        private void ClearAllCaches()
        {
            // Clear all caches (use with caution)
            if (_memoryCache is MemoryCache memoryCache)
            {
                memoryCache.Compact(1.0); // Remove all entries;
            }
        }

        private string BuildRolesCacheKey(RoleQueryOptions options)
        {
            using var sha256 = SHA256.Create();
            var input = JsonConvert.SerializeObject(options);
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return $"roles_query_{Convert.ToBase64String(hash)}";
        }

        private List<Role> SortRoles(IEnumerable<Role> roles, string sortBy, string sortDirection)
        {
            var query = roles.AsQueryable();

            sortBy ??= "Name";
            sortDirection ??= "asc";

            return (sortBy.ToLower(), sortDirection.ToLower()) switch;
            {
                ("name", "asc") => query.OrderBy(r => r.Name).ToList(),
                ("name", "desc") => query.OrderByDescending(r => r.Name).ToList(),
                ("createdat", "asc") => query.OrderBy(r => r.CreatedAt).ToList(),
                ("createdat", "desc") => query.OrderByDescending(r => r.CreatedAt).ToList(),
                ("updatedat", "asc") => query.OrderBy(r => r.UpdatedAt).ToList(),
                ("updatedat", "desc") => query.OrderByDescending(r => r.UpdatedAt).ToList(),
                _ => query.OrderBy(r => r.Name).ToList()
            };
        }

        private string ComputeContextHash(ResourceContext context)
        {
            if (context == null)
                return "null";

            using var sha256 = SHA256.Create();
            var input = JsonConvert.SerializeObject(context);
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(hash).Substring(0, 8);
        }

        private bool IsValidRoleCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            // Role code should be uppercase with underscores;
            return System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Z0-9_]+$");
        }

        private bool IsValidRoleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            // Role name should start with capital letter and contain only letters, numbers, and spaces;
            return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z][A-Za-z0-9 ]*$");
        }

        private string SuggestRoleName(string currentName)
        {
            if (string.IsNullOrWhiteSpace(currentName))
                return "New Role";

            // Capitalize first letter and remove special characters;
            var cleaned = System.Text.RegularExpressions.Regex.Replace(currentName, @"[^A-Za-z0-9 ]", " ");
            return char.ToUpper(cleaned[0]) + cleaned.Substring(1).ToLower();
        }

        private string SuggestRoleCode(string currentCode)
        {
            if (string.IsNullOrWhiteSpace(currentCode))
                return "NEW_ROLE";

            // Convert to uppercase and replace non-alphanumeric with underscore;
            var cleaned = System.Text.RegularExpressions.Regex.Replace(currentCode.ToUpperInvariant(), @"[^A-Z0-9]", "_");
            // Remove consecutive underscores;
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"_+", "_");
            // Remove leading/trailing underscores;
            cleaned = cleaned.Trim('_');

            return string.IsNullOrEmpty(cleaned) ? "NEW_ROLE" : cleaned;
        }

        private async void ExecuteScheduledCleanupAsync(object state)
        {
            try
            {
                _logger.LogDebug("Running scheduled role cleanup");

                var result = await CleanupRolesAsync(new RoleCleanupOptions;
                {
                    CleanupOrphanedRoles = true,
                    OrphanedForAtLeast = TimeSpan.FromDays(90),
                    CleanupStaleAssignments = true,
                    StaleForAtLeast = TimeSpan.FromDays(180),
                    CleanupArchivedRoles = true,
                    ArchivedForAtLeast = TimeSpan.FromDays(365),
                    CleanupExpiredAssignments = true;
                });

                _logger.LogInformation("Scheduled role cleanup completed: {Result}",
                    JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled role cleanup failed");
            }
        }

        #endregion;

        #region IDisposable Implementation;

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
                    _cleanupTimer?.Dispose();
                    _roleOperationLock?.Dispose();
                    _evaluationEngine?.Dispose();
                    _hierarchyManager?.Dispose();

                    _logger.LogInformation("RoleManager disposed");
                }

                _disposed = true;
            }
        }

        ~RoleManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Enums;

    public enum AssigneeType;
    {
        User = 0,
        Group = 1,
        Service = 2,
        Device = 3;
    }

    public enum RevokeReason;
    {
        Manual = 0,
        Expired = 1,
        Security = 2,
        Compliance = 3,
        Replaced = 4,
        System = 5;
    }

    public enum ImportMode;
    {
        CreateOnly = 0,
        UpdateOnly = 1,
        CreateOrUpdate = 2,
        ReplaceAll = 3;
    }

    public enum ExportFormat;
    {
        JSON = 0,
        XML = 1,
        CSV = 2,
        YAML = 3;
    }

    public enum ComplianceLevel;
    {
        Critical = 0,
        Poor = 1,
        Fair = 2,
        Good = 3,
        Excellent = 4;
    }

    public enum ServiceStatus;
    {
        Healthy = 0,
        Degraded = 1,
        Unhealthy = 2,
        Critical = 3;
    }

    // Additional supporting classes would be defined here...
    // (Role, RoleCreateRequest, RoleUpdateRequest, RoleAssignment, etc.)

    #endregion;
}
