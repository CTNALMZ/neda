using Amazon.S3;
using Microsoft.Extensions.Logging;
using NEDA.Core.SystemControl;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.PersonalAssistant.ReminderSystem;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.SecurityModules.Firewall.Rules;
using NEDA.SecurityModules.Manifest;
using NEDA.SecurityModules.Monitoring;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.SecurityModules.Firewall.FirewallManager;

namespace NEDA.SecurityModules.Firewall;
{
    /// <summary>
    /// Firewall Manager - Advanced firewall management system with rule processing,
    /// traffic monitoring, and real-time threat detection.
    /// </summary>
    public class FirewallManager : IFirewallManager, IDisposable;
    {
        #region Private Fields;

        private readonly IFirewallRuleEngine _ruleEngine;
        private readonly IAccessController _accessController;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IAuditLogger _auditLogger;
        private readonly IManifestManager _manifestManager;
        private readonly ILogger<FirewallManager> _logger;
        private readonly IFirewallRuleRepository _ruleRepository;

        private readonly Dictionary<string, FirewallRule> _activeRules;
        private readonly List<NetworkConnection> _activeConnections;
        private readonly Timer _monitoringTimer;
        private readonly Timer _ruleSyncTimer;
        private readonly SemaphoreSlim _ruleLock = new SemaphoreSlim(1, 1);
        private readonly FirewallConfiguration _configuration;

        private bool _isInitialized;
        private bool _isRunning;
        private int _totalPacketsProcessed;
        private int _totalBlocks;
        private int _totalAllows;

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when a rule is added, updated, or removed;
        /// </summary>
        public event EventHandler<FirewallRuleEventArgs> RuleChanged;

        /// <summary>
        /// Raised when traffic is blocked;
        /// </summary>
        public event EventHandler<TrafficBlockedEventArgs> TrafficBlocked;

        /// <summary>
        /// Raised when suspicious activity is detected;
        /// </summary>
        public event EventHandler<SuspiciousActivityEventArgs> SuspiciousActivityDetected;

        /// <summary>
        /// Raised when firewall status changes;
        /// </summary>
        public event EventHandler<FirewallStatusEventArgs> StatusChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of the FirewallManager class;
        /// </summary>
        public FirewallManager(
            IFirewallRuleEngine ruleEngine,
            IAccessController accessController,
            ISecurityMonitor securityMonitor,
            IAuditLogger auditLogger,
            IManifestManager manifestManager,
            ILogger<FirewallManager> logger,
            IFirewallRuleRepository ruleRepository = null)
        {
            _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
            _accessController = accessController ?? throw new ArgumentNullException(nameof(accessController));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _manifestManager = manifestManager ?? throw new ArgumentNullException(nameof(manifestManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ruleRepository = ruleRepository;

            _activeRules = new Dictionary<string, FirewallRule>(StringComparer.OrdinalIgnoreCase);
            _activeConnections = new List<NetworkConnection>();
            _configuration = new FirewallConfiguration();

            // Initialize timers for monitoring and synchronization;
            _monitoringTimer = new Timer(MonitoringCallback, null, Timeout.Infinite, Timeout.Infinite);
            _ruleSyncTimer = new Timer(RuleSyncCallback, null, Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation("FirewallManager initialized");
        }

        #endregion;

        #region Public Methods - Core Operations;

        /// <summary>
        /// Initializes the firewall manager with configuration;
        /// </summary>
        public async Task InitializeAsync(FirewallConfiguration configuration = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (_isInitialized)
                {
                    _logger.LogWarning("FirewallManager already initialized");
                    return;
                }

                if (configuration != null)
                {
                    _configuration = configuration;
                }

                // Load initial rules from repository or manifest;
                await LoadRulesAsync(cancellationToken);

                // Initialize rule engine;
                await _ruleEngine.InitializeAsync(_activeRules.Values.ToList(), cancellationToken);

                // Initialize access controller;
                await _accessController.InitializeAsync(cancellationToken);

                // Start monitoring;
                StartMonitoring();

                _isInitialized = true;

                OnStatusChanged(new FirewallStatusEventArgs;
                {
                    Status = FirewallStatus.Initialized,
                    Message = "Firewall initialized successfully"
                });

                _logger.LogInformation("FirewallManager initialized successfully with {RuleCount} rules", _activeRules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize FirewallManager");
                throw new FirewallInitializationException("Failed to initialize firewall", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Starts the firewall protection;
        /// </summary>
        public async Task StartProtectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (!_isInitialized)
                {
                    throw new InvalidOperationException("Firewall must be initialized before starting protection");
                }

                if (_isRunning)
                {
                    _logger.LogWarning("Firewall protection already running");
                    return;
                }

                // Apply rules to system;
                await ApplyRulesToSystemAsync(cancellationToken);

                // Start monitoring;
                _monitoringTimer.Change(0, _configuration.MonitoringInterval);

                // Start rule synchronization;
                if (_configuration.EnableRuleSync)
                {
                    _ruleSyncTimer.Change(_configuration.RuleSyncInterval, _configuration.RuleSyncInterval);
                }

                _isRunning = true;

                OnStatusChanged(new FirewallStatusEventArgs;
                {
                    Status = FirewallStatus.Running,
                    Message = "Firewall protection started"
                });

                _logger.LogInformation("Firewall protection started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start firewall protection");
                throw new FirewallOperationException("Failed to start firewall protection", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Stops the firewall protection;
        /// </summary>
        public async Task StopProtectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (!_isRunning)
                {
                    _logger.LogWarning("Firewall protection not running");
                    return;
                }

                // Stop timers;
                _monitoringTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _ruleSyncTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // Remove rules from system;
                await RemoveRulesFromSystemAsync(cancellationToken);

                _isRunning = false;

                OnStatusChanged(new FirewallStatusEventArgs;
                {
                    Status = FirewallStatus.Stopped,
                    Message = "Firewall protection stopped"
                });

                _logger.LogInformation("Firewall protection stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop firewall protection");
                throw new FirewallOperationException("Failed to stop firewall protection", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Processes network traffic and applies firewall rules;
        /// </summary>
        public async Task<TrafficDecision> ProcessTrafficAsync(
            NetworkTraffic traffic,
            CancellationToken cancellationToken = default)
        {
            if (traffic == null)
                throw new ArgumentNullException(nameof(traffic));

            if (!_isRunning)
            {
                _logger.LogWarning("Firewall protection not running, allowing traffic by default");
                return TrafficDecision.Allow;
            }

            try
            {
                Interlocked.Increment(ref _totalPacketsProcessed);

                // Check with rule engine;
                var decision = await _ruleEngine.EvaluateTrafficAsync(traffic, cancellationToken);

                // Check with access controller;
                var accessDecision = await _accessController.CheckAccessAsync(traffic, cancellationToken);

                // Final decision;
                var finalDecision = CombineDecisions(decision, accessDecision);

                // Log the decision;
                await LogTrafficDecisionAsync(traffic, finalDecision, cancellationToken);

                // Update counters;
                if (finalDecision == TrafficDecision.Block)
                {
                    Interlocked.Increment(ref _totalBlocks);

                    OnTrafficBlocked(new TrafficBlockedEventArgs;
                    {
                        Traffic = traffic,
                        BlockReason = decision.Reason,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Check for suspicious activity;
                    await CheckSuspiciousActivityAsync(traffic, cancellationToken);
                }
                else;
                {
                    Interlocked.Increment(ref _totalAllows);
                }

                return finalDecision;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing traffic from {SourceIP} to {DestinationIP}",
                    traffic.SourceIP, traffic.DestinationIP);

                // Default to block in case of error (fail-secure)
                return TrafficDecision.Block;
            }
        }

        #endregion;

        #region Public Methods - Rule Management;

        /// <summary>
        /// Adds a new firewall rule;
        /// </summary>
        public async Task<FirewallRule> AddRuleAsync(
            FirewallRule rule,
            bool applyImmediately = true,
            CancellationToken cancellationToken = default)
        {
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                // Generate ID if not provided;
                if (string.IsNullOrEmpty(rule.Id))
                {
                    rule.Id = Guid.NewGuid().ToString();
                }

                // Validate rule;
                ValidateRule(rule);

                // Check for duplicates;
                if (_activeRules.ContainsKey(rule.Id))
                {
                    throw new FirewallRuleException($"Rule with ID {rule.Id} already exists");
                }

                // Add to active rules;
                _activeRules[rule.Id] = rule;

                // Save to repository if available;
                if (_ruleRepository != null)
                {
                    await _ruleRepository.AddRuleAsync(rule, cancellationToken);
                }

                // Apply to system if running and requested;
                if (_isRunning && applyImmediately)
                {
                    await ApplyRuleToSystemAsync(rule, cancellationToken);
                }

                // Log the addition;
                await _auditLogger.LogAsync(new AuditLogEntry
                {
                    Action = "FirewallRuleAdded",
                    Resource = $"FirewallRule/{rule.Id}",
                    User = rule.CreatedBy ?? "System",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rule '{rule.Name}' ({rule.Id}) added"
                }, cancellationToken);

                OnRuleChanged(new FirewallRuleEventArgs;
                {
                    Action = RuleAction.Added,
                    Rule = rule,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Firewall rule '{RuleName}' ({RuleId}) added", rule.Name, rule.Id);

                return rule;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add firewall rule '{RuleName}'", rule.Name);
                throw new FirewallRuleException("Failed to add firewall rule", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Updates an existing firewall rule;
        /// </summary>
        public async Task<FirewallRule> UpdateRuleAsync(
            string ruleId,
            FirewallRule rule,
            bool applyImmediately = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentNullException(nameof(ruleId));
            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (!_activeRules.ContainsKey(ruleId))
                {
                    throw new FirewallRuleException($"Rule with ID {ruleId} not found");
                }

                // Preserve original ID;
                rule.Id = ruleId;

                // Validate rule;
                ValidateRule(rule);

                var oldRule = _activeRules[ruleId];

                // Update in active rules;
                _activeRules[ruleId] = rule;

                // Update in repository if available;
                if (_ruleRepository != null)
                {
                    await _ruleRepository.UpdateRuleAsync(rule, cancellationToken);
                }

                // Apply to system if running and requested;
                if (_isRunning && applyImmediately)
                {
                    await UpdateRuleInSystemAsync(oldRule, rule, cancellationToken);
                }

                // Log the update;
                await _auditLogger.LogAsync(new AuditLogEntry
                {
                    Action = "FirewallRuleUpdated",
                    Resource = $"FirewallRule/{rule.Id}",
                    User = rule.ModifiedBy ?? "System",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rule '{rule.Name}' ({rule.Id}) updated"
                }, cancellationToken);

                OnRuleChanged(new FirewallRuleEventArgs;
                {
                    Action = RuleAction.Updated,
                    Rule = rule,
                    OldRule = oldRule,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Firewall rule '{RuleName}' ({RuleId}) updated", rule.Name, rule.Id);

                return rule;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update firewall rule '{RuleId}'", ruleId);
                throw new FirewallRuleException("Failed to update firewall rule", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Removes a firewall rule;
        /// </summary>
        public async Task RemoveRuleAsync(
            string ruleId,
            bool removeFromSystem = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentNullException(nameof(ruleId));

            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (!_activeRules.ContainsKey(ruleId))
                {
                    throw new FirewallRuleException($"Rule with ID {ruleId} not found");
                }

                var rule = _activeRules[ruleId];

                // Remove from system if running and requested;
                if (_isRunning && removeFromSystem)
                {
                    await RemoveRuleFromSystemAsync(rule, cancellationToken);
                }

                // Remove from active rules;
                _activeRules.Remove(ruleId);

                // Remove from repository if available;
                if (_ruleRepository != null)
                {
                    await _ruleRepository.RemoveRuleAsync(ruleId, cancellationToken);
                }

                // Log the removal;
                await _auditLogger.LogAsync(new AuditLogEntry
                {
                    Action = "FirewallRuleRemoved",
                    Resource = $"FirewallRule/{rule.Id}",
                    User = rule.ModifiedBy ?? "System",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rule '{rule.Name}' ({rule.Id}) removed"
                }, cancellationToken);

                OnRuleChanged(new FirewallRuleEventArgs;
                {
                    Action = RuleAction.Removed,
                    Rule = rule,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Firewall rule '{RuleName}' ({RuleId}) removed", rule.Name, rule.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove firewall rule '{RuleId}'", ruleId);
                throw new FirewallRuleException("Failed to remove firewall rule", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Gets all active firewall rules;
        /// </summary>
        public async Task<IEnumerable<FirewallRule>> GetRulesAsync(
            RuleFilter filter = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                var rules = _activeRules.Values.AsEnumerable();

                if (filter != null)
                {
                    rules = ApplyFilter(rules, filter);
                }

                return rules.ToList();
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Gets a specific firewall rule by ID;
        /// </summary>
        public async Task<FirewallRule> GetRuleAsync(string ruleId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentNullException(nameof(ruleId));

            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (_activeRules.TryGetValue(ruleId, out var rule))
                {
                    return rule;
                }

                return null;
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Enables or disables a firewall rule;
        /// </summary>
        public async Task ToggleRuleAsync(
            string ruleId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentNullException(nameof(ruleId));

            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                if (!_activeRules.TryGetValue(ruleId, out var rule))
                {
                    throw new FirewallRuleException($"Rule with ID {ruleId} not found");
                }

                if (rule.IsEnabled == enabled)
                {
                    return; // No change needed;
                }

                var oldRule = rule.Clone();
                rule.IsEnabled = enabled;
                rule.ModifiedDate = DateTime.UtcNow;

                // Update in repository if available;
                if (_ruleRepository != null)
                {
                    await _ruleRepository.UpdateRuleAsync(rule, cancellationToken);
                }

                // Apply to system if running;
                if (_isRunning)
                {
                    if (enabled)
                    {
                        await ApplyRuleToSystemAsync(rule, cancellationToken);
                    }
                    else;
                    {
                        await RemoveRuleFromSystemAsync(rule, cancellationToken);
                    }
                }

                // Log the toggle;
                await _auditLogger.LogAsync(new AuditLogEntry
                {
                    Action = "FirewallRuleToggled",
                    Resource = $"FirewallRule/{rule.Id}",
                    User = rule.ModifiedBy ?? "System",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rule '{rule.Name}' ({rule.Id}) {(enabled ? "enabled" : "disabled")}"
                }, cancellationToken);

                OnRuleChanged(new FirewallRuleEventArgs;
                {
                    Action = RuleAction.Updated,
                    Rule = rule,
                    OldRule = oldRule,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Firewall rule '{RuleName}' ({RuleId}) {Status}",
                    rule.Name, rule.Id, enabled ? "enabled" : "disabled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle firewall rule '{RuleId}'", ruleId);
                throw new FirewallRuleException("Failed to toggle firewall rule", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        #endregion;

        #region Public Methods - Monitoring & Diagnostics;

        /// <summary>
        /// Gets firewall statistics;
        /// </summary>
        public async Task<FirewallStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                return new FirewallStatistics;
                {
                    TotalRules = _activeRules.Count,
                    EnabledRules = _activeRules.Values.Count(r => r.IsEnabled),
                    ActiveConnections = _activeConnections.Count,
                    TotalPacketsProcessed = _totalPacketsProcessed,
                    TotalBlocks = _totalBlocks,
                    TotalAllows = _totalAllows,
                    IsRunning = _isRunning,
                    IsInitialized = _isInitialized,
                    LastUpdated = DateTime.UtcNow;
                };
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Gets active network connections;
        /// </summary>
        public async Task<IEnumerable<NetworkConnection>> GetActiveConnectionsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);
                return _activeConnections.ToList();
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Scans for rule violations and anomalies;
        /// </summary>
        public async Task<SecurityScanResult> ScanForViolationsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var violations = new List<RuleViolation>();
                var anomalies = new List<NetworkAnomaly>();

                // Check for expired rules;
                var expiredRules = _activeRules.Values;
                    .Where(r => r.ExpirationDate.HasValue && r.ExpirationDate.Value < DateTime.UtcNow)
                    .ToList();

                foreach (var rule in expiredRules)
                {
                    violations.Add(new RuleViolation;
                    {
                        RuleId = rule.Id,
                        RuleName = rule.Name,
                        ViolationType = ViolationType.ExpiredRule,
                        Severity = SeverityLevel.Warning,
                        Description = $"Rule '{rule.Name}' has expired",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check for conflicting rules;
                var conflicts = await FindRuleConflictsAsync(cancellationToken);
                violations.AddRange(conflicts);

                // Check for suspicious activity patterns;
                var detectedAnomalies = await DetectAnomaliesAsync(cancellationToken);
                anomalies.AddRange(detectedAnomalies);

                return new SecurityScanResult;
                {
                    Violations = violations,
                    Anomalies = anomalies,
                    ScanTime = DateTime.UtcNow,
                    TotalIssuesFound = violations.Count + anomalies.Count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan for violations");
                throw new FirewallOperationException("Failed to scan for violations", ex);
            }
        }

        /// <summary>
        /// Exports firewall rules to a file;
        /// </summary>
        public async Task<string> ExportRulesAsync(
            string filePath,
            ExportFormat format = ExportFormat.Json,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                var exporter = new FirewallRuleExporter();
                return await exporter.ExportAsync(_activeRules.Values, filePath, format, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export firewall rules");
                throw new FirewallOperationException("Failed to export firewall rules", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        /// <summary>
        /// Imports firewall rules from a file;
        /// </summary>
        public async Task<IEnumerable<FirewallRule>> ImportRulesAsync(
            string filePath,
            bool overwriteExisting = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _ruleLock.WaitAsync(cancellationToken);

                var importer = new FirewallRuleImporter();
                var importedRules = await importer.ImportAsync(filePath, cancellationToken);

                var addedRules = new List<FirewallRule>();

                foreach (var rule in importedRules)
                {
                    try
                    {
                        if (overwriteExisting && _activeRules.ContainsKey(rule.Id))
                        {
                            await UpdateRuleAsync(rule.Id, rule, false, cancellationToken);
                        }
                        else if (!_activeRules.ContainsKey(rule.Id))
                        {
                            await AddRuleAsync(rule, false, cancellationToken);
                        }

                        addedRules.Add(rule);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to import rule '{RuleName}'", rule.Name);
                    }
                }

                // Apply all imported rules if firewall is running;
                if (_isRunning && addedRules.Any())
                {
                    await ApplyRulesToSystemAsync(cancellationToken);
                }

                return addedRules;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import firewall rules");
                throw new FirewallOperationException("Failed to import firewall rules", ex);
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        #endregion;

        #region Private Methods;

        private async Task LoadRulesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Try to load from repository first;
                if (_ruleRepository != null)
                {
                    var rules = await _ruleRepository.GetAllRulesAsync(cancellationToken);
                    foreach (var rule in rules)
                    {
                        _activeRules[rule.Id] = rule;
                    }

                    _logger.LogInformation("Loaded {RuleCount} rules from repository", rules.Count());
                }

                // Load from manifest if repository is empty or not available;
                if (_activeRules.Count == 0 && _manifestManager != null)
                {
                    var manifestRules = await _manifestManager.GetFirewallRulesAsync(cancellationToken);
                    foreach (var rule in manifestRules)
                    {
                        _activeRules[rule.Id] = rule;
                    }

                    _logger.LogInformation("Loaded {RuleCount} rules from manifest", manifestRules.Count());
                }

                // Add default rules if still empty;
                if (_activeRules.Count == 0)
                {
                    await AddDefaultRulesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load firewall rules");
                throw new FirewallInitializationException("Failed to load firewall rules", ex);
            }
        }

        private async Task AddDefaultRulesAsync(CancellationToken cancellationToken)
        {
            // Default deny-all rule;
            var defaultDenyRule = new FirewallRule;
            {
                Id = "default-deny-all",
                Name = "Default Deny All",
                Description = "Default rule that denies all traffic",
                Direction = TrafficDirection.Inbound,
                Action = RuleActionType.Deny,
                Protocol = Protocol.Any,
                SourceIP = IPAddress.Any,
                DestinationIP = IPAddress.Any,
                SourcePort = PortRange.Any,
                DestinationPort = PortRange.Any,
                IsEnabled = false, // Disabled by default;
                Priority = 1000,
                CreatedBy = "System",
                CreatedDate = DateTime.UtcNow;
            };

            await AddRuleAsync(defaultDenyRule, false, cancellationToken);

            // Allow localhost traffic;
            var localhostRule = new FirewallRule;
            {
                Id = "allow-localhost",
                Name = "Allow Localhost",
                Description = "Allow traffic from localhost",
                Direction = TrafficDirection.Both,
                Action = RuleActionType.Allow,
                Protocol = Protocol.Any,
                SourceIP = IPAddress.Parse("127.0.0.1"),
                DestinationIP = IPAddress.Parse("127.0.0.1"),
                SourcePort = PortRange.Any,
                DestinationPort = PortRange.Any,
                IsEnabled = true,
                Priority = 100,
                CreatedBy = "System",
                CreatedDate = DateTime.UtcNow;
            };

            await AddRuleAsync(localhostRule, false, cancellationToken);

            _logger.LogInformation("Added default firewall rules");
        }

        private async Task ApplyRulesToSystemAsync(CancellationToken cancellationToken)
        {
            var enabledRules = _activeRules.Values.Where(r => r.IsEnabled).ToList();

            _logger.LogInformation("Applying {RuleCount} enabled rules to system", enabledRules.Count);

            foreach (var rule in enabledRules)
            {
                try
                {
                    await ApplyRuleToSystemAsync(rule, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply rule '{RuleName}' to system", rule.Name);
                }
            }
        }

        private async Task ApplyRuleToSystemAsync(FirewallRule rule, CancellationToken cancellationToken)
        {
            // This method would integrate with the operating system's firewall;
            // For Windows: netsh advfirewall firewall add rule;
            // For Linux: iptables command;
            // Implementation depends on the target platform;

            // Placeholder implementation;
            _logger.LogDebug("Applying rule '{RuleName}' to system firewall", rule.Name);

            // In a real implementation, this would call OS-specific APIs;
            await Task.Delay(10, cancellationToken); // Simulate work;

            // Update rule engine;
            await _ruleEngine.AddRuleAsync(rule, cancellationToken);
        }

        private async Task UpdateRuleInSystemAsync(
            FirewallRule oldRule,
            FirewallRule newRule,
            CancellationToken cancellationToken)
        {
            if (oldRule.IsEnabled)
            {
                await RemoveRuleFromSystemAsync(oldRule, cancellationToken);
            }

            if (newRule.IsEnabled)
            {
                await ApplyRuleToSystemAsync(newRule, cancellationToken);
            }
        }

        private async Task RemoveRuleFromSystemAsync(FirewallRule rule, CancellationToken cancellationToken)
        {
            // Placeholder implementation;
            _logger.LogDebug("Removing rule '{RuleName}' from system firewall", rule.Name);

            // In a real implementation, this would call OS-specific APIs;
            await Task.Delay(10, cancellationToken); // Simulate work;

            // Update rule engine;
            await _ruleEngine.RemoveRuleAsync(rule.Id, cancellationToken);
        }

        private async Task RemoveRulesFromSystemAsync(CancellationToken cancellationToken)
        {
            var enabledRules = _activeRules.Values.Where(r => r.IsEnabled).ToList();

            _logger.LogInformation("Removing {RuleCount} enabled rules from system", enabledRules.Count);

            foreach (var rule in enabledRules)
            {
                try
                {
                    await RemoveRuleFromSystemAsync(rule, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove rule '{RuleName}' from system", rule.Name);
                }
            }
        }

        private void ValidateRule(FirewallRule rule)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
                throw new FirewallRuleException("Rule name cannot be empty");

            if (rule.Priority < 0 || rule.Priority > 10000)
                throw new FirewallRuleException("Rule priority must be between 0 and 10000");

            if (rule.SourceIP == null)
                throw new FirewallRuleException("Source IP cannot be null");

            if (rule.DestinationIP == null)
                throw new FirewallRuleException("Destination IP cannot be null");

            // Validate IP ranges if specified;
            if (rule.SourceIPRange != null)
            {
                ValidateIPRange(rule.SourceIPRange, "Source");
            }

            if (rule.DestinationIPRange != null)
            {
                ValidateIPRange(rule.DestinationIPRange, "Destination");
            }
        }

        private void ValidateIPRange(IPAddressRange range, string fieldName)
        {
            if (range.StartAddress == null)
                throw new FirewallRuleException($"{fieldName} IP range start address cannot be null");

            if (range.EndAddress == null)
                throw new FirewallRuleException($"{fieldName} IP range end address cannot be null");

            if (range.StartAddress.AddressFamily != range.EndAddress.AddressFamily)
                throw new FirewallRuleException($"{fieldName} IP range addresses must be of the same address family");
        }

        private IEnumerable<FirewallRule> ApplyFilter(IEnumerable<FirewallRule> rules, RuleFilter filter)
        {
            var query = rules;

            if (filter.EnabledOnly)
            {
                query = query.Where(r => r.IsEnabled);
            }

            if (!string.IsNullOrEmpty(filter.Direction))
            {
                if (Enum.TryParse<TrafficDirection>(filter.Direction, true, out var direction))
                {
                    query = query.Where(r => r.Direction == direction);
                }
            }

            if (!string.IsNullOrEmpty(filter.Action))
            {
                if (Enum.TryParse<RuleActionType>(filter.Action, true, out var action))
                {
                    query = query.Where(r => r.Action == action);
                }
            }

            if (!string.IsNullOrEmpty(filter.Protocol))
            {
                if (Enum.TryParse<Protocol>(filter.Protocol, true, out var protocol))
                {
                    query = query.Where(r => r.Protocol == protocol);
                }
            }

            if (!string.IsNullOrEmpty(filter.SearchText))
            {
                var search = filter.SearchText.ToLowerInvariant();
                query = query.Where(r =>
                    r.Name.ToLowerInvariant().Contains(search) ||
                    r.Description.ToLowerInvariant().Contains(search) ||
                    r.Tags.Any(t => t.ToLowerInvariant().Contains(search)));
            }

            if (filter.CreatedAfter.HasValue)
            {
                query = query.Where(r => r.CreatedDate >= filter.CreatedAfter.Value);
            }

            if (filter.CreatedBefore.HasValue)
            {
                query = query.Where(r => r.CreatedDate <= filter.CreatedBefore.Value);
            }

            // Apply ordering;
            query = filter.SortOrder == SortOrder.Ascending;
                ? query.OrderBy(r => r.Priority).ThenBy(r => r.Name)
                : query.OrderByDescending(r => r.Priority).ThenBy(r => r.Name);

            return query;
        }

        private async Task LogTrafficDecisionAsync(
            NetworkTraffic traffic,
            TrafficDecision decision,
            CancellationToken cancellationToken)
        {
            try
            {
                var logEntry = new TrafficLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    SourceIP = traffic.SourceIP,
                    DestinationIP = traffic.DestinationIP,
                    SourcePort = traffic.SourcePort,
                    DestinationPort = traffic.DestinationPort,
                    Protocol = traffic.Protocol,
                    Decision = decision,
                    Direction = traffic.Direction,
                    BytesTransferred = traffic.BytesTransferred,
                    ConnectionDuration = traffic.ConnectionDuration;
                };

                await _auditLogger.LogTrafficAsync(logEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log traffic decision");
            }
        }

        private async Task CheckSuspiciousActivityAsync(
            NetworkTraffic traffic,
            CancellationToken cancellationToken)
        {
            try
            {
                // Check for multiple blocked attempts from same source;
                var recentBlocks = await _auditLogger.GetRecentBlocksAsync(
                    traffic.SourceIP,
                    TimeSpan.FromMinutes(5),
                    cancellationToken);

                if (recentBlocks >= _configuration.SuspiciousActivityThreshold)
                {
                    var eventArgs = new SuspiciousActivityEventArgs;
                    {
                        SourceIP = traffic.SourceIP,
                        ActivityType = SuspiciousActivityType.MultipleBlocks,
                        BlockCount = recentBlocks,
                        Timestamp = DateTime.UtcNow,
                        Details = $"Multiple blocked attempts from {traffic.SourceIP} in last 5 minutes"
                    };

                    OnSuspiciousActivityDetected(eventArgs);

                    // Log to security monitor;
                    await _securityMonitor.LogSuspiciousActivityAsync(eventArgs, cancellationToken);

                    // Optionally, create a temporary block rule;
                    if (_configuration.AutoBlockSuspiciousIPs)
                    {
                        await CreateTemporaryBlockRuleAsync(traffic.SourceIP, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for suspicious activity");
            }
        }

        private async Task CreateTemporaryBlockRuleAsync(
            IPAddress sourceIP,
            CancellationToken cancellationToken)
        {
            var tempRule = new FirewallRule;
            {
                Id = $"temp-block-{sourceIP}-{Guid.NewGuid():N}",
                Name = $"Temporary Block for {sourceIP}",
                Description = $"Automatically created block rule for suspicious activity from {sourceIP}",
                Direction = TrafficDirection.Inbound,
                Action = RuleActionType.Deny,
                Protocol = Protocol.Any,
                SourceIP = sourceIP,
                DestinationIP = IPAddress.Any,
                SourcePort = PortRange.Any,
                DestinationPort = PortRange.Any,
                IsEnabled = true,
                Priority = 10, // High priority;
                IsTemporary = true,
                ExpirationDate = DateTime.UtcNow.AddHours(1),
                CreatedBy = "System",
                CreatedDate = DateTime.UtcNow,
                Tags = new List<string> { "auto-generated", "suspicious-activity" }
            };

            await AddRuleAsync(tempRule, true, cancellationToken);

            _logger.LogWarning("Created temporary block rule for suspicious IP: {SourceIP}", sourceIP);
        }

        private async Task<List<RuleViolation>> FindRuleConflictsAsync(CancellationToken cancellationToken)
        {
            var violations = new List<RuleViolation>();
            var rules = _activeRules.Values.Where(r => r.IsEnabled).ToList();

            for (int i = 0; i < rules.Count; i++)
            {
                for (int j = i + 1; j < rules.Count; j++)
                {
                    var rule1 = rules[i];
                    var rule2 = rules[j];

                    if (AreRulesConflicting(rule1, rule2))
                    {
                        violations.Add(new RuleViolation;
                        {
                            RuleId = rule1.Id,
                            RuleName = rule1.Name,
                            ViolationType = ViolationType.ConflictingRule,
                            Severity = SeverityLevel.Warning,
                            Description = $"Rule '{rule1.Name}' conflicts with rule '{rule2.Name}'",
                            Timestamp = DateTime.UtcNow,
                            RelatedRuleId = rule2.Id;
                        });
                    }
                }
            }

            return violations;
        }

        private bool AreRulesConflicting(FirewallRule rule1, FirewallRule rule2)
        {
            // Rules with opposite actions that match the same traffic are conflicting;
            if (rule1.Action == rule2.Action)
                return false; // Same action, not conflicting;

            // Check if rules apply to same direction;
            if (rule1.Direction != rule2.Direction &&
                rule1.Direction != TrafficDirection.Both &&
                rule2.Direction != TrafficDirection.Both)
                return false;

            // Check protocol match;
            if (rule1.Protocol != rule2.Protocol &&
                rule1.Protocol != Protocol.Any &&
                rule2.Protocol != Protocol.Any)
                return false;

            // Check IP match (simplified - in reality would need range checking)
            if (!IPAddressEquals(rule1.SourceIP, rule2.SourceIP) &&
                !rule1.SourceIP.Equals(IPAddress.Any) &&
                !rule2.SourceIP.Equals(IPAddress.Any))
                return false;

            // Check port match;
            if (!PortRangeOverlaps(rule1.SourcePort, rule2.SourcePort))
                return false;

            return true;
        }

        private bool IPAddressEquals(IPAddress ip1, IPAddress ip2)
        {
            if (ip1 == null || ip2 == null)
                return false;

            return ip1.Equals(ip2);
        }

        private bool PortRangeOverlaps(PortRange range1, PortRange range2)
        {
            if (range1 == null || range2 == null)
                return false;

            if (range1.IsAny || range2.IsAny)
                return true;

            return !(range1.EndPort < range2.StartPort || range2.EndPort < range1.StartPort);
        }

        private async Task<List<NetworkAnomaly>> DetectAnomaliesAsync(CancellationToken cancellationToken)
        {
            var anomalies = new List<NetworkAnomaly>();

            try
            {
                // Analyze traffic patterns;
                var recentTraffic = await _auditLogger.GetRecentTrafficAsync(
                    TimeSpan.FromMinutes(15),
                    cancellationToken);

                if (recentTraffic.Any())
                {
                    // Check for unusual traffic spikes;
                    var trafficByMinute = recentTraffic;
                        .GroupBy(t => t.Timestamp.Minute)
                        .Select(g => new { Minute = g.Key, Count = g.Count() })
                        .ToList();

                    if (trafficByMinute.Count > 1)
                    {
                        var avgTraffic = trafficByMinute.Average(x => x.Count);
                        var maxTraffic = trafficByMinute.Max(x => x.Count);

                        if (maxTraffic > avgTraffic * _configuration.AnomalyThresholdMultiplier)
                        {
                            anomalies.Add(new NetworkAnomaly;
                            {
                                Type = AnomalyType.TrafficSpike,
                                Severity = SeverityLevel.High,
                                Description = $"Unusual traffic spike detected: {maxTraffic} packets/min (average: {avgTraffic:F1})",
                                Timestamp = DateTime.UtcNow,
                                Details = $"Traffic spike detected at {DateTime.UtcNow:HH:mm:ss}"
                            });
                        }
                    }

                    // Check for port scanning activity;
                    var uniquePortsBySource = recentTraffic;
                        .Where(t => t.Decision == TrafficDecision.Block)
                        .GroupBy(t => t.SourceIP)
                        .Select(g => new;
                        {
                            SourceIP = g.Key,
                            UniquePorts = g.Select(t => t.DestinationPort).Distinct().Count(),
                            TotalAttempts = g.Count()
                        })
                        .Where(x => x.UniquePorts > _configuration.PortScanThreshold)
                        .ToList();

                    foreach (var scan in uniquePortsBySource)
                    {
                        anomalies.Add(new NetworkAnomaly;
                        {
                            Type = AnomalyType.PortScan,
                            Severity = SeverityLevel.Critical,
                            Description = $"Possible port scan detected from {scan.SourceIP}",
                            Timestamp = DateTime.UtcNow,
                            Details = $"{scan.UniquePorts} unique ports scanned in {scan.TotalAttempts} attempts",
                            SourceIP = scan.SourceIP;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to detect anomalies");
            }

            return anomalies;
        }

        private TrafficDecision CombineDecisions(RuleEvaluationResult ruleResult, AccessDecision accessDecision)
        {
            // Rule engine decision takes precedence;
            if (ruleResult.Decision == TrafficDecision.Block)
                return TrafficDecision.Block;

            // Then check access control;
            if (accessDecision == AccessDecision.Deny)
                return TrafficDecision.Block;

            return TrafficDecision.Allow;
        }

        private void MonitoringCallback(object state)
        {
            try
            {
                // Update active connections;
                UpdateActiveConnections();

                // Check for expired rules;
                CheckExpiredRules();

                // Log statistics periodically;
                LogStatistics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in firewall monitoring callback");
            }
        }

        private void UpdateActiveConnections()
        {
            try
            {
                // In a real implementation, this would query the system for active connections;
                // Placeholder implementation;

                _activeConnections.Clear();

                // Simulate some connections;
                if (_isRunning)
                {
                    var random = new Random();
                    int connectionCount = random.Next(5, 20);

                    for (int i = 0; i < connectionCount; i++)
                    {
                        _activeConnections.Add(new NetworkConnection;
                        {
                            Id = Guid.NewGuid().ToString(),
                            SourceIP = IPAddress.Parse($"192.168.1.{random.Next(1, 255)}"),
                            DestinationIP = IPAddress.Parse($"10.0.0.{random.Next(1, 255)}"),
                            SourcePort = random.Next(1024, 65535),
                            DestinationPort = random.Next(1, 1024),
                            Protocol = random.Next(0, 2) == 0 ? Protocol.TCP : Protocol.UDP,
                            State = ConnectionState.Established,
                            StartTime = DateTime.UtcNow.AddMinutes(-random.Next(1, 60)),
                            BytesSent = random.Next(1024, 1048576),
                            BytesReceived = random.Next(1024, 1048576)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update active connections");
            }
        }

        private async void CheckExpiredRules()
        {
            try
            {
                var expiredRules = _activeRules.Values;
                    .Where(r => r.ExpirationDate.HasValue &&
                           r.ExpirationDate.Value < DateTime.UtcNow &&
                           r.IsEnabled)
                    .ToList();

                foreach (var rule in expiredRules)
                {
                    _logger.LogInformation("Rule '{RuleName}' has expired, disabling", rule.Name);

                    await ToggleRuleAsync(rule.Id, false, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check expired rules");
            }
        }

        private void LogStatistics()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Firewall stats: Rules={RuleCount}, Packets={PacketCount}, Blocks={BlockCount}, Allows={AllowCount}",
                    _activeRules.Count, _totalPacketsProcessed, _totalBlocks, _totalAllows);
            }
        }

        private async void RuleSyncCallback(object state)
        {
            try
            {
                await _ruleLock.WaitAsync();

                if (_ruleRepository != null)
                {
                    var repositoryRules = await _ruleRepository.GetAllRulesAsync(CancellationToken.None);

                    // Synchronize rules between memory and repository;
                    await SynchronizeRulesAsync(repositoryRules, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in rule synchronization callback");
            }
            finally
            {
                _ruleLock.Release();
            }
        }

        private async Task SynchronizeRulesAsync(
            IEnumerable<FirewallRule> repositoryRules,
            CancellationToken cancellationToken)
        {
            var repoDict = repositoryRules.ToDictionary(r => r.Id, r => r);
            var syncLog = new List<string>();

            // Add new rules from repository;
            foreach (var repoRule in repositoryRules)
            {
                if (!_activeRules.ContainsKey(repoRule.Id))
                {
                    _activeRules[repoRule.Id] = repoRule;
                    syncLog.Add($"Added rule '{repoRule.Name}' from repository");
                }
                else if (_activeRules[repoRule.Id].ModifiedDate < repoRule.ModifiedDate)
                {
                    // Update if repository has newer version;
                    var oldRule = _activeRules[repoRule.Id];
                    _activeRules[repoRule.Id] = repoRule;

                    // Re-apply if rule was enabled;
                    if (oldRule.IsEnabled && _isRunning)
                    {
                        await UpdateRuleInSystemAsync(oldRule, repoRule, cancellationToken);
                    }

                    syncLog.Add($"Updated rule '{repoRule.Name}' from repository");
                }
            }

            // Remove rules not in repository (if they're marked as syncable)
            var rulesToRemove = _activeRules.Values;
                .Where(r => !repoDict.ContainsKey(r.Id) && r.IsSyncable)
                .ToList();

            foreach (var rule in rulesToRemove)
            {
                _activeRules.Remove(rule.Id);
                syncLog.Add($"Removed rule '{rule.Name}' not found in repository");
            }

            if (syncLog.Any())
            {
                _logger.LogInformation("Rule synchronization completed: {SyncCount} changes", syncLog.Count);

                foreach (var log in syncLog)
                {
                    _logger.LogDebug("Sync: {LogMessage}", log);
                }
            }
        }

        private void StartMonitoring()
        {
            // Start background monitoring tasks;
            _monitoringTimer.Change(0, _configuration.MonitoringInterval);

            if (_configuration.EnableRuleSync)
            {
                _ruleSyncTimer.Change(_configuration.RuleSyncInterval, _configuration.RuleSyncInterval);
            }

            _logger.LogInformation("Firewall monitoring started");
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnRuleChanged(FirewallRuleEventArgs e)
        {
            RuleChanged?.Invoke(this, e);
        }

        protected virtual void OnTrafficBlocked(TrafficBlockedEventArgs e)
        {
            TrafficBlocked?.Invoke(this, e);
        }

        protected virtual void OnSuspiciousActivityDetected(SuspiciousActivityEventArgs e)
        {
            SuspiciousActivityDetected?.Invoke(this, e);
        }

        protected virtual void OnStatusChanged(FirewallStatusEventArgs e)
        {
            StatusChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Stop protection if running;
                    if (_isRunning)
                    {
                        try
                        {
                            StopProtectionAsync().GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error stopping protection during disposal");
                        }
                    }

                    // Dispose timers;
                    _monitoringTimer?.Dispose();
                    _ruleSyncTimer?.Dispose();

                    // Dispose semaphore;
                    _ruleLock?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Firewall configuration settings;
        /// </summary>
        public class FirewallConfiguration;
        {
            public int MonitoringInterval { get; set; } = 5000; // 5 seconds;
            public bool EnableRuleSync { get; set; } = true;
            public int RuleSyncInterval { get; set; } = 300000; // 5 minutes;
            public int SuspiciousActivityThreshold { get; set; } = 10;
            public bool AutoBlockSuspiciousIPs { get; set; } = true;
            public int PortScanThreshold { get; set; } = 20;
            public double AnomalyThresholdMultiplier { get; set; } = 3.0;
            public LogLevel LogLevel { get; set; } = LogLevel.Information;
        }

        /// <summary>
        /// Rule filter for querying firewall rules;
        /// </summary>
        public class RuleFilter;
        {
            public bool EnabledOnly { get; set; }
            public string Direction { get; set; }
            public string Action { get; set; }
            public string Protocol { get; set; }
            public string SearchText { get; set; }
            public DateTime? CreatedAfter { get; set; }
            public DateTime? CreatedBefore { get; set; }
            public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
        }

        /// <summary>
        /// Firewall statistics;
        /// </summary>
        public class FirewallStatistics;
        {
            public int TotalRules { get; set; }
            public int EnabledRules { get; set; }
            public int ActiveConnections { get; set; }
            public int TotalPacketsProcessed { get; set; }
            public int TotalBlocks { get; set; }
            public int TotalAllows { get; set; }
            public bool IsRunning { get; set; }
            public bool IsInitialized { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IFirewallManager;
    {
        Task InitializeAsync(FirewallConfiguration configuration = null, CancellationToken cancellationToken = default);
        Task StartProtectionAsync(CancellationToken cancellationToken = default);
        Task StopProtectionAsync(CancellationToken cancellationToken = default);
        Task<TrafficDecision> ProcessTrafficAsync(NetworkTraffic traffic, CancellationToken cancellationToken = default);

        Task<FirewallRule> AddRuleAsync(FirewallRule rule, bool applyImmediately = true, CancellationToken cancellationToken = default);
        Task<FirewallRule> UpdateRuleAsync(string ruleId, FirewallRule rule, bool applyImmediately = true, CancellationToken cancellationToken = default);
        Task RemoveRuleAsync(string ruleId, bool removeFromSystem = true, CancellationToken cancellationToken = default);
        Task<IEnumerable<FirewallRule>> GetRulesAsync(RuleFilter filter = null, CancellationToken cancellationToken = default);
        Task<FirewallRule> GetRuleAsync(string ruleId, CancellationToken cancellationToken = default);
        Task ToggleRuleAsync(string ruleId, bool enabled, CancellationToken cancellationToken = default);

        Task<FirewallStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<NetworkConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default);
        Task<SecurityScanResult> ScanForViolationsAsync(CancellationToken cancellationToken = default);
        Task<string> ExportRulesAsync(string filePath, ExportFormat format = ExportFormat.Json, CancellationToken cancellationToken = default);
        Task<IEnumerable<FirewallRule>> ImportRulesAsync(string filePath, bool overwriteExisting = false, CancellationToken cancellationToken = default);

        event EventHandler<FirewallRuleEventArgs> RuleChanged;
        event EventHandler<TrafficBlockedEventArgs> TrafficBlocked;
        event EventHandler<SuspiciousActivityEventArgs> SuspiciousActivityDetected;
        event EventHandler<FirewallStatusEventArgs> StatusChanged;
    }

    public enum TrafficDecision;
    {
        Allow,
        Block,
        Challenge // For future use (CAPTCHA, MFA, etc.)
    }

    public enum FirewallStatus;
    {
        Stopped,
        Initializing,
        Initialized,
        Running,
        Error;
    }

    public enum RuleAction;
    {
        Added,
        Updated,
        Removed;
    }

    public enum SortOrder;
    {
        Ascending,
        Descending;
    }

    public enum ExportFormat;
    {
        Json,
        Xml,
        Csv;
    }

    public enum ViolationType;
    {
        ExpiredRule,
        ConflictingRule,
        InvalidRule,
        OverlyPermissiveRule;
    }

    public enum SeverityLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum AnomalyType;
    {
        TrafficSpike,
        PortScan,
        DDoSAttempt,
        UnusualProtocol,
        GeographicAnomaly;
    }

    public enum SuspiciousActivityType;
    {
        MultipleBlocks,
        RapidConnectionAttempts,
        UnusualPortAccess,
        MalformedPackets;
    }

    public class FirewallRuleEventArgs : EventArgs;
    {
        public RuleAction Action { get; set; }
        public FirewallRule Rule { get; set; }
        public FirewallRule OldRule { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TrafficBlockedEventArgs : EventArgs;
    {
        public NetworkTraffic Traffic { get; set; }
        public string BlockReason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SuspiciousActivityEventArgs : EventArgs;
    {
        public IPAddress SourceIP { get; set; }
        public SuspiciousActivityType ActivityType { get; set; }
        public int BlockCount { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
    }

    public class FirewallStatusEventArgs : EventArgs;
    {
        public FirewallStatus Status { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class RuleViolation;
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public ViolationType ViolationType { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public string RelatedRuleId { get; set; }
    }

    public class NetworkAnomaly;
    {
        public AnomalyType Type { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
        public IPAddress SourceIP { get; set; }
    }

    public class SecurityScanResult;
    {
        public List<RuleViolation> Violations { get; set; } = new List<RuleViolation>();
        public List<NetworkAnomaly> Anomalies { get; set; } = new List<NetworkAnomaly>();
        public DateTime ScanTime { get; set; }
        public int TotalIssuesFound { get; set; }
    }

    // Custom exceptions for firewall operations;
    public class FirewallInitializationException : Exception
    {
        public FirewallInitializationException() { }
        public FirewallInitializationException(string message) : base(message) { }
        public FirewallInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class FirewallOperationException : Exception
    {
        public FirewallOperationException() { }
        public FirewallOperationException(string message) : base(message) { }
        public FirewallOperationException(string message, Exception inner) : base(message, inner) { }
    }

    public class FirewallRuleException : Exception
    {
        public FirewallRuleException() { }
        public FirewallRuleException(string message) : base(message) { }
        public FirewallRuleException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
