using NEDA.AI.ComputerVision;
using NEDA.ExceptionHandling;
using NEDA.HardwareSecurity.PhysicalManifesto;
using NEDA.Logging;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.HardwareSecurity.USBCryptoToken;
{
    /// <summary>
    /// Token durumu;
    /// </summary>
    public enum TokenStatus;
    {
        /// <summary>Token mevcut değil</summary>
        NotPresent = 0,

        /// <summary>Token takılı ama başlatılmamış</summary>
        Present = 1,

        /// <summary>Token başlatıldı</summary>
        Initialized = 2,

        /// <summary>Token aktif</summary>
        Active = 3,

        /// <summary>Token kilitli</summary>
        Locked = 4,

        /// <summary>Token arızalı</summary>
        Faulty = 5,

        /// <summary>Token geçersiz</summary>
        Invalid = 6,

        /// <summary>Token takılıyor/çıkarılıyor</summary>
        Busy = 7;
    }

    /// <summary>
    /// Token güvenlik seviyesi;
    /// </summary>
    public enum TokenSecurityLevel;
    {
        /// <summary>Minimum güvenlik</summary>
        Minimum = 0,

        /// <summary>Temel güvenlik</summary>
        Basic = 1,

        /// <summary>Standart güvenlik</summary>
        Standard = 2,

        /// <summary>Yüksek güvenlik</summary>
        High = 3,

        /// <summary>Kritik güvenlik</summary>
        Critical = 4,

        /// <summary>FIPS 140-2 seviye 3</summary>
        FIPS140_Level3 = 5,

        /// <summary>FIPS 140-2 seviye 4</summary>
        FIPS140_Level4 = 6;
    }

    /// <summary>
    /// Anahtar türü;
    /// </summary>
    public enum TokenKeyType;
    {
        /// <summary>AES simetrik anahtar</summary>
        AES = 0,

        /// <summary>RSA asimetrik anahtar çifti</summary>
        RSA = 1,

        /// <summary>ECC asimetrik anahtar çifti</summary>
        ECC = 2,

        /// <summary>HMAC anahtarı</summary>
        HMAC = 3,

        /// <summary>Anahtar şifreleme anahtarı</summary>
        KEK = 4,

        /// <summary>Kök anahtar</summary>
        Root = 5,

        /// <summary>Oturum anahtarı</summary>
        Session = 6,

        /// <summary>İmza anahtarı</summary>
        Signature = 7,

        /// <summary>Şifreleme anahtarı</summary>
        Encryption = 8;
    }

    /// <summary>
    /// Anahtar kullanım izinleri;
    /// </summary>
    [Flags]
    public enum KeyUsageFlags;
    {
        /// <summary>Hiçbir kullanım</summary>
        None = 0,

        /// <summary>Şifreleme</summary>
        Encrypt = 1,

        /// <summary>Şifre çözme</summary>
        Decrypt = 2,

        /// <summary>İmzalama</summary>
        Sign = 4,

        /// <summary>Doğrulama</summary>
        Verify = 8,

        /// <summary>Anahtar sarmalama</summary>
        Wrap = 16,

        /// <summary>Anahtar açma</summary>
        Unwrap = 32,

        /// <summary>Türetme</summary>
        Derive = 64,

        /// <summary>Dışa aktarılabilir</summary>
        Exportable = 128,

        /// <summary>Kalıcı depolama</summary>
        Persistent = 256,

        /// <summary>Sensitiv (hassas) anahtar</summary>
        Sensitive = 512,

        /// <summary>Her zaman hassas</summary>
        AlwaysSensitive = 1024,

        /// <summary>Asla dışa aktarılabilir değil</summary>
        NeverExtractable = 2048;
    }

    /// <summary>
    /// Token bilgileri;
    /// </summary>
    public class TokenInfo;
    {
        public string TokenId { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string FirmwareVersion { get; set; }
        public string HardwareVersion { get; set; }
        public int TotalMemory { get; set; }
        public int FreeMemory { get; set; }
        public int MaxPinAttempts { get; set; }
        public int RemainingPinAttempts { get; set; }
        public bool HasSecureDisplay { get; set; }
        public bool HasKeypad { get; set; }
        public bool HasBiometric { get; set; }
        public List<string> SupportedAlgorithms { get; } = new List<string>();
        public TokenSecurityLevel SecurityLevel { get; set; }
        public DateTime ManufacturingDate { get; set; }
        public DateTime InitializationDate { get; set; }
        public DateTime LastUsedDate { get; set; }
    }

    /// <summary>
    /// Token anahtarı;
    /// </summary>
    public class TokenKey;
    {
        public string KeyId { get; }
        public string KeyLabel { get; }
        public TokenKeyType KeyType { get; }
        public int KeySize { get; }
        public DateTime CreationDate { get; }
        public DateTime ExpirationDate { get; }
        public KeyUsageFlags UsageFlags { get; }
        public bool IsActive { get; set; }
        public string Owner { get; }
        public string Application { get; }
        public byte[] KeyAttributes { get; }

        public TokenKey(
            string keyId,
            string keyLabel,
            TokenKeyType keyType,
            int keySize,
            DateTime expirationDate,
            KeyUsageFlags usageFlags,
            string owner,
            string application,
            byte[] keyAttributes = null)
        {
            KeyId = keyId;
            KeyLabel = keyLabel;
            KeyType = keyType;
            KeySize = keySize;
            CreationDate = DateTime.UtcNow;
            ExpirationDate = expirationDate;
            UsageFlags = usageFlags;
            IsActive = true;
            Owner = owner;
            Application = application;
            KeyAttributes = keyAttributes;
        }

        public bool CanEncrypt => (UsageFlags & KeyUsageFlags.Encrypt) != 0;
        public bool CanDecrypt => (UsageFlags & KeyUsageFlags.Decrypt) != 0;
        public bool CanSign => (UsageFlags & KeyUsageFlags.Sign) != 0;
        public bool CanVerify => (UsageFlags & KeyUsageFlags.Verify) != 0;
        public bool IsExportable => (UsageFlags & KeyUsageFlags.Exportable) != 0;
        public bool IsPersistent => (UsageFlags & KeyUsageFlags.Persistent) != 0;
        public bool IsSensitive => (UsageFlags & KeyUsageFlags.Sensitive) != 0;
    }

    /// <summary>
    /// Token yapılandırması;
    /// </summary>
    public class TokenConfig;
    {
        public bool AutoDetectTokens { get; set; } = true;
        public int DetectionIntervalMs { get; set; } = 1000;
        public bool RequirePin { get; set; } = true;
        public int MaxPinLength { get; set; } = 16;
        public int MinPinLength { get; set; } = 6;
        public bool EnablePinComplexity { get; set; } = true;
        public int MaxPinAttempts { get; set; } = 10;
        public int LockoutDurationMinutes { get; set; } = 30;
        public bool EnableSecureChannel { get; set; } = true;
        public bool EnableKeyRotation { get; set; } = true;
        public int KeyRotationDays { get; set; } = 90;
        public bool EnableAuditLogging { get; set; } = true;
        public bool EnableTamperDetection { get; set; } = true;
        public TokenSecurityLevel RequiredSecurityLevel { get; set; } = TokenSecurityLevel.Standard;
        public List<string> AllowedManufacturers { get; } = new List<string>();
        public List<string> BlockedModels { get; } = new List<string>();
    }

    /// <summary>
    /// Token olayı;
    /// </summary>
    public class TokenEvent;
    {
        public DateTime Timestamp { get; }
        public string EventType { get; }
        public string TokenId { get; }
        public string Description { get; }
        public TokenStatus StatusBefore { get; }
        public TokenStatus StatusAfter { get; }
        public int Severity { get; }

        public TokenEvent(
            string eventType,
            string tokenId,
            string description,
            TokenStatus before,
            TokenStatus after,
            int severity = 1)
        {
            Timestamp = DateTime.UtcNow;
            EventType = eventType;
            TokenId = tokenId;
            Description = description;
            StatusBefore = before;
            StatusAfter = after;
            Severity = severity;
        }
    }

    /// <summary>
    /// PIN doğrulama sonucu;
    /// </summary>
    public class PinVerificationResult;
    {
        public bool IsValid { get; }
        public int RemainingAttempts { get; }
        public bool IsLocked { get; }
        public DateTime? LockedUntil { get; }
        public string Message { get; }

        public PinVerificationResult(
            bool isValid,
            int remainingAttempts,
            bool isLocked = false,
            DateTime? lockedUntil = null,
            string message = "")
        {
            IsValid = isValid;
            RemainingAttempts = remainingAttempts;
            IsLocked = isLocked;
            LockedUntil = lockedUntil;
            Message = message;
        }
    }

    /// <summary>
    /// Anahtar oluşturma parametreleri;
    /// </summary>
    public class KeyGenerationParams;
    {
        public string KeyLabel { get; set; }
        public TokenKeyType KeyType { get; set; }
        public int KeySize { get; set; }
        public KeyUsageFlags UsageFlags { get; set; }
        public string Owner { get; set; }
        public string Application { get; set; }
        public TimeSpan ValidityPeriod { get; set; }
        public byte[] KeyAttributes { get; set; }
        public bool GenerateOnToken { get; set; } = true;

        public KeyGenerationParams()
        {
            ValidityPeriod = TimeSpan.FromDays(365);
            UsageFlags = KeyUsageFlags.Encrypt | KeyUsageFlags.Decrypt | KeyUsageFlags.Persistent;
        }
    }

    /// <summary>
    /// Anahtar şifreleme parametreleri;
    /// </summary>
    public class KeyEncryptionParams;
    {
        public string WrappingKeyId { get; set; }
        public byte[] InitializationVector { get; set; }
        public byte[] AdditionalAuthenticatedData { get; set; }
        public bool IncludeKeyAttributes { get; set; } = true;
        public bool IncludeKeyMetadata { get; set; } = true;
    }

    /// <summary>
    /// Ana sınıf: USB Kripto Token Yöneticisi;
    /// </summary>
    public class TokenManager : IDisposable
    {
        #region Events;

        /// <summary>Token takıldığında tetiklenir</summary>
        public event EventHandler<TokenInfo> TokenInserted;

        /// <summary>Token çıkarıldığında tetiklenir</summary>
        public event EventHandler<string> TokenRemoved;

        /// <summary>Token durumu değiştiğinde tetiklenir</summary>
        public event EventHandler<(string TokenId, TokenStatus Status)> TokenStatusChanged;

        /// <summary>PIN doğrulandığında tetiklenir</summary>
        public event EventHandler<string> PinVerified;

        /// <summary>Token kilitlendiğinde tetiklenir</summary>
        public event EventHandler<string> TokenLocked;

        /// <summary>Anahtar işlemi gerçekleştiğinde tetiklenir</summary>
        public event EventHandler<string> KeyOperationPerformed;

        /// <summary>Güvenlik olayı gerçekleştiğinde tetiklenir</summary>
        public event EventHandler<TokenEvent> SecurityEventOccurred;

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IAuditLogger _auditLogger;
        private readonly IManifestoValidator _manifestoValidator;

        private readonly TokenConfig _config;
        private readonly Dictionary<string, TokenSession> _activeSessions;
        private readonly Dictionary<string, TokenInfo> _connectedTokens;
        private readonly Dictionary<string, List<TokenKey>> _tokenKeys;
        private readonly Dictionary<string, int> _pinAttempts;
        private readonly Dictionary<string, DateTime> _tokenLockouts;
        private readonly Queue<TokenEvent> _securityEvents;

        private readonly Timer _tokenDetectionTimer;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly RNGCryptoServiceProvider _rng;
        private CancellationTokenSource _detectionCancellationTokenSource;
        private bool _isMonitoring = false;

        #endregion;

        #region Properties;

        /// <summary>Bağlı token sayısı</summary>
        public int ConnectedTokenCount => _connectedTokens.Count;

        /// <summary>Aktif oturum sayısı</summary>
        public int ActiveSessionCount => _activeSessions.Count;

        /// <summary>İzleme aktif mi?</summary>
        public bool IsMonitoring => _isMonitoring;

        /// <summary>Token yapılandırması</summary>
        public TokenConfig Config => _config;

        /// <summary>Bağlı token listesi</summary>
        public IReadOnlyCollection<TokenInfo> ConnectedTokens => _connectedTokens.Values.ToList().AsReadOnly();

        #endregion;

        #region Constructor;

        /// <summary>
        /// TokenManager sınıfı yapıcısı;
        /// </summary>
        public TokenManager(
            ILogger logger,
            ICryptoEngine cryptoEngine,
            IAuditLogger auditLogger,
            IManifestoValidator manifestoValidator,
            TokenConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _manifestoValidator = manifestoValidator ?? throw new ArgumentNullException(nameof(manifestoValidator));

            _config = config ?? new TokenConfig();

            _activeSessions = new Dictionary<string, TokenSession>();
            _connectedTokens = new Dictionary<string, TokenInfo>();
            _tokenKeys = new Dictionary<string, List<TokenKey>>();
            _pinAttempts = new Dictionary<string, int>();
            _tokenLockouts = new Dictionary<string, DateTime>();
            _securityEvents = new Queue<TokenEvent>();

            _rng = new RNGCryptoServiceProvider();

            _tokenDetectionTimer = new Timer(DetectTokens, null,
                Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation("TokenManager initialized");
        }

        #endregion;

        #region Public Methods - Token Management;

        /// <summary>
        /// Token izlemeyi başlat;
        /// </summary>
        public async Task StartMonitoringAsync()
        {
            if (_isMonitoring)
            {
                _logger.LogWarning("Token monitoring is already active");
                return;
            }

            await _operationLock.WaitAsync();

            try
            {
                _logger.LogInformation("Starting token monitoring");

                // Mevcut tokenları tespit et;
                await DetectConnectedTokensAsync();

                // Zamanlayıcıyı başlat;
                _tokenDetectionTimer.Change(0, _config.DetectionIntervalMs);

                _detectionCancellationTokenSource = new CancellationTokenSource();
                _isMonitoring = true;

                _logger.LogInformation("Token monitoring started successfully");
                LogSecurityEvent("MonitoringStart", "SYSTEM", "Token monitoring started",
                    TokenStatus.NotPresent, TokenStatus.NotPresent, 2);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start token monitoring: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token izlemeyi durdur;
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (!_isMonitoring)
                return;

            await _operationLock.WaitAsync();

            try
            {
                _logger.LogInformation("Stopping token monitoring");

                // Zamanlayıcıyı durdur;
                _tokenDetectionTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // Tüm oturumları kapat;
                await CloseAllSessionsAsync();

                // İzlemeyi iptal et;
                _detectionCancellationTokenSource?.Cancel();
                _detectionCancellationTokenSource?.Dispose();
                _detectionCancellationTokenSource = null;

                _isMonitoring = false;

                _logger.LogInformation("Token monitoring stopped");
                LogSecurityEvent("MonitoringStop", "SYSTEM", "Token monitoring stopped",
                    TokenStatus.NotPresent, TokenStatus.NotPresent, 2);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to stop token monitoring: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Bağlı tokenları listele;
        /// </summary>
        public async Task<List<TokenInfo>> ListTokensAsync()
        {
            if (!_isMonitoring)
                throw new InvalidOperationException("Token monitoring is not active");

            await _operationLock.WaitAsync();

            try
            {
                return _connectedTokens.Values.ToList();
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token oturumu aç;
        /// </summary>
        public async Task<TokenSession> OpenSessionAsync(string tokenId, string pin = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out var tokenInfo))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"Session already open for token: {tokenId}");

            await _operationLock.WaitAsync();

            try
            {
                // Token kilitli mi kontrol et;
                if (IsTokenLocked(tokenId))
                {
                    throw new TokenLockedException($"Token is locked: {tokenId}");
                }

                // PIN gerekiyorsa doğrula;
                if (_config.RequirePin && !string.IsNullOrEmpty(pin))
                {
                    var pinResult = await VerifyPinAsync(tokenId, pin);

                    if (!pinResult.IsValid)
                    {
                        if (pinResult.IsLocked)
                        {
                            await HandleTokenLockoutAsync(tokenId);
                        }

                        throw new InvalidPinException($"Invalid PIN for token: {tokenId}");
                    }
                }

                // Oturum oluştur;
                var session = new TokenSession(tokenId, tokenInfo);
                _activeSessions[tokenId] = session;

                // Oturum başlat;
                await session.InitializeAsync();

                _logger.LogInformation($"Session opened for token: {tokenId}");
                LogSecurityEvent("SessionOpen", tokenId, "Token session opened",
                    TokenStatus.Present, TokenStatus.Active, 1);

                OnTokenStatusChanged(tokenId, TokenStatus.Active);

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to open session for token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token oturumu kapat;
        /// </summary>
        public async Task CloseSessionAsync(string tokenId)
        {
            if (!_activeSessions.TryGetValue(tokenId, out var session))
                return;

            await _operationLock.WaitAsync();

            try
            {
                // Oturumu kapat;
                await session.CloseAsync();

                // Oturumu listeden kaldır;
                _activeSessions.Remove(tokenId);

                _logger.LogInformation($"Session closed for token: {tokenId}");
                LogSecurityEvent("SessionClose", tokenId, "Token session closed",
                    TokenStatus.Active, TokenStatus.Present, 1);

                OnTokenStatusChanged(tokenId, TokenStatus.Present);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to close session for token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Tüm oturumları kapat;
        /// </summary>
        public async Task CloseAllSessionsAsync()
        {
            await _operationLock.WaitAsync();

            try
            {
                var tokenIds = _activeSessions.Keys.ToList();

                foreach (var tokenId in tokenIds)
                {
                    try
                    {
                        await CloseSessionAsync(tokenId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error closing session for token {tokenId}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        #endregion;

        #region Public Methods - PIN Management;

        /// <summary>
        /// PIN doğrula;
        /// </summary>
        public async Task<PinVerificationResult> VerifyPinAsync(string tokenId, string pin)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out var tokenInfo))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (IsTokenLocked(tokenId))
            {
                var lockedUntil = _tokenLockouts[tokenId];
                return new PinVerificationResult(
                    false, 0, true, lockedUntil, "Token is locked");
            }

            await _operationLock.WaitAsync();

            try
            {
                // PIN uzunluğunu kontrol et;
                if (pin.Length < _config.MinPinLength || pin.Length > _config.MaxPinLength)
                {
                    return new PinVerificationResult(
                        false, GetRemainingPinAttempts(tokenId),
                        false, null, $"PIN length must be between {_config.MinPinLength} and {_config.MaxPinLength}");
                }

                // PIN karmaşıklığını kontrol et;
                if (_config.EnablePinComplexity && !IsPinComplexEnough(pin))
                {
                    return new PinVerificationResult(
                        false, GetRemainingPinAttempts(tokenId),
                        false, null, "PIN does not meet complexity requirements");
                }

                // Token'da PIN doğrulaması yap;
                bool isValid = await VerifyPinOnTokenAsync(tokenId, pin);

                if (isValid)
                {
                    // Başarılı doğrulama;
                    ResetPinAttempts(tokenId);

                    _logger.LogInformation($"PIN verified successfully for token: {tokenId}");
                    LogSecurityEvent("PinVerified", tokenId, "PIN verified successfully",
                        TokenStatus.Present, TokenStatus.Present, 1);

                    OnPinVerified(tokenId);

                    return new PinVerificationResult(
                        true, _config.MaxPinAttempts, false, null, "PIN verified successfully");
                }
                else;
                {
                    // Başarısız doğrulama;
                    IncrementPinAttempts(tokenId);
                    int remaining = GetRemainingPinAttempts(tokenId);

                    _logger.LogWarning($"Invalid PIN attempt for token: {tokenId}. Remaining attempts: {remaining}");
                    LogSecurityEvent("PinFailed", tokenId, $"Invalid PIN attempt. Remaining: {remaining}",
                        TokenStatus.Present, TokenStatus.Present, 3);

                    if (remaining <= 0)
                    {
                        // Token'ı kilitle;
                        await LockTokenAsync(tokenId);

                        return new PinVerificationResult(
                            false, 0, true, DateTime.UtcNow.AddMinutes(_config.LockoutDurationMinutes),
                            "Token locked due to too many failed attempts");
                    }

                    return new PinVerificationResult(
                        false, remaining, false, null, $"Invalid PIN. Remaining attempts: {remaining}");
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// PIN değiştir;
        /// </summary>
        public async Task<bool> ChangePinAsync(string tokenId, string oldPin, string newPin)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (IsTokenLocked(tokenId))
                throw new TokenLockedException($"Token is locked: {tokenId}");

            // Eski PIN'i doğrula;
            var verification = await VerifyPinAsync(tokenId, oldPin);

            if (!verification.IsValid)
                return false;

            // Yeni PIN gereksinimlerini kontrol et;
            if (newPin.Length < _config.MinPinLength || newPin.Length > _config.MaxPinLength)
                throw new ArgumentException($"PIN length must be between {_config.MinPinLength} and {_config.MaxPinLength}");

            if (_config.EnablePinComplexity && !IsPinComplexEnough(newPin))
                throw new ArgumentException("PIN does not meet complexity requirements");

            await _operationLock.WaitAsync();

            try
            {
                // Token'da PIN değiştir;
                bool success = await ChangePinOnTokenAsync(tokenId, oldPin, newPin);

                if (success)
                {
                    _logger.LogInformation($"PIN changed successfully for token: {tokenId}");
                    LogSecurityEvent("PinChanged", tokenId, "PIN changed successfully",
                        TokenStatus.Present, TokenStatus.Present, 2);

                    return true;
                }

                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token kilidini aç (yönetici işlemi)
        /// </summary>
        public async Task<bool> UnlockTokenAsync(string tokenId, string adminPin)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            await _operationLock.WaitAsync();

            try
            {
                // Yönetici PIN'i ile kilidi aç;
                bool success = await UnlockTokenWithAdminPinAsync(tokenId, adminPin);

                if (success)
                {
                    ResetPinAttempts(tokenId);
                    _tokenLockouts.Remove(tokenId);

                    _logger.LogInformation($"Token unlocked: {tokenId}");
                    LogSecurityEvent("TokenUnlocked", tokenId, "Token unlocked by administrator",
                        TokenStatus.Locked, TokenStatus.Present, 3);

                    OnTokenStatusChanged(tokenId, TokenStatus.Present);

                    return true;
                }

                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token'ı sıfırla (fabrika ayarları)
        /// </summary>
        public async Task<bool> ResetTokenAsync(string tokenId, string adminPin)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            await _operationLock.WaitAsync();

            try
            {
                // Önce kilidi aç;
                bool unlocked = await UnlockTokenAsync(tokenId, adminPin);

                if (!unlocked)
                    return false;

                // Token'ı sıfırla;
                bool success = await ResetTokenToFactoryAsync(tokenId);

                if (success)
                {
                    // Tüm kayıtları temizle;
                    _tokenKeys.Remove(tokenId);
                    _pinAttempts.Remove(tokenId);
                    _tokenLockouts.Remove(tokenId);

                    if (_activeSessions.ContainsKey(tokenId))
                    {
                        _activeSessions.Remove(tokenId);
                    }

                    _logger.LogWarning($"Token reset to factory settings: {tokenId}");
                    LogSecurityEvent("TokenReset", tokenId, "Token reset to factory settings",
                        TokenStatus.Present, TokenStatus.Present, 4);

                    return true;
                }

                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        #endregion;

        #region Public Methods - Key Management;

        /// <summary>
        /// Anahtar oluştur;
        /// </summary>
        public async Task<TokenKey> GenerateKeyAsync(
            string tokenId,
            KeyGenerationParams parameters)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            // Parametreleri doğrula;
            ValidateKeyGenerationParams(parameters);

            await _operationLock.WaitAsync();

            try
            {
                // Anahtar ID oluştur;
                string keyId = await GenerateKeyIdAsync();

                // Token'da anahtar oluştur;
                bool success = parameters.GenerateOnToken;
                    ? await GenerateKeyOnTokenAsync(tokenId, keyId, parameters)
                    : await ImportKeyToTokenAsync(tokenId, keyId, parameters);

                if (!success)
                    throw new TokenOperationException($"Failed to generate key on token: {tokenId}");

                // Anahtar nesnesi oluştur;
                var key = new TokenKey(
                    keyId,
                    parameters.KeyLabel,
                    parameters.KeyType,
                    parameters.KeySize,
                    DateTime.UtcNow.Add(parameters.ValidityPeriod),
                    parameters.UsageFlags,
                    parameters.Owner,
                    parameters.Application,
                    parameters.KeyAttributes);

                // Anahtarı sakla;
                if (!_tokenKeys.ContainsKey(tokenId))
                {
                    _tokenKeys[tokenId] = new List<TokenKey>();
                }

                _tokenKeys[tokenId].Add(key);

                _logger.LogInformation($"Key generated on token {tokenId}: {parameters.KeyLabel}");
                LogSecurityEvent("KeyGenerated", tokenId, $"Generated {parameters.KeyType} key: {parameters.KeyLabel}",
                    TokenStatus.Active, TokenStatus.Active, 2);

                OnKeyOperationPerformed($"Generated key: {parameters.KeyLabel} on token: {tokenId}");

                return key;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key generation failed for token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar listesini al;
        /// </summary>
        public async Task<List<TokenKey>> ListKeysAsync(string tokenId)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            await _operationLock.WaitAsync();

            try
            {
                // Bellekteki anahtarları getir;
                if (_tokenKeys.TryGetValue(tokenId, out var keys))
                {
                    return keys.ToList();
                }

                // Token'dan anahtarları oku;
                var tokenKeys = await ReadKeysFromTokenAsync(tokenId);

                if (tokenKeys != null)
                {
                    _tokenKeys[tokenId] = tokenKeys;
                    return tokenKeys.ToList();
                }

                return new List<TokenKey>();
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtarı bul;
        /// </summary>
        public async Task<TokenKey> FindKeyAsync(string tokenId, string keyLabel)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            var keys = await ListKeysAsync(tokenId);
            return keys.FirstOrDefault(k => k.KeyLabel == keyLabel);
        }

        /// <summary>
        /// Anahtarı sil;
        /// </summary>
        public async Task<bool> DeleteKeyAsync(string tokenId, string keyId)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            await _operationLock.WaitAsync();

            try
            {
                // Token'dan anahtarı sil;
                bool success = await DeleteKeyFromTokenAsync(tokenId, keyId);

                if (success)
                {
                    // Bellekten kaldır;
                    if (_tokenKeys.TryGetValue(tokenId, out var keys))
                    {
                        var keyToRemove = keys.FirstOrDefault(k => k.KeyId == keyId);
                        if (keyToRemove != null)
                        {
                            keys.Remove(keyToRemove);
                        }
                    }

                    _logger.LogInformation($"Key deleted from token {tokenId}: {keyId}");
                    LogSecurityEvent("KeyDeleted", tokenId, $"Deleted key: {keyId}",
                        TokenStatus.Active, TokenStatus.Active, 2);

                    OnKeyOperationPerformed($"Deleted key: {keyId} from token: {tokenId}");

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key deletion failed for token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtarı dışa aktar (eğer izin veriliyorsa)
        /// </summary>
        public async Task<byte[]> ExportKeyAsync(
            string tokenId,
            string keyId,
            KeyEncryptionParams encryptionParams = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            await _operationLock.WaitAsync();

            try
            {
                // Anahtarı bul;
                var keys = await ListKeysAsync(tokenId);
                var key = keys.FirstOrDefault(k => k.KeyId == keyId);

                if (key == null)
                    throw new KeyNotFoundException($"Key not found: {keyId}");

                // Dışa aktarılabilir mi kontrol et;
                if (!key.IsExportable)
                    throw new InvalidOperationException($"Key is not exportable: {keyId}");

                // Token'dan anahtarı dışa aktar;
                byte[] exportedKey = await ExportKeyFromTokenAsync(tokenId, keyId, encryptionParams);

                if (exportedKey == null)
                    throw new TokenOperationException($"Failed to export key: {keyId}");

                _logger.LogInformation($"Key exported from token {tokenId}: {keyId}");
                LogSecurityEvent("KeyExported", tokenId, $"Exported key: {keyId}",
                    TokenStatus.Active, TokenStatus.Active, 3);

                OnKeyOperationPerformed($"Exported key: {keyId} from token: {tokenId}");

                return exportedKey;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key export failed for token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtarı içe aktar;
        /// </summary>
        public async Task<TokenKey> ImportKeyAsync(
            string tokenId,
            byte[] keyData,
            KeyGenerationParams parameters,
            KeyEncryptionParams encryptionParams = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (keyData == null || keyData.Length == 0)
                throw new ArgumentException("Key data cannot be empty", nameof(keyData));

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            await _operationLock.WaitAsync();

            try
            {
                // Anahtar ID oluştur;
                string keyId = await GenerateKeyIdAsync();

                // Token'a anahtarı içe aktar;
                bool success = await ImportKeyToTokenAsync(tokenId, keyId, keyData, parameters, encryptionParams);

                if (!success)
                    throw new TokenOperationException($"Failed to import key to token: {tokenId}");

                // Anahtar nesnesi oluştur;
                var key = new TokenKey(
                    keyId,
                    parameters.KeyLabel,
                    parameters.KeyType,
                    parameters.KeySize,
                    DateTime.UtcNow.Add(parameters.ValidityPeriod),
                    parameters.UsageFlags,
                    parameters.Owner,
                    parameters.Application,
                    parameters.KeyAttributes);

                // Anahtarı sakla;
                if (!_tokenKeys.ContainsKey(tokenId))
                {
                    _tokenKeys[tokenId] = new List<TokenKey>();
                }

                _tokenKeys[tokenId].Add(key);

                _logger.LogInformation($"Key imported to token {tokenId}: {parameters.KeyLabel}");
                LogSecurityEvent("KeyImported", tokenId, $"Imported {parameters.KeyType} key: {parameters.KeyLabel}",
                    TokenStatus.Active, TokenStatus.Active, 2);

                OnKeyOperationPerformed($"Imported key: {parameters.KeyLabel} to token: {tokenId}");

                return key;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key import failed for token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        #endregion;

        #region Public Methods - Cryptographic Operations;

        /// <summary>
        /// Veriyi şifrele;
        /// </summary>
        public async Task<byte[]> EncryptAsync(
            string tokenId,
            string keyId,
            byte[] plaintext,
            byte[] iv = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (plaintext == null || plaintext.Length == 0)
                throw new ArgumentException("Plaintext cannot be empty", nameof(plaintext));

            await _operationLock.WaitAsync();

            try
            {
                // Anahtarı bul;
                var keys = await ListKeysAsync(tokenId);
                var key = keys.FirstOrDefault(k => k.KeyId == keyId);

                if (key == null)
                    throw new KeyNotFoundException($"Key not found: {keyId}");

                if (!key.CanEncrypt)
                    throw new InvalidOperationException($"Key does not support encryption: {keyId}");

                if (!key.IsActive)
                    throw new InvalidOperationException($"Key is not active: {keyId}");

                // Token'da şifreleme yap;
                byte[] ciphertext = await EncryptOnTokenAsync(tokenId, keyId, plaintext, iv);

                _logger.LogDebug($"Data encrypted using key {keyId} on token {tokenId}");
                LogSecurityEvent("Encryption", tokenId, $"Encrypted {plaintext.Length} bytes with key: {keyId}",
                    TokenStatus.Active, TokenStatus.Active, 1);

                OnKeyOperationPerformed($"Encrypted data using key: {keyId} on token: {tokenId}");

                return ciphertext;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Encryption failed on token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Şifreyi çöz;
        /// </summary>
        public async Task<byte[]> DecryptAsync(
            string tokenId,
            string keyId,
            byte[] ciphertext,
            byte[] iv = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (ciphertext == null || ciphertext.Length == 0)
                throw new ArgumentException("Ciphertext cannot be empty", nameof(ciphertext));

            await _operationLock.WaitAsync();

            try
            {
                // Anahtarı bul;
                var keys = await ListKeysAsync(tokenId);
                var key = keys.FirstOrDefault(k => k.KeyId == keyId);

                if (key == null)
                    throw new KeyNotFoundException($"Key not found: {keyId}");

                if (!key.CanDecrypt)
                    throw new InvalidOperationException($"Key does not support decryption: {keyId}");

                if (!key.IsActive)
                    throw new InvalidOperationException($"Key is not active: {keyId}");

                // Token'da şifre çözme yap;
                byte[] plaintext = await DecryptOnTokenAsync(tokenId, keyId, ciphertext, iv);

                _logger.LogDebug($"Data decrypted using key {keyId} on token {tokenId}");
                LogSecurityEvent("Decryption", tokenId, $"Decrypted {ciphertext.Length} bytes with key: {keyId}",
                    TokenStatus.Active, TokenStatus.Active, 1);

                OnKeyOperationPerformed($"Decrypted data using key: {keyId} on token: {tokenId}");

                return plaintext;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Decryption failed on token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Veriyi imzala;
        /// </summary>
        public async Task<byte[]> SignAsync(
            string tokenId,
            string keyId,
            byte[] data,
            string hashAlgorithm = "SHA256")
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be empty", nameof(data));

            await _operationLock.WaitAsync();

            try
            {
                // Anahtarı bul;
                var keys = await ListKeysAsync(tokenId);
                var key = keys.FirstOrDefault(k => k.KeyId == keyId);

                if (key == null)
                    throw new KeyNotFoundException($"Key not found: {keyId}");

                if (!key.CanSign)
                    throw new InvalidOperationException($"Key does not support signing: {keyId}");

                if (!key.IsActive)
                    throw new InvalidOperationException($"Key is not active: {keyId}");

                // Token'da imzalama yap;
                byte[] signature = await SignOnTokenAsync(tokenId, keyId, data, hashAlgorithm);

                _logger.LogDebug($"Data signed using key {keyId} on token {tokenId}");
                LogSecurityEvent("Signing", tokenId, $"Signed {data.Length} bytes with key: {keyId}",
                    TokenStatus.Active, TokenStatus.Active, 1);

                OnKeyOperationPerformed($"Signed data using key: {keyId} on token: {tokenId}");

                return signature;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Signing failed on token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// İmzayı doğrula;
        /// </summary>
        public async Task<bool> VerifyAsync(
            string tokenId,
            string keyId,
            byte[] data,
            byte[] signature,
            string hashAlgorithm = "SHA256")
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be empty", nameof(data));

            if (signature == null || signature.Length == 0)
                throw new ArgumentException("Signature cannot be empty", nameof(signature));

            await _operationLock.WaitAsync();

            try
            {
                // Anahtarı bul;
                var keys = await ListKeysAsync(tokenId);
                var key = keys.FirstOrDefault(k => k.KeyId == keyId);

                if (key == null)
                    throw new KeyNotFoundException($"Key not found: {keyId}");

                if (!key.CanVerify)
                    throw new InvalidOperationException($"Key does not support verification: {keyId}");

                if (!key.IsActive)
                    throw new InvalidOperationException($"Key is not active: {keyId}");

                // Token'da doğrulama yap;
                bool isValid = await VerifyOnTokenAsync(tokenId, keyId, data, signature, hashAlgorithm);

                _logger.LogDebug($"Signature verification result: {isValid} on token {tokenId}");
                LogSecurityEvent("Verification", tokenId, $"Verified signature for {data.Length} bytes",
                    TokenStatus.Active, TokenStatus.Active, 1);

                OnKeyOperationPerformed($"Verified signature using key: {keyId} on token: {tokenId}");

                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Verification failed on token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar sarmala (wrap)
        /// </summary>
        public async Task<byte[]> WrapKeyAsync(
            string tokenId,
            string wrappingKeyId,
            byte[] keyToWrap,
            KeyEncryptionParams encryptionParams = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (keyToWrap == null || keyToWrap.Length == 0)
                throw new ArgumentException("Key to wrap cannot be empty", nameof(keyToWrap));

            await _operationLock.WaitAsync();

            try
            {
                // Sarmalama anahtarını bul;
                var keys = await ListKeysAsync(tokenId);
                var wrappingKey = keys.FirstOrDefault(k => k.KeyId == wrappingKeyId);

                if (wrappingKey == null)
                    throw new KeyNotFoundException($"Wrapping key not found: {wrappingKeyId}");

                if ((wrappingKey.UsageFlags & KeyUsageFlags.Wrap) == 0)
                    throw new InvalidOperationException($"Key does not support wrapping: {wrappingKeyId}");

                // Token'da anahtar sarmalama yap;
                byte[] wrappedKey = await WrapKeyOnTokenAsync(tokenId, wrappingKeyId, keyToWrap, encryptionParams);

                _logger.LogDebug($"Key wrapped using wrapping key {wrappingKeyId} on token {tokenId}");
                LogSecurityEvent("KeyWrap", tokenId, $"Wrapped key using wrapping key: {wrappingKeyId}",
                    TokenStatus.Active, TokenStatus.Active, 2);

                OnKeyOperationPerformed($"Wrapped key using wrapping key: {wrappingKeyId} on token: {tokenId}");

                return wrappedKey;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key wrapping failed on token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar aç (unwrap)
        /// </summary>
        public async Task<byte[]> UnwrapKeyAsync(
            string tokenId,
            string unwrappingKeyId,
            byte[] wrappedKey,
            KeyEncryptionParams encryptionParams = null)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                throw new TokenNotFoundException($"Token not found: {tokenId}");

            if (!_activeSessions.ContainsKey(tokenId))
                throw new InvalidOperationException($"No active session for token: {tokenId}");

            if (wrappedKey == null || wrappedKey.Length == 0)
                throw new ArgumentException("Wrapped key cannot be empty", nameof(wrappedKey));

            await _operationLock.WaitAsync();

            try
            {
                // Açma anahtarını bul;
                var keys = await ListKeysAsync(tokenId);
                var unwrappingKey = keys.FirstOrDefault(k => k.KeyId == unwrappingKeyId);

                if (unwrappingKey == null)
                    throw new KeyNotFoundException($"Unwrapping key not found: {unwrappingKeyId}");

                if ((unwrappingKey.UsageFlags & KeyUsageFlags.Unwrap) == 0)
                    throw new InvalidOperationException($"Key does not support unwrapping: {unwrappingKeyId}");

                // Token'da anahtar açma yap;
                byte[] unwrappedKey = await UnwrapKeyOnTokenAsync(tokenId, unwrappingKeyId, wrappedKey, encryptionParams);

                _logger.LogDebug($"Key unwrapped using unwrapping key {unwrappingKeyId} on token {tokenId}");
                LogSecurityEvent("KeyUnwrap", tokenId, $"Unwrapped key using unwrapping key: {unwrappingKeyId}",
                    TokenStatus.Active, TokenStatus.Active, 2);

                OnKeyOperationPerformed($"Unwrapped key using unwrapping key: {unwrappingKeyId} on token: {tokenId}");

                return unwrappedKey;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key unwrapping failed on token {tokenId}: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        #endregion;

        #region Private Methods - Token Detection;

        /// <summary>
        /// Tokenları tespit et;
        /// </summary>
        private async void DetectTokens(object state)
        {
            if (!_isMonitoring || _detectionCancellationTokenSource?.Token.IsCancellationRequested == true)
                return;

            try
            {
                await DetectConnectedTokensAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Token detection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Bağlı tokenları tespit et;
        /// </summary>
        private async Task DetectConnectedTokensAsync()
        {
            try
            {
                // Sistemdeki tokenları tara;
                var detectedTokens = await ScanForTokensAsync();

                // Yeni tokenları ekle;
                foreach (var token in detectedTokens)
                {
                    if (!_connectedTokens.ContainsKey(token.TokenId))
                    {
                        await HandleTokenInsertionAsync(token);
                    }
                }

                // Eksik tokenları kaldır;
                var connectedTokenIds = _connectedTokens.Keys.ToList();
                foreach (var tokenId in connectedTokenIds)
                {
                    if (!detectedTokens.Any(t => t.TokenId == tokenId))
                    {
                        await HandleTokenRemovalAsync(tokenId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Token detection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Token ekleme işlemi;
        /// </summary>
        private async Task HandleTokenInsertionAsync(TokenInfo tokenInfo)
        {
            await _operationLock.WaitAsync();

            try
            {
                // Token güvenlik seviyesini kontrol et;
                if (tokenInfo.SecurityLevel < _config.RequiredSecurityLevel)
                {
                    _logger.LogWarning($"Token security level insufficient: {tokenInfo.SecurityLevel}");
                    LogSecurityEvent("TokenRejected", tokenInfo.TokenId,
                        $"Token rejected due to insufficient security level: {tokenInfo.SecurityLevel}",
                        TokenStatus.NotPresent, TokenStatus.Invalid, 3);
                    return;
                }

                // Üretici kontrolü;
                if (_config.AllowedManufacturers.Count > 0 &&
                    !_config.AllowedManufacturers.Contains(tokenInfo.Manufacturer, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"Token manufacturer not allowed: {tokenInfo.Manufacturer}");
                    LogSecurityEvent("TokenRejected", tokenInfo.TokenId,
                        $"Token rejected due to manufacturer restriction: {tokenInfo.Manufacturer}",
                        TokenStatus.NotPresent, TokenStatus.Invalid, 3);
                    return;
                }

                // Model kontrolü;
                if (_config.BlockedModels.Contains(tokenInfo.Model, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"Token model blocked: {tokenInfo.Model}");
                    LogSecurityEvent("TokenRejected", tokenInfo.TokenId,
                        $"Token rejected due to model restriction: {tokenInfo.Model}",
                        TokenStatus.NotPresent, TokenStatus.Invalid, 3);
                    return;
                }

                // Token'ı ekle;
                _connectedTokens[tokenInfo.TokenId] = tokenInfo;

                // PIN deneme sayacını sıfırla;
                _pinAttempts[tokenInfo.TokenId] = _config.MaxPinAttempts;

                _logger.LogInformation($"Token inserted: {tokenInfo.Manufacturer} {tokenInfo.Model} ({tokenInfo.TokenId})");
                LogSecurityEvent("TokenInserted", tokenInfo.TokenId,
                    $"Token inserted: {tokenInfo.Manufacturer} {tokenInfo.Model}",
                    TokenStatus.NotPresent, TokenStatus.Present, 1);

                OnTokenInserted(tokenInfo);
                OnTokenStatusChanged(tokenInfo.TokenId, TokenStatus.Present);

                // Denetim günlüğüne kaydet;
                await _auditLogger.LogAuditEventAsync(
                    "TokenInserted",
                    $"USB Crypto Token inserted: {tokenInfo.Manufacturer} {tokenInfo.Model}",
                    "TokenManager",
                    tokenInfo.TokenId);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token çıkarma işlemi;
        /// </summary>
        private async Task HandleTokenRemovalAsync(string tokenId)
        {
            await _operationLock.WaitAsync();

            try
            {
                if (!_connectedTokens.TryGetValue(tokenId, out var tokenInfo))
                    return;

                // Aktif oturum varsa kapat;
                if (_activeSessions.ContainsKey(tokenId))
                {
                    try
                    {
                        await CloseSessionAsync(tokenId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error closing session during token removal: {ex.Message}");
                    }
                }

                // Kayıtları temizle;
                _connectedTokens.Remove(tokenId);
                _tokenKeys.Remove(tokenId);
                _pinAttempts.Remove(tokenId);
                _tokenLockouts.Remove(tokenId);

                _logger.LogInformation($"Token removed: {tokenInfo.Manufacturer} {tokenInfo.Model} ({tokenId})");
                LogSecurityEvent("TokenRemoved", tokenId,
                    $"Token removed: {tokenInfo.Manufacturer} {tokenInfo.Model}",
                    TokenStatus.Present, TokenStatus.NotPresent, 1);

                OnTokenRemoved(tokenId);
                OnTokenStatusChanged(tokenId, TokenStatus.NotPresent);

                // Denetim günlüğüne kaydet;
                await _auditLogger.LogAuditEventAsync(
                    "TokenRemoved",
                    $"USB Crypto Token removed: {tokenInfo.Manufacturer} {tokenInfo.Model}",
                    "TokenManager",
                    tokenId);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Token kilitleme işlemi;
        /// </summary>
        private async Task HandleTokenLockoutAsync(string tokenId)
        {
            await _operationLock.WaitAsync();

            try
            {
                await LockTokenAsync(tokenId);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        #endregion;

        #region Private Methods - PIN Management;

        /// <summary>
        /// Token kilitli mi?
        /// </summary>
        private bool IsTokenLocked(string tokenId)
        {
            if (_tokenLockouts.TryGetValue(tokenId, out var lockoutUntil))
            {
                return DateTime.UtcNow < lockoutUntil;
            }

            return false;
        }

        /// <summary>
        /// Kalan PIN deneme sayısı;
        /// </summary>
        private int GetRemainingPinAttempts(string tokenId)
        {
            if (_pinAttempts.TryGetValue(tokenId, out var attempts))
            {
                return attempts;
            }

            return _config.MaxPinAttempts;
        }

        /// <summary>
        /// PIN deneme sayısını artır;
        /// </summary>
        private void IncrementPinAttempts(string tokenId)
        {
            if (_pinAttempts.ContainsKey(tokenId))
            {
                _pinAttempts[tokenId]--;

                if (_pinAttempts[tokenId] < 0)
                {
                    _pinAttempts[tokenId] = 0;
                }
            }
            else;
            {
                _pinAttempts[tokenId] = _config.MaxPinAttempts - 1;
            }
        }

        /// <summary>
        /// PIN deneme sayısını sıfırla;
        /// </summary>
        private void ResetPinAttempts(string tokenId)
        {
            if (_pinAttempts.ContainsKey(tokenId))
            {
                _pinAttempts[tokenId] = _config.MaxPinAttempts;
            }
        }

        /// <summary>
        /// Token'ı kilitle;
        /// </summary>
        private async Task LockTokenAsync(string tokenId)
        {
            if (!_connectedTokens.TryGetValue(tokenId, out _))
                return;

            // Kilitleme süresini ayarla;
            _tokenLockouts[tokenId] = DateTime.UtcNow.AddMinutes(_config.LockoutDurationMinutes);

            // Aktif oturum varsa kapat;
            if (_activeSessions.ContainsKey(tokenId))
            {
                try
                {
                    await CloseSessionAsync(tokenId);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error closing session during token lock: {ex.Message}");
                }
            }

            _logger.LogWarning($"Token locked: {tokenId}");
            LogSecurityEvent("TokenLocked", tokenId,
                $"Token locked due to too many failed PIN attempts",
                TokenStatus.Present, TokenStatus.Locked, 4);

            OnTokenLocked(tokenId);
            OnTokenStatusChanged(tokenId, TokenStatus.Locked);
        }

        /// <summary>
        /// PIN karmaşıklık kontrolü;
        /// </summary>
        private bool IsPinComplexEnough(string pin)
        {
            // En az bir rakam;
            bool hasDigit = pin.Any(char.IsDigit);

            // En az bir harf;
            bool hasLetter = pin.Any(char.IsLetter);

            // En az bir özel karakter;
            bool hasSpecial = pin.Any(c => !char.IsLetterOrDigit(c));

            // En az 3 farklı karakter türü gereksinimi;
            int characterTypes = 0;
            if (hasDigit) characterTypes++;
            if (hasLetter) characterTypes++;
            if (hasSpecial) characterTypes++;

            return characterTypes >= 2; // En az 2 farklı karakter türü;
        }

        #endregion;

        #region Private Methods - Key Management;

        /// <summary>
        /// Anahtar oluşturma parametrelerini doğrula;
        /// </summary>
        private void ValidateKeyGenerationParams(KeyGenerationParams parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters.KeyLabel))
                throw new ArgumentException("Key label cannot be empty", nameof(parameters.KeyLabel));

            if (parameters.KeySize <= 0)
                throw new ArgumentException("Key size must be positive", nameof(parameters.KeySize));

            if (parameters.ValidityPeriod <= TimeSpan.Zero)
                throw new ArgumentException("Validity period must be positive", nameof(parameters.ValidityPeriod));

            // Anahtar türüne göre boyut kontrolü;
            switch (parameters.KeyType)
            {
                case TokenKeyType.AES:
                    if (parameters.KeySize != 128 && parameters.KeySize != 192 && parameters.KeySize != 256)
                        throw new ArgumentException($"Invalid AES key size: {parameters.KeySize}. Must be 128, 192, or 256.");
                    break;

                case TokenKeyType.RSA:
                    if (parameters.KeySize < 2048 || parameters.KeySize > 4096 || parameters.KeySize % 8 != 0)
                        throw new ArgumentException($"Invalid RSA key size: {parameters.KeySize}. Must be between 2048 and 4096, divisible by 8.");
                    break;

                case TokenKeyType.ECC:
                    if (parameters.KeySize != 256 && parameters.KeySize != 384 && parameters.KeySize != 521)
                        throw new ArgumentException($"Invalid ECC key size: {parameters.KeySize}. Must be 256, 384, or 521.");
                    break;
            }
        }

        /// <summary>
        /// Anahtar ID oluştur;
        /// </summary>
        private async Task<string> GenerateKeyIdAsync()
        {
            var randomBytes = new byte[16];
            _rng.GetBytes(randomBytes);
            return BitConverter.ToString(randomBytes).Replace("-", "").ToLower();
        }

        #endregion;

        #region Private Methods - Security Event Logging;

        /// <summary>
        /// Güvenlik olayını kaydet;
        /// </summary>
        private void LogSecurityEvent(
            string eventType,
            string tokenId,
            string description,
            TokenStatus before,
            TokenStatus after,
            int severity)
        {
            var securityEvent = new TokenEvent(eventType, tokenId, description, before, after, severity);

            _securityEvents.Enqueue(securityEvent);

            // Kuyruk boyutunu sınırla (son 1000 olay)
            while (_securityEvents.Count > 1000)
            {
                _securityEvents.Dequeue();
            }

            OnSecurityEventOccurred(securityEvent);

            // Yüksek önemdeki olayları denetim günlüğüne kaydet;
            if (severity >= 3)
            {
                _auditLogger.LogAuditEventAsync(
                    $"Token_{eventType}",
                    description,
                    "TokenManager",
                    tokenId).ConfigureAwait(false);
            }
        }

        #endregion;

        #region Hardware Abstraction Methods;

        // Bu metodlar gerçek donanım API'leri ile implemente edilecek;

        private async Task<List<TokenInfo>> ScanForTokensAsync()
        {
            await Task.Delay(100); // Simülasyon gecikmesi;

            // Simülasyon: Rastgele token tespiti;
            var random = new Random();
            int tokenCount = random.Next(0, 3); // 0-2 arası token;

            var tokens = new List<TokenInfo>();

            for (int i = 0; i < tokenCount; i++)
            {
                var token = new TokenInfo;
                {
                    TokenId = $"TOKEN_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                    Manufacturer = "NEDA Security",
                    Model = "CryptoToken Pro",
                    SerialNumber = $"SN{DateTime.Now:yyyyMMddHHmmss}{i}",
                    FirmwareVersion = "2.3.1",
                    HardwareVersion = "1.2",
                    TotalMemory = 8192,
                    FreeMemory = 4096,
                    MaxPinAttempts = 10,
                    RemainingPinAttempts = 10,
                    HasSecureDisplay = true,
                    HasKeypad = true,
                    HasBiometric = false,
                    SecurityLevel = TokenSecurityLevel.High,
                    ManufacturingDate = new DateTime(2023, 1, 1),
                    InitializationDate = DateTime.UtcNow.AddDays(-random.Next(1, 365)),
                    LastUsedDate = DateTime.UtcNow.AddHours(-random.Next(1, 24))
                };

                token.SupportedAlgorithms.AddRange(new[] { "AES", "RSA", "ECC", "SHA2", "SHA3" });

                tokens.Add(token);
            }

            return tokens;
        }

        private async Task<bool> VerifyPinOnTokenAsync(string tokenId, string pin)
        {
            await Task.Delay(50); // Donanım gecikmesi;

            // Simülasyon: Sabit PIN "123456"
            return pin == "123456";
        }

        private async Task<bool> ChangePinOnTokenAsync(string tokenId, string oldPin, string newPin)
        {
            await Task.Delay(100); // Donanım gecikmesi;

            // Simülasyon: Eski PIN doğruysa değiştir;
            if (oldPin == "123456")
            {
                return true;
            }

            return false;
        }

        private async Task<bool> UnlockTokenWithAdminPinAsync(string tokenId, string adminPin)
        {
            await Task.Delay(50);

            // Simülasyon: Yönetici PIN'i "ADMIN123"
            return adminPin == "ADMIN123";
        }

        private async Task<bool> ResetTokenToFactoryAsync(string tokenId)
        {
            await Task.Delay(200); // Fabrika sıfırlama süresi;

            return true;
        }

        private async Task<bool> GenerateKeyOnTokenAsync(string tokenId, string keyId, KeyGenerationParams parameters)
        {
            await Task.Delay(100); // Anahtar oluşturma süresi;

            return true;
        }

        private async Task<bool> ImportKeyToTokenAsync(string tokenId, string keyId, KeyGenerationParams parameters)
        {
            await Task.Delay(50);

            return true;
        }

        private async Task<bool> ImportKeyToTokenAsync(string tokenId, string keyId, byte[] keyData,
            KeyGenerationParams parameters, KeyEncryptionParams encryptionParams)
        {
            await Task.Delay(100);

            return true;
        }

        private async Task<List<TokenKey>> ReadKeysFromTokenAsync(string tokenId)
        {
            await Task.Delay(50);

            // Simülasyon: Rastgele anahtarlar;
            var random = new Random();
            int keyCount = random.Next(1, 5);

            var keys = new List<TokenKey>();

            for (int i = 0; i < keyCount; i++)
            {
                var key = new TokenKey(
                    $"KEY_{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}",
                    $"Key_{i + 1}",
                    (TokenKeyType)random.Next(0, 3),
                    random.Next(0, 3) == 0 ? 128 : 256,
                    DateTime.UtcNow.AddDays(365),
                    KeyUsageFlags.Encrypt | KeyUsageFlags.Decrypt | KeyUsageFlags.Persistent,
                    "System",
                    "NEDA",
                    null);

                keys.Add(key);
            }

            return keys;
        }

        private async Task<bool> DeleteKeyFromTokenAsync(string tokenId, string keyId)
        {
            await Task.Delay(50);

            return true;
        }

        private async Task<byte[]> ExportKeyFromTokenAsync(string tokenId, string keyId, KeyEncryptionParams encryptionParams)
        {
            await Task.Delay(100);

            // Simülasyon: Rastgele anahtar verisi;
            var randomBytes = new byte[32];
            _rng.GetBytes(randomBytes);

            return randomBytes;
        }

        private async Task<byte[]> EncryptOnTokenAsync(string tokenId, string keyId, byte[] plaintext, byte[] iv)
        {
            await Task.Delay(50);

            // Simülasyon: Basit XOR şifreleme (gerçekte donanım şifrelemesi kullanılacak)
            byte[] key = new byte[32];
            _rng.GetBytes(key);

            byte[] ciphertext = new byte[plaintext.Length];
            for (int i = 0; i < plaintext.Length; i++)
            {
                ciphertext[i] = (byte)(plaintext[i] ^ key[i % key.Length]);
            }

            return ciphertext;
        }

        private async Task<byte[]> DecryptOnTokenAsync(string tokenId, string keyId, byte[] ciphertext, byte[] iv)
        {
            await Task.Delay(50);

            // Simülasyon: XOR şifre çözme;
            return await EncryptOnTokenAsync(tokenId, keyId, ciphertext, iv); // XOR tersinir;
        }

        private async Task<byte[]> SignOnTokenAsync(string tokenId, string keyId, byte[] data, string hashAlgorithm)
        {
            await Task.Delay(100);

            // Simülasyon: Sahte imza;
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(data);

            // İmza olarak hash'in ilk 64 byte'ını döndür;
            byte[] signature = new byte[64];
            Array.Copy(hash, 0, signature, 0, Math.Min(hash.Length, 64));

            return signature;
        }

        private async Task<bool> VerifyOnTokenAsync(string tokenId, string keyId, byte[] data, byte[] signature, string hashAlgorithm)
        {
            await Task.Delay(50);

            // Simülasyon: İmza doğrulama;
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(data);

            // Basit doğrulama: İmza uzunluğu 64 mü?
            return signature.Length == 64;
        }

        private async Task<byte[]> WrapKeyOnTokenAsync(string tokenId, string wrappingKeyId, byte[] keyToWrap, KeyEncryptionParams encryptionParams)
        {
            await Task.Delay(100);

            // Simülasyon: Anahtar sarmalama;
            byte[] wrappedKey = new byte[keyToWrap.Length + 32]; // 32 byte header ekle;
            _rng.GetBytes(wrappedKey, 0, 32); // Rastgele header;
            Array.Copy(keyToWrap, 0, wrappedKey, 32, keyToWrap.Length);

            return wrappedKey;
        }

        private async Task<byte[]> UnwrapKeyOnTokenAsync(string tokenId, string unwrappingKeyId, byte[] wrappedKey, KeyEncryptionParams encryptionParams)
        {
            await Task.Delay(100);

            // Simülasyon: Anahtar açma;
            if (wrappedKey.Length < 32)
                throw new ArgumentException("Invalid wrapped key");

            byte[] unwrappedKey = new byte[wrappedKey.Length - 32];
            Array.Copy(wrappedKey, 32, unwrappedKey, 0, unwrappedKey.Length);

            return unwrappedKey;
        }

        #endregion;

        #region Event Triggers;

        private void OnTokenInserted(TokenInfo tokenInfo)
        {
            TokenInserted?.Invoke(this, tokenInfo);
        }

        private void OnTokenRemoved(string tokenId)
        {
            TokenRemoved?.Invoke(this, tokenId);
        }

        private void OnTokenStatusChanged(string tokenId, TokenStatus status)
        {
            TokenStatusChanged?.Invoke(this, (tokenId, status));
        }

        private void OnPinVerified(string tokenId)
        {
            PinVerified?.Invoke(this, tokenId);
        }

        private void OnTokenLocked(string tokenId)
        {
            TokenLocked?.Invoke(this, tokenId);
        }

        private void OnKeyOperationPerformed(string operation)
        {
            KeyOperationPerformed?.Invoke(this, operation);
        }

        private void OnSecurityEventOccurred(TokenEvent tokenEvent)
        {
            SecurityEventOccurred?.Invoke(this, tokenEvent);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

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
                    // Yönetilen kaynakları serbest bırak;
                    try
                    {
                        StopMonitoringAsync().Wait();
                    }
                    catch
                    {
                        // Durdurma hatalarını görmezden gel;
                    }

                    _tokenDetectionTimer?.Dispose();
                    _operationLock?.Dispose();
                    _rng?.Dispose();
                    _detectionCancellationTokenSource?.Dispose();
                }

                _disposed = true;
            }
        }

        ~TokenManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Token oturumu;
    /// </summary>
    public class TokenSession : IDisposable
    {
        private readonly string _tokenId;
        private readonly TokenInfo _tokenInfo;
        private bool _isActive;
        private DateTime _sessionStartTime;
        private DateTime _lastActivityTime;

        public string TokenId => _tokenId;
        public TokenInfo TokenInfo => _tokenInfo;
        public bool IsActive => _isActive;
        public DateTime SessionStartTime => _sessionStartTime;
        public DateTime LastActivityTime => _lastActivityTime;
        public TimeSpan SessionDuration => DateTime.UtcNow - _sessionStartTime;

        public TokenSession(string tokenId, TokenInfo tokenInfo)
        {
            _tokenId = tokenId;
            _tokenInfo = tokenInfo;
            _isActive = false;
        }

        public async Task InitializeAsync()
        {
            if (_isActive)
                return;

            await Task.Delay(10); // Oturum başlatma gecikmesi;

            _sessionStartTime = DateTime.UtcNow;
            _lastActivityTime = _sessionStartTime;
            _isActive = true;
        }

        public async Task CloseAsync()
        {
            if (!_isActive)
                return;

            await Task.Delay(10); // Oturum kapatma gecikmesi;

            _isActive = false;
        }

        public void UpdateActivity()
        {
            _lastActivityTime = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_isActive)
            {
                CloseAsync().Wait();
            }
        }
    }

    /// <summary>
    /// Token bulunamadı istisnası;
    /// </summary>
    public class TokenNotFoundException : Exception
    {
        public TokenNotFoundException(string message) : base(message) { }
        public TokenNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Geçersiz PIN istisnası;
    /// </summary>
    public class InvalidPinException : Exception
    {
        public InvalidPinException(string message) : base(message) { }
        public InvalidPinException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Token kilitli istisnası;
    /// </summary>
    public class TokenLockedException : Exception
    {
        public TokenLockedException(string message) : base(message) { }
        public TokenLockedException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Token işlem istisnası;
    /// </summary>
    public class TokenOperationException : Exception
    {
        public TokenOperationException(string message) : base(message) { }
        public TokenOperationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Anahtar bulunamadı istisnası;
    /// </summary>
    public class KeyNotFoundException : Exception
    {
        public KeyNotFoundException(string message) : base(message) { }
        public KeyNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
