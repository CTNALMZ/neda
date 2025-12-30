using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Logging;
using NEDA.SecurityModules.Firewall;
using NEDA.SecurityModules.Manifest;

namespace NEDA.SecurityModules.Firewall;
{
    /// <summary>
    /// Access Control List rule types;
    /// </summary>
    public enum AccessRuleType;
    {
        Allow,
        Deny,
        RequireAuth,
        RateLimit,
        TimeRestricted;
    }

    /// <summary>
    /// Access control direction;
    /// </summary>
    public enum AccessDirection;
    {
        Inbound,
        Outbound,
        Both;
    }

    /// <summary>
    /// Network protocol types;
    /// </summary>
    public enum NetworkProtocol;
    {
        TCP,
        UDP,
        ICMP,
        Any;
    }

    /// <summary>
    /// Access rule definition;
    /// </summary>
    public class AccessRule;
    {
        public string RuleId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public AccessRuleType RuleType { get; set; }
        public AccessDirection Direction { get; set; }
        public NetworkProtocol Protocol { get; set; } = NetworkProtocol.Any;

        public IPAddress SourceAddress { get; set; } = IPAddress.Any;
        public int SourcePort { get; set; } = 0;
        public IPAddress DestinationAddress { get; set; } = IPAddress.Any;
        public int DestinationPort { get; set; } = 0;

        public string ApplicationName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;

        public DateTime? ActiveFrom { get; set; }
        public DateTime? ActiveUntil { get; set; }
        public DayOfWeek[] ActiveDays { get; set; } = Array.Empty<DayOfWeek>();
        public TimeSpan? ActiveStartTime { get; set; }
        public TimeSpan? ActiveEndTime { get; set; }

        public int RateLimitCount { get; set; } = 0;
        public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromSeconds(1);

        public bool IsEnabled { get; set; } = true;
        public int Priority { get; set; } = 100;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Access request context;
    /// </summary>
    public class AccessRequest;
    {
        public string RequestId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; } = DateTime.UtcNow;

        public AccessDirection Direction { get; set; }
        public NetworkProtocol Protocol { get; set; }

        public IPAddress SourceAddress { get; set; } = IPAddress.None;
        public int SourcePort { get; set; }
        public IPAddress DestinationAddress { get; set; } = IPAddress.None;
        public int DestinationPort { get; set; }

        public string ApplicationName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public int ProcessId { get; set; }

        public object UserContext { get; set; }
        public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Access control decision;
    /// </summary>
    public class AccessDecision;
    {
        public bool IsAllowed { get; set; }
        public string RuleId { get; set; } = string.Empty;
        public string RuleName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime DecisionTime { get; } = DateTime.UtcNow;
        public AccessRule MatchedRule { get; set; }
        public AccessRequest Request { get; set; }

        public Dictionary<string, object> Details { get; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rate limiting state;
    /// </summary>
    private class RateLimitState;
    {
        public DateTime WindowStart { get; set; } = DateTime.UtcNow;
        public int RequestCount { get; set; }
        public ConcurrentQueue<DateTime> RequestTimes { get; } = new ConcurrentQueue<DateTime>();
    }

    /// <summary>
    /// Advanced access controller for firewall and network security;
    /// Implements rule-based access control with rate limiting, time restrictions, and application filtering;
    /// </summary>
    public class AccessController : IDisposable
    {
        private readonly ILogger<AccessController> _logger;
        private readonly RuleEngine _ruleEngine;
        private readonly SecurityManifest _securityManifest;
        private readonly IAuditLogger _auditLogger;

        private readonly ConcurrentDictionary<string, AccessRule> _rules;
        private readonly ConcurrentDictionary<string, RateLimitState> _rateLimits;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _syncLocks;

        private readonly Timer _cleanupTimer;
        private readonly Timer _ruleRefreshTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private bool _isDisposed;
        private bool _isEnabled = true;
        private int _totalRequestsProcessed;
        private int _blockedRequests;
        private int _allowedRequests;
        private DateTime _startTime = DateTime.UtcNow;

        /// <summary>
        /// Event raised when access decision is made;
        /// </summary>
        public event EventHandler<AccessDecision> AccessDecisionMade;

        /// <summary>
        /// Event raised when rule is violated;
        /// </summary>
        public event EventHandler<AccessDecision> RuleViolationDetected;

        /// <summary>
        /// Initializes a new instance of the AccessController;
        /// </summary>
        public AccessController(
            ILogger<AccessController> logger,
            RuleEngine ruleEngine,
            SecurityManifest securityManifest,
            IAuditLogger auditLogger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
            _securityManifest = securityManifest ?? throw new ArgumentNullException(nameof(securityManifest));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

            _rules = new ConcurrentDictionary<string, AccessRule>();
            _rateLimits = new ConcurrentDictionary<string, RateLimitState>();
            _syncLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
            _cancellationTokenSource = new CancellationTokenSource();

            // Initialize cleanup timer (every 5 minutes)
            _cleanupTimer = new Timer(CleanupExpiredData, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // Initialize rule refresh timer (every 1 minute)
            _ruleRefreshTimer = new Timer(RefreshRules, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            LoadDefaultRules();
            InitializeFromManifest();

            _logger.LogInformation("AccessController initialized successfully");
        }

        /// <summary>
        /// Loads default security rules;
        /// </summary>
        private void LoadDefaultRules()
        {
            try
            {
                // Default rule: Allow loopback traffic;
                var loopbackRule = new AccessRule;
                {
                    Name = "Allow Loopback",
                    Description = "Allow all loopback traffic",
                    RuleType = AccessRuleType.Allow,
                    Direction = AccessDirection.Both,
                    Protocol = NetworkProtocol.Any,
                    SourceAddress = IPAddress.Loopback,
                    DestinationAddress = IPAddress.Loopback,
                    Priority = 1000;
                };
                AddRule(loopbackRule);

                // Default rule: Deny private network access from external;
                var privateNetworkRule = new AccessRule;
                {
                    Name = "Restrict Private Networks",
                    Description = "Restrict access to private networks",
                    RuleType = AccessRuleType.Deny,
                    Direction = AccessDirection.Inbound,
                    Protocol = NetworkProtocol.Any,
                    SourceAddress = IPAddress.Parse("10.0.0.0"),
                    Priority = 500;
                };
                AddRule(privateNetworkRule);

                _logger.LogInformation("Loaded {Count} default rules", _rules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load default rules");
            }
        }

        /// <summary>
        /// Initializes rules from security manifest;
        /// </summary>
        private void InitializeFromManifest()
        {
            try
            {
                if (_securityManifest?.FirewallRules != null)
                {
                    foreach (var manifestRule in _securityManifest.FirewallRules)
                    {
                        var rule = ConvertFromManifest(manifestRule);
                        if (rule != null)
                        {
                            AddRule(rule);
                        }
                    }
                    _logger.LogInformation("Loaded {Count} rules from security manifest",
                        _securityManifest.FirewallRules.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize from security manifest");
            }
        }

        /// <summary>
        /// Checks if access is allowed for the given request;
        /// </summary>
        public async Task<AccessDecision> CheckAccessAsync(AccessRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!_isEnabled)
            {
                return new AccessDecision;
                {
                    IsAllowed = true,
                    Reason = "AccessController is disabled",
                    Request = request;
                };
            }

            Interlocked.Increment(ref _totalRequestsProcessed);

            try
            {
                // Get applicable rules sorted by priority;
                var applicableRules = GetApplicableRules(request)
                    .OrderBy(r => r.Priority)
                    .ThenByDescending(r => r.CreatedAt)
                    .ToList();

                if (applicableRules.Count == 0)
                {
                    // Default deny if no rules apply;
                    return CreateDenyDecision(request, "No applicable rules found");
                }

                // Evaluate rules in order;
                foreach (var rule in applicableRules)
                {
                    var decision = await EvaluateRuleAsync(rule, request);
                    if (decision != null)
                    {
                        // Log decision;
                        await LogAccessDecisionAsync(decision);

                        // Raise events;
                        OnAccessDecisionMade(decision);

                        if (!decision.IsAllowed)
                        {
                            OnRuleViolationDetected(decision);
                        }

                        return decision;
                    }
                }

                // If no rule made a definitive decision, default deny;
                return CreateDenyDecision(request, "No rule matched the request");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking access for request {RequestId}", request.RequestId);
                return CreateDenyDecision(request, $"Error processing request: {ex.Message}");
            }
        }

        /// <summary>
        /// Evaluates a single rule against the request;
        /// </summary>
        private async Task<AccessDecision> EvaluateRuleAsync(AccessRule rule, AccessRequest request)
        {
            if (!rule.IsEnabled)
                return null;

            // Check time restrictions;
            if (!IsRuleActive(rule))
                return null;

            // Check if rule applies to this request;
            if (!DoesRuleApply(rule, request))
                return null;

            // Apply rate limiting if configured;
            if (rule.RuleType == AccessRuleType.RateLimit && rule.RateLimitCount > 0)
            {
                var rateLimitKey = GetRateLimitKey(rule, request);
                if (!await CheckRateLimitAsync(rateLimitKey, rule.RateLimitCount, rule.RateLimitWindow))
                {
                    return CreateDenyDecision(request, rule, "Rate limit exceeded");
                }
            }

            // Create decision based on rule type;
            return rule.RuleType switch;
            {
                AccessRuleType.Allow => CreateAllowDecision(request, rule),
                AccessRuleType.Deny => CreateDenyDecision(request, rule, "Explicitly denied by rule"),
                AccessRuleType.RequireAuth => await CheckAuthenticationAsync(request, rule),
                AccessRuleType.RateLimit => CreateAllowDecision(request, rule),
                AccessRuleType.TimeRestricted => CreateAllowDecision(request, rule),
                _ => CreateDenyDecision(request, rule, "Unknown rule type")
            };
        }

        /// <summary>
        /// Checks if rule is currently active based on time restrictions;
        /// </summary>
        private bool IsRuleActive(AccessRule rule)
        {
            var now = DateTime.UtcNow;
            var currentTime = now.TimeOfDay;
            var currentDay = now.DayOfWeek;

            // Check date range;
            if (rule.ActiveFrom.HasValue && now < rule.ActiveFrom.Value)
                return false;
            if (rule.ActiveUntil.HasValue && now > rule.ActiveUntil.Value)
                return false;

            // Check day of week;
            if (rule.ActiveDays != null && rule.ActiveDays.Length > 0)
            {
                if (!rule.ActiveDays.Contains(currentDay))
                    return false;
            }

            // Check time of day;
            if (rule.ActiveStartTime.HasValue && rule.ActiveEndTime.HasValue)
            {
                if (currentTime < rule.ActiveStartTime.Value || currentTime > rule.ActiveEndTime.Value)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if rule applies to the request;
        /// </summary>
        private bool DoesRuleApply(AccessRule rule, AccessRequest request)
        {
            // Check direction;
            if (rule.Direction != AccessDirection.Both && rule.Direction != request.Direction)
                return false;

            // Check protocol;
            if (rule.Protocol != NetworkProtocol.Any && rule.Protocol != request.Protocol)
                return false;

            // Check source address;
            if (!IsAddressMatch(rule.SourceAddress, request.SourceAddress))
                return false;

            // Check destination address;
            if (!IsAddressMatch(rule.DestinationAddress, request.DestinationAddress))
                return false;

            // Check ports (0 means any port)
            if (rule.SourcePort > 0 && rule.SourcePort != request.SourcePort)
                return false;
            if (rule.DestinationPort > 0 && rule.DestinationPort != request.DestinationPort)
                return false;

            // Check application/process if specified;
            if (!string.IsNullOrEmpty(rule.ApplicationName) &&
                !string.Equals(rule.ApplicationName, request.ApplicationName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(rule.ProcessPath) &&
                !string.Equals(rule.ProcessPath, request.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// Checks IP address matching with subnet support;
        /// </summary>
        private bool IsAddressMatch(IPAddress ruleAddress, IPAddress requestAddress)
        {
            if (ruleAddress.Equals(IPAddress.Any))
                return true;

            if (ruleAddress.Equals(IPAddress.None))
                return false;

            // Simple exact match for now;
            // TODO: Implement subnet mask matching;
            return ruleAddress.Equals(requestAddress);
        }

        /// <summary>
        /// Checks rate limiting;
        /// </summary>
        private async Task<bool> CheckRateLimitAsync(string key, int limit, TimeSpan window)
        {
            var syncLock = _syncLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await syncLock.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                var state = _rateLimits.GetOrAdd(key, _ => new RateLimitState());

                // Clean old requests;
                while (state.RequestTimes.TryPeek(out var oldest) &&
                       now - oldest > window)
                {
                    state.RequestTimes.TryDequeue(out _);
                    state.RequestCount = Math.Max(0, state.RequestCount - 1);
                }

                // Check if limit exceeded;
                if (state.RequestCount >= limit)
                    return false;

                // Add current request;
                state.RequestTimes.Enqueue(now);
                state.RequestCount++;
                state.WindowStart = now;

                return true;
            }
            finally
            {
                syncLock.Release();
            }
        }

        /// <summary>
        /// Checks authentication requirements;
        /// </summary>
        private async Task<AccessDecision> CheckAuthenticationAsync(AccessRequest request, AccessRule rule)
        {
            // TODO: Implement authentication check;
            // This would integrate with NEDA.SecurityModules.AdvancedSecurity.Authentication;

            // For now, simulate authentication check;
            var isAuthenticated = await Task.Run(() =>
            {
                // Check if request has user context;
                return request.UserContext != null;
            });

            if (isAuthenticated)
            {
                return CreateAllowDecision(request, rule);
            }
            else;
            {
                return CreateDenyDecision(request, rule, "Authentication required");
            }
        }

        /// <summary>
        /// Gets applicable rules for the request;
        /// </summary>
        private IEnumerable<AccessRule> GetApplicableRules(AccessRequest request)
        {
            foreach (var rule in _rules.Values)
            {
                if (rule.IsEnabled && DoesRuleApply(rule, request))
                {
                    yield return rule;
                }
            }
        }

        /// <summary>
        /// Creates an allow decision;
        /// </summary>
        private AccessDecision CreateAllowDecision(AccessRequest request, AccessRule rule = null)
        {
            Interlocked.Increment(ref _allowedRequests);

            return new AccessDecision;
            {
                IsAllowed = true,
                RuleId = rule?.RuleId ?? string.Empty,
                RuleName = rule?.Name ?? "Default Allow",
                Reason = rule != null ? $"Allowed by rule: {rule.Name}" : "Default allow",
                MatchedRule = rule,
                Request = request;
            };
        }

        /// <summary>
        /// Creates a deny decision;
        /// </summary>
        private AccessDecision CreateDenyDecision(AccessRequest request, AccessRule rule = null, string reason = null)
        {
            Interlocked.Increment(ref _blockedRequests);

            return new AccessDecision;
            {
                IsAllowed = false,
                RuleId = rule?.RuleId ?? string.Empty,
                RuleName = rule?.Name ?? "Default Deny",
                Reason = reason ?? (rule != null ? $"Denied by rule: {rule.Name}" : "Default deny"),
                MatchedRule = rule,
                Request = request;
            };
        }

        /// <summary>
        /// Gets rate limit key for tracking;
        /// </summary>
        private string GetRateLimitKey(AccessRule rule, AccessRequest request)
        {
            return $"{rule.RuleId}:{request.SourceAddress}:{request.DestinationPort}";
        }

        /// <summary>
        /// Adds a new access rule;
        /// </summary>
        public bool AddRule(AccessRule rule)
        {
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            if (string.IsNullOrEmpty(rule.RuleId))
                rule.RuleId = Guid.NewGuid().ToString();

            rule.ModifiedAt = DateTime.UtcNow;

            if (_rules.TryAdd(rule.RuleId, rule))
            {
                _logger.LogInformation("Added access rule: {RuleName} ({RuleId})", rule.Name, rule.RuleId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Updates an existing access rule;
        /// </summary>
        public bool UpdateRule(AccessRule rule)
        {
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            if (string.IsNullOrEmpty(rule.RuleId))
                throw new ArgumentException("RuleId cannot be null or empty", nameof(rule));

            rule.ModifiedAt = DateTime.UtcNow;

            if (_rules.TryGetValue(rule.RuleId, out var existingRule))
            {
                _rules[rule.RuleId] = rule;
                _logger.LogInformation("Updated access rule: {RuleName} ({RuleId})", rule.Name, rule.RuleId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes an access rule;
        /// </summary>
        public bool RemoveRule(string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentException("RuleId cannot be null or empty", nameof(ruleId));

            if (_rules.TryRemove(ruleId, out var removedRule))
            {
                _logger.LogInformation("Removed access rule: {RuleName} ({RuleId})",
                    removedRule.Name, removedRule.RuleId);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets all access rules;
        /// </summary>
        public IReadOnlyCollection<AccessRule> GetAllRules()
        {
            return _rules.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets rule by ID;
        /// </summary>
        public AccessRule GetRule(string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentException("RuleId cannot be null or empty", nameof(ruleId));

            _rules.TryGetValue(ruleId, out var rule);
            return rule;
        }

        /// <summary>
        /// Enables or disables the access controller;
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
            _logger.LogInformation("AccessController {State}", enabled ? "enabled" : "disabled");
        }

        /// <summary>
        /// Clears all rules;
        /// </summary>
        public void ClearRules()
        {
            _rules.Clear();
            _logger.LogInformation("Cleared all access rules");
        }

        /// <summary>
        /// Gets statistics about access control;
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>
            {
                ["TotalRequestsProcessed"] = _totalRequestsProcessed,
                ["AllowedRequests"] = _allowedRequests,
                ["BlockedRequests"] = _blockedRequests,
                ["ActiveRules"] = _rules.Count,
                ["IsEnabled"] = _isEnabled,
                ["Uptime"] = DateTime.UtcNow - _startTime,
                ["RateLimitEntries"] = _rateLimits.Count;
            };

            // Calculate percentages;
            if (_totalRequestsProcessed > 0)
            {
                stats["AllowPercentage"] = (double)_allowedRequests / _totalRequestsProcessed * 100;
                stats["BlockPercentage"] = (double)_blockedRequests / _totalRequestsProcessed * 100;
            }

            return stats;
        }

        /// <summary>
        /// Logs access decision for audit trail;
        /// </summary>
        private async Task LogAccessDecisionAsync(AccessDecision decision)
        {
            try
            {
                var auditEntry = new;
                {
                    DecisionId = Guid.NewGuid().ToString(),
                    Timestamp = decision.DecisionTime,
                    IsAllowed = decision.IsAllowed,
                    RuleId = decision.RuleId,
                    RuleName = decision.RuleName,
                    Reason = decision.Reason,
                    RequestId = decision.Request.RequestId,
                    SourceAddress = decision.Request.SourceAddress?.ToString(),
                    DestinationAddress = decision.Request.DestinationAddress?.ToString(),
                    DestinationPort = decision.Request.DestinationPort,
                    Protocol = decision.Request.Protocol.ToString(),
                    ApplicationName = decision.Request.ApplicationName,
                    ProcessPath = decision.Request.ProcessPath;
                };

                await _auditLogger.LogSecurityEventAsync("AccessControl", auditEntry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log access decision");
            }
        }

        /// <summary>
        /// Converts manifest rule to AccessRule;
        /// </summary>
        private AccessRule ConvertFromManifest(FirewallRule manifestRule)
        {
            try
            {
                return new AccessRule;
                {
                    RuleId = manifestRule.Id,
                    Name = manifestRule.Name,
                    Description = manifestRule.Description,
                    RuleType = ParseRuleType(manifestRule.Action),
                    Direction = ParseDirection(manifestRule.Direction),
                    Protocol = ParseProtocol(manifestRule.Protocol),
                    SourceAddress = ParseIPAddress(manifestRule.Source),
                    DestinationAddress = ParseIPAddress(manifestRule.Destination),
                    SourcePort = manifestRule.SourcePort ?? 0,
                    DestinationPort = manifestRule.DestinationPort ?? 0,
                    IsEnabled = manifestRule.Enabled,
                    Priority = manifestRule.Priority;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert manifest rule: {RuleId}", manifestRule.Id);
                return null;
            }
        }

        /// <summary>
        /// Parses rule type from string;
        /// </summary>
        private AccessRuleType ParseRuleType(string action)
        {
            return action?.ToLowerInvariant() switch;
            {
                "allow" => AccessRuleType.Allow,
                "deny" => AccessRuleType.Deny,
                "require_auth" => AccessRuleType.RequireAuth,
                "rate_limit" => AccessRuleType.RateLimit,
                "time_restricted" => AccessRuleType.TimeRestricted,
                _ => AccessRuleType.Deny;
            };
        }

        /// <summary>
        /// Parses direction from string;
        /// </summary>
        private AccessDirection ParseDirection(string direction)
        {
            return direction?.ToLowerInvariant() switch;
            {
                "inbound" => AccessDirection.Inbound,
                "outbound" => AccessDirection.Outbound,
                "both" => AccessDirection.Both,
                _ => AccessDirection.Both;
            };
        }

        /// <summary>
        /// Parses protocol from string;
        /// </summary>
        private NetworkProtocol ParseProtocol(string protocol)
        {
            return protocol?.ToUpperInvariant() switch;
            {
                "TCP" => NetworkProtocol.TCP,
                "UDP" => NetworkProtocol.UDP,
                "ICMP" => NetworkProtocol.ICMP,
                "ANY" => NetworkProtocol.Any,
                _ => NetworkProtocol.Any;
            };
        }

        /// <summary>
        /// Parses IP address from string;
        /// </summary>
        private IPAddress ParseIPAddress(string address)
        {
            if (string.IsNullOrEmpty(address) || address == "*")
                return IPAddress.Any;

            if (IPAddress.TryParse(address, out var ip))
                return ip;

            return IPAddress.Any;
        }

        /// <summary>
        /// Cleans up expired data (rate limits, etc.)
        /// </summary>
        private void CleanupExpiredData(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var keysToRemove = new List<string>();

                // Clean up old rate limits;
                foreach (var kvp in _rateLimits)
                {
                    var state = kvp.Value;
                    if (now - state.WindowStart > TimeSpan.FromHours(1))
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _rateLimits.TryRemove(key, out _);
                    _syncLocks.TryRemove(key, out var syncLock);
                    syncLock?.Dispose();
                }

                // Clean up old sync locks;
                var lockKeysToRemove = _syncLocks;
                    .Where(kvp => !_rateLimits.ContainsKey(kvp.Key))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in lockKeysToRemove)
                {
                    if (_syncLocks.TryRemove(key, out var syncLock))
                    {
                        syncLock.Dispose();
                    }
                }

                _logger.LogDebug("Cleanup removed {Count} expired entries",
                    keysToRemove.Count + lockKeysToRemove.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
            }
        }

        /// <summary>
        /// Refreshes rules from external sources;
        /// </summary>
        private void RefreshRules(object state)
        {
            try
            {
                // TODO: Implement rule refresh from external sources;
                // This could load rules from database, API, or configuration files;

                _logger.LogDebug("Rule refresh executed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing rules");
            }
        }

        /// <summary>
        /// Raises AccessDecisionMade event;
        /// </summary>
        protected virtual void OnAccessDecisionMade(AccessDecision decision)
        {
            AccessDecisionMade?.Invoke(this, decision);
        }

        /// <summary>
        /// Raises RuleViolationDetected event;
        /// </summary>
        protected virtual void OnRuleViolationDetected(AccessDecision decision)
        {
            RuleViolationDetected?.Invoke(this, decision);
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cancellationTokenSource.Cancel();

            _cleanupTimer?.Dispose();
            _ruleRefreshTimer?.Dispose();
            _cancellationTokenSource?.Dispose();

            foreach (var syncLock in _syncLocks.Values)
            {
                syncLock.Dispose();
            }

            _rules.Clear();
            _rateLimits.Clear();
            _syncLocks.Clear();

            _logger.LogInformation("AccessController disposed");

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~AccessController()
        {
            Dispose();
        }
    }

    // Supporting classes for Security Manifest integration;
    public class FirewallRule;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Action { get; set; } = "deny";
        public string Direction { get; set; } = "both";
        public string Protocol { get; set; } = "any";
        public string Source { get; set; } = "any";
        public string Destination { get; set; } = "any";
        public int? SourcePort { get; set; }
        public int? DestinationPort { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;
    }

    public class SecurityManifest;
    {
        public List<FirewallRule> FirewallRules { get; set; } = new List<FirewallRule>();
    }
}
