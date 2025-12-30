// NEDA.HardwareSecurity/USBCryptoToken/CryptoDevice.cs;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.HardwareSecurity.PhysicalManifesto;
using NEDA.Monitoring.Diagnostics;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.SecurityModules.Monitoring;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.HardwareSecurity.USBCryptoToken;
{
    /// <summary>
    /// Donanım tabanlı kriptografi cihazı yöneticisi;
    /// FIPS 140-2/3 uyumlu USB kriptografi token'ları, HSM'ler ve güvenli donanım modülleri için entegrasyon sağlar;
    /// </summary>
    public class CryptoDevice : ICryptoDevice, IDisposable;
    {
        private readonly ILogger<CryptoDevice> _logger;
        private readonly CryptoDeviceConfig _config;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IEventBus _eventBus;
        private readonly IHardwareLock _hardwareLock;

        private readonly CancellationTokenSource _monitoringCts;
        private Task _monitoringTask;
        private bool _isDisposed;
        private bool _isInitialized;
        private bool _isConnected;
        private DateTime? _lastConnectionTime;
        private int _operationCount;

        private readonly object _syncLock = new object();
        private readonly System.Timers.Timer _healthTimer;
        private readonly List<DeviceEvent> _deviceEvents;
        private IntPtr _deviceHandle;
        private GCHandle _pinnedKeyHandle;

        // PC/SC ve kriptografi sabitleri;
        private const uint SCARD_PROTOCOL_T0 = 0x0001;
        private const uint SCARD_PROTOCOL_T1 = 0x0002;
        private const uint SCARD_PROTOCOL_RAW = 0x0004;
        private const uint SCARD_SHARE_EXCLUSIVE = 0x0001;
        private const uint SCARD_SHARE_SHARED = 0x0002;
        private const uint SCARD_LEAVE_CARD = 0x0000;
        private const uint SCARD_RESET_CARD = 0x0001;
        private const uint SCARD_UNPOWER_CARD = 0x0002;
        private const uint SCARD_EJECT_CARD = 0x0003;

        // Kriptografi sabitleri;
        private const int RSA_KEY_SIZE_2048 = 2048;
        private const int RSA_KEY_SIZE_4096 = 4096;
        private const int AES_KEY_SIZE_256 = 256;
        private const int ECC_KEY_SIZE_256 = 256;
        private const int MAX_PIN_RETRIES = 3;

        /// <summary>
        /// Cihaz durumu değiştiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<DeviceStatusChangedEventArgs> DeviceStatusChanged;

        /// <summary>
        /// Kriptografi işlemi gerçekleştirildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<CryptoOperationEventArgs> CryptoOperationPerformed;

        /// <summary>
        /// Anahtar işlemi gerçekleştirildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<KeyOperationEventArgs> KeyOperationPerformed;

        /// <summary>
        /// Donanım tabanlı kriptografi cihazı yöneticisi;
        /// </summary>
        public CryptoDevice(
            ILogger<CryptoDevice> logger,
            IOptions<CryptoDeviceConfig> config,
            ISecurityMonitor securityMonitor,
            IEventBus eventBus,
            IHardwareLock hardwareLock)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _hardwareLock = hardwareLock ?? throw new ArgumentNullException(nameof(hardwareLock));

            _monitoringCts = new CancellationTokenSource();
            _deviceEvents = new List<DeviceEvent>();
            _operationCount = 0;
            _deviceHandle = IntPtr.Zero;

            // Sağlık kontrol timer'ı;
            _healthTimer = new System.Timers.Timer(_config.HealthCheckIntervalMs);
            _healthTimer.Elapsed += OnHealthCheck;
            _healthTimer.AutoReset = true;

            InitializeEventSubscriptions();
            _logger.LogInformation("CryptoDevice initialized with {@Config}", _config);
        }

        /// <summary>
        /// Kriptografi cihazını başlat;
        /// </summary>
        public void StartDevice()
        {
            if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            {
                _logger.LogWarning("Crypto device is already running");
                return;
            }

            _monitoringTask = Task.Run(async () => await MonitorDeviceAsync(_monitoringCts.Token));
            _healthTimer.Start();

            _logger.LogInformation("Crypto device started");

            // Başlangıç durumunu kaydet;
            LogDeviceState("Device_Started", "Crypto device monitoring started", DeviceState.Initializing);
        }

        /// <summary>
        /// Kriptografi cihazını durdur;
        /// </summary>
        public async Task StopDeviceAsync()
        {
            _healthTimer.Stop();
            _monitoringCts.Cancel();

            try
            {
                if (_monitoringTask != null)
                {
                    await _monitoringTask;
                }
            }
            catch (OperationCanceledException)
            {
                // Task iptal edildi, normal durum;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping crypto device");
            }

            // Cihaz bağlantısını güvenli şekilde kapat;
            await SecureDisconnectAsync();

            _logger.LogInformation("Crypto device stopped");
        }

        /// <summary>
        /// Cihaza bağlan ve başlat;
        /// </summary>
        public async Task<bool> ConnectAndInitializeAsync()
        {
            try
            {
                LogDeviceState("Connect_Attempt", "Attempting to connect to crypto device", DeviceState.Connecting);

                // Cihazı bul;
                var devices = await DiscoverDevicesAsync();
                if (!devices.Any())
                {
                    _logger.LogError("No crypto devices found");
                    return false;
                }

                // İlk cihaza bağlan (veya konfigürasyona göre)
                var targetDevice = devices.FirstOrDefault(d => d.DeviceId == _config.PreferredDeviceId) ?? devices.First();

                _logger.LogInformation("Connecting to device: {DeviceName} ({DeviceId})",
                    targetDevice.DeviceName, targetDevice.DeviceId);

                // Bağlantı kur;
                var connectResult = await ConnectToDeviceAsync(targetDevice);
                if (!connectResult.Success)
                {
                    _logger.LogError("Failed to connect to device: {Error}", connectResult.Error);
                    return false;
                }

                // Başlatma (PIN girişi, oturum açma, vb.)
                var initResult = await InitializeDeviceAsync();
                if (!initResult.Success)
                {
                    _logger.LogError("Failed to initialize device: {Error}", initResult.Error);
                    await DisconnectDeviceAsync();
                    return false;
                }

                lock (_syncLock)
                {
                    _isConnected = true;
                    _isInitialized = true;
                    _lastConnectionTime = DateTime.UtcNow;
                    _deviceHandle = connectResult.DeviceHandle;
                }

                // Durum değişikliğini bildir;
                OnDeviceStatusChanged(new DeviceStatusChangedEventArgs;
                {
                    IsConnected = true,
                    IsInitialized = true,
                    DeviceInfo = targetDevice,
                    ChangedAt = DateTime.UtcNow;
                });

                // Cihaz bilgilerini logla;
                var deviceInfo = await GetDeviceInfoAsync();
                _logger.LogInformation("Crypto device connected and initialized: {@DeviceInfo}", deviceInfo);

                LogDeviceState("Connect_Success", $"Connected to {targetDevice.DeviceName}", DeviceState.Connected);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to crypto device");
                LogDeviceState("Connect_Failed", $"Connection failed: {ex.Message}", DeviceState.Error);
                return false;
            }
        }

        /// <summary>
        /// Cihazdan bağlantıyı kes;
        /// </summary>
        public async Task<bool> DisconnectAsync()
        {
            try
            {
                LogDeviceState("Disconnect_Attempt", "Attempting to disconnect from crypto device", DeviceState.Disconnecting);

                var disconnectResult = await DisconnectDeviceAsync();

                lock (_syncLock)
                {
                    _isConnected = false;
                    _isInitialized = false;
                    _deviceHandle = IntPtr.Zero;
                }

                // Durum değişikliğini bildir;
                OnDeviceStatusChanged(new DeviceStatusChangedEventArgs;
                {
                    IsConnected = false,
                    IsInitialized = false,
                    ChangedAt = DateTime.UtcNow;
                });

                if (disconnectResult.Success)
                {
                    _logger.LogInformation("Crypto device disconnected successfully");
                    LogDeviceState("Disconnect_Success", "Disconnected from crypto device", DeviceState.Disconnected);
                    return true;
                }
                else;
                {
                    _logger.LogWarning("Crypto device disconnected with warnings: {Message}", disconnectResult.Message);
                    LogDeviceState("Disconnect_Warning", disconnectResult.Message, DeviceState.Disconnected);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting from crypto device");
                LogDeviceState("Disconnect_Failed", $"Disconnect failed: {ex.Message}", DeviceState.Error);
                return false;
            }
        }

        /// <summary>
        /// Veriyi donanımda şifrele;
        /// </summary>
        public async Task<byte[]> EncryptAsync(byte[] plaintext, string keyId, CryptoAlgorithm algorithm = CryptoAlgorithm.AES256_CBC)
        {
            ValidateDeviceState();
            ValidateInput(plaintext, nameof(plaintext));
            ValidateKeyId(keyId);

            try
            {
                LogOperation("Encrypt_Start", $"Starting encryption with key {keyId}, algorithm {algorithm}");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım şifreleme işlemi;
                byte[] ciphertext;

                if (_config.UseHardwareAcceleration)
                {
                    ciphertext = await EncryptWithHardwareAsync(plaintext, keyId, algorithm);
                }
                else;
                {
                    // Yazılım fallback (sadece test/simülasyon için)
                    ciphertext = await EncryptWithSoftwareAsync(plaintext, keyId, algorithm);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.Encryption,
                    Operation = "Encrypt",
                    KeyId = keyId,
                    Algorithm = algorithm.ToString(),
                    InputSize = plaintext.Length,
                    OutputSize = ciphertext?.Length ?? 0,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = ciphertext != null;
                };

                AddDeviceEvent(operationEvent);

                // Kriptografi event'ini tetikle;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = CryptoOperationType.Encrypt,
                    Algorithm = algorithm,
                    KeyId = keyId,
                    InputSize = plaintext.Length,
                    OutputSize = ciphertext?.Length ?? 0,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = ciphertext != null;
                });

                if (ciphertext == null)
                {
                    throw new CryptographicException("Encryption failed - null result");
                }

                _logger.LogDebug("Encryption completed in {Duration}ms, {Input} -> {Output} bytes",
                    stopwatch.ElapsedMilliseconds, plaintext.Length, ciphertext.Length);

                return ciphertext;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encryption failed for key {KeyId}", keyId);
                LogDeviceState("Encrypt_Failed", $"Encryption failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware encryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Veriyi donanımda çöz;
        /// </summary>
        public async Task<byte[]> DecryptAsync(byte[] ciphertext, string keyId, CryptoAlgorithm algorithm = CryptoAlgorithm.AES256_CBC)
        {
            ValidateDeviceState();
            ValidateInput(ciphertext, nameof(ciphertext));
            ValidateKeyId(keyId);

            try
            {
                LogOperation("Decrypt_Start", $"Starting decryption with key {keyId}, algorithm {algorithm}");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım çözme işlemi;
                byte[] plaintext;

                if (_config.UseHardwareAcceleration)
                {
                    plaintext = await DecryptWithHardwareAsync(ciphertext, keyId, algorithm);
                }
                else;
                {
                    // Yazılım fallback (sadece test/simülasyon için)
                    plaintext = await DecryptWithSoftwareAsync(ciphertext, keyId, algorithm);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.Decryption,
                    Operation = "Decrypt",
                    KeyId = keyId,
                    Algorithm = algorithm.ToString(),
                    InputSize = ciphertext.Length,
                    OutputSize = plaintext?.Length ?? 0,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = plaintext != null;
                };

                AddDeviceEvent(operationEvent);

                // Kriptografi event'ini tetikle;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = CryptoOperationType.Decrypt,
                    Algorithm = algorithm,
                    KeyId = keyId,
                    InputSize = ciphertext.Length,
                    OutputSize = plaintext?.Length ?? 0,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = plaintext != null;
                });

                if (plaintext == null)
                {
                    throw new CryptographicException("Decryption failed - null result");
                }

                _logger.LogDebug("Decryption completed in {Duration}ms, {Input} -> {Output} bytes",
                    stopwatch.ElapsedMilliseconds, ciphertext.Length, plaintext.Length);

                return plaintext;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Decryption failed for key {KeyId}", keyId);
                LogDeviceState("Decrypt_Failed", $"Decryption failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware decryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Veriyi donanımda imzala;
        /// </summary>
        public async Task<byte[]> SignAsync(byte[] data, string keyId, SignAlgorithm algorithm = SignAlgorithm.RSA_PSS_SHA256)
        {
            ValidateDeviceState();
            ValidateInput(data, nameof(data));
            ValidateKeyId(keyId);

            try
            {
                LogOperation("Sign_Start", $"Starting signing with key {keyId}, algorithm {algorithm}");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım imzalama işlemi;
                byte[] signature;

                if (_config.UseHardwareAcceleration)
                {
                    signature = await SignWithHardwareAsync(data, keyId, algorithm);
                }
                else;
                {
                    // Yazılım fallback (sadece test/simülasyon için)
                    signature = await SignWithSoftwareAsync(data, keyId, algorithm);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.Signing,
                    Operation = "Sign",
                    KeyId = keyId,
                    Algorithm = algorithm.ToString(),
                    InputSize = data.Length,
                    OutputSize = signature?.Length ?? 0,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = signature != null;
                };

                AddDeviceEvent(operationEvent);

                // Kriptografi event'ini tetikle;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = CryptoOperationType.Sign,
                    Algorithm = CryptoAlgorithm.RSA2048, // Varsayılan, gerçekte algorithm'a göre değişir;
                    KeyId = keyId,
                    InputSize = data.Length,
                    OutputSize = signature?.Length ?? 0,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = signature != null;
                });

                if (signature == null)
                {
                    throw new CryptographicException("Signing failed - null result");
                }

                _logger.LogDebug("Signing completed in {Duration}ms, {Input} -> {Output} bytes",
                    stopwatch.ElapsedMilliseconds, data.Length, signature.Length);

                return signature;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Signing failed for key {KeyId}", keyId);
                LogDeviceState("Sign_Failed", $"Signing failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware signing failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// İmzayı donanımda doğrula;
        /// </summary>
        public async Task<bool> VerifyAsync(byte[] data, byte[] signature, string keyId, SignAlgorithm algorithm = SignAlgorithm.RSA_PSS_SHA256)
        {
            ValidateDeviceState();
            ValidateInput(data, nameof(data));
            ValidateInput(signature, nameof(signature));
            ValidateKeyId(keyId);

            try
            {
                LogOperation("Verify_Start", $"Starting verification with key {keyId}, algorithm {algorithm}");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım doğrulama işlemi;
                bool isValid;

                if (_config.UseHardwareAcceleration)
                {
                    isValid = await VerifyWithHardwareAsync(data, signature, keyId, algorithm);
                }
                else;
                {
                    // Yazılım fallback (sadece test/simülasyon için)
                    isValid = await VerifyWithSoftwareAsync(data, signature, keyId, algorithm);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.Verification,
                    Operation = "Verify",
                    KeyId = keyId,
                    Algorithm = algorithm.ToString(),
                    InputSize = data.Length,
                    SignatureSize = signature.Length,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = isValid;
                };

                AddDeviceEvent(operationEvent);

                // Kriptografi event'ini tetikle;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = CryptoOperationType.Verify,
                    Algorithm = CryptoAlgorithm.RSA2048, // Varsayılan, gerçekte algorithm'a göre değişir;
                    KeyId = keyId,
                    InputSize = data.Length,
                    OutputSize = signature.Length,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = isValid;
                });

                _logger.LogDebug("Verification completed in {Duration}ms, result: {IsValid}",
                    stopwatch.ElapsedMilliseconds, isValid);

                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Verification failed for key {KeyId}", keyId);
                LogDeviceState("Verify_Failed", $"Verification failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware verification failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Donanımda rastgele sayı üret;
        /// </summary>
        public async Task<byte[]> GenerateRandomAsync(int length)
        {
            ValidateDeviceState();

            if (length <= 0)
                throw new ArgumentException("Length must be positive", nameof(length));

            if (length > _config.MaxRandomLength)
                throw new ArgumentException($"Length exceeds maximum allowed ({_config.MaxRandomLength})", nameof(length));

            try
            {
                LogOperation("Random_Start", $"Generating {length} random bytes");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım RNG işlemi;
                byte[] randomBytes;

                if (_config.UseHardwareRNG && _config.UseHardwareAcceleration)
                {
                    randomBytes = await GenerateRandomWithHardwareAsync(length);
                }
                else;
                {
                    // Yazılım RNG fallback;
                    randomBytes = GenerateRandomWithSoftware(length);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.RandomGeneration,
                    Operation = "GenerateRandom",
                    Algorithm = "TRNG/PRNG",
                    OutputSize = randomBytes.Length,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = randomBytes != null;
                };

                AddDeviceEvent(operationEvent);

                // Kriptografi event'ini tetikle;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = CryptoOperationType.GenerateRandom,
                    Algorithm = CryptoAlgorithm.AES256_CBC, // Placeholder;
                    KeyId = "RNG",
                    InputSize = 0,
                    OutputSize = randomBytes.Length,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = randomBytes != null;
                });

                if (randomBytes == null)
                {
                    throw new CryptographicException("Random generation failed - null result");
                }

                _logger.LogDebug("Random generation completed in {Duration}ms, {Length} bytes",
                    stopwatch.ElapsedMilliseconds, length);

                return randomBytes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Random generation failed");
                LogDeviceState("Random_Failed", $"Random generation failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware random generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Donanımda anahtar çifti oluştur;
        /// </summary>
        public async Task<KeyPair> GenerateKeyPairAsync(KeyAlgorithm algorithm = KeyAlgorithm.RSA_2048,
            string keyId = null, KeyUsage usage = KeyUsage.SignAndEncrypt)
        {
            ValidateDeviceState();

            try
            {
                keyId ??= $"KEY_{Guid.NewGuid():N}";

                LogOperation("KeyGen_Start", $"Generating key pair: {algorithm}, ID: {keyId}");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım anahtar üretimi;
                KeyPair keyPair;

                if (_config.UseHardwareKeyGeneration && _config.UseHardwareAcceleration)
                {
                    keyPair = await GenerateKeyPairWithHardwareAsync(algorithm, keyId, usage);
                }
                else;
                {
                    // Yazılım anahtar üretimi fallback;
                    keyPair = await GenerateKeyPairWithSoftwareAsync(algorithm, keyId, usage);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.KeyGeneration,
                    Operation = "GenerateKeyPair",
                    Algorithm = algorithm.ToString(),
                    KeyId = keyId,
                    KeySize = GetKeySize(algorithm),
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = keyPair != null;
                };

                AddDeviceEvent(operationEvent);

                // Anahtar event'ini tetikle;
                OnKeyOperationPerformed(new KeyOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = KeyOperationType.Generate,
                    KeyId = keyId,
                    Algorithm = algorithm,
                    KeySize = GetKeySize(algorithm),
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = keyPair != null;
                });

                if (keyPair == null)
                {
                    throw new CryptographicException("Key pair generation failed - null result");
                }

                _logger.LogInformation("Key pair generated in {Duration}ms: {KeyId} ({Algorithm})",
                    stopwatch.ElapsedMilliseconds, keyId, algorithm);

                // Anahtarı güvenli depoya kaydet (eğer yapılandırıldıysa)
                if (_config.AutoStoreGeneratedKeys)
                {
                    await StoreKeyPairAsync(keyPair);
                }

                return keyPair;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key pair generation failed for algorithm {Algorithm}", algorithm);
                LogDeviceState("KeyGen_Failed", $"Key generation failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware key generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Anahtarı donanımdan sil;
        /// </summary>
        public async Task<bool> DeleteKeyAsync(string keyId, bool force = false)
        {
            ValidateDeviceState();
            ValidateKeyId(keyId);

            try
            {
                LogOperation("KeyDelete_Start", $"Deleting key: {keyId}, force: {force}");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım anahtar silme işlemi;
                bool success;

                if (_config.UseHardwareAcceleration)
                {
                    success = await DeleteKeyWithHardwareAsync(keyId, force);
                }
                else;
                {
                    // Yazılım anahtar silme fallback;
                    success = await DeleteKeyWithSoftwareAsync(keyId, force);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.KeyDeletion,
                    Operation = "DeleteKey",
                    KeyId = keyId,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = success,
                    Details = $"Force: {force}"
                };

                AddDeviceEvent(operationEvent);

                // Anahtar event'ini tetikle;
                OnKeyOperationPerformed(new KeyOperationEventArgs;
                {
                    OperationId = operationId,
                    OperationType = KeyOperationType.Delete,
                    KeyId = keyId,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow,
                    Success = success;
                });

                _logger.LogInformation("Key deletion completed in {Duration}ms: {KeyId}, success: {Success}",
                    stopwatch.ElapsedMilliseconds, keyId, success);

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key deletion failed for key {KeyId}", keyId);
                LogDeviceState("KeyDelete_Failed", $"Key deletion failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Hardware key deletion failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cihaz PIN'ini değiştir;
        /// </summary>
        public async Task<bool> ChangePinAsync(string oldPin, string newPin)
        {
            ValidateDeviceState();

            if (string.IsNullOrEmpty(oldPin))
                throw new ArgumentException("Old PIN cannot be null or empty", nameof(oldPin));

            if (string.IsNullOrEmpty(newPin))
                throw new ArgumentException("New PIN cannot be null or empty", nameof(newPin));

            if (newPin.Length < _config.MinPinLength)
                throw new ArgumentException($"New PIN must be at least {_config.MinPinLength} characters", nameof(newPin));

            try
            {
                LogOperation("ChangePin_Start", "Changing device PIN");

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım PIN değiştirme işlemi;
                bool success;

                if (_config.UseHardwareAcceleration)
                {
                    success = await ChangePinWithHardwareAsync(oldPin, newPin);
                }
                else;
                {
                    // Yazılım PIN değiştirme fallback (simülasyon)
                    success = await ChangePinWithSoftwareAsync(oldPin, newPin);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.PinChange,
                    Operation = "ChangePin",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = success;
                };

                AddDeviceEvent(operationEvent);

                if (success)
                {
                    _logger.LogInformation("PIN changed successfully in {Duration}ms", stopwatch.ElapsedMilliseconds);
                }
                else;
                {
                    _logger.LogWarning("PIN change failed in {Duration}ms", stopwatch.ElapsedMilliseconds);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PIN change failed");
                LogDeviceState("ChangePin_Failed", $"PIN change failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"PIN change failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cihazı fabrika ayarlarına sıfırla (DİKKAT: Tüm anahtarları siler!)
        /// </summary>
        public async Task<bool> FactoryResetAsync(string adminPin)
        {
            if (string.IsNullOrEmpty(adminPin))
                throw new ArgumentException("Admin PIN cannot be null or empty", nameof(adminPin));

            try
            {
                LogOperation("FactoryReset_Start", "Initiating factory reset (WARNING: All keys will be deleted!)");

                // Ekstra güvenlik kontrolü;
                if (!_config.AllowFactoryReset)
                {
                    _logger.LogCritical("Factory reset attempted but not allowed by configuration");
                    throw new InvalidOperationException("Factory reset is not allowed by configuration");
                }

                var operationId = Guid.NewGuid();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Donanım fabrika sıfırlama işlemi;
                bool success;

                if (_config.UseHardwareAcceleration)
                {
                    success = await FactoryResetWithHardwareAsync(adminPin);
                }
                else;
                {
                    // Yazılım fabrika sıfırlama fallback (simülasyon)
                    success = await FactoryResetWithSoftwareAsync(adminPin);
                }

                stopwatch.Stop();

                // Operasyonu logla;
                var operationEvent = new DeviceEvent;
                {
                    Id = operationId,
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.FactoryReset,
                    Operation = "FactoryReset",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Success = success,
                    IsSecurityCritical = true;
                };

                AddDeviceEvent(operationEvent);

                if (success)
                {
                    _logger.LogCritical("FACTORY RESET COMPLETED in {Duration}ms. All keys have been erased.",
                        stopwatch.ElapsedMilliseconds);

                    // Durumu sıfırla;
                    lock (_syncLock)
                    {
                        _isInitialized = false;
                    }

                    // Güvenlik bildirimi gönder;
                    await NotifyFactoryResetAsync();
                }
                else;
                {
                    _logger.LogWarning("Factory reset failed in {Duration}ms", stopwatch.ElapsedMilliseconds);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Factory reset failed");
                LogDeviceState("FactoryReset_Failed", $"Factory reset failed: {ex.Message}", DeviceState.OperationError);
                throw new CryptographicException($"Factory reset failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Mevcut cihaz durumunu al;
        /// </summary>
        public DeviceStatus GetDeviceStatus()
        {
            lock (_syncLock)
            {
                var recentEvents = _deviceEvents;
                    .Where(e => e.Timestamp > DateTime.UtcNow.AddMinutes(-_config.EventRetentionMinutes))
                    .OrderByDescending(e => e.Timestamp)
                    .Take(_config.EventRetentionCount)
                    .ToList();

                return new DeviceStatus;
                {
                    IsConnected = _isConnected,
                    IsInitialized = _isInitialized,
                    LastConnectionTime = _lastConnectionTime,
                    OperationCount = _operationCount,
                    DeviceHandle = _deviceHandle,
                    RecentEvents = recentEvents,
                    HealthScore = CalculateHealthScore(),
                    MemoryUsage = GetMemoryUsage(),
                    Temperature = GetDeviceTemperature()
                };
            }
        }

        /// <summary>
        /// Cihaz bilgilerini al;
        /// </summary>
        public async Task<DeviceInfo> GetDeviceInfoAsync()
        {
            ValidateDeviceState();

            try
            {
                if (_config.UseHardwareAcceleration)
                {
                    return await GetDeviceInfoWithHardwareAsync();
                }
                else;
                {
                    return GetDeviceInfoWithSoftware();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get device info");
                throw new CryptographicException($"Failed to get device info: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cihaz tanılama testi çalıştır;
        /// </summary>
        public async Task<DeviceDiagnostics> RunDiagnosticsAsync()
        {
            var diagnostics = new DeviceDiagnostics;
            {
                Timestamp = DateTime.UtcNow,
                TestId = Guid.NewGuid(),
                DeviceStatus = GetDeviceStatus()
            };

            try
            {
                _logger.LogInformation("Starting crypto device diagnostics");

                // 1. Bağlantı testi;
                diagnostics.ConnectionTest = await TestConnectionAsync();
                diagnostics.Tests.Add(new DeviceTest;
                {
                    TestName = "Connection",
                    Success = diagnostics.ConnectionTest.Success,
                    Details = diagnostics.ConnectionTest.Details,
                    DurationMs = diagnostics.ConnectionTest.DurationMs;
                });

                // 2. PIN testi (eğer başlatıldıysa)
                if (_isInitialized)
                {
                    diagnostics.PinTest = await TestPinAsync();
                    diagnostics.Tests.Add(new DeviceTest;
                    {
                        TestName = "PIN Authentication",
                        Success = diagnostics.PinTest.Success,
                        Details = diagnostics.PinTest.Details,
                        DurationMs = diagnostics.PinTest.DurationMs;
                    });
                }

                // 3. RNG testi;
                diagnostics.RngTest = await TestRandomGenerationAsync();
                diagnostics.Tests.Add(new DeviceTest;
                {
                    TestName = "Random Number Generation",
                    Success = diagnostics.RngTest.Success,
                    Details = diagnostics.RngTest.Details,
                    DurationMs = diagnostics.RngTest.DurationMs;
                });

                // 4. Şifreleme testi;
                diagnostics.EncryptionTest = await TestEncryptionAsync();
                diagnostics.Tests.Add(new DeviceTest;
                {
                    TestName = "Encryption/Decryption",
                    Success = diagnostics.EncryptionTest.Success,
                    Details = diagnostics.EncryptionTest.Details,
                    DurationMs = diagnostics.EncryptionTest.DurationMs;
                });

                // 5. İmza testi;
                diagnostics.SignatureTest = await TestSignaturesAsync();
                diagnostics.Tests.Add(new DeviceTest;
                {
                    TestName = "Signatures",
                    Success = diagnostics.SignatureTest.Success,
                    Details = diagnostics.SignatureTest.Details,
                    DurationMs = diagnostics.SignatureTest.DurationMs;
                });

                // 6. Anahtar üretim testi;
                diagnostics.KeyGenTest = await TestKeyGenerationAsync();
                diagnostics.Tests.Add(new DeviceTest;
                {
                    TestName = "Key Generation",
                    Success = diagnostics.KeyGenTest.Success,
                    Details = diagnostics.KeyGenTest.Details,
                    DurationMs = diagnostics.KeyGenTest.DurationMs;
                });

                // Genel durum;
                diagnostics.OverallStatus = diagnostics.Tests.All(t => t.Success)
                    ? DiagnosticsStatus.Passed;
                    : DiagnosticsStatus.Failed;

                diagnostics.Message = diagnostics.OverallStatus == DiagnosticsStatus.Passed;
                    ? "All device diagnostics passed"
                    : "Some device diagnostics failed";

                _logger.LogInformation("Device diagnostics completed: {Status}", diagnostics.OverallStatus);

                return diagnostics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during device diagnostics");
                diagnostics.OverallStatus = DiagnosticsStatus.Error;
                diagnostics.Message = $"Diagnostics error: {ex.Message}";
                return diagnostics;
            }
        }

        /// <summary>
        /// Cihaz geçmişini temizle;
        /// </summary>
        public void ClearDeviceHistory()
        {
            lock (_syncLock)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-_config.EventRetentionDays);
                _deviceEvents.RemoveAll(e => e.Timestamp < cutoffDate);
                _logger.LogInformation("Device history cleared, events older than {CutoffDate} removed", cutoffDate);
            }
        }

        /// <summary>
        /// Kaynakları serbest bırak;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _healthTimer?.Dispose();
            _monitoringCts?.Cancel();
            _monitoringCts?.Dispose();

            if (_pinnedKeyHandle.IsAllocated)
            {
                _pinnedKeyHandle.Free();
            }

            // Cihaz handle'ını serbest bırak;
            if (_deviceHandle != IntPtr.Zero)
            {
                ReleaseDeviceHandle(_deviceHandle);
                _deviceHandle = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        /// <summary>
        /// Event aboneliklerini başlat;
        /// </summary>
        private void InitializeEventSubscriptions()
        {
            // Güvenlik olaylarına abone ol;
            _securityMonitor.SecurityBreachDetected += OnSecurityBreachDetected;
            _securityMonitor.TamperAlert += OnTamperAlert;

            // Donanım kilit olaylarına abone ol;
            _hardwareLock.LockStatusChanged += OnHardwareLockStatusChanged;

            _logger.LogDebug("Event subscriptions initialized");
        }

        /// <summary>
        /// Cihazı izle;
        /// </summary>
        private async Task MonitorDeviceAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting crypto device monitoring");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.MonitoringIntervalMs, cancellationToken);

                    // Cihaz durumunu kontrol et;
                    await CheckDeviceStatusAsync();

                    // Cihaz sağlığını kontrol et;
                    await CheckDeviceHealthAsync();

                    // Güvenlik kontrolleri;
                    await PerformSecurityChecksAsync();

                    // Performans metriklerini topla;
                    await CollectMetricsAsync();

                    // Otomatik bağlantı yenileme;
                    await CheckAutoReconnectAsync();
                }
                catch (OperationCanceledException)
                {
                    // İptal edildi, döngüden çık;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in device monitoring");
                    await Task.Delay(5000, cancellationToken);
                }
            }

            _logger.LogInformation("Crypto device monitoring stopped");
        }

        /// <summary>
        /// Cihaz durumunu kontrol et;
        /// </summary>
        private async Task CheckDeviceStatusAsync()
        {
            try
            {
                if (_isConnected)
                {
                    // Bağlantı durumunu doğrula;
                    var isAlive = await VerifyDeviceAliveAsync();

                    if (!isAlive)
                    {
                        _logger.LogWarning("Device connection lost, attempting to reconnect");
                        await HandleConnectionLossAsync();
                    }
                }
                else if (_config.AutoReconnect)
                {
                    // Otomatik yeniden bağlanma;
                    await AttemptAutoReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking device status");
            }
        }

        /// <summary>
        /// Cihaz sağlığını kontrol et;
        /// </summary>
        private async Task CheckDeviceHealthAsync()
        {
            try
            {
                if (!_isConnected) return;

                var health = await GetDeviceHealthAsync();

                if (health.Status != DeviceHealthStatus.Healthy)
                {
                    var healthEvent = new DeviceEvent;
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = DateTime.UtcNow,
                        EventType = DeviceEventType.HealthWarning,
                        Operation = "HealthCheck",
                        Details = $"Device health: {health.Status}, Message: {health.Message}",
                        Success = false;
                    };

                    AddDeviceEvent(healthEvent);

                    if (health.Status == DeviceHealthStatus.Critical)
                    {
                        _logger.LogError("Critical device health issue: {Message}", health.Message);
                        await NotifyCriticalHealthAsync(health);
                    }
                    else if (health.Status == DeviceHealthStatus.Warning)
                    {
                        _logger.LogWarning("Device health warning: {Message}", health.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking device health");
            }
        }

        /// <summary>
        /// Güvenlik kontrolleri yap;
        /// </summary>
        private async Task PerformSecurityChecksAsync()
        {
            try
            {
                if (!_isConnected) return;

                // PIN deneme sayısını kontrol et;
                var pinStatus = await GetPinStatusAsync();
                if (pinStatus.RemainingAttempts <= 1)
                {
                    _logger.LogWarning("Low PIN attempts remaining: {Remaining}", pinStatus.RemainingAttempts);

                    var securityEvent = new DeviceEvent;
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = DateTime.UtcNow,
                        EventType = DeviceEventType.SecurityWarning,
                        Operation = "PinCheck",
                        Details = $"Low PIN attempts: {pinStatus.RemainingAttempts}",
                        Success = false,
                        IsSecurityCritical = true;
                    };

                    AddDeviceEvent(securityEvent);
                }

                // Cihaz müdahale durumunu kontrol et;
                var tamperStatus = await CheckTamperStatusAsync();
                if (tamperStatus.IsTampered)
                {
                    _logger.LogCritical("DEVICE TAMPER DETECTED: {Details}", tamperStatus.Details);

                    var tamperEvent = new DeviceEvent;
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = DateTime.UtcNow,
                        EventType = DeviceEventType.TamperDetected,
                        Operation = "TamperCheck",
                        Details = tamperStatus.Details,
                        Success = false,
                        IsSecurityCritical = true;
                    };

                    AddDeviceEvent(tamperEvent);

                    // Acil durum prosedürlerini uygula;
                    await HandleTamperDetectedAsync(tamperStatus);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing security checks");
            }
        }

        /// <summary>
        /// Metrikleri topla;
        /// </summary>
        private async Task CollectMetricsAsync()
        {
            try
            {
                if (!_isConnected) return;

                var metrics = new DeviceMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    OperationCount = _operationCount,
                    Uptime = _lastConnectionTime.HasValue;
                        ? DateTime.UtcNow - _lastConnectionTime.Value;
                        : TimeSpan.Zero,
                    MemoryUsage = GetMemoryUsage(),
                    Temperature = GetDeviceTemperature(),
                    PerformanceScore = CalculatePerformanceScore()
                };

                // Metrikleri yayınla;
                await PublishMetricsAsync(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting metrics");
            }
        }

        /// <summary>
        /// Otomatik yeniden bağlanmayı kontrol et;
        /// </summary>
        private async Task CheckAutoReconnectAsync()
        {
            if (!_config.AutoReconnect || _isConnected) return;

            try
            {
                // Bağlantı kaybından belirli süre sonra yeniden dene;
                var timeSinceDisconnect = DateTime.UtcNow - (_lastConnectionTime ?? DateTime.MinValue);
                if (timeSinceDisconnect > TimeSpan.FromSeconds(_config.ReconnectDelaySeconds))
                {
                    _logger.LogInformation("Attempting auto-reconnect after {Delay} seconds", _config.ReconnectDelaySeconds);
                    await AttemptAutoReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during auto-reconnect check");
            }
        }

        /// <summary>
        /// Cihaz durumunu doğrula;
        /// </summary>
        private void ValidateDeviceState()
        {
            if (!_isConnected)
                throw new InvalidOperationException("Crypto device is not connected");

            if (!_isInitialized)
                throw new InvalidOperationException("Crypto device is not initialized");

            if (_deviceHandle == IntPtr.Zero)
                throw new InvalidOperationException("Invalid device handle");
        }

        /// <summary>
        /// Giriş verisini doğrula;
        /// </summary>
        private void ValidateInput(byte[] data, string paramName)
        {
            if (data == null)
                throw new ArgumentNullException(paramName);

            if (data.Length == 0)
                throw new ArgumentException("Data cannot be empty", paramName);

            if (data.Length > _config.MaxDataSize)
                throw new ArgumentException($"Data size exceeds maximum allowed ({_config.MaxDataSize} bytes)", paramName);
        }

        /// <summary>
        /// Anahtar ID'sini doğrula;
        /// </summary>
        private void ValidateKeyId(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId))
                throw new ArgumentException("Key ID cannot be null or empty", nameof(keyId));

            if (keyId.Length > 64)
                throw new ArgumentException("Key ID too long (max 64 characters)", nameof(keyId));
        }

        /// <summary>
        /// Cihaz olayı ekle;
        /// </summary>
        private void AddDeviceEvent(DeviceEvent deviceEvent)
        {
            lock (_syncLock)
            {
                _deviceEvents.Add(deviceEvent);
                _operationCount++;

                // Maksimum kayıt sayısını kontrol et;
                if (_deviceEvents.Count > _config.MaxStoredEvents)
                {
                    _deviceEvents.RemoveRange(0, _deviceEvents.Count - _config.MaxStoredEvents);
                }

                // Event'i yayınla;
                _ = Task.Run(async () => await PublishDeviceEventAsync(deviceEvent));
            }
        }

        /// <summary>
        /// Cihaz durumunu logla;
        /// </summary>
        private void LogDeviceState(string state, string message, DeviceState deviceState)
        {
            try
            {
                var logEntry = new;
                {
                    Timestamp = DateTime.UtcNow,
                    Component = "CryptoDevice",
                    State = state,
                    Message = message,
                    DeviceState = deviceState,
                    IsConnected = _isConnected,
                    IsInitialized = _isInitialized,
                    OperationCount = _operationCount;
                };

                _logger.LogDebug("Device state: {@LogEntry}", logEntry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging device state");
            }
        }

        /// <summary>
        /// Operasyonu logla;
        /// </summary>
        private void LogOperation(string operation, string details)
        {
            try
            {
                _logger.LogDebug("Crypto operation: {Operation} - {Details}", operation, details);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging operation");
            }
        }

        /// <summary>
        /// Sağlık skorunu hesapla;
        /// </summary>
        private double CalculateHealthScore()
        {
            try
            {
                var recentEvents = _deviceEvents;
                    .Where(e => e.Timestamp > DateTime.UtcNow.AddHours(-1))
                    .ToList();

                if (!recentEvents.Any())
                    return 100.0;

                var errorEvents = recentEvents.Count(e => !e.Success);
                var totalEvents = recentEvents.Count;

                var errorRatio = (double)errorEvents / totalEvents;
                var healthScore = Math.Max(0.0, 100.0 - (errorRatio * 100.0));

                return healthScore;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Performans skorunu hesapla;
        /// </summary>
        private double CalculatePerformanceScore()
        {
            try
            {
                var recentOperations = _deviceEvents;
                    .Where(e => e.Timestamp > DateTime.UtcNow.AddMinutes(-5) && e.DurationMs > 0)
                    .ToList();

                if (!recentOperations.Any())
                    return 100.0;

                var avgDuration = recentOperations.Average(e => e.DurationMs);
                var maxAllowedDuration = _config.ExpectedMaxOperationMs;

                var performanceRatio = Math.Min(1.0, maxAllowedDuration / avgDuration);
                var performanceScore = performanceRatio * 100.0;

                return performanceScore;
            }
            catch
            {
                return 0.0;
            }
        }

        #region Donanım İşlemleri (Simülasyon/Gerçek)

        /// <summary>
        /// Cihazları keşfet;
        /// </summary>
        private async Task<List<DeviceInfo>> DiscoverDevicesAsync()
        {
            // Gerçek cihaz keşfi (PC/SC veya üretici SDK'sı kullanılır)
            // Bu örnekte simüle ediliyor;

            await Task.Delay(100);

            var devices = new List<DeviceInfo>();

            if (_config.SimulateHardware)
            {
                devices.Add(new DeviceInfo;
                {
                    DeviceId = "SIM_CRYPTO_001",
                    DeviceName = "NEDA Simulated Crypto Device",
                    Manufacturer = "NEDA Security",
                    Model = "CryptoToken Simulator v2.0",
                    SerialNumber = "SIM-2024-001",
                    FirmwareVersion = "2.1.0",
                    MaxKeySlots = 20,
                    SupportedAlgorithms = new List<string>
                    {
                        "RSA-2048", "RSA-4096", "AES-256", "ECC-256", "SHA-256", "SHA-384", "SHA-512"
                    },
                    IsFips140Compliant = true,
                    IsConnected = true;
                });
            }
            else;
            {
                // Gerçek cihaz keşfi kodu buraya gelecek;
                // Örnek: PC/SC üzerinden akıllı kart okuyucularını tarama;
            }

            return devices;
        }

        /// <summary>
        /// Cihaza bağlan;
        /// </summary>
        private async Task<ConnectResult> ConnectToDeviceAsync(DeviceInfo device)
        {
            // Gerçek cihaz bağlantısı;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(50);

            if (_config.SimulateHardware)
            {
                return new ConnectResult;
                {
                    Success = true,
                    DeviceHandle = new IntPtr(0x12345678), // Simüle edilmiş handle;
                    Protocol = SCARD_PROTOCOL_T1,
                    Message = "Connected to simulated device"
                };
            }
            else;
            {
                // Gerçek bağlantı kodu buraya gelecek;
                // Örnek: SCardConnect çağrısı;
                return new ConnectResult;
                {
                    Success = false,
                    Error = "Real hardware connection not implemented in this example"
                };
            }
        }

        /// <summary>
        /// Cihazı başlat;
        /// </summary>
        private async Task<InitResult> InitializeDeviceAsync()
        {
            // Gerçek cihaz başlatma (PIN girişi, oturum açma)
            // Bu örnekte simüle ediliyor;

            await Task.Delay(50);

            if (_config.SimulateHardware)
            {
                // Simüle edilmiş PIN doğrulama;
                var pinValid = await VerifyPinAsync(_config.DefaultPin);

                return new InitResult;
                {
                    Success = pinValid,
                    Message = pinValid ? "Device initialized successfully" : "PIN verification failed",
                    SessionId = Guid.NewGuid().ToString()
                };
            }
            else;
            {
                // Gerçek başlatma kodu buraya gelecek;
                return new InitResult;
                {
                    Success = false,
                    Error = "Real hardware initialization not implemented in this example"
                };
            }
        }

        /// <summary>
        /// Cihazdan bağlantıyı kes;
        /// </summary>
        private async Task<DisconnectResult> DisconnectDeviceAsync()
        {
            // Gerçek cihaz bağlantısını kesme;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(30);

            if (_config.SimulateHardware)
            {
                return new DisconnectResult;
                {
                    Success = true,
                    Message = "Disconnected from simulated device"
                };
            }
            else;
            {
                // Gerçek bağlantı kesme kodu buraya gelecek;
                return new DisconnectResult;
                {
                    Success = true,
                    Message = "Disconnected from device"
                };
            }
        }

        /// <summary>
        /// Güvenli bağlantı kesme;
        /// </summary>
        private async Task SecureDisconnectAsync()
        {
            try
            {
                if (_isConnected)
                {
                    _logger.LogInformation("Securely disconnecting crypto device");
                    await DisconnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during secure disconnect");
            }
        }

        /// <summary>
        /// Donanımda şifrele;
        /// </summary>
        private async Task<byte[]> EncryptWithHardwareAsync(byte[] plaintext, string keyId, CryptoAlgorithm algorithm)
        {
            // Gerçek donanım şifreleme;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Simüle edilmiş şifreleme;
                    using var aes = Aes.Create();
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    aes.GenerateIV();
                    aes.GenerateKey();

                    using var encryptor = aes.CreateEncryptor();
                    var encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

                    // IV + şifreli metin;
                    var result = new byte[aes.IV.Length + encrypted.Length];
                    Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                    Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

                    return result;
                }
                catch
                {
                    return null;
                }
            }
            else;
            {
                // Gerçek donanım şifreleme kodu buraya gelecek;
                throw new NotImplementedException("Real hardware encryption not implemented");
            }
        }

        /// <summary>
        /// Yazılımda şifrele (fallback)
        /// </summary>
        private async Task<byte[]> EncryptWithSoftwareAsync(byte[] plaintext, string keyId, CryptoAlgorithm algorithm)
        {
            // Yazılım fallback şifreleme;
            await Task.Delay(_config.SimulatedOperationDelayMs / 2);

            try
            {
                // Basit simülasyon - gerçekte anahtar yönetimi gerekli;
                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                // Simüle edilmiş anahtar (gerçekte keyId'den türetilmeli)
                var key = DeriveKeyFromId(keyId);
                aes.Key = key;

                aes.GenerateIV();

                using var encryptor = aes.CreateEncryptor();
                var encrypted = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

                // IV + şifreli metin;
                var result = new byte[aes.IV.Length + encrypted.Length];
                Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Donanımda çöz;
        /// </summary>
        private async Task<byte[]> DecryptWithHardwareAsync(byte[] ciphertext, string keyId, CryptoAlgorithm algorithm)
        {
            // Gerçek donanım çözme;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Simüle edilmiş çözme;
                    using var aes = Aes.Create();
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    // IV'yi ayır (ilk 16 byte)
                    var iv = new byte[16];
                    Buffer.BlockCopy(ciphertext, 0, iv, 0, iv.Length);
                    aes.IV = iv;

                    // Şifreli metni ayır;
                    var encrypted = new byte[ciphertext.Length - iv.Length];
                    Buffer.BlockCopy(ciphertext, iv.Length, encrypted, 0, encrypted.Length);

                    // Simüle edilmiş anahtar;
                    aes.GenerateKey();

                    using var decryptor = aes.CreateDecryptor();
                    var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

                    return decrypted;
                }
                catch
                {
                    return null;
                }
            }
            else;
            {
                // Gerçek donanım çözme kodu buraya gelecek;
                throw new NotImplementedException("Real hardware decryption not implemented");
            }
        }

        /// <summary>
        /// Yazılımda çöz (fallback)
        /// </summary>
        private async Task<byte[]> DecryptWithSoftwareAsync(byte[] ciphertext, string keyId, CryptoAlgorithm algorithm)
        {
            // Yazılım fallback çözme;
            await Task.Delay(_config.SimulatedOperationDelayMs / 2);

            try
            {
                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                // IV'yi ayır (ilk 16 byte)
                var iv = new byte[16];
                Buffer.BlockCopy(ciphertext, 0, iv, 0, iv.Length);
                aes.IV = iv;

                // Şifreli metni ayır;
                var encrypted = new byte[ciphertext.Length - iv.Length];
                Buffer.BlockCopy(ciphertext, iv.Length, encrypted, 0, encrypted.Length);

                // Simüle edilmiş anahtar;
                var key = DeriveKeyFromId(keyId);
                aes.Key = key;

                using var decryptor = aes.CreateDecryptor();
                var decrypted = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

                return decrypted;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Donanımda imzala;
        /// </summary>
        private async Task<byte[]> SignWithHardwareAsync(byte[] data, string keyId, SignAlgorithm algorithm)
        {
            // Gerçek donanım imzalama;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Simüle edilmiş imza;
                    using var rsa = RSA.Create(2048);
                    var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return signature;
                }
                catch
                {
                    return null;
                }
            }
            else;
            {
                // Gerçek donanım imzalama kodu buraya gelecek;
                throw new NotImplementedException("Real hardware signing not implemented");
            }
        }

        /// <summary>
        /// Yazılımda imzala (fallback)
        /// </summary>
        private async Task<byte[]> SignWithSoftwareAsync(byte[] data, string keyId, SignAlgorithm algorithm)
        {
            // Yazılım fallback imzalama;
            await Task.Delay(_config.SimulatedOperationDelayMs / 2);

            try
            {
                // Simüle edilmiş imza;
                using var rsa = RSA.Create(2048);
                var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return signature;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Donanımda doğrula;
        /// </summary>
        private async Task<bool> VerifyWithHardwareAsync(byte[] data, byte[] signature, string keyId, SignAlgorithm algorithm)
        {
            // Gerçek donanım doğrulama;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Simüle edilmiş doğrulama;
                    using var rsa = RSA.Create(2048);
                    var isValid = rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return isValid;
                }
                catch
                {
                    return false;
                }
            }
            else;
            {
                // Gerçek donanım doğrulama kodu buraya gelecek;
                throw new NotImplementedException("Real hardware verification not implemented");
            }
        }

        /// <summary>
        /// Yazılımda doğrula (fallback)
        /// </summary>
        private async Task<bool> VerifyWithSoftwareAsync(byte[] data, byte[] signature, string keyId, SignAlgorithm algorithm)
        {
            // Yazılım fallback doğrulama;
            await Task.Delay(_config.SimulatedOperationDelayMs / 2);

            try
            {
                // Simüle edilmiş doğrulama;
                using var rsa = RSA.Create(2048);
                var isValid = rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return isValid;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Donanımda rastgele sayı üret;
        /// </summary>
        private async Task<byte[]> GenerateRandomWithHardwareAsync(int length)
        {
            // Gerçek donanım RNG;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs / 3);

            if (_config.SimulateHardware)
            {
                try
                {
                    var randomBytes = new byte[length];
                    using var rng = RandomNumberGenerator.Create();
                    rng.GetBytes(randomBytes);
                    return randomBytes;
                }
                catch
                {
                    return null;
                }
            }
            else;
            {
                // Gerçek donanım RNG kodu buraya gelecek;
                throw new NotImplementedException("Real hardware RNG not implemented");
            }
        }

        /// <summary>
        /// Yazılımda rastgele sayı üret (fallback)
        /// </summary>
        private byte[] GenerateRandomWithSoftware(int length)
        {
            // Yazılım fallback RNG;
            try
            {
                var randomBytes = new byte[length];
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(randomBytes);
                return randomBytes;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Donanımda anahtar çifti oluştur;
        /// </summary>
        private async Task<KeyPair> GenerateKeyPairWithHardwareAsync(KeyAlgorithm algorithm, string keyId, KeyUsage usage)
        {
            // Gerçek donanım anahtar üretimi;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs * 2); // Anahtar üretimi daha uzun sürer;

            if (_config.SimulateHardware)
            {
                try
                {
                    var keySize = GetKeySize(algorithm);

                    if (algorithm.ToString().StartsWith("RSA"))
                    {
                        using var rsa = RSA.Create(keySize);
                        var keyPair = new KeyPair;
                        {
                            KeyId = keyId,
                            Algorithm = algorithm,
                            PublicKey = rsa.ExportRSAPublicKey(),
                            PrivateKey = null, // Donanımda özel anahtar dışarı çıkmaz;
                            CreatedAt = DateTime.UtcNow,
                            Usage = usage,
                            IsHardwareProtected = true;
                        };

                        return keyPair;
                    }
                    else if (algorithm.ToString().StartsWith("ECC"))
                    {
                        using var ecdsa = ECDsa.Create();
                        ecdsa.KeySize = keySize;

                        var keyPair = new KeyPair;
                        {
                            KeyId = keyId,
                            Algorithm = algorithm,
                            PublicKey = ecdsa.ExportSubjectPublicKeyInfo(),
                            PrivateKey = null,
                            CreatedAt = DateTime.UtcNow,
                            Usage = usage,
                            IsHardwareProtected = true;
                        };

                        return keyPair;
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            }
            else;
            {
                // Gerçek donanım anahtar üretimi kodu buraya gelecek;
                throw new NotImplementedException("Real hardware key generation not implemented");
            }
        }

        /// <summary>
        /// Yazılımda anahtar çifti oluştur (fallback)
        /// </summary>
        private async Task<KeyPair> GenerateKeyPairWithSoftwareAsync(KeyAlgorithm algorithm, string keyId, KeyUsage usage)
        {
            // Yazılım fallback anahtar üretimi;
            await Task.Delay(_config.SimulatedOperationDelayMs);

            try
            {
                var keySize = GetKeySize(algorithm);

                if (algorithm.ToString().StartsWith("RSA"))
                {
                    using var rsa = RSA.Create(keySize);
                    var keyPair = new KeyPair;
                    {
                        KeyId = keyId,
                        Algorithm = algorithm,
                        PublicKey = rsa.ExportRSAPublicKey(),
                        PrivateKey = rsa.ExportRSAPrivateKey(),
                        CreatedAt = DateTime.UtcNow,
                        Usage = usage,
                        IsHardwareProtected = false;
                    };

                    return keyPair;
                }
                else if (algorithm.ToString().StartsWith("ECC"))
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.KeySize = keySize;

                    var keyPair = new KeyPair;
                    {
                        KeyId = keyId,
                        Algorithm = algorithm,
                        PublicKey = ecdsa.ExportSubjectPublicKeyInfo(),
                        PrivateKey = ecdsa.ExportPkcs8PrivateKey(),
                        CreatedAt = DateTime.UtcNow,
                        Usage = usage,
                        IsHardwareProtected = false;
                    };

                    return keyPair;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Donanımdan anahtarı sil;
        /// </summary>
        private async Task<bool> DeleteKeyWithHardwareAsync(string keyId, bool force)
        {
            // Gerçek donanım anahtar silme;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Simüle edilmiş anahtar silme;
                    _logger.LogInformation("Simulated key deletion: {KeyId}, force: {Force}", keyId, force);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            else;
            {
                // Gerçek donanım anahtar silme kodu buraya gelecek;
                throw new NotImplementedException("Real hardware key deletion not implemented");
            }
        }

        /// <summary>
        /// Yazılımdan anahtarı sil (fallback)
        /// </summary>
        private async Task<bool> DeleteKeyWithSoftwareAsync(string keyId, bool force)
        {
            // Yazılım fallback anahtar silme;
            await Task.Delay(_config.SimulatedOperationDelayMs / 2);

            try
            {
                // Simüle edilmiş anahtar silme;
                _logger.LogInformation("Software key deletion: {KeyId}, force: {Force}", keyId, force);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Donanımda PIN değiştir;
        /// </summary>
        private async Task<bool> ChangePinWithHardwareAsync(string oldPin, string newPin)
        {
            // Gerçek donanım PIN değiştirme;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Eski PIN'i doğrula;
                    var pinValid = await VerifyPinAsync(oldPin);
                    if (!pinValid)
                        return false;

                    // Yeni PIN'i kaydet (simülasyon)
                    _config.DefaultPin = newPin;

                    return true;
                }
                catch
                {
                    return false;
                }
            }
            else;
            {
                // Gerçek donanım PIN değiştirme kodu buraya gelecek;
                throw new NotImplementedException("Real hardware PIN change not implemented");
            }
        }

        /// <summary>
        /// Yazılımda PIN değiştir (fallback)
        /// </summary>
        private async Task<bool> ChangePinWithSoftwareAsync(string oldPin, string newPin)
        {
            // Yazılım fallback PIN değiştirme;
            await Task.Delay(_config.SimulatedOperationDelayMs / 2);

            try
            {
                // Basit simülasyon;
                if (oldPin == _config.DefaultPin)
                {
                    _config.DefaultPin = newPin;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Donanımda fabrika ayarlarına sıfırla;
        /// </summary>
        private async Task<bool> FactoryResetWithHardwareAsync(string adminPin)
        {
            // Gerçek donanım fabrika sıfırlama;
            // Bu örnekte simüle ediliyor;

            await Task.Delay(_config.SimulatedOperationDelayMs * 3);

            if (_config.SimulateHardware)
            {
                try
                {
                    // Admin PIN doğrulama (simülasyon)
                    if (adminPin != "ADMIN123") // Sabit değer, gerçekte güvenli olmalı;
                        return false;

                    // Simüle edilmiş fabrika sıfırlama;
                    _logger.LogCritical("SIMULATED FACTORY RESET - All keys would be erased");

                    // Cihaz durumunu sıfırla;
                    lock (_syncLock)
                    {
                        _isInitialized = false;
                        _deviceEvents.Clear();
                        _operationCount = 0;
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }
            else;
            {
                // Gerçek donanım fabrika sıfırlama kodu buraya gelecek;
                throw new NotImplementedException("Real hardware factory reset not implemented");
            }
        }

        /// <summary>
        /// Yazılımda fabrika ayarlarına sıfırla (fallback)
        /// </summary>
        private async Task<bool> FactoryResetWithSoftwareAsync(string adminPin)
        {
            // Yazılım fallback fabrika sıfırlama;
            await Task.Delay(_config.SimulatedOperationDelayMs);

            try
            {
                // Basit simülasyon;
                if (adminPin != "ADMIN123")
                    return false;

                _logger.LogCritical("SOFTWARE FACTORY RESET SIMULATION");

                lock (_syncLock)
                {
                    _isInitialized = false;
                    _deviceEvents.Clear();
                    _operationCount = 0;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Anahtar çiftini sakla;
        /// </summary>
        private async Task StoreKeyPairAsync(KeyPair keyPair)
        {
            try
            {
                // Anahtarı güvenli depoya kaydet;
                // Gerçek implementasyon için key store entegrasyonu gerekir;
                await Task.Delay(10);
                _logger.LogDebug("Key pair stored: {KeyId}", keyPair.KeyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store key pair: {KeyId}", keyPair.KeyId);
            }
        }

        /// <summary>
        /// PIN doğrula;
        /// </summary>
        private async Task<bool> VerifyPinAsync(string pin)
        {
            // PIN doğrulama (simülasyon)
            await Task.Delay(20);

            // Basit simülasyon - gerçekte donanım PIN doğrulaması;
            return pin == _config.DefaultPin;
        }

        /// <summary>
        /// Anahtar ID'sinden anahtar türet;
        /// </summary>
        private byte[] DeriveKeyFromId(string keyId)
        {
            // Basit anahtar türetme (sadece simülasyon için)
            using var sha256 = SHA256.Create();
            var keyBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(keyId + _config.KeyDerivationSalt));

            // 256-bit anahtar için ilk 32 byte'ı al;
            var key = new byte[32];
            Buffer.BlockCopy(keyBytes, 0, key, 0, 32);

            return key;
        }

        /// <summary>
        /// Anahtar boyutunu al;
        /// </summary>
        private int GetKeySize(KeyAlgorithm algorithm)
        {
            return algorithm switch;
            {
                KeyAlgorithm.RSA_1024 => 1024,
                KeyAlgorithm.RSA_2048 => 2048,
                KeyAlgorithm.RSA_4096 => 4096,
                KeyAlgorithm.ECC_256 => 256,
                KeyAlgorithm.ECC_384 => 384,
                KeyAlgorithm.ECC_521 => 521,
                _ => 2048;
            };
        }

        /// <summary>
        /// Cihaz bilgilerini al (donanım)
        /// </summary>
        private async Task<DeviceInfo> GetDeviceInfoWithHardwareAsync()
        {
            await Task.Delay(30);

            return new DeviceInfo;
            {
                DeviceId = "HW_CRYPTO_001",
                DeviceName = "Hardware Crypto Device",
                Manufacturer = "NEDA Security",
                Model = "CryptoToken Pro v3.0",
                SerialNumber = "HW-2024-001",
                FirmwareVersion = "3.2.1",
                MaxKeySlots = 50,
                SupportedAlgorithms = new List<string>
                {
                    "RSA-1024", "RSA-2048", "RSA-4096",
                    "AES-128", "AES-192", "AES-256",
                    "ECC-256", "ECC-384", "ECC-521",
                    "SHA-256", "SHA-384", "SHA-512"
                },
                IsFips140Compliant = true,
                IsConnected = _isConnected;
            };
        }

        /// <summary>
        /// Cihaz bilgilerini al (yazılım)
        /// </summary>
        private DeviceInfo GetDeviceInfoWithSoftware()
        {
            return new DeviceInfo;
            {
                DeviceId = "SW_CRYPTO_001",
                DeviceName = "Software Crypto Emulation",
                Manufacturer = "NEDA Security",
                Model = "Crypto Emulator v1.0",
                SerialNumber = "SW-2024-001",
                FirmwareVersion = "1.0.0",
                MaxKeySlots = 100,
                SupportedAlgorithms = new List<string>
                {
                    "RSA-1024", "RSA-2048", "RSA-4096",
                    "AES-128", "AES-192", "AES-256",
                    "ECC-256", "ECC-384", "ECC-521"
                },
                IsFips140Compliant = false,
                IsConnected = _isConnected;
            };
        }

        /// <summary>
        /// Cihaz handle'ını serbest bırak;
        /// </summary>
        private void ReleaseDeviceHandle(IntPtr handle)
        {
            // Donanım handle'ını serbest bırak;
            // Gerçek implementasyon platforma bağlı;
            if (handle != IntPtr.Zero)
            {
                // Simülasyon: sadece logla;
                _logger.LogDebug("Device handle released: {Handle}", handle);
            }
        }

        /// <summary>
        /// Bellek kullanımını al;
        /// </summary>
        private long GetMemoryUsage()
        {
            try
            {
                // Basit simülasyon;
                return _deviceEvents.Count * 1024L; // Her event ~1KB;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Cihaz sıcaklığını al;
        /// </summary>
        private double GetDeviceTemperature()
        {
            try
            {
                // Simüle edilmiş sıcaklık;
                var baseTemp = 35.0;
                var loadFactor = Math.Min(1.0, _operationCount / 1000.0);
                return baseTemp + (loadFactor * 15.0);
            }
            catch
            {
                return 0.0;
            }
        }

        #region Tanılama Testleri;

        /// <summary>
        /// Bağlantı testi;
        /// </summary>
        private async Task<DeviceTestResult> TestConnectionAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var isAlive = await VerifyDeviceAliveAsync();
                stopwatch.Stop();

                return new DeviceTestResult;
                {
                    Success = isAlive,
                    Details = isAlive ? "Device connection alive" : "Device connection dead",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new DeviceTestResult;
                {
                    Success = false,
                    Details = $"Connection test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// PIN testi;
        /// </summary>
        private async Task<DeviceTestResult> TestPinAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var pinValid = await VerifyPinAsync(_config.DefaultPin);
                stopwatch.Stop();

                return new DeviceTestResult;
                {
                    Success = pinValid,
                    Details = pinValid ? "PIN authentication successful" : "PIN authentication failed",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new DeviceTestResult;
                {
                    Success = false,
                    Details = $"PIN test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// RNG testi;
        /// </summary>
        private async Task<DeviceTestResult> TestRandomGenerationAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var randomBytes = await GenerateRandomAsync(32);
                stopwatch.Stop();

                var success = randomBytes != null && randomBytes.Length == 32;

                // Basit entropi kontrolü (simülasyon)
                if (success)
                {
                    var distinctBytes = randomBytes.Distinct().Count();
                    success = distinctBytes > 20; // En az 20 farklı byte;
                }

                return new DeviceTestResult;
                {
                    Success = success,
                    Details = success ? "Random generation successful" : "Random generation failed",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new DeviceTestResult;
                {
                    Success = false,
                    Details = $"RNG test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// Şifreleme testi;
        /// </summary>
        private async Task<DeviceTestResult> TestEncryptionAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var testData = Guid.NewGuid().ToByteArray();
                var keyId = "TEST_KEY_" + Guid.NewGuid().ToString("N");

                // Anahtar oluştur (simülasyon)
                var keyPair = await GenerateKeyPairWithSoftwareAsync(KeyAlgorithm.RSA_2048, keyId, KeyUsage.SignAndEncrypt);

                if (keyPair == null)
                {
                    stopwatch.Stop();
                    return new DeviceTestResult;
                    {
                        Success = false,
                        Details = "Failed to generate test key",
                        DurationMs = stopwatch.ElapsedMilliseconds;
                    };
                }

                // Şifrele;
                var encrypted = await EncryptAsync(testData, keyId);
                if (encrypted == null)
                {
                    stopwatch.Stop();
                    return new DeviceTestResult;
                    {
                        Success = false,
                        Details = "Encryption failed",
                        DurationMs = stopwatch.ElapsedMilliseconds;
                    };
                }

                // Çöz;
                var decrypted = await DecryptAsync(encrypted, keyId);
                stopwatch.Stop();

                var success = decrypted != null && testData.SequenceEqual(decrypted);

                return new DeviceTestResult;
                {
                    Success = success,
                    Details = success ? "Encryption/decryption successful" : "Encryption/decryption failed",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new DeviceTestResult;
                {
                    Success = false,
                    Details = $"Encryption test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// İmza testi;
        /// </summary>
        private async Task<DeviceTestResult> TestSignaturesAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var testData = Guid.NewGuid().ToByteArray();
                var keyId = "SIGN_TEST_KEY_" + Guid.NewGuid().ToString("N");

                // Anahtar oluştur (simülasyon)
                var keyPair = await GenerateKeyPairWithSoftwareAsync(KeyAlgorithm.RSA_2048, keyId, KeyUsage.Sign);

                if (keyPair == null)
                {
                    stopwatch.Stop();
                    return new DeviceTestResult;
                    {
                        Success = false,
                        Details = "Failed to generate test key",
                        DurationMs = stopwatch.ElapsedMilliseconds;
                    };
                }

                // İmzala;
                var signature = await SignAsync(testData, keyId);
                if (signature == null)
                {
                    stopwatch.Stop();
                    return new DeviceTestResult;
                    {
                        Success = false,
                        Details = "Signing failed",
                        DurationMs = stopwatch.ElapsedMilliseconds;
                    };
                }

                // Doğrula;
                var isValid = await VerifyAsync(testData, signature, keyId);
                stopwatch.Stop();

                return new DeviceTestResult;
                {
                    Success = isValid,
                    Details = isValid ? "Signature verification successful" : "Signature verification failed",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new DeviceTestResult;
                {
                    Success = false,
                    Details = $"Signature test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// Anahtar üretim testi;
        /// </summary>
        private async Task<DeviceTestResult> TestKeyGenerationAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var keyId = "GEN_TEST_KEY_" + Guid.NewGuid().ToString("N");
                var keyPair = await GenerateKeyPairAsync(KeyAlgorithm.RSA_2048, keyId);
                stopwatch.Stop();

                var success = keyPair != null &&
                             keyPair.PublicKey != null &&
                             keyPair.PublicKey.Length > 0;

                return new DeviceTestResult;
                {
                    Success = success,
                    Details = success ? "Key generation successful" : "Key generation failed",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new DeviceTestResult;
                {
                    Success = false,
                    Details = $"Key generation test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        #endregion;

        #region Yardımcı Donanım Metodları;

        /// <summary>
        /// Cihazın canlı olduğunu doğrula;
        /// </summary>
        private async Task<bool> VerifyDeviceAliveAsync()
        {
            try
            {
                if (_config.SimulateHardware)
                {
                    await Task.Delay(10);
                    return true;
                }
                else;
                {
                    // Gerçek donanım canlılık kontrolü;
                    // Örnek: SCardStatus çağrısı;
                    return _deviceHandle != IntPtr.Zero;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Bağlantı kaybını işle;
        /// </summary>
        private async Task HandleConnectionLossAsync()
        {
            try
            {
                _logger.LogWarning("Handling device connection loss");

                var connectionEvent = new DeviceEvent;
                {
                    Id = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.ConnectionLost,
                    Operation = "ConnectionLoss",
                    Details = "Device connection lost",
                    Success = false;
                };

                AddDeviceEvent(connectionEvent);

                lock (_syncLock)
                {
                    _isConnected = false;
                    _isInitialized = false;
                }

                // Durum değişikliğini bildir;
                OnDeviceStatusChanged(new DeviceStatusChangedEventArgs;
                {
                    IsConnected = false,
                    IsInitialized = false,
                    ChangedAt = DateTime.UtcNow,
                    Reason = "Connection lost"
                });

                // Yeniden bağlanmayı dene;
                if (_config.AutoReconnect)
                {
                    await AttemptAutoReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling connection loss");
            }
        }

        /// <summary>
        /// Otomatik yeniden bağlanmayı dene;
        /// </summary>
        private async Task AttemptAutoReconnectAsync()
        {
            try
            {
                _logger.LogInformation("Attempting auto-reconnect to crypto device");
                var success = await ConnectAndInitializeAsync();

                if (success)
                {
                    _logger.LogInformation("Auto-reconnect successful");
                }
                else;
                {
                    _logger.LogWarning("Auto-reconnect failed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during auto-reconnect attempt");
            }
        }

        /// <summary>
        /// Cihaz sağlığını al;
        /// </summary>
        private async Task<DeviceHealth> GetDeviceHealthAsync()
        {
            try
            {
                await Task.Delay(10);

                // Simüle edilmiş sağlık durumu;
                var healthScore = CalculateHealthScore();
                var temp = GetDeviceTemperature();

                var status = DeviceHealthStatus.Healthy;
                var message = "Device healthy";

                if (healthScore < 50)
                {
                    status = DeviceHealthStatus.Critical;
                    message = $"Critical health score: {healthScore}";
                }
                else if (healthScore < 80)
                {
                    status = DeviceHealthStatus.Warning;
                    message = $"Warning health score: {healthScore}";
                }

                if (temp > 70)
                {
                    status = DeviceHealthStatus.Critical;
                    message = $"Critical temperature: {temp}°C";
                }
                else if (temp > 60)
                {
                    status = status == DeviceHealthStatus.Healthy ? DeviceHealthStatus.Warning : status;
                    message = $"High temperature: {temp}°C";
                }

                return new DeviceHealth;
                {
                    Status = status,
                    Message = message,
                    HealthScore = healthScore,
                    Temperature = temp,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch
            {
                return new DeviceHealth;
                {
                    Status = DeviceHealthStatus.Unknown,
                    Message = "Health check failed",
                    HealthScore = 0,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// PIN durumunu al;
        /// </summary>
        private async Task<PinStatus> GetPinStatusAsync()
        {
            await Task.Delay(10);

            // Simüle edilmiş PIN durumu;
            return new PinStatus;
            {
                IsLocked = false,
                RemainingAttempts = 3,
                MaxAttempts = 3,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Müdahale durumunu kontrol et;
        /// </summary>
        private async Task<TamperStatus> CheckTamperStatusAsync()
        {
            await Task.Delay(10);

            // Simüle edilmiş müdahale durumu;
            return new TamperStatus;
            {
                IsTampered = false,
                SensorId = "N/A",
                Details = "No tamper detected",
                Confidence = 0.0,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Müdahale tespit edildiğinde işle;
        /// </summary>
        private async Task HandleTamperDetectedAsync(TamperStatus tamperStatus)
        {
            try
            {
                _logger.LogCritical("Handling device tamper detection");

                // Acil durum prosedürleri;
                // 1. Tüm hassas verileri temizle;
                await ClearSensitiveDataAsync();

                // 2. Cihazı kilitle;
                await _hardwareLock.EmergencyLockAsync("Crypto device tamper detected");

                // 3. Güvenlik bildirimi gönder;
                await NotifyTamperDetectedAsync(tamperStatus);

                // 4. Cihazı devre dışı bırak;
                await SecureDisconnectAsync();

                var tamperEvent = new DeviceEvent;
                {
                    Id = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.TamperEmergency,
                    Operation = "TamperResponse",
                    Details = "Emergency procedures executed due to tamper detection",
                    Success = true,
                    IsSecurityCritical = true;
                };

                AddDeviceEvent(tamperEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling tamper detection");
            }
        }

        /// <summary>
        /// Hassas verileri temizle;
        /// </summary>
        private async Task ClearSensitiveDataAsync()
        {
            try
            {
                _logger.LogCritical("Clearing sensitive data from crypto device");

                // Bellekteki hassas verileri temizle;
                if (_pinnedKeyHandle.IsAllocated)
                {
                    // Pinned veriyi sıfırla;
                    var array = _pinnedKeyHandle.Target as byte[];
                    if (array != null)
                    {
                        Array.Clear(array, 0, array.Length);
                    }
                    _pinnedKeyHandle.Free();
                }

                // Diğer temizleme işlemleri;
                await Task.Delay(50);

                _logger.LogCritical("Sensitive data cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing sensitive data");
            }
        }

        #endregion;

        #region Bildirim Metodları;

        /// <summary>
        /// Cihaz event'ini yayınla;
        /// </summary>
        private async Task PublishDeviceEventAsync(DeviceEvent deviceEvent)
        {
            try
            {
                var eventMessage = new CryptoDeviceEventMessage;
                {
                    EventId = deviceEvent.Id,
                    Timestamp = deviceEvent.Timestamp,
                    EventType = deviceEvent.EventType.ToString(),
                    Operation = deviceEvent.Operation,
                    KeyId = deviceEvent.KeyId,
                    Algorithm = deviceEvent.Algorithm,
                    Success = deviceEvent.Success,
                    Details = deviceEvent.Details,
                    IsSecurityCritical = deviceEvent.IsSecurityCritical;
                };

                await _eventBus.PublishAsync(eventMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing device event");
            }
        }

        /// <summary>
        /// Metrikleri yayınla;
        /// </summary>
        private async Task PublishMetricsAsync(DeviceMetrics metrics)
        {
            try
            {
                await _eventBus.PublishAsync(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing metrics");
            }
        }

        /// <summary>
        /// Kritik sağlık bildirimi;
        /// </summary>
        private async Task NotifyCriticalHealthAsync(DeviceHealth health)
        {
            try
            {
                var healthAlert = new DeviceHealthAlert;
                {
                    Timestamp = DateTime.UtcNow,
                    DeviceId = "CRYPTO_DEVICE_001",
                    HealthStatus = health.Status.ToString(),
                    Message = health.Message,
                    HealthScore = health.HealthScore,
                    Temperature = health.Temperature,
                    RequiresAttention = health.Status == DeviceHealthStatus.Critical;
                };

                await _eventBus.PublishAsync(healthAlert);
                _logger.LogWarning("Critical health alert sent: {Message}", health.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending health alert");
            }
        }

        /// <summary>
        /// Müdahale bildirimi;
        /// </summary>
        private async Task NotifyTamperDetectedAsync(TamperStatus tamperStatus)
        {
            try
            {
                var tamperAlert = new DeviceTamperAlert;
                {
                    Timestamp = DateTime.UtcNow,
                    DeviceId = "CRYPTO_DEVICE_001",
                    SensorId = tamperStatus.SensorId,
                    Details = tamperStatus.Details,
                    Confidence = tamperStatus.Confidence,
                    IsCritical = true,
                    RequiresImmediateAction = true;
                };

                await _eventBus.PublishAsync(tamperAlert);
                _logger.LogCritical("Tamper alert sent: {Details}", tamperStatus.Details);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending tamper alert");
            }
        }

        /// <summary>
        /// Fabrika sıfırlama bildirimi;
        /// </summary>
        private async Task NotifyFactoryResetAsync()
        {
            try
            {
                var factoryResetAlert = new FactoryResetAlert;
                {
                    Timestamp = DateTime.UtcNow,
                    DeviceId = "CRYPTO_DEVICE_001",
                    Message = "CRYPTO DEVICE FACTORY RESET EXECUTED - ALL KEYS ERASED",
                    IsCritical = true,
                    RequiresVerification = true;
                };

                await _eventBus.PublishAsync(factoryResetAlert);
                _logger.LogCritical("Factory reset alert sent");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending factory reset alert");
            }
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// Cihaz durumu değişti event handler;
        /// </summary>
        private void OnDeviceStatusChanged(DeviceStatusChangedEventArgs e)
        {
            try
            {
                DeviceStatusChanged?.Invoke(this, e);
                LogDeviceState("Status_Changed",
                    $"Device status changed: Connected={e.IsConnected}, Initialized={e.IsInitialized}",
                    e.IsConnected ? DeviceState.Connected : DeviceState.Disconnected);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in device status changed event");
            }
        }

        /// <summary>
        /// Kriptografi operasyonu gerçekleştirildi event handler;
        /// </summary>
        private void OnCryptoOperationPerformed(CryptoOperationEventArgs e)
        {
            try
            {
                CryptoOperationPerformed?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in crypto operation event");
            }
        }

        /// <summary>
        /// Anahtar operasyonu gerçekleştirildi event handler;
        /// </summary>
        private void OnKeyOperationPerformed(KeyOperationEventArgs e)
        {
            try
            {
                KeyOperationPerformed?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in key operation event");
            }
        }

        /// <summary>
        /// Sağlık kontrol timer event handler;
        /// </summary>
        private void OnHealthCheck(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // Basit sağlık logu;
                if (_isConnected)
                {
                    _logger.LogDebug("Crypto device health check - Device is connected");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health check timer");
            }
        }

        private void OnSecurityBreachDetected(object sender, SecurityBreachEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                _logger.LogWarning("Security breach detected, securing crypto device");

                // Güvenlik ihlalinde cihazı güvenli moda al;
                await SecureDisconnectAsync();

                var securityEvent = new DeviceEvent;
                {
                    Id = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.SecurityBreach,
                    Operation = "SecurityResponse",
                    Details = $"Security breach: {e.BreachType}",
                    Success = false,
                    IsSecurityCritical = true;
                };

                AddDeviceEvent(securityEvent);
            });
        }

        private void OnTamperAlert(object sender, TamperAlertEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                _logger.LogWarning("Tamper alert received: {AlertType}", e.AlertType);

                var tamperEvent = new DeviceEvent;
                {
                    Id = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = DeviceEventType.TamperAlert,
                    Operation = "TamperAlert",
                    Details = $"Tamper alert: {e.AlertType}",
                    Success = false,
                    IsSecurityCritical = true;
                };

                AddDeviceEvent(tamperEvent);

                // Kritikse acil önlem al;
                if (e.AlertType.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleTamperDetectedAsync(new TamperStatus;
                    {
                        IsTampered = true,
                        SensorId = "EXTERNAL",
                        Details = e.Details,
                        Confidence = 1.0,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            });
        }

        private void OnHardwareLockStatusChanged(object sender, LockStatusChangedEventArgs e)
        {
            // Donanım kilidi durumu değiştiğinde;
            if (e.IsLocked && _isConnected)
            {
                _logger.LogInformation("Hardware lock engaged, securing crypto device");
                _ = Task.Run(async () => await SecureDisconnectAsync());
            }
        }

        #endregion;

        #endregion;
    }

    /// <summary>
    /// Kriptografi cihazı konfigürasyonu;
    /// </summary>
    public class CryptoDeviceConfig;
    {
        public bool SimulateHardware { get; set; } = true;
        public bool UseHardwareAcceleration { get; set; } = true;
        public bool UseHardwareRNG { get; set; } = true;
        public bool UseHardwareKeyGeneration { get; set; } = true;

        public string PreferredDeviceId { get; set; } = "SIM_CRYPTO_001";
        public string DefaultPin { get; set; } = "123456";
        public string KeyDerivationSalt { get; set; } = "NEDA_CRYPTO_SALT_2024";

        public int MonitoringIntervalMs { get; set; } = 5000;
        public int HealthCheckIntervalMs { get; set; } = 30000;
        public int SimulatedOperationDelayMs { get; set; } = 50;

        public bool AutoReconnect { get; set; } = true;
        public int ReconnectDelaySeconds { get; set; } = 10;

        public int MaxDataSize { get; set; } = 64 * 1024; // 64KB;
        public int MaxRandomLength { get; set; } = 1024 * 1024; // 1MB;
        public int ExpectedMaxOperationMs { get; set; } = 1000;

        public bool AutoStoreGeneratedKeys { get; set; } = true;
        public bool AllowFactoryReset { get; set; } = false;
        public int MinPinLength { get; set; } = 6;

        public int MaxStoredEvents { get; set; } = 10000;
        public int EventRetentionDays { get; set; } = 90;
        public int EventRetentionMinutes { get; set; } = 1440; // 24 saat;
        public int EventRetentionCount { get; set; } = 1000;
    }

    /// <summary>
    /// Cihaz durumları;
    /// </summary>
    public enum DeviceState;
    {
        Unknown = 0,
        Disconnected = 1,
        Connected = 2,
        Initializing = 3,
        Initialized = 4,
        Processing = 5,
        Disconnecting = 6,
        Error = 99;
    }

    /// <summary>
    /// Cihaz event tipleri;
    /// </summary>
    public enum DeviceEventType;
    {
        Unknown = 0,
        ConnectionEstablished = 1,
        ConnectionLost = 2,
        Initialized = 3,
        Encryption = 4,
        Decryption = 5,
        Signing = 6,
        Verification = 7,
        RandomGeneration = 8,
        KeyGeneration = 9,
        KeyDeletion = 10,
        PinChange = 11,
        FactoryReset = 12,
        HealthWarning = 13,
        SecurityWarning = 14,
        TamperDetected = 15,
        TamperAlert = 16,
        SecurityBreach = 17,
        TamperEmergency = 18,
        OperationError = 19;
    }

    /// <summary>
    /// Cihaz sağlık durumları;
    /// </summary>
    public enum DeviceHealthStatus;
    {
        Unknown = 0,
        Healthy = 1,
        Warning = 2,
        Critical = 3;
    }

    /// <summary>
    /// Kriptografi algoritmaları;
    /// </summary>
    public enum CryptoAlgorithm;
    {
        AES128_CBC = 1,
        AES192_CBC = 2,
        AES256_CBC = 3,
        AES128_GCM = 4,
        AES256_GCM = 5,
        RSA1024 = 6,
        RSA2048 = 7,
        RSA4096 = 8;
    }

    /// <summary>
    /// İmza algoritmaları;
    /// </summary>
    public enum SignAlgorithm;
    {
        RSA_PKCS1_SHA256 = 1,
        RSA_PSS_SHA256 = 2,
        RSA_PSS_SHA384 = 3,
        RSA_PSS_SHA512 = 4,
        ECDSA_SHA256 = 5,
        ECDSA_SHA384 = 6,
        ECDSA_SHA512 = 7;
    }

    /// <summary>
    /// Anahtar algoritmaları;
    /// </summary>
    public enum KeyAlgorithm;
    {
        RSA_1024 = 1,
        RSA_2048 = 2,
        RSA_4096 = 3,
        ECC_256 = 4,
        ECC_384 = 5,
        ECC_521 = 6;
    }

    /// <summary>
    /// Anahtar kullanım tipleri;
    /// </summary>
    public enum KeyUsage;
    {
        Sign = 1,
        Encrypt = 2,
        SignAndEncrypt = 3,
        Derive = 4;
    }

    /// <summary>
    /// Kriptografi operasyon tipleri;
    /// </summary>
    public enum CryptoOperationType;
    {
        Unknown = 0,
        Encrypt = 1,
        Decrypt = 2,
        Sign = 3,
        Verify = 4,
        GenerateRandom = 5;
    }

    /// <summary>
    /// Anahtar operasyon tipleri;
    /// </summary>
    public enum KeyOperationType;
    {
        Unknown = 0,
        Generate = 1,
        Import = 2,
        Export = 3,
        Delete = 4,
        Backup = 5,
        Restore = 6;
    }

    /// <summary>
    /// Tanılama durumları;
    /// </summary>
    public enum DiagnosticsStatus;
    {
        Unknown = 0,
        Passed = 1,
        Failed = 2,
        Warning = 3,
        Error = 4;
    }

    /// <summary>
    /// Cihaz event'i;
    /// </summary>
    public class DeviceEvent;
    {
        public Guid Id { get; set; }
        public DateTime Timestamp { get; set; }
        public DeviceEventType EventType { get; set; }
        public string Operation { get; set; }
        public string KeyId { get; set; }
        public string Algorithm { get; set; }
        public int InputSize { get; set; }
        public int OutputSize { get; set; }
        public int SignatureSize { get; set; }
        public int KeySize { get; set; }
        public long DurationMs { get; set; }
        public string Details { get; set; }
        public bool Success { get; set; }
        public bool IsSecurityCritical { get; set; }
    }

    /// <summary>
    /// Cihaz durumu;
    /// </summary>
    public class DeviceStatus;
    {
        public bool IsConnected { get; set; }
        public bool IsInitialized { get; set; }
        public DateTime? LastConnectionTime { get; set; }
        public int OperationCount { get; set; }
        public IntPtr DeviceHandle { get; set; }
        public List<DeviceEvent> RecentEvents { get; set; }
        public double HealthScore { get; set; }
        public long MemoryUsage { get; set; }
        public double Temperature { get; set; }
    }

    /// <summary>
    /// Cihaz durumu değişti event args;
    /// </summary>
    public class DeviceStatusChangedEventArgs : EventArgs;
    {
        public bool IsConnected { get; set; }
        public bool IsInitialized { get; set; }
        public DateTime ChangedAt { get; set; }
        public string Reason { get; set; }
        public DeviceInfo DeviceInfo { get; set; }
    }

    /// <summary>
    /// Kriptografi operasyonu event args;
    /// </summary>
    public class CryptoOperationEventArgs : EventArgs;
    {
        public Guid OperationId { get; set; }
        public CryptoOperationType OperationType { get; set; }
        public CryptoAlgorithm Algorithm { get; set; }
        public string KeyId { get; set; }
        public int InputSize { get; set; }
        public int OutputSize { get; set; }
        public long DurationMs { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Anahtar operasyonu event args;
    /// </summary>
    public class KeyOperationEventArgs : EventArgs;
    {
        public Guid OperationId { get; set; }
        public KeyOperationType OperationType { get; set; }
        public string KeyId { get; set; }
        public KeyAlgorithm Algorithm { get; set; }
        public int KeySize { get; set; }
        public long DurationMs { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Cihaz bilgileri;
    /// </summary>
    public class DeviceInfo;
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string FirmwareVersion { get; set; }
        public int MaxKeySlots { get; set; }
        public List<string> SupportedAlgorithms { get; set; }
        public bool IsFips140Compliant { get; set; }
        public bool IsConnected { get; set; }
    }

    /// <summary>
    /// Bağlantı sonucu;
    /// </summary>
    public class ConnectResult;
    {
        public bool Success { get; set; }
        public IntPtr DeviceHandle { get; set; }
        public uint Protocol { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Başlatma sonucu;
    /// </summary>
    public class InitResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Bağlantı kesme sonucu;
    /// </summary>
    public class DisconnectResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Anahtar çifti;
    /// </summary>
    public class KeyPair;
    {
        public string KeyId { get; set; }
        public KeyAlgorithm Algorithm { get; set; }
        public byte[] PublicKey { get; set; }
        public byte[] PrivateKey { get; set; }
        public DateTime CreatedAt { get; set; }
        public KeyUsage Usage { get; set; }
        public bool IsHardwareProtected { get; set; }
    }

    /// <summary>
    /// Cihaz sağlığı;
    /// </summary>
    public class DeviceHealth;
    {
        public DateTime Timestamp { get; set; }
        public DeviceHealthStatus Status { get; set; }
        public string Message { get; set; }
        public double HealthScore { get; set; }
        public double Temperature { get; set; }
    }

    /// <summary>
    /// PIN durumu;
    /// </summary>
    public class PinStatus;
    {
        public DateTime Timestamp { get; set; }
        public bool IsLocked { get; set; }
        public int RemainingAttempts { get; set; }
        public int MaxAttempts { get; set; }
    }

    /// <summary>
    /// Müdahale durumu;
    /// </summary>
    public class TamperStatus;
    {
        public DateTime Timestamp { get; set; }
        public bool IsTampered { get; set; }
        public string SensorId { get; set; }
        public string Details { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Cihaz metrikleri;
    /// </summary>
    public class DeviceMetrics : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public int OperationCount { get; set; }
        public TimeSpan Uptime { get; set; }
        public long MemoryUsage { get; set; }
        public double Temperature { get; set; }
        public double PerformanceScore { get; set; }
    }

    /// <summary>
    /// Cihaz tanılama;
    /// </summary>
    public class DeviceDiagnostics;
    {
        public DateTime Timestamp { get; set; }
        public Guid TestId { get; set; }
        public DiagnosticsStatus OverallStatus { get; set; }
        public string Message { get; set; }
        public DeviceTestResult ConnectionTest { get; set; }
        public DeviceTestResult PinTest { get; set; }
        public DeviceTestResult RngTest { get; set; }
        public DeviceTestResult EncryptionTest { get; set; }
        public DeviceTestResult SignatureTest { get; set; }
        public DeviceTestResult KeyGenTest { get; set; }
        public List<DeviceTest> Tests { get; set; } = new List<DeviceTest>();
        public DeviceStatus DeviceStatus { get; set; }
    }

    /// <summary>
    /// Cihaz testi;
    /// </summary>
    public class DeviceTest;
    {
        public string TestName { get; set; }
        public bool Success { get; set; }
        public string Details { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Cihaz test sonucu;
    /// </summary>
    public class DeviceTestResult;
    {
        public bool Success { get; set; }
        public string Details { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Kriptografi cihazı interface'i;
    /// </summary>
    public interface ICryptoDevice : IDisposable
    {
        event EventHandler<DeviceStatusChangedEventArgs> DeviceStatusChanged;
        event EventHandler<CryptoOperationEventArgs> CryptoOperationPerformed;
        event EventHandler<KeyOperationEventArgs> KeyOperationPerformed;

        void StartDevice();
        Task StopDeviceAsync();
        Task<bool> ConnectAndInitializeAsync();
        Task<bool> DisconnectAsync();

        Task<byte[]> EncryptAsync(byte[] plaintext, string keyId, CryptoAlgorithm algorithm = CryptoAlgorithm.AES256_CBC);
        Task<byte[]> DecryptAsync(byte[] ciphertext, string keyId, CryptoAlgorithm algorithm = CryptoAlgorithm.AES256_CBC);
        Task<byte[]> SignAsync(byte[] data, string keyId, SignAlgorithm algorithm = SignAlgorithm.RSA_PSS_SHA256);
        Task<bool> VerifyAsync(byte[] data, byte[] signature, string keyId, SignAlgorithm algorithm = SignAlgorithm.RSA_PSS_SHA256);
        Task<byte[]> GenerateRandomAsync(int length);

        Task<KeyPair> GenerateKeyPairAsync(KeyAlgorithm algorithm = KeyAlgorithm.RSA_2048, string keyId = null, KeyUsage usage = KeyUsage.SignAndEncrypt);
        Task<bool> DeleteKeyAsync(string keyId, bool force = false);
        Task<bool> ChangePinAsync(string oldPin, string newPin);
        Task<bool> FactoryResetAsync(string adminPin);

        DeviceStatus GetDeviceStatus();
        Task<DeviceInfo> GetDeviceInfoAsync();
        Task<DeviceDiagnostics> RunDiagnosticsAsync();
        void ClearDeviceHistory();
    }

    #region Event Sınıfları;

    /// <summary>
    /// Kriptografi cihazı event mesajı;
    /// </summary>
    public class CryptoDeviceEventMessage : IEvent;
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; }
        public string Operation { get; set; }
        public string KeyId { get; set; }
        public string Algorithm { get; set; }
        public bool Success { get; set; }
        public string Details { get; set; }
        public bool IsSecurityCritical { get; set; }
    }

    /// <summary>
    /// Cihaz sağlık alarmı;
    /// </summary>
    public class DeviceHealthAlert : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public string DeviceId { get; set; }
        public string HealthStatus { get; set; }
        public string Message { get; set; }
        public double HealthScore { get; set; }
        public double Temperature { get; set; }
        public bool RequiresAttention { get; set; }
    }

    /// <summary>
    /// Cihaz müdahale alarmı;
    /// </summary>
    public class DeviceTamperAlert : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public string DeviceId { get; set; }
        public string SensorId { get; set; }
        public string Details { get; set; }
        public double Confidence { get; set; }
        public bool IsCritical { get; set; }
        public bool RequiresImmediateAction { get; set; }
    }

    /// <summary>
    /// Fabrika sıfırlama alarmı;
    /// </summary>
    public class FactoryResetAlert : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public string DeviceId { get; set; }
        public string Message { get; set; }
        public bool IsCritical { get; set; }
        public bool RequiresVerification { get; set; }
    }

    #endregion;
}
