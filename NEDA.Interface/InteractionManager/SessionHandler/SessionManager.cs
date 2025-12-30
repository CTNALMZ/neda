using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.UserProfiles;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Interface.InteractionManager.UserProfileManager;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.InteractionManager.SessionHandler;
{
    /// <summary>
    /// Kullanıcı oturumu;
    /// </summary>
    public class UserSession;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string DeviceId { get; set; }
        public string ClientInfo { get; set; }
        public string IPAddress { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public SessionStatus Status { get; set; }
        public Dictionary<string, object> SessionData { get; set; }
        public Dictionary<string, object> Tokens { get; set; }
        public List<string> Permissions { get; set; }
        public Dictionary<string, object> Context { get; set; }
        public int ActivityCount { get; set; }
        public TimeSpan IdleTimeout { get; set; }
        public TimeSpan AbsoluteTimeout { get; set; }

        public UserSession()
        {
            SessionData = new Dictionary<string, object>();
            Tokens = new Dictionary<string, object>();
            Permissions = new List<string>();
            Context = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
            LastActivity = DateTime.UtcNow;
            Status = SessionStatus.Active;
            ActivityCount = 0;
            IdleTimeout = TimeSpan.FromMinutes(30);
            AbsoluteTimeout = TimeSpan.FromHours(8);
            ExpiresAt = DateTime.UtcNow.Add(AbsoluteTimeout);
        }

        /// <summary>
        /// Oturumun geçerli olup olmadığını kontrol eder;
        /// </summary>
        public bool IsValid()
        {
            if (Status != SessionStatus.Active)
                return false;

            if (ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value)
                return false;

            var idleTime = DateTime.UtcNow - LastActivity;
            if (idleTime > IdleTimeout)
                return false;

            return true;
        }

        /// <summary>
        /// Kalan süreyi hesaplar;
        /// </summary>
        public TimeSpan GetRemainingTime()
        {
            var absoluteRemaining = ExpiresAt.HasValue ?
                ExpiresAt.Value - DateTime.UtcNow : TimeSpan.Zero;

            var idleRemaining = IdleTimeout - (DateTime.UtcNow - LastActivity);

            return absoluteRemaining < idleRemaining ? absoluteRemaining : idleRemaining;
        }

        /// <summary>
        /// Aktiviteyi kaydeder;
        /// </summary>
        public void RecordActivity()
        {
            LastActivity = DateTime.UtcNow;
            ActivityCount++;
        }
    }

    /// <summary>
    /// Oturum durumları;
    /// </summary>
    public enum SessionStatus;
    {
        Active,
        Inactive,
        Suspended,
        Expired,
        Terminated,
        Locked;
    }

    /// <summary>
    /// Oturum filtreleri;
    /// </summary>
    public class SessionFilter;
    {
        public string UserId { get; set; }
        public string DeviceId { get; set; }
        public string IPAddress { get; set; }
        public SessionStatus? Status { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
        public DateTime? ActiveAfter { get; set; }
        public DateTime? ActiveBefore { get; set; }
        public int? MinActivityCount { get; set; }
        public bool? IncludeExpired { get; set; }
    }

    /// <summary>
    /// Oturum yöneticisi arayüzü;
    /// </summary>
    public interface ISessionManager : IDisposable
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<UserSession> CreateSessionAsync(string userId, Dictionary<string, object> sessionData,
            CancellationToken cancellationToken = default);
        Task<UserSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<UserSession> ValidateSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<bool> UpdateSessionAsync(string sessionId, Dictionary<string, object> updates,
            CancellationToken cancellationToken = default);
        Task<bool> TerminateSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<bool> TerminateAllUserSessionsAsync(string userId, CancellationToken cancellationToken = default);
        Task<List<UserSession>> GetUserSessionsAsync(string userId, CancellationToken cancellationToken = default);
        Task<List<UserSession>> GetSessionsAsync(SessionFilter filter, CancellationToken cancellationToken = default);
        Task<int> CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default);
        Task<bool> RenewSessionAsync(string sessionId, TimeSpan extension,
            CancellationToken cancellationToken = default);
        Task<UserSession> FindSessionByTokenAsync(string tokenType, string tokenValue,
            CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GetSessionStatisticsAsync(CancellationToken cancellationToken = default);
        Task<bool> LockSessionAsync(string sessionId, string reason, CancellationToken cancellationToken = default);
        Task<bool> UnlockSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<bool> MigrateSessionAsync(string oldSessionId, string newDeviceId,
            CancellationToken cancellationToken = default);
        event EventHandler<SessionEventArgs> SessionCreated;
        event EventHandler<SessionEventArgs> SessionTerminated;
        event EventHandler<SessionEventArgs> SessionUpdated;
    }

    /// <summary>
    /// Oturum event argümanları;
    /// </summary>
    public class SessionEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public SessionAction Action { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public SessionEventArgs(string sessionId, string userId, SessionAction action)
        {
            SessionId = sessionId;
            UserId = userId;
            Action = action;
            Timestamp = DateTime.UtcNow;
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Oturum aksiyonları;
    /// </summary>
    public enum SessionAction;
    {
        Created,
        Updated,
        Terminated,
        Renewed,
        Locked,
        Unlocked,
        Expired,
        Migrated;
    }

    /// <summary>
    /// Oturum yöneticisi implementasyonu;
    /// </summary>
    public class SessionManager : ISessionManager;
    {
        private readonly ILogger<SessionManager> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly AppConfig _appConfig;
        private readonly ISecurityManager _securityManager;
        private readonly IProfileManager _profileManager;

        private readonly ConcurrentDictionary<string, UserSession> _activeSessions;
        private readonly ConcurrentDictionary<string, List<string>> _userSessionIndex;
        private readonly ConcurrentDictionary<string, string> _tokenSessionIndex;

        private readonly SemaphoreSlim _sessionLock;
        private readonly Timer _cleanupTimer;
        private readonly Timer _monitoringTimer;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _syncLock = new object();

        // Varsayılan timeout değerleri;
        private readonly TimeSpan _defaultIdleTimeout = TimeSpan.FromMinutes(30);
        private readonly TimeSpan _defaultAbsoluteTimeout = TimeSpan.FromHours(8);
        private readonly int _maxSessionsPerUser = 5;

        /// <summary>
        /// Oturum oluşturuldu event'i;
        /// </summary>
        public event EventHandler<SessionEventArgs> SessionCreated;

        /// <summary>
        /// Oturum sonlandırıldı event'i;
        /// </summary>
        public event EventHandler<SessionEventArgs> SessionTerminated;

        /// <summary>
        /// Oturum güncellendi event'i;
        /// </summary>
        public event EventHandler<SessionEventArgs> SessionUpdated;

        /// <summary>
        /// SessionManager constructor;
        /// </summary>
        public SessionManager(
            ILogger<SessionManager> logger,
            IOptions<AppConfig> appConfig,
            IExceptionHandler exceptionHandler,
            ISecurityManager securityManager,
            IProfileManager profileManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig?.Value ?? throw new ArgumentNullException(nameof(appConfig));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));

            _activeSessions = new ConcurrentDictionary<string, UserSession>();
            _userSessionIndex = new ConcurrentDictionary<string, List<string>>();
            _tokenSessionIndex = new ConcurrentDictionary<string, string>();

            _sessionLock = new SemaphoreSlim(1, 1);

            // Her 5 dakikada bir süresi dolan oturumları temizle;
            _cleanupTimer = new Timer(CleanupExpiredSessionsCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // Her dakika oturumları izle;
            _monitoringTimer = new Timer(MonitorSessionsCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// SessionManager'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("SessionManager başlatılıyor...");

                if (_isInitialized)
                {
                    _logger.LogWarning("SessionManager zaten başlatılmış");
                    return;
                }

                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    // Önceden kaydedilmiş oturumları yükle;
                    await LoadPersistentSessionsAsync(cancellationToken);

                    _isInitialized = true;
                    _logger.LogInformation("SessionManager başarıyla başlatıldı. Aktif oturum: {SessionCount}",
                        _activeSessions.Count);
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SessionManager başlatılırken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.InitializeAsync");
                throw;
            }
        }

        /// <summary>
        /// Yeni oturum oluşturur;
        /// </summary>
        public async Task<UserSession> CreateSessionAsync(string userId, Dictionary<string, object> sessionData,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    // Kullanıcı profilini doğrula;
                    var profile = await _profileManager.GetProfileAsync(userId, cancellationToken);
                    if (profile == null)
                        throw new InvalidOperationException($"Geçersiz kullanıcı: {userId}");

                    // Kullanıcının maksimum oturum sayısını kontrol et;
                    var userSessions = await GetUserSessionsAsync(userId, cancellationToken);
                    var activeUserSessions = userSessions.Where(s => s.Status == SessionStatus.Active).ToList();

                    if (activeUserSessions.Count >= _maxSessionsPerUser)
                    {
                        // En eski oturumu sonlandır;
                        var oldestSession = activeUserSessions.OrderBy(s => s.CreatedAt).First();
                        await TerminateSessionAsync(oldestSession.SessionId, cancellationToken);

                        _logger.LogWarning("Kullanıcı maksimum oturum sınırına ulaştı. Eski oturum sonlandırıldı. UserId: {UserId}, SessionId: {SessionId}",
                            userId, oldestSession.SessionId);
                    }

                    // Yeni oturum oluştur;
                    var sessionId = GenerateSessionId();
                    var session = new UserSession;
                    {
                        SessionId = sessionId,
                        UserId = userId,
                        DeviceId = sessionData?.ContainsKey("DeviceId") == true ? sessionData["DeviceId"].ToString() : "Unknown",
                        ClientInfo = sessionData?.ContainsKey("ClientInfo") == true ? sessionData["ClientInfo"].ToString() : "Unknown",
                        IPAddress = sessionData?.ContainsKey("IPAddress") == true ? sessionData["IPAddress"].ToString() : "0.0.0.0",
                        Status = SessionStatus.Active,
                        CreatedAt = DateTime.UtcNow,
                        LastActivity = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.Add(_defaultAbsoluteTimeout),
                        IdleTimeout = _defaultIdleTimeout,
                        AbsoluteTimeout = _defaultAbsoluteTimeout;
                    };

                    // SessionData'yı ekle;
                    if (sessionData != null)
                    {
                        foreach (var kvp in sessionData)
                        {
                            session.SessionData[kvp.Key] = kvp.Value;
                        }
                    }

                    // Güvenlik token'ları oluştur;
                    await GenerateSessionTokensAsync(session, cancellationToken);

                    // Cache'e ekle;
                    _activeSessions[sessionId] = session;

                    // İndeksleri güncelle;
                    UpdateIndexes(session, true);

                    // Event tetikle;
                    OnSessionCreated(new SessionEventArgs(sessionId, userId, SessionAction.Created)
                    {
                        Metadata = new Dictionary<string, object>
                        {
                            ["DeviceId"] = session.DeviceId,
                            ["IPAddress"] = session.IPAddress,
                            ["ClientInfo"] = session.ClientInfo;
                        }
                    });

                    _logger.LogInformation("Yeni oturum oluşturuldu. SessionId: {SessionId}, UserId: {UserId}, Device: {DeviceId}",
                        sessionId, userId, session.DeviceId);

                    return session;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum oluşturulurken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.CreateSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturumu getirir;
        /// </summary>
        public async Task<UserSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            try
            {
                // Cache'ten getir;
                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    // Aktiviteyi kaydet;
                    session.RecordActivity();
                    return session;
                }

                // Persistent storage'dan yükle;
                var loadedSession = await LoadSessionFromStorageAsync(sessionId, cancellationToken);
                if (loadedSession != null && loadedSession.IsValid())
                {
                    // Cache'e ekle;
                    _activeSessions[sessionId] = loadedSession;
                    UpdateIndexes(loadedSession, true);

                    loadedSession.RecordActivity();
                    return loadedSession;
                }

                _logger.LogWarning("Oturum bulunamadı veya geçersiz. SessionId: {SessionId}", sessionId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum getirilirken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.GetSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturumu doğrular;
        /// </summary>
        public async Task<UserSession> ValidateSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            try
            {
                var session = await GetSessionAsync(sessionId, cancellationToken);
                if (session == null)
                {
                    _logger.LogWarning("Oturum doğrulama başarısız: Oturum bulunamadı. SessionId: {SessionId}", sessionId);
                    return null;
                }

                // Geçerlilik kontrolü;
                if (!session.IsValid())
                {
                    _logger.LogWarning("Oturum doğrulama başarısız: Oturum geçersiz. SessionId: {SessionId}, Status: {Status}",
                        sessionId, session.Status);

                    // Geçersiz oturumu temizle;
                    await CleanupInvalidSessionAsync(sessionId, cancellationToken);
                    return null;
                }

                // Güvenlik kontrolleri;
                var securityCheck = await _securityManager.ValidateSessionSecurityAsync(session, cancellationToken);
                if (!securityCheck)
                {
                    _logger.LogWarning("Oturum doğrulama başarısız: Güvenlik kontrolünden geçemedi. SessionId: {SessionId}", sessionId);

                    // Güvenlik ihlali nedeniyle oturumu kilitle;
                    await LockSessionAsync(sessionId, "Security validation failed", cancellationToken);
                    return null;
                }

                // Aktiviteyi güncelle;
                session.RecordActivity();
                await UpdateSessionInStorageAsync(session, cancellationToken);

                _logger.LogDebug("Oturum başarıyla doğrulandı. SessionId: {SessionId}, UserId: {UserId}, Remaining: {RemainingTime}",
                    sessionId, session.UserId, session.GetRemainingTime());

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum doğrulanırken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.ValidateSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturumu günceller;
        /// </summary>
        public async Task<bool> UpdateSessionAsync(string sessionId, Dictionary<string, object> updates,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            if (updates == null || !updates.Any())
                return false;

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    var session = await GetSessionAsync(sessionId, cancellationToken);
                    if (session == null || !session.IsValid())
                    {
                        _logger.LogWarning("Güncellenecek oturum bulunamadı veya geçersiz. SessionId: {SessionId}", sessionId);
                        return false;
                    }

                    bool updated = false;

                    // SessionData'yı güncelle;
                    foreach (var kvp in updates)
                    {
                        if (session.SessionData.ContainsKey(kvp.Key))
                        {
                            session.SessionData[kvp.Key] = kvp.Value;
                        }
                        else;
                        {
                            session.SessionData.Add(kvp.Key, kvp.Value);
                        }
                        updated = true;
                    }

                    if (updated)
                    {
                        // Aktiviteyi kaydet;
                        session.RecordActivity();

                        // Cache'i güncelle;
                        _activeSessions[sessionId] = session;

                        // Storage'a kaydet;
                        await UpdateSessionInStorageAsync(session, cancellationToken);

                        // Event tetikle;
                        OnSessionUpdated(new SessionEventArgs(sessionId, session.UserId, SessionAction.Updated)
                        {
                            Metadata = new Dictionary<string, object>(updates)
                        });

                        _logger.LogDebug("Oturum güncellendi. SessionId: {SessionId}, UpdateCount: {UpdateCount}",
                            sessionId, updates.Count);
                    }

                    return updated;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum güncellenirken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.UpdateSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturumu sonlandırır;
        /// </summary>
        public async Task<bool> TerminateSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    if (!_activeSessions.TryRemove(sessionId, out var session))
                    {
                        // Storage'dan yükle;
                        session = await LoadSessionFromStorageAsync(sessionId, cancellationToken);
                        if (session == null)
                        {
                            _logger.LogWarning("Sonlandırılacak oturum bulunamadı. SessionId: {SessionId}", sessionId);
                            return false;
                        }
                    }

                    // Durumu güncelle;
                    session.Status = SessionStatus.Terminated;
                    session.ExpiresAt = DateTime.UtcNow;

                    // İndekslerden kaldır;
                    RemoveFromIndexes(session);

                    // Storage'a kaydet;
                    await UpdateSessionInStorageAsync(session, cancellationToken);

                    // Event tetikle;
                    OnSessionTerminated(new SessionEventArgs(sessionId, session.UserId, SessionAction.Terminated)
                    {
                        Metadata = new Dictionary<string, object>
                        {
                            ["TerminationTime"] = DateTime.UtcNow,
                            ["SessionDuration"] = DateTime.UtcNow - session.CreatedAt,
                            ["ActivityCount"] = session.ActivityCount;
                        }
                    });

                    _logger.LogInformation("Oturum sonlandırıldı. SessionId: {SessionId}, UserId: {UserId}, Duration: {Duration}, Activities: {ActivityCount}",
                        sessionId, session.UserId, DateTime.UtcNow - session.CreatedAt, session.ActivityCount);

                    return true;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum sonlandırılırken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.TerminateSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcının tüm oturumlarını sonlandırır;
        /// </summary>
        public async Task<bool> TerminateAllUserSessionsAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                var userSessions = await GetUserSessionsAsync(userId, cancellationToken);
                var activeSessions = userSessions.Where(s => s.Status == SessionStatus.Active).ToList();

                if (!activeSessions.Any())
                {
                    _logger.LogInformation("Kullanıcının aktif oturumu bulunmuyor. UserId: {UserId}", userId);
                    return true;
                }

                int terminatedCount = 0;
                foreach (var session in activeSessions)
                {
                    if (await TerminateSessionAsync(session.SessionId, cancellationToken))
                    {
                        terminatedCount++;
                    }
                }

                _logger.LogInformation("Kullanıcının tüm oturumları sonlandırıldı. UserId: {UserId}, Terminated: {TerminatedCount}",
                    userId, terminatedCount);

                return terminatedCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı oturumları sonlandırılırken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.TerminateAllUserSessionsAsync");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcıya ait oturumları getirir;
        /// </summary>
        public async Task<List<UserSession>> GetUserSessionsAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                var sessions = new List<UserSession>();

                // Cache'ten getir;
                if (_userSessionIndex.TryGetValue(userId, out var sessionIds))
                {
                    foreach (var sessionId in sessionIds)
                    {
                        if (_activeSessions.TryGetValue(sessionId, out var session))
                        {
                            sessions.Add(session);
                        }
                    }
                }

                // Storage'dan yükle;
                var storedSessions = await LoadUserSessionsFromStorageAsync(userId, cancellationToken);
                foreach (var storedSession in storedSessions)
                {
                    // Cache'te yoksa ekle;
                    if (!_activeSessions.ContainsKey(storedSession.SessionId))
                    {
                        _activeSessions[storedSession.SessionId] = storedSession;
                        UpdateIndexes(storedSession, false);
                    }

                    // Listeye ekle (henüz eklenmediyse)
                    if (!sessions.Any(s => s.SessionId == storedSession.SessionId))
                    {
                        sessions.Add(storedSession);
                    }
                }

                return sessions.OrderByDescending(s => s.LastActivity).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı oturumları getirilirken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.GetUserSessionsAsync");
                throw;
            }
        }

        /// <summary>
        /// Filtrelenmiş oturumları getirir;
        /// </summary>
        public async Task<List<UserSession>> GetSessionsAsync(SessionFilter filter, CancellationToken cancellationToken = default)
        {
            if (filter == null)
                filter = new SessionFilter();

            try
            {
                var allSessions = new List<UserSession>();

                // Cache'teki tüm oturumları getir;
                foreach (var session in _activeSessions.Values)
                {
                    allSessions.Add(session);
                }

                // Storage'dan ek oturumları yükle (eğer filtre varsa)
                if (!string.IsNullOrEmpty(filter.UserId))
                {
                    var storedSessions = await LoadUserSessionsFromStorageAsync(filter.UserId, cancellationToken);
                    foreach (var storedSession in storedSessions)
                    {
                        if (!allSessions.Any(s => s.SessionId == storedSession.SessionId))
                        {
                            allSessions.Add(storedSession);
                        }
                    }
                }

                // Filtrele;
                var filteredSessions = allSessions.AsEnumerable();

                if (!string.IsNullOrEmpty(filter.UserId))
                    filteredSessions = filteredSessions.Where(s => s.UserId == filter.UserId);

                if (!string.IsNullOrEmpty(filter.DeviceId))
                    filteredSessions = filteredSessions.Where(s => s.DeviceId == filter.DeviceId);

                if (!string.IsNullOrEmpty(filter.IPAddress))
                    filteredSessions = filteredSessions.Where(s => s.IPAddress == filter.IPAddress);

                if (filter.Status.HasValue)
                    filteredSessions = filteredSessions.Where(s => s.Status == filter.Status.Value);

                if (filter.CreatedAfter.HasValue)
                    filteredSessions = filteredSessions.Where(s => s.CreatedAt >= filter.CreatedAfter.Value);

                if (filter.CreatedBefore.HasValue)
                    filteredSessions = filteredSessions.Where(s => s.CreatedAt <= filter.CreatedBefore.Value);

                if (filter.ActiveAfter.HasValue)
                    filteredSessions = filteredSessions.Where(s => s.LastActivity >= filter.ActiveAfter.Value);

                if (filter.ActiveBefore.HasValue)
                    filteredSessions = filteredSessions.Where(s => s.LastActivity <= filter.ActiveBefore.Value);

                if (filter.MinActivityCount.HasValue)
                    filteredSessions = filteredSessions.Where(s => s.ActivityCount >= filter.MinActivityCount.Value);

                if (!(filter.IncludeExpired ?? false))
                    filteredSessions = filteredSessions.Where(s => s.IsValid());

                return filteredSessions.OrderByDescending(s => s.LastActivity).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Filtrelenmiş oturumlar getirilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.GetSessionsAsync");
                throw;
            }
        }

        /// <summary>
        /// Süresi dolan oturumları temizler;
        /// </summary>
        public async Task<int> CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogInformation("Süresi dolan oturumlar temizleniyor...");

                    int cleanedCount = 0;
                    var expiredSessionIds = new List<string>();

                    // Süresi dolan oturumları bul;
                    foreach (var kvp in _activeSessions)
                    {
                        if (!kvp.Value.IsValid())
                        {
                            expiredSessionIds.Add(kvp.Key);
                        }
                    }

                    // Süresi dolan oturumları temizle;
                    foreach (var sessionId in expiredSessionIds)
                    {
                        if (_activeSessions.TryRemove(sessionId, out var session))
                        {
                            // Durumu güncelle;
                            session.Status = SessionStatus.Expired;

                            // Storage'a kaydet;
                            await UpdateSessionInStorageAsync(session, cancellationToken);

                            // İndekslerden kaldır;
                            RemoveFromIndexes(session);

                            cleanedCount++;

                            // Event tetikle;
                            OnSessionTerminated(new SessionEventArgs(sessionId, session.UserId, SessionAction.Expired)
                            {
                                Metadata = new Dictionary<string, object>
                                {
                                    ["ExpirationReason"] = "Timeout",
                                    ["SessionDuration"] = DateTime.UtcNow - session.CreatedAt;
                                }
                            });

                            _logger.LogDebug("Süresi dolan oturum temizlendi. SessionId: {SessionId}, UserId: {UserId}",
                                sessionId, session.UserId);
                        }
                    }

                    // Persistent storage'daki eski oturumları da temizle;
                    var storageCleaned = await CleanupExpiredSessionsFromStorageAsync(cancellationToken);
                    cleanedCount += storageCleaned;

                    _logger.LogInformation("Süresi dolan oturum temizleme tamamlandı. Temizlenen: {CleanedCount}", cleanedCount);
                    return cleanedCount;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Süresi dolan oturumlar temizlenirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.CleanupExpiredSessionsAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturum süresini uzatır;
        /// </summary>
        public async Task<bool> RenewSessionAsync(string sessionId, TimeSpan extension,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            if (extension <= TimeSpan.Zero)
                throw new ArgumentException("Extension süresi pozitif olmalıdır", nameof(extension));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    var session = await GetSessionAsync(sessionId, cancellationToken);
                    if (session == null || !session.IsValid())
                    {
                        _logger.LogWarning("Uzatılacak oturum bulunamadı veya geçersiz. SessionId: {SessionId}", sessionId);
                        return false;
                    }

                    // Süreyi uzat;
                    if (session.ExpiresAt.HasValue)
                    {
                        session.ExpiresAt = session.ExpiresAt.Value.Add(extension);
                    }
                    else;
                    {
                        session.ExpiresAt = DateTime.UtcNow.Add(extension);
                    }

                    // Aktiviteyi kaydet;
                    session.RecordActivity();

                    // Cache'i güncelle;
                    _activeSessions[sessionId] = session;

                    // Storage'a kaydet;
                    await UpdateSessionInStorageAsync(session, cancellationToken);

                    // Event tetikle;
                    OnSessionUpdated(new SessionEventArgs(sessionId, session.UserId, SessionAction.Renewed)
                    {
                        Metadata = new Dictionary<string, object>
                        {
                            ["Extension"] = extension,
                            ["NewExpiration"] = session.ExpiresAt,
                            ["RemainingTime"] = session.GetRemainingTime()
                        }
                    });

                    _logger.LogInformation("Oturum süresi uzatıldı. SessionId: {SessionId}, Extension: {Extension}, NewExpiresAt: {NewExpiresAt}",
                        sessionId, extension, session.ExpiresAt);

                    return true;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum süresi uzatılırken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.RenewSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Token'a göre oturum bulur;
        /// </summary>
        public async Task<UserSession> FindSessionByTokenAsync(string tokenType, string tokenValue,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tokenType))
                throw new ArgumentException("TokenType boş olamaz", nameof(tokenType));

            if (string.IsNullOrEmpty(tokenValue))
                throw new ArgumentException("TokenValue boş olamaz", nameof(tokenValue));

            try
            {
                // Token indeksinden oturum ID'sini bul;
                var tokenKey = $"{tokenType}:{tokenValue}";
                if (_tokenSessionIndex.TryGetValue(tokenKey, out var sessionId))
                {
                    return await GetSessionAsync(sessionId, cancellationToken);
                }

                // Storage'dan ara;
                var session = await FindSessionByTokenInStorageAsync(tokenType, tokenValue, cancellationToken);
                if (session != null && session.IsValid())
                {
                    // Cache'e ekle;
                    _activeSessions[session.SessionId] = session;
                    UpdateIndexes(session, true);

                    // Token indeksine ekle;
                    _tokenSessionIndex[tokenKey] = session.SessionId;

                    return session;
                }

                _logger.LogWarning("Token ile eşleşen oturum bulunamadı. TokenType: {TokenType}", tokenType);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token ile oturum aranırken hata oluştu. TokenType: {TokenType}", tokenType);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.FindSessionByTokenAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturum istatistiklerini getirir;
        /// </summary>
        public async Task<Dictionary<string, object>> GetSessionStatisticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = new Dictionary<string, object>();

                // Temel istatistikler;
                stats["TotalSessions"] = _activeSessions.Count;
                stats["TotalUsers"] = _userSessionIndex.Count;

                // Durum bazlı istatistikler;
                var statusCounts = _activeSessions.Values;
                    .GroupBy(s => s.Status)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                stats["StatusCounts"] = statusCounts;

                // Aktif oturumlar;
                var activeSessions = _activeSessions.Values.Where(s => s.Status == SessionStatus.Active).ToList();
                stats["ActiveSessions"] = activeSessions.Count;

                // Ortalama oturum süresi;
                if (activeSessions.Any())
                {
                    var avgDuration = activeSessions.Average(s => (DateTime.UtcNow - s.CreatedAt).TotalMinutes);
                    stats["AverageSessionDurationMinutes"] = avgDuration;

                    var avgActivityCount = activeSessions.Average(s => s.ActivityCount);
                    stats["AverageActivityCount"] = avgActivityCount;
                }

                // Kullanıcı başına oturum sayısı;
                var sessionsPerUser = _userSessionIndex;
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
                stats["SessionsPerUser"] = sessionsPerUser;

                // En aktif kullanıcılar;
                var mostActiveUsers = _userSessionIndex;
                    .OrderByDescending(kvp => kvp.Value.Count)
                    .Take(10)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
                stats["MostActiveUsers"] = mostActiveUsers;

                // Zaman bazlı istatistikler;
                var sessionsByHour = _activeSessions.Values;
                    .Where(s => s.CreatedAt > DateTime.UtcNow.AddDays(-1))
                    .GroupBy(s => s.CreatedAt.Hour)
                    .OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                stats["SessionsLast24Hours"] = sessionsByHour;

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum istatistikleri getirilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.GetSessionStatisticsAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturumu kilitler;
        /// </summary>
        public async Task<bool> LockSessionAsync(string sessionId, string reason, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    var session = await GetSessionAsync(sessionId, cancellationToken);
                    if (session == null)
                    {
                        _logger.LogWarning("Kilitlenecek oturum bulunamadı. SessionId: {SessionId}", sessionId);
                        return false;
                    }

                    // Durumu güncelle;
                    session.Status = SessionStatus.Locked;

                    // Kilitleme sebebini session data'ya ekle;
                    session.SessionData["LockReason"] = reason;
                    session.SessionData["LockedAt"] = DateTime.UtcNow;

                    // Cache'i güncelle;
                    _activeSessions[sessionId] = session;

                    // Storage'a kaydet;
                    await UpdateSessionInStorageAsync(session, cancellationToken);

                    // Event tetikle;
                    OnSessionUpdated(new SessionEventArgs(sessionId, session.UserId, SessionAction.Locked)
                    {
                        Metadata = new Dictionary<string, object>
                        {
                            ["Reason"] = reason,
                            ["LockedAt"] = DateTime.UtcNow;
                        }
                    });

                    _logger.LogWarning("Oturum kilitlendi. SessionId: {SessionId}, UserId: {UserId}, Reason: {Reason}",
                        sessionId, session.UserId, reason);

                    return true;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum kilitlenirken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.LockSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturum kilidini açar;
        /// </summary>
        public async Task<bool> UnlockSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    var session = await GetSessionAsync(sessionId, cancellationToken);
                    if (session == null || session.Status != SessionStatus.Locked)
                    {
                        _logger.LogWarning("Açılacak kilitli oturum bulunamadı. SessionId: {SessionId}", sessionId);
                        return false;
                    }

                    // Durumu güncelle;
                    session.Status = SessionStatus.Active;

                    // Kilitleme bilgilerini temizle;
                    session.SessionData.Remove("LockReason");
                    session.SessionData.Remove("LockedAt");
                    session.SessionData["UnlockedAt"] = DateTime.UtcNow;

                    // Cache'i güncelle;
                    _activeSessions[sessionId] = session;

                    // Storage'a kaydet;
                    await UpdateSessionInStorageAsync(session, cancellationToken);

                    // Event tetikle;
                    OnSessionUpdated(new SessionEventArgs(sessionId, session.UserId, SessionAction.Unlocked)
                    {
                        Metadata = new Dictionary<string, object>
                        {
                            ["UnlockedAt"] = DateTime.UtcNow;
                        }
                    });

                    _logger.LogInformation("Oturum kilidi açıldı. SessionId: {SessionId}, UserId: {UserId}",
                        sessionId, session.UserId);

                    return true;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum kilidi açılırken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.UnlockSessionAsync");
                throw;
            }
        }

        /// <summary>
        /// Oturumu farklı cihaza taşır;
        /// </summary>
        public async Task<bool> MigrateSessionAsync(string oldSessionId, string newDeviceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(oldSessionId))
                throw new ArgumentException("OldSessionId boş olamaz", nameof(oldSessionId));

            if (string.IsNullOrEmpty(newDeviceId))
                throw new ArgumentException("NewDeviceId boş olamaz", nameof(newDeviceId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);
                try
                {
                    var oldSession = await GetSessionAsync(oldSessionId, cancellationToken);
                    if (oldSession == null || !oldSession.IsValid())
                    {
                        _logger.LogWarning("Taşınacak oturum bulunamadı veya geçersiz. SessionId: {SessionId}", oldSessionId);
                        return false;
                    }

                    // Yeni oturum oluştur;
                    var newSessionData = new Dictionary<string, object>
                    {
                        ["DeviceId"] = newDeviceId,
                        ["ClientInfo"] = oldSession.ClientInfo,
                        ["IPAddress"] = oldSession.IPAddress,
                        ["MigratedFrom"] = oldSessionId,
                        ["MigrationTime"] = DateTime.UtcNow;
                    };

                    // Eski session data'yı kopyala;
                    foreach (var kvp in oldSession.SessionData)
                    {
                        newSessionData[kvp.Key] = kvp.Value;
                    }

                    var newSession = await CreateSessionAsync(oldSession.UserId, newSessionData, cancellationToken);

                    if (newSession != null)
                    {
                        // Eski oturumu sonlandır;
                        await TerminateSessionAsync(oldSessionId, cancellationToken);

                        // Event tetikle;
                        OnSessionUpdated(new SessionEventArgs(oldSessionId, oldSession.UserId, SessionAction.Migrated)
                        {
                            Metadata = new Dictionary<string, object>
                            {
                                ["NewSessionId"] = newSession.SessionId,
                                ["NewDeviceId"] = newDeviceId,
                                ["MigrationTime"] = DateTime.UtcNow;
                            }
                        });

                        _logger.LogInformation("Oturum taşındı. OldSessionId: {OldSessionId}, NewSessionId: {NewSessionId}, NewDevice: {NewDeviceId}",
                            oldSessionId, newSession.SessionId, newDeviceId);

                        return true;
                    }

                    return false;
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum taşınırken hata oluştu. OldSessionId: {OldSessionId}", oldSessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "SessionManager.MigrateSessionAsync");
                throw;
            }
        }

        #region Yardımcı Metodlar;

        /// <summary>
        /// Persistent oturumları yükler;
        /// </summary>
        private async Task LoadPersistentSessionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var sessionsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "Sessions");

                if (!Directory.Exists(sessionsPath))
                    return;

                var files = Directory.GetFiles(sessionsPath, "*.json");
                int loadedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var session = JsonConvert.DeserializeObject<UserSession>(json);

                        if (session != null && session.IsValid())
                        {
                            _activeSessions[session.SessionId] = session;
                            UpdateIndexes(session, false);
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Oturum dosyası yüklenirken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                _logger.LogDebug("{LoadedCount} oturum depolamadan yüklendi", loadedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persistent oturumlar yüklenirken hata oluştu");
                throw;
            }
        }

        /// <summary>
        /// Oturumu depolamadan yükler;
        /// </summary>
        private async Task<UserSession> LoadSessionFromStorageAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                var filePath = GetSessionFilePath(sessionId);
                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    return JsonConvert.DeserializeObject<UserSession>(json);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Oturum depolamadan yüklenirken hata oluştu: {SessionId}", sessionId);
                return null;
            }
        }

        /// <summary>
        /// Kullanıcı oturumlarını depolamadan yükler;
        /// </summary>
        private async Task<List<UserSession>> LoadUserSessionsFromStorageAsync(string userId, CancellationToken cancellationToken)
        {
            var sessions = new List<UserSession>();

            try
            {
                var sessionsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "Sessions",
                    "Users",
                    userId);

                if (!Directory.Exists(sessionsPath))
                    return sessions;

                var files = Directory.GetFiles(sessionsPath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var session = JsonConvert.DeserializeObject<UserSession>(json);

                        if (session != null)
                        {
                            sessions.Add(session);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Kullanıcı oturum dosyası okunurken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı oturumları yüklenirken hata oluştu. UserId: {UserId}", userId);
                return sessions;
            }
        }

        /// <summary>
        /// Oturumu depolamaya kaydeder;
        /// </summary>
        private async Task UpdateSessionInStorageAsync(UserSession session, CancellationToken cancellationToken)
        {
            try
            {
                var sessionsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "Sessions");

                Directory.CreateDirectory(sessionsPath);

                var filePath = GetSessionFilePath(session.SessionId);
                var json = JsonConvert.SerializeObject(session, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, json, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Oturum depolamaya kaydedilirken hata oluştu: {SessionId}", session.SessionId);
            }
        }

        /// <summary>
        /// Süresi dolan oturumları depolamadan temizler;
        /// </summary>
        private async Task<int> CleanupExpiredSessionsFromStorageAsync(CancellationToken cancellationToken)
        {
            int cleanedCount = 0;

            try
            {
                var sessionsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "Sessions");

                if (!Directory.Exists(sessionsPath))
                    return 0;

                var files = Directory.GetFiles(sessionsPath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var session = JsonConvert.DeserializeObject<UserSession>(json);

                        if (session != null && !session.IsValid())
                        {
                            File.Delete(file);
                            cleanedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Oturum dosyası işlenirken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                return cleanedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Depolamadaki oturumlar temizlenirken hata oluştu");
                return cleanedCount;
            }
        }

        /// <summary>
        /// Token'a göre oturumu depolamada arar;
        /// </summary>
        private async Task<UserSession> FindSessionByTokenInStorageAsync(string tokenType, string tokenValue,
            CancellationToken cancellationToken)
        {
            try
            {
                var sessionsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "Sessions");

                if (!Directory.Exists(sessionsPath))
                    return null;

                var files = Directory.GetFiles(sessionsPath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var session = JsonConvert.DeserializeObject<UserSession>(json);

                        if (session != null && session.IsValid())
                        {
                            if (session.Tokens.TryGetValue(tokenType, out var storedToken) &&
                                storedToken?.ToString() == tokenValue)
                            {
                                return session;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Oturum dosyası okunurken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token ile oturum aranırken hata oluştu");
                return null;
            }
        }

        /// <summary>
        /// Oturum dosya yolunu getirir;
        /// </summary>
        private string GetSessionFilePath(string sessionId)
        {
            var sessionsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NEDA",
                "Sessions");

            return Path.Combine(sessionsPath, $"{sessionId}.json");
        }

        /// <summary>
        /// Oturum ID'si oluşturur;
        /// </summary>
        private string GenerateSessionId()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[16];
                rng.GetBytes(bytes);
                return Convert.ToBase64String(bytes)
                    .Replace("+", "-")
                    .Replace("/", "_")
                    .Replace("=", "");
            }
        }

        /// <summary>
        /// Oturum token'ları oluşturur;
        /// </summary>
        private async Task GenerateSessionTokensAsync(UserSession session, CancellationToken cancellationToken)
        {
            try
            {
                // Erişim token'ı;
                var accessToken = await _securityManager.GenerateTokenAsync(
                    session.UserId,
                    "AccessToken",
                    new Dictionary<string, object>
                    {
                        ["SessionId"] = session.SessionId,
                        ["DeviceId"] = session.DeviceId,
                        ["IPAddress"] = session.IPAddress;
                    },
                    cancellationToken);

                session.Tokens["AccessToken"] = accessToken;

                // Yenileme token'ı;
                var refreshToken = await _securityManager.GenerateTokenAsync(
                    session.UserId,
                    "RefreshToken",
                    new Dictionary<string, object>
                    {
                        ["SessionId"] = session.SessionId,
                        ["TokenType"] = "Refresh"
                    },
                    cancellationToken);

                session.Tokens["RefreshToken"] = refreshToken;

                // Token indeksine ekle;
                _tokenSessionIndex[$"AccessToken:{accessToken}"] = session.SessionId;
                _tokenSessionIndex[$"RefreshToken:{refreshToken}"] = session.SessionId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum token'ları oluşturulurken hata oluştu");
                throw;
            }
        }

        /// <summary>
        /// İndeksleri günceller;
        /// </summary>
        private void UpdateIndexes(UserSession session, bool addToCache)
        {
            // Kullanıcı oturum indeksi;
            _userSessionIndex.AddOrUpdate(
                session.UserId,
                new List<string> { session.SessionId },
                (key, existingList) =>
                {
                    if (!existingList.Contains(session.SessionId))
                        existingList.Add(session.SessionId);
                    return existingList;
                });

            // Cache'e ekle (eğer henüz eklenmediyse)
            if (addToCache && !_activeSessions.ContainsKey(session.SessionId))
            {
                _activeSessions[session.SessionId] = session;
            }
        }

        /// <summary>
        /// İndekslerden kaldırır;
        /// </summary>
        private void RemoveFromIndexes(UserSession session)
        {
            // Kullanıcı oturum indeksi;
            if (_userSessionIndex.TryGetValue(session.UserId, out var sessionList))
            {
                sessionList.Remove(session.SessionId);
                if (sessionList.Count == 0)
                    _userSessionIndex.TryRemove(session.UserId, out _);
            }

            // Token indeksi;
            var tokensToRemove = _tokenSessionIndex;
                .Where(kvp => kvp.Value == session.SessionId)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var tokenKey in tokensToRemove)
            {
                _tokenSessionIndex.TryRemove(tokenKey, out _);
            }
        }

        /// <summary>
        /// Geçersiz oturumu temizler;
        /// </summary>
        private async Task CleanupInvalidSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                if (_activeSessions.TryRemove(sessionId, out var session))
                {
                    // Durumu güncelle;
                    session.Status = SessionStatus.Expired;

                    // Storage'a kaydet;
                    await UpdateSessionInStorageAsync(session, cancellationToken);

                    // İndekslerden kaldır;
                    RemoveFromIndexes(session);

                    _logger.LogDebug("Geçersiz oturum temizlendi. SessionId: {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geçersiz oturum temizlenirken hata oluştu. SessionId: {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// SessionCreated event'ini tetikler;
        /// </summary>
        protected virtual void OnSessionCreated(SessionEventArgs e)
        {
            SessionCreated?.Invoke(this, e);
        }

        /// <summary>
        /// SessionTerminated event'ini tetikler;
        /// </summary>
        protected virtual void OnSessionTerminated(SessionEventArgs e)
        {
            SessionTerminated?.Invoke(this, e);
        }

        /// <summary>
        /// SessionUpdated event'ini tetikler;
        /// </summary>
        protected virtual void OnSessionUpdated(SessionEventArgs e)
        {
            SessionUpdated?.Invoke(this, e);
        }

        /// <summary>
        /// Süresi dolan oturumları temizleme callback'i;
        /// </summary>
        private async void CleanupExpiredSessionsCallback(object state)
        {
            try
            {
                if (_isDisposed)
                    return;

                await CleanupExpiredSessionsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum temizleme callback'inde hata oluştu");
            }
        }

        /// <summary>
        /// Oturum izleme callback'i;
        /// </summary>
        private async void MonitorSessionsCallback(object state)
        {
            try
            {
                if (_isDisposed)
                    return;

                // İstatistikleri logla;
                var stats = await GetSessionStatisticsAsync(CancellationToken.None);

                _logger.LogInformation("Oturum izleme - Aktif: {ActiveSessions}, Toplam: {TotalSessions}, Kullanıcı: {TotalUsers}",
                    stats["ActiveSessions"], stats["TotalSessions"], stats["TotalUsers"]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oturum izleme callback'inde hata oluştu");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _cleanupTimer?.Dispose();
                    _monitoringTimer?.Dispose();
                    _sessionLock?.Dispose();

                    // Aktif oturumları kaydet;
                    try
                    {
                        Task.Run(async () =>
                        {
                            foreach (var session in _activeSessions.Values)
                            {
                                await UpdateSessionInStorageAsync(session, CancellationToken.None);
                            }
                        }).Wait(TimeSpan.FromSeconds(10));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SessionManager dispose edilirken hata oluştu");
                    }
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SessionManager()
        {
            Dispose(false);
        }

        #endregion;
    }
}
