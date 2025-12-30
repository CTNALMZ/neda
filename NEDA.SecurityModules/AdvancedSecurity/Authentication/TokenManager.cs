using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using Newtonsoft.Json;
using System.Security.Cryptography.X509Certificates;

namespace NEDA.SecurityModules.AdvancedSecurity.Authentication;
{
    /// <summary>
    /// Token types supported by the TokenManager;
    /// </summary>
    public enum TokenType;
    {
        AccessToken = 0,
        RefreshToken = 1,
        ApiKey = 2,
        SessionToken = 3,
        OneTimeToken = 4,
        ServiceToken = 5,
        DeviceToken = 6,
        CrossDomainToken = 7;
    }

    /// <summary>
    /// Token validation status;
    /// </summary>
    public enum TokenValidationStatus;
    {
        Valid = 0,
        Invalid = 1,
        Expired = 2,
        Revoked = 3,
        Suspended = 4,
        Tampered = 5,
        NotFound = 6,
        Renewed = 7;
    }

    /// <summary>
    /// Token security level;
    /// </summary>
    public enum TokenSecurityLevel;
    {
        Standard = 0,
        High = 1,
        Critical = 2,
        Military = 3;
    }

    /// <summary>
    /// Main TokenManager interface;
    /// </summary>
    public interface ITokenManager : IDisposable
    {
        /// <summary>
        /// Generate JWT token with specified claims and options;
        /// </summary>
        Task<TokenResponse> GenerateTokenAsync(TokenRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Validate token and extract claims;
        /// </summary>
        Task<TokenValidationResult> ValidateTokenAsync(string token, TokenValidationOptions options = null);

        /// <summary>
        /// Refresh expired access token using refresh token;
        /// </summary>
        Task<TokenResponse> RefreshTokenAsync(string refreshToken, RefreshTokenOptions options = null);

        /// <summary>
        /// Revoke token immediately;
        /// </summary>
        Task<bool> RevokeTokenAsync(string token, RevokeReason reason = RevokeReason.UserRequest);

        /// <summary>
        /// Get all active tokens for user;
        /// </summary>
        Task<IEnumerable<TokenInfo>> GetUserTokensAsync(string userId, bool includeRevoked = false);

        /// <summary>
        /// Get token statistics and metrics;
        /// </summary>
        Task<TokenStatistics> GetTokenStatisticsAsync(TimeSpan? timeframe = null);

        /// <summary>
        /// Rotate encryption keys (key rollover)
        /// </summary>
        Task<KeyRotationResult> RotateKeysAsync(KeyRotationOptions options);

        /// <summary>
        /// Generate API key for service-to-service authentication;
        /// </summary>
        Task<ApiKeyResponse> GenerateApiKeyAsync(ApiKeyRequest request);

        /// <summary>
        /// Validate API key and get associated permissions;
        /// </summary>
        Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string requiredScope = null);

        /// <summary>
        /// Issue one-time token for sensitive operations;
        /// </summary>
        Task<OneTimeToken> GenerateOneTimeTokenAsync(OneTimeTokenRequest request);

        /// <summary>
        /// Validate and consume one-time token;
        /// </summary>
        Task<bool> ValidateOneTimeTokenAsync(string token, string operationId);

        /// <summary>
        /// Perform token health check;
        /// </summary>
        Task<TokenHealthStatus> GetHealthStatusAsync();

        /// <summary>
        /// Clean up expired and revoked tokens;
        /// </summary>
        Task<CleanupResult> CleanupTokensAsync(CleanupOptions options = null);
    }

    /// <summary>
    /// Main TokenManager implementation with JWT and custom token support;
    /// </summary>
    public class TokenManager : ITokenManager;
    {
        private readonly ILogger<TokenManager> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IMemoryCache _memoryCache;
        private readonly TokenManagerConfig _config;
        private readonly TokenRepository _repository;
        private readonly JwtSecurityTokenHandler _jwtHandler;
        private readonly SemaphoreSlim _keyRotationLock = new SemaphoreSlim(1, 1);
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        // Key management;
        private readonly Dictionary<string, SecurityKey> _signingKeys;
        private readonly Dictionary<string, SecurityKey> _encryptionKeys;
        private string _currentSigningKeyId;
        private string _currentEncryptionKeyId;

        // Token validation parameters cache;
        private readonly MemoryCache<TokenType, TokenValidationParameters> _validationParamsCache;

        public TokenManager(
            ILogger<TokenManager> logger,
            IAuditLogger auditLogger,
            ICryptoEngine cryptoEngine,
            IMemoryCache memoryCache,
            IOptions<TokenManagerConfig> configOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));

            _repository = new TokenRepository();
            _jwtHandler = new JwtSecurityTokenHandler();
            _signingKeys = new Dictionary<string, SecurityKey>();
            _encryptionKeys = new Dictionary<string, SecurityKey>();
            _validationParamsCache = new MemoryCache<TokenType, TokenValidationParameters>(TimeSpan.FromMinutes(5));

            InitializeKeys();
            InitializeTokenHandlers();

            // Setup periodic cleanup;
            if (_config.EnableAutomaticCleanup)
            {
                _cleanupTimer = new Timer(
                    ExecuteCleanupAsync,
                    null,
                    TimeSpan.FromHours(_config.CleanupIntervalHours),
                    TimeSpan.FromHours(_config.CleanupIntervalHours));
            }

            _logger.LogInformation("TokenManager initialized with {KeyCount} signing keys", _signingKeys.Count);
        }

        private void InitializeKeys()
        {
            try
            {
                // Load signing keys from configuration;
                foreach (var keyConfig in _config.SigningKeys)
                {
                    var securityKey = LoadSecurityKey(keyConfig);
                    if (securityKey != null)
                    {
                        _signingKeys[keyConfig.KeyId] = securityKey;

                        if (keyConfig.IsDefault)
                        {
                            _currentSigningKeyId = keyConfig.KeyId;
                        }
                    }
                }

                // Load encryption keys;
                foreach (var keyConfig in _config.EncryptionKeys)
                {
                    var securityKey = LoadSecurityKey(keyConfig);
                    if (securityKey != null)
                    {
                        _encryptionKeys[keyConfig.KeyId] = securityKey;

                        if (keyConfig.IsDefault)
                        {
                            _currentEncryptionKeyId = keyConfig.KeyId;
                        }
                    }
                }

                // If no default key specified, use first one;
                if (string.IsNullOrEmpty(_currentSigningKeyId) && _signingKeys.Any())
                {
                    _currentSigningKeyId = _signingKeys.Keys.First();
                }

                if (string.IsNullOrEmpty(_currentEncryptionKeyId) && _encryptionKeys.Any())
                {
                    _currentEncryptionKeyId = _encryptionKeys.Keys.First();
                }

                _logger.LogInformation("Loaded {SigningKeyCount} signing keys and {EncryptionKeyCount} encryption keys",
                    _signingKeys.Count, _encryptionKeys.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize security keys");
                throw new TokenException("Failed to initialize security keys", ex);
            }
        }

        private SecurityKey LoadSecurityKey(KeyConfig keyConfig)
        {
            try
            {
                switch (keyConfig.KeyType)
                {
                    case KeyType.RSA:
                        var rsa = RSA.Create();
                        if (keyConfig.KeySource == KeySource.File)
                        {
                            var pemContent = System.IO.File.ReadAllText(keyConfig.KeyPath);
                            rsa.ImportFromPem(pemContent);
                        }
                        else if (keyConfig.KeySource == KeySource.Xml)
                        {
                            rsa.FromXmlString(keyConfig.KeyData);
                        }
                        return new RsaSecurityKey(rsa);

                    case KeyType.ECDsa:
                        var ecdsa = ECDsa.Create();
                        // Load ECDsa key based on configuration;
                        return new ECDsaSecurityKey(ecdsa);

                    case KeyType.Symmetric:
                        var keyBytes = Encoding.UTF8.GetBytes(keyConfig.KeyData);
                        return new SymmetricSecurityKey(keyBytes);

                    case KeyType.Certificate:
                        var cert = new X509Certificate2(keyConfig.KeyPath, keyConfig.Password);
                        return new X509SecurityKey(cert);

                    default:
                        throw new NotSupportedException($"Key type {keyConfig.KeyType} not supported");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load security key: {KeyId}", keyConfig.KeyId);
                return null;
            }
        }

        private void InitializeTokenHandlers()
        {
            // Configure JWT handler;
            _jwtHandler.InboundClaimTypeMap.Clear();
            _jwtHandler.OutboundClaimTypeMap.Clear();

            // Register custom token handlers;
            TokenHandlerFactory.RegisterHandler(TokenType.AccessToken, new JwtTokenHandler(_jwtHandler, this));
            TokenHandlerFactory.RegisterHandler(TokenType.RefreshToken, new RefreshTokenHandler(this));
            TokenHandlerFactory.RegisterHandler(TokenType.ApiKey, new ApiKeyHandler(this));
            TokenHandlerFactory.RegisterHandler(TokenType.OneTimeToken, new OneTimeTokenHandler(this));
            TokenHandlerFactory.RegisterHandler(TokenType.SessionToken, new SessionTokenHandler(this));
        }

        public async Task<TokenResponse> GenerateTokenAsync(
            TokenRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogDebug("Generating token for user: {UserId}, type: {TokenType}",
                    request.UserId, request.TokenType);

                // Validate request;
                var validationResult = ValidateTokenRequest(request);
                if (!validationResult.IsValid)
                {
                    throw new TokenValidationException($"Invalid token request: {string.Join(", ", validationResult.Errors)}");
                }

                // Get appropriate token handler;
                var handler = TokenHandlerFactory.GetHandler(request.TokenType);
                if (handler == null)
                {
                    throw new TokenException($"No handler registered for token type: {request.TokenType}");
                }

                // Generate token;
                var tokenResult = await handler.GenerateTokenAsync(request, cancellationToken);

                // Store token in repository;
                var tokenInfo = new TokenInfo;
                {
                    Id = Guid.NewGuid(),
                    Token = tokenResult.Token,
                    TokenHash = ComputeTokenHash(tokenResult.Token),
                    TokenType = request.TokenType,
                    UserId = request.UserId,
                    ClientId = request.ClientId,
                    IssuedAt = DateTime.UtcNow,
                    ExpiresAt = tokenResult.ExpiresAt,
                    SecurityLevel = request.SecurityLevel,
                    Claims = request.Claims.ToDictionary(c => c.Type, c => c.Value),
                    Metadata = request.Metadata,
                    IsRevoked = false;
                };

                await _repository.StoreTokenAsync(tokenInfo);

                // Log token generation;
                await _auditLogger.LogTokenGeneratedAsync(new TokenAuditRecord;
                {
                    TokenId = tokenInfo.Id,
                    UserId = request.UserId,
                    TokenType = request.TokenType,
                    ClientId = request.ClientId,
                    IssuedAt = tokenInfo.IssuedAt,
                    ExpiresAt = tokenInfo.ExpiresAt,
                    SecurityLevel = request.SecurityLevel;
                });

                // Cache token if configured;
                if (_config.EnableTokenCaching && tokenResult.ExpiresAt.HasValue)
                {
                    await CacheTokenAsync(tokenInfo);
                }

                _logger.LogInformation("Generated {TokenType} token for user: {UserId}, Expires: {ExpiresAt}",
                    request.TokenType, request.UserId, tokenResult.ExpiresAt);

                return tokenResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate token for user: {UserId}", request.UserId);
                throw new TokenException("Token generation failed", ex);
            }
        }

        public async Task<TokenValidationResult> ValidateTokenAsync(
            string token,
            TokenValidationOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token cannot be empty", nameof(token));

            options ??= new TokenValidationOptions();

            try
            {
                var startTime = DateTime.UtcNow;

                // Check token blacklist;
                if (await IsTokenBlacklistedAsync(token))
                {
                    _logger.LogWarning("Token validation failed: token is blacklisted");
                    return TokenValidationResult.Blacklisted();
                }

                // Check cache for validation result;
                var cacheKey = $"token_validation_{ComputeTokenHash(token)}";
                if (_memoryCache.TryGetValue(cacheKey, out TokenValidationResult cachedResult))
                {
                    _logger.LogDebug("Using cached token validation result");
                    return cachedResult;
                }

                // Determine token type;
                var tokenType = DetectTokenType(token);

                // Get appropriate handler;
                var handler = TokenHandlerFactory.GetHandler(tokenType);
                if (handler == null)
                {
                    _logger.LogWarning("No handler found for token type: {TokenType}", tokenType);
                    return TokenValidationResult.Invalid($"Unsupported token type: {tokenType}");
                }

                // Validate token;
                var validationResult = await handler.ValidateTokenAsync(token, options);

                // Check if token exists in repository;
                var tokenInfo = await _repository.GetTokenByHashAsync(ComputeTokenHash(token));
                if (tokenInfo != null)
                {
                    // Update validation result with token info;
                    validationResult.TokenInfo = tokenInfo;

                    // Check if token is revoked;
                    if (tokenInfo.IsRevoked)
                    {
                        validationResult.Status = TokenValidationStatus.Revoked;
                        validationResult.IsValid = false;
                        validationResult.Error = "Token has been revoked";
                    }

                    // Check expiration;
                    if (tokenInfo.ExpiresAt.HasValue && tokenInfo.ExpiresAt < DateTime.UtcNow)
                    {
                        validationResult.Status = TokenValidationStatus.Expired;
                        validationResult.IsValid = false;
                        validationResult.Error = "Token has expired";
                    }
                }

                // Cache validation result if token is valid;
                if (validationResult.IsValid && validationResult.ExpiresAt.HasValue)
                {
                    var cacheDuration = validationResult.ExpiresAt.Value - DateTime.UtcNow;
                    if (cacheDuration > TimeSpan.Zero)
                    {
                        _memoryCache.Set(cacheKey, validationResult, cacheDuration);
                    }
                }

                // Log validation;
                await _auditLogger.LogTokenValidationAsync(new ValidationAuditRecord;
                {
                    TokenHash = ComputeTokenHash(token),
                    ValidationTime = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - startTime,
                    Status = validationResult.Status,
                    UserId = validationResult.Claims?.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value,
                    ClientId = validationResult.Claims?.FirstOrDefault(c => c.Type == "client_id")?.Value;
                });

                _logger.LogDebug("Token validation completed: {Status}, Duration: {Duration}ms",
                    validationResult.Status, (DateTime.UtcNow - startTime).TotalMilliseconds);

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token validation failed");
                return TokenValidationResult.Invalid($"Validation error: {ex.Message}");
            }
        }

        public async Task<TokenResponse> RefreshTokenAsync(
            string refreshToken,
            RefreshTokenOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new ArgumentException("Refresh token cannot be empty", nameof(refreshToken));

            options ??= new RefreshTokenOptions();

            await using var transaction = await _repository.BeginTransactionAsync();
            try
            {
                // Validate refresh token;
                var validationResult = await ValidateTokenAsync(refreshToken, new TokenValidationOptions;
                {
                    ValidateLifetime = true,
                    ValidateIssuer = true,
                    ValidateAudience = true;
                });

                if (!validationResult.IsValid)
                {
                    throw new TokenValidationException($"Invalid refresh token: {validationResult.Error}");
                }

                // Check if refresh token is allowed for refresh;
                if (validationResult.TokenInfo?.TokenType != TokenType.RefreshToken)
                {
                    throw new TokenValidationException("Token is not a refresh token");
                }

                // Check refresh token usage limits;
                if (validationResult.TokenInfo.RefreshCount >= _config.MaxRefreshTokenUsage)
                {
                    await RevokeTokenAsync(refreshToken, RevokeReason.MaxUsageExceeded);
                    throw new TokenValidationException("Refresh token usage limit exceeded");
                }

                var userId = validationResult.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
                var clientId = validationResult.Claims.FirstOrDefault(c => c.Type == "client_id")?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    throw new TokenValidationException("User ID not found in token claims");
                }

                // Generate new access token;
                var tokenRequest = new TokenRequest;
                {
                    UserId = userId,
                    ClientId = clientId,
                    TokenType = TokenType.AccessToken,
                    Claims = validationResult.Claims.Select(c => new Claim(c.Type, c.Value)).ToList(),
                    Metadata = options.Metadata,
                    SecurityLevel = options.SecurityLevel ?? validationResult.TokenInfo.SecurityLevel;
                };

                // Set expiration based on options;
                if (options.AccessTokenLifetime.HasValue)
                {
                    tokenRequest.Lifetime = options.AccessTokenLifetime.Value;
                }

                var newToken = await GenerateTokenAsync(tokenRequest);

                // Update refresh token usage count;
                validationResult.TokenInfo.RefreshCount++;
                validationResult.TokenInfo.LastRefreshedAt = DateTime.UtcNow;
                await _repository.UpdateTokenAsync(validationResult.TokenInfo);

                // Revoke old access tokens if configured;
                if (options.RevokeOldAccessTokens)
                {
                    await RevokeOldAccessTokensAsync(userId, clientId);
                }

                await transaction.CommitAsync();

                _logger.LogInformation("Refreshed token for user: {UserId}, New token expires: {ExpiresAt}",
                    userId, newToken.ExpiresAt);

                return newToken;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Token refresh failed");
                throw new TokenException("Token refresh failed", ex);
            }
        }

        public async Task<bool> RevokeTokenAsync(string token, RevokeReason reason = RevokeReason.UserRequest)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token cannot be empty", nameof(token));

            try
            {
                var tokenHash = ComputeTokenHash(token);
                var tokenInfo = await _repository.GetTokenByHashAsync(tokenHash);

                if (tokenInfo == null)
                {
                    _logger.LogWarning("Token not found for revocation: {TokenHash}", tokenHash);
                    return false;
                }

                // Mark token as revoked;
                tokenInfo.IsRevoked = true;
                tokenInfo.RevokedAt = DateTime.UtcNow;
                tokenInfo.RevokeReason = reason;
                tokenInfo.RevokedBy = "System"; // In production, this would be the user/admin;

                await _repository.UpdateTokenAsync(tokenInfo);

                // Add to blacklist if configured;
                if (_config.EnableTokenBlacklist)
                {
                    await BlacklistTokenAsync(tokenHash, tokenInfo.ExpiresAt);
                }

                // Clear from cache;
                var cacheKey = $"token_validation_{tokenHash}";
                _memoryCache.Remove(cacheKey);

                // Log revocation;
                await _auditLogger.LogTokenRevokedAsync(new RevocationAuditRecord;
                {
                    TokenId = tokenInfo.Id,
                    UserId = tokenInfo.UserId,
                    TokenType = tokenInfo.TokenType,
                    RevokedAt = tokenInfo.RevokedAt.Value,
                    Reason = reason,
                    RevokedBy = tokenInfo.RevokedBy;
                });

                _logger.LogInformation("Token revoked: {TokenId}, Reason: {Reason}, User: {UserId}",
                    tokenInfo.Id, reason, tokenInfo.UserId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke token");
                return false;
            }
        }

        public async Task<IEnumerable<TokenInfo>> GetUserTokensAsync(string userId, bool includeRevoked = false)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            try
            {
                var tokens = await _repository.GetUserTokensAsync(userId, includeRevoked);

                // Filter out token values for security;
                foreach (var token in tokens)
                {
                    token.Token = "[REDACTED]";
                }

                return tokens;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user tokens for: {UserId}", userId);
                throw new TokenException("Failed to retrieve user tokens", ex);
            }
        }

        public async Task<TokenStatistics> GetTokenStatisticsAsync(TimeSpan? timeframe = null)
        {
            try
            {
                var startTime = timeframe.HasValue;
                    ? DateTime.UtcNow.Subtract(timeframe.Value)
                    : DateTime.UtcNow.AddDays(-1);

                var stats = new TokenStatistics;
                {
                    GeneratedAt = DateTime.UtcNow,
                    TimeframeStart = startTime,
                    TimeframeEnd = DateTime.UtcNow;
                };

                // Get statistics from repository;
                var tokenStats = await _repository.GetTokenStatisticsAsync(startTime);

                stats.TotalTokens = tokenStats.TotalTokens;
                stats.ActiveTokens = tokenStats.ActiveTokens;
                stats.ExpiredTokens = tokenStats.ExpiredTokens;
                stats.RevokedTokens = tokenStats.RevokedTokens;

                // Calculate token types distribution;
                stats.TokenTypeDistribution = await _repository.GetTokenTypeDistributionAsync(startTime);

                // Calculate issue/revocation rates;
                stats.IssuanceRate = await CalculateIssuanceRateAsync(startTime);
                stats.RevocationRate = await CalculateRevocationRateAsync(startTime);

                // Get security level distribution;
                stats.SecurityLevelDistribution = await _repository.GetSecurityLevelDistributionAsync(startTime);

                // Calculate average token lifetime;
                stats.AverageTokenLifetime = await CalculateAverageTokenLifetimeAsync(startTime);

                // Identify top users by token count;
                stats.TopUsersByTokenCount = await _repository.GetTopUsersByTokenCountAsync(startTime, 10);

                _logger.LogDebug("Generated token statistics: {TotalTokens} total tokens, {ActiveTokens} active",
                    stats.TotalTokens, stats.ActiveTokens);

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get token statistics");
                throw new TokenException("Failed to get token statistics", ex);
            }
        }

        public async Task<KeyRotationResult> RotateKeysAsync(KeyRotationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            await _keyRotationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Starting key rotation for type: {KeyType}", options.KeyType);

                var result = new KeyRotationResult;
                {
                    RotationId = Guid.NewGuid(),
                    KeyType = options.KeyType,
                    StartedAt = DateTime.UtcNow;
                };

                switch (options.KeyType)
                {
                    case KeyRotationType.SigningKey:
                        result = await RotateSigningKeysAsync(options, result);
                        break;

                    case KeyRotationType.EncryptionKey:
                        result = await RotateEncryptionKeysAsync(options, result);
                        break;

                    case KeyRotationType.Both:
                        var signingResult = await RotateSigningKeysAsync(options, result);
                        var encryptionResult = await RotateEncryptionKeysAsync(options, result);

                        result.Success = signingResult.Success && encryptionResult.Success;
                        result.Operations.AddRange(signingResult.Operations);
                        result.Operations.AddRange(encryptionResult.Operations);
                        break;
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Log key rotation;
                await _auditLogger.LogKeyRotationAsync(new KeyRotationAuditRecord;
                {
                    RotationId = result.RotationId,
                    KeyType = options.KeyType,
                    Success = result.Success,
                    StartedAt = result.StartedAt,
                    CompletedAt = result.CompletedAt,
                    Operations = result.Operations.Select(op => op.ToString()).ToList()
                });

                if (result.Success)
                {
                    _logger.LogInformation("Key rotation completed successfully: {RotationId}", result.RotationId);
                }
                else;
                {
                    _logger.LogWarning("Key rotation completed with errors: {RotationId}", result.RotationId);
                }

                return result;
            }
            finally
            {
                _keyRotationLock.Release();
            }
        }

        public async Task<ApiKeyResponse> GenerateApiKeyAsync(ApiKeyRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogDebug("Generating API key for service: {ServiceName}", request.ServiceName);

                // Generate cryptographically secure API key;
                var apiKey = GenerateSecureApiKey(request.KeyLength);
                var keyHash = ComputeTokenHash(apiKey);

                // Create API key info;
                var apiKeyInfo = new ApiKeyInfo;
                {
                    Id = Guid.NewGuid(),
                    ApiKeyHash = keyHash,
                    ServiceName = request.ServiceName,
                    Description = request.Description,
                    OwnerUserId = request.OwnerUserId,
                    Permissions = request.Permissions,
                    Scopes = request.Scopes,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = request.ExpiresAt,
                    IsActive = true,
                    LastUsedAt = null,
                    UsageCount = 0,
                    Metadata = request.Metadata;
                };

                // Store API key;
                await _repository.StoreApiKeyAsync(apiKeyInfo);

                // Log API key generation;
                await _auditLogger.LogApiKeyGeneratedAsync(new ApiKeyAuditRecord;
                {
                    ApiKeyId = apiKeyInfo.Id,
                    ServiceName = request.ServiceName,
                    OwnerUserId = request.OwnerUserId,
                    CreatedAt = apiKeyInfo.CreatedAt,
                    ExpiresAt = apiKeyInfo.ExpiresAt;
                });

                _logger.LogInformation("Generated API key for service: {ServiceName}, ID: {ApiKeyId}",
                    request.ServiceName, apiKeyInfo.Id);

                return new ApiKeyResponse;
                {
                    ApiKey = apiKey,
                    ApiKeyId = apiKeyInfo.Id,
                    CreatedAt = apiKeyInfo.CreatedAt,
                    ExpiresAt = apiKeyInfo.ExpiresAt,
                    Permissions = apiKeyInfo.Permissions,
                    Scopes = apiKeyInfo.Scopes;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate API key for service: {ServiceName}", request.ServiceName);
                throw new TokenException("API key generation failed", ex);
            }
        }

        public async Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string requiredScope = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key cannot be empty", nameof(apiKey));

            try
            {
                var keyHash = ComputeTokenHash(apiKey);
                var apiKeyInfo = await _repository.GetApiKeyByHashAsync(keyHash);

                if (apiKeyInfo == null)
                {
                    return ApiKeyValidationResult.Invalid("API key not found");
                }

                // Check if API key is active;
                if (!apiKeyInfo.IsActive)
                {
                    return ApiKeyValidationResult.Invalid("API key is inactive");
                }

                // Check expiration;
                if (apiKeyInfo.ExpiresAt.HasValue && apiKeyInfo.ExpiresAt < DateTime.UtcNow)
                {
                    await DeactivateApiKeyAsync(apiKeyInfo.Id, "Expired");
                    return ApiKeyValidationResult.Invalid("API key has expired");
                }

                // Check required scope;
                if (!string.IsNullOrEmpty(requiredScope) &&
                    !apiKeyInfo.Scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
                {
                    return ApiKeyValidationResult.Invalid($"Required scope '{requiredScope}' not granted");
                }

                // Update usage statistics;
                apiKeyInfo.LastUsedAt = DateTime.UtcNow;
                apiKeyInfo.UsageCount++;
                await _repository.UpdateApiKeyAsync(apiKeyInfo);

                // Log API key usage;
                await _auditLogger.LogApiKeyUsedAsync(new ApiKeyUsageAuditRecord;
                {
                    ApiKeyId = apiKeyInfo.Id,
                    ServiceName = apiKeyInfo.ServiceName,
                    UsedAt = DateTime.UtcNow,
                    Scope = requiredScope;
                });

                return ApiKeyValidationResult.Valid(apiKeyInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API key validation failed");
                return ApiKeyValidationResult.Invalid($"Validation error: {ex.Message}");
            }
        }

        public async Task<OneTimeToken> GenerateOneTimeTokenAsync(OneTimeTokenRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogDebug("Generating one-time token for operation: {OperationId}", request.OperationId);

                // Generate token;
                var token = GenerateSecureToken(32);
                var tokenHash = ComputeTokenHash(token);

                // Create token info;
                var tokenInfo = new OneTimeTokenInfo;
                {
                    Id = Guid.NewGuid(),
                    TokenHash = tokenHash,
                    OperationId = request.OperationId,
                    UserId = request.UserId,
                    Purpose = request.Purpose,
                    Data = request.Data,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(request.ExpiresAfter),
                    IsUsed = false,
                    MaxUsageCount = request.MaxUsageCount,
                    CurrentUsageCount = 0;
                };

                // Store token;
                await _repository.StoreOneTimeTokenAsync(tokenInfo);

                _logger.LogInformation("Generated one-time token for operation: {OperationId}, Expires: {ExpiresAt}",
                    request.OperationId, tokenInfo.ExpiresAt);

                return new OneTimeToken;
                {
                    Token = token,
                    TokenId = tokenInfo.Id,
                    ExpiresAt = tokenInfo.ExpiresAt,
                    OperationId = tokenInfo.OperationId;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate one-time token for operation: {OperationId}",
                    request.OperationId);
                throw new TokenException("One-time token generation failed", ex);
            }
        }

        public async Task<bool> ValidateOneTimeTokenAsync(string token, string operationId)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token cannot be empty", nameof(token));

            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation ID cannot be empty", nameof(operationId));

            try
            {
                var tokenHash = ComputeTokenHash(token);
                var tokenInfo = await _repository.GetOneTimeTokenByHashAsync(tokenHash, operationId);

                if (tokenInfo == null)
                {
                    _logger.LogWarning("One-time token not found for operation: {OperationId}", operationId);
                    return false;
                }

                // Check if already used;
                if (tokenInfo.IsUsed)
                {
                    _logger.LogWarning("One-time token already used for operation: {OperationId}", operationId);
                    return false;
                }

                // Check expiration;
                if (tokenInfo.ExpiresAt < DateTime.UtcNow)
                {
                    _logger.LogWarning("One-time token expired for operation: {OperationId}", operationId);
                    return false;
                }

                // Check usage count;
                tokenInfo.CurrentUsageCount++;
                if (tokenInfo.CurrentUsageCount >= tokenInfo.MaxUsageCount)
                {
                    tokenInfo.IsUsed = true;
                }

                await _repository.UpdateOneTimeTokenAsync(tokenInfo);

                _logger.LogInformation("Validated one-time token for operation: {OperationId}, Usage: {CurrentUsage}/{MaxUsage}",
                    operationId, tokenInfo.CurrentUsageCount, tokenInfo.MaxUsageCount);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate one-time token for operation: {OperationId}", operationId);
                return false;
            }
        }

        public async Task<TokenHealthStatus> GetHealthStatusAsync()
        {
            try
            {
                var status = new TokenHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ServiceStatus = ServiceStatus.Healthy;
                };

                // Check key availability;
                status.SigningKeysHealthy = _signingKeys.Count > 0;
                status.EncryptionKeysHealthy = _encryptionKeys.Count > 0;

                // Check repository connection;
                status.RepositoryHealthy = await _repository.CheckHealthAsync();

                // Check cache health;
                status.CacheHealthy = _memoryCache is MemoryCache;

                // Calculate token statistics;
                var stats = await GetTokenStatisticsAsync(TimeSpan.FromHours(1));

                status.TokensIssuedLastHour = stats.IssuanceRate;
                status.TokensRevokedLastHour = stats.RevocationRate;
                status.ActiveTokens = stats.ActiveTokens;

                // Check for anomalies;
                status.Anomalies = await DetectAnomaliesAsync(stats);

                // Determine overall status;
                if (!status.SigningKeysHealthy || !status.EncryptionKeysHealthy || !status.RepositoryHealthy)
                {
                    status.ServiceStatus = ServiceStatus.Critical;
                }
                else if (status.Anomalies.Any())
                {
                    status.ServiceStatus = ServiceStatus.Degraded;
                }

                return status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get token health status");
                return new TokenHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ServiceStatus = ServiceStatus.Unhealthy,
                    HealthCheckError = ex.Message;
                };
            }
        }

        public async Task<CleanupResult> CleanupTokensAsync(CleanupOptions options = null)
        {
            options ??= new CleanupOptions();

            try
            {
                _logger.LogInformation("Starting token cleanup with options: {Options}", JsonConvert.SerializeObject(options));

                var result = new CleanupResult;
                {
                    StartedAt = DateTime.UtcNow;
                };

                // Cleanup expired tokens;
                if (options.CleanupExpiredTokens)
                {
                    result.ExpiredTokensRemoved = await _repository.RemoveExpiredTokensAsync(options.ExpiredTokensOlderThan);
                }

                // Cleanup revoked tokens;
                if (options.CleanupRevokedTokens)
                {
                    result.RevokedTokensRemoved = await _repository.RemoveRevokedTokensAsync(options.RevokedTokensOlderThan);
                }

                // Cleanup old audit logs;
                if (options.CleanupAuditLogs)
                {
                    result.AuditLogsRemoved = await _repository.CleanupAuditLogsAsync(options.AuditLogsOlderThan);
                }

                // Cleanup unused API keys;
                if (options.CleanupUnusedApiKeys)
                {
                    result.UnusedApiKeysRemoved = await _repository.RemoveUnusedApiKeysAsync(options.ApiKeyUnusedFor);
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                _logger.LogInformation("Token cleanup completed: {Expired} expired, {Revoked} revoked, {ApiKeys} API keys removed",
                    result.ExpiredTokensRemoved, result.RevokedTokensRemoved, result.UnusedApiKeysRemoved);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token cleanup failed");
                throw new TokenException("Token cleanup failed", ex);
            }
        }

        #region Private Helper Methods;

        private async Task<KeyRotationResult> RotateSigningKeysAsync(KeyRotationOptions options, KeyRotationResult result)
        {
            try
            {
                var newKeyId = $"signing_key_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var newKey = GenerateNewSigningKey(options.Algorithm, options.KeySize);

                // Add new key;
                _signingKeys[newKeyId] = newKey;

                // Update current key ID;
                var oldKeyId = _currentSigningKeyId;
                _currentSigningKeyId = newKeyId;

                // Keep old key for token validation during grace period;
                if (options.KeepOldKeysForGracePeriod && !string.IsNullOrEmpty(oldKeyId))
                {
                    // Schedule old key removal;
                    _ = Task.Delay(options.GracePeriod).ContinueWith(async _ =>
                    {
                        await RemoveOldKeyAsync(oldKeyId, KeyRotationType.SigningKey);
                    });
                }
                else if (!string.IsNullOrEmpty(oldKeyId))
                {
                    // Remove old key immediately;
                    _signingKeys.Remove(oldKeyId);
                }

                // Clear validation cache;
                _validationParamsCache.Clear();

                result.Operations.Add($"Added new signing key: {newKeyId}");
                result.Operations.Add($"Set current signing key to: {newKeyId}");

                if (!string.IsNullOrEmpty(oldKeyId))
                {
                    result.Operations.Add($"Removed old signing key: {oldKeyId}");
                }

                result.Success = true;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rotate signing keys");
                result.Success = false;
                result.Error = ex.Message;
                return result;
            }
        }

        private async Task<KeyRotationResult> RotateEncryptionKeysAsync(KeyRotationOptions options, KeyRotationResult result)
        {
            try
            {
                var newKeyId = $"encryption_key_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var newKey = GenerateNewEncryptionKey(options.Algorithm, options.KeySize);

                // Add new key;
                _encryptionKeys[newKeyId] = newKey;

                // Update current key ID;
                var oldKeyId = _currentEncryptionKeyId;
                _currentEncryptionKeyId = newKeyId;

                // Keep old key for decryption during grace period;
                if (options.KeepOldKeysForGracePeriod && !string.IsNullOrEmpty(oldKeyId))
                {
                    // Schedule old key removal;
                    _ = Task.Delay(options.GracePeriod).ContinueWith(async _ =>
                    {
                        await RemoveOldKeyAsync(oldKeyId, KeyRotationType.EncryptionKey);
                    });
                }
                else if (!string.IsNullOrEmpty(oldKeyId))
                {
                    // Remove old key immediately;
                    _encryptionKeys.Remove(oldKeyId);
                }

                result.Operations.Add($"Added new encryption key: {newKeyId}");
                result.Operations.Add($"Set current encryption key to: {newKeyId}");

                if (!string.IsNullOrEmpty(oldKeyId))
                {
                    result.Operations.Add($"Removed old encryption key: {oldKeyId}");
                }

                result.Success = true;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rotate encryption keys");
                result.Success = false;
                result.Error = ex.Message;
                return result;
            }
        }

        private async Task RemoveOldKeyAsync(string keyId, KeyRotationType keyType)
        {
            try
            {
                switch (keyType)
                {
                    case KeyRotationType.SigningKey:
                        _signingKeys.Remove(keyId);
                        break;
                    case KeyRotationType.EncryptionKey:
                        _encryptionKeys.Remove(keyId);
                        break;
                }

                _logger.LogInformation("Removed old {KeyType} key: {KeyId} after grace period", keyType, keyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove old key: {KeyId}", keyId);
            }
        }

        private ValidationResult ValidateTokenRequest(TokenRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.UserId))
                errors.Add("User ID is required");

            if (request.TokenType == TokenType.Unknown)
                errors.Add("Valid token type is required");

            if (request.Lifetime <= TimeSpan.Zero)
                errors.Add("Token lifetime must be positive");

            if (request.SecurityLevel < TokenSecurityLevel.Standard || request.SecurityLevel > TokenSecurityLevel.Military)
                errors.Add("Invalid security level");

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private TokenType DetectTokenType(string token)
        {
            // Check if it's a JWT;
            if (token.Count(c => c == '.') == 2)
            {
                try
                {
                    var jwtToken = new JwtSecurityToken(token);
                    var tokenTypeClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "token_type")?.Value;

                    if (Enum.TryParse<TokenType>(tokenTypeClaim, out var type))
                    {
                        return type;
                    }

                    // Default to AccessToken for JWTs;
                    return TokenType.AccessToken;
                }
                catch
                {
                    // Not a valid JWT;
                }
            }

            // Check API key format (typically base64)
            if (token.Length == 32 || token.Length == 64 || token.Length == 128)
            {
                try
                {
                    Convert.FromBase64String(token);
                    return TokenType.ApiKey;
                }
                catch
                {
                    // Not base64;
                }
            }

            // Default to SessionToken for unknown formats;
            return TokenType.SessionToken;
        }

        private string ComputeTokenHash(string token)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
            return Convert.ToBase64String(hashBytes);
        }

        private async Task<bool> IsTokenBlacklistedAsync(string token)
        {
            if (!_config.EnableTokenBlacklist)
                return false;

            var tokenHash = ComputeTokenHash(token);
            return await _repository.IsTokenBlacklistedAsync(tokenHash);
        }

        private async Task BlacklistTokenAsync(string tokenHash, DateTime? expiresAt)
        {
            if (!_config.EnableTokenBlacklist)
                return;

            var blacklistEntry = new TokenBlacklistEntry
            {
                TokenHash = tokenHash,
                BlacklistedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt ?? DateTime.UtcNow.Add(_config.BlacklistDuration)
            };

            await _repository.AddToBlacklistAsync(blacklistEntry);
        }

        private async Task CacheTokenAsync(TokenInfo tokenInfo)
        {
            var cacheKey = $"token_{tokenInfo.Id}";
            var cacheDuration = tokenInfo.ExpiresAt.HasValue;
                ? tokenInfo.ExpiresAt.Value - DateTime.UtcNow;
                : TimeSpan.FromMinutes(5);

            if (cacheDuration > TimeSpan.Zero)
            {
                _memoryCache.Set(cacheKey, tokenInfo, cacheDuration);
            }
        }

        private async Task RevokeOldAccessTokensAsync(string userId, string clientId)
        {
            try
            {
                var oldTokens = await _repository.GetUserAccessTokensAsync(userId, clientId, includeActive: true);
                var tokensToRevoke = oldTokens;
                    .Where(t => t.TokenType == TokenType.AccessToken && !t.IsRevoked)
                    .OrderByDescending(t => t.IssuedAt)
                    .Skip(1) // Keep the most recent one;
                    .ToList();

                foreach (var token in tokensToRevoke)
                {
                    await RevokeTokenAsync(token.Token, RevokeReason.Refreshed);
                }

                _logger.LogDebug("Revoked {Count} old access tokens for user: {UserId}", tokensToRevoke.Count, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke old access tokens for user: {UserId}", userId);
            }
        }

        private async Task<double> CalculateIssuanceRateAsync(DateTime startTime)
        {
            var tokensIssued = await _repository.GetTokensIssuedCountAsync(startTime, DateTime.UtcNow);
            var hours = (DateTime.UtcNow - startTime).TotalHours;

            return hours > 0 ? tokensIssued / hours : 0;
        }

        private async Task<double> CalculateRevocationRateAsync(DateTime startTime)
        {
            var tokensRevoked = await _repository.GetTokensRevokedCountAsync(startTime, DateTime.UtcNow);
            var hours = (DateTime.UtcNow - startTime).TotalHours;

            return hours > 0 ? tokensRevoked / hours : 0;
        }

        private async Task<TimeSpan> CalculateAverageTokenLifetimeAsync(DateTime startTime)
        {
            var averageLifetime = await _repository.GetAverageTokenLifetimeAsync(startTime);
            return TimeSpan.FromSeconds(averageLifetime);
        }

        private async Task<List<string>> DetectAnomaliesAsync(TokenStatistics stats)
        {
            var anomalies = new List<string>();

            // Check for unusual issuance rate;
            var historicalAvgRate = await _repository.GetHistoricalIssuanceRateAsync(TimeSpan.FromDays(7));
            if (stats.IssuanceRate > historicalAvgRate * 2)
            {
                anomalies.Add($"Unusually high token issuance rate: {stats.IssuanceRate:F2}/hour");
            }

            // Check for unusual revocation rate;
            var historicalRevocationRate = await _repository.GetHistoricalRevocationRateAsync(TimeSpan.FromDays(7));
            if (stats.RevocationRate > historicalRevocationRate * 2)
            {
                anomalies.Add($"Unusually high token revocation rate: {stats.RevocationRate:F2}/hour");
            }

            // Check for too many active tokens;
            var avgActiveTokens = await _repository.GetAverageActiveTokensAsync(TimeSpan.FromDays(7));
            if (stats.ActiveTokens > avgActiveTokens * 1.5)
            {
                anomalies.Add($"Unusually high number of active tokens: {stats.ActiveTokens}");
            }

            return anomalies;
        }

        private async Task DeactivateApiKeyAsync(Guid apiKeyId, string reason)
        {
            try
            {
                var apiKeyInfo = await _repository.GetApiKeyByIdAsync(apiKeyId);
                if (apiKeyInfo != null)
                {
                    apiKeyInfo.IsActive = false;
                    apiKeyInfo.DeactivatedAt = DateTime.UtcNow;
                    apiKeyInfo.DeactivationReason = reason;

                    await _repository.UpdateApiKeyAsync(apiKeyInfo);

                    _logger.LogInformation("Deactivated API key: {ApiKeyId}, Reason: {Reason}", apiKeyId, reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate API key: {ApiKeyId}", apiKeyId);
            }
        }

        private string GenerateSecureApiKey(int length)
        {
            const string validChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[length];
            rng.GetBytes(bytes);

            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = validChars[bytes[i] % validChars.Length];
            }

            return new string(chars);
        }

        private string GenerateSecureToken(int length)
        {
            var bytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
        }

        private SecurityKey GenerateNewSigningKey(string algorithm, int keySize)
        {
            return algorithm.ToUpperInvariant() switch;
            {
                "RSA" => new RsaSecurityKey(RSA.Create(keySize)),
                "ECDSA" => new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)),
                _ => new SymmetricSecurityKey(GenerateRandomBytes(keySize / 8))
            };
        }

        private SecurityKey GenerateNewEncryptionKey(string algorithm, int keySize)
        {
            return algorithm.ToUpperInvariant() switch;
            {
                "RSA" => new RsaSecurityKey(RSA.Create(keySize)),
                _ => new SymmetricSecurityKey(GenerateRandomBytes(keySize / 8))
            };
        }

        private byte[] GenerateRandomBytes(int length)
        {
            var bytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return bytes;
        }

        private async void ExecuteCleanupAsync(object state)
        {
            try
            {
                _logger.LogDebug("Running scheduled token cleanup");

                var result = await CleanupTokensAsync(new CleanupOptions;
                {
                    CleanupExpiredTokens = true,
                    ExpiredTokensOlderThan = TimeSpan.FromDays(1),
                    CleanupRevokedTokens = true,
                    RevokedTokensOlderThan = TimeSpan.FromDays(7),
                    CleanupAuditLogs = true,
                    AuditLogsOlderThan = TimeSpan.FromDays(30),
                    CleanupUnusedApiKeys = true,
                    ApiKeyUnusedFor = TimeSpan.FromDays(90)
                });

                _logger.LogInformation("Scheduled cleanup completed: {Result}",
                    JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled token cleanup failed");
            }
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
                    _cleanupTimer?.Dispose();
                    _keyRotationLock?.Dispose();
                    _validationParamsCache?.Dispose();

                    foreach (var key in _signingKeys.Values)
                    {
                        if (key is IDisposable disposableKey)
                        {
                            disposableKey.Dispose();
                        }
                    }

                    foreach (var key in _encryptionKeys.Values)
                    {
                        if (key is IDisposable disposableKey)
                        {
                            disposableKey.Dispose();
                        }
                    }

                    _logger.LogInformation("TokenManager disposed");
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

    #region Supporting Types and Enums;

    public enum KeyRotationType;
    {
        SigningKey,
        EncryptionKey,
        Both;
    }

    public enum RevokeReason;
    {
        UserRequest,
        SecurityBreach,
        PasswordChange,
        AccountCompromised,
        SuspiciousActivity,
        MaxUsageExceeded,
        Refreshed,
        AdminAction,
        SystemMaintenance;
    }

    public enum ServiceStatus;
    {
        Healthy,
        Degraded,
        Unhealthy,
        Critical;
    }

    public class TokenRequest;
    {
        public string UserId { get; set; }
        public string ClientId { get; set; }
        public TokenType TokenType { get; set; }
        public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);
        public List<Claim> Claims { get; set; } = new List<Claim>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public TokenSecurityLevel SecurityLevel { get; set; } = TokenSecurityLevel.Standard;
        public string Audience { get; set; }
        public string Issuer { get; set; }
        public List<string> Scopes { get; set; } = new List<string>();
    }

    public class TokenResponse;
    {
        public string Token { get; set; }
        public string RefreshToken { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? RefreshTokenExpiresAt { get; set; }
        public TokenType TokenType { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class TokenValidationOptions;
    {
        public bool ValidateLifetime { get; set; } = true;
        public bool ValidateIssuer { get; set; } = true;
        public bool ValidateAudience { get; set; } = true;
        public bool ValidateIssuerSigningKey { get; set; } = true;
        public string ValidIssuer { get; set; }
        public string ValidAudience { get; set; }
        public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);
        public List<string> RequiredClaims { get; set; } = new List<string>();
        public List<string> RequiredScopes { get; set; } = new List<string>();
    }

    public class TokenValidationResult;
    {
        public bool IsValid { get; set; }
        public TokenValidationStatus Status { get; set; }
        public string Error { get; set; }
        public IEnumerable<Claim> Claims { get; set; } = new List<Claim>();
        public TokenInfo TokenInfo { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? IssuedAt { get; set; }

        public static TokenValidationResult Valid(IEnumerable<Claim> claims, DateTime? expiresAt = null)
        {
            return new TokenValidationResult;
            {
                IsValid = true,
                Status = TokenValidationStatus.Valid,
                Claims = claims,
                ExpiresAt = expiresAt;
            };
        }

        public static TokenValidationResult Invalid(string error)
        {
            return new TokenValidationResult;
            {
                IsValid = false,
                Status = TokenValidationStatus.Invalid,
                Error = error;
            };
        }

        public static TokenValidationResult Blacklisted()
        {
            return new TokenValidationResult;
            {
                IsValid = false,
                Status = TokenValidationStatus.Revoked,
                Error = "Token is blacklisted"
            };
        }
    }

    // Additional supporting classes would be defined here...
    // (TokenInfo, TokenStatistics, KeyRotationResult, ApiKeyRequest, etc.)

    #endregion;
}
