using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.SecurityModules.Manifest;

namespace NEDA.SecurityModules.AdvancedSecurity.Authentication;
{
    /// <summary>
    /// Kimlik bilgilerini doğrulayan, yüksek güvenlikli, endüstriyel seviyede validator;
    /// Brute-force koruma, karmaşık şifre politikaları, MFA ve risk bazlı doğrulama içerir;
    /// </summary>
    public class CredentialValidator : ICredentialValidator, IDisposable;
    {
        private readonly ILogger<CredentialValidator> _logger;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly ITokenManager _tokenManager;
        private readonly IMemoryCache _memoryCache;
        private readonly CredentialValidationConfig _config;
        private readonly ICredentialRepository _repository;
        private readonly IRiskAnalyzer _riskAnalyzer;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, FailedAttemptInfo> _failedAttempts = new();
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public CredentialValidator(
            ILogger<CredentialValidator> logger,
            ICryptoEngine cryptoEngine,
            ITokenManager tokenManager,
            IMemoryCache memoryCache,
            IConfiguration configuration,
            ICredentialRepository repository,
            IRiskAnalyzer riskAnalyzer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _riskAnalyzer = riskAnalyzer ?? throw new ArgumentNullException(nameof(riskAnalyzer));

            _config = CredentialValidationConfig.LoadFromConfiguration(configuration);

            // Başarısız denemeleri temizlemek için timer;
            _cleanupTimer = new Timer(
                _ => CleanupOldFailedAttempts(),
                null,
                TimeSpan.FromMinutes(_config.FailedAttemptCleanupMinutes),
                TimeSpan.FromMinutes(_config.FailedAttemptCleanupMinutes));
        }

        #region Public Validation Methods;

        /// <summary>
        /// Kimlik bilgilerini doğrular (Temel doğrulama)
        /// </summary>
        public async Task<CredentialValidationResult> ValidateCredentialsAsync(
            string username,
            SecureString password,
            ValidationContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));
            if (password == null || password.Length == 0)
                throw new ArgumentException("Password cannot be empty", nameof(password));

            context ??= new ValidationContext();

            try
            {
                // 1. Güvenlik kontrolleri (Brute-force, IP kısıtlaması, vs.)
                var securityCheck = await PerformSecurityChecksAsync(username, context, cancellationToken);
                if (!securityCheck.IsAllowed)
                {
                    _logger.LogWarning("Security block for user {Username}: {Reason}",
                        username, securityCheck.BlockReason);

                    return CredentialValidationResult.Blocked(
                        securityCheck.BlockReason,
                        securityCheck.RetryAfter);
                }

                // 2. Risk analizi;
                var riskAssessment = await _riskAnalyzer.AssessRiskAsync(username, context, cancellationToken);
                context.RiskScore = riskAssessment.RiskScore;
                context.RiskLevel = riskAssessment.RiskLevel;

                // 3. Kimlik bilgilerini doğrula;
                var validationStartTime = DateTime.UtcNow;
                var isValid = await VerifyCredentialsInternalAsync(username, password, cancellationToken);
                var validationDuration = DateTime.UtcNow - validationStartTime;

                if (isValid)
                {
                    // 4. Başarılı doğrulama - kayıtları temizle;
                    await ClearFailedAttemptsAsync(username);

                    // 5. Şifre süresi kontrolü;
                    var passwordAge = await GetPasswordAgeAsync(username, cancellationToken);
                    var isPasswordExpired = passwordAge > TimeSpan.FromDays(_config.PasswordMaxAgeDays);

                    // 6. MFA gereklilik kontrolü;
                    var mfaRequired = await DetermineMfaRequirementAsync(username, context, cancellationToken);

                    // 7. Oturum politikalarını kontrol et;
                    var sessionPolicy = await CheckSessionPolicyAsync(username, context, cancellationToken);

                    // 8. Audit log;
                    await LogSuccessfulValidationAsync(username, context, validationDuration, cancellationToken);

                    _logger.LogInformation("Credentials validated successfully for user {Username} in {Duration}ms",
                        username, validationDuration.TotalMilliseconds);

                    return CredentialValidationResult.Success(
                        mfaRequired,
                        isPasswordExpired,
                        sessionPolicy,
                        riskAssessment.RiskLevel);
                }
                else;
                {
                    // 4. Başarısız doğrulama - kayıt altına al;
                    await RecordFailedAttemptAsync(username, context);

                    // 5. Kalan deneme hakkı ve kilitleme süresi;
                    var attemptsLeft = await GetRemainingAttemptsAsync(username);
                    var lockoutTime = await GetLockoutTimeRemainingAsync(username);

                    // 6. Risk puanını artır;
                    await _riskAnalyzer.RecordFailedAttemptAsync(username, context, cancellationToken);

                    // 7. Audit log;
                    await LogFailedValidationAsync(username, context, cancellationToken);

                    _logger.LogWarning("Invalid credentials for user {Username}. Attempts left: {AttemptsLeft}, Risk: {RiskLevel}",
                        username, attemptsLeft, riskAssessment.RiskLevel);

                    return CredentialValidationResult.Failure(
                        "Invalid credentials",
                        attemptsLeft,
                        lockoutTime,
                        riskAssessment.RiskLevel);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating credentials for user {Username}", username);
                throw new CredentialValidationException($"Error validating credentials for user {username}", ex);
            }
        }

        /// <summary>
        /// Çok faktörlü kimlik doğrulama;
        /// </summary>
        public async Task<MfaValidationResult> ValidateMultiFactorAsync(
            string username,
            MultiFactorRequest request,
            ValidationContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            context ??= new ValidationContext();

            try
            {
                // 1. MFA attempt kontrolü;
                var mfaCheck = await CheckMfaAttemptLimitsAsync(username, context, cancellationToken);
                if (!mfaCheck.IsAllowed)
                {
                    return MfaValidationResult.Blocked(
                        mfaCheck.BlockReason,
                        mfaCheck.RetryAfter);
                }

                // 2. MFA yöntemine göre doğrulama;
                bool isValid;
                switch (request.Method)
                {
                    case MfaMethod.TOTP:
                        isValid = await ValidateTotpAsync(username, request.Code, cancellationToken);
                        break;
                    case MfaMethod.SMS:
                        isValid = await ValidateSmsCodeAsync(username, request.Code, cancellationToken);
                        break;
                    case MfaMethod.Email:
                        isValid = await ValidateEmailCodeAsync(username, request.Code, cancellationToken);
                        break;
                    case MfaMethod.Biometric:
                        isValid = await ValidateBiometricAsync(username, request.BiometricData, cancellationToken);
                        break;
                    case MfaMethod.SecurityKey:
                        isValid = await ValidateSecurityKeyAsync(username, request.SecurityKeyResponse, cancellationToken);
                        break;
                    case MfaMethod.BackupCode:
                        isValid = await ValidateBackupCodeAsync(username, request.Code, cancellationToken);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported MFA method: {request.Method}");
                }

                if (isValid)
                {
                    // 3. Başarılı MFA - kayıtları temizle;
                    await ClearMfaAttemptsAsync(username);

                    // 4. MFA token oluştur;
                    var mfaToken = await _tokenManager.GenerateMfaTokenAsync(
                        username,
                        request.Method,
                        context,
                        cancellationToken);

                    // 5. Audit log;
                    await LogSuccessfulMfaAsync(username, request.Method, context, cancellationToken);

                    _logger.LogInformation("MFA validation successful for user {Username} with method {Method}",
                        username, request.Method);

                    return MfaValidationResult.Success(mfaToken);
                }
                else;
                {
                    // 3. Başarısız MFA - kayıt altına al;
                    await RecordFailedMfaAttemptAsync(username, request.Method, context);

                    // 4. Kalan deneme hakkı;
                    var attemptsLeft = await GetRemainingMfaAttemptsAsync(username);
                    var lockoutTime = await GetMfaLockoutTimeRemainingAsync(username);

                    // 5. Risk puanını artır;
                    await _riskAnalyzer.RecordFailedMfaAsync(username, context, cancellationToken);

                    // 6. Audit log;
                    await LogFailedMfaAsync(username, request.Method, context, cancellationToken);

                    _logger.LogWarning("Invalid MFA code for user {Username} with method {Method}. Attempts left: {AttemptsLeft}",
                        username, request.Method, attemptsLeft);

                    return MfaValidationResult.Failure(
                        "Invalid MFA code",
                        attemptsLeft,
                        lockoutTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating MFA for user {Username}", username);
                throw new CredentialValidationException($"Error validating MFA for user {username}", ex);
            }
        }

        /// <summary>
        /// Biyometrik kimlik doğrulama;
        /// </summary>
        public async Task<BiometricValidationResult> ValidateBiometricAsync(
            string username,
            BiometricData biometricData,
            ValidationContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));
            if (biometricData == null)
                throw new ArgumentNullException(nameof(biometricData));

            context ??= new ValidationContext();

            try
            {
                // 1. Biyometrik attempt kontrolü;
                var biometricCheck = await CheckBiometricAttemptLimitsAsync(username, context, cancellationToken);
                if (!biometricCheck.IsAllowed)
                {
                    return BiometricValidationResult.Blocked(
                        biometricCheck.BlockReason,
                        biometricCheck.RetryAfter);
                }

                // 2. Biyometrik veriyi doğrula;
                var storedTemplate = await _repository.GetBiometricTemplateAsync(username, cancellationToken);
                if (storedTemplate == null)
                {
                    return BiometricValidationResult.Failure("No biometric template registered");
                }

                // 3. Biyometrik eşleştirme;
                var matchResult = await PerformBiometricMatchingAsync(
                    biometricData,
                    storedTemplate,
                    cancellationToken);

                if (matchResult.IsMatch && matchResult.ConfidenceScore >= _config.BiometricThreshold)
                {
                    // 4. Başarılı biyometrik doğrulama;
                    await ClearBiometricAttemptsAsync(username);

                    // 5. Biyometrik token oluştur;
                    var biometricToken = await _tokenManager.GenerateBiometricTokenAsync(
                        username,
                        matchResult.ConfidenceScore,
                        context,
                        cancellationToken);

                    // 6. Liveness kontrolü (canlılık tespiti)
                    var livenessCheck = await CheckLivenessAsync(biometricData, cancellationToken);

                    // 7. Audit log;
                    await LogSuccessfulBiometricAsync(username, matchResult.ConfidenceScore, context, cancellationToken);

                    _logger.LogInformation("Biometric validation successful for user {Username} with confidence {Confidence}%",
                        username, matchResult.ConfidenceScore);

                    return BiometricValidationResult.Success(
                        biometricToken,
                        matchResult.ConfidenceScore,
                        livenessCheck.IsLive);
                }
                else;
                {
                    // 4. Başarısız biyometrik doğrulama;
                    await RecordFailedBiometricAttemptAsync(username, context);

                    // 5. Kalan deneme hakkı;
                    var attemptsLeft = await GetRemainingBiometricAttemptsAsync(username);
                    var lockoutTime = await GetBiometricLockoutTimeRemainingAsync(username);

                    // 6. Risk puanını artır;
                    await _riskAnalyzer.RecordFailedBiometricAsync(username, context, cancellationToken);

                    // 7. Audit log;
                    await LogFailedBiometricAsync(username, matchResult.ConfidenceScore, context, cancellationToken);

                    _logger.LogWarning("Biometric validation failed for user {Username}. Confidence: {Confidence}%, Threshold: {Threshold}%",
                        username, matchResult.ConfidenceScore, _config.BiometricThreshold);

                    return BiometricValidationResult.Failure(
                        $"Biometric match failed (confidence: {matchResult.ConfidenceScore}%)",
                        attemptsLeft,
                        lockoutTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating biometric for user {Username}", username);
                throw new CredentialValidationException($"Error validating biometric for user {username}", ex);
            }
        }

        /// <summary>
        /// Şifre politikalarını kontrol eder;
        /// </summary>
        public async Task<PasswordPolicyResult> ValidatePasswordPolicyAsync(
            SecureString password,
            string username = null,
            PasswordPolicyContext policyContext = null,
            CancellationToken cancellationToken = default)
        {
            if (password == null)
                throw new ArgumentNullException(nameof(password));

            policyContext ??= new PasswordPolicyContext();

            try
            {
                var result = new PasswordPolicyResult;
                {
                    IsValid = true,
                    Requirements = new List<PasswordRequirement>(),
                    Score = 0,
                    Strength = PasswordStrength.VeryWeak;
                };

                var passwordString = password.ToUnsecureString();
                var passwordBytes = Encoding.UTF8.GetBytes(passwordString);

                // 1. Temel uzunluk kontrolü;
                result.Requirements.Add(CheckLengthRequirement(password));

                // 2. Karmaşıklık kontrolü;
                result.Requirements.AddRange(CheckComplexityRequirements(password));

                // 3. Sözlük kontrolü;
                if (_config.CheckDictionaryAttacks)
                {
                    result.Requirements.Add(CheckDictionaryRequirement(passwordString));
                }

                // 4. Pattern kontrolü;
                if (_config.DetectCommonPatterns)
                {
                    result.Requirements.AddRange(CheckPatternRequirements(passwordString));
                }

                // 5. Entropi hesabı;
                result.Requirements.Add(CheckEntropyRequirement(passwordBytes));

                // 6. Kullanıcı adı benzerliği kontrolü;
                if (!string.IsNullOrEmpty(username) && _config.PreventUsernameSimilarity)
                {
                    result.Requirements.Add(CheckUsernameSimilarityRequirement(passwordString, username));
                }

                // 7. Geçmiş şifre kontrolü;
                if (!string.IsNullOrEmpty(username) && _config.PasswordHistorySize > 0)
                {
                    var historyCheck = await CheckPasswordHistoryAsync(username, password, cancellationToken);
                    result.Requirements.Add(historyCheck);
                }

                // 8. Zaman bazlı zayıflık kontrolü;
                if (_config.DetectTimeBasedPatterns)
                {
                    result.Requirements.AddRange(CheckTimeBasedRequirements(passwordString));
                }

                // 9. Sonuçları değerlendir;
                result.MetRequirements = result.Requirements.Count(r => r.IsMet);
                result.TotalRequirements = result.Requirements.Count;
                result.IsValid = result.Requirements.All(r => r.IsMet || !r.IsMandatory);

                // 10. Şifre gücünü hesapla;
                result.Score = CalculatePasswordScore(result.Requirements);
                result.Strength = DeterminePasswordStrength(result.Score);

                // 11. Önerileri oluştur;
                result.Recommendations = GeneratePasswordRecommendations(result.Requirements);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating password policy");
                throw new CredentialValidationException("Error validating password policy", ex);
            }
        }

        /// <summary>
        /// Kimlik bilgisi güvenlik durumunu kontrol eder;
        /// </summary>
        public async Task<CredentialSecurityStatus> GetCredentialSecurityStatusAsync(
            string username,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));

            try
            {
                var status = new CredentialSecurityStatus;
                {
                    Username = username,
                    CheckedAt = DateTime.UtcNow;
                };

                // 1. Şifre bilgileri;
                status.PasswordInfo = await GetPasswordInfoAsync(username, cancellationToken);

                // 2. MFA durumu;
                status.MfaStatus = await GetMfaStatusAsync(username, cancellationToken);

                // 3. Biyometrik durum;
                status.BiometricStatus = await GetBiometricStatusAsync(username, cancellationToken);

                // 4. Başarısız denemeler;
                status.FailedAttempts = await GetFailedAttemptInfoAsync(username);

                // 5. Oturum durumu;
                status.SessionStatus = await GetSessionStatusAsync(username, cancellationToken);

                // 6. Risk durumu;
                status.RiskAssessment = await _riskAnalyzer.GetRiskAssessmentAsync(username, cancellationToken);

                // 7. Son etkinlikler;
                status.RecentActivities = await GetRecentActivitiesAsync(username, cancellationToken);

                // 8. Güvenlik önerileri;
                status.SecurityRecommendations = await GenerateSecurityRecommendationsAsync(status, cancellationToken);

                // 9. Uyumluluk durumu;
                status.ComplianceStatus = await CheckComplianceStatusAsync(status, cancellationToken);

                // 10. Genel güvenlik skoru;
                status.SecurityScore = CalculateSecurityScore(status);

                return status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting credential security status for user {Username}", username);
                throw new CredentialValidationException($"Error getting security status for user {username}", ex);
            }
        }

        /// <summary>
        /// Şifre değiştirme işlemi;
        /// </summary>
        public async Task<PasswordChangeResult> ChangePasswordAsync(
            string username,
            SecureString currentPassword,
            SecureString newPassword,
            PasswordChangeContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));
            if (currentPassword == null)
                throw new ArgumentNullException(nameof(currentPassword));
            if (newPassword == null)
                throw new ArgumentNullException(nameof(newPassword));

            context ??= new PasswordChangeContext();

            try
            {
                // 1. Mevcut şifreyi doğrula;
                var currentValidation = await ValidateCredentialsAsync(
                    username,
                    currentPassword,
                    new ValidationContext { Operation = "PasswordChange" },
                    cancellationToken);

                if (!currentValidation.IsValid)
                {
                    return PasswordChangeResult.Failure(
                        "Current password is incorrect",
                        currentValidation.RemainingAttempts,
                        currentValidation.LockoutTimeRemaining);
                }

                // 2. Yeni şifre politikalarını kontrol et;
                var policyResult = await ValidatePasswordPolicyAsync(
                    newPassword,
                    username,
                    new PasswordPolicyContext { IsPasswordChange = true },
                    cancellationToken);

                if (!policyResult.IsValid)
                {
                    return PasswordChangeResult.Failure(
                        "New password does not meet policy requirements",
                        failedRequirements: policyResult.Requirements;
                            .Where(r => !r.IsMet)
                            .Select(r => r.Description)
                            .ToList());
                }

                // 3. Şifre değişikliği için risk kontrolü;
                var riskCheck = await CheckPasswordChangeRiskAsync(
                    username,
                    currentPassword,
                    newPassword,
                    context,
                    cancellationToken);

                if (!riskCheck.IsAllowed)
                {
                    return PasswordChangeResult.Failure(
                        $"Password change blocked: {riskCheck.BlockReason}",
                        requiresAdditionalVerification: riskCheck.RequiresVerification);
                }

                // 4. Şifreyi değiştir;
                var changeResult = await PerformPasswordChangeInternalAsync(
                    username,
                    newPassword,
                    context,
                    cancellationToken);

                if (changeResult.Success)
                {
                    // 5. Başarısız denemeleri temizle;
                    await ClearFailedAttemptsAsync(username);

                    // 6. Şifre geçmişine ekle;
                    await AddToPasswordHistoryAsync(username, newPassword, cancellationToken);

                    // 7. Tüm oturumları sonlandır (opsiyonel)
                    if (_config.InvalidateSessionsOnPasswordChange)
                    {
                        await _repository.InvalidateAllSessionsAsync(username, cancellationToken);
                    }

                    // 8. Bildirim gönder;
                    await SendPasswordChangeNotificationAsync(username, context, cancellationToken);

                    // 9. Audit log;
                    await LogPasswordChangeAsync(username, context, cancellationToken);

                    _logger.LogInformation("Password changed successfully for user {Username}", username);

                    return PasswordChangeResult.Success(
                        changeResult.NextRequiredChange,
                        changeResult.EnforceMfa,
                        changeResult.RequiresReauthentication);
                }
                else;
                {
                    return PasswordChangeResult.Failure(changeResult.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing password for user {Username}", username);
                throw new CredentialValidationException($"Error changing password for user {username}", ex);
            }
        }

        #endregion;

        #region Private Helper Methods;

        private async Task<SecurityCheckResult> PerformSecurityChecksAsync(
            string username,
            ValidationContext context,
            CancellationToken cancellationToken)
        {
            var result = new SecurityCheckResult { IsAllowed = true };

            // 1. Account lock kontrolü;
            var isAccountLocked = await _repository.IsAccountLockedAsync(username, cancellationToken);
            if (isAccountLocked)
            {
                result.IsAllowed = false;
                result.BlockReason = "Account is locked";
                result.RetryAfter = await _repository.GetAccountLockoutRemainingAsync(username, cancellationToken);
                return result;
            }

            // 2. IP kısıtlama kontrolü;
            if (!string.IsNullOrEmpty(context.IpAddress) && _config.EnableIpRestrictions)
            {
                var isIpAllowed = await _repository.IsIpAllowedAsync(
                    username,
                    context.IpAddress,
                    cancellationToken);

                if (!isIpAllowed)
                {
                    result.IsAllowed = false;
                    result.BlockReason = "IP address is not allowed";
                    return result;
                }
            }

            // 3. Zaman kısıtlaması kontrolü;
            if (_config.EnableTimeRestrictions)
            {
                var isTimeAllowed = await _repository.IsLoginTimeAllowedAsync(
                    username,
                    context.Timestamp,
                    cancellationToken);

                if (!isTimeAllowed)
                {
                    result.IsAllowed = false;
                    result.BlockReason = "Login not allowed at this time";
                    return result;
                }
            }

            // 4. Coğrafi kısıtlama kontrolü;
            if (!string.IsNullOrEmpty(context.Geolocation) && _config.EnableGeolocationRestrictions)
            {
                var isLocationAllowed = await _repository.IsLocationAllowedAsync(
                    username,
                    context.Geolocation,
                    cancellationToken);

                if (!isLocationAllowed)
                {
                    result.IsAllowed = false;
                    result.BlockReason = "Location is not allowed";
                    return result;
                }
            }

            // 5. Cihaz kısıtlama kontrolü;
            if (!string.IsNullOrEmpty(context.DeviceId) && _config.EnableDeviceRestrictions)
            {
                var isDeviceAllowed = await _repository.IsDeviceAllowedAsync(
                    username,
                    context.DeviceId,
                    cancellationToken);

                if (!isDeviceAllowed)
                {
                    result.IsAllowed = false;
                    result.BlockReason = "Device is not allowed";
                    return result;
                }
            }

            // 6. Başarısız deneme kontrolü;
            var failedAttempts = await GetFailedAttemptCountAsync(username);
            if (failedAttempts >= _config.MaxFailedAttempts)
            {
                var lockoutTime = await GetLockoutTimeRemainingAsync(username);
                if (lockoutTime > TimeSpan.Zero)
                {
                    result.IsAllowed = false;
                    result.BlockReason = "Too many failed attempts";
                    result.RetryAfter = lockoutTime;
                    return result;
                }
            }

            return result;
        }

        private async Task<bool> VerifyCredentialsInternalAsync(
            string username,
            SecureString password,
            CancellationToken cancellationToken)
        {
            // 1. Veritabanından hash'lenmiş şifreyi al;
            var storedCredential = await _repository.GetCredentialHashAsync(username, cancellationToken);
            if (storedCredential == null)
            {
                // Timing attack koruması için sabit süre bekleyelim;
                await Task.Delay(_config.FixedValidationDelayMs, cancellationToken);
                return false;
            }

            // 2. Şifreyi hash'le;
            var passwordHash = await _cryptoEngine.ComputeHashAsync(
                password,
                storedCredential.Salt,
                cancellationToken);

            // 3. Timing attack korumalı hash karşılaştırması;
            var isValid = CryptographicOperations.FixedTimeEquals(
                passwordHash,
                storedCredential.PasswordHash);

            // 4. Hash güvenliği kontrolü (eski algoritmalar için)
            if (isValid && storedCredential.Algorithm != _config.PreferredHashAlgorithm)
            {
                // Hash'i güncel algoritmayla yeniden hesapla ve güncelle;
                await _repository.UpdatePasswordHashAsync(
                    username,
                    password,
                    _config.PreferredHashAlgorithm,
                    cancellationToken);
            }

            return isValid;
        }

        private async Task<MfaRequirement> DetermineMfaRequirementAsync(
            string username,
            ValidationContext context,
            CancellationToken cancellationToken)
        {
            var requirement = new MfaRequirement;
            {
                IsRequired = false,
                RequiredMethods = new List<MfaMethod>(),
                Priority = MfaPriority.Optional;
            };

            // 1. Kullanıcı bazlı MFA zorunluluğu;
            var userMfaSettings = await _repository.GetMfaSettingsAsync(username, cancellationToken);
            if (userMfaSettings.IsEnabled && userMfaSettings.IsMandatory)
            {
                requirement.IsRequired = true;
                requirement.RequiredMethods.AddRange(userMfaSettings.EnabledMethods);
                requirement.Priority = MfaPriority.Mandatory;
                return requirement;
            }

            // 2. Risk bazlı MFA zorunluluğu;
            if (context.RiskLevel >= _config.MfaRiskThreshold)
            {
                requirement.IsRequired = true;
                requirement.RequiredMethods.Add(MfaMethod.TOTP); // Default risk-based method;
                requirement.Priority = MfaPriority.RiskBased;
                return requirement;
            }

            // 3. Context bazlı MFA zorunluluğu;
            if (context.RequireMfa ||
                (!string.IsNullOrEmpty(context.IpAddress) && IsSuspiciousIp(context.IpAddress)) ||
                (!string.IsNullOrEmpty(context.DeviceId) && !context.IsTrustedDevice))
            {
                requirement.IsRequired = true;
                requirement.RequiredMethods.AddRange(_config.DefaultMfaMethods);
                requirement.Priority = MfaPriority.ContextBased;
                return requirement;
            }

            // 4. Policy bazlı MFA zorunluluğu;
            if (_config.AlwaysRequireMfaForAdmins && await _repository.IsAdminUserAsync(username, cancellationToken))
            {
                requirement.IsRequired = true;
                requirement.RequiredMethods.AddRange(_config.AdminMfaMethods);
                requirement.Priority = MfaPriority.PolicyBased;
                return requirement;
            }

            return requirement;
        }

        private PasswordRequirement CheckLengthRequirement(SecureString password)
        {
            var requirement = new PasswordRequirement;
            {
                Type = PasswordRequirementType.MinLength,
                Description = $"Password must be at least {_config.MinPasswordLength} characters",
                IsMandatory = true;
            };

            requirement.IsMet = password.Length >= _config.MinPasswordLength;
            requirement.Score = requirement.IsMet ? 25 : 0;

            if (_config.MaxPasswordLength > 0 && password.Length > _config.MaxPasswordLength)
            {
                requirement.Description = $"Password must be between {_config.MinPasswordLength} and {_config.MaxPasswordLength} characters";
                requirement.IsMet = false;
                requirement.Score = 0;
            }

            return requirement;
        }

        private List<PasswordRequirement> CheckComplexityRequirements(SecureString password)
        {
            var requirements = new List<PasswordRequirement>();
            var passwordString = password.ToUnsecureString();

            bool hasUpper = false, hasLower = false, hasDigit = false, hasSpecial = false;
            int charCategories = 0;

            foreach (char c in passwordString)
            {
                if (char.IsUpper(c)) hasUpper = true;
                if (char.IsLower(c)) hasLower = true;
                if (char.IsDigit(c)) hasDigit = true;
                if (char.IsSymbol(c) || char.IsPunctuation(c)) hasSpecial = true;

                // Unicode karakter kategorileri;
                var category = char.GetUnicodeCategory(c);
                if (category != System.Globalization.UnicodeCategory.UppercaseLetter &&
                    category != System.Globalization.UnicodeCategory.LowercaseLetter &&
                    category != System.Globalization.UnicodeCategory.DecimalDigitNumber)
                {
                    charCategories++;
                }
            }

            if (_config.RequireUppercase)
            {
                requirements.Add(new PasswordRequirement;
                {
                    Type = PasswordRequirementType.Uppercase,
                    Description = "At least one uppercase letter",
                    IsMet = hasUpper,
                    IsMandatory = _config.RequireUppercase,
                    Score = hasUpper ? 10 : 0;
                });
            }

            if (_config.RequireLowercase)
            {
                requirements.Add(new PasswordRequirement;
                {
                    Type = PasswordRequirementType.Lowercase,
                    Description = "At least one lowercase letter",
                    IsMet = hasLower,
                    IsMandatory = _config.RequireLowercase,
                    Score = hasLower ? 10 : 0;
                });
            }

            if (_config.RequireDigit)
            {
                requirements.Add(new PasswordRequirement;
                {
                    Type = PasswordRequirementType.Digit,
                    Description = "At least one digit",
                    IsMet = hasDigit,
                    IsMandatory = _config.RequireDigit,
                    Score = hasDigit ? 10 : 0;
                });
            }

            if (_config.RequireSpecialCharacter)
            {
                requirements.Add(new PasswordRequirement;
                {
                    Type = PasswordRequirementType.SpecialCharacter,
                    Description = "At least one special character",
                    IsMet = hasSpecial,
                    IsMandatory = _config.RequireSpecialCharacter,
                    Score = hasSpecial ? 15 : 0;
                });
            }

            // Unicode karakter çeşitliliği bonusu;
            if (charCategories >= 3)
            {
                requirements.Add(new PasswordRequirement;
                {
                    Type = PasswordRequirementType.CharacterDiversity,
                    Description = "Multiple character categories",
                    IsMet = true,
                    IsMandatory = false,
                    Score = 10,
                    IsBonus = true;
                });
            }

            return requirements;
        }

        private async Task RecordFailedAttemptAsync(string username, ValidationContext context)
        {
            await _lock.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                var cacheKey = $"FailedAttempts_{username}";

                if (!_failedAttempts.ContainsKey(username))
                {
                    _failedAttempts[username] = new FailedAttemptInfo;
                    {
                        Username = username,
                        FirstAttempt = now,
                        LastAttempt = now,
                        Attempts = new List<FailedAttempt>(),
                        LockoutUntil = null;
                    };
                }

                var info = _failedAttempts[username];
                info.Attempts.Add(new FailedAttempt;
                {
                    Timestamp = now,
                    IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent,
                    DeviceId = context.DeviceId,
                    Geolocation = context.Geolocation,
                    Reason = "Invalid credentials"
                });
                info.LastAttempt = now;
                info.TotalAttempts++;

                // Kilitleme kontrolü;
                if (info.Attempts.Count >= _config.MaxFailedAttempts)
                {
                    info.LockoutUntil = now.AddMinutes(_config.LockoutDurationMinutes);
                    info.IsLockedOut = true;

                    // Kilitleme olayını kaydet;
                    await _repository.RecordAccountLockoutAsync(
                        username,
                        info.LockoutUntil.Value,
                        "Too many failed attempts",
                        CancellationToken.None);
                }

                // Cache'e kaydet;
                _memoryCache.Set(cacheKey, info, TimeSpan.FromMinutes(_config.FailedAttemptWindowMinutes));

                // Veritabanına kaydet (uzun süreli depolama)
                await _repository.RecordFailedLoginAttemptAsync(
                    username,
                    context.IpAddress,
                    context.UserAgent,
                    context.DeviceId,
                    now,
                    CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task ClearFailedAttemptsAsync(string username)
        {
            await _lock.WaitAsync();
            try
            {
                _failedAttempts.Remove(username);
                var cacheKey = $"FailedAttempts_{username}";
                _memoryCache.Remove(cacheKey);

                await _repository.ClearFailedLoginAttemptsAsync(username, CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<int> GetRemainingAttemptsAsync(string username)
        {
            var cacheKey = $"FailedAttempts_{username}";
            if (_memoryCache.TryGetValue(cacheKey, out FailedAttemptInfo info))
            {
                var usedAttempts = info?.Attempts.Count ?? 0;
                return Math.Max(0, _config.MaxFailedAttempts - usedAttempts);
            }

            var dbAttempts = await _repository.GetRecentFailedAttemptCountAsync(
                username,
                DateTime.UtcNow.AddMinutes(-_config.FailedAttemptWindowMinutes),
                CancellationToken.None);

            return Math.Max(0, _config.MaxFailedAttempts - dbAttempts);
        }

        private async Task<TimeSpan> GetLockoutTimeRemainingAsync(string username)
        {
            var cacheKey = $"FailedAttempts_{username}";
            if (_memoryCache.TryGetValue(cacheKey, out FailedAttemptInfo info) && info?.LockoutUntil.HasValue == true)
            {
                var remaining = info.LockoutUntil.Value - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            var lockoutTime = await _repository.GetAccountLockoutRemainingAsync(username, CancellationToken.None);
            return lockoutTime ?? TimeSpan.Zero;
        }

        private void CleanupOldFailedAttempts()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-_config.FailedAttemptWindowMinutes);
            var keysToRemove = new List<string>();

            foreach (var kvp in _failedAttempts)
            {
                kvp.Value.Attempts.RemoveAll(a => a.Timestamp < cutoff);

                if (kvp.Value.Attempts.Count == 0 &&
                    (!kvp.Value.LockoutUntil.HasValue || kvp.Value.LockoutUntil < DateTime.UtcNow))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _failedAttempts.Remove(key);
                _memoryCache.Remove($"FailedAttempts_{key}");
            }
        }

        private bool IsSuspiciousIp(string ipAddress)
        {
            // IP blacklist, proxy/VPN tespiti, coğrafi anomaliler;
            // Gerçek implementasyonda IP intelligence servisi kullanılacak;
            return false;
        }

        private int CalculatePasswordScore(List<PasswordRequirement> requirements)
        {
            int score = 0;

            foreach (var requirement in requirements)
            {
                if (requirement.IsMet)
                {
                    score += requirement.Score;
                }
                else if (requirement.IsMandatory)
                {
                    score -= 10; // Zorunlu gereksinimler için ceza;
                }
            }

            // Bonus puanlar;
            if (requirements.Any(r => r.IsBonus && r.IsMet))
            {
                score += 20;
            }

            return Math.Max(0, Math.Min(100, score));
        }

        private PasswordStrength DeterminePasswordStrength(int score)
        {
            return score switch;
            {
                >= 90 => PasswordStrength.VeryStrong,
                >= 75 => PasswordStrength.Strong,
                >= 60 => PasswordStrength.Medium,
                >= 40 => PasswordStrength.Weak,
                _ => PasswordStrength.VeryWeak;
            };
        }

        private List<string> GeneratePasswordRecommendations(List<PasswordRequirement> requirements)
        {
            var recommendations = new List<string>();

            foreach (var requirement in requirements.Where(r => !r.IsMet && r.IsMandatory))
            {
                recommendations.Add(requirement.Description);
            }

            // Ek öneriler;
            if (!requirements.Any(r => r.Type == PasswordRequirementType.CharacterDiversity && r.IsMet))
            {
                recommendations.Add("Consider using characters from different Unicode categories");
            }

            if (!requirements.Any(r => r.Type == PasswordRequirementType.NotInDictionary && r.IsMet))
            {
                recommendations.Add("Avoid common words and phrases");
            }

            return recommendations;
        }

        #endregion;

        #region Stub Methods (Actual implementation would integrate with specific services)

        private async Task<bool> ValidateTotpAsync(string username, string code, CancellationToken cancellationToken)
        {
            var secret = await _repository.GetTotpSecretAsync(username, cancellationToken);
            if (string.IsNullOrEmpty(secret))
                return false;

            // TOTP doğrulama implementasyonu;
            await Task.Delay(10, cancellationToken); // Simulate work;
            return true;
        }

        private async Task<bool> ValidateSmsCodeAsync(string username, string code, CancellationToken cancellationToken)
        {
            var validCode = await _repository.GetValidSmsCodeAsync(username, cancellationToken);
            return validCode == code;
        }

        private async Task<bool> ValidateEmailCodeAsync(string username, string code, CancellationToken cancellationToken)
        {
            var validCode = await _repository.GetValidEmailCodeAsync(username, cancellationToken);
            return validCode == code;
        }

        private async Task<bool> ValidateBiometricAsync(string username, byte[] biometricData, CancellationToken cancellationToken)
        {
            var template = await _repository.GetBiometricTemplateAsync(username, cancellationToken);
            // Biyometrik eşleştirme algoritması;
            await Task.Delay(50, cancellationToken); // Simulate biometric matching;
            return true;
        }

        private async Task<bool> ValidateSecurityKeyAsync(string username, byte[] securityKeyResponse, CancellationToken cancellationToken)
        {
            var credential = await _repository.GetSecurityKeyCredentialAsync(username, cancellationToken);
            // WebAuthn/FIDO2 doğrulama;
            await Task.Delay(20, cancellationToken);
            return true;
        }

        private async Task<bool> ValidateBackupCodeAsync(string username, string code, CancellationToken cancellationToken)
        {
            var isValid = await _repository.ValidateBackupCodeAsync(username, code, cancellationToken);
            if (isValid)
            {
                await _repository.MarkBackupCodeUsedAsync(username, code, cancellationToken);
            }
            return isValid;
        }

        private async Task<BiometricMatchResult> PerformBiometricMatchingAsync(
            BiometricData inputData,
            byte[] storedTemplate,
            CancellationToken cancellationToken)
        {
            // Gerçek biyometrik eşleştirme algoritması;
            await Task.Delay(100, cancellationToken);
            return new BiometricMatchResult;
            {
                IsMatch = true,
                ConfidenceScore = 95.5f,
                MatchingAlgorithm = "DeepFace v3",
                ProcessingTime = TimeSpan.FromMilliseconds(100)
            };
        }

        private async Task<LivenessCheckResult> CheckLivenessAsync(BiometricData biometricData, CancellationToken cancellationToken)
        {
            // Canlılık tespiti;
            await Task.Delay(50, cancellationToken);
            return new LivenessCheckResult;
            {
                IsLive = true,
                Confidence = 98.2f,
                Method = "3DDepthAnalysis"
            };
        }

        private async Task<PasswordInfo> GetPasswordInfoAsync(string username, CancellationToken cancellationToken)
        {
            return await _repository.GetPasswordInfoAsync(username, cancellationToken);
        }

        private async Task<MfaStatus> GetMfaStatusAsync(string username, CancellationToken cancellationToken)
        {
            return await _repository.GetMfaStatusAsync(username, cancellationToken);
        }

        private async Task<BiometricStatus> GetBiometricStatusAsync(string username, CancellationToken cancellationToken)
        {
            return await _repository.GetBiometricStatusAsync(username, cancellationToken);
        }

        private async Task<FailedAttemptInfo> GetFailedAttemptInfoAsync(string username)
        {
            var cacheKey = $"FailedAttempts_{username}";
            if (_memoryCache.TryGetValue(cacheKey, out FailedAttemptInfo info))
            {
                return info;
            }

            return await _repository.GetFailedAttemptInfoAsync(username, CancellationToken.None);
        }

        private async Task<SessionStatus> GetSessionStatusAsync(string username, CancellationToken cancellationToken)
        {
            return await _repository.GetSessionStatusAsync(username, cancellationToken);
        }

        private async Task<List<RecentActivity>> GetRecentActivitiesAsync(string username, CancellationToken cancellationToken)
        {
            return await _repository.GetRecentActivitiesAsync(username, 10, cancellationToken);
        }

        private async Task<List<string>> GenerateSecurityRecommendationsAsync(
            CredentialSecurityStatus status,
            CancellationToken cancellationToken)
        {
            var recommendations = new List<string>();

            if (status.PasswordInfo.Age.TotalDays > 60)
            {
                recommendations.Add("Consider changing your password (last changed 60+ days ago)");
            }

            if (!status.MfaStatus.IsEnabled)
            {
                recommendations.Add("Enable multi-factor authentication for better security");
            }

            if (status.FailedAttempts.TotalAttempts > 0)
            {
                recommendations.Add($"Review {status.FailedAttempts.TotalAttempts} failed login attempts");
            }

            return recommendations;
        }

        private async Task<ComplianceStatus> CheckComplianceStatusAsync(
            CredentialSecurityStatus status,
            CancellationToken cancellationToken)
        {
            var compliance = new ComplianceStatus;
            {
                IsCompliant = true,
                Standards = new List<string> { "NIST-800-63B", "GDPR", "ISO27001" },
                Checks = new List<ComplianceCheck>()
            };

            // NIST kontrolü;
            compliance.Checks.Add(new ComplianceCheck;
            {
                Standard = "NIST-800-63B",
                Requirement = "Password complexity",
                IsMet = status.PasswordInfo.Strength >= PasswordStrength.Medium,
                Details = status.PasswordInfo.Strength.ToString()
            });

            // GDPR kontrolü;
            compliance.Checks.Add(new ComplianceCheck;
            {
                Standard = "GDPR",
                Requirement = "Data protection",
                IsMet = status.BiometricStatus?.IsEncrypted ?? false,
                Details = "Biometric data encryption"
            });

            compliance.IsCompliant = compliance.Checks.All(c => c.IsMet);

            return compliance;
        }

        private int CalculateSecurityScore(CredentialSecurityStatus status)
        {
            int score = 100;

            // Şifre gücü;
            score += status.PasswordInfo.Strength switch;
            {
                PasswordStrength.VeryStrong => 20,
                PasswordStrength.Strong => 15,
                PasswordStrength.Medium => 10,
                PasswordStrength.Weak => -10,
                PasswordStrength.VeryWeak => -20,
                _ => 0;
            };

            // MFA durumu;
            if (status.MfaStatus.IsEnabled)
            {
                score += status.MfaStatus.EnabledMethods.Count * 10;
            }

            // Biyometrik durum;
            if (status.BiometricStatus?.IsRegistered == true)
            {
                score += 15;
            }

            // Başarısız denemeler;
            score -= Math.Min(status.FailedAttempts.TotalAttempts * 2, 20);

            // Risk durumu;
            score -= status.RiskAssessment.RiskLevel switch;
            {
                RiskLevel.Critical => 30,
                RiskLevel.High => 20,
                RiskLevel.Medium => 10,
                _ => 0;
            };

            return Math.Max(0, Math.Min(100, score));
        }

        private async Task LogSuccessfulValidationAsync(
            string username,
            ValidationContext context,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            await _repository.LogAuthenticationEventAsync(
                username,
                "CredentialValidation",
                "Success",
                context.IpAddress,
                context.UserAgent,
                context.DeviceId,
                duration,
                cancellationToken);
        }

        private async Task LogFailedValidationAsync(
            string username,
            ValidationContext context,
            CancellationToken cancellationToken)
        {
            await _repository.LogAuthenticationEventAsync(
                username,
                "CredentialValidation",
                "Failure",
                context.IpAddress,
                context.UserAgent,
                context.DeviceId,
                TimeSpan.Zero,
                cancellationToken);
        }

        #endregion;

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
                    _cleanupTimer?.Dispose();
                    _lock?.Dispose();
                }

                _disposed = true;
            }
        }
    }

    #region Supporting Classes and Interfaces;

    public interface ICredentialValidator : IDisposable
    {
        Task<CredentialValidationResult> ValidateCredentialsAsync(
            string username,
            SecureString password,
            ValidationContext context = null,
            CancellationToken cancellationToken = default);

        Task<MfaValidationResult> ValidateMultiFactorAsync(
            string username,
            MultiFactorRequest request,
            ValidationContext context = null,
            CancellationToken cancellationToken = default);

        Task<BiometricValidationResult> ValidateBiometricAsync(
            string username,
            BiometricData biometricData,
            ValidationContext context = null,
            CancellationToken cancellationToken = default);

        Task<PasswordPolicyResult> ValidatePasswordPolicyAsync(
            SecureString password,
            string username = null,
            PasswordPolicyContext policyContext = null,
            CancellationToken cancellationToken = default);

        Task<CredentialSecurityStatus> GetCredentialSecurityStatusAsync(
            string username,
            CancellationToken cancellationToken = default);

        Task<PasswordChangeResult> ChangePasswordAsync(
            string username,
            SecureString currentPassword,
            SecureString newPassword,
            PasswordChangeContext context = null,
            CancellationToken cancellationToken = default);
    }

    public class CredentialValidationConfig;
    {
        // Password Policy;
        public int MinPasswordLength { get; set; } = 12;
        public int MaxPasswordLength { get; set; } = 128;
        public bool RequireUppercase { get; set; } = true;
        public bool RequireLowercase { get; set; } = true;
        public bool RequireDigit { get; set; } = true;
        public bool RequireSpecialCharacter { get; set; } = true;
        public int PasswordMaxAgeDays { get; set; } = 90;
        public int PasswordHistorySize { get; set; } = 10;
        public bool PreventUsernameSimilarity { get; set; } = true;
        public bool CheckDictionaryAttacks { get; set; } = true;
        public bool DetectCommonPatterns { get; set; } = true;
        public bool DetectTimeBasedPatterns { get; set; } = true;

        // Security;
        public int MaxFailedAttempts { get; set; } = 5;
        public int FailedAttemptWindowMinutes { get; set; } = 15;
        public int LockoutDurationMinutes { get; set; } = 30;
        public int FailedAttemptCleanupMinutes { get; set; } = 60;
        public int FixedValidationDelayMs { get; set; } = 100;
        public string PreferredHashAlgorithm { get; set; } = "Argon2id";

        // MFA;
        public bool AlwaysRequireMfaForAdmins { get; set; } = true;
        public RiskLevel MfaRiskThreshold { get; set; } = RiskLevel.Medium;
        public List<MfaMethod> DefaultMfaMethods { get; set; } = new() { MfaMethod.TOTP, MfaMethod.Email };
        public List<MfaMethod> AdminMfaMethods { get; set; } = new() { MfaMethod.TOTP, MfaMethod.SecurityKey };

        // Biometric;
        public float BiometricThreshold { get; set; } = 85.0f;
        public int MaxBiometricAttempts { get; set; } = 3;
        public int BiometricLockoutMinutes { get; set; } = 10;

        // Restrictions;
        public bool EnableIpRestrictions { get; set; } = false;
        public bool EnableTimeRestrictions { get; set; } = false;
        public bool EnableGeolocationRestrictions { get; set; } = false;
        public bool EnableDeviceRestrictions { get; set; } = true;

        // Session;
        public bool InvalidateSessionsOnPasswordChange { get; set; } = true;

        public static CredentialValidationConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var config = new CredentialValidationConfig();

            var section = configuration.GetSection("Security:CredentialValidation");
            if (section.Exists())
            {
                section.Bind(config);
            }

            return config;
        }
    }

    public class ValidationContext;
    {
        public string IpAddress { get; set; }
        public string UserAgent { get; set; }
        public string DeviceId { get; set; }
        public string Geolocation { get; set; }
        public bool IsTrustedDevice { get; set; }
        public bool RequireMfa { get; set; }
        public string Operation { get; set; } = "Authentication";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public RiskLevel RiskLevel { get; set; }
        public double RiskScore { get; set; }
    }

    public class PasswordPolicyContext;
    {
        public bool IsPasswordChange { get; set; }
        public bool IsAdminUser { get; set; }
        public string PolicyName { get; set; } = "Default";
        public Dictionary<string, object> CustomRules { get; set; } = new();
    }

    public class PasswordChangeContext;
    {
        public string ChangedBy { get; set; } = "User";
        public string Reason { get; set; } = "RegularChange";
        public bool ForceAllSessionsLogout { get; set; } = true;
        public bool NotifyUser { get; set; } = true;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class MultiFactorRequest;
    {
        public MfaMethod Method { get; set; }
        public string Code { get; set; }
        public byte[] BiometricData { get; set; }
        public byte[] SecurityKeyResponse { get; set; }
        public string SessionId { get; set; }
    }

    public class CredentialValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public int RemainingAttempts { get; set; }
        public TimeSpan? LockoutTimeRemaining { get; set; }
        public bool RequiresMfa { get; set; }
        public MfaRequirement MfaRequirement { get; set; }
        public bool IsPasswordExpired { get; set; }
        public SessionPolicy SessionPolicy { get; set; }
        public RiskLevel RiskLevel { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();

        public static CredentialValidationResult Success(
            bool requiresMfa = false,
            bool isPasswordExpired = false,
            SessionPolicy sessionPolicy = null,
            RiskLevel riskLevel = RiskLevel.Low)
        {
            return new CredentialValidationResult;
            {
                IsValid = true,
                RequiresMfa = requiresMfa,
                IsPasswordExpired = isPasswordExpired,
                SessionPolicy = sessionPolicy,
                RiskLevel = riskLevel;
            };
        }

        public static CredentialValidationResult Failure(
            string errorMessage,
            int remainingAttempts,
            TimeSpan? lockoutTimeRemaining = null,
            RiskLevel riskLevel = RiskLevel.Medium)
        {
            return new CredentialValidationResult;
            {
                IsValid = false,
                ErrorMessage = errorMessage,
                RemainingAttempts = remainingAttempts,
                LockoutTimeRemaining = lockoutTimeRemaining,
                RiskLevel = riskLevel;
            };
        }

        public static CredentialValidationResult Blocked(string reason, TimeSpan? retryAfter = null)
        {
            return new CredentialValidationResult;
            {
                IsValid = false,
                ErrorMessage = $"Authentication blocked: {reason}",
                LockoutTimeRemaining = retryAfter,
                RiskLevel = RiskLevel.High;
            };
        }
    }

    public class MfaValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public int RemainingAttempts { get; set; }
        public TimeSpan? LockoutTimeRemaining { get; set; }
        public string MfaToken { get; set; }
        public DateTime? TokenExpiry { get; set; }

        public static MfaValidationResult Success(string mfaToken)
        {
            return new MfaValidationResult;
            {
                IsValid = true,
                MfaToken = mfaToken,
                TokenExpiry = DateTime.UtcNow.AddHours(1)
            };
        }

        public static MfaValidationResult Failure(
            string errorMessage,
            int remainingAttempts,
            TimeSpan? lockoutTimeRemaining = null)
        {
            return new MfaValidationResult;
            {
                IsValid = false,
                ErrorMessage = errorMessage,
                RemainingAttempts = remainingAttempts,
                LockoutTimeRemaining = lockoutTimeRemaining;
            };
        }

        public static MfaValidationResult Blocked(string reason, TimeSpan? retryAfter = null)
        {
            return new MfaValidationResult;
            {
                IsValid = false,
                ErrorMessage = $"MFA blocked: {reason}",
                LockoutTimeRemaining = retryAfter;
            };
        }
    }

    public class BiometricValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public int RemainingAttempts { get; set; }
        public TimeSpan? LockoutTimeRemaining { get; set; }
        public string BiometricToken { get; set; }
        public float ConfidenceScore { get; set; }
        public bool IsLive { get; set; }
        public DateTime? TokenExpiry { get; set; }

        public static BiometricValidationResult Success(
            string biometricToken,
            float confidenceScore,
            bool isLive)
        {
            return new BiometricValidationResult;
            {
                IsValid = true,
                BiometricToken = biometricToken,
                ConfidenceScore = confidenceScore,
                IsLive = isLive,
                TokenExpiry = DateTime.UtcNow.AddHours(2)
            };
        }

        public static BiometricValidationResult Failure(
            string errorMessage,
            int remainingAttempts = 0,
            TimeSpan? lockoutTimeRemaining = null)
        {
            return new BiometricValidationResult;
            {
                IsValid = false,
                ErrorMessage = errorMessage,
                RemainingAttempts = remainingAttempts,
                LockoutTimeRemaining = lockoutTimeRemaining;
            };
        }

        public static BiometricValidationResult Blocked(string reason, TimeSpan? retryAfter = null)
        {
            return new BiometricValidationResult;
            {
                IsValid = false,
                ErrorMessage = $"Biometric blocked: {reason}",
                LockoutTimeRemaining = retryAfter;
            };
        }
    }

    public class PasswordPolicyResult;
    {
        public bool IsValid { get; set; }
        public List<PasswordRequirement> Requirements { get; set; } = new();
        public int MetRequirements { get; set; }
        public int TotalRequirements { get; set; }
        public int Score { get; set; }
        public PasswordStrength Strength { get; set; }
        public List<string> Recommendations { get; set; } = new();
        public Dictionary<string, object> Metrics { get; set; } = new();
    }

    public class PasswordRequirement;
    {
        public PasswordRequirementType Type { get; set; }
        public string Description { get; set; }
        public bool IsMet { get; set; }
        public bool IsMandatory { get; set; }
        public int Score { get; set; }
        public bool IsBonus { get; set; }
        public string Details { get; set; }
    }

    public class CredentialSecurityStatus;
    {
        public string Username { get; set; }
        public DateTime CheckedAt { get; set; }
        public PasswordInfo PasswordInfo { get; set; }
        public MfaStatus MfaStatus { get; set; }
        public BiometricStatus BiometricStatus { get; set; }
        public FailedAttemptInfo FailedAttempts { get; set; }
        public SessionStatus SessionStatus { get; set; }
        public RiskAssessment RiskAssessment { get; set; }
        public List<RecentActivity> RecentActivities { get; set; } = new();
        public List<string> SecurityRecommendations { get; set; } = new();
        public ComplianceStatus ComplianceStatus { get; set; }
        public int SecurityScore { get; set; }
    }

    public class PasswordChangeResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int? RemainingAttempts { get; set; }
        public TimeSpan? LockoutTimeRemaining { get; set; }
        public List<string> FailedRequirements { get; set; }
        public DateTime? NextRequiredChange { get; set; }
        public bool EnforceMfa { get; set; }
        public bool RequiresReauthentication { get; set; }
        public bool RequiresAdditionalVerification { get; set; }

        public static PasswordChangeResult Success(
            DateTime? nextRequiredChange = null,
            bool enforceMfa = false,
            bool requiresReauthentication = false)
        {
            return new PasswordChangeResult;
            {
                Success = true,
                NextRequiredChange = nextRequiredChange,
                EnforceMfa = enforceMfa,
                RequiresReauthentication = requiresReauthentication;
            };
        }

        public static PasswordChangeResult Failure(
            string errorMessage,
            int? remainingAttempts = null,
            TimeSpan? lockoutTimeRemaining = null,
            List<string> failedRequirements = null,
            bool requiresAdditionalVerification = false)
        {
            return new PasswordChangeResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                RemainingAttempts = remainingAttempts,
                LockoutTimeRemaining = lockoutTimeRemaining,
                FailedRequirements = failedRequirements,
                RequiresAdditionalVerification = requiresAdditionalVerification;
            };
        }
    }

    // Supporting internal classes;
    internal class SecurityCheckResult;
    {
        public bool IsAllowed { get; set; }
        public string BlockReason { get; set; }
        public TimeSpan? RetryAfter { get; set; }
    }

    internal class FailedAttemptInfo;
    {
        public string Username { get; set; }
        public DateTime FirstAttempt { get; set; }
        public DateTime LastAttempt { get; set; }
        public int TotalAttempts { get; set; }
        public List<FailedAttempt> Attempts { get; set; } = new();
        public DateTime? LockoutUntil { get; set; }
        public bool IsLockedOut { get; set; }
    }

    internal class FailedAttempt;
    {
        public DateTime Timestamp { get; set; }
        public string IpAddress { get; set; }
        public string UserAgent { get; set; }
        public string DeviceId { get; set; }
        public string Geolocation { get; set; }
        public string Reason { get; set; }
    }

    internal class BiometricMatchResult;
    {
        public bool IsMatch { get; set; }
        public float ConfidenceScore { get; set; }
        public string MatchingAlgorithm { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    internal class LivenessCheckResult;
    {
        public bool IsLive { get; set; }
        public float Confidence { get; set; }
        public string Method { get; set; }
    }

    #endregion;

    #region Data Models (partial)

    public enum PasswordRequirementType;
    {
        MinLength = 0,
        MaxLength = 1,
        Uppercase = 2,
        Lowercase = 3,
        Digit = 4,
        SpecialCharacter = 5,
        CharacterDiversity = 6,
        NotInDictionary = 7,
        NotSequential = 8,
        NotRepetitive = 9,
        NotSimilarToUsername = 10,
        NotInHistory = 11,
        Entropy = 12;
    }

    public enum PasswordStrength;
    {
        VeryWeak = 0,
        Weak = 1,
        Medium = 2,
        Strong = 3,
        VeryStrong = 4;
    }

    public enum MfaMethod;
    {
        TOTP = 0,
        SMS = 1,
        Email = 2,
        Biometric = 3,
        SecurityKey = 4,
        BackupCode = 5,
        PushNotification = 6;
    }

    public enum MfaPriority;
    {
        Optional = 0,
        Recommended = 1,
        ContextBased = 2,
        RiskBased = 3,
        PolicyBased = 4,
        Mandatory = 5;
    }

    public enum RiskLevel;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3;
    }

    public class MfaRequirement;
    {
        public bool IsRequired { get; set; }
        public List<MfaMethod> RequiredMethods { get; set; } = new();
        public MfaPriority Priority { get; set; }
        public DateTime? ValidUntil { get; set; }
    }

    public class SessionPolicy;
    {
        public TimeSpan MaxSessionDuration { get; set; }
        public TimeSpan InactivityTimeout { get; set; }
        public int MaxConcurrentSessions { get; set; }
        public bool ForceLogoutOnPasswordChange { get; set; }
    }

    // Diğer data model sınıfları (PasswordInfo, MfaStatus, BiometricStatus, vs.)
    // Proje yapısına uyumlu olacak şekilde kısaltıldı;

    #endregion;

    #region Repository Interfaces (Stubs)

    public interface ICredentialRepository;
    {
        Task<CredentialHashInfo> GetCredentialHashAsync(string username, CancellationToken cancellationToken);
        Task UpdatePasswordHashAsync(string username, SecureString password, string algorithm, CancellationToken cancellationToken);
        Task<bool> IsAccountLockedAsync(string username, CancellationToken cancellationToken);
        Task<TimeSpan?> GetAccountLockoutRemainingAsync(string username, CancellationToken cancellationToken);
        Task<bool> IsIpAllowedAsync(string username, string ipAddress, CancellationToken cancellationToken);
        Task<bool> IsLoginTimeAllowedAsync(string username, DateTime timestamp, CancellationToken cancellationToken);
        Task<bool> IsLocationAllowedAsync(string username, string geolocation, CancellationToken cancellationToken);
        Task<bool> IsDeviceAllowedAsync(string username, string deviceId, CancellationToken cancellationToken);
        Task RecordAccountLockoutAsync(string username, DateTime lockoutUntil, string reason, CancellationToken cancellationToken);
        Task RecordFailedLoginAttemptAsync(string username, string ipAddress, string userAgent, string deviceId, DateTime timestamp, CancellationToken cancellationToken);
        Task ClearFailedLoginAttemptsAsync(string username, CancellationToken cancellationToken);
        Task<int> GetRecentFailedAttemptCountAsync(string username, DateTime since, CancellationToken cancellationToken);
        Task<string> GetTotpSecretAsync(string username, CancellationToken cancellationToken);
        Task<string> GetValidSmsCodeAsync(string username, CancellationToken cancellationToken);
        Task<string> GetValidEmailCodeAsync(string username, CancellationToken cancellationToken);
        Task<byte[]> GetBiometricTemplateAsync(string username, CancellationToken cancellationToken);
        Task<byte[]> GetSecurityKeyCredentialAsync(string username, CancellationToken cancellationToken);
        Task<bool> ValidateBackupCodeAsync(string username, string code, CancellationToken cancellationToken);
        Task MarkBackupCodeUsedAsync(string username, string code, CancellationToken cancellationToken);
        Task<PasswordInfo> GetPasswordInfoAsync(string username, CancellationToken cancellationToken);
        Task<MfaStatus> GetMfaStatusAsync(string username, CancellationToken cancellationToken);
        Task<BiometricStatus> GetBiometricStatusAsync(string username, CancellationToken cancellationToken);
        Task<FailedAttemptInfo> GetFailedAttemptInfoAsync(string username, CancellationToken cancellationToken);
        Task<SessionStatus> GetSessionStatusAsync(string username, CancellationToken cancellationToken);
        Task<List<RecentActivity>> GetRecentActivitiesAsync(string username, int count, CancellationToken cancellationToken);
        Task LogAuthenticationEventAsync(string username, string eventType, string result, string ipAddress, string userAgent, string deviceId, TimeSpan duration, CancellationToken cancellationToken);
        Task InvalidateAllSessionsAsync(string username, CancellationToken cancellationToken);
        Task<MfaSettings> GetMfaSettingsAsync(string username, CancellationToken cancellationToken);
        Task<bool> IsAdminUserAsync(string username, CancellationToken cancellationToken);
        // Diğer methodlar...
    }

    public interface IRiskAnalyzer;
    {
        Task<RiskAssessment> AssessRiskAsync(string username, ValidationContext context, CancellationToken cancellationToken);
        Task RecordFailedAttemptAsync(string username, ValidationContext context, CancellationToken cancellationToken);
        Task RecordFailedMfaAsync(string username, ValidationContext context, CancellationToken cancellationToken);
        Task RecordFailedBiometricAsync(string username, ValidationContext context, CancellationToken cancellationToken);
        Task<RiskAssessment> GetRiskAssessmentAsync(string username, CancellationToken cancellationToken);
    }

    #endregion;

    #region Exceptions;

    public class CredentialValidationException : Exception
    {
        public CredentialValidationException(string message) : base(message) { }
        public CredentialValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PasswordPolicyException : CredentialValidationException;
    {
        public PasswordPolicyException(string message) : base(message) { }
    }

    public class AccountLockedException : CredentialValidationException;
    {
        public AccountLockedException(string username, DateTime lockoutUntil)
            : base($"Account {username} is locked until {lockoutUntil:yyyy-MM-dd HH:mm:ss}")
        {
            Username = username;
            LockoutUntil = lockoutUntil;
        }

        public string Username { get; }
        public DateTime LockoutUntil { get; }
    }

    #endregion;
}

// Extension method for SecureString;
internal static class SecureStringExtensions;
{
    public static string ToUnsecureString(this SecureString secureString)
    {
        if (secureString == null)
            return string.Empty;

        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(secureString);
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }
}
