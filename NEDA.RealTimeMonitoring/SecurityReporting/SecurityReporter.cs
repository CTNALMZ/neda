using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Cloud;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.SecurityModules.Monitoring;
using NEDA.Services.Messaging;
using NEDA.Services.NotificationService;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.PersonalAssistant.DailyPlanner.TaskScheduler;

namespace NEDA.RealTimeMonitoring.SecurityReporting;
{
    /// <summary>
    /// Security incident reporter with multi-channel notification support;
    /// Implements real-time security event reporting with severity-based routing;
    /// </summary>
    public interface ISecurityReporter : IDisposable
    {
        /// <summary>
        /// Report security incident with detailed information;
        /// </summary>
        Task<ReportResult> ReportIncidentAsync(SecurityIncident incident, CancellationToken cancellationToken = default);

        /// <summary>
        /// Generate comprehensive security report for specified time period;
        /// </summary>
        Task<SecurityReport> GeneratePeriodicReportAsync(DateTime startTime, DateTime endTime, ReportFormat format = ReportFormat.PDF);

        /// <summary>
        /// Get real-time security dashboard statistics;
        /// </summary>
        Task<SecurityDashboardStats> GetDashboardStatsAsync();

        /// <summary>
        /// Configure notification channels for specific incident types;
        /// </summary>
        Task ConfigureNotificationChannelAsync(NotificationChannelConfig config);

        /// <summary>
        /// Acknowledge security incident by authorized personnel;
        /// </summary>
        Task<bool> AcknowledgeIncidentAsync(Guid incidentId, string acknowledgedBy, string notes = null);

        /// <summary>
        /// Get unresolved security incidents;
        /// </summary>
        Task<IEnumerable<SecurityIncident>> GetUnresolvedIncidentsAsync(TimeSpan? timeframe = null);

        /// <summary>
        /// Escalate incident to higher severity level;
        /// </summary>
        Task<bool> EscalateIncidentAsync(Guid incidentId, SeverityLevel newSeverity, string reason);
    }

    /// <summary>
    /// Main security reporter implementation;
    /// </summary>
    public class SecurityReporter : ISecurityReporter;
    {
        private readonly ILogger<SecurityReporter> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IThreatDetector _threatDetector;
        private readonly INotificationManager _notificationManager;
        private readonly IEventBus _eventBus;
        private readonly IReportGenerator _reportGenerator;
        private readonly ReportConfig _config;
        private readonly SecurityReportRepository _repository;
        private readonly Timer _periodicReportTimer;
        private readonly SemaphoreSlim _reportLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // Incident handlers by severity;
        private readonly Dictionary<SeverityLevel, List<IIncidentHandler>> _incidentHandlers;

        // Notification channels;
        private readonly Dictionary<string, INotificationChannel> _notificationChannels;

        public SecurityReporter(
            ILogger<SecurityReporter> logger,
            IAuditLogger auditLogger,
            ISecurityMonitor securityMonitor,
            IThreatDetector threatDetector,
            INotificationManager notificationManager,
            IEventBus eventBus,
            IReportGenerator reportGenerator,
            IOptions<ReportConfig> configOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _threatDetector = threatDetector ?? throw new ArgumentNullException(nameof(threatDetector));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));

            _repository = new SecurityReportRepository();
            _incidentHandlers = new Dictionary<SeverityLevel, List<IIncidentHandler>>();
            _notificationChannels = new Dictionary<string, INotificationChannel>();

            InitializeIncidentHandlers();
            InitializeNotificationChannels();

            // Setup periodic report generation;
            if (_config.EnablePeriodicReports)
            {
                _periodicReportTimer = new Timer(
                    GenerateScheduledReportsAsync,
                    null,
                    TimeSpan.FromMinutes(_config.ReportIntervalMinutes),
                    TimeSpan.FromMinutes(_config.ReportIntervalMinutes));
            }

            _logger.LogInformation("SecurityReporter initialized with {HandlerCount} incident handlers",
                _incidentHandlers.Values.Sum(h => h.Count));
        }

        private void InitializeIncidentHandlers()
        {
            // Critical incidents - immediate action required;
            _incidentHandlers[SeverityLevel.Critical] = new List<IIncidentHandler>
            {
                new CriticalIncidentHandler(_notificationManager, _eventBus),
                new EmergencyAlertHandler(_notificationManager),
                new SystemLockdownHandler(_securityMonitor)
            };

            // High severity incidents;
            _incidentHandlers[SeverityLevel.High] = new List<IIncidentHandler>
            {
                new HighSeverityHandler(_notificationManager),
                new SecurityTeamNotificationHandler(),
                new IncidentLogHandler(_auditLogger)
            };

            // Medium severity incidents;
            _incidentHandlers[SeverityLevel.Medium] = new List<IIncidentHandler>
            {
                new IncidentLogHandler(_auditLogger),
                new ManagerNotificationHandler(_notificationManager)
            };

            // Low severity incidents;
            _incidentHandlers[SeverityLevel.Low] = new List<IIncidentHandler>
            {
                new IncidentLogHandler(_auditLogger)
            };
        }

        private void InitializeNotificationChannels()
        {
            // Initialize built-in notification channels;
            _notificationChannels["email"] = new EmailNotificationChannel();
            _notificationChannels["sms"] = new SMSNotificationChannel();
            _notificationChannels["slack"] = new SlackNotificationChannel();
            _notificationChannels["teams"] = new MicrosoftTeamsChannel();
            _notificationChannels["pagerduty"] = new PagerDutyChannel();

            // Load custom channels from configuration;
            foreach (var channelConfig in _config.NotificationChannels)
            {
                try
                {
                    var channelType = Type.GetType(channelConfig.ChannelType);
                    if (channelType != null && typeof(INotificationChannel).IsAssignableFrom(channelType))
                    {
                        var channel = Activator.CreateInstance(channelType, channelConfig.Parameters) as INotificationChannel;
                        if (channel != null)
                        {
                            _notificationChannels[channelConfig.ChannelName] = channel;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize notification channel: {ChannelName}",
                        channelConfig.ChannelName);
                }
            }
        }

        public async Task<ReportResult> ReportIncidentAsync(
            SecurityIncident incident,
            CancellationToken cancellationToken = default)
        {
            if (incident == null)
                throw new ArgumentNullException(nameof(incident));

            await _reportLock.WaitAsync(cancellationToken);
            try
            {
                // Validate incident;
                var validationResult = ValidateIncident(incident);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Incident validation failed: {Errors}",
                        string.Join(", ", validationResult.Errors));

                    return ReportResult.Failed(validationResult.Errors);
                }

                // Generate unique incident ID if not provided;
                if (incident.Id == Guid.Empty)
                    incident.Id = Guid.NewGuid();

                incident.ReportedAt = DateTime.UtcNow;
                incident.Status = IncidentStatus.Reported;

                // Log to audit system;
                await _auditLogger.LogSecurityIncidentAsync(incident);

                // Store incident;
                await _repository.AddIncidentAsync(incident);

                // Process based on severity;
                await ProcessIncidentBySeverityAsync(incident);

                // Publish event for other services;
                await _eventBus.PublishAsync(new SecurityIncidentReportedEvent;
                {
                    IncidentId = incident.Id,
                    Severity = incident.Severity,
                    IncidentType = incident.IncidentType,
                    Source = incident.Source,
                    Timestamp = incident.ReportedAt;
                });

                // Notify configured recipients;
                await NotifyRecipientsAsync(incident);

                _logger.LogInformation("Security incident reported: {IncidentId}, Type: {Type}, Severity: {Severity}",
                    incident.Id, incident.IncidentType, incident.Severity);

                return ReportResult.Success(incident.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to report security incident: {IncidentType}", incident.IncidentType);

                // Fallback emergency notification;
                await SendEmergencyNotificationAsync(incident, ex);

                throw new SecurityReportingException(
                    $"Failed to report security incident: {incident.IncidentType}", ex);
            }
            finally
            {
                _reportLock.Release();
            }
        }

        public async Task<SecurityReport> GeneratePeriodicReportAsync(
            DateTime startTime,
            DateTime endTime,
            ReportFormat format = ReportFormat.PDF)
        {
            if (endTime <= startTime)
                throw new ArgumentException("End time must be after start time");

            try
            {
                // Gather incident data;
                var incidents = await _repository.GetIncidentsInTimeRangeAsync(startTime, endTime);

                // Get threat statistics;
                var threatStats = await _threatDetector.GetThreatStatisticsAsync(startTime, endTime);

                // Get system security status;
                var securityStatus = await _securityMonitor.GetSecurityStatusAsync();

                // Generate report;
                var reportData = new SecurityReportData;
                {
                    PeriodStart = startTime,
                    PeriodEnd = endTime,
                    TotalIncidents = incidents.Count(),
                    IncidentsBySeverity = incidents.GroupBy(i => i.Severity)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    IncidentsByType = incidents.GroupBy(i => i.IncidentType)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    ThreatStatistics = threatStats,
                    SecurityStatus = securityStatus,
                    ResponseTimes = await CalculateAverageResponseTimesAsync(incidents),
                    Recommendations = await GenerateRecommendationsAsync(incidents, threatStats)
                };

                // Generate formatted report;
                var report = await _reportGenerator.GenerateReportAsync(
                    reportData,
                    format,
                    "Security_Report_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));

                // Store report metadata;
                await _repository.SaveReportAsync(report);

                // Send to configured recipients if enabled;
                if (_config.AutoSendPeriodicReports)
                {
                    await DistributeReportAsync(report);
                }

                _logger.LogInformation("Generated periodic security report for {StartTime} to {EndTime}",
                    startTime, endTime);

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate periodic security report");
                throw new SecurityReportingException("Failed to generate periodic report", ex);
            }
        }

        public async Task<SecurityDashboardStats> GetDashboardStatsAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var last24Hours = now.AddHours(-24);
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var incidents24h = await _repository.GetIncidentsInTimeRangeAsync(last24Hours, now);
                var incidents7d = await _repository.GetIncidentsInTimeRangeAsync(last7Days, now);
                var incidents30d = await _repository.GetIncidentsInTimeRangeAsync(last30Days, now);

                var activeThreats = await _threatDetector.GetActiveThreatsCountAsync();
                var unresolvedIncidents = await GetUnresolvedIncidentsAsync(TimeSpan.FromHours(24));

                var stats = new SecurityDashboardStats;
                {
                    IncidentsLast24h = incidents24h.Count(),
                    IncidentsLast7d = incidents7d.Count(),
                    IncidentsLast30d = incidents30d.Count(),
                    ActiveThreats = activeThreats,
                    UnresolvedIncidents = unresolvedIncidents.Count(),
                    AverageResponseTime = await CalculateAverageResponseTimeAsync(incidents24h),
                    TopIncidentTypes = incidents24h;
                        .GroupBy(i => i.IncidentType)
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    SystemSecurityScore = await CalculateSecurityScoreAsync(),
                    LastIncidentTime = incidents24h.Max(i => i.ReportedAt) as DateTime?,
                    AlertStatus = await GetSystemAlertStatusAsync()
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get dashboard statistics");
                throw new SecurityReportingException("Failed to get dashboard stats", ex);
            }
        }

        public async Task ConfigureNotificationChannelAsync(NotificationChannelConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            try
            {
                // Validate channel configuration;
                var validator = new NotificationChannelValidator();
                var validationResult = validator.Validate(config);

                if (!validationResult.IsValid)
                {
                    throw new ConfigurationException(
                        $"Invalid notification channel configuration: {string.Join(", ", validationResult.Errors)}");
                }

                // Create channel instance;
                var channel = NotificationChannelFactory.CreateChannel(config);

                // Test connection;
                var testResult = await channel.TestConnectionAsync();

                if (!testResult.Success)
                {
                    throw new ConfigurationException(
                        $"Failed to test notification channel: {testResult.ErrorMessage}");
                }

                // Add or update channel;
                _notificationChannels[config.ChannelName] = channel;

                // Update configuration;
                _config.NotificationChannels.RemoveAll(c => c.ChannelName == config.ChannelName);
                _config.NotificationChannels.Add(config);

                _logger.LogInformation("Configured notification channel: {ChannelName}", config.ChannelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure notification channel: {ChannelName}", config.ChannelName);
                throw new SecurityReportingException("Failed to configure notification channel", ex);
            }
        }

        public async Task<bool> AcknowledgeIncidentAsync(
            Guid incidentId,
            string acknowledgedBy,
            string notes = null)
        {
            if (string.IsNullOrWhiteSpace(acknowledgedBy))
                throw new ArgumentException("Acknowledged by cannot be empty", nameof(acknowledgedBy));

            try
            {
                var incident = await _repository.GetIncidentAsync(incidentId);

                if (incident == null)
                {
                    _logger.LogWarning("Incident not found: {IncidentId}", incidentId);
                    return false;
                }

                incident.AcknowledgedAt = DateTime.UtcNow;
                incident.AcknowledgedBy = acknowledgedBy;
                incident.AcknowledgmentNotes = notes;
                incident.Status = IncidentStatus.Acknowledged;

                await _repository.UpdateIncidentAsync(incident);

                // Log acknowledgment;
                await _auditLogger.LogIncidentAcknowledgmentAsync(incidentId, acknowledgedBy, notes);

                // Notify about acknowledgment;
                await NotifyIncidentUpdateAsync(incident, "Acknowledged");

                _logger.LogInformation("Incident acknowledged: {IncidentId} by {User}",
                    incidentId, acknowledgedBy);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge incident: {IncidentId}", incidentId);
                return false;
            }
        }

        public async Task<IEnumerable<SecurityIncident>> GetUnresolvedIncidentsAsync(TimeSpan? timeframe = null)
        {
            try
            {
                var cutoffTime = timeframe.HasValue;
                    ? DateTime.UtcNow.Subtract(timeframe.Value)
                    : DateTime.MinValue;

                return await _repository.GetUnresolvedIncidentsAsync(cutoffTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get unresolved incidents");
                throw new SecurityReportingException("Failed to get unresolved incidents", ex);
            }
        }

        public async Task<bool> EscalateIncidentAsync(
            Guid incidentId,
            SeverityLevel newSeverity,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Escalation reason cannot be empty", nameof(reason));

            try
            {
                var incident = await _repository.GetIncidentAsync(incidentId);

                if (incident == null)
                {
                    _logger.LogWarning("Incident not found for escalation: {IncidentId}", incidentId);
                    return false;
                }

                var oldSeverity = incident.Severity;

                if (newSeverity <= oldSeverity)
                {
                    _logger.LogWarning("Cannot escalate to same or lower severity: {IncidentId}", incidentId);
                    return false;
                }

                incident.Severity = newSeverity;
                incident.EscalationHistory.Add(new EscalationRecord;
                {
                    Timestamp = DateTime.UtcNow,
                    FromSeverity = oldSeverity,
                    ToSeverity = newSeverity,
                    Reason = reason,
                    EscalatedBy = "System" // In real implementation, this would be the user;
                });

                await _repository.UpdateIncidentAsync(incident);

                // Process with higher severity handlers;
                await ProcessIncidentBySeverityAsync(incident);

                // Log escalation;
                await _auditLogger.LogIncidentEscalationAsync(incidentId, oldSeverity, newSeverity, reason);

                // Notify about escalation;
                await NotifyIncidentUpdateAsync(incident, $"Escalated to {newSeverity}");

                _logger.LogInformation("Incident escalated: {IncidentId} from {OldSeverity} to {NewSeverity}",
                    incidentId, oldSeverity, newSeverity);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to escalate incident: {IncidentId}", incidentId);
                return false;
            }
        }

        private async Task ProcessIncidentBySeverityAsync(SecurityIncident incident)
        {
            if (!_incidentHandlers.ContainsKey(incident.Severity))
            {
                _logger.LogWarning("No handlers configured for severity level: {Severity}", incident.Severity);
                return;
            }

            var handlers = _incidentHandlers[incident.Severity];

            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleIncidentAsync(incident);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Handler {HandlerType} failed for incident {IncidentId}",
                        handler.GetType().Name, incident.Id);
                }
            }
        }

        private async Task NotifyRecipientsAsync(SecurityIncident incident)
        {
            var notificationTasks = new List<Task>();

            // Get recipients for this incident type and severity;
            var recipients = GetRecipientsForIncident(incident);

            foreach (var recipient in recipients)
            {
                foreach (var channelName in recipient.NotificationChannels)
                {
                    if (_notificationChannels.TryGetValue(channelName, out var channel))
                    {
                        var message = CreateNotificationMessage(incident, recipient, channelName);

                        notificationTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await channel.SendNotificationAsync(message);

                                _logger.LogDebug("Notification sent via {Channel} to {Recipient}",
                                    channelName, recipient.Email);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to send notification via {Channel} to {Recipient}",
                                    channelName, recipient.Email);

                                // Try fallback channel;
                                await TryFallbackNotificationAsync(incident, recipient, ex);
                            }
                        }));
                    }
                }
            }

            // Send notifications in parallel, but wait for all to complete;
            await Task.WhenAll(notificationTasks);
        }

        private async Task DistributeReportAsync(SecurityReport report)
        {
            var distributionTasks = new List<Task>();

            foreach (var distribution in _config.ReportDistributions)
            {
                if (distribution.Enabled && distribution.Formats.Contains(report.Format))
                {
                    switch (distribution.Type)
                    {
                        case DistributionType.Email:
                            distributionTasks.Add(SendReportByEmailAsync(report, distribution));
                            break;

                        case DistributionType.CloudStorage:
                            distributionTasks.Add(UploadReportToCloudAsync(report, distribution));
                            break;

                        case DistributionType.APICall:
                            distributionTasks.Add(SendReportViaAPIAsync(report, distribution));
                            break;

                        case DistributionType.FileShare:
                            distributionTasks.Add(CopyReportToShareAsync(report, distribution));
                            break;
                    }
                }
            }

            if (distributionTasks.Any())
            {
                await Task.WhenAll(distributionTasks);
            }
        }

        private async Task GenerateScheduledReportsAsync(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var reportStart = now.Subtract(TimeSpan.FromMinutes(_config.ReportIntervalMinutes));

                await GeneratePeriodicReportAsync(reportStart, now, _config.DefaultReportFormat);

                _logger.LogDebug("Generated scheduled security report for interval ending at {Time}", now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate scheduled security report");
            }
        }

        private ValidationResult ValidateIncident(SecurityIncident incident)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(incident.IncidentType))
                errors.Add("Incident type is required");

            if (string.IsNullOrWhiteSpace(incident.Description))
                errors.Add("Description is required");

            if (string.IsNullOrWhiteSpace(incident.Source))
                errors.Add("Source is required");

            if (incident.Severity == SeverityLevel.Unknown)
                errors.Add("Valid severity level is required");

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private IEnumerable<Recipient> GetRecipientsForIncident(SecurityIncident incident)
        {
            // In real implementation, this would query a database or configuration;
            // For now, return based on severity and incident type;

            var recipients = new List<Recipient>();

            // Always include security team for medium+ severity;
            if (incident.Severity >= SeverityLevel.Medium)
            {
                recipients.Add(new Recipient;
                {
                    Email = "security-team@company.com",
                    Name = "Security Team",
                    NotificationChannels = new[] { "email", "slack" },
                    MinSeverity = SeverityLevel.Medium;
                });
            }

            // Include management for high+ severity;
            if (incident.Severity >= SeverityLevel.High)
            {
                recipients.Add(new Recipient;
                {
                    Email = "management@company.com",
                    Name = "Management Team",
                    NotificationChannels = new[] { "email", "sms" },
                    MinSeverity = SeverityLevel.High;
                });
            }

            // Include emergency contacts for critical incidents;
            if (incident.Severity == SeverityLevel.Critical)
            {
                recipients.Add(new Recipient;
                {
                    Email = "emergency@company.com",
                    Name = "Emergency Contacts",
                    NotificationChannels = new[] { "sms", "pagerduty" },
                    MinSeverity = SeverityLevel.Critical;
                });
            }

            return recipients;
        }

        private NotificationMessage CreateNotificationMessage(
            SecurityIncident incident,
            Recipient recipient,
            string channel)
        {
            return new NotificationMessage;
            {
                To = recipient.Email,
                Subject = $"[{incident.Severity}] Security Incident: {incident.IncidentType}",
                Body = FormatNotificationBody(incident, channel),
                Priority = MapSeverityToPriority(incident.Severity),
                Channel = channel,
                Metadata = new Dictionary<string, string>
                {
                    ["IncidentId"] = incident.Id.ToString(),
                    ["IncidentType"] = incident.IncidentType,
                    ["Severity"] = incident.Severity.ToString(),
                    ["Timestamp"] = incident.ReportedAt.ToString("O")
                }
            };
        }

        private string FormatNotificationBody(SecurityIncident incident, string channel)
        {
            return channel switch;
            {
                "sms" => $"SECURITY ALERT: {incident.IncidentType} ({incident.Severity})\n" +
                        $"Time: {incident.ReportedAt:HH:mm}\n" +
                        $"Source: {incident.Source}\n" +
                        $"ID: {incident.Id}",

                "slack" => $"*Security Incident Reported*\n" +
                          $"• *Type*: {incident.IncidentType}\n" +
                          $"• *Severity*: {incident.Severity}\n" +
                          $"• *Time*: {incident.ReportedAt:yyyy-MM-dd HH:mm:ss}\n" +
                          $"• *Source*: {incident.Source}\n" +
                          $"• *Description*: {incident.Description}\n" +
                          $"• *Incident ID*: `{incident.Id}`",

                _ => $"Security Incident Report\n\n" +
                    $"Incident ID: {incident.Id}\n" +
                    $"Type: {incident.IncidentType}\n" +
                    $"Severity: {incident.Severity}\n" +
                    $"Reported At: {incident.ReportedAt:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Source: {incident.Source}\n\n" +
                    $"Description:\n{incident.Description}\n\n" +
                    $"Affected Systems:\n{string.Join("\n", incident.AffectedSystems)}\n\n" +
                    $"Please review and take appropriate action."
            };
        }

        private async Task SendEmergencyNotificationAsync(SecurityIncident incident, Exception ex)
        {
            try
            {
                var emergencyMessage = new NotificationMessage;
                {
                    To = "emergency-admin@company.com",
                    Subject = "EMERGENCY: Security Reporter Failure",
                    Body = $"Security reporter failed while processing incident:\n\n" +
                          $"Incident Type: {incident.IncidentType}\n" +
                          $"Error: {ex.Message}\n\n" +
                          $"Please investigate immediately.",
                    Priority = NotificationPriority.Critical,
                    Channel = "email"
                };

                if (_notificationChannels.TryGetValue("email", out var emailChannel))
                {
                    await emailChannel.SendNotificationAsync(emergencyMessage);
                }
            }
            catch
            {
                // Last resort - log to system event;
                _logger.LogCritical("CRITICAL: Security reporter failed and emergency notification also failed");
            }
        }

        private async Task<double> CalculateAverageResponseTimeAsync(IEnumerable<SecurityIncident> incidents)
        {
            var resolvedIncidents = incidents;
                .Where(i => i.ResolvedAt.HasValue && i.AcknowledgedAt.HasValue)
                .ToList();

            if (!resolvedIncidents.Any())
                return 0;

            var totalResponseTime = resolvedIncidents;
                .Sum(i => (i.ResolvedAt.Value - i.AcknowledgedAt.Value).TotalMinutes);

            return totalResponseTime / resolvedIncidents.Count;
        }

        private async Task<double> CalculateSecurityScoreAsync()
        {
            // Complex security scoring algorithm;
            var incidents24h = await _repository.GetIncidentsInTimeRangeAsync(
                DateTime.UtcNow.AddHours(-24),
                DateTime.UtcNow);

            var activeThreats = await _threatDetector.GetActiveThreatsCountAsync();
            var unresolvedCount = (await GetUnresolvedIncidentsAsync(TimeSpan.FromHours(24))).Count();

            // Base score;
            double score = 100;

            // Deductions;
            score -= incidents24h.Count(i => i.Severity == SeverityLevel.Critical) * 20;
            score -= incidents24h.Count(i => i.Severity == SeverityLevel.High) * 10;
            score -= incidents24h.Count(i => i.Severity == SeverityLevel.Medium) * 5;
            score -= incidents24h.Count(i => i.Severity == SeverityLevel.Low) * 1;
            score -= activeThreats * 15;
            score -= unresolvedCount * 8;

            // Ensure score doesn't go below 0;
            return Math.Max(0, Math.Min(100, score));
        }

        #region Helper Methods for Report Generation;

        private async Task<Dictionary<SeverityLevel, TimeSpan>> CalculateAverageResponseTimesAsync(
            IEnumerable<SecurityIncident> incidents)
        {
            var result = new Dictionary<SeverityLevel, TimeSpan>();

            foreach (var severity in Enum.GetValues(typeof(SeverityLevel)).Cast<SeverityLevel>())
            {
                var severityIncidents = incidents;
                    .Where(i => i.Severity == severity && i.ResolvedAt.HasValue && i.AcknowledgedAt.HasValue)
                    .ToList();

                if (severityIncidents.Any())
                {
                    var avgMinutes = severityIncidents;
                        .Average(i => (i.ResolvedAt.Value - i.AcknowledgedAt.Value).TotalMinutes);

                    result[severity] = TimeSpan.FromMinutes(avgMinutes);
                }
                else;
                {
                    result[severity] = TimeSpan.Zero;
                }
            }

            return result;
        }

        private async Task<List<SecurityRecommendation>> GenerateRecommendationsAsync(
            IEnumerable<SecurityIncident> incidents,
            ThreatStatistics threatStats)
        {
            var recommendations = new List<SecurityRecommendation>();

            // Analyze incident patterns;
            var incidentGroups = incidents.GroupBy(i => i.IncidentType);

            foreach (var group in incidentGroups)
            {
                if (group.Count() > 5) // Frequent incident type;
                {
                    recommendations.Add(new SecurityRecommendation;
                    {
                        Priority = RecommendationPriority.High,
                        Title = $"Address frequent {group.Key} incidents",
                        Description = $"Detected {group.Count()} incidents of type '{group.Key}' in reporting period.",
                        Action = "Review and strengthen controls for this incident type",
                        EstimatedEffort = "Medium",
                        Impact = "High"
                    });
                }
            }

            // Check response times;
            var avgResponseTime = await CalculateAverageResponseTimeAsync(incidents);
            if (avgResponseTime > 60) // More than 1 hour average;
            {
                recommendations.Add(new SecurityRecommendation;
                {
                    Priority = RecommendationPriority.Medium,
                    Title = "Improve incident response time",
                    Description = $"Average response time is {avgResponseTime:F1} minutes.",
                    Action = "Review and optimize response procedures",
                    EstimatedEffort = "Low",
                    Impact = "Medium"
                });
            }

            // Check threat detection effectiveness;
            if (threatStats.FalsePositiveRate > 0.3) // 30% false positive rate;
            {
                recommendations.Add(new SecurityRecommendation;
                {
                    Priority = RecommendationPriority.Medium,
                    Title = "Reduce false positive rate",
                    Description = $"Current false positive rate is {threatStats.FalsePositiveRate:P0}.",
                    Action = "Fine-tune threat detection rules and thresholds",
                    EstimatedEffort = "Medium",
                    Impact = "Medium"
                });
            }

            return recommendations;
        }

        private async Task<AlertStatus> GetSystemAlertStatusAsync()
        {
            var incidents = await GetUnresolvedIncidentsAsync(TimeSpan.FromHours(1));

            if (incidents.Any(i => i.Severity == SeverityLevel.Critical))
                return AlertStatus.Critical;

            if (incidents.Any(i => i.Severity == SeverityLevel.High))
                return AlertStatus.High;

            if (incidents.Any(i => i.Severity == SeverityLevel.Medium))
                return AlertStatus.Medium;

            if (incidents.Any(i => i.Severity == SeverityLevel.Low))
                return AlertStatus.Low;

            return AlertStatus.Normal;
        }

        #endregion;

        #region Notification Helper Methods;

        private async Task TryFallbackNotificationAsync(
            SecurityIncident incident,
            Recipient recipient,
            Exception originalError)
        {
            try
            {
                // Try SMS as fallback;
                if (_notificationChannels.TryGetValue("sms", out var smsChannel))
                {
                    var fallbackMessage = new NotificationMessage;
                    {
                        To = recipient.Email,
                        Subject = "SECURITY ALERT - Fallback",
                        Body = $"Security incident detected but primary notification failed. Incident ID: {incident.Id}",
                        Priority = NotificationPriority.High,
                        Channel = "sms"
                    };

                    await smsChannel.SendNotificationAsync(fallbackMessage);

                    _logger.LogWarning("Sent fallback notification via SMS for incident {IncidentId}", incident.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallback notification also failed for incident {IncidentId}", incident.Id);
            }
        }

        private async Task NotifyIncidentUpdateAsync(SecurityIncident incident, string updateType)
        {
            var message = new NotificationMessage;
            {
                Subject = $"Incident Update: {incident.Id} - {updateType}",
                Body = $"Incident {incident.Id} has been {updateType.ToLower()}.\n" +
                      $"Current Status: {incident.Status}\n" +
                      $"Severity: {incident.Severity}\n" +
                      $"Updated At: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
                Priority = MapSeverityToPriority(incident.Severity),
                Channel = "email"
            };

            // Send to incident responders;
            var responders = GetRecipientsForIncident(incident);

            foreach (var responder in responders)
            {
                message.To = responder.Email;

                if (_notificationChannels.TryGetValue("email", out var emailChannel))
                {
                    await emailChannel.SendNotificationAsync(message);
                }
            }
        }

        private NotificationPriority MapSeverityToPriority(SeverityLevel severity)
        {
            return severity switch;
            {
                SeverityLevel.Critical => NotificationPriority.Critical,
                SeverityLevel.High => NotificationPriority.High,
                SeverityLevel.Medium => NotificationPriority.Medium,
                SeverityLevel.Low => NotificationPriority.Low,
                _ => NotificationPriority.Low;
            };
        }

        private async Task SendReportByEmailAsync(SecurityReport report, DistributionConfig config)
        {
            // Implementation for email distribution;
            var emailService = new EmailService();
            await emailService.SendReportAsync(report, config.Recipients);
        }

        private async Task UploadReportToCloudAsync(SecurityReport report, DistributionConfig config)
        {
            // Implementation for cloud storage upload;
            var cloudStorage = CloudStorageFactory.GetStorage(config.Parameters["Provider"]);
            await cloudStorage.UploadFileAsync(report.FilePath, config.Parameters["Container"]);
        }

        private async Task SendReportViaAPIAsync(SecurityReport report, DistributionConfig config)
        {
            // Implementation for API distribution;
            using var client = new HttpClient();
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(await System.IO.File.ReadAllBytesAsync(report.FilePath));

            content.Add(fileContent, "report", System.IO.Path.GetFileName(report.FilePath));

            await client.PostAsync(config.Parameters["Endpoint"], content);
        }

        private async Task CopyReportToShareAsync(SecurityReport report, DistributionConfig config)
        {
            // Implementation for file share copy;
            var destinationPath = System.IO.Path.Combine(
                config.Parameters["SharePath"],
                System.IO.Path.GetFileName(report.FilePath));

            System.IO.File.Copy(report.FilePath, destinationPath, true);
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
                    _periodicReportTimer?.Dispose();
                    _reportLock?.Dispose();

                    foreach (var channel in _notificationChannels.Values)
                    {
                        if (channel is IDisposable disposableChannel)
                        {
                            disposableChannel.Dispose();
                        }
                    }

                    _logger.LogInformation("SecurityReporter disposed");
                }

                _disposed = true;
            }
        }

        ~SecurityReporter()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Enums;

    public enum SeverityLevel;
    {
        Unknown = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    public enum IncidentStatus;
    {
        Reported = 0,
        Acknowledged = 1,
        Investigating = 2,
        Resolved = 3,
        Closed = 4;
    }

    public enum ReportFormat;
    {
        PDF,
        Excel,
        HTML,
        CSV,
        JSON;
    }

    public enum NotificationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum DistributionType;
    {
        Email,
        CloudStorage,
        APICall,
        FileShare;
    }

    public enum AlertStatus;
    {
        Normal,
        Low,
        Medium,
        High,
        Critical;
    }

    public enum RecommendationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public class SecurityIncident;
    {
        public Guid Id { get; set; }
        public string IncidentType { get; set; }
        public SeverityLevel Severity { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public DateTime ReportedAt { get; set; }
        public string ReportedBy { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string AcknowledgedBy { get; set; }
        public string AcknowledgmentNotes { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string ResolvedBy { get; set; }
        public string ResolutionNotes { get; set; }
        public IncidentStatus Status { get; set; }
        public List<string> AffectedSystems { get; set; } = new List<string>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public List<EscalationRecord> EscalationHistory { get; set; } = new List<EscalationRecord>();
        public List<string> Attachments { get; set; } = new List<string>();
        public double EstimatedImpact { get; set; }
        public string Category { get; set; }
    }

    public class EscalationRecord;
    {
        public DateTime Timestamp { get; set; }
        public SeverityLevel FromSeverity { get; set; }
        public SeverityLevel ToSeverity { get; set; }
        public string Reason { get; set; }
        public string EscalatedBy { get; set; }
    }

    public class ReportResult;
    {
        public bool Success { get; set; }
        public Guid IncidentId { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; }

        public static ReportResult Success(Guid incidentId)
        {
            return new ReportResult;
            {
                Success = true,
                IncidentId = incidentId,
                Timestamp = DateTime.UtcNow;
            };
        }

        public static ReportResult Failed(List<string> errors)
        {
            return new ReportResult;
            {
                Success = false,
                Errors = errors,
                Timestamp = DateTime.UtcNow;
            };
        }
    }

    public class SecurityReport;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime GeneratedAt { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public ReportFormat Format { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
        public List<SecurityRecommendation> Recommendations { get; set; } = new List<SecurityRecommendation>();
        public string Checksum { get; set; }
    }

    public class SecurityDashboardStats;
    {
        public int IncidentsLast24h { get; set; }
        public int IncidentsLast7d { get; set; }
        public int IncidentsLast30d { get; set; }
        public int ActiveThreats { get; set; }
        public int UnresolvedIncidents { get; set; }
        public double AverageResponseTime { get; set; }
        public Dictionary<string, int> TopIncidentTypes { get; set; } = new Dictionary<string, int>();
        public double SystemSecurityScore { get; set; }
        public DateTime? LastIncidentTime { get; set; }
        public AlertStatus AlertStatus { get; set; }
    }

    public class SecurityRecommendation;
    {
        public RecommendationPriority Priority { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Action { get; set; }
        public string EstimatedEffort { get; set; }
        public string Impact { get; set; }
        public DateTime? DueDate { get; set; }
        public string AssignedTo { get; set; }
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class ThreatStatistics;
    {
        public int TotalThreatsDetected { get; set; }
        public int ThreatsBlocked { get; set; }
        public int ThreatsMitigated { get; set; }
        public int ThreatsPending { get; set; }
        public double FalsePositiveRate { get; set; }
        public double DetectionAccuracy { get; set; }
        public TimeSpan AverageDetectionTime { get; set; }
    }

    public class SecurityReportData;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TotalIncidents { get; set; }
        public Dictionary<SeverityLevel, int> IncidentsBySeverity { get; set; } = new Dictionary<SeverityLevel, int>();
        public Dictionary<string, int> IncidentsByType { get; set; } = new Dictionary<string, int>();
        public ThreatStatistics ThreatStatistics { get; set; }
        public object SecurityStatus { get; set; }
        public Dictionary<SeverityLevel, TimeSpan> ResponseTimes { get; set; } = new Dictionary<SeverityLevel, TimeSpan>();
        public List<SecurityRecommendation> Recommendations { get; set; } = new List<SecurityRecommendation>();
    }

    public class NotificationChannelConfig;
    {
        public string ChannelName { get; set; }
        public string ChannelType { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public bool Enabled { get; set; }
        public int RetryAttempts { get; set; } = 3;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public class DistributionConfig;
    {
        public DistributionType Type { get; set; }
        public List<string> Recipients { get; set; } = new List<string>();
        public List<ReportFormat> Formats { get; set; } = new List<ReportFormat>();
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        public bool Enabled { get; set; }
        public Schedule Schedule { get; set; }
    }

    public class Schedule;
    {
        public bool Daily { get; set; }
        public bool Weekly { get; set; }
        public bool Monthly { get; set; }
        public TimeSpan TimeOfDay { get; set; }
        public DayOfWeek DayOfWeek { get; set; }
        public int DayOfMonth { get; set; }
    }

    public class ReportConfig;
    {
        public bool EnablePeriodicReports { get; set; } = true;
        public int ReportIntervalMinutes { get; set; } = 60;
        public ReportFormat DefaultReportFormat { get; set; } = ReportFormat.PDF;
        public bool AutoSendPeriodicReports { get; set; } = true;
        public List<NotificationChannelConfig> NotificationChannels { get; set; } = new List<NotificationChannelConfig>();
        public List<DistributionConfig> ReportDistributions { get; set; } = new List<DistributionConfig>();
        public string StoragePath { get; set; } = "./Reports";
        public int RetentionDays { get; set; } = 90;
        public bool EnableCompression { get; set; } = true;
        public bool EnableEncryption { get; set; } = true;
    }

    public class Recipient;
    {
        public string Email { get; set; }
        public string Name { get; set; }
        public string[] NotificationChannels { get; set; }
        public SeverityLevel MinSeverity { get; set; }
        public List<string> IncidentTypes { get; set; } = new List<string>();
        public bool IsActive { get; set; } = true;
    }

    public class NotificationMessage;
    {
        public string To { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public NotificationPriority Priority { get; set; }
        public string Channel { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public List<Attachment> Attachments { get; set; } = new List<Attachment>();
    }

    public class Attachment;
    {
        public string Filename { get; set; }
        public byte[] Content { get; set; }
        public string ContentType { get; set; }
    }

    #endregion;

    #region Interfaces for Dependencies;

    public interface IIncidentHandler;
    {
        Task HandleIncidentAsync(SecurityIncident incident);
    }

    public interface INotificationChannel;
    {
        Task<bool> SendNotificationAsync(NotificationMessage message);
        Task<TestResult> TestConnectionAsync();
        string ChannelType { get; }
    }

    public interface IReportGenerator;
    {
        Task<SecurityReport> GenerateReportAsync(SecurityReportData data, ReportFormat format, string reportName);
        Task<byte[]> GenerateReportBytesAsync(SecurityReportData data, ReportFormat format);
    }

    public class TestResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ResponseTime { get; set; }
    }

    #endregion;

    #region Exception Classes;

    public class SecurityReportingException : Exception
    {
        public SecurityReportingException(string message) : base(message) { }
        public SecurityReportingException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ConfigurationException : Exception
    {
        public ConfigurationException(string message) : base(message) { }
    }

    #endregion;
}
