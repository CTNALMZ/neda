using NEDA.Common;
using NEDA.Common.Extensions;
using NEDA.Monitoring.Diagnostics;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEDA.Biometrics.VoiceIdentification;
{
    /// <summary>
    /// Represents a user's unique voice biometric profile;
    /// Used for voice authentication and identification;
    /// </summary>
    [Table("VoiceProfiles")]
    public class VoiceProfile : IAuditableEntity, IVersionable, ISecureEntity;
    {
        #region Constants and Static Members;

        private const int PROFILE_VERSION = 1;
        private const int MIN_SAMPLE_COUNT = 5;
        private const int MAX_SAMPLE_COUNT = 100;
        private const double MIN_CONFIDENCE_THRESHOLD = 0.7;
        private const double MAX_CONFIDENCE_THRESHOLD = 0.99;

        private static readonly TimeSpan PROFILE_EXPIRY_DURATION = TimeSpan.FromDays(90);
        private static readonly HashSet<string> SupportedLanguages = new()
        {
            "en-US", "en-GB", "tr-TR", "de-DE", "fr-FR",
            "es-ES", "it-IT", "ja-JP", "ko-KR", "zh-CN"
        };

        #endregion;

        #region Primary Properties;

        /// <summary>
        /// Unique identifier for the voice profile;
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGenerated.Identity)]
        public Guid Id { get; private set; }

        /// <summary>
        /// Reference to the user who owns this voice profile;
        /// </summary>
        [Required]
        [ForeignKey(nameof(User))]
        public Guid UserId { get; private set; }

        /// <summary>
        /// User who owns this voice profile;
        /// </summary>
        [JsonIgnore]
        public virtual UserProfile User { get; private set; }

        /// <summary>
        /// Profile version for backward compatibility;
        /// </summary>
        [Required]
        public int ProfileVersion { get; private set; } = PROFILE_VERSION;

        /// <summary>
        /// Current status of the voice profile;
        /// </summary>
        [Required]
        public VoiceProfileStatus Status { get; private set; } = VoiceProfileStatus.Creating;

        #endregion;

        #region Biometric Data;

        /// <summary>
        /// Encrypted voice feature vector (MFCC coefficients, pitch, formants, etc.)
        /// </summary>
        [Required]
        [MaxLength(8192)] // 8KB for encrypted biometric data;
        [EncryptedData]
        public byte[] EncryptedFeatureVector { get; private set; }

        /// <summary>
        /// Voice biometric template for fast matching;
        /// </summary>
        [MaxLength(4096)] // 4KB for template;
        [EncryptedData]
        public byte[] BiometricTemplate { get; private set; }

        /// <summary>
        /// Digital signature of the biometric data for integrity verification;
        /// </summary>
        [MaxLength(512)]
        public byte[] DataSignature { get; private set; }

        /// <summary>
        /// Cryptographic hash of the original voice samples;
        /// </summary>
        [MaxLength(64)]
        public byte[] SampleHash { get; private set; }

        #endregion;

        #region Voice Characteristics;

        /// <summary>
        /// Average fundamental frequency (pitch) in Hz;
        /// </summary>
        [Range(50, 500)]
        public double AveragePitch { get; private set; }

        /// <summary>
        /// Pitch variation (jitter) coefficient;
        /// </summary>
        [Range(0, 1)]
        public double PitchVariation { get; private set; }

        /// <summary>
        /// Average speaking rate in words per minute;
        /// </summary>
        [Range(50, 250)]
        public double SpeakingRate { get; private set; }

        /// <summary>
        /// Voice intensity/loudness characteristics;
        /// </summary>
        [Range(0, 1)]
        public double VoiceIntensity { get; private set; }

        /// <summary>
        /// Formant frequencies (F1, F2, F3, F4)
        /// </summary>
        [MaxLength(256)]
        public double[] FormantFrequencies { get; private set; }

        /// <summary>
        /// Mel-frequency cepstral coefficients (MFCCs)
        /// </summary>
        [MaxLength(1024)]
        public double[] MFCCoefficients { get; private set; }

        /// <summary>
        /// Voice quality metrics (harmonicity, noise ratio, etc.)
        /// </summary>
        public VoiceQualityMetrics QualityMetrics { get; private set; }

        #endregion;

        #region Profile Metadata;

        /// <summary>
        /// Primary language/locale of the voice profile;
        /// </summary>
        [Required]
        [StringLength(10)]
        public string LanguageCode { get; private set; }

        /// <summary>
        /// Accent or dialect information;
        /// </summary>
        [StringLength(50)]
        public string Accent { get; private set; }

        /// <summary>
        /// Gender information if available;
        /// </summary>
        public Gender? Gender { get; private set; }

        /// <summary>
        /// Age range estimation;
        /// </summary>
        public AgeRange? EstimatedAgeRange { get; private set; }

        /// <summary>
        /// Confidence score of the profile creation;
        /// </summary>
        [Range(MIN_CONFIDENCE_THRESHOLD, MAX_CONFIDENCE_THRESHOLD)]
        public double ConfidenceScore { get; private set; }

        /// <summary>
        /// Number of voice samples used to create this profile;
        /// </summary>
        [Range(MIN_SAMPLE_COUNT, MAX_SAMPLE_COUNT)]
        public int SampleCount { get; private set; }

        /// <summary>
        /// Total duration of voice samples in seconds;
        /// </summary>
        [Range(10, 3600)] // 10 seconds to 1 hour;
        public double TotalSampleDuration { get; private set; }

        /// <summary>
        /// Environment where samples were collected;
        /// </summary>
        public RecordingEnvironment RecordingEnvironment { get; private set; }

        #endregion;

        #region Security and Validation;

        /// <summary>
        /// Encryption key identifier for the biometric data;
        /// </summary>
        [Required]
        [StringLength(100)]
        public string EncryptionKeyId { get; private set; }

        /// <summary>
        /// Algorithm used for biometric template generation;
        /// </summary>
        [Required]
        [StringLength(50)]
        public string TemplateAlgorithm { get; private set; }

        /// <summary>
        /// Hash algorithm used for data integrity;
        /// </summary>
        [Required]
        [StringLength(50)]
        public string HashAlgorithm { get; private set; }

        /// <summary>
        /// Digital signature algorithm;
        /// </summary>
        [Required]
        [StringLength(50)]
        public string SignatureAlgorithm { get; private set; }

        /// <summary>
        /// Flag indicating if the profile has been verified;
        /// </summary>
        public bool IsVerified { get; private set; }

        /// <summary>
        /// Flag indicating if the profile is locked (temporarily unusable)
        /// </summary>
        public bool IsLocked { get; private set; }

        /// <summary>
        /// Reason for locking the profile;
        /// </summary>
        [StringLength(200)]
        public string LockReason { get; private set; }

        #endregion;

        #region Audit and Timing;

        /// <summary>
        /// When the profile was created;
        /// </summary>
        [Required]
        public DateTime CreatedAt { get; private set; }

        /// <summary>
        /// Who created the profile;
        /// </summary>
        [Required]
        [StringLength(100)]
        public string CreatedBy { get; private set; }

        /// <summary>
        /// When the profile was last updated;
        /// </summary>
        public DateTime? UpdatedAt { get; private set; }

        /// <summary>
        /// Who last updated the profile;
        /// </summary>
        [StringLength(100)]
        public string UpdatedBy { get; private set; }

        /// <summary>
        /// When the profile was last used for authentication;
        /// </summary>
        public DateTime? LastUsedAt { get; private set; }

        /// <summary>
        /// When the profile will expire and need renewal;
        /// </summary>
        public DateTime ExpiresAt { get; private set; }

        /// <summary>
        /// Number of times this profile has been used;
        /// </summary>
        public int UsageCount { get; private set; }

        /// <summary>
        /// Version for optimistic concurrency;
        /// </summary>
        [Timestamp]
        public byte[] RowVersion { get; private set; }

        #endregion;

        #region Navigation Properties;

        /// <summary>
        /// Collection of voice samples used to create this profile;
        /// </summary>
        [JsonIgnore]
        public virtual ICollection<VoiceSample> VoiceSamples { get; private set; }
            = new List<VoiceSample>();

        /// <summary>
        /// Authentication attempts using this profile;
        /// </summary>
        [JsonIgnore]
        public virtual ICollection<VoiceAuthenticationAttempt> AuthenticationAttempts { get; private set; }
            = new List<VoiceAuthenticationAttempt>();

        /// <summary>
        /// Profile update/version history;
        /// </summary>
        [JsonIgnore]
        public virtual ICollection<VoiceProfileHistory> History { get; private set; }
            = new List<VoiceProfileHistory>();

        #endregion;

        #region Constructors;

        /// <summary>
        /// Private constructor for EF Core;
        /// </summary>
        private VoiceProfile()
        {
            // Required by EF Core;
        }

        /// <summary>
        /// Creates a new voice profile with initial biometric data;
        /// </summary>
        /// <param name="userId">Owner user ID</param>
        /// <param name="voiceData">Processed voice biometric data</param>
        /// <param name="metadata">Profile metadata</param>
        /// <param name="cryptoService">Encryption service</param>
        /// <param name="createdBy">Creator identifier</param>
        /// <returns>New voice profile instance</returns>
        public static VoiceProfile Create(
            Guid userId,
            VoiceBiometricData voiceData,
            VoiceProfileMetadata metadata,
            ICryptoEngine cryptoService,
            string createdBy)
        {
            Guard.ArgumentNotNull(voiceData, nameof(voiceData));
            Guard.ArgumentNotNull(metadata, nameof(metadata));
            Guard.ArgumentNotNull(cryptoService, nameof(cryptoService));
            Guard.ArgumentNotNullOrEmpty(createdBy, nameof(createdBy));

            ValidateVoiceData(voiceData);
            ValidateMetadata(metadata);

            var profile = new VoiceProfile;
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy,
                Status = VoiceProfileStatus.Creating,
                LanguageCode = metadata.LanguageCode,
                Accent = metadata.Accent,
                Gender = metadata.Gender,
                EstimatedAgeRange = metadata.EstimatedAgeRange,
                SampleCount = metadata.SampleCount,
                TotalSampleDuration = metadata.TotalSampleDuration,
                RecordingEnvironment = metadata.RecordingEnvironment,
                AveragePitch = voiceData.AveragePitch,
                PitchVariation = voiceData.PitchVariation,
                SpeakingRate = voiceData.SpeakingRate,
                VoiceIntensity = voiceData.VoiceIntensity,
                FormantFrequencies = voiceData.FormantFrequencies,
                MFCCoefficients = voiceData.MFCCoefficients,
                QualityMetrics = voiceData.QualityMetrics,
                ConfidenceScore = voiceData.ConfidenceScore,
                TemplateAlgorithm = metadata.TemplateAlgorithm,
                HashAlgorithm = metadata.HashAlgorithm,
                SignatureAlgorithm = metadata.SignatureAlgorithm;
            };

            // Encrypt and store biometric data;
            profile.EncryptBiometricData(voiceData, cryptoService);

            // Set expiry date;
            profile.ExpiresAt = DateTime.UtcNow.Add(PROFILE_EXPIRY_DURATION);

            return profile;
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Finalizes the profile creation process;
        /// </summary>
        /// <param name="cryptoService">Encryption service for final validation</param>
        /// <param name="updatedBy">Updater identifier</param>
        public void FinalizeCreation(ICryptoEngine cryptoService, string updatedBy)
        {
            Guard.ArgumentNotNull(cryptoService, nameof(cryptoService));
            Guard.ArgumentNotNullOrEmpty(updatedBy, nameof(updatedBy));

            if (Status != VoiceProfileStatus.Creating)
            {
                throw new InvalidOperationException(
                    $"Cannot finalize profile in {Status} status. Profile must be in Creating status.");
            }

            ValidateProfileData();

            // Verify encryption integrity;
            if (!VerifyDataIntegrity(cryptoService))
            {
                throw new SecurityException("Voice profile data integrity check failed.");
            }

            Status = VoiceProfileStatus.Active;
            UpdatedAt = DateTime.UtcNow;
            UpdatedBy = updatedBy;
            IsVerified = true;

            // Create initial history entry
            AddHistoryEntry("Profile finalized and activated", updatedBy);
        }

        /// <summary>
        /// Updates the voice profile with new biometric data;
        /// </summary>
        /// <param name="newVoiceData">New voice biometric data</param>
        /// <param name="metadata">Updated metadata</param>
        /// <param name="cryptoService">Encryption service</param>
        /// <param name="updatedBy">Updater identifier</param>
        public void UpdateProfile(
            VoiceBiometricData newVoiceData,
            VoiceProfileMetadata metadata,
            ICryptoEngine cryptoService,
            string updatedBy)
        {
            Guard.ArgumentNotNull(newVoiceData, nameof(newVoiceData));
            Guard.ArgumentNotNull(metadata, nameof(metadata));
            Guard.ArgumentNotNull(cryptoService, nameof(cryptoService));
            Guard.ArgumentNotNullOrEmpty(updatedBy, nameof(updatedBy));

            if (IsLocked)
            {
                throw new InvalidOperationException(
                    $"Cannot update locked profile. Reason: {LockReason}");
            }

            if (Status != VoiceProfileStatus.Active)
            {
                throw new InvalidOperationException(
                    $"Cannot update profile in {Status} status.");
            }

            ValidateVoiceData(newVoiceData);
            ValidateMetadata(metadata);

            // Store current state for history;
            var previousState = CreateHistorySnapshot();

            // Update properties;
            LanguageCode = metadata.LanguageCode;
            Accent = metadata.Accent;
            Gender = metadata.Gender;
            EstimatedAgeRange = metadata.EstimatedAgeRange;
            SampleCount += metadata.SampleCount;
            TotalSampleDuration += metadata.TotalSampleDuration;
            AveragePitch = newVoiceData.AveragePitch;
            PitchVariation = newVoiceData.PitchVariation;
            SpeakingRate = newVoiceData.SpeakingRate;
            VoiceIntensity = newVoiceData.VoiceIntensity;
            FormantFrequencies = newVoiceData.FormantFrequencies;
            MFCCoefficients = newVoiceData.MFCCoefficients;
            QualityMetrics = newVoiceData.QualityMetrics;
            ConfidenceScore = newVoiceData.ConfidenceScore;

            // Re-encrypt with new data;
            EncryptBiometricData(newVoiceData, cryptoService);

            // Update audit fields;
            UpdatedAt = DateTime.UtcNow;
            UpdatedBy = updatedBy;
            ExpiresAt = DateTime.UtcNow.Add(PROFILE_EXPIRY_DURATION);

            // Add to history;
            AddHistoryEntry("Profile updated with new biometric data", updatedBy, previousState);
        }

        /// <summary>
        /// Records usage of this profile for authentication;
        /// </summary>
        /// <param name="success">Whether authentication was successful</param>
        /// <param name="confidenceScore">Confidence score of the attempt</param>
        public void RecordUsage(bool success, double confidenceScore)
        {
            LastUsedAt = DateTime.UtcNow;
            UsageCount++;

            if (!success && confidenceScore < MIN_CONFIDENCE_THRESHOLD)
            {
                // Consider implementing a fraud detection system here;
                DiagnosticTool.LogWarning(
                    $"Low confidence authentication attempt for voice profile {Id}. " +
                    $"Confidence: {confidenceScore:P2}");
            }
        }

        /// <summary>
        /// Locks the voice profile for security reasons;
        /// </summary>
        /// <param name="reason">Reason for locking</param>
        /// <param name="updatedBy">Who is locking the profile</param>
        public void LockProfile(string reason, string updatedBy)
        {
            Guard.ArgumentNotNullOrEmpty(reason, nameof(reason));
            Guard.ArgumentNotNullOrEmpty(updatedBy, nameof(updatedBy));

            if (IsLocked)
            {
                throw new InvalidOperationException("Profile is already locked.");
            }

            IsLocked = true;
            LockReason = reason;
            UpdatedAt = DateTime.UtcNow;
            UpdatedBy = updatedBy;

            AddHistoryEntry($"Profile locked: {reason}", updatedBy);

            // Publish domain event;
            DomainEvents.Raise(new VoiceProfileLockedEvent(Id, UserId, reason, DateTime.UtcNow));
        }

        /// <summary>
        /// Unlocks a previously locked voice profile;
        /// </summary>
        /// <param name="updatedBy">Who is unlocking the profile</param>
        public void UnlockProfile(string updatedBy)
        {
            Guard.ArgumentNotNullOrEmpty(updatedBy, nameof(updatedBy));

            if (!IsLocked)
            {
                throw new InvalidOperationException("Profile is not locked.");
            }

            IsLocked = false;
            LockReason = null;
            UpdatedAt = DateTime.UtcNow;
            UpdatedBy = updatedBy;

            AddHistoryEntry("Profile unlocked", updatedBy);

            // Publish domain event;
            DomainEvents.Raise(new VoiceProfileUnlockedEvent(Id, UserId, DateTime.UtcNow));
        }

        /// <summary>
        /// Marks the profile as expired;
        /// </summary>
        /// <param name="updatedBy">Who is expiring the profile</param>
        public void ExpireProfile(string updatedBy)
        {
            Guard.ArgumentNotNullOrEmpty(updatedBy, nameof(updatedBy));

            if (Status == VoiceProfileStatus.Expired)
            {
                throw new InvalidOperationException("Profile is already expired.");
            }

            Status = VoiceProfileStatus.Expired;
            UpdatedAt = DateTime.UtcNow;
            UpdatedBy = updatedBy;

            AddHistoryEntry("Profile expired", updatedBy);
        }

        /// <summary>
        /// Renews an expired or soon-to-expire profile;
        /// </summary>
        /// <param name="cryptoService">Encryption service for renewal</param>
        /// <param name="updatedBy">Who is renewing the profile</param>
        public void RenewProfile(ICryptoEngine cryptoService, string updatedBy)
        {
            Guard.ArgumentNotNull(cryptoService, nameof(cryptoService));
            Guard.ArgumentNotNullOrEmpty(updatedBy, nameof(updatedBy));

            if (Status != VoiceProfileStatus.Active && Status != VoiceProfileStatus.Expired)
            {
                throw new InvalidOperationException(
                    $"Cannot renew profile in {Status} status.");
            }

            Status = VoiceProfileStatus.Active;
            ExpiresAt = DateTime.UtcNow.Add(PROFILE_EXPIRY_DURATION);
            UpdatedAt = DateTime.UtcNow;
            UpdatedBy = updatedBy;

            // Verify data integrity on renewal;
            if (!VerifyDataIntegrity(cryptoService))
            {
                throw new SecurityException("Data integrity check failed during renewal.");
            }

            AddHistoryEntry("Profile renewed", updatedBy);
        }

        /// <summary>
        /// Verifies if the profile is currently usable;
        /// </summary>
        /// <returns>True if profile can be used for authentication</returns>
        public bool IsUsable()
        {
            return Status == VoiceProfileStatus.Active;
                && !IsLocked;
                && ExpiresAt > DateTime.UtcNow;
                && IsVerified;
                && ConfidenceScore >= MIN_CONFIDENCE_THRESHOLD;
        }

        /// <summary>
        /// Gets the days remaining until expiry;
        /// </summary>
        /// <returns>Number of days until expiry</returns>
        public int GetDaysUntilExpiry()
        {
            if (ExpiresAt <= DateTime.UtcNow)
                return 0;

            return (int)(ExpiresAt.Value - DateTime.UtcNow).TotalDays;
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Encrypts and stores biometric data;
        /// </summary>
        private void EncryptBiometricData(VoiceBiometricData voiceData, ICryptoEngine cryptoService)
        {
            try
            {
                // Serialize biometric data;
                var biometricData = JsonSerializer.SerializeToUtf8Bytes(voiceData);

                // Generate hash of original data for integrity checking;
                SampleHash = cryptoService.ComputeHash(biometricData, HashAlgorithm);

                // Encrypt the biometric data;
                var encryptionResult = cryptoService.Encrypt(
                    biometricData,
                    EncryptionKeyId ?? GenerateEncryptionKeyId());

                EncryptedFeatureVector = encryptionResult.Ciphertext;
                EncryptionKeyId = encryptionResult.KeyId;

                // Generate biometric template for fast matching;
                BiometricTemplate = GenerateBiometricTemplate(voiceData);

                // Sign the data for integrity verification;
                DataSignature = cryptoService.SignData(
                    EncryptedFeatureVector,
                    SignatureAlgorithm);
            }
            catch (Exception ex)
            {
                throw new SecurityException("Failed to encrypt voice biometric data.", ex);
            }
        }

        /// <summary>
        /// Generates a biometric template for fast matching;
        /// </summary>
        private byte[] GenerateBiometricTemplate(VoiceBiometricData voiceData)
        {
            // In a real implementation, this would use specialized algorithms;
            // like i-vector, x-vector, or d-vector extraction;

            // For this example, we'll create a simplified template;
            var templateData = new;
            {
                Pitch = Math.Round(voiceData.AveragePitch, 2),
                MFCC_Mean = voiceData.MFCCoefficients?.Take(13).Average() ?? 0,
                Formants = voiceData.FormantFrequencies?.Take(4).ToArray() ?? Array.Empty<double>(),
                Timestamp = DateTime.UtcNow.Ticks;
            };

            return JsonSerializer.SerializeToUtf8Bytes(templateData);
        }

        /// <summary>
        /// Generates a unique encryption key identifier;
        /// </summary>
        private string GenerateEncryptionKeyId()
        {
            return $"voice-profile-{Id}-key-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Verifies the integrity of encrypted data;
        /// </summary>
        private bool VerifyDataIntegrity(ICryptoEngine cryptoService)
        {
            try
            {
                // Verify signature;
                var isSignatureValid = cryptoService.VerifySignature(
                    EncryptedFeatureVector,
                    DataSignature,
                    SignatureAlgorithm);

                if (!isSignatureValid)
                {
                    DiagnosticTool.LogError($"Signature verification failed for voice profile {Id}");
                    return false;
                }

                // Verify data is not corrupted;
                if (EncryptedFeatureVector == null || EncryptedFeatureVector.Length == 0)
                {
                    DiagnosticTool.LogError($"Empty encrypted data for voice profile {Id}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticTool.LogException(ex, $"Data integrity check failed for voice profile {Id}");
                return false;
            }
        }

        /// <summary>
        /// Validates voice biometric data;
        /// </summary>
        private static void ValidateVoiceData(VoiceBiometricData voiceData)
        {
            if (voiceData.AveragePitch < 50 || voiceData.AveragePitch > 500)
                throw new ArgumentException("Average pitch must be between 50 and 500 Hz.");

            if (voiceData.PitchVariation < 0 || voiceData.PitchVariation > 1)
                throw new ArgumentException("Pitch variation must be between 0 and 1.");

            if (voiceData.ConfidenceScore < MIN_CONFIDENCE_THRESHOLD)
                throw new ArgumentException(
                    $"Confidence score must be at least {MIN_CONFIDENCE_THRESHOLD:P2}");

            if (voiceData.MFCCoefficients == null || voiceData.MFCCoefficients.Length < 13)
                throw new ArgumentException("MFCC coefficients must include at least 13 values.");
        }

        /// <summary>
        /// Validates profile metadata;
        /// </summary>
        private static void ValidateMetadata(VoiceProfileMetadata metadata)
        {
            if (!SupportedLanguages.Contains(metadata.LanguageCode))
                throw new ArgumentException($"Unsupported language code: {metadata.LanguageCode}");

            if (metadata.SampleCount < MIN_SAMPLE_COUNT)
                throw new ArgumentException(
                    $"At least {MIN_SAMPLE_COUNT} samples are required.");

            if (metadata.TotalSampleDuration < 10)
                throw new ArgumentException(
                    "Total sample duration must be at least 10 seconds.");
        }

        /// <summary>
        /// Validates the entire profile data;
        /// </summary>
        private void ValidateProfileData()
        {
            if (SampleCount < MIN_SAMPLE_COUNT)
                throw new InvalidOperationException(
                    $"Profile has insufficient samples: {SampleCount}");

            if (ConfidenceScore < MIN_CONFIDENCE_THRESHOLD)
                throw new InvalidOperationException(
                    $"Profile confidence score too low: {ConfidenceScore:P2}");

            if (string.IsNullOrEmpty(LanguageCode))
                throw new InvalidOperationException("Language code is required.");

            if (EncryptedFeatureVector == null || EncryptedFeatureVector.Length == 0)
                throw new InvalidOperationException("Biometric data is missing.");
        }

        /// <summary>
        /// Creates a snapshot of current state for history tracking;
        /// </summary>
        private string CreateHistorySnapshot()
        {
            var snapshot = new;
            {
                Id,
                Status,
                ConfidenceScore,
                SampleCount,
                LastUsedAt,
                ExpiresAt,
                IsLocked,
                LockReason,
                UpdatedAt = DateTime.UtcNow;
            };

            return JsonSerializer.Serialize(snapshot);
        }

        /// <summary>
        /// Adds an entry to the profile history;
        /// </summary>
        private void AddHistoryEntry(string action, string performedBy, string previousState = null)
        {
            var historyEntry = VoiceProfileHistory.Create(
                Id,
                action,
                previousState,
                performedBy);

            History.Add(historyEntry);
        }

        #endregion;

        #region Domain Events;

        private readonly List<IDomainEvent> _domainEvents = new();

        /// <summary>
        /// Domain events raised by this entity;
        /// </summary>
        [JsonIgnore]
        public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

        /// <summary>
        /// Clears all domain events;
        /// </summary>
        public void ClearDomainEvents()
        {
            _domainEvents.Clear();
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Status of a voice profile;
    /// </summary>
    public enum VoiceProfileStatus;
    {
        Creating = 0,
        Active = 1,
        Locked = 2,
        Expired = 3,
        Deleted = 4;
    }

    /// <summary>
    /// Voice recording environment classification;
    /// </summary>
    public enum RecordingEnvironment;
    {
        Unknown = 0,
        Studio = 1,
        Office = 2,
        Home = 3,
        Vehicle = 4,
        PublicSpace = 5,
        Noisy = 6;
    }

    /// <summary>
    /// Gender classification;
    /// </summary>
    public enum Gender;
    {
        Unknown = 0,
        Male = 1,
        Female = 2,
        Other = 3;
    }

    /// <summary>
    /// Age range estimation;
    /// </summary>
    public enum AgeRange;
    {
        Unknown = 0,
        Child = 1,      // 0-12;
        Teen = 2,       // 13-19;
        YoungAdult = 3, // 20-35;
        Adult = 4,      // 36-60;
        Senior = 5      // 61+
    }

    /// <summary>
    /// Voice quality metrics;
    /// </summary>
    public class VoiceQualityMetrics;
    {
        public double HarmonicToNoiseRatio { get; set; }
        public double Jitter { get; set; }
        public double Shimmer { get; set; }
        public double SpectralCentroid { get; set; }
        public double SpectralFlux { get; set; }
        public double ZeroCrossingRate { get; set; }
    }

    /// <summary>
    /// Voice biometric data container;
    /// </summary>
    public class VoiceBiometricData;
    {
        public double AveragePitch { get; set; }
        public double PitchVariation { get; set; }
        public double SpeakingRate { get; set; }
        public double VoiceIntensity { get; set; }
        public double[] FormantFrequencies { get; set; }
        public double[] MFCCoefficients { get; set; }
        public VoiceQualityMetrics QualityMetrics { get; set; }
        public double ConfidenceScore { get; set; }
    }

    /// <summary>
    /// Voice profile metadata;
    /// </summary>
    public class VoiceProfileMetadata;
    {
        public string LanguageCode { get; set; }
        public string Accent { get; set; }
        public Gender? Gender { get; set; }
        public AgeRange? EstimatedAgeRange { get; set; }
        public int SampleCount { get; set; }
        public double TotalSampleDuration { get; set; }
        public RecordingEnvironment RecordingEnvironment { get; set; }
        public string TemplateAlgorithm { get; set; } = "IVector";
        public string HashAlgorithm { get; set; } = "SHA256";
        public string SignatureAlgorithm { get; set; } = "ECDsaP256";
    }

    /// <summary>
    /// Domain event: Voice profile locked;
    /// </summary>
    public class VoiceProfileLockedEvent : IDomainEvent;
    {
        public Guid ProfileId { get; }
        public Guid UserId { get; }
        public string Reason { get; }
        public DateTime LockedAt { get; }

        public VoiceProfileLockedEvent(Guid profileId, Guid userId, string reason, DateTime lockedAt)
        {
            ProfileId = profileId;
            UserId = userId;
            Reason = reason;
            LockedAt = lockedAt;
        }
    }

    /// <summary>
    /// Domain event: Voice profile unlocked;
    /// </summary>
    public class VoiceProfileUnlockedEvent : IDomainEvent;
    {
        public Guid ProfileId { get; }
        public Guid UserId { get; }
        public DateTime UnlockedAt { get; }

        public VoiceProfileUnlockedEvent(Guid profileId, Guid userId, DateTime unlockedAt)
        {
            ProfileId = profileId;
            UserId = userId;
            UnlockedAt = unlockedAt;
        }
    }

    #endregion;
}
