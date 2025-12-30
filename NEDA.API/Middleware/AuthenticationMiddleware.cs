using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NEDA.API.Middleware;
{
    /// <summary>
    /// Authentication middleware for NEDA API;
    /// Handles JWT token validation, API key authentication, and user context creation;
    /// </summary>
    public class AuthenticationMiddleware;
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<AuthenticationMiddleware> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly AuthenticationSettings _authSettings;
        private readonly IUserContext _userContext;
        private readonly List<string> _publicEndpoints;

        public AuthenticationMiddleware(
            RequestDelegate next,
            ILogger<AuthenticationMiddleware> logger,
            ISecurityManager securityManager,
            IOptions<AuthenticationSettings> authSettings,
            IUserContext userContext)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _authSettings = authSettings?.Value ?? throw new ArgumentNullException(nameof(authSettings));
            _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));

            // Define public endpoints that don't require authentication;
            _publicEndpoints = new List<string>
            {
                "/api/auth/login",
                "/api/auth/register",
                "/api/auth/forgot-password",
                "/api/auth/reset-password",
                "/api/system/health",
                "/api/system/status",
                "/api/public/",
                "/swagger",
                "/favicon.ico"
            };
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var startTime = DateTime.UtcNow;
            var requestId = Guid.NewGuid().ToString();
            var correlationId = GetCorrelationId(context);

            try
            {
                // Set correlation ID in response headers;
                context.Response.Headers["X-Correlation-ID"] = correlationId;
                context.Response.Headers["X-Request-ID"] = requestId;

                // Check if endpoint is public;
                if (IsPublicEndpoint(context.Request.Path))
                {
                    _logger.LogDebug("Public endpoint accessed: {Path}", context.Request.Path);
                    await _next(context);
                    return;
                }

                // Extract authentication information;
                var authResult = await AuthenticateRequestAsync(context);

                if (!authResult.IsAuthenticated)
                {
                    _logger.LogWarning("Authentication failed for endpoint: {Path}", context.Request.Path);
                    await WriteErrorResponseAsync(context, HttpStatusCode.Unauthorized, "Authentication required");
                    return;
                }

                // Validate authorization if required;
                if (!await AuthorizeRequestAsync(context, authResult))
                {
                    _logger.LogWarning("Authorization failed for user {UserId} on endpoint {Path}",
                        authResult.UserId, context.Request.Path);
                    await WriteErrorResponseAsync(context, HttpStatusCode.Forbidden, "Insufficient permissions");
                    return;
                }

                // Set user context for downstream components;
                SetUserContext(context, authResult);

                // Log successful authentication;
                _logger.LogInformation("User {UserId} authenticated successfully for {Path}",
                    authResult.UserId, context.Request.Path);

                await _next(context);
            }
            catch (SecurityTokenExpiredException ex)
            {
                _logger.LogWarning(ex, "Token expired for request: {Path}", context.Request.Path);
                await WriteErrorResponseAsync(context, HttpStatusCode.Unauthorized, "Token expired",
                    new ErrorResponse;
                    {
                        ErrorCode = "TOKEN_EXPIRED",
                        Message = "Authentication token has expired",
                        RecoverySuggestion = "Refresh your token or re-authenticate"
                    });
            }
            catch (SecurityTokenValidationException ex)
            {
                _logger.LogWarning(ex, "Invalid token for request: {Path}", context.Request.Path);
                await WriteErrorResponseAsync(context, HttpStatusCode.Unauthorized, "Invalid token",
                    new ErrorResponse;
                    {
                        ErrorCode = "INVALID_TOKEN",
                        Message = "Authentication token is invalid",
                        RecoverySuggestion = "Provide a valid authentication token"
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Authentication error for request: {Path}", context.Request.Path);
                await WriteErrorResponseAsync(context, HttpStatusCode.InternalServerError, "Authentication error",
                    new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_ERROR",
                        Message = "An error occurred during authentication",
                        Details = new { OriginalError = ex.Message }
                    });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogDebug("Authentication middleware completed in {Duration}ms for {Path}",
                    duration.TotalMilliseconds, context.Request.Path);
            }
        }

        #region Authentication Methods;
        private async Task<AuthenticationResult> AuthenticateRequestAsync(HttpContext context)
        {
            // Try different authentication methods in order;
            var authResult = await TryJwtAuthenticationAsync(context) ??
                           await TryApiKeyAuthenticationAsync(context) ??
                           await TrySessionAuthenticationAsync(context);

            if (authResult == null)
            {
                return AuthenticationResult.Failed("No valid authentication method found");
            }

            // Validate token if it exists;
            if (!string.IsNullOrEmpty(authResult.Token))
            {
                var tokenValidation = await ValidateTokenAsync(authResult.Token);
                if (!tokenValidation.IsValid)
                {
                    return AuthenticationResult.Failed($"Token validation failed: {tokenValidation.Error}");
                }

                authResult.Claims = tokenValidation.Claims;
                authResult.UserId = tokenValidation.UserId;
            }

            // Verify user exists and is active;
            if (!string.IsNullOrEmpty(authResult.UserId))
            {
                var userStatus = await _securityManager.GetUserStatusAsync(authResult.UserId);
                if (!userStatus.IsActive)
                {
                    return AuthenticationResult.Failed($"User is not active: {userStatus.Status}");
                }
            }

            return authResult;
        }

        private async Task<AuthenticationResult> TryJwtAuthenticationAsync(HttpContext context)
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader))
            {
                return null;
            }

            if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var token = authHeader.Substring("Bearer ".Length).Trim();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            _logger.LogDebug("JWT authentication attempted for request: {Path}", context.Request.Path);

            return new AuthenticationResult;
            {
                IsAuthenticated = true,
                AuthenticationMethod = "JWT",
                Token = token,
                TokenType = "Bearer"
            };
        }

        private async Task<AuthenticationResult> TryApiKeyAuthenticationAsync(HttpContext context)
        {
            // Check for API key in headers;
            var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault() ??
                        context.Request.Headers["Api-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(apiKey))
            {
                // Check for API key in query string;
                apiKey = context.Request.Query["api_key"].FirstOrDefault();
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                return null;
            }

            _logger.LogDebug("API key authentication attempted for request: {Path}", context.Request.Path);

            // Validate API key;
            var apiKeyValidation = await _securityManager.ValidateApiKeyAsync(apiKey);
            if (!apiKeyValidation.IsValid)
            {
                return AuthenticationResult.Failed($"API key validation failed: {apiKeyValidation.Error}");
            }

            return new AuthenticationResult;
            {
                IsAuthenticated = true,
                AuthenticationMethod = "API Key",
                UserId = apiKeyValidation.UserId,
                ClientId = apiKeyValidation.ClientId,
                Permissions = apiKeyValidation.Permissions;
            };
        }

        private async Task<AuthenticationResult> TrySessionAuthenticationAsync(HttpContext context)
        {
            // Check for session token in cookies;
            var sessionToken = context.Request.Cookies["NEDA-Session"] ??
                              context.Request.Headers["X-Session-Token"].FirstOrDefault();

            if (string.IsNullOrEmpty(sessionToken))
            {
                return null;
            }

            _logger.LogDebug("Session authentication attempted for request: {Path}", context.Request.Path);

            // Validate session;
            var sessionValidation = await _securityManager.ValidateSessionAsync(sessionToken);
            if (!sessionValidation.IsValid)
            {
                return AuthenticationResult.Failed($"Session validation failed: {sessionValidation.Error}");
            }

            return new AuthenticationResult;
            {
                IsAuthenticated = true,
                AuthenticationMethod = "Session",
                Token = sessionToken,
                TokenType = "Session",
                UserId = sessionValidation.UserId,
                SessionId = sessionValidation.SessionId;
            };
        }

        private async Task<TokenValidationResult> ValidateTokenAsync(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var validationParameters = new TokenValidationParameters;
                {
                    ValidateIssuer = true,
                    ValidIssuer = _authSettings.JwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = _authSettings.JwtAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(_authSettings.JwtSecret))
                };

                var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
                var jwtToken = validatedToken as JwtSecurityToken;

                if (jwtToken == null)
                {
                    return TokenValidationResult.Failed("Invalid JWT token format");
                }

                // Extract claims;
                var claims = principal.Claims.ToList();
                var userId = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ??
                           claims.FirstOrDefault(c => c.Type == "sub")?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    return TokenValidationResult.Failed("User ID not found in token");
                }

                // Check token revocation;
                var isRevoked = await _securityManager.IsTokenRevokedAsync(token);
                if (isRevoked)
                {
                    return TokenValidationResult.Failed("Token has been revoked");
                }

                return TokenValidationResult.Success(userId, claims);
            }
            catch (SecurityTokenExpiredException ex)
            {
                _logger.LogWarning(ex, "Token expired during validation");
                return TokenValidationResult.Failed("Token has expired");
            }
            catch (SecurityTokenInvalidSignatureException ex)
            {
                _logger.LogWarning(ex, "Invalid token signature");
                return TokenValidationResult.Failed("Invalid token signature");
            }
            catch (SecurityTokenInvalidIssuerException ex)
            {
                _logger.LogWarning(ex, "Invalid token issuer");
                return TokenValidationResult.Failed("Invalid token issuer");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Token validation error");
                return TokenValidationResult.Failed($"Token validation error: {ex.Message}");
            }
        }
        #endregion;

        #region Authorization Methods;
        private async Task<bool> AuthorizeRequestAsync(HttpContext context, AuthenticationResult authResult)
        {
            // Skip authorization for public endpoints;
            if (IsPublicEndpoint(context.Request.Path))
            {
                return true;
            }

            // Check if endpoint requires specific permissions;
            var requiredPermissions = GetRequiredPermissions(context);
            if (!requiredPermissions.Any())
            {
                return true; // No specific permissions required;
            }

            // Check user permissions;
            foreach (var permission in requiredPermissions)
            {
                var hasPermission = await _securityManager.HasPermissionAsync(authResult.UserId, permission);
                if (!hasPermission)
                {
                    _logger.LogDebug("User {UserId} lacks required permission: {Permission}",
                        authResult.UserId, permission);
                    return false;
                }
            }

            // Check role-based access if applicable;
            var requiredRoles = GetRequiredRoles(context);
            if (requiredRoles.Any())
            {
                var userRoles = authResult.Claims?
                    .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                    .Select(c => c.Value)
                    .ToList() ?? new List<string>();

                if (!requiredRoles.Any(role => userRoles.Contains(role)))
                {
                    _logger.LogDebug("User {UserId} lacks required role. Has: {UserRoles}, Required: {RequiredRoles}",
                        authResult.UserId, string.Join(", ", userRoles), string.Join(", ", requiredRoles));
                    return false;
                }
            }

            // Check IP restrictions if configured;
            var clientIp = GetClientIpAddress(context);
            var isIpAllowed = await _securityManager.IsIpAllowedAsync(authResult.UserId, clientIp);
            if (!isIpAllowed)
            {
                _logger.LogWarning("IP restriction violation for user {UserId} from IP {ClientIp}",
                    authResult.UserId, clientIp);
                return false;
            }

            return true;
        }

        private List<string> GetRequiredPermissions(HttpContext context)
        {
            var permissions = new List<string>();

            // Extract from route data (could be set by attributes)
            var endpoint = context.GetEndpoint();
            if (endpoint?.Metadata != null)
            {
                // Check for permission attributes (this would be custom attributes in your project)
                // For now, we'll use a simple heuristic based on path;
                var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

                if (path.Contains("/admin/"))
                {
                    permissions.Add("Admin.Access");
                }

                if (path.Contains("/system/") && !path.Contains("/system/health"))
                {
                    permissions.Add("System.Access");
                }

                if (path.Contains("/projects/") && context.Request.Method != "GET")
                {
                    permissions.Add("Project.Write");
                }
            }

            return permissions;
        }

        private List<string> GetRequiredRoles(HttpContext context)
        {
            var roles = new List<string>();
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            // Simple role-based access control based on path patterns;
            if (path.Contains("/admin/"))
            {
                roles.Add("Administrator");
            }

            if (path.Contains("/system/") && !path.Contains("/system/health"))
            {
                roles.Add("SystemAdmin");
            }

            if (path.Contains("/audit/"))
            {
                roles.Add("Auditor");
            }

            return roles;
        }
        #endregion;

        #region Context Management;
        private void SetUserContext(HttpContext context, AuthenticationResult authResult)
        {
            // Create claims principal;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, authResult.UserId),
                new Claim("auth_method", authResult.AuthenticationMethod),
                new Claim("client_id", authResult.ClientId ?? ""),
                new Claim("session_id", authResult.SessionId ?? "")
            };

            // Add additional claims from token;
            if (authResult.Claims != null)
            {
                claims.AddRange(authResult.Claims);
            }

            // Add permissions as claims;
            if (authResult.Permissions != null)
            {
                foreach (var permission in authResult.Permissions)
                {
                    claims.Add(new Claim("permission", permission));
                }
            }

            var identity = new ClaimsIdentity(claims, "NEDA_Auth");
            var principal = new ClaimsPrincipal(identity);

            context.User = principal;

            // Set user context for services;
            _userContext.UserId = authResult.UserId;
            _userContext.Username = authResult.Claims?
                .FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? "Unknown";
            _userContext.Email = authResult.Claims?
                .FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            _userContext.Roles = authResult.Claims?
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                .Select(c => c.Value)
                .ToList() ?? new List<string>();
            _userContext.Permissions = authResult.Permissions ?? new List<string>();
            _userContext.ClientId = authResult.ClientId;
            _userContext.SessionId = authResult.SessionId;
            _userContext.AuthenticationMethod = authResult.AuthenticationMethod;
            _userContext.IpAddress = GetClientIpAddress(context);
            _userContext.UserAgent = context.Request.Headers["User-Agent"].ToString();
        }
        #endregion;

        #region Utility Methods;
        private bool IsPublicEndpoint(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var normalizedPath = path.ToLowerInvariant();

            return _publicEndpoints.Any(endpoint =>
                normalizedPath.StartsWith(endpoint.ToLowerInvariant()) ||
                normalizedPath == endpoint.ToLowerInvariant());
        }

        private string GetCorrelationId(HttpContext context)
        {
            return context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ??
                   context.Request.Headers["Correlation-ID"].FirstOrDefault() ??
                   Guid.NewGuid().ToString();
        }

        private string GetClientIpAddress(HttpContext context)
        {
            var ipAddress = context.Connection.RemoteIpAddress?.ToString();

            // Check for forwarded headers (behind proxy/load balancer)
            if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                ipAddress = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            }
            else if (context.Request.Headers.ContainsKey("X-Real-IP"))
            {
                ipAddress = context.Request.Headers["X-Real-IP"].ToString();
            }

            return ipAddress ?? "Unknown";
        }

        private async Task WriteErrorResponseAsync(HttpContext context, HttpStatusCode statusCode,
            string message, ErrorResponse errorResponse = null)
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = "application/json";

            var response = errorResponse ?? new ErrorResponse;
            {
                Success = false,
                ErrorCode = statusCode.ToString(),
                Message = message,
                Timestamp = DateTime.UtcNow,
                CorrelationId = GetCorrelationId(context)
            };

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false;
            };

            var json = JsonSerializer.Serialize(response, jsonOptions);
            await context.Response.WriteAsync(json);
        }
        #endregion;

        #region Supporting Classes;
        private class AuthenticationResult;
        {
            public bool IsAuthenticated { get; set; }
            public string AuthenticationMethod { get; set; }
            public string Token { get; set; }
            public string TokenType { get; set; }
            public string UserId { get; set; }
            public string ClientId { get; set; }
            public string SessionId { get; set; }
            public List<Claim> Claims { get; set; }
            public List<string> Permissions { get; set; }

            public static AuthenticationResult Failed(string reason) => new AuthenticationResult;
            {
                IsAuthenticated = false;
            };
        }

        private class TokenValidationResult;
        {
            public bool IsValid { get; set; }
            public string UserId { get; set; }
            public List<Claim> Claims { get; set; }
            public string Error { get; set; }

            public static TokenValidationResult Success(string userId, List<Claim> claims) => new TokenValidationResult;
            {
                IsValid = true,
                UserId = userId,
                Claims = claims;
            };

            public static TokenValidationResult Failed(string error) => new TokenValidationResult;
            {
                IsValid = false,
                Error = error;
            };
        }
        #endregion;
    }

    /// <summary>
    /// User context interface for accessing current user information;
    /// </summary>
    public interface IUserContext;
    {
        string UserId { get; set; }
        string Username { get; set; }
        string Email { get; set; }
        List<string> Roles { get; set; }
        List<string> Permissions { get; set; }
        string ClientId { get; set; }
        string SessionId { get; set; }
        string AuthenticationMethod { get; set; }
        string IpAddress { get; set; }
        string UserAgent { get; set; }
        DateTime AuthenticatedAt { get; set; }
    }

    /// <summary>
    /// Implementation of user context;
    /// </summary>
    public class UserContext : IUserContext;
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public List<string> Permissions { get; set; } = new List<string>();
        public string ClientId { get; set; }
        public string SessionId { get; set; }
        public string AuthenticationMethod { get; set; }
        public string IpAddress { get; set; }
        public string UserAgent { get; set; }
        public DateTime AuthenticatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Authentication settings configuration;
    /// </summary>
    public class AuthenticationSettings;
    {
        public string JwtSecret { get; set; }
        public string JwtIssuer { get; set; }
        public string JwtAudience { get; set; }
        public int TokenExpiryMinutes { get; set; } = 60;
        public int RefreshTokenExpiryDays { get; set; } = 7;
        public bool RequireHttps { get; set; } = true;
        public List<string> AllowedOrigins { get; set; } = new List<string>();
        public RateLimitSettings RateLimit { get; set; } = new RateLimitSettings();
    }

    public class RateLimitSettings;
    {
        public int RequestsPerMinute { get; set; } = 60;
        public int BurstLimit { get; set; } = 100;
        public bool Enabled { get; set; } = true;
    }
}
