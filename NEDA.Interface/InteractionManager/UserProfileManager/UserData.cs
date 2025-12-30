using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Common.Constants;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Logging;
using NEDA.Security.Encryption;
using NEDA.SecurityModules.Authentication;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Threading.Tasks;

namespace NEDA.Interface.InteractionManager.UserProfileManager;
{
    /// <summary>
    /// Kullanıcı veri modeli - Güvenli ve şifreli kullanıcı veri depolama;
    /// </summary>
    [Serializable]
    [DataContract(Name = "UserData", Namespace = "NEDA.UserProfile")]
    public sealed class UserData : INotifyPropertyChanged, IDisposable;
    {
        #region Private Fields;

        private static readonly ILogger _logger = LogManager.GetLogger(typeof(UserData));
        private readonly ICryptoEngine _cryptoEngine;
        private readonly object _syncLock = new object();
        private bool _isDisposed;

        // Şifreli depolama;
        private Dictionary<string, byte[]> _encryptedPreferences;
        private Dictionary<string, byte[]> _encryptedHistory;
        private Dictionary<string, byte[]> _encryptedSensitiveData;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Yeni UserData örneği oluşturur;
        /// </summary>
        /// <param name="cryptoEngine">Şifreleme motoru</param>
        /// <exception cref="ArgumentNullException">cryptoEngine null ise</exception>
        public UserData(ICryptoEngine cryptoEngine)
        {
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));

            InitializeDataStructures();
            InitializeDefaultPreferences();

            _logger.Info("UserData instance created", new { UserId, CreatedAt });
        }

        /// <summary>
        /// JSON serileştirme için constructor;
        /// </summary>
        [JsonConstructor]
        private UserData()
        {
            // JSON serileştirme için;
        }

        #endregion;

        #region Core Properties;

        /// <summary>
        /// Benzersiz kullanıcı kimliği;
        /// </summary>
        [DataMember(Order = 1)]
        [JsonProperty("userId")]
        public string UserId { get; private set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Kullanıcı adı;
        /// </summary>
        [DataMember(Order = 2)]
        [JsonProperty("username")]
        public string Username { get; set; }

        /// <summary>
        /// Kullanıcı tam adı;
        /// </summary>
        [DataMember(Order = 3)]
        [JsonProperty("fullName")]
        public string FullName { get; set; }

        /// <summary>
        /// E-posta adresi (şifreli)
        /// </summary>
        [DataMember(Order = 4)]
        [JsonProperty("encryptedEmail")]
        public byte[] EncryptedEmail { get; private set; }

        /// <summary>
        /// Kullanıcı rolü;
        /// </summary>
        [DataMember(Order = 5)]
        [JsonProperty("userRole")]
        public UserRole UserRole { get; set; } = UserRole.Standard;

        /// <summary>
        /// Kullanıcı durumu;
        /// </summary>
        [DataMember(Order = 6)]
        [JsonProperty("userStatus")]
        public UserStatus UserStatus { get; set; } = UserStatus.Active;

        /// <summary>
        /// Son oturum açma tarihi;
        /// </summary>
        [DataMember(Order = 7)]
        [JsonProperty("lastLogin")]
        public DateTime? LastLogin { get; set; }

        /// <summary>
        /// Toplam oturum süresi (saniye)
        /// </summary>
        [DataMember(Order = 8)]
        [JsonProperty("totalSessionTime")]
        public long TotalSessionTime { get; set; }

        /// <summary>
        Oluşturulma tarihi;
        /// </summary>
        [DataMember(Order = 9)]
        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// Son güncellenme tarihi;
        /// </summary>
        [DataMember(Order = 10)]
        [JsonProperty("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// Dil tercihi;
        /// </summary>
        [DataMember(Order = 11)]
        [JsonProperty("language")]
        public string Language { get; set; } = "tr-TR";

        /// <summary>
        /// Tema tercihi;
        /// </summary>
        [DataMember(Order = 12)]
        [JsonProperty("theme")]
        public string Theme { get; set; } = "Dark";

        /// <summary>
        /// Zaman dilimi;
        /// </summary>
        [DataMember(Order = 13)]
        [JsonProperty("timeZone")]
        public string TimeZone { get; set; } = "Turkey Standard Time";

        #endregion;

        #region Preference Properties;

        /// <summary>
        /// Arayüz tercihleri;
        /// </summary>
        [DataMember(Order = 20)]
        [JsonProperty("uiPreferences")]
        public Dictionary<string, object> UIPreferences { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Bildirim tercihleri;
        /// </summary>
        [DataMember(Order = 21)]
        [JsonProperty("notificationPreferences")]
        public NotificationPreferences NotificationPrefs { get; set; } = new NotificationPreferences();

        /// <summary>
        /// Ses ve konuşma tercihleri;
        /// </summary>
        [DataMember(Order = 22)]
        [JsonProperty("voicePreferences")]
        public VoicePreferences VoicePrefs { get; set; } = new VoicePreferences();

        /// <summary>
        /// Gizlilik tercihleri;
        /// </summary>
        [DataMember(Order = 23)]
        [JsonProperty("privacyPreferences")]
        public PrivacyPreferences PrivacyPrefs { get; set; } = new PrivacyPreferences();

        /// <summary>
        /// Performans tercihleri;
        /// </summary>
        [DataMember(Order = 24)]
        [JsonProperty("performancePreferences")]
        public PerformancePreferences PerformancePrefs { get; set; } = new PerformancePreferences();

        #endregion;

        #region Statistics Properties;

        /// <summary>
        /// Toplam etkileşim sayısı;
        /// </summary>
        [DataMember(Order = 30)]
        [JsonProperty("totalInteractions")]
        public int TotalInteractions { get; set; }

        /// <summary>
        /// Başarılı komut sayısı;
        /// </summary>
        [DataMember(Order = 31)]
        [JsonProperty("successfulCommands")]
        public int SuccessfulCommands { get; set; }

        /// <summary>
        /// Öğrenme puanı;
        /// </summary>
        [DataMember(Order = 32)]
        [JsonProperty("learningScore")]
        public double LearningScore { get; set; }

        /// <summary>
        /// Uyum puanı;
        /// </summary>
        [DataMember(Order = 33)]
        [JsonProperty("adaptationScore")]
        public double AdaptationScore { get; set; }

        /// <summary>
        /// Güven puanı;
        /// </summary>
        [DataMember(Order = 34)]
        [JsonProperty("trustScore")]
        public double TrustScore { get; set; }

        #endregion;

        #region Security Properties;

        /// <summary>
        /// Veri bütünlük kontrolü için hash;
        /// </summary>
        [DataMember(Order = 40)]
        [JsonProperty("integrityHash")]
        public string IntegrityHash { get; private set; }

        /// <summary>
        /// Veri şifreleme anahtarı (şifreli)
        /// </summary>
        [DataMember(Order = 41)]
        [JsonProperty("encryptedDataKey")]
        public byte[] EncryptedDataKey { get; private set; }

        /// <summary>
        /// Şifreleme başlatma vektörü;
        /// </summary>
        [DataMember(Order = 42)]
        [JsonProperty("encryptionIV")]
        public byte[] EncryptionIV { get; private set; }

        /// <summary>
        /// Veri imzası;
        /// </summary>
        [DataMember(Order = 43)]
        [JsonProperty("dataSignature")]
        public byte[] DataSignature { get; private set; }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// E-posta adresini güvenli şekilde ayarlar;
        /// </summary>
        /// <param name="email">E-posta adresi</param>
        /// <param name="encryptionKey">Şifreleme anahtarı</param>
        public void SetEmail(string email, SecureString encryptionKey)
        {
            ValidateEncryptionKey(encryptionKey);

            try
            {
                var emailBytes = System.Text.Encoding.UTF8.GetBytes(email);
                EncryptedEmail = _cryptoEngine.Encrypt(emailBytes, encryptionKey);

                OnPropertyChanged(nameof(EncryptedEmail));
                UpdatedAt = DateTime.UtcNow;

                _logger.Debug("Email encrypted and stored", new { UserId });
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to encrypt email", ex);
                throw new SecurityException("Email encryption failed", ex);
            }
        }

        /// <summary>
        /// E-posta adresini çözümler;
        /// </summary>
        /// <param name="encryptionKey">Şifreleme anahtarı</param>
        /// <returns>Çözümlenmiş e-posta adresi</returns>
        public string GetEmail(SecureString encryptionKey)
        {
            ValidateEncryptionKey(encryptionKey);

            if (EncryptedEmail == null || EncryptedEmail.Length == 0)
                return string.Empty;

            try
            {
                var decryptedBytes = _cryptoEngine.Decrypt(EncryptedEmail, encryptionKey);
                return System.Text.Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to decrypt email", ex);
                throw new SecurityException("Email decryption failed", ex);
            }
        }

        /// <summary>
        /// Hassas veri ekler;
        /// </summary>
        /// <param name="key">Veri anahtarı</param>
        /// <param name="value">Veri değeri</param>
        /// <param name="encryptionKey">Şifreleme anahtarı</param>
        public void AddSensitiveData(string key, string value, SecureString encryptionKey)
        {
            ValidateKey(key);
            ValidateEncryptionKey(encryptionKey);

            lock (_syncLock)
            {
                try
                {
                    var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
                    var encryptedValue = _cryptoEngine.Encrypt(valueBytes, encryptionKey);

                    _encryptedSensitiveData[key] = encryptedValue;
                    UpdatedAt = DateTime.UtcNow;

                    _logger.Debug("Sensitive data added", new { UserId, Key = key, Length = value.Length });
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to encrypt sensitive data", ex);
                    throw new SecurityException("Sensitive data encryption failed", ex);
                }
            }
        }

        /// <summary>
        /// Hassas veri alır;
        /// </summary>
        /// <param name="key">Veri anahtarı</param>
        /// <param name="encryptionKey">Şifreleme anahtarı</param>
        /// <returns>Çözümlenmiş veri</returns>
        public string GetSensitiveData(string key, SecureString encryptionKey)
        {
            ValidateKey(key);
            ValidateEncryptionKey(encryptionKey);

            lock (_syncLock)
            {
                if (!_encryptedSensitiveData.ContainsKey(key))
                    return null;

                try
                {
                    var encryptedValue = _encryptedSensitiveData[key];
                    var decryptedBytes = _cryptoEngine.Decrypt(encryptedValue, encryptionKey);
                    return System.Text.Encoding.UTF8.GetString(decryptedBytes);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to decrypt sensitive data", ex);
                    throw new SecurityException("Sensitive data decryption failed", ex);
                }
            }
        }

        /// <summary>
        /// Tercih ekler veya günceller;
        /// </summary>
        /// <param name="key">Tercih anahtarı</param>
        /// <param name="value">Tercih değeri</param>
        public void SetPreference(string key, object value)
        {
            ValidateKey(key);

            lock (_syncLock)
            {
                UIPreferences[key] = value;
                UpdatedAt = DateTime.UtcNow;

                OnPropertyChanged(nameof(UIPreferences));

                _logger.Debug("Preference updated", new { UserId, Key = key, Value = value });
            }
        }

        /// <summary>
        /// Tercih alır;
        /// </summary>
        /// <typeparam name="T">Beklenen tür</typeparam>
        /// <param name="key">Tercih anahtarı</param>
        /// <param name="defaultValue">Varsayılan değer</param>
        /// <returns>Tercih değeri</returns>
        public T GetPreference<T>(string key, T defaultValue = default)
        {
            ValidateKey(key);

            lock (_syncLock)
            {
                if (!UIPreferences.ContainsKey(key))
                    return defaultValue;

                try
                {
                    return (T)Convert.ChangeType(UIPreferences[key], typeof(T));
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to convert preference value for key: {key}", ex);
                    return defaultValue;
                }
            }
        }

        /// <summary>
        /// Etkileşim geçmişi ekler;
        /// </summary>
        /// <param name="interaction">Etkileşim verisi</param>
        /// <param name="encryptionKey">Şifreleme anahtarı</param>
        public void AddInteractionHistory(InteractionData interaction, SecureString encryptionKey)
        {
            if (interaction == null)
                throw new ArgumentNullException(nameof(interaction));

            ValidateEncryptionKey(encryptionKey);

            lock (_syncLock)
            {
                try
                {
                    var interactionJson = JsonConvert.SerializeObject(interaction);
                    var interactionBytes = System.Text.Encoding.UTF8.GetBytes(interactionJson);
                    var encryptedData = _cryptoEngine.Encrypt(interactionBytes, encryptionKey);

                    var timestamp = DateTime.UtcNow.Ticks.ToString();
                    _encryptedHistory[timestamp] = encryptedData;

                    TotalInteractions++;
                    UpdatedAt = DateTime.UtcNow;

                    _logger.Debug("Interaction history added", new { UserId, InteractionType = interaction.Type });
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to add interaction history", ex);
                    throw new DataStorageException("Interaction history storage failed", ex);
                }
            }
        }

        /// <summary>
        /// Kullanıcı istatistiklerini günceller;
        /// </summary>
        /// <param name="commandSuccessful">Komut başarılı mı?</param>
        public void UpdateStatistics(bool commandSuccessful)
        {
            lock (_syncLock)
            {
                if (commandSuccessful)
                {
                    SuccessfulCommands++;
                    TrustScore = Math.Min(100, TrustScore + 0.5);
                    LearningScore = Math.Min(100, LearningScore + 0.3);
                }
                else;
                {
                    TrustScore = Math.Max(0, TrustScore - 0.2);
                }

                AdaptationScore = CalculateAdaptationScore();
                UpdatedAt = DateTime.UtcNow;

                _logger.Debug("Statistics updated", new;
                {
                    UserId,
                    SuccessfulCommands,
                    TrustScore,
                    LearningScore;
                });
            }
        }

        /// <summary>
        /// Veri bütünlüğünü doğrular;
        /// </summary>
        /// <returns>Bütünlük doğrulaması başarılı mı?</returns>
        public bool VerifyIntegrity()
        {
            try
            {
                var currentHash = CalculateDataHash();
                return string.Equals(currentHash, IntegrityHash, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.Error("Integrity verification failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Verileri şifreler ve bütünlük hash'i oluşturur;
        /// </summary>
        /// <param name="encryptionKey">Şifreleme anahtarı</param>
        public async Task SecureDataAsync(SecureString encryptionKey)
        {
            ValidateEncryptionKey(encryptionKey);

            try
            {
                // Şifreleme anahtarı oluştur;
                var dataKey = _cryptoEngine.GenerateKey();
                EncryptedDataKey = _cryptoEngine.Encrypt(dataKey, encryptionKey);
                EncryptionIV = _cryptoEngine.GenerateIV();

                // Veri bütünlük hash'i oluştur;
                IntegrityHash = CalculateDataHash();

                // Veri imzası oluştur;
                var dataToSign = System.Text.Encoding.UTF8.GetBytes(UserId + IntegrityHash);
                DataSignature = await _cryptoEngine.SignDataAsync(dataToSign, encryptionKey);

                UpdatedAt = DateTime.UtcNow;

                _logger.Info("Data secured successfully", new { UserId });
            }
            catch (Exception ex)
            {
                _logger.Error("Data security operation failed", ex);
                throw new SecurityException("Data security operation failed", ex);
            }
        }

        /// <summary>
        /// Kullanıcı verilerini temizler;
        /// </summary>
        public void ClearSensitiveData()
        {
            lock (_syncLock)
            {
                _encryptedSensitiveData.Clear();
                _encryptedHistory.Clear();
                EncryptedEmail = null;

                // Güvenli bellek temizleme;
                if (EncryptedDataKey != null)
                {
                    Array.Clear(EncryptedDataKey, 0, EncryptedDataKey.Length);
                    EncryptedDataKey = null;
                }

                if (EncryptionIV != null)
                {
                    Array.Clear(EncryptionIV, 0, EncryptionIV.Length);
                    EncryptionIV = null;
                }

                UpdatedAt = DateTime.UtcNow;

                _logger.Info("Sensitive data cleared", new { UserId });
            }
        }

        #endregion;

        #region Private Methods;

        private void InitializeDataStructures()
        {
            _encryptedPreferences = new Dictionary<string, byte[]>();
            _encryptedHistory = new Dictionary<string, byte[]>();
            _encryptedSensitiveData = new Dictionary<string, byte[]>();
        }

        private void InitializeDefaultPreferences()
        {
            // Varsayılan arayüz tercihleri;
            UIPreferences["FontSize"] = 14;
            UIPreferences["AnimationEnabled"] = true;
            UIPreferences["AutoSaveInterval"] = 300; // 5 dakika;
            UIPreferences["ShowTooltips"] = true;
            UIPreferences["CompactMode"] = false;

            // Varsayılan bildirim tercihleri;
            NotificationPrefs = new NotificationPreferences;
            {
                EnableEmailNotifications = true,
                EnablePushNotifications = true,
                EnableSound = true,
                CriticalAlertsOnly = false,
                QuietHoursStart = new TimeSpan(22, 0, 0), // 22:00;
                QuietHoursEnd = new TimeSpan(7, 0, 0)     // 07:00;
            };

            // Varsayılan ses tercihleri;
            VoicePrefs = new VoicePreferences;
            {
                VoiceGender = VoiceGender.Female,
                SpeechRate = 1.0,
                SpeechVolume = 0.8,
                EnableVoiceCommands = true,
                Language = "tr-TR"
            };

            // Varsayılan gizlilik tercihleri;
            PrivacyPrefs = new PrivacyPreferences;
            {
                CollectUsageData = true,
                ShareAnonymousData = false,
                AutoDeleteHistoryDays = 30,
                EncryptLocalData = true;
            };

            // Varsayılan performans tercihleri;
            PerformancePrefs = new PerformancePreferences;
            {
                MaxConcurrentTasks = 4,
                CacheSizeMB = 512,
                EnableHardwareAcceleration = true,
                QualityLevel = QualityLevel.High;
            };
        }

        private double CalculateAdaptationScore()
        {
            var baseScore = 50.0;

            // Etkileşim sayısına göre artış;
            if (TotalInteractions > 100) baseScore += 20;
            else if (TotalInteractions > 50) baseScore += 10;
            else if (TotalInteractions > 10) baseScore += 5;

            // Başarı oranına göre artış;
            var successRate = TotalInteractions > 0 ? (double)SuccessfulCommands / TotalInteractions : 0;
            baseScore += successRate * 30;

            // Zaman bazlı uyum (kullanım süresi)
            var daysSinceCreation = (DateTime.UtcNow - CreatedAt).TotalDays;
            if (daysSinceCreation > 30) baseScore += 15;
            else if (daysSinceCreation > 7) baseScore += 5;

            return Math.Min(100, Math.Max(0, baseScore));
        }

        private string CalculateDataHash()
        {
            var dataToHash = $"{UserId}{Username}{FullName}{UserRole}{CreatedAt.Ticks}";

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(dataToHash);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        private void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (key.Length > 256)
                throw new ArgumentException("Key length cannot exceed 256 characters", nameof(key));
        }

        private void ValidateEncryptionKey(SecureString encryptionKey)
        {
            if (encryptionKey == null)
                throw new ArgumentNullException(nameof(encryptionKey));

            if (encryptionKey.Length < 8)
                throw new ArgumentException("Encryption key must be at least 8 characters", nameof(encryptionKey));
        }

        #endregion;

        #region INotifyPropertyChanged Implementation;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                // Yönetilen kaynakları temizle;
                ClearSensitiveData();

                if (_encryptedPreferences != null)
                {
                    _encryptedPreferences.Clear();
                    _encryptedPreferences = null;
                }

                if (UIPreferences != null)
                {
                    UIPreferences.Clear();
                    UIPreferences = null;
                }
            }

            _isDisposed = true;
        }

        ~UserData()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes;

        /// <summary>
        /// Bildirim tercihleri;
        /// </summary>
        [DataContract]
        public class NotificationPreferences;
        {
            [DataMember] public bool EnableEmailNotifications { get; set; }
            [DataMember] public bool EnablePushNotifications { get; set; }
            [DataMember] public bool EnableSound { get; set; }
            [DataMember] public bool CriticalAlertsOnly { get; set; }
            [DataMember] public TimeSpan QuietHoursStart { get; set; }
            [DataMember] public TimeSpan QuietHoursEnd { get; set; }
        }

        /// <summary>
        /// Ses tercihleri;
        /// </summary>
        [DataContract]
        public class VoicePreferences;
        {
            [DataMember] public VoiceGender VoiceGender { get; set; }
            [DataMember] public double SpeechRate { get; set; }
            [DataMember] public double SpeechVolume { get; set; }
            [DataMember] public bool EnableVoiceCommands { get; set; }
            [DataMember] public string Language { get; set; }
        }

        /// <summary>
        /// Gizlilik tercihleri;
        /// </summary>
        [DataContract]
        public class PrivacyPreferences;
        {
            [DataMember] public bool CollectUsageData { get; set; }
            [DataMember] public bool ShareAnonymousData { get; set; }
            [DataMember] public int AutoDeleteHistoryDays { get; set; }
            [DataMember] public bool EncryptLocalData { get; set; }
        }

        /// <summary>
        /// Performans tercihleri;
        /// </summary>
        [DataContract]
        public class PerformancePreferences;
        {
            [DataMember] public int MaxConcurrentTasks { get; set; }
            [DataMember] public int CacheSizeMB { get; set; }
            [DataMember] public bool EnableHardwareAcceleration { get; set; }
            [DataMember] public QualityLevel QualityLevel { get; set; }
        }

        /// <summary>
        /// Etkileşim verisi;
        /// </summary>
        [DataContract]
        public class InteractionData;
        {
            [DataMember] public string Type { get; set; }
            [DataMember] public DateTime Timestamp { get; set; }
            [DataMember] public string Command { get; set; }
            [DataMember] public bool Success { get; set; }
            [DataMember] public double ResponseTime { get; set; }
            [DataMember] public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        #endregion;
    }

    #region Enums;

    /// <summary>
    /// Kullanıcı rolü;
    /// </summary>
    public enum UserRole;
    {
        Guest = 0,
        Standard = 1,
        PowerUser = 2,
        Administrator = 3,
        System = 4;
    }

    /// <summary>
    /// Kullanıcı durumu;
    /// </summary>
    public enum UserStatus;
    {
        Inactive = 0,
        Active = 1,
        Suspended = 2,
        Banned = 3,
        Deleted = 4;
    }

    /// <summary>
    /// Ses cinsiyeti;
    /// </summary>
    public enum VoiceGender;
    {
        Male = 0,
        Female = 1,
        Neutral = 2;
    }

    /// <summary>
    /// Kalite seviyesi;
    /// </summary>
    public enum QualityLevel;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3;
    }

    #endregion;
}
