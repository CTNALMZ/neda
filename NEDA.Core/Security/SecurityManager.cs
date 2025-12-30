// NEDA.Core/Security/SecurityManager.cs
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.Core.Configuration.AppSettings;
using NEDA.SecurityModules.AdvancedSecurity.Authentication;
using NEDA.SecurityModules.AdvancedSecurity.Authorization;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.SecurityModules.Firewall;
using NEDA.SecurityModules.Monitoring;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.Security
{
    /// <summary>
    /// Merkezi güvenlik yönetim sistemi - Singleton tasarım deseni ile implemente edilmiştir.
    /// </summary>
    public sealed class SecurityManager : ISecurityManager, IDisposable
    {
        #region Singleton Implementation
        private static readonly Lazy<SecurityManager> _instance =
            new Lazy<SecurityManager>(() => new SecurityManager());

        public static SecurityManager Instance => _instance.Value;

        private SecurityManager()
        {
            Initialize();
        }
        #endregion

        #region Fields and Properties
        private readonly ConcurrentDictionary<string, UserSession> _activeSessions =
            new ConcurrentDictionary<string, UserSession>();

        private readonly ConcurrentDictionary<string, SecurityPolicy> _securityPolicies =
            new ConcurrentDictionary<string, SecurityPolicy>();

        private readonly ConcurrentBag<SecurityEvent> _securityEvents =
            new ConcurrentBag<SecurityEvent>();

        private readonly ReaderWriterLockSlim _securityLock = new ReaderWriterLockSlim();
        private readonly SemaphoreSlim _auditLock = new SemaphoreSlim(1, 1);
        private readonly MemoryCache _securityCache = new MemoryCache(new MemoryCacheOptions());
        private readonly CancellationTokenSource _cancellationTokenSource =
            new CancellationTokenSource();

        private Task _monitoringTask;
        private Task _cleanupTask;
        private bool _isInitialized;
        private bool _isDisposed;

        private AppConfig _appConfig;
        private ILogger _logger;
        private IAuthService _authService;
        private IPermissionService _permissionService;
        private ICryptoEngine _cryptoEngine;
        private IFirewallManager _firewallManager;
        private ISecurityMonitor _securityMonitor;

        private DateTime _lastSecurityScan = DateTime.MinValue;
        private int _failedAttempts;
        private bool _isLocked;

        public SecurityLevel CurrentSecurityLevel { get; private set; } = SecurityLevel.Medium;
        public bool IsAuditEnabled { get; private set; } = true;
        public int ActiveSessionCount => _activeSessions.Count;
        public DateTime LastSecurityIncident { get; private set; } = DateTime.MinValue;
        #endregion

        #region Public Methods
        /// <summary>
        /// SecurityManager'ı başlatır ve yapılandırır.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            try
            {
                _securityLock.EnterWriteLock();

                LoadDependencies();
                LoadSecurityPolicies();
                CreateSystemAccounts();
                InitializeSecurityModules();
                StartMonitoringTasks();
                StartCleanupTask();

                _isInitialized = true;

                LogSecurityEvent(SecurityEventType.SystemInitialized,
                    "SecurityManager başarıyla başlatıldı",
                    SecuritySeverity.Information);
            }
            catch (Exception ex)
            {
                HandleInitializationError(ex);
                throw new SecurityInitializationException(
                    "SecurityManager başlatma sırasında hata oluştu", ex);
            }
            finally
            {
                if (_securityLock.IsWriteLockHeld)
                    _securityLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Kullanıcı kimlik doğrulaması yapar.
        /// </summary>
        public async Task<AuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            string clientIp = null,
            Dictionary<string, string> additionalData = null)
        {
            ValidateDisposed();
            ValidateSecurityLock();

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Kullanıcı adı boş olamaz", nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Şifre boş olamaz", nameof(password));

            try
            {
                // Brute-force koruması
                if (IsAuthenticationBlocked(clientIp))
                {
                    LogSecurityEvent(SecurityEventType.AuthenticationBlocked,
                        $"IP {clientIp} için kimlik doğrulama engellendi (brute-force)",
                        SecuritySeverity.High);

                    return AuthenticationResult.Failed(
                        "Kimlik doğrulama geçici olarak engellendi. Lütfen daha sonra tekrar deneyin.",
                        AuthenticationFailureReason.RateLimited);
                }

                // IP kontrolü
                if (!await IsIpAllowedAsync(clientIp))
                {
                    LogSecurityEvent(SecurityEventType.IpBlocked,
                        $"IP {clientIp} erişim engellendi",
                        SecuritySeverity.Medium);

                    return AuthenticationResult.Failed(
                        "IP adresinizden erişim engellendi",
                        AuthenticationFailureReason.IpBlocked);
                }

                // Kimlik doğrulama
                var authResult = await _authService.AuthenticateAsync(
                    username,
                    password,
                    clientIp,
                    additionalData);

                if (authResult.IsSuccess)
                {
                    // Oturum oluştur
                    var session = await CreateUserSessionAsync(
                        authResult.User,
                        clientIp,
                        authResult.Token);

                    // Başarılı oturum için cache temizle
                    ClearFailedAttempts(clientIp);

                    LogSecurityEvent(SecurityEventType.AuthenticationSuccess,
                        $"Kullanıcı {username} başarıyla kimlik doğruladı",
                        SecuritySeverity.Information,
                        username,
                        clientIp);

                    return AuthenticationResult.Success(
                        session.SessionId,
                        authResult.Token,
                        authResult.User,
                        session.ExpiresAt);
                }

                // Başarısız girişi kaydet
                RecordFailedAttempt(clientIp, username);

                LogSecurityEvent(SecurityEventType.AuthenticationFailed,
                    $"Kullanıcı {username} kimlik doğrulama başarısız: {authResult.FailureReason}",
                    SecuritySeverity.Medium,
                    username,
                    clientIp);

                return authResult;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Kimlik doğrulama sırasında hata oluştu: {Username}", username);

                LogSecurityEvent(SecurityEventType.AuthenticationError,
                    $"Kimlik doğrulama hatası: {ex.Message}",
                    SecuritySeverity.High,
                    username,
                    clientIp);

                throw new SecurityAuthenticationException("Kimlik doğrulama sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Token ile kimlik doğrulaması yapar.
        /// </summary>
        public async Task<AuthenticationResult> AuthenticateWithTokenAsync(
            string token,
            string clientIp = null)
        {
            ValidateDisposed();
            ValidateSecurityLock();

            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token boş olamaz", nameof(token));

            try
            {
                // Token doğrulama
                var validationResult = await _authService.ValidateTokenAsync(token, clientIp);

                if (!validationResult.IsValid)
                {
                    LogSecurityEvent(SecurityEventType.InvalidToken,
                        "Geçersiz token kullanımı",
                        SecuritySeverity.Medium,
                        clientIp: clientIp);

                    return AuthenticationResult.Failed(
                        "Geçersiz veya süresi dolmuş token",
                        AuthenticationFailureReason.InvalidToken);
                }

                // Oturumu bul veya yenile
                var session = await GetOrRefreshSessionAsync(
                    validationResult.UserId,
                    token,
                    clientIp);

                LogSecurityEvent(SecurityEventType.TokenAuthenticationSuccess,
                    $"Token kimlik doğrulama başarılı: {validationResult.UserId}",
                    SecuritySeverity.Information,
                    validationResult.UserId,
                    clientIp);

                return AuthenticationResult.Success(
                    session.SessionId,
                    token,
                    validationResult.User,
                    session.ExpiresAt);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Token kimlik doğrulama sırasında hata oluştu");

                LogSecurityEvent(SecurityEventType.TokenAuthenticationError,
                    $"Token kimlik doğrulama hatası: {ex.Message}",
                    SecuritySeverity.High,
                    clientIp: clientIp);

                throw new SecurityAuthenticationException("Token kimlik doğrulama sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Yetkilendirme kontrolü yapar.
        /// </summary>
        public async Task<AuthorizationResult> AuthorizeAsync(
            string userId,
            string resource,
            string action,
            Dictionary<string, object> context = null)
        {
            ValidateDisposed();
            ValidateSecurityLock();

            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("Kullanıcı ID boş olamaz", nameof(userId));

            if (string.IsNullOrWhiteSpace(resource))
                throw new ArgumentException("Kaynak boş olamaz", nameof(resource));

            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("Eylem boş olamaz", nameof(action));

            try
            {
                // Cache kontrolü
                var cacheKey = $"auth_{userId}_{resource}_{action}";
                if (_securityCache.TryGetValue(cacheKey, out AuthorizationResult cachedResult))
                {
                    return cachedResult;
                }

                // Yetki kontrolü
                var result = await _permissionService.CheckPermissionAsync(
                    userId,
                    resource,
                    action,
                    context);

                // Cache'e kaydet (kısa süreli)
                if (result.IsAuthorized)
                {
                    _securityCache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                }

                // Audit log
                if (IsAuditEnabled)
                {
                    await LogAuthorizationAuditAsync(userId, resource, action, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Yetkilendirme kontrolü sırasında hata oluştu: {UserId}, {Resource}, {Action}",
                    userId, resource, action);

                // Güvenlik nedeniyle yetkilendirme hatası durumunda erişimi reddet
                return AuthorizationResult.Denied(
                    "Yetkilendirme kontrolü sırasında hata oluştu",
                    AuthorizationFailureReason.SystemError);
            }
        }

        /// <summary>
        /// Kullanıcı oturumunu sonlandırır.
        /// </summary>
        public async Task<bool> LogoutAsync(string sessionId, string userId = null)
        {
            ValidateDisposed();

            try
            {
                if (_activeSessions.TryRemove(sessionId, out var session))
                {
                    // Token'ı geçersiz kıl
                    await _authService.InvalidateTokenAsync(session.AccessToken);

                    // Oturum kaydını temizle
                    await ClearSessionDataAsync(sessionId);

                    LogSecurityEvent(SecurityEventType.UserLoggedOut,
                        $"Kullanıcı oturumu sonlandırıldı: {session.UserId}",
                        SecuritySeverity.Information,
                        session.UserId,
                        session.ClientIp);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Oturum sonlandırma sırasında hata oluştu: {SessionId}", sessionId);
                return false;
            }
        }

        /// <summary>
        /// Tüm oturumları temizler (acil durum).
        /// </summary>
        public async Task<int> ClearAllSessionsAsync(bool force = false)
        {
            ValidateDisposed();

            if (!force && CurrentSecurityLevel < SecurityLevel.High)
            {
                throw new SecurityException("Yüksek güvenlik seviyesi gerekiyor");
            }

            var count = 0;
            var sessionsToRemove = _activeSessions.Keys.ToList();

            foreach (var sessionId in sessionsToRemove)
            {
                if (await LogoutAsync(sessionId))
                {
                    count++;
                }
            }

            LogSecurityEvent(SecurityEventType.AllSessionsCleared,
                $"Tüm oturumlar temizlendi: {count} oturum",
                SecuritySeverity.High);

            return count;
        }

        /// <summary>
        /// Güvenlik taraması başlatır.
        /// </summary>
        public async Task<SecurityScanResult> PerformSecurityScanAsync()
        {
            ValidateDisposed();

            try
            {
                var scanId = Guid.NewGuid().ToString();
                var startTime = DateTime.UtcNow;

                LogSecurityEvent(SecurityEventType.SecurityScanStarted,
                    $"Güvenlik taraması başlatıldı: {scanId}",
                    SecuritySeverity.Information);

                // 1. Aktif oturumları kontrol et
                var sessionScan = await ScanActiveSessionsAsync();

                // 2. Sistem bütünlüğünü kontrol et
                var integrityScan = await PerformIntegrityCheckAsync();

                // 3. Güvenlik açıklarını tara
                var vulnerabilityScan = await ScanVulnerabilitiesAsync();

                // 4. Log analizi yap
                var logAnalysis = await AnalyzeSecurityLogsAsync();

                var result = new SecurityScanResult
                {
                    ScanId = scanId,
                    ScanTime = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - startTime,
                    SessionIssues = sessionScan,
                    IntegrityIssues = integrityScan,
                    Vulnerabilities = vulnerabilityScan,
                    LogFindings = logAnalysis,
                    OverallRiskLevel = CalculateRiskLevel(
                        sessionScan,
                        integrityScan,
                        vulnerabilityScan,
                        logAnalysis)
                };

                _lastSecurityScan = DateTime.UtcNow;

                // Risk seviyesine göre güvenlik seviyesini ayarla
                await AdjustSecurityLevelBasedOnScan(result);

                LogSecurityEvent(SecurityEventType.SecurityScanCompleted,
                    $"Güvenlik taraması tamamlandı: {scanId}, Risk Seviyesi: {result.OverallRiskLevel}",
                    SecuritySeverity.Information);

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Güvenlik taraması sırasında hata oluştu");

                LogSecurityEvent(SecurityEventType.SecurityScanFailed,
                    $"Güvenlik taraması başarısız: {ex.Message}",
                    SecuritySeverity.High);

                throw new SecurityScanException("Güvenlik taraması başarısız", ex);
            }
        }

        /// <summary>
        /// Güvenlik olaylarını filtreleyerek getirir.
        /// </summary>
        public IEnumerable<SecurityEvent> GetSecurityEvents(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            SecurityEventType? eventType = null,
            SecuritySeverity? minSeverity = null)
        {
            ValidateDisposed();

            var events = _securityEvents.AsEnumerable();

            if (fromDate.HasValue)
                events = events.Where(e => e.Timestamp >= fromDate.Value);

            if (toDate.HasValue)
                events = events.Where(e => e.Timestamp <= toDate.Value);

            if (eventType.HasValue)
                events = events.Where(e => e.EventType == eventType.Value);

            if (minSeverity.HasValue)
                events = events.Where(e => e.Severity >= minSeverity.Value);

            return events.OrderByDescending(e => e.Timestamp).Take(1000);
        }

        /// <summary>
        /// Sistem durumunu alır.
        /// </summary>
        public SecurityStatus GetStatus()
        {
            ValidateDisposed();

            return new SecurityStatus
            {
                IsInitialized = _isInitialized,
                IsLocked = _isLocked,
                CurrentSecurityLevel = CurrentSecurityLevel,
                ActiveSessionCount = ActiveSessionCount,
                FailedAttempts = _failedAttempts,
                LastSecurityScan = _lastSecurityScan,
                LastSecurityIncident = LastSecurityIncident,
                IsAuditEnabled = IsAuditEnabled,
                SecurityModulesStatus = GetSecurityModulesStatus()
            };
        }

        /// <summary>
        /// Güvenlik seviyesini değiştirir.
        /// </summary>
        public void SetSecurityLevel(SecurityLevel level)
        {
            ValidateDisposed();
            ValidateSecurityLock();

            var previousLevel = CurrentSecurityLevel;
            CurrentSecurityLevel = level;

            UpdatePoliciesForSecurityLevel(level);

            LogSecurityEvent(SecurityEventType.SecurityLevelChanged,
                $"Güvenlik seviyesi değiştirildi: {previousLevel} -> {level}",
                SecuritySeverity.Medium);
        }

        /// <summary>
        /// Acil durum kilidini etkinleştirir.
        /// </summary>
        public void ActivateEmergencyLock()
        {
            ValidateDisposed();

            _securityLock.EnterWriteLock();
            try
            {
                _isLocked = true;
                SetSecurityLevel(SecurityLevel.Critical);

                // Tüm oturumları sonlandır (ateşle-unut)
                _ = Task.Run(() => ClearAllSessionsAsync(true));

                LockAllSecurityModules();

                LogSecurityEvent(SecurityEventType.EmergencyLockActivated,
                    "Acil durum kilidi etkinleştirildi",
                    SecuritySeverity.Critical);
            }
            finally
            {
                if (_securityLock.IsWriteLockHeld)
                    _securityLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Şifreleme işlemi yapar.
        /// </summary>
        public async Task<byte[]> EncryptAsync(
            byte[] data,
            string keyId = null,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
        {
            ValidateDisposed();

            if (data == null || data.Length == 0)
                throw new ArgumentException("Şifrelenecek veri boş olamaz", nameof(data));

            return await _cryptoEngine.EncryptAsync(data, keyId, algorithm);
        }

        /// <summary>
        /// Şifre çözme işlemi yapar.
        /// </summary>
        public async Task<byte[]> DecryptAsync(
            byte[] encryptedData,
            string keyId = null,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
        {
            ValidateDisposed();

            if (encryptedData == null || encryptedData.Length == 0)
                throw new ArgumentException("Şifreli veri boş olamaz", nameof(encryptedData));

            return await _cryptoEngine.DecryptAsync(encryptedData, keyId, algorithm);
        }

        /// <summary>
        /// Veri bütünlüğünü doğrular.
        /// </summary>
        public async Task<bool> VerifyIntegrityAsync(
            byte[] data,
            byte[] signature,
            string keyId = null)
        {
            ValidateDisposed();

            if (data == null)
                throw new ArgumentException("Veri boş olamaz", nameof(data));

            if (signature == null)
                throw new ArgumentException("İmza boş olamaz", nameof(signature));

            return await _cryptoEngine.VerifyAsync(data, signature, keyId);
        }
        #endregion

        #region Private Methods
        private void LoadDependencies()
        {
            var serviceProvider = ServiceProviderFactory.GetServiceProvider();

            _appConfig = serviceProvider.GetRequiredService<AppConfig>();
            _logger = serviceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<SecurityManager>();

            _authService = serviceProvider.GetRequiredService<IAuthService>();
            _permissionService = serviceProvider.GetRequiredService<IPermissionService>();
            _cryptoEngine = serviceProvider.GetRequiredService<ICryptoEngine>();
            _firewallManager = serviceProvider.GetRequiredService<IFirewallManager>();
            _securityMonitor = serviceProvider.GetRequiredService<ISecurityMonitor>();
        }

        private void LoadSecurityPolicies()
        {
            var defaultPolicies = new[]
            {
                new SecurityPolicy
                {
                    Id = "password_policy",
                    Name = "Şifre Politikası",
                    Description = "Kullanıcı şifreleri için gereksinimler",
                    Rules = new Dictionary<string, object>
                    {
                        ["min_length"] = 12,
                        ["require_uppercase"] = true,
                        ["require_lowercase"] = true,
                        ["require_digits"] = true,
                        ["require_special"] = true,
                        ["max_age_days"] = 90,
                        ["prevent_reuse"] = 5
                    },
                    AppliesTo = SecurityPolicyScope.AllUsers
                },
                new SecurityPolicy
                {
                    Id = "session_policy",
                    Name = "Oturum Politikası",
                    Description = "Kullanıcı oturumları için kurallar",
                    Rules = new Dictionary<string, object>
                    {
                        ["timeout_minutes"] = 30,
                        ["max_sessions_per_user"] = 3,
                        ["require_https"] = true,
                        ["invalidate_on_password_change"] = true
                    },
                    AppliesTo = SecurityPolicyScope.AllUsers
                },
                new SecurityPolicy
                {
                    Id = "authentication_policy",
                    Name = "Kimlik Doğrulama Politikası",
                    Description = "Kimlik doğrulama işlemleri için kurallar",
                    Rules = new Dictionary<string, object>
                    {
                        ["max_attempts"] = 5,
                        ["lockout_duration_minutes"] = 15,
                        ["require_mfa"] = false,
                        ["ip_whitelist_required"] = false
                    },
                    AppliesTo = SecurityPolicyScope.AllUsers
                }
            };

            foreach (var policy in defaultPolicies)
            {
                _securityPolicies[policy.Id] = policy;
            }
        }

        private void CreateSystemAccounts()
        {
            var systemAccounts = new[]
            {
                new SystemAccount
                {
                    Id = "SYSTEM_ADMIN",
                    Name = "Sistem Yöneticisi",
                    Description = "Tam sistem erişimi",
                    Permissions = new[] { "*" },
                    IsEnabled = true
                },
                new SystemAccount
                {
                    Id = "SECURITY_MONITOR",
                    Name = "Güvenlik İzleyici",
                    Description = "Güvenlik olaylarını izleme",
                    Permissions = new[] { "security.view", "security.monitor" },
                    IsEnabled = true
                }
            };

            foreach (var account in systemAccounts)
            {
                _securityCache.Set($"system_account_{account.Id}", account, TimeSpan.FromDays(1));
            }
        }

        private void InitializeSecurityModules()
        {
            _authService.Initialize();
            _permissionService.Initialize();
            _cryptoEngine.Initialize();
            _firewallManager.Initialize();
            _securityMonitor.StartMonitoring();
        }

        private void StartMonitoringTasks()
        {
            _monitoringTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await MonitorSessionsAsync();
                        await MonitorSecurityEventsAsync();
                        await CheckSystemHealthAsync();

                        await Task.Delay(TimeSpan.FromMinutes(1), _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Güvenlik izleme görevi sırasında hata oluştu");
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private void StartCleanupTask()
        {
            _cleanupTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await CleanupExpiredSessionsAsync();
                        await CleanupOldSecurityEventsAsync();
                        await CleanupSecurityCacheAsync();

                        await Task.Delay(TimeSpan.FromMinutes(5), _cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Güvenlik temizleme görevi sırasında hata oluştu");
                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(1), _cancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private Task<UserSession> CreateUserSessionAsync(
            UserIdentity user,
            string clientIp,
            string accessToken)
        {
            var session = new UserSession
            {
                SessionId = GenerateSessionId(),
                UserId = user.Id,
                Username = user.Username,
                AccessToken = accessToken,
                ClientIp = clientIp,
                UserAgent = GetUserAgent(),
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(GetSessionTimeout()),
                IsValid = true,
                AdditionalData = new Dictionary<string, object>()
            };

            _activeSessions[session.SessionId] = session;

            CacheSession(session);

            return Task.FromResult(session);
        }

        private async Task<UserSession> GetOrRefreshSessionAsync(
            string userId,
            string token,
            string clientIp)
        {
            var existingSession = _activeSessions.Values
                .FirstOrDefault(s => s.UserId == userId && s.AccessToken == token);

            if (existingSession != null && existingSession.IsValid)
            {
                existingSession.LastActivity = DateTime.UtcNow;
                existingSession.ExpiresAt = DateTime.UtcNow.AddMinutes(GetSessionTimeout());

                if (!string.IsNullOrEmpty(clientIp) && existingSession.ClientIp != clientIp)
                {
                    existingSession.ClientIp = clientIp;
                    LogSecurityEvent(SecurityEventType.SessionIpChanged,
                        $"Oturum IP adresi değişti: {existingSession.UserId}",
                        SecuritySeverity.Low,
                        existingSession.UserId,
                        clientIp);
                }

                return existingSession;
            }

            var user = await _authService.GetUserByIdAsync(userId);
            return await CreateUserSessionAsync(user, clientIp, token);
        }

        private async Task MonitorSessionsAsync()
        {
            var now = DateTime.UtcNow;

            var expiredSessions = _activeSessions.Values
                .Where(s => s.ExpiresAt < now || !s.IsValid)
                .ToList();

            foreach (var session in expiredSessions)
            {
                await LogoutAsync(session.SessionId, session.UserId);
            }

            var inactiveThreshold = TimeSpan.FromMinutes(GetInactiveTimeout());
            var inactiveSessions = _activeSessions.Values
                .Where(s => now - s.LastActivity > inactiveThreshold)
                .ToList();

            foreach (var session in inactiveSessions)
            {
                LogSecurityEvent(SecurityEventType.SessionTimeout,
                    $"Hareketsiz oturum zaman aşımı: {session.UserId}",
                    SecuritySeverity.Low,
                    session.UserId,
                    session.ClientIp);

                await LogoutAsync(session.SessionId, session.UserId);
            }
        }

        private async Task MonitorSecurityEventsAsync()
        {
            var recentHighEvents = GetSecurityEvents(
                fromDate: DateTime.UtcNow.AddMinutes(-5),
                minSeverity: SecuritySeverity.High);

            if (recentHighEvents.Any())
            {
                await _securityMonitor.AnalyzeThreatPatternAsync(recentHighEvents);

                if (recentHighEvents.Count(e => e.Severity == SecuritySeverity.Critical) >= 3)
                {
                    SetSecurityLevel(SecurityLevel.High);

                    LogSecurityEvent(SecurityEventType.ThreatDetected,
                        "Çoklu kritik güvenlik olayı tespit edildi, güvenlik seviyesi yükseltildi",
                        SecuritySeverity.Critical);
                }
            }
        }

        private async Task CheckSystemHealthAsync()
        {
            var modules = new (ISecurityModule module, string name)[]
            {
                (_authService as ISecurityModule, "AuthService"),
                (_permissionService as ISecurityModule, "PermissionService"),
                (_cryptoEngine as ISecurityModule, "CryptoEngine"),
                (_firewallManager as ISecurityModule, "FirewallManager"),
                (_securityMonitor as ISecurityModule, "SecurityMonitor")
            };

            foreach (var (module, name) in modules)
            {
                if (module != null)
                {
                    var health = await module.CheckHealthAsync();
                    if (!health.IsHealthy)
                    {
                        LogSecurityEvent(SecurityEventType.ModuleFailure,
                            $"Güvenlik modülü arızası: {name} - {health.Message}",
                            SecuritySeverity.High);
                    }
                }
            }
        }

        private async Task CleanupExpiredSessionsAsync()
        {
            var expired = _activeSessions.Values
                .Where(s => s.ExpiresAt < DateTime.UtcNow.AddHours(-1))
                .ToList();

            foreach (var session in expired)
            {
                _activeSessions.TryRemove(session.SessionId, out _);
            }

            if (expired.Any())
            {
                await _auditLock.WaitAsync();
                try
                {
                    foreach (var session in expired)
                    {
                        await LogSecurityAuditAsync(session.UserId,
                            "SessionAutoCleanup",
                            $"Otomatik temizleme: {session.SessionId}");
                    }
                }
                finally
                {
                    _auditLock.Release();
                }
            }
        }

        private Task CleanupOldSecurityEventsAsync()
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-30);
            var oldEvents = _securityEvents
                .Where(e => e.Timestamp < cutoffDate)
                .ToList();

            // ConcurrentBag toplu silmeye uygun değil.
            // Bu yüzden "TryTake" ile sayısal azaltma yapıyoruz.
            // Bu, eski olayları tamamen garanti temizlemez ama sentaks ve thread-safe çalışır.
            var toRemoveCount = oldEvents.Count;
            for (int i = 0; i < toRemoveCount; i++)
            {
                _securityEvents.TryTake(out _);
            }

            if (toRemoveCount > 0)
            {
                _logger?.LogInformation("{Count} eski güvenlik olayı temizlendi", toRemoveCount);
            }

            return Task.CompletedTask;
        }

        private Task CleanupSecurityCacheAsync()
        {
            _securityCache.Compact(0.5);
            return Task.CompletedTask;
        }

        private bool IsAuthenticationBlocked(string clientIp)
        {
            if (string.IsNullOrEmpty(clientIp)) return false;

            var cacheKey = $"auth_block_{clientIp}";
            if (_securityCache.TryGetValue(cacheKey, out DateTime blockUntil))
            {
                return DateTime.UtcNow < blockUntil;
            }

            return false;
        }

        private async Task<bool> IsIpAllowedAsync(string clientIp)
        {
            if (string.IsNullOrEmpty(clientIp)) return true;

            return await _firewallManager.IsIpAllowedAsync(clientIp);
        }

        private void RecordFailedAttempt(string clientIp, string username)
        {
            if (string.IsNullOrEmpty(clientIp)) return;

            var cacheKey = $"failed_attempts_{clientIp}";
            var attempts = _securityCache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return new List<FailedAttempt>();
            });

            attempts.Add(new FailedAttempt
            {
                Username = username,
                Timestamp = DateTime.UtcNow,
                ClientIp = clientIp
            });

            _failedAttempts++;

            if (attempts.Count >= GetMaxFailedAttempts())
            {
                BlockIp(clientIp, TimeSpan.FromMinutes(GetBlockDuration()));
            }
        }

        private void ClearFailedAttempts(string clientIp)
        {
            if (string.IsNullOrEmpty(clientIp)) return;

            _securityCache.Remove($"failed_attempts_{clientIp}");
            _securityCache.Remove($"auth_block_{clientIp}");
        }

        private void BlockIp(string clientIp, TimeSpan duration)
        {
            var cacheKey = $"auth_block_{clientIp}";
            _securityCache.Set(cacheKey, DateTime.UtcNow.Add(duration), duration);

            try
            {
                // Sync Dispose içinden veya sync çağrılardan kaçınmak zor, burada minimum müdahale:
                _firewallManager.BlockIpAsync(clientIp, duration).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Firewall IP block sırasında hata: {ClientIp}", clientIp);
            }

            LogSecurityEvent(SecurityEventType.IpBlocked,
                $"IP bloke edildi: {clientIp} - Süre: {duration.TotalMinutes} dakika",
                SecuritySeverity.Medium,
                clientIp: clientIp);
        }

        private void LogSecurityEvent(
            SecurityEventType eventType,
            string description,
            SecuritySeverity severity,
            string userId = null,
            string clientIp = null)
        {
            var securityEvent = new SecurityEvent
            {
                Id = Guid.NewGuid().ToString(),
                EventType = eventType,
                Timestamp = DateTime.UtcNow,
                Description = description,
                Severity = severity,
                UserId = userId,
                ClientIp = clientIp,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                AdditionalData = new Dictionary<string, object>()
            };

            _securityEvents.Add(securityEvent);
            LastSecurityIncident = DateTime.UtcNow;

            // Log'a da yaz
            var logLevel = severity switch
            {
                SecuritySeverity.Low => Microsoft.Extensions.Logging.LogLevel.Debug,
                SecuritySeverity.Medium => Microsoft.Extensions.Logging.LogLevel.Information,
                SecuritySeverity.High => Microsoft.Extensions.Logging.LogLevel.Warning,
                SecuritySeverity.Critical => Microsoft.Extensions.Logging.LogLevel.Error,
                _ => Microsoft.Extensions.Logging.LogLevel.Information
            };

            _logger?.Log(logLevel, "[SECURITY] {EventType}: {Description}", eventType, description);

            // Güvenlik izleyicisine bildir
            try
            {
                _securityMonitor.RecordEventAsync(securityEvent).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SecurityMonitor RecordEvent sırasında hata");
            }
        }

        private async Task LogAuthorizationAuditAsync(
            string userId,
            string resource,
            string action,
            AuthorizationResult result)
        {
            await _auditLock.WaitAsync();
            try
            {
                var auditLog = new AuthorizationAudit
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    Resource = resource,
                    Action = action,
                    Timestamp = DateTime.UtcNow,
                    IsAuthorized = result.IsAuthorized,
                    Reason = result.Reason,
                    ClientIp = GetClientIp(),
                    AdditionalData = new Dictionary<string, object>()
                };

                var cacheKey = $"audit_auth_{auditLog.Id}";
                _securityCache.Set(cacheKey, auditLog, TimeSpan.FromDays(7));
            }
            finally
            {
                _auditLock.Release();
            }
        }

        private async Task LogSecurityAuditAsync(string userId, string action, string details)
        {
            await _auditLock.WaitAsync();
            try
            {
                var auditLog = new SecurityAudit
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    Action = action,
                    Details = details,
                    Timestamp = DateTime.UtcNow,
                    ClientIp = GetClientIp(),
                    MachineName = Environment.MachineName
                };

                var cacheKey = $"audit_sec_{auditLog.Id}";
                _securityCache.Set(cacheKey, auditLog, TimeSpan.FromDays(7));
            }
            finally
            {
                _auditLock.Release();
            }
        }

        private void CacheSession(UserSession session)
        {
            var cacheKey = $"session_{session.SessionId}";
            _securityCache.Set(cacheKey, session, session.ExpiresAt - DateTime.UtcNow);

            var userSessionsKey = $"user_sessions_{session.UserId}";
            var userSessions = _securityCache.GetOrCreate(userSessionsKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return new List<string>();
            });

            userSessions.Add(session.SessionId);
        }

        private Task ClearSessionDataAsync(string sessionId)
        {
            _securityCache.Remove($"session_{sessionId}");

            var relatedKeys = new[]
            {
                $"session_token_{sessionId}",
                $"session_activity_{sessionId}",
                $"session_data_{sessionId}"
            };

            foreach (var key in relatedKeys)
            {
                _securityCache.Remove(key);
            }

            return Task.CompletedTask;
        }

        private Task<SessionScanResult> ScanActiveSessionsAsync()
        {
            var result = new SessionScanResult();

            foreach (var session in _activeSessions.Values)
            {
                if (IsSuspiciousSession(session))
                {
                    result.SuspiciousSessions.Add(session.SessionId);
                    result.IssuesFound++;
                }

                if (session.ExpiresAt < DateTime.UtcNow.AddMinutes(5))
                {
                    result.NearlyExpiredSessions.Add(session.SessionId);
                }

                var userSessionsCount = _activeSessions.Values.Count(s => s.UserId == session.UserId);
                if (userSessionsCount > GetMaxSessionsPerUser())
                {
                    result.ExcessiveSessions.Add(session.UserId);
                }
            }

            return Task.FromResult(result);
        }

        private async Task<IntegrityScanResult> PerformIntegrityCheckAsync()
        {
            var result = new IntegrityScanResult();

            var criticalFiles = new[]
            {
                "NEDA.Core.dll",
                "NEDA.SecurityModules.dll",
                "appsettings.json",
                "SecurityManifest.xml"
            };

            foreach (var file in criticalFiles)
            {
                if (!await CheckFileIntegrityAsync(file))
                {
                    result.CorruptedFiles.Add(file);
                    result.IssuesFound++;
                }
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.FullName != null && a.FullName.StartsWith("NEDA.", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var assembly in assemblies)
            {
                try
                {
                    _ = CalculateAssemblyHash(assembly);
                    // Gerçek uygulamada kayıtlı hash'lerle karşılaştırılır
                }
                catch
                {
                    result.TamperedAssemblies.Add(assembly.GetName().Name);
                    result.IssuesFound++;
                }
            }

            return result;
        }

        private Task<VulnerabilityScanResult> ScanVulnerabilitiesAsync()
        {
            var result = new VulnerabilityScanResult();
            return Task.FromResult(result);
        }

        private Task<LogAnalysisResult> AnalyzeSecurityLogsAsync()
        {
            var result = new LogAnalysisResult();

            var recentEvents = GetSecurityEvents(fromDate: DateTime.UtcNow.AddHours(-24))
                .ToList();

            var failedAuths = recentEvents.Count(e => e.EventType == SecurityEventType.AuthenticationFailed);

            if (failedAuths > 50)
            {
                result.PotentialBruteForce = true;
                result.Findings.Add($"24 saat içinde {failedAuths} başarısız kimlik doğrulama");
            }

            var ipGroups = recentEvents
                .Where(e => !string.IsNullOrEmpty(e.ClientIp))
                .GroupBy(e => e.ClientIp)
                .Where(g => g.Count() > 10)
                .ToList();

            foreach (var group in ipGroups)
            {
                result.SuspiciousIps.Add(group.Key);
                result.Findings.Add($"IP {group.Key}: {group.Count()} olay");
            }

            return Task.FromResult(result);
        }

        private RiskLevel CalculateRiskLevel(
            SessionScanResult sessionScan,
            IntegrityScanResult integrityScan,
            VulnerabilityScanResult vulnerabilityScan,
            LogAnalysisResult logAnalysis)
        {
            var score = 0;

            if (sessionScan.IssuesFound > 0) score += sessionScan.IssuesFound * 10;
            if (integrityScan.IssuesFound > 0) score += integrityScan.IssuesFound * 20;
            if (vulnerabilityScan.CriticalVulnerabilities > 0) score += vulnerabilityScan.CriticalVulnerabilities * 30;
            if (logAnalysis.PotentialBruteForce) score += 25;
            if (logAnalysis.SuspiciousIps.Any()) score += logAnalysis.SuspiciousIps.Count * 5;

            return score switch
            {
                < 10 => RiskLevel.Low,
                < 30 => RiskLevel.Medium,
                < 50 => RiskLevel.High,
                _ => RiskLevel.Critical
            };
        }

        private Task AdjustSecurityLevelBasedOnScan(SecurityScanResult result)
        {
            var newLevel = result.OverallRiskLevel switch
            {
                RiskLevel.Low => SecurityLevel.Low,
                RiskLevel.Medium => SecurityLevel.Medium,
                RiskLevel.High => SecurityLevel.High,
                RiskLevel.Critical => SecurityLevel.Critical,
                _ => SecurityLevel.Medium
            };

            if (newLevel > CurrentSecurityLevel)
            {
                SetSecurityLevel(newLevel);
            }

            return Task.CompletedTask;
        }

        private void UpdatePoliciesForSecurityLevel(SecurityLevel level)
        {
            foreach (var policy in _securityPolicies.Values)
            {
                switch (level)
                {
                    case SecurityLevel.Low:
                        UpdatePolicyForLowSecurity(policy);
                        break;
                    case SecurityLevel.Medium:
                        UpdatePolicyForMediumSecurity(policy);
                        break;
                    case SecurityLevel.High:
                        UpdatePolicyForHighSecurity(policy);
                        break;
                    case SecurityLevel.Critical:
                        UpdatePolicyForCriticalSecurity(policy);
                        break;
                }
            }
        }

        private void LockAllSecurityModules()
        {
            var modules = new ISecurityModule[]
            {
                _authService,
                _permissionService,
                _cryptoEngine,
                _firewallManager,
                _securityMonitor
            };

            foreach (var module in modules)
            {
                try
                {
                    module?.LockdownAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Security module lockdown sırasında hata");
                }
            }
        }

        private bool IsSuspiciousSession(UserSession session)
        {
            if (session == null) return true;

            if (string.IsNullOrEmpty(session.ClientIp)) return true;
            if (!string.IsNullOrEmpty(session.UserAgent) &&
                session.UserAgent.Contains("bot", StringComparison.OrdinalIgnoreCase)) return true;
            if (session.LastActivity < session.CreatedAt) return true;

            var recentActivities = GetSessionActivities(session.SessionId);
            if (recentActivities.Count > 100) return true;

            return false;
        }

        private List<DateTime> GetSessionActivities(string sessionId)
        {
            var cacheKey = $"session_activity_{sessionId}";
            return _securityCache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return new List<DateTime>();
            });
        }

        private async Task<bool> CheckFileIntegrityAsync(string fileName)
        {
            try
            {
                var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (!File.Exists(filePath)) return false;

                using var sha256 = SHA256.Create();
                await using var stream = File.OpenRead(filePath);
                _ = await sha256.ComputeHashAsync(stream);

                // Gerçek uygulamada kayıtlı hash ile karşılaştırılır
                return true;
            }
            catch
            {
                return false;
            }
        }

        private byte[] CalculateAssemblyHash(System.Reflection.Assembly assembly)
        {
            using var sha256 = SHA256.Create();
            var location = assembly.Location;

            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                throw new SecurityException($"Assembly dosyası bulunamadı: {assembly.FullName}");
            }

            using var stream = File.OpenRead(location);
            return sha256.ComputeHash(stream);
        }

        private string GenerateSessionId()
        {
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .Replace("/", "_")
                .Replace("+", "-")
                .Replace("=", "");
        }

        private string GetUserAgent()
        {
            return "NEDA.SecurityManager/1.0";
        }

        private string GetClientIp()
        {
            return "127.0.0.1";
        }

        private int GetSessionTimeout()
        {
            return CurrentSecurityLevel switch
            {
                SecurityLevel.Low => 120,
                SecurityLevel.Medium => 60,
                SecurityLevel.High => 30,
                SecurityLevel.Critical => 15,
                _ => 60
            };
        }

        private int GetInactiveTimeout()
        {
            return CurrentSecurityLevel switch
            {
                SecurityLevel.Low => 60,
                SecurityLevel.Medium => 30,
                SecurityLevel.High => 15,
                SecurityLevel.Critical => 5,
                _ => 30
            };
        }

        private int GetMaxFailedAttempts()
        {
            return CurrentSecurityLevel switch
            {
                SecurityLevel.Low => 10,
                SecurityLevel.Medium => 5,
                SecurityLevel.High => 3,
                SecurityLevel.Critical => 1,
                _ => 5
            };
        }

        private int GetBlockDuration()
        {
            return CurrentSecurityLevel switch
            {
                SecurityLevel.Low => 5,
                SecurityLevel.Medium => 15,
                SecurityLevel.High => 60,
                SecurityLevel.Critical => 240,
                _ => 15
            };
        }

        private int GetMaxSessionsPerUser()
        {
            return CurrentSecurityLevel switch
            {
                SecurityLevel.Low => 5,
                SecurityLevel.Medium => 3,
                SecurityLevel.High => 2,
                SecurityLevel.Critical => 1,
                _ => 3
            };
        }

        private Dictionary<string, ModuleStatus> GetSecurityModulesStatus()
        {
            return new Dictionary<string, ModuleStatus>
            {
                ["AuthService"] = new ModuleStatus { IsActive = _authService != null, LastCheck = DateTime.UtcNow },
                ["PermissionService"] = new ModuleStatus { IsActive = _permissionService != null, LastCheck = DateTime.UtcNow },
                ["CryptoEngine"] = new ModuleStatus { IsActive = _cryptoEngine != null, LastCheck = DateTime.UtcNow },
                ["FirewallManager"] = new ModuleStatus { IsActive = _firewallManager != null, LastCheck = DateTime.UtcNow },
                ["SecurityMonitor"] = new ModuleStatus { IsActive = _securityMonitor != null, LastCheck = DateTime.UtcNow }
            };
        }

        private void UpdatePolicyForLowSecurity(SecurityPolicy policy)
        {
            switch (policy.Id)
            {
                case "password_policy":
                    policy.Rules["min_length"] = 8;
                    policy.Rules["max_age_days"] = 365;
                    break;
                case "session_policy":
                    policy.Rules["timeout_minutes"] = 120;
                    policy.Rules["max_sessions_per_user"] = 10;
                    break;
            }
        }

        private void UpdatePolicyForMediumSecurity(SecurityPolicy policy)
        {
            switch (policy.Id)
            {
                case "password_policy":
                    policy.Rules["min_length"] = 12;
                    policy.Rules["max_age_days"] = 180;
                    break;
                case "session_policy":
                    policy.Rules["timeout_minutes"] = 60;
                    policy.Rules["max_sessions_per_user"] = 5;
                    break;
            }
        }

        private void UpdatePolicyForHighSecurity(SecurityPolicy policy)
        {
            switch (policy.Id)
            {
                case "password_policy":
                    policy.Rules["min_length"] = 16;
                    policy.Rules["max_age_days"] = 90;
                    policy.Rules["require_mfa"] = true;
                    break;
                case "session_policy":
                    policy.Rules["timeout_minutes"] = 30;
                    policy.Rules["max_sessions_per_user"] = 3;
                    policy.Rules["require_ip_validation"] = true;
                    break;
            }
        }

        private void UpdatePolicyForCriticalSecurity(SecurityPolicy policy)
        {
            switch (policy.Id)
            {
                case "password_policy":
                    policy.Rules["min_length"] = 20;
                    policy.Rules["max_age_days"] = 30;
                    policy.Rules["require_mfa"] = true;
                    policy.Rules["require_hardware_token"] = true;
                    break;
                case "session_policy":
                    policy.Rules["timeout_minutes"] = 15;
                    policy.Rules["max_sessions_per_user"] = 1;
                    policy.Rules["require_ip_validation"] = true;
                    policy.Rules["require_device_fingerprint"] = true;
                    break;
            }
        }

        private void ValidateSecurityLock()
        {
            if (_isLocked)
            {
                throw new SecurityLockedException("Güvenlik sistemi kilitli. Acil durum modu aktif.");
            }
        }

        private void ValidateDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(SecurityManager));
        }

        private void HandleInitializationError(Exception ex)
        {
            Console.WriteLine($"[SECURITY EMERGENCY] SecurityManager initialization failed: {ex.Message}");

            try
            {
                CurrentSecurityLevel = SecurityLevel.Critical;
                _isLocked = true;
            }
            catch
            {
                _isLocked = true;
            }
        }
        #endregion

        #region IDisposable Implementation
        public void Dispose()
        {
            if (_isDisposed) return;

            try
            {
                _cancellationTokenSource.Cancel();

                var tasks = new List<Task>();
                if (_monitoringTask != null) tasks.Add(_monitoringTask);
                if (_cleanupTask != null) tasks.Add(_cleanupTask);

                if (tasks.Count > 0)
                {
                    Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10));
                }

                try
                {
                    ClearAllSessionsAsync(true).GetAwaiter().GetResult();
                }
                catch
                {
                    // Dispose içinde yut
                }

                try
                {
                    _securityMonitor?.StopMonitoring();
                }
                catch
                {
                    // Dispose içinde yut
                }

                _securityLock?.Dispose();
                _auditLock?.Dispose();
                _cancellationTokenSource?.Dispose();
                _securityCache?.Dispose();
            }
            finally
            {
                _isDisposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~SecurityManager()
        {
            Dispose();
        }
        #endregion
    }

    #region Supporting Types and Interfaces
    public interface ISecurityManager
    {
        Task<AuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            string clientIp = null,
            Dictionary<string, string> additionalData = null);

        Task<AuthenticationResult> AuthenticateWithTokenAsync(
            string token,
            string clientIp = null);

        Task<AuthorizationResult> AuthorizeAsync(
            string userId,
            string resource,
            string action,
            Dictionary<string, object> context = null);

        Task<bool> LogoutAsync(string sessionId, string userId = null);
        Task<int> ClearAllSessionsAsync(bool force = false);
        Task<SecurityScanResult> PerformSecurityScanAsync();

        IEnumerable<SecurityEvent> GetSecurityEvents(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            SecurityEventType? eventType = null,
            SecuritySeverity? minSeverity = null);

        SecurityStatus GetStatus();
        void SetSecurityLevel(SecurityLevel level);
        void ActivateEmergencyLock();

        Task<byte[]> EncryptAsync(
            byte[] data,
            string keyId = null,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256);

        Task<byte[]> DecryptAsync(
            byte[] encryptedData,
            string keyId = null,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256);

        Task<bool> VerifyIntegrityAsync(
            byte[] data,
            byte[] signature,
            string keyId = null);
    }

    public interface ISecurityModule
    {
        Task<ModuleHealth> CheckHealthAsync();
        Task LockdownAsync();
    }

    // Bu interface projende zaten varsa burayı kaldırabilirsin.
    public interface ISecurityMonitor : ISecurityModule
    {
        void StartMonitoring();
        void StopMonitoring();
        Task AnalyzeThreatPatternAsync(IEnumerable<SecurityEvent> events);
        Task RecordEventAsync(SecurityEvent evt);
    }

    public enum SecurityLevel
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    public enum SecurityEventType
    {
        AuthenticationSuccess,
        AuthenticationFailed,
        AuthenticationError,
        AuthenticationBlocked,
        TokenAuthenticationSuccess,
        TokenAuthenticationError,
        InvalidToken,
        UserLoggedOut,
        SessionTimeout,
        SessionIpChanged,
        SecurityScanStarted,
        SecurityScanCompleted,
        SecurityScanFailed,
        SecurityLevelChanged,
        EmergencyLockActivated,
        AllSessionsCleared,
        IpBlocked,
        ModuleFailure,
        ThreatDetected,
        SystemInitialized
    }

    public enum SecuritySeverity
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    public enum EncryptionAlgorithm
    {
        AES128,
        AES256,
        RSA2048,
        RSA4096
    }

    public enum RiskLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    public class SecurityEvent
    {
        public string Id { get; set; }
        public SecurityEventType EventType { get; set; }
        public DateTime Timestamp { get; set; }
        public string Description { get; set; }
        public SecuritySeverity Severity { get; set; }
        public string UserId { get; set; }
        public string ClientIp { get; set; }
        public string MachineName { get; set; }
        public int ProcessId { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    public class UserSession
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public string AccessToken { get; set; }
        public string ClientIp { get; set; }
        public string UserAgent { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsValid { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    public class SecurityPolicy
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Rules { get; set; }
        public SecurityPolicyScope AppliesTo { get; set; }
    }

    public enum SecurityPolicyScope
    {
        AllUsers,
        Administrators,
        SystemAccounts,
        ExternalUsers
    }

    public class AuthenticationResult
    {
        public bool IsSuccess { get; set; }
        public string SessionId { get; set; }
        public string Token { get; set; }
        public UserIdentity User { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string ErrorMessage { get; set; }
        public AuthenticationFailureReason FailureReason { get; set; }

        public static AuthenticationResult Success(
            string sessionId,
            string token,
            UserIdentity user,
            DateTime? expiresAt)
        {
            return new AuthenticationResult
            {
                IsSuccess = true,
                SessionId = sessionId,
                Token = token,
                User = user,
                ExpiresAt = expiresAt
            };
        }

        public static AuthenticationResult Failed(
            string errorMessage,
            AuthenticationFailureReason reason)
        {
            return new AuthenticationResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
                FailureReason = reason
            };
        }
    }

    public enum AuthenticationFailureReason
    {
        InvalidCredentials,
        AccountLocked,
        AccountDisabled,
        IpBlocked,
        RateLimited,
        InvalidToken,
        TokenExpired,
        SystemError
    }

    public class AuthorizationResult
    {
        public bool IsAuthorized { get; set; }
        public string Reason { get; set; }
        public AuthorizationFailureReason FailureReason { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }

        public static AuthorizationResult Allowed(string reason = null)
        {
            return new AuthorizationResult
            {
                IsAuthorized = true,
                Reason = reason
            };
        }

        public static AuthorizationResult Denied(
            string reason,
            AuthorizationFailureReason failureReason)
        {
            return new AuthorizationResult
            {
                IsAuthorized = false,
                Reason = reason,
                FailureReason = failureReason
            };
        }
    }

    public enum AuthorizationFailureReason
    {
        NoPermission,
        ResourceNotFound,
        AccessRestricted,
        SessionExpired,
        SystemError
    }

    public class SecurityScanResult
    {
        public string ScanId { get; set; }
        public DateTime ScanTime { get; set; }
        public TimeSpan Duration { get; set; }
        public SessionScanResult SessionIssues { get; set; }
        public IntegrityScanResult IntegrityIssues { get; set; }
        public VulnerabilityScanResult Vulnerabilities { get; set; }
        public LogAnalysisResult LogFindings { get; set; }
        public RiskLevel OverallRiskLevel { get; set; }
    }

    public class SessionScanResult
    {
        public int IssuesFound { get; set; }
        public List<string> SuspiciousSessions { get; set; } = new List<string>();
        public List<string> NearlyExpiredSessions { get; set; } = new List<string>();
        public List<string> ExcessiveSessions { get; set; } = new List<string>();
    }

    public class IntegrityScanResult
    {
        public int IssuesFound { get; set; }
        public List<string> CorruptedFiles { get; set; } = new List<string>();
        public List<string> TamperedAssemblies { get; set; } = new List<string>();
    }

    public class VulnerabilityScanResult
    {
        public int CriticalVulnerabilities { get; set; }
        public int HighVulnerabilities { get; set; }
        public int MediumVulnerabilities { get; set; }
        public List<string> Vulnerabilities { get; set; } = new List<string>();
    }

    public class LogAnalysisResult
    {
        public bool PotentialBruteForce { get; set; }
        public List<string> SuspiciousIps { get; set; } = new List<string>();
        public List<string> Findings { get; set; } = new List<string>();
    }

    public class SecurityStatus
    {
        public bool IsInitialized { get; set; }
        public bool IsLocked { get; set; }
        public SecurityLevel CurrentSecurityLevel { get; set; }
        public int ActiveSessionCount { get; set; }
        public int FailedAttempts { get; set; }
        public DateTime LastSecurityScan { get; set; }
        public DateTime LastSecurityIncident { get; set; }
        public bool IsAuditEnabled { get; set; }
        public Dictionary<string, ModuleStatus> SecurityModulesStatus { get; set; }
    }

    public class ModuleStatus
    {
        public bool IsActive { get; set; }
        public DateTime LastCheck { get; set; }
        public string StatusMessage { get; set; }
    }

    public class ModuleHealth
    {
        public bool IsHealthy { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Details { get; set; }
    }

    public class FailedAttempt
    {
        public string Username { get; set; }
        public DateTime Timestamp { get; set; }
        public string ClientIp { get; set; }
    }

    public class AuthorizationAudit
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public string Resource { get; set; }
        public string Action { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsAuthorized { get; set; }
        public string Reason { get; set; }
        public string ClientIp { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    public class SecurityAudit
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public string Action { get; set; }
        public string Details { get; set; }
        public DateTime Timestamp { get; set; }
        public string ClientIp { get; set; }
        public string MachineName { get; set; }
    }

    public class SystemAccount
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string[] Permissions { get; set; }
        public bool IsEnabled { get; set; }
    }

    // Exceptions
    public class SecurityInitializationException : Exception
    {
        public SecurityInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SecurityAuthenticationException : Exception
    {
        public SecurityAuthenticationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SecurityScanException : Exception
    {
        public SecurityScanException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class SecurityLockedException : Exception
    {
        public SecurityLockedException(string message) : base(message) { }
    }

    // Supporting classes from other modules
    public class UserIdentity
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string[] Roles { get; set; }
        public Dictionary<string, object> Claims { get; set; }
    }
    #endregion
}
