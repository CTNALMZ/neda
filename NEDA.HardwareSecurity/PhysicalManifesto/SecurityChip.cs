using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NEDA.HardwareSecurity.PhysicalManifesto;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.Logging;
using NEDA.Monitoring.HealthChecks;
using NEDA.ExceptionHandling;

namespace NEDA.HardwareSecurity.PhysicalManifesto;
{
    /// <summary>
    /// Güvenlik çipi durumu;
    /// </summary>
    public enum SecurityChipStatus;
    {
        /// <summary>Çip başlatıldı ve hazır</summary>
        Initialized = 0,

        /// <summary>Aktif ve çalışıyor</summary>
        Active = 1,

        /// <summary>Uyku modunda</summary>
        Standby = 2,

        /// <summary>Test modunda</summary>
        TestMode = 3,

        /// <summary>Arıza durumu</summary>
        Fault = 4,

        /// <summary>Fiziksel müdahale tespit edildi</summary>
        Tampered = 5,

        /// <summary>Çip devre dışı</summary>
        Disabled = 6,

        /// <summary>Kritik hata</summary>
        CriticalFailure = 7;
    }

    /// <summary>
    /// Güvenlik çipi modu;
    /// </summary>
    public enum SecurityChipMode;
    {
        /// <summary>Normal işletim modu</summary>
        Normal = 0,

        /// <summary>Yüksek güvenlik modu</summary>
        HighSecurity = 1,

        /// <summary>FIPS 140-2 uyumlu mod</summary>
        FIPS140 = 2,

        /// <summary>Kriptografik işlem modu</summary>
        Cryptographic = 3,

        /// <summary>Anahtar yönetim modu</summary>
        KeyManagement = 4,

        /// <summary>Donanım doğrulama modu</summary>
        HardwareValidation = 5,

        /// <summary>Güvenli önyükleme modu</summary>
        SecureBoot = 6;
    }

    /// <summary>
    /// Çip performans metrikleri;
    /// </summary>
    public class ChipPerformanceMetrics;
    {
        public int OperationsPerSecond { get; set; }
        public double PowerConsumption { get; set; }
        public double Temperature { get; set; }
        public int ErrorRate { get; set; }
        public TimeSpan AverageResponseTime { get; set; }
        public DateTime MeasurementTime { get; set; }

        public ChipPerformanceMetrics()
        {
            MeasurementTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Çip donanım bilgileri;
    /// </summary>
    public class ChipHardwareInfo;
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string FirmwareVersion { get; set; }
        public string HardwareVersion { get; set; }
        public DateTime ManufacturingDate { get; set; }
        public string SupportedAlgorithms { get; set; }
        public int MemorySize { get; set; }
        public int KeyStorageCapacity { get; set; }
    }

    /// <summary>
    /// Güvenlik olayı;
    /// </summary>
    public class SecurityEvent;
    {
        public DateTime Timestamp { get; }
        public string EventType { get; }
        public string Description { get; }
        public SecurityChipStatus StatusBefore { get; }
        public SecurityChipStatus StatusAfter { get; }
        public int Severity { get; }

        public SecurityEvent(string eventType, string description,
            SecurityChipStatus before, SecurityChipStatus after, int severity = 1)
        {
            Timestamp = DateTime.UtcNow;
            EventType = eventType;
            Description = description;
            StatusBefore = before;
            StatusAfter = after;
            Severity = severity;
        }
    }

    /// <summary>
    /// Çip yapılandırması;
    /// </summary>
    public class SecurityChipConfig;
    {
        public bool EnableHardwareAcceleration { get; set; } = true;
        public bool EnableTamperDetection { get; set; } = true;
        public bool EnablePowerAnalysisProtection { get; set; } = true;
        public bool EnableTemperatureMonitoring { get; set; } = true;
        public int TemperatureThreshold { get; set; } = 85; // °C;
        public bool EnableSecureKeyStorage { get; set; } = true;
        public int KeyRotationInterval { get; set; } = 86400; // saniye;
        public bool EnableAuditLogging { get; set; } = true;
        public int AuditLogSize { get; set; } = 10000;
        public SecurityChipMode DefaultMode { get; set; } = SecurityChipMode.Normal;
        public bool AutoSwitchToHighSecurity { get; set; } = true;
        public int HighSecurityThreshold { get; set; } = 3; // kritik olay sayısı;
    }

    /// <summary>
    /// Güvenlik çipi anahtarı;
    /// </summary>
    public class ChipKey;
    {
        public string KeyId { get; }
        public string KeyName { get; }
        public KeyType Type { get; }
        public int KeySize { get; }
        public DateTime CreatedAt { get; }
        public DateTime ExpiresAt { get; }
        public bool IsActive { get; set; }
        public bool IsExportable { get; }
        public string Usage { get; }

        public ChipKey(string keyId, string keyName, KeyType type,
            int keySize, DateTime expiresAt, bool isExportable = false, string usage = "General")
        {
            KeyId = keyId;
            KeyName = keyName;
            Type = type;
            KeySize = keySize;
            CreatedAt = DateTime.UtcNow;
            ExpiresAt = expiresAt;
            IsActive = true;
            IsExportable = isExportable;
            Usage = usage;
        }
    }

    /// <summary>
    /// Anahtar türü;
    /// </summary>
    public enum KeyType;
    {
        AES,
        RSA,
        ECC,
        HMAC,
        DRBG,
        Root,
        Master,
        Session;
    }

    /// <summary>
    /// Güvenlik çipi ana sınıfı;
    /// Fiziksel güvenlik çipi işlemlerini yönetir;
    /// </summary>
    public class SecurityChip : IDisposable
    {
        #region Events;

        /// <summary>Çip durumu değiştiğinde tetiklenir</summary>
        public event EventHandler<SecurityChipStatus> StatusChanged;

        /// <summary>Güvenlik olayı gerçekleştiğinde tetiklenir</summary>
        public event EventHandler<SecurityEvent> SecurityEventOccurred;

        /// <summary>Fiziksel müdahale tespit edildiğinde tetiklenir</summary>
        public event EventHandler<string> TamperDetected;

        /// <summary>Anahtar işlemi gerçekleştiğinde tetiklenir</summary>
        public event EventHandler<string> KeyOperationPerformed;

        /// <summary>Performans metrikleri güncellendiğinde tetiklenir</summary>
        public event EventHandler<ChipPerformanceMetrics> PerformanceMetricsUpdated;

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IManifestoValidator _manifestoValidator;

        private readonly SecurityChipConfig _config;
        private readonly Dictionary<string, ChipKey> _keys;
        private readonly Queue<SecurityEvent> _securityEvents;
        private readonly List<ChipPerformanceMetrics> _performanceHistory;

        private SecurityChipStatus _currentStatus = SecurityChipStatus.Disabled;
        private SecurityChipMode _currentMode;
        private ChipHardwareInfo _hardwareInfo;
        private bool _isInitialized = false;
        private bool _isTampered = false;
        private int _tamperAttempts = 0;
        private DateTime _lastKeyRotation;
        private readonly Timer _healthMonitorTimer;
        private readonly Timer _keyRotationTimer;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly RNGCryptoServiceProvider _rng;
        private CancellationTokenSource _monitoringCancellationTokenSource;

        #endregion;

        #region Properties;

        /// <summary>Geçerli çip durumu</summary>
        public SecurityChipStatus CurrentStatus => _currentStatus;

        /// <summary>Geçerli çip modu</summary>
        public SecurityChipMode CurrentMode => _currentMode;

        /// <summary>Çip başlatıldı mı?</summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>Fiziksel müdahale tespit edildi mi?</summary>
        public bool IsTampered => _isTampered;

        /// <summary>Çip donanım bilgileri</summary>
        public ChipHardwareInfo HardwareInfo => _hardwareInfo;

        /// <summary>Depolanan anahtar sayısı</summary>
        public int KeyCount => _keys.Count;

        /// <summary>Güvenlik olayları geçmişi</summary>
        public IReadOnlyCollection<SecurityEvent> SecurityEvents => _securityEvents.AsReadOnly();

        /// <summary>Performans geçmişi</summary>
        public IReadOnlyCollection<ChipPerformanceMetrics> PerformanceHistory => _performanceHistory.AsReadOnly();

        #endregion;

        #region Constructor;

        /// <summary>
        /// SecurityChip sınıfı yapıcısı;
        /// </summary>
        public SecurityChip(
            ILogger logger,
            ICryptoEngine cryptoEngine,
            IManifestoValidator manifestoValidator,
            SecurityChipConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _manifestoValidator = manifestoValidator ?? throw new ArgumentNullException(nameof(manifestoValidator));

            _config = config ?? new SecurityChipConfig();
            _currentMode = _config.DefaultMode;

            _keys = new Dictionary<string, ChipKey>();
            _securityEvents = new Queue<SecurityEvent>();
            _performanceHistory = new List<ChipPerformanceMetrics>();

            _rng = new RNGCryptoServiceProvider();

            _healthMonitorTimer = new Timer(MonitorChipHealth, null,
                Timeout.Infinite, Timeout.Infinite);

            _keyRotationTimer = new Timer(RotateKeysIfNeeded, null,
                Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation("SecurityChip instance created");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Güvenlik çipini başlat;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("Security chip is already initialized");
                return;
            }

            await _operationLock.WaitAsync();

            try
            {
                _logger.LogInformation("Initializing security chip");

                // Donanımı tespit et;
                await DetectHardwareAsync();

                // Çip firmware'ını doğrula;
                await ValidateFirmwareAsync();

                // Self-test yap;
                await PerformSelfTestAsync();

                // Anahtar depolarını temizle;
                await ClearKeyStorageAsync();

                // Kök anahtarları oluştur;
                await GenerateRootKeysAsync();

                // İzleme zamanlayıcılarını başlat;
                StartMonitoringTimers();

                _isInitialized = true;
                UpdateStatus(SecurityChipStatus.Active);

                _logger.LogInformation($"Security chip initialized successfully. Mode: {_currentMode}");
                LogSecurityEvent("Initialization", "Chip initialized successfully",
                    SecurityChipStatus.Disabled, SecurityChipStatus.Active);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Security chip initialization failed: {ex.Message}");
                UpdateStatus(SecurityChipStatus.Fault);
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Güvenlik çipini kapat;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            await _operationLock.WaitAsync();

            try
            {
                _logger.LogInformation("Shutting down security chip");

                // İzlemeyi durdur;
                StopMonitoringTimers();

                // Hassas verileri temizle;
                await ClearSensitiveDataAsync();

                // Anahtarları güvenli bir şekilde imha et;
                await DestroyAllKeysAsync();

                // Çipi güvenli moda al;
                await SetSecureModeAsync();

                _isInitialized = false;
                UpdateStatus(SecurityChipStatus.Disabled);

                _logger.LogInformation("Security chip shut down successfully");
                LogSecurityEvent("Shutdown", "Chip shut down safely",
                    SecurityChipStatus.Active, SecurityChipStatus.Disabled);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Security chip shutdown failed: {ex.Message}");
                UpdateStatus(SecurityChipStatus.CriticalFailure);
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Çip modunu değiştir;
        /// </summary>
        public async Task SetModeAsync(SecurityChipMode newMode)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (_currentMode == newMode)
                return;

            await _operationLock.WaitAsync();

            try
            {
                var oldMode = _currentMode;

                // Mod değişikliği için gerekli kontroller;
                if (!await CanSwitchModeAsync(oldMode, newMode))
                {
                    throw new InvalidOperationException($"Mode switch from {oldMode} to {newMode} not allowed");
                }

                // Önceki modun temizliği;
                await CleanupCurrentModeAsync();

                // Yeni modu başlat;
                await InitializeModeAsync(newMode);

                _currentMode = newMode;

                _logger.LogInformation($"Security chip mode changed: {oldMode} -> {newMode}");
                LogSecurityEvent("ModeChange", $"Mode changed from {oldMode} to {newMode}",
                    _currentStatus, _currentStatus, 2);
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar oluştur;
        /// </summary>
        public async Task<ChipKey> GenerateKeyAsync(
            string keyName,
            KeyType keyType,
            int keySize,
            TimeSpan validityPeriod,
            bool isExportable = false,
            string usage = "General")
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (_isTampered)
                throw new InvalidOperationException("Cannot generate keys on tampered chip");

            await _operationLock.WaitAsync();

            try
            {
                // Anahtar boyutu kontrolü;
                ValidateKeySize(keyType, keySize);

                // Anahtar ID oluştur;
                string keyId = await GenerateKeyIdAsync();

                // Anahtar oluşturma tarihi ve geçerlilik süresi;
                DateTime expiresAt = DateTime.UtcNow.Add(validityPeriod);

                // Donanımda anahtar oluştur;
                await GenerateKeyInHardwareAsync(keyId, keyType, keySize);

                // Anahtar nesnesi oluştur;
                var key = new ChipKey(keyId, keyName, keyType, keySize,
                    expiresAt, isExportable, usage);

                // Anahtarı sakla;
                _keys[keyId] = key;

                _logger.LogInformation($"Key generated: {keyName} ({keyType}-{keySize})");
                LogSecurityEvent("KeyGeneration", $"Generated {keyType} key: {keyName}",
                    _currentStatus, _currentStatus, 2);

                OnKeyOperationPerformed($"Generated key: {keyName}");

                return key;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key generation failed: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar ile şifrele;
        /// </summary>
        public async Task<byte[]> EncryptAsync(
            string keyId,
            byte[] plaintext,
            byte[] iv = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            if (!key.IsActive)
                throw new InvalidOperationException($"Key is not active: {keyId}");

            await _operationLock.WaitAsync();

            try
            {
                // Anahtar kullanımını kontrol et;
                ValidateKeyUsage(key, "Encryption");

                // Donanımda şifreleme yap;
                byte[] ciphertext = await EncryptInHardwareAsync(keyId, plaintext, iv);

                // Performans metriklerini güncelle;
                UpdatePerformanceMetrics(1);

                _logger.LogDebug($"Data encrypted using key: {key.KeyName}");
                LogSecurityEvent("Encryption", $"Encrypted {plaintext.Length} bytes with key: {key.KeyName}",
                    _currentStatus, _currentStatus, 1);

                return ciphertext;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Encryption failed: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar ile şifreyi çöz;
        /// </summary>
        public async Task<byte[]> DecryptAsync(
            string keyId,
            byte[] ciphertext,
            byte[] iv = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            if (!key.IsActive)
                throw new InvalidOperationException($"Key is not active: {keyId}");

            await _operationLock.WaitAsync();

            try
            {
                // Anahtar kullanımını kontrol et;
                ValidateKeyUsage(key, "Decryption");

                // Donanımda şifre çözme yap;
                byte[] plaintext = await DecryptInHardwareAsync(keyId, ciphertext, iv);

                // Performans metriklerini güncelle;
                UpdatePerformanceMetrics(1);

                _logger.LogDebug($"Data decrypted using key: {key.KeyName}");
                LogSecurityEvent("Decryption", $"Decrypted {ciphertext.Length} bytes with key: {key.KeyName}",
                    _currentStatus, _currentStatus, 1);

                return plaintext;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Decryption failed: {ex.Message}");
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
            string keyId,
            byte[] data)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            if (key.Type != KeyType.RSA && key.Type != KeyType.ECC)
                throw new InvalidOperationException($"Key type {key.Type} does not support signing");

            await _operationLock.WaitAsync();

            try
            {
                // Donanımda imzalama yap;
                byte[] signature = await SignInHardwareAsync(keyId, data);

                // Performans metriklerini güncelle;
                UpdatePerformanceMetrics(1);

                _logger.LogDebug($"Data signed using key: {key.KeyName}");
                LogSecurityEvent("Signing", $"Signed {data.Length} bytes with key: {key.KeyName}",
                    _currentStatus, _currentStatus, 1);

                return signature;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Signing failed: {ex.Message}");
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
            string keyId,
            byte[] data,
            byte[] signature)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            if (key.Type != KeyType.RSA && key.Type != KeyType.ECC)
                throw new InvalidOperationException($"Key type {key.Type} does not support verification");

            await _operationLock.WaitAsync();

            try
            {
                // Donanımda doğrulama yap;
                bool isValid = await VerifyInHardwareAsync(keyId, data, signature);

                // Performans metriklerini güncelle;
                UpdatePerformanceMetrics(1);

                _logger.LogDebug($"Signature verification result: {isValid}");
                LogSecurityEvent("Verification", $"Verified signature for {data.Length} bytes",
                    _currentStatus, _currentStatus, 1);

                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Verification failed: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Güvenli rastgele sayı üret;
        /// </summary>
        public async Task<byte[]> GenerateRandomAsync(int size)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Security chip not initialized");

            if (size <= 0 || size > 4096)
                throw new ArgumentOutOfRangeException(nameof(size),
                    "Size must be between 1 and 4096 bytes");

            await _operationLock.WaitAsync();

            try
            {
                // Donanımda rastgele sayı üret;
                byte[] randomBytes = await GenerateRandomInHardwareAsync(size);

                // Performans metriklerini güncelle;
                UpdatePerformanceMetrics(1);

                _logger.LogDebug($"Generated {size} bytes of random data");
                LogSecurityEvent("RandomGeneration", $"Generated {size} random bytes",
                    _currentStatus, _currentStatus, 1);

                return randomBytes;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Random generation failed: {ex.Message}");

                // Yedek olarak yazılım RNG kullan;
                byte[] fallbackRandom = new byte[size];
                _rng.GetBytes(fallbackRandom);

                _logger.LogWarning("Used fallback software RNG");
                return fallbackRandom;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar bilgilerini al;
        /// </summary>
        public ChipKey GetKeyInfo(string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            // Hassas bilgileri temizlemiş bir kopya döndür;
            return new ChipKey(
                key.KeyId,
                key.KeyName,
                key.Type,
                key.KeySize,
                key.ExpiresAt,
                key.IsExportable,
                key.Usage)
            {
                IsActive = key.IsActive;
            };
        }

        /// <summary>
        /// Anahtar listesini al;
        /// </summary>
        public IReadOnlyCollection<ChipKey> ListKeys()
        {
            return _keys.Values;
                .Select(k => new ChipKey(
                    k.KeyId,
                    k.KeyName,
                    k.Type,
                    k.KeySize,
                    k.ExpiresAt,
                    k.IsExportable,
                    k.Usage)
                {
                    IsActive = k.IsActive;
                })
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Anahtarı devre dışı bırak;
        /// </summary>
        public async Task DisableKeyAsync(string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            await _operationLock.WaitAsync();

            try
            {
                key.IsActive = false;

                _logger.LogInformation($"Key disabled: {key.KeyName}");
                LogSecurityEvent("KeyDisable", $"Disabled key: {key.KeyName}",
                    _currentStatus, _currentStatus, 2);

                OnKeyOperationPerformed($"Disabled key: {key.KeyName}");
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtarı tamamen sil;
        /// </summary>
        public async Task DestroyKeyAsync(string keyId)
        {
            if (!_keys.TryGetValue(keyId, out var key))
                throw new KeyNotFoundException($"Key not found: {keyId}");

            await _operationLock.WaitAsync();

            try
            {
                // Donanımda anahtarı imha et;
                await DestroyKeyInHardwareAsync(keyId);

                // Bellekten kaldır;
                _keys.Remove(keyId);

                _logger.LogInformation($"Key destroyed: {key.KeyName}");
                LogSecurityEvent("KeyDestruction", $"Destroyed key: {key.KeyName}",
                    _currentStatus, _currentStatus, 3);

                OnKeyOperationPerformed($"Destroyed key: {key.KeyName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key destruction failed: {ex.Message}");
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Çip sağlık durumunu kontrol et;
        /// </summary>
        public async Task<SystemHealth> CheckHealthAsync()
        {
            if (!_isInitialized)
                return SystemHealth.Critical;

            await _operationLock.WaitAsync();

            try
            {
                // Sıcaklık kontrolü;
                double temperature = await ReadTemperatureAsync();

                // Güç tüketimi kontrolü;
                double power = await ReadPowerConsumptionAsync();

                // Hata sayacı kontrolü;
                int errorCount = await ReadErrorCounterAsync();

                // Donanım testi;
                bool hardwareTest = await PerformQuickTestAsync();

                // Durum değerlendirmesi;
                if (!hardwareTest || temperature > _config.TemperatureThreshold)
                {
                    UpdateStatus(SecurityChipStatus.Fault);
                    return SystemHealth.Critical;
                }

                if (errorCount > 10 || temperature > _config.TemperatureThreshold - 10)
                {
                    UpdateStatus(SecurityChipStatus.Standby);
                    return SystemHealth.Warning;
                }

                UpdateStatus(SecurityChipStatus.Active);
                return SystemHealth.Healthy;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Health check failed: {ex.Message}");
                UpdateStatus(SecurityChipStatus.Fault);
                return SystemHealth.Critical;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Fiziksel müdahale kontrolü yap;
        /// </summary>
        public async Task<bool> CheckForTamperingAsync()
        {
            if (!_config.EnableTamperDetection)
                return false;

            await _operationLock.WaitAsync();

            try
            {
                // Donanım müdahale sensörlerini oku;
                bool tamperDetected = await ReadTamperSensorsAsync();

                if (tamperDetected && !_isTampered)
                {
                    _isTampered = true;
                    _tamperAttempts++;

                    UpdateStatus(SecurityChipStatus.Tampered);

                    // Hassas verileri temizle;
                    await ClearSensitiveDataAsync();

                    // Anahtarları imha et;
                    await DestroyAllKeysAsync();

                    // Olayı kaydet;
                    LogSecurityEvent("Tampering", "Physical tampering detected",
                        SecurityChipStatus.Active, SecurityChipStatus.Tampered, 5);

                    OnTamperDetected($"Physical tampering detected. Attempt #{_tamperAttempts}");

                    _logger.LogCritical($"PHYSICAL TAMPERING DETECTED! Attempt #{_tamperAttempts}");

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Tamper check failed: {ex.Message}");
                return false;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Çip sıfırlama (fabrika ayarları)
        /// </summary>
        public async Task ResetToFactoryAsync()
        {
            if (_isTampered)
                throw new InvalidOperationException("Cannot reset tampered chip");

            await _operationLock.WaitAsync();

            try
            {
                _logger.LogWarning("Resetting security chip to factory settings");

                // İzlemeyi durdur;
                StopMonitoringTimers();

                // Tüm anahtarları imha et;
                await DestroyAllKeysAsync();

                // Tüm konfigürasyonu temizle;
                await ClearConfigurationAsync();

                // Firmware'ı yeniden yükle;
                await ReloadFirmwareAsync();

                // Self-test yap;
                await PerformSelfTestAsync();

                // Fabrika ayarlarına dön;
                _currentMode = SecurityChipMode.Normal;
                _isInitialized = false;
                _isTampered = false;
                _securityEvents.Clear();
                _performanceHistory.Clear();

                UpdateStatus(SecurityChipStatus.Disabled);

                _logger.LogInformation("Security chip reset to factory settings");
                LogSecurityEvent("FactoryReset", "Chip reset to factory settings",
                    _currentStatus, SecurityChipStatus.Disabled, 4);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Factory reset failed: {ex.Message}");
                UpdateStatus(SecurityChipStatus.CriticalFailure);
                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Performans raporu oluştur;
        /// </summary>
        public ChipPerformanceMetrics GetPerformanceReport()
        {
            if (_performanceHistory.Count == 0)
                return new ChipPerformanceMetrics();

            var recent = _performanceHistory;
                .Where(m => m.MeasurementTime > DateTime.UtcNow.AddHours(-1))
                .ToList();

            if (recent.Count == 0)
                return _performanceHistory.Last();

            return new ChipPerformanceMetrics;
            {
                OperationsPerSecond = (int)recent.Average(m => m.OperationsPerSecond),
                PowerConsumption = recent.Average(m => m.PowerConsumption),
                Temperature = recent.Average(m => m.Temperature),
                ErrorRate = (int)recent.Average(m => m.ErrorRate),
                AverageResponseTime = TimeSpan.FromMilliseconds(
                    recent.Average(m => m.AverageResponseTime.TotalMilliseconds))
            };
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Donanımı tespit et;
        /// </summary>
        private async Task DetectHardwareAsync()
        {
            _logger.LogInformation("Detecting security chip hardware");

            try
            {
                // Donanım bilgilerini oku;
                _hardwareInfo = await ReadHardwareInfoAsync();

                if (_hardwareInfo == null)
                    throw new InvalidOperationException("Security chip hardware not detected");

                _logger.LogInformation($"Security chip detected: {_hardwareInfo.Manufacturer} {_hardwareInfo.Model}");
                _logger.LogInformation($"Firmware: {_hardwareInfo.FirmwareVersion}, Hardware: {_hardwareInfo.HardwareVersion}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hardware detection failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Firmware doğrulama;
        /// </summary>
        private async Task ValidateFirmwareAsync()
        {
            _logger.LogInformation("Validating chip firmware");

            try
            {
                // Firmware imzasını doğrula;
                bool isValid = await VerifyFirmwareSignatureAsync();

                if (!isValid)
                    throw new InvalidOperationException("Firmware signature validation failed");

                // Firmware bütünlüğünü kontrol et;
                bool isIntegrityValid = await CheckFirmwareIntegrityAsync();

                if (!isIntegrityValid)
                    throw new InvalidOperationException("Firmware integrity check failed");

                _logger.LogInformation("Firmware validation successful");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Firmware validation failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Self-test yap;
        /// </summary>
        private async Task PerformSelfTestAsync()
        {
            _logger.LogInformation("Performing self-test");

            try
            {
                // Kriptografik algoritma testleri;
                await TestCryptographicFunctionsAsync();

                // Bellek testi;
                await TestMemoryAsync();

                // RNG testi;
                await TestRandomNumberGeneratorAsync();

                // Sensör testi;
                await TestSensorsAsync();

                _logger.LogInformation("Self-test completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Self-test failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Kök anahtarları oluştur;
        /// </summary>
        private async Task GenerateRootKeysAsync()
        {
            _logger.LogInformation("Generating root keys");

            try
            {
                // Master anahtar oluştur;
                var masterKey = await GenerateKeyAsync(
                    "MASTER_KEY",
                    KeyType.AES,
                    256,
                    TimeSpan.FromDays(3650),
                    false,
                    "KeyEncryption");

                // İmza anahtarı oluştur;
                var signingKey = await GenerateKeyAsync(
                    "SIGNING_KEY",
                    KeyType.ECC,
                    256,
                    TimeSpan.FromDays(365),
                    false,
                    "Signing");

                _logger.LogInformation("Root keys generated successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Root key generation failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// İzleme zamanlayıcılarını başlat;
        /// </summary>
        private void StartMonitoringTimers()
        {
            _healthMonitorTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            if (_config.KeyRotationInterval > 0)
            {
                _keyRotationTimer.Change(
                    TimeSpan.FromSeconds(_config.KeyRotationInterval),
                    TimeSpan.FromSeconds(_config.KeyRotationInterval));
            }

            _monitoringCancellationTokenSource = new CancellationTokenSource();

            _logger.LogDebug("Monitoring timers started");
        }

        /// <summary>
        /// İzleme zamanlayıcılarını durdur;
        /// </summary>
        private void StopMonitoringTimers()
        {
            _healthMonitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _keyRotationTimer.Change(Timeout.Infinite, Timeout.Infinite);

            _monitoringCancellationTokenSource?.Cancel();
            _monitoringCancellationTokenSource?.Dispose();
            _monitoringCancellationTokenSource = null;

            _logger.LogDebug("Monitoring timers stopped");
        }

        /// <summary>
        /// Çip sağlığını izle;
        /// </summary>
        private async void MonitorChipHealth(object state)
        {
            if (!_isInitialized || _monitoringCancellationTokenSource?.Token.IsCancellationRequested == true)
                return;

            try
            {
                // Sağlık kontrolü yap;
                await CheckHealthAsync();

                // Müdahale kontrolü yap;
                if (_config.EnableTamperDetection)
                {
                    await CheckForTamperingAsync();
                }

                // Sıcaklık izleme;
                if (_config.EnableTemperatureMonitoring)
                {
                    await MonitorTemperatureAsync();
                }

                // Performans metriklerini güncelle;
                await UpdatePerformanceMonitoringAsync();

                // Güvenlik modu değiştirme kontrolü;
                if (_config.AutoSwitchToHighSecurity &&
                    _securityEvents.Count(e => e.Severity >= 3) >= _config.HighSecurityThreshold)
                {
                    await SetModeAsync(SecurityChipMode.HighSecurity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Health monitoring error: {ex.Message}");
            }
        }

        /// <summary>
        /// Anahtar rotasyonu yap;
        /// </summary>
        private async void RotateKeysIfNeeded(object state)
        {
            if (!_isInitialized || _monitoringCancellationTokenSource?.Token.IsCancellationRequested == true)
                return;

            await _operationLock.WaitAsync();

            try
            {
                var now = DateTime.UtcNow;

                // Süresi dolan anahtarları bul;
                var expiredKeys = _keys.Values;
                    .Where(k => k.ExpiresAt <= now && k.IsActive)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    try
                    {
                        // Yeni anahtar oluştur;
                        var newKey = await GenerateKeyAsync(
                            $"{key.KeyName}_ROTATED",
                            key.Type,
                            key.KeySize,
                            TimeSpan.FromSeconds(_config.KeyRotationInterval),
                            key.IsExportable,
                            key.Usage);

                        // Eski anahtarı devre dışı bırak;
                        key.IsActive = false;

                        _logger.LogInformation($"Key rotated: {key.KeyName} -> {newKey.KeyName}");
                        LogSecurityEvent("KeyRotation", $"Rotated key: {key.KeyName}",
                            _currentStatus, _currentStatus, 2);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Key rotation failed for {key.KeyName}: {ex.Message}");
                    }
                }

                _lastKeyRotation = now;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Anahtar boyutunu doğrula;
        /// </summary>
        private void ValidateKeySize(KeyType keyType, int keySize)
        {
            switch (keyType)
            {
                case KeyType.AES:
                    if (keySize != 128 && keySize != 192 && keySize != 256)
                        throw new ArgumentException($"Invalid AES key size: {keySize}. Must be 128, 192, or 256.");
                    break;

                case KeyType.RSA:
                    if (keySize < 2048 || keySize > 4096 || keySize % 8 != 0)
                        throw new ArgumentException($"Invalid RSA key size: {keySize}. Must be between 2048 and 4096, divisible by 8.");
                    break;

                case KeyType.ECC:
                    if (keySize != 256 && keySize != 384 && keySize != 521)
                        throw new ArgumentException($"Invalid ECC key size: {keySize}. Must be 256, 384, or 521.");
                    break;

                default:
                    if (keySize <= 0 || keySize > 65536)
                        throw new ArgumentException($"Invalid key size: {keySize}");
                    break;
            }
        }

        /// <summary>
        /// Anahtar kullanımını doğrula;
        /// </summary>
        private void ValidateKeyUsage(ChipKey key, string operation)
        {
            if (key.ExpiresAt <= DateTime.UtcNow)
            {
                key.IsActive = false;
                throw new InvalidOperationException($"Key expired: {key.KeyName}");
            }

            // Kullanım kısıtlamaları;
            if (key.Usage == "Signing" && operation != "Signing" && operation != "Verification")
                throw new InvalidOperationException($"Key {key.KeyName} can only be used for signing/verification");

            if (key.Usage == "KeyEncryption" && operation != "Encryption" && operation != "Decryption")
                throw new InvalidOperationException($"Key {key.KeyName} can only be used for encryption/decryption");
        }

        /// <summary>
        /// Performans metriklerini güncelle;
        /// </summary>
        private void UpdatePerformanceMetrics(int operations)
        {
            var metrics = new ChipPerformanceMetrics;
            {
                OperationsPerSecond = operations * 2, // Tahmini değer;
                Temperature = ReadTemperatureAsync().Result,
                PowerConsumption = ReadPowerConsumptionAsync().Result,
                ErrorRate = ReadErrorCounterAsync().Result,
                AverageResponseTime = TimeSpan.FromMilliseconds(10) // Tahmini değer;
            };

            _performanceHistory.Add(metrics);

            // Eski kayıtları temizle (24 saatten eski)
            _performanceHistory.RemoveAll(m => m.MeasurementTime < DateTime.UtcNow.AddHours(-24));

            OnPerformanceMetricsUpdated(metrics);
        }

        /// <summary>
        /// Durumu güncelle;
        /// </summary>
        private void UpdateStatus(SecurityChipStatus newStatus)
        {
            if (_currentStatus == newStatus)
                return;

            var oldStatus = _currentStatus;
            _currentStatus = newStatus;

            _logger.LogInformation($"Security chip status changed: {oldStatus} -> {newStatus}");

            OnStatusChanged(newStatus);
        }

        /// <summary>
        /// Güvenlik olayını kaydet;
        /// </summary>
        private void LogSecurityEvent(string eventType, string description,
            SecurityChipStatus before, SecurityChipStatus after, int severity = 1)
        {
            var securityEvent = new SecurityEvent(eventType, description, before, after, severity);

            _securityEvents.Enqueue(securityEvent);

            // Kuyruk boyutunu sınırla;
            while (_securityEvents.Count > _config.AuditLogSize)
            {
                _securityEvents.Dequeue();
            }

            OnSecurityEventOccurred(securityEvent);
        }

        #endregion;

        #region Hardware Abstraction Methods;

        // Bu metodlar gerçek donanım API'leri ile implemente edilecek;

        private async Task<ChipHardwareInfo> ReadHardwareInfoAsync()
        {
            // Gerçek donanım bilgilerini oku;
            await Task.Delay(10); // Simülasyon;

            return new ChipHardwareInfo;
            {
                Manufacturer = "NEDA Security",
                Model = "SC-5000",
                SerialNumber = Guid.NewGuid().ToString("N").ToUpper(),
                FirmwareVersion = "2.1.5",
                HardwareVersion = "1.0",
                ManufacturingDate = new DateTime(2023, 6, 15),
                SupportedAlgorithms = "AES,RSA,ECC,SHA2,SHA3,DRBG",
                MemorySize = 4096,
                KeyStorageCapacity = 100;
            };
        }

        private async Task<bool> VerifyFirmwareSignatureAsync()
        {
            await Task.Delay(5);
            return true;
        }

        private async Task<bool> CheckFirmwareIntegrityAsync()
        {
            await Task.Delay(5);
            return true;
        }

        private async Task TestCryptographicFunctionsAsync()
        {
            await Task.Delay(20);
        }

        private async Task TestMemoryAsync()
        {
            await Task.Delay(15);
        }

        private async Task TestRandomNumberGeneratorAsync()
        {
            await Task.Delay(10);
        }

        private async Task TestSensorsAsync()
        {
            await Task.Delay(10);
        }

        private async Task ClearKeyStorageAsync()
        {
            await Task.Delay(10);
        }

        private async Task ClearSensitiveDataAsync()
        {
            await Task.Delay(10);
        }

        private async Task DestroyAllKeysAsync()
        {
            await Task.Delay(20);
        }

        private async Task SetSecureModeAsync()
        {
            await Task.Delay(5);
        }

        private async Task<bool> CanSwitchModeAsync(SecurityChipMode oldMode, SecurityChipMode newMode)
        {
            await Task.Delay(1);

            // Mod geçiş kuralları;
            if (oldMode == SecurityChipMode.SecureBoot && newMode != SecurityChipMode.Normal)
                return false;

            if (newMode == SecurityChipMode.SecureBoot && oldMode != SecurityChipMode.Normal)
                return false;

            return true;
        }

        private async Task CleanupCurrentModeAsync()
        {
            await Task.Delay(5);
        }

        private async Task InitializeModeAsync(SecurityChipMode mode)
        {
            await Task.Delay(10);
        }

        private async Task<string> GenerateKeyIdAsync()
        {
            var randomBytes = await GenerateRandomAsync(16);
            return BitConverter.ToString(randomBytes).Replace("-", "");
        }

        private async Task GenerateKeyInHardwareAsync(string keyId, KeyType keyType, int keySize)
        {
            await Task.Delay(50); // Donanım anahtar oluşturma süresi;
        }

        private async Task<byte[]> EncryptInHardwareAsync(string keyId, byte[] data, byte[] iv)
        {
            await Task.Delay(20);

            // Simülasyon: Gerçekte donanım şifrelemesi yapılacak;
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();

            if (iv != null)
                aes.IV = iv;
            else;
                aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(data, 0, data.Length);
        }

        private async Task<byte[]> DecryptInHardwareAsync(string keyId, byte[] data, byte[] iv)
        {
            await Task.Delay(20);

            // Simülasyon: Gerçekte donanım şifre çözme yapılacak;
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();

            if (iv != null)
                aes.IV = iv;
            else;
                aes.GenerateIV();

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(data, 0, data.Length);
        }

        private async Task<byte[]> SignInHardwareAsync(string keyId, byte[] data)
        {
            await Task.Delay(30);
            return await GenerateRandomAsync(64); // Simülasyon;
        }

        private async Task<bool> VerifyInHardwareAsync(string keyId, byte[] data, byte[] signature)
        {
            await Task.Delay(30);
            return signature.Length == 64; // Basit simülasyon;
        }

        private async Task<byte[]> GenerateRandomInHardwareAsync(int size)
        {
            await Task.Delay(size / 100); // Boyuta bağlı gecikme;

            byte[] random = new byte[size];
            _rng.GetBytes(random);
            return random;
        }

        private async Task DestroyKeyInHardwareAsync(string keyId)
        {
            await Task.Delay(10);
        }

        private async Task<double> ReadTemperatureAsync()
        {
            await Task.Delay(5);

            // Simülasyon: Rastgele sıcaklık;
            Random rand = new Random();
            return 30 + rand.NextDouble() * 20; // 30-50°C arası;
        }

        private async Task<double> ReadPowerConsumptionAsync()
        {
            await Task.Delay(5);

            // Simülasyon: Rastgele güç tüketimi;
            Random rand = new Random();
            return 0.5 + rand.NextDouble() * 0.5; // 0.5-1.0W arası;
        }

        private async Task<int> ReadErrorCounterAsync()
        {
            await Task.Delay(5);
            return 0; // Simülasyon;
        }

        private async Task<bool> PerformQuickTestAsync()
        {
            await Task.Delay(10);
            return true;
        }

        private async Task<bool> ReadTamperSensorsAsync()
        {
            await Task.Delay(5);
            return false; // Simülasyon;
        }

        private async Task ClearConfigurationAsync()
        {
            await Task.Delay(10);
        }

        private async Task ReloadFirmwareAsync()
        {
            await Task.Delay(50);
        }

        private async Task MonitorTemperatureAsync()
        {
            double temp = await ReadTemperatureAsync();

            if (temp > _config.TemperatureThreshold)
            {
                _logger.LogWarning($"Chip temperature critical: {temp}°C");

                // Aşırı ısınma durumunda güvenli moda geç;
                if (temp > _config.TemperatureThreshold + 10)
                {
                    await SetModeAsync(SecurityChipMode.HighSecurity);
                    UpdateStatus(SecurityChipStatus.Standby);
                }
            }
        }

        private async Task UpdatePerformanceMonitoringAsync()
        {
            await Task.CompletedTask;
        }

        #endregion;

        #region Event Triggers;

        private void OnStatusChanged(SecurityChipStatus newStatus)
        {
            StatusChanged?.Invoke(this, newStatus);
        }

        private void OnSecurityEventOccurred(SecurityEvent securityEvent)
        {
            SecurityEventOccurred?.Invoke(this, securityEvent);
        }

        private void OnTamperDetected(string message)
        {
            TamperDetected?.Invoke(this, message);
        }

        private void OnKeyOperationPerformed(string operation)
        {
            KeyOperationPerformed?.Invoke(this, operation);
        }

        private void OnPerformanceMetricsUpdated(ChipPerformanceMetrics metrics)
        {
            PerformanceMetricsUpdated?.Invoke(this, metrics);
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
                        ShutdownAsync().Wait();
                    }
                    catch
                    {
                        // Shutdown hatalarını görmezden gel;
                    }

                    _healthMonitorTimer?.Dispose();
                    _keyRotationTimer?.Dispose();
                    _operationLock?.Dispose();
                    _rng?.Dispose();
                    _monitoringCancellationTokenSource?.Dispose();
                }

                _disposed = true;
            }
        }

        ~SecurityChip()
        {
            Dispose(false);
        }

        #endregion;
    }
}
