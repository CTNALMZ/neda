using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.SecurityModules.AdvancedSecurity.Authorization.AccessDecision;

namespace NEDA.SecurityModules.AdvancedSecurity.Authorization;
{
    /// <summary>
    /// Authorization decision types;
    /// </summary>
    public enum AccessDecisionType;
    {
        Allow = 0,
        Deny = 1,
        Conditional = 2,
        Defer = 3;
    }

    /// <summary>
    /// Access control levels;
    /// </summary>
    public enum AccessLevel;
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 3,
        Delete = 4,
        Admin = 5,
        Owner = 6;
    }

    /// <summary>
    /// Resource types for authorization;
    /// </summary>
    public enum ResourceType;
    {
        FileSystem = 0,
        Database = 1,
        APIEndpoint = 2,
        SystemCommand = 3,
        Configuration = 4,
        UserData = 5,
        Financial = 6,
        Medical = 7,
        IntellectualProperty = 8;
    }

    /// <summary>
    /// Authorization context containing all information needed for access decision;
    /// </summary>
    public class AuthorizationContext;
    {
        public string RequestId { get; set; }
        public ClaimsPrincipal Principal { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public List<string> UserRoles { get; set; }
        public string Resource { get; set; }
        public ResourceType ResourceType { get; set; }
        public string Action { get; set; }
        public AccessLevel RequiredLevel { get; set; }
        public Dictionary<string, object> Environment { get; set; }
        public Dictionary<string, object> ResourceMetadata { get; set; }
        public DateTime RequestTime { get; set; }
        public string ClientIP { get; set; }
        public string UserAgent { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> CustomData { get; set; }

        public AuthorizationContext()
        {
            RequestId = Guid.NewGuid().ToString("N");
            UserRoles = new List<string>();
            Environment = new Dictionary<string, object>();
            ResourceMetadata = new Dictionary<string, object>();
            CustomData = new Dictionary<string, object>();
            RequestTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Result of access decision evaluation;
    /// </summary>
    public class AccessDecisionResult;
    {
        public AccessDecisionType Decision { get; set; }
        public bool IsAllowed => Decision == AccessDecisionType.Allow;
        public string Reason { get; set; }
        public List<string> AppliedPolicies { get; set; }
        public List<string> EvaluatedRules { get; set; }
        public Dictionary<string, object> Details { get; set; }
        public TimeSpan EvaluationTime { get; set; }
        public string DecisionId { get; set; }
        public DateTime DecisionTime { get; set; }

        public AccessDecisionResult()
        {
            Decision = AccessDecisionType.Deny;
            AppliedPolicies = new List<string>();
            EvaluatedRules = new List<string>();
            Details = new Dictionary<string, object>();
            DecisionId = Guid.NewGuid().ToString("N");
            DecisionTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Authorization policy definition;
    /// </summary>
    public class AuthorizationPolicy;
    {
        public string PolicyId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ResourceType ResourceType { get; set; }
        public List<string> AllowedActions { get; set; }
        public AccessLevel MinimumLevel { get; set; }
        public List<string> RequiredRoles { get; set; }
        public List<string> RequiredClaims { get; set; }
        public Dictionary<string, object> Conditions { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }

        public AuthorizationPolicy()
        {
            PolicyId = Guid.NewGuid().ToString("N");
            AllowedActions = new List<string>();
            RequiredRoles = new List<string>();
            RequiredClaims = new List<string>();
            Conditions = new Dictionary<string, object>();
            Attributes = new Dictionary<string, object>();
            Priority = 100;
            IsEnabled = true;
            CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Authorization rule for fine-grained access control;
    /// </summary>
    public class AuthorizationRule;
    {
        public string RuleId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Effect { get; set; } // "Allow" or "Deny"
        public List<string> Principals { get; set; }
        public List<string> Actions { get; set; }
        public List<string> Resources { get; set; }
        public Dictionary<string, object> Conditions { get; set; }
        public int Priority { get; set; }
        public bool IsEnabled { get; set; }

        public AuthorizationRule()
        {
            RuleId = Guid.NewGuid().ToString("N");
            Principals = new List<string>();
            Actions = new List<string>();
            Resources = new List<string>();
            Conditions = new Dictionary<string, object>();
            Priority = 100;
            IsEnabled = true;
        }
    }

    /// <summary>
    /// Professional access decision engine with policy-based authorization,
    /// rule evaluation, real-time monitoring, and risk-based access control;
    /// </summary>
    public class AccessDecision : IAccessDecision, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IPermissionService _permissionService;
        private readonly IRoleManager _roleManager;
        private readonly IEventBus _eventBus;
        private readonly IAuditLogger _auditLogger;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly ISecurityMonitor _securityMonitor;

        private readonly Dictionary<string, AuthorizationPolicy> _policies = new Dictionary<string, AuthorizationPolicy>();
        private readonly Dictionary<string, AuthorizationRule> _rules = new Dictionary<string, AuthorizationRule>();
        private readonly SemaphoreSlim _policyLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _ruleLock = new SemaphoreSlim(1, 1);
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        private readonly MemoryCache<DecisionCacheKey, AccessDecisionResult> _decisionCache;

        private bool _disposed = false;
        private long _totalDecisions = 0;
        private long _allowedDecisions = 0;
        private long _deniedDecisions = 0;
        private DateTime _startTime = DateTime.UtcNow;

        // Performance counters;
        private readonly PerformanceCounter _decisionsPerSecondCounter;
        private readonly PerformanceCounter _averageDecisionTimeCounter;
        private readonly PerformanceCounter _denialRateCounter;

        /// <summary>
        /// Event raised when access decision is made;
        /// </summary>
        public event EventHandler<AccessDecisionMadeEventArgs> AccessDecisionMade;

        /// <summary>
        /// Event raised when suspicious access pattern is detected;
        /// </summary>
        public event EventHandler<SuspiciousAccessEventArgs> SuspiciousAccessDetected;

        /// <summary>
        /// Initializes a new instance of AccessDecision;
        /// </summary>
        public AccessDecision(
            ILogger logger,
            IPermissionService permissionService,
            IRoleManager roleManager,
            IEventBus eventBus,
            IAuditLogger auditLogger,
            IDiagnosticTool diagnosticTool,
            ISecurityMonitor securityMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _permissionService = permissionService ?? throw new ArgumentNullException(nameof(permissionService));
            _roleManager = roleManager ?? throw new ArgumentNullException(nameof(roleManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));

            // Initialize decision cache with 5-minute sliding expiration;
            _decisionCache = new MemoryCache<DecisionCacheKey, AccessDecisionResult>(
                TimeSpan.FromMinutes(5),
                maxItems: 10000);

            // Initialize performance counters;
            _decisionsPerSecondCounter = new PerformanceCounter("NEDA Access Decisions", "Decisions Per Second");
            _averageDecisionTimeCounter = new PerformanceCounter("NEDA Access Decisions", "Average Decision Time");
            _denialRateCounter = new PerformanceCounter("NEDA Access Decisions", "Denial Rate");

            // Load default policies;
            LoadDefaultPolicies();

            _logger.LogInformation("AccessDecision engine initialized", new;
            {
                CacheSize = _decisionCache.Count,
                DefaultPolicies = _policies.Count;
            });
        }

        /// <summary>
        /// Evaluates access request and returns decision;
        /// </summary>
        public async Task<AccessDecisionResult> EvaluateAsync(AuthorizationContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (context.Principal == null)
                throw new ArgumentException("Principal is required", nameof(context.Principal));

            if (string.IsNullOrWhiteSpace(context.Resource))
                throw new ArgumentException("Resource is required", nameof(context.Resource));

            if (string.IsNullOrWhiteSpace(context.Action))
                throw new ArgumentException("Action is required", nameof(context.Action));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Interlocked.Increment(ref _totalDecisions);

                // Check cache first;
                var cacheKey = new DecisionCacheKey(context);
                AccessDecisionResult cachedResult = null;

                _cacheLock.EnterReadLock();
                try
                {
                    cachedResult = _decisionCache.Get(cacheKey);
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }

                if (cachedResult != null)
                {
                    cachedResult.Details["Cached"] = true;
                    cachedResult.EvaluationTime = stopwatch.Elapsed;

                    UpdatePerformanceCounters(cachedResult, stopwatch.Elapsed);
                    return cachedResult;
                }

                // Extract user information;
                ExtractUserInfo(context);

                // Perform risk assessment;
                var riskScore = await AssessRiskAsync(context);
                context.CustomData["RiskScore"] = riskScore;

                // Evaluate policies;
                var policyResult = await EvaluatePoliciesAsync(context);

                // Evaluate rules;
                var ruleResult = await EvaluateRulesAsync(context);

                // Make final decision;
                var decision = await MakeFinalDecisionAsync(context, policyResult, ruleResult, riskScore);

                // Apply risk-based adjustments;
                decision = await ApplyRiskBasedAdjustmentsAsync(decision, context, riskScore);

                // Cache the decision;
                _cacheLock.EnterWriteLock();
                try
                {
                    _decisionCache.Set(cacheKey, decision);
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }

                decision.EvaluationTime = stopwatch.Elapsed;

                // Update statistics;
                if (decision.IsAllowed)
                    Interlocked.Increment(ref _allowedDecisions);
                else;
                    Interlocked.Increment(ref _deniedDecisions);

                // Update performance counters;
                UpdatePerformanceCounters(decision, stopwatch.Elapsed);

                // Raise decision made event;
                OnAccessDecisionMade(new AccessDecisionMadeEventArgs(context, decision));

                // Log audit event;
                await LogAuthorizationDecisionAsync(context, decision);

                // Check for suspicious patterns;
                await CheckForSuspiciousPatternsAsync(context, decision);

                // Publish decision event;
                await _eventBus.PublishAsync(new AccessDecisionEvent;
                {
                    DecisionId = decision.DecisionId,
                    Context = context,
                    Result = decision,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Access decision made: {decision.Decision} for {context.Action} on {context.Resource}",
                    new Dictionary<string, object>
                    {
                        ["UserId"] = context.UserId,
                        ["Resource"] = context.Resource,
                        ["Action"] = context.Action,
                        ["Decision"] = decision.Decision.ToString(),
                        ["EvaluationTimeMs"] = decision.EvaluationTime.TotalMilliseconds,
                        ["RiskScore"] = riskScore;
                    });

                return decision;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Access decision evaluation failed", ex);

                // Default to deny on error (fail-safe)
                return new AccessDecisionResult;
                {
                    Decision = AccessDecisionType.Deny,
                    Reason = $"Evaluation failed: {ex.Message}",
                    EvaluationTime = stopwatch.Elapsed;
                };
            }
        }

        /// <summary>
        /// Checks if user is authorized for specific resource and action;
        /// </summary>
        public async Task<bool> IsAuthorizedAsync(
            ClaimsPrincipal user,
            string resource,
            string action,
            ResourceType resourceType = ResourceType.APIEndpoint,
            AccessLevel requiredLevel = AccessLevel.Read,
            Dictionary<string, object> environment = null)
        {
            var context = new AuthorizationContext;
            {
                Principal = user,
                Resource = resource,
                Action = action,
                ResourceType = resourceType,
                RequiredLevel = requiredLevel,
                Environment = environment ?? new Dictionary<string, object>(),
                RequestTime = DateTime.UtcNow;
            };

            var result = await EvaluateAsync(context);
            return result.IsAllowed;
        }

        /// <summary>
        /// Adds or updates authorization policy;
        /// </summary>
        public async Task<bool> SetPolicyAsync(AuthorizationPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            await _policyLock.WaitAsync();

            try
            {
                _policies[policy.PolicyId] = policy;

                // Clear relevant cache entries;
                ClearPolicyCache(policy);

                await _auditLogger.LogActivityAsync(
                    "PolicyUpdated",
                    $"Authorization policy updated: {policy.Name}",
                    new Dictionary<string, object>
                    {
                        ["PolicyId"] = policy.PolicyId,
                        ["PolicyName"] = policy.Name,
                        ["ResourceType"] = policy.ResourceType.ToString(),
                        ["UpdatedDate"] = DateTime.UtcNow;
                    });

                return true;
            }
            finally
            {
                _policyLock.Release();
            }
        }

        /// <summary>
        /// Removes authorization policy;
        /// </summary>
        public async Task<bool> RemovePolicyAsync(string policyId)
        {
            if (string.IsNullOrWhiteSpace(policyId))
                throw new ArgumentException("Policy ID is required", nameof(policyId));

            await _policyLock.WaitAsync();

            try
            {
                if (_policies.TryGetValue(policyId, out var policy))
                {
                    _policies.Remove(policyId);

                    // Clear relevant cache entries;
                    ClearPolicyCache(policy);

                    await _auditLogger.LogActivityAsync(
                        "PolicyRemoved",
                        $"Authorization policy removed: {policy.Name}",
                        new Dictionary<string, object>
                        {
                            ["PolicyId"] = policyId,
                            ["PolicyName"] = policy.Name,
                            ["RemovedDate"] = DateTime.UtcNow;
                        });

                    return true;
                }

                return false;
            }
            finally
            {
                _policyLock.Release();
            }
        }

        /// <summary>
        /// Gets authorization policy by ID;
        /// </summary>
        public async Task<AuthorizationPolicy> GetPolicyAsync(string policyId)
        {
            if (string.IsNullOrWhiteSpace(policyId))
                throw new ArgumentException("Policy ID is required", nameof(policyId));

            await _policyLock.WaitAsync();

            try
            {
                return _policies.TryGetValue(policyId, out var policy) ? policy : null;
            }
            finally
            {
                _policyLock.Release();
            }
        }

        /// <summary>
        /// Gets all policies for specific resource type;
        /// </summary>
        public async Task<List<AuthorizationPolicy>> GetPoliciesByResourceTypeAsync(ResourceType resourceType)
        {
            await _policyLock.WaitAsync();

            try
            {
                return _policies.Values;
                    .Where(p => p.ResourceType == resourceType && p.IsEnabled)
                    .OrderBy(p => p.Priority)
                    .ToList();
            }
            finally
            {
                _policyLock.Release();
            }
        }

        /// <summary>
        /// Adds or updates authorization rule;
        /// </summary>
        public async Task<bool> SetRuleAsync(AuthorizationRule rule)
        {
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            await _ruleLock.WaitAsync();

            try
            {
                _rules[rule.RuleId] = rule;

                // Clear relevant cache entries;
                ClearRuleCache(rule);

                await _auditLogger.LogActivityAsync(
                    "RuleUpdated",
                    $"Authorization rule updated: {rule.Name}",
                    new Dictionary<string, object>
                    {
                        ["RuleId"] = rule.RuleId,
                        ["RuleName"] = rule.Name,
                        ["Effect"] = rule.Effect,
                        ["UpdatedDate"] = DateTime.UtcNow;
                    });

                return true;
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Removes authorization rule;
        /// </summary>
        public async Task<bool> RemoveRuleAsync(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
                throw new ArgumentException("Rule ID is required", nameof(ruleId));

            await _ruleLock.WaitAsync();

            try
            {
                if (_rules.TryGetValue(ruleId, out var rule))
                {
                    _rules.Remove(ruleId);

                    // Clear relevant cache entries;
                    ClearRuleCache(rule);

                    await _auditLogger.LogActivityAsync(
                        "RuleRemoved",
                        $"Authorization rule removed: {rule.Name}",
                        new Dictionary<string, object>
                        {
                            ["RuleId"] = ruleId,
                            ["RuleName"] = rule.Name,
                            ["RemovedDate"] = DateTime.UtcNow;
                        });

                    return true;
                }

                return false;
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Evaluates all policies against context and returns combined result;
        /// </summary>
        public async Task<AccessDecisionResult> EvaluatePoliciesAsync(AuthorizationContext context)
        {
            var policies = await GetApplicablePoliciesAsync(context);
            var result = new AccessDecisionResult();

            foreach (var policy in policies)
            {
                result.EvaluatedRules.Add($"Policy:{policy.Name}");

                var policyResult = await EvaluatePolicyAsync(policy, context);
                if (policyResult != null)
                {
                    result.AppliedPolicies.Add(policy.Name);
                    result.Details[$"Policy_{policy.PolicyId}"] = policyResult;

                    // If any policy denies, return deny;
                    if (policyResult == AccessDecisionType.Deny)
                    {
                        result.Decision = AccessDecisionType.Deny;
                        result.Reason = $"Policy '{policy.Name}' denied access";
                        return result;
                    }

                    // If policy allows and we haven't decided yet, set to allow;
                    if (policyResult == AccessDecisionType.Allow && result.Decision != AccessDecisionType.Allow)
                    {
                        result.Decision = AccessDecisionType.Allow;
                        result.Reason = $"Policy '{policy.Name}' allowed access";
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Evaluates all rules against context and returns combined result;
        /// </summary>
        public async Task<AccessDecisionResult> EvaluateRulesAsync(AuthorizationContext context)
        {
            var rules = await GetApplicableRulesAsync(context);
            var result = new AccessDecisionResult();

            // Sort rules by priority (lower number = higher priority)
            var sortedRules = rules.OrderBy(r => r.Priority).ToList();

            foreach (var rule in sortedRules)
            {
                result.EvaluatedRules.Add($"Rule:{rule.Name}");

                var ruleResult = await EvaluateRuleAsync(rule, context);
                if (ruleResult != null)
                {
                    result.AppliedPolicies.Add(rule.Name);
                    result.Details[$"Rule_{rule.RuleId}"] = ruleResult;

                    // Rule evaluation is conclusive - first matching rule decides;
                    if (ruleResult == AccessDecisionType.Allow || ruleResult == AccessDecisionType.Deny)
                    {
                        result.Decision = ruleResult;
                        result.Reason = $"Rule '{rule.Name}' resulted in {ruleResult}";
                        return result;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clears decision cache;
        /// </summary>
        public void ClearCache()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _decisionCache.Clear();
                _logger.LogInformation("Access decision cache cleared");
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets authorization statistics;
        /// </summary>
        public async Task<AuthorizationStatistics> GetStatisticsAsync(TimeSpan? period = null)
        {
            var endDate = DateTime.UtcNow;
            var startDate = period.HasValue ? endDate.Subtract(period.Value) : _startTime;

            // In production, this would query a database;
            // For now, return overall statistics;

            return new AuthorizationStatistics;
            {
                TotalDecisions = _totalDecisions,
                AllowedDecisions = _allowedDecisions,
                DeniedDecisions = _deniedDecisions,
                AverageDecisionTimeMs = GetAverageDecisionTime(),
                CacheHitRate = GetCacheHitRate(),
                StartDate = startDate,
                EndDate = endDate,
                Uptime = endDate - _startTime;
            };
        }

        /// <summary>
        /// Validates policy configuration;
        /// </summary>
        public async Task<List<string>> ValidatePoliciesAsync()
        {
            var errors = new List<string>();

            await _policyLock.WaitAsync();

            try
            {
                foreach (var policy in _policies.Values)
                {
                    // Check for expired policies;
                    if (policy.ExpiresAt.HasValue && policy.ExpiresAt.Value < DateTime.UtcNow)
                    {
                        errors.Add($"Policy '{policy.Name}' (ID: {policy.PolicyId}) has expired");
                    }

                    // Check for conflicts;
                    var conflictingPolicies = _policies.Values;
                        .Where(p => p.PolicyId != policy.PolicyId &&
                                    p.ResourceType == policy.ResourceType &&
                                    p.AllowedActions.Intersect(policy.AllowedActions).Any())
                        .ToList();

                    if (conflictingPolicies.Any())
                    {
                        errors.Add($"Policy '{policy.Name}' conflicts with: {string.Join(", ", conflictingPolicies.Select(p => p.Name))}");
                    }
                }
            }
            finally
            {
                _policyLock.Release();
            }

            return await Task.FromResult(errors);
        }

        /// <summary>
        /// Performs risk assessment for authorization context;
        /// </summary>
        public async Task<double> AssessRiskAsync(AuthorizationContext context)
        {
            var riskScore = 0.0;

            // Base risk factors;
            var factors = new Dictionary<string, double>
            {
                // Resource sensitivity;
                ["ResourceSensitivity"] = GetResourceSensitivity(context.ResourceType),

                // Action severity;
                ["ActionSeverity"] = GetActionSeverity(context.Action, context.ResourceType),

                // User trust level (based on role, tenure, etc.)
                ["UserTrustLevel"] = await GetUserTrustLevelAsync(context.UserId),

                // Time-based risk (off-hours access)
                ["TimeRisk"] = GetTimeRisk(context.RequestTime),

                // Location-based risk (unusual IP/location)
                ["LocationRisk"] = GetLocationRisk(context.ClientIP),

                // Device/browser risk;
                ["DeviceRisk"] = GetDeviceRisk(context.UserAgent),

                // Previous behavior;
                ["BehaviorRisk"] = await GetBehaviorRiskAsync(context.UserId, context.Resource, context.Action)
            };

            // Calculate weighted risk score;
            var weights = new Dictionary<string, double>
            {
                ["ResourceSensitivity"] = 0.25,
                ["ActionSeverity"] = 0.25,
                ["UserTrustLevel"] = 0.15,
                ["TimeRisk"] = 0.10,
                ["LocationRisk"] = 0.10,
                ["DeviceRisk"] = 0.10,
                ["BehaviorRisk"] = 0.05;
            };

            foreach (var factor in factors)
            {
                riskScore += factor.Value * weights[factor.Key];
            }

            // Normalize to 0-100 scale;
            riskScore = Math.Min(100, Math.Max(0, riskScore * 100));

            context.CustomData["RiskFactors"] = factors;
            context.CustomData["RiskWeights"] = weights;
            context.CustomData["CalculatedRiskScore"] = riskScore;

            return await Task.FromResult(riskScore);
        }

        /// <summary>
        /// Checks access patterns for anomalies;
        /// </summary>
        public async Task<bool> CheckForAnomaliesAsync(string userId, string resource, string action)
        {
            // Implement anomaly detection logic;
            // This could include:
            // - Frequency analysis;
            // - Time pattern analysis;
            // - Resource access pattern analysis;
            // - Peer group comparison;

            var accessPattern = new;
            {
                UserId = userId,
                Resource = resource,
                Action = action,
                Timestamp = DateTime.UtcNow;
            };

            // For now, return false (no anomalies detected)
            // In production, this would integrate with ML anomaly detection;

            return await Task.FromResult(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _policyLock?.Dispose();
                    _ruleLock?.Dispose();
                    _cacheLock?.Dispose();
                    _decisionCache?.Dispose();

                    // Dispose performance counters;
                    _decisionsPerSecondCounter?.Dispose();
                    _averageDecisionTimeCounter?.Dispose();
                    _denialRateCounter?.Dispose();
                }
                _disposed = true;
            }
        }

        private void LoadDefaultPolicies()
        {
            // Default deny-all policy (lowest priority)
            var denyAllPolicy = new AuthorizationPolicy;
            {
                Name = "DefaultDenyAll",
                Description = "Default policy that denies all access unless explicitly allowed",
                ResourceType = ResourceType.APIEndpoint,
                MinimumLevel = AccessLevel.None,
                Priority = 1000, // Lowest priority;
                IsEnabled = true;
            };

            _policies[denyAllPolicy.PolicyId] = denyAllPolicy;

            // Admin full access policy;
            var adminPolicy = new AuthorizationPolicy;
            {
                Name = "AdminFullAccess",
                Description = "Administrators have full access to all resources",
                ResourceType = ResourceType.APIEndpoint,
                RequiredRoles = new List<string> { "Administrator", "SuperAdmin" },
                MinimumLevel = AccessLevel.Admin,
                Priority = 10, // High priority;
                IsEnabled = true;
            };

            _policies[adminPolicy.PolicyId] = adminPolicy;

            // User read-only policy;
            var userReadPolicy = new AuthorizationPolicy;
            {
                Name = "UserReadOnly",
                Description = "Regular users have read-only access to most resources",
                ResourceType = ResourceType.APIEndpoint,
                RequiredRoles = new List<string> { "User" },
                MinimumLevel = AccessLevel.Read,
                Priority = 100, // Medium priority;
                IsEnabled = true;
            };

            _policies[userReadPolicy.PolicyId] = userReadPolicy;
        }

        private void ExtractUserInfo(AuthorizationContext context)
        {
            if (context.Principal?.Identity != null)
            {
                context.UserId = context.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                context.Username = context.Principal.Identity.Name;

                // Extract roles;
                var roleClaims = context.Principal.FindAll(ClaimTypes.Role);
                context.UserRoles = roleClaims.Select(c => c.Value).ToList();

                // Extract session ID if present;
                var sessionClaim = context.Principal.FindFirst("SessionId");
                if (sessionClaim != null)
                {
                    context.SessionId = sessionClaim.Value;
                }
            }
        }

        private async Task<List<AuthorizationPolicy>> GetApplicablePoliciesAsync(AuthorizationContext context)
        {
            await _policyLock.WaitAsync();

            try
            {
                return _policies.Values;
                    .Where(p => p.IsEnabled &&
                               (p.ResourceType == context.ResourceType || p.ResourceType == ResourceType.APIEndpoint) &&
                               (!p.ExpiresAt.HasValue || p.ExpiresAt.Value > DateTime.UtcNow))
                    .OrderBy(p => p.Priority)
                    .ToList();
            }
            finally
            {
                _policyLock.Release();
            }
        }

        private async Task<List<AuthorizationRule>> GetApplicableRulesAsync(AuthorizationContext context)
        {
            await _ruleLock.WaitAsync();

            try
            {
                return _rules.Values;
                    .Where(r => r.IsEnabled &&
                               MatchesRule(r, context))
                    .OrderBy(r => r.Priority)
                    .ToList();
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        private bool MatchesRule(AuthorizationRule rule, AuthorizationContext context)
        {
            // Check principals;
            if (rule.Principals.Any() && !rule.Principals.Contains(context.UserId) &&
                !rule.Principals.Any(p => context.UserRoles.Contains(p)))
                return false;

            // Check actions;
            if (rule.Actions.Any() && !rule.Actions.Contains(context.Action))
                return false;

            // Check resources (supports wildcards)
            if (rule.Resources.Any())
            {
                var matches = rule.Resources.Any(r =>
                    r == context.Resource ||
                    r.EndsWith("*") && context.Resource.StartsWith(r.TrimEnd('*')) ||
                    r.StartsWith("*") && context.Resource.EndsWith(r.TrimStart('*')));

                if (!matches)
                    return false;
            }

            // Check conditions;
            foreach (var condition in rule.Conditions)
            {
                if (!EvaluateCondition(condition.Key, condition.Value, context))
                    return false;
            }

            return true;
        }

        private bool EvaluateCondition(string key, object value, AuthorizationContext context)
        {
            // Simple condition evaluation;
            // In production, this would use a more sophisticated expression evaluator;

            switch (key.ToLowerInvariant())
            {
                case "time":
                    // Check if current time is within allowed range;
                    if (value is string timeRange)
                    {
                        var parts = timeRange.Split('-');
                        if (parts.Length == 2)
                        {
                            var start = TimeSpan.Parse(parts[0]);
                            var end = TimeSpan.Parse(parts[1]);
                            var current = context.RequestTime.TimeOfDay;

                            return current >= start && current <= end;
                        }
                    }
                    break;

                case "dayofweek":
                    // Check day of week;
                    if (value is string days)
                    {
                        var allowedDays = days.Split(',');
                        var currentDay = context.RequestTime.DayOfWeek.ToString();
                        return allowedDays.Contains(currentDay, StringComparer.OrdinalIgnoreCase);
                    }
                    break;

                case "iprange":
                    // Check IP range;
                    if (value is string ipRange && !string.IsNullOrEmpty(context.ClientIP))
                    {
                        return CheckIPRange(context.ClientIP, ipRange);
                    }
                    break;

                case "hasclaim":
                    // Check if user has specific claim;
                    if (value is string claimType)
                    {
                        return context.Principal.HasClaim(c => c.Type == claimType);
                    }
                    break;
            }

            return true; // Default to true if condition type not recognized;
        }

        private bool CheckIPRange(string ip, string ipRange)
        {
            // Simplified IP range check;
            // In production, use proper IP address parsing and range checking;

            if (ipRange.Contains("-"))
            {
                var parts = ipRange.Split('-');
                return ip == parts[0] || ip == parts[1];
            }
            else if (ipRange.EndsWith("*"))
            {
                var prefix = ipRange.TrimEnd('*');
                return ip.StartsWith(prefix);
            }

            return ip == ipRange;
        }

        private async Task<AccessDecisionType> EvaluatePolicyAsync(AuthorizationPolicy policy, AuthorizationContext context)
        {
            // Check if policy applies to this resource type;
            if (policy.ResourceType != context.ResourceType && policy.ResourceType != ResourceType.APIEndpoint)
                return AccessDecisionType.Defer;

            // Check required roles;
            if (policy.RequiredRoles.Any() && !policy.RequiredRoles.Any(r => context.UserRoles.Contains(r)))
                return AccessDecisionType.Defer;

            // Check required claims;
            if (policy.RequiredClaims.Any())
            {
                foreach (var claim in policy.RequiredClaims)
                {
                    if (!context.Principal.HasClaim(c => c.Type == claim))
                        return AccessDecisionType.Defer;
                }
            }

            // Check allowed actions;
            if (policy.AllowedActions.Any() && !policy.AllowedActions.Contains(context.Action))
                return AccessDecisionType.Defer;

            // Check minimum access level;
            if (context.RequiredLevel > policy.MinimumLevel)
                return AccessDecisionType.Deny;

            // Check conditions;
            foreach (var condition in policy.Conditions)
            {
                if (!EvaluateCondition(condition.Key, condition.Value, context))
                    return AccessDecisionType.Deny;
            }

            // Policy allows access;
            return AccessDecisionType.Allow;
        }

        private async Task<AccessDecisionType> EvaluateRuleAsync(AuthorizationRule rule, AuthorizationContext context)
        {
            if (!MatchesRule(rule, context))
                return AccessDecisionType.Defer;

            return rule.Effect.Equals("Allow", StringComparison.OrdinalIgnoreCase)
                ? AccessDecisionType.Allow;
                : AccessDecisionType.Deny;
        }

        private async Task<AccessDecisionResult> MakeFinalDecisionAsync(
            AuthorizationContext context,
            AccessDecisionResult policyResult,
            AccessDecisionResult ruleResult,
            double riskScore)
        {
            var finalResult = new AccessDecisionResult();

            // Rule-based decisions take precedence;
            if (ruleResult.Decision == AccessDecisionType.Allow || ruleResult.Decision == AccessDecisionType.Deny)
            {
                finalResult.Decision = ruleResult.Decision;
                finalResult.Reason = ruleResult.Reason;
                finalResult.AppliedPolicies.AddRange(ruleResult.AppliedPolicies);
            }
            // Then policy-based decisions;
            else if (policyResult.Decision == AccessDecisionType.Allow || policyResult.Decision == AccessDecisionType.Deny)
            {
                finalResult.Decision = policyResult.Decision;
                finalResult.Reason = policyResult.Reason;
                finalResult.AppliedPolicies.AddRange(policyResult.AppliedPolicies);
            }
            // Default to deny;
            else;
            {
                finalResult.Decision = AccessDecisionType.Deny;
                finalResult.Reason = "No matching policies or rules found";
            }

            // Add evaluated rules from both results;
            finalResult.EvaluatedRules.AddRange(policyResult.EvaluatedRules);
            finalResult.EvaluatedRules.AddRange(ruleResult.EvaluatedRules);

            // Add details;
            finalResult.Details["PolicyResult"] = policyResult;
            finalResult.Details["RuleResult"] = ruleResult;
            finalResult.Details["RiskScore"] = riskScore;
            finalResult.Details["UserId"] = context.UserId;
            finalResult.Details["Resource"] = context.Resource;
            finalResult.Details["Action"] = context.Action;

            return finalResult;
        }

        private async Task<AccessDecisionResult> ApplyRiskBasedAdjustmentsAsync(
            AccessDecisionResult decision,
            AuthorizationContext context,
            double riskScore)
        {
            // Apply risk-based adjustments;
            if (riskScore > 75) // High risk;
            {
                if (decision.IsAllowed)
                {
                    // Require additional verification for high-risk allowed accesses;
                    decision.Decision = AccessDecisionType.Conditional;
                    decision.Reason += " (Conditional - high risk, requires additional verification)";
                    decision.Details["RiskAdjustment"] = "ElevatedToConditional";
                    decision.Details["RequiredVerification"] = "MultiFactor";
                }
                else;
                {
                    // Log high-risk denials for review;
                    decision.Details["RiskAdjustment"] = "HighRiskDenial";
                }
            }
            else if (riskScore > 50) // Medium risk;
            {
                if (decision.IsAllowed)
                {
                    // Add additional logging for medium-risk accesses;
                    decision.Details["RiskAdjustment"] = "EnhancedLogging";
                }
            }

            return decision;
        }

        private async Task LogAuthorizationDecisionAsync(AuthorizationContext context, AccessDecisionResult result)
        {
            await _auditLogger.LogAuthorizationAsync(
                context.UserId,
                context.Username,
                context.Resource,
                context.Action,
                result.IsAllowed ? NEDA.SecurityModules.AdvancedSecurity.AuditLogging.AuditStatus.Success :
                                  NEDA.SecurityModules.AdvancedSecurity.AuditLogging.AuditStatus.Failure,
                result.Reason,
                new Dictionary<string, object>
                {
                    ["DecisionId"] = result.DecisionId,
                    ["RequestId"] = context.RequestId,
                    ["ResourceType"] = context.ResourceType.ToString(),
                    ["RequiredLevel"] = context.RequiredLevel.ToString(),
                    ["RiskScore"] = context.CustomData.ContainsKey("RiskScore") ? context.CustomData["RiskScore"] : 0,
                    ["EvaluationTimeMs"] = result.EvaluationTime.TotalMilliseconds,
                    ["AppliedPolicies"] = result.AppliedPolicies,
                    ["ClientIP"] = context.ClientIP,
                    ["UserAgent"] = context.UserAgent;
                });
        }

        private async Task CheckForSuspiciousPatternsAsync(AuthorizationContext context, AccessDecisionResult result)
        {
            // Check for suspicious access patterns;
            var isSuspicious = false;
            var reasons = new List<string>();

            // Multiple denied attempts in short period;
            if (!result.IsAllowed)
            {
                // Check rate of denied requests for this user/resource;
                var denialRate = await GetDenialRateAsync(context.UserId, context.Resource);
                if (denialRate > 0.5) // More than 50% denials;
                {
                    isSuspicious = true;
                    reasons.Add($"High denial rate ({denialRate:P0}) for user {context.UserId} on resource {context.Resource}");
                }
            }

            // Unusual time access;
            var hour = context.RequestTime.Hour;
            if (hour < 6 || hour > 22) // Outside normal business hours;
            {
                isSuspicious = true;
                reasons.Add($"Access attempted outside normal hours ({context.RequestTime:HH:mm})");
            }

            // Access to sensitive resources;
            if (IsSensitiveResource(context.ResourceType, context.Resource))
            {
                isSuspicious = true;
                reasons.Add($"Access attempted to sensitive resource: {context.Resource}");
            }

            if (isSuspicious)
            {
                OnSuspiciousAccessDetected(new SuspiciousAccessEventArgs(
                    context.UserId,
                    context.Username,
                    context.Resource,
                    context.Action,
                    reasons,
                    context.ClientIP,
                    context.RequestTime));

                await _securityMonitor.RaiseAlertAsync(
                    "SuspiciousAccessPattern",
                    string.Join("; ", reasons),
                    NEDA.SecurityModules.Monitoring.SecuritySeverity.Medium);
            }
        }

        private async Task<double> GetDenialRateAsync(string userId, string resource)
        {
            // In production, this would query historical data;
            // For now, return a simulated value;
            var random = new Random();
            return await Task.FromResult(random.NextDouble() * 0.3); // 0-30%
        }

        private bool IsSensitiveResource(ResourceType resourceType, string resource)
        {
            var sensitivePatterns = new[]
            {
                "admin",
                "password",
                "secret",
                "key",
                "token",
                "config",
                "user",
                "financial",
                "medical"
            };

            var resourceLower = resource.ToLowerInvariant();
            return sensitivePatterns.Any(pattern => resourceLower.Contains(pattern));
        }

        private double GetResourceSensitivity(ResourceType resourceType)
        {
            return resourceType switch;
            {
                ResourceType.Financial => 0.9,
                ResourceType.Medical => 0.9,
                ResourceType.IntellectualProperty => 0.8,
                ResourceType.Configuration => 0.7,
                ResourceType.UserData => 0.6,
                ResourceType.Database => 0.5,
                ResourceType.FileSystem => 0.4,
                ResourceType.APIEndpoint => 0.3,
                ResourceType.SystemCommand => 0.8,
                _ => 0.5;
            };
        }

        private double GetActionSeverity(string action, ResourceType resourceType)
        {
            var actionLower = action.ToLowerInvariant();

            if (actionLower.Contains("delete") || actionLower.Contains("remove"))
                return 0.9;

            if (actionLower.Contains("write") || actionLower.Contains("update") || actionLower.Contains("modify"))
                return 0.7;

            if (actionLower.Contains("read") || actionLower.Contains("get"))
                return 0.3;

            if (actionLower.Contains("execute") || actionLower.Contains("run"))
                return 0.8;

            return 0.5;
        }

        private async Task<double> GetUserTrustLevelAsync(string userId)
        {
            // In production, this would consider:
            // - Account age;
            // - Previous successful authentications;
            // - Security training completion;
            // - Previous security incidents;

            // For now, return a base value;
            return await Task.FromResult(0.7);
        }

        private double GetTimeRisk(DateTime requestTime)
        {
            var hour = requestTime.Hour;

            // Normal business hours: 9 AM to 5 PM;
            if (hour >= 9 && hour <= 17)
                return 0.2; // Low risk;

            // After hours but reasonable: 6 PM to 10 PM;
            if (hour >= 18 && hour <= 22)
                return 0.5; // Medium risk;

            // Late night/early morning: 11 PM to 8 AM;
            return 0.8; // High risk;
        }

        private double GetLocationRisk(string clientIP)
        {
            // In production, this would:
            // - Check against known VPN IPs;
            // - Check geographic location;
            // - Compare with usual access locations;

            // For now, return a base value;
            return 0.3;
        }

        private double GetDeviceRisk(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent))
                return 0.7; // Unknown device;

            var ua = userAgent.ToLowerInvariant();

            // Check for suspicious user agents;
            if (ua.Contains("curl") || ua.Contains("wget") || ua.Contains("python"))
                return 0.8; // Script/automated access;

            if (ua.Contains("bot") || ua.Contains("crawler") || ua.Contains("spider"))
                return 0.9; // Known bots;

            // Check for modern browsers;
            if (ua.Contains("chrome") || ua.Contains("firefox") || ua.Contains("safari"))
                return 0.2; // Low risk;

            return 0.5; // Medium risk;
        }

        private async Task<double> GetBehaviorRiskAsync(string userId, string resource, string action)
        {
            // In production, this would analyze:
            // - Access frequency;
            // - Pattern deviation;
            // - Peer group comparison;

            // For now, return a base value;
            return await Task.FromResult(0.3);
        }

        private void UpdatePerformanceCounters(AccessDecisionResult decision, TimeSpan evaluationTime)
        {
            try
            {
                _decisionsPerSecondCounter.Increment();
                _averageDecisionTimeCounter.RawValue = (long)evaluationTime.TotalMilliseconds;

                if (!decision.IsAllowed)
                {
                    _denialRateCounter.Increment();
                }
            }
            catch
            {
                // Performance counter errors should not affect functionality;
            }
        }

        private void ClearPolicyCache(AuthorizationPolicy policy)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                // Clear cache entries that might be affected by this policy;
                var keysToRemove = _decisionCache.Keys;
                    .Where(k => k.ResourceType == policy.ResourceType)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _decisionCache.Remove(key);
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private void ClearRuleCache(AuthorizationRule rule)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                // Clear all cache entries for simplicity;
                // In production, could be more targeted;
                _decisionCache.Clear();
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private double GetAverageDecisionTime()
        {
            if (_totalDecisions == 0)
                return 0;

            // In production, this would track actual decision times;
            // For now, return a simulated value;
            return 15.5; // milliseconds;
        }

        private double GetCacheHitRate()
        {
            if (_totalDecisions == 0)
                return 0;

            // In production, this would track cache hits/misses;
            // For now, return a simulated value;
            return 0.65; // 65% cache hit rate;
        }

        private void OnAccessDecisionMade(AccessDecisionMadeEventArgs e)
        {
            AccessDecisionMade?.Invoke(this, e);
        }

        private void OnSuspiciousAccessDetected(SuspiciousAccessEventArgs e)
        {
            SuspiciousAccessDetected?.Invoke(this, e);
        }

        #endregion;

        #region Supporting Classes;

        /// <summary>
        /// Decision cache key for efficient caching;
        /// </summary>
        private class DecisionCacheKey : IEquatable<DecisionCacheKey>
        {
            public string UserId { get; }
            public string Resource { get; }
            public string Action { get; }
            public ResourceType ResourceType { get; }
            public AccessLevel RequiredLevel { get; }

            public DecisionCacheKey(AuthorizationContext context)
            {
                UserId = context.UserId;
                Resource = context.Resource;
                Action = context.Action;
                ResourceType = context.ResourceType;
                RequiredLevel = context.RequiredLevel;
            }

            public bool Equals(DecisionCacheKey other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;

                return UserId == other.UserId &&
                       Resource == other.Resource &&
                       Action == other.Action &&
                       ResourceType == other.ResourceType &&
                       RequiredLevel == other.RequiredLevel;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as DecisionCacheKey);
            }

            public override int GetHashCode()
            {
                unchecked;
                {
                    var hashCode = UserId?.GetHashCode() ?? 0;
                    hashCode = (hashCode * 397) ^ (Resource?.GetHashCode() ?? 0);
                    hashCode = (hashCode * 397) ^ (Action?.GetHashCode() ?? 0);
                    hashCode = (hashCode * 397) ^ ResourceType.GetHashCode();
                    hashCode = (hashCode * 397) ^ RequiredLevel.GetHashCode();
                    return hashCode;
                }
            }
        }

        /// <summary>
        /// Simple memory cache implementation;
        /// </summary>
        private class MemoryCache<TKey, TValue> : IDisposable where TKey : class;
        {
            private readonly Dictionary<TKey, CacheEntry> _cache = new Dictionary<TKey, CacheEntry>();
            private readonly TimeSpan _defaultExpiration;
            private readonly int _maxItems;
            private readonly object _lock = new object();
            private Timer _cleanupTimer;

            private class CacheEntry
            {
                public TValue Value { get; set; }
                public DateTime ExpiresAt { get; set; }
                public DateTime LastAccessed { get; set; }
            }

            public MemoryCache(TimeSpan defaultExpiration, int maxItems = 1000)
            {
                _defaultExpiration = defaultExpiration;
                _maxItems = maxItems;
                _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            }

            public int Count;
            {
                get;
                {
                    lock (_lock)
                    {
                        return _cache.Count;
                    }
                }
            }

            public IEnumerable<TKey> Keys;
            {
                get;
                {
                    lock (_lock)
                    {
                        return _cache.Keys.ToList();
                    }
                }
            }

            public TValue Get(TKey key)
            {
                lock (_lock)
                {
                    if (_cache.TryGetValue(key, out var entry))
                    {
                        if (entry.ExpiresAt >= DateTime.UtcNow)
                        {
                            entry.LastAccessed = DateTime.UtcNow;
                            return entry.Value;
                        }

                        _cache.Remove(key);
                    }

                    return default;
                }
            }

            public void Set(TKey key, TValue value, TimeSpan? expiration = null)
            {
                lock (_lock)
                {
                    // Remove oldest items if cache is full;
                    if (_cache.Count >= _maxItems)
                    {
                        var oldest = _cache.OrderBy(e => e.Value.LastAccessed).First();
                        _cache.Remove(oldest.Key);
                    }

                    var expiresAt = DateTime.UtcNow.Add(expiration ?? _defaultExpiration);
                    _cache[key] = new CacheEntry
                    {
                        Value = value,
                        ExpiresAt = expiresAt,
                        LastAccessed = DateTime.UtcNow;
                    };
                }
            }

            public void Remove(TKey key)
            {
                lock (_lock)
                {
                    _cache.Remove(key);
                }
            }

            public void Clear()
            {
                lock (_lock)
                {
                    _cache.Clear();
                }
            }

            private void Cleanup(object state)
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var expiredKeys = _cache.Where(e => e.Value.ExpiresAt < now).Select(e => e.Key).ToList();

                    foreach (var key in expiredKeys)
                    {
                        _cache.Remove(key);
                    }
                }
            }

            public void Dispose()
            {
                _cleanupTimer?.Dispose();
            }
        }

        /// <summary>
        /// Authorization statistics;
        /// </summary>
        public class AuthorizationStatistics;
        {
            public long TotalDecisions { get; set; }
            public long AllowedDecisions { get; set; }
            public long DeniedDecisions { get; set; }
            public double AverageDecisionTimeMs { get; set; }
            public double CacheHitRate { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public TimeSpan Uptime { get; set; }
        }

        /// <summary>
        /// Event arguments for access decision;
        /// </summary>
        public class AccessDecisionMadeEventArgs : EventArgs;
        {
            public AuthorizationContext Context { get; }
            public AccessDecisionResult Result { get; }

            public AccessDecisionMadeEventArgs(AuthorizationContext context, AccessDecisionResult result)
            {
                Context = context;
                Result = result;
            }
        }

        /// <summary>
        /// Event arguments for suspicious access;
        /// </summary>
        public class SuspiciousAccessEventArgs : EventArgs;
        {
            public string UserId { get; }
            public string Username { get; }
            public string Resource { get; }
            public string Action { get; }
            public List<string> Reasons { get; }
            public string ClientIP { get; }
            public DateTime DetectedAt { get; }

            public SuspiciousAccessEventArgs(
                string userId,
                string username,
                string resource,
                string action,
                List<string> reasons,
                string clientIP,
                DateTime detectedAt)
            {
                UserId = userId;
                Username = username;
                Resource = resource;
                Action = action;
                Reasons = reasons;
                ClientIP = clientIP;
                DetectedAt = detectedAt;
            }
        }

        /// <summary>
        /// Event published when access decision is made;
        /// </summary>
        public class AccessDecisionEvent : IEvent;
        {
            public string DecisionId { get; set; }
            public AuthorizationContext Context { get; set; }
            public AccessDecisionResult Result { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Interface for access decision engine;
        /// </summary>
        public interface IAccessDecision;
        {
            Task<AccessDecisionResult> EvaluateAsync(AuthorizationContext context);
            Task<bool> IsAuthorizedAsync(
                ClaimsPrincipal user,
                string resource,
                string action,
                ResourceType resourceType = ResourceType.APIEndpoint,
                AccessLevel requiredLevel = AccessLevel.Read,
                Dictionary<string, object> environment = null);
            Task<bool> SetPolicyAsync(AuthorizationPolicy policy);
            Task<bool> RemovePolicyAsync(string policyId);
            Task<AuthorizationPolicy> GetPolicyAsync(string policyId);
            Task<List<AuthorizationPolicy>> GetPoliciesByResourceTypeAsync(ResourceType resourceType);
            Task<bool> SetRuleAsync(AuthorizationRule rule);
            Task<bool> RemoveRuleAsync(string ruleId);
            Task<AccessDecisionResult> EvaluatePoliciesAsync(AuthorizationContext context);
            Task<AccessDecisionResult> EvaluateRulesAsync(AuthorizationContext context);
            void ClearCache();
            Task<AuthorizationStatistics> GetStatisticsAsync(TimeSpan? period = null);
            Task<List<string>> ValidatePoliciesAsync();
            Task<double> AssessRiskAsync(AuthorizationContext context);
            Task<bool> CheckForAnomaliesAsync(string userId, string resource, string action);

            event EventHandler<AccessDecisionMadeEventArgs> AccessDecisionMade;
            event EventHandler<SuspiciousAccessEventArgs> SuspiciousAccessDetected;
        }

        #endregion;
    }
}
