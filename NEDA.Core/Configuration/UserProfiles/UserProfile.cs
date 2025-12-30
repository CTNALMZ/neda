using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common.Constants;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Security;

namespace NEDA.Core.Configuration.UserProfiles
{
    /// <summary>
    /// Represents a user profile with preferences, settings, and security information;
    /// </summary>
    [DataContract]
    public class UserProfile : INotifyPropertyChanged, IDisposable
    {
        private readonly ILogger<UserProfile> _logger;
        private string _userId;
        private string _username;
        private string _displayName;
        private string _email;
        private UserRole _role;
        private UserPreferences _preferences;
        private SecurityContext _securityContext;
        private ProfileStatus _status;
        private DateTime _createdDate;
        private DateTime _lastModifiedDate;
        private DateTime _lastLoginDate;
        private int _loginCount;
        private Dictionary<string, object> _customProperties;
        private List<ProfileActivity> _recentActivities;
        private bool _isDirty;
        private bool _disposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// Unique identifier for the user;
        /// </summary>
        [DataMember(Name = "userId")]
        [Required(ErrorMessage = "User ID is required")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "User ID must be between 3 and 50 characters")]
        [RegularExpression(@"^[a-zA-Z0-9_\-\.]+$", ErrorMessage = "User ID can only contain letters, numbers, underscores, hyphens, and periods")]
        public string UserId
        {
            get => _userId;
            set
            {
                if (_userId != value)
                {
                    _userId = value;
                    OnPropertyChanged(nameof(UserId));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Username for authentication;
        /// </summary>
        [DataMember(Name = "username")]
        [Required(ErrorMessage = "Username is required")]
        [StringLength(100, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 100 characters")]
        public string Username
        {
            get => _username;
            set
            {
                if (_username != value)
                {
                    _username = value;
                    OnPropertyChanged(nameof(Username));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Display name shown in UI;
        /// </summary>
        [DataMember(Name = "displayName")]
        [StringLength(150, ErrorMessage = "Display name cannot exceed 150 characters")]
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged(nameof(DisplayName));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// User's email address;
        /// </summary>
        [DataMember(Name = "email")]
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address format")]
        [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters")]
        public string Email
        {
            get => _email;
            set
            {
                if (_email != value)
                {
                    _email = value;
                    OnPropertyChanged(nameof(Email));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// User's role in the system;
        /// </summary>
        [DataMember(Name = "role")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public UserRole Role
        {
            get => _role;
            set
            {
                if (_role != value)
                {
                    _role = value;
                    OnPropertyChanged(nameof(Role));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// User preferences and settings;
        /// </summary>
        [DataMember(Name = "preferences")]
        public UserPreferences Preferences
        {
            get => _preferences;
            set
            {
                if (_preferences != value)
                {
                    _preferences = value;
                    OnPropertyChanged(nameof(Preferences));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Security context for the user;
        /// </summary>
        [DataMember(Name = "securityContext")]
        public SecurityContext SecurityContext
        {
            get => _securityContext;
            set
            {
                if (_securityContext != value)
                {
                    _securityContext = value;
                    OnPropertyChanged(nameof(SecurityContext));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Current status of the user profile;
        /// </summary>
        [DataMember(Name = "status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ProfileStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Date when the profile was created;
        /// </summary>
        [DataMember(Name = "createdDate")]
        public DateTime CreatedDate
        {
            get => _createdDate;
            set
            {
                if (_createdDate != value)
                {
                    _createdDate = value;
                    OnPropertyChanged(nameof(CreatedDate));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Date when the profile was last modified;
        /// </summary>
        [DataMember(Name = "lastModifiedDate")]
        public DateTime LastModifiedDate
        {
            get => _lastModifiedDate;
            set
            {
                if (_lastModifiedDate != value)
                {
                    _lastModifiedDate = value;
                    OnPropertyChanged(nameof(LastModifiedDate));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Date when the user last logged in;
        /// </summary>
        [DataMember(Name = "lastLoginDate")]
        public DateTime LastLoginDate
        {
            get => _lastLoginDate;
            set
            {
                if (_lastLoginDate != value)
                {
                    _lastLoginDate = value;
                    OnPropertyChanged(nameof(LastLoginDate));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Number of successful logins;
        /// </summary>
        [DataMember(Name = "loginCount")]
        public int LoginCount
        {
            get => _loginCount;
            set
            {
                if (_loginCount != value)
                {
                    _loginCount = value;
                    OnPropertyChanged(nameof(LoginCount));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Custom properties for extensibility;
        /// </summary>
        [DataMember(Name = "customProperties")]
        public Dictionary<string, object> CustomProperties
        {
            get => _customProperties;
            set
            {
                if (_customProperties != value)
                {
                    _customProperties = value;
                    OnPropertyChanged(nameof(CustomProperties));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Recent activities performed by the user;
        /// </summary>
        [DataMember(Name = "recentActivities")]
        public List<ProfileActivity> RecentActivities
        {
            get => _recentActivities;
            set
            {
                if (_recentActivities != value)
                {
                    _recentActivities = value;
                    OnPropertyChanged(nameof(RecentActivities));
                    MarkDirty();
                }
            }
        }

        /// <summary>
        /// Gets whether the profile has unsaved changes;
        /// </summary>
        [JsonIgnore]
        public bool IsDirty => _isDirty;

        /// <summary>
        /// Event raised when a property changes;
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Initializes a new instance of UserProfile;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        public UserProfile(ILogger<UserProfile> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Initialize();
        }

        /// <summary>
        /// Initializes a new instance of UserProfile with user data;
        /// </summary>
        public UserProfile(string userId, string username, string email, ILogger<UserProfile> logger)
            : this(logger)
        {
            Guard.AgainstNullOrEmpty(userId, nameof(userId));
            Guard.AgainstNullOrEmpty(username, nameof(username));
            Guard.AgainstNullOrEmpty(email, nameof(email));

            _userId = userId;
            _username = username;
            _email = email;
            _displayName = username;
            _createdDate = DateTime.UtcNow;
            _lastModifiedDate = DateTime.UtcNow;
            _status = ProfileStatus.Active;
            _role = UserRole.User;

            _logger.LogDebug("UserProfile created for user: {UserId}", userId);
        }

        /// <summary>
        /// Initializes profile properties;
        /// </summary>
        private void Initialize()
        {
            _preferences = new UserPreferences();
            _securityContext = new SecurityContext();
            _customProperties = new Dictionary<string, object>();
            _recentActivities = new List<ProfileActivity>();
            _status = ProfileStatus.Pending;
            _role = UserRole.Guest;
            _createdDate = DateTime.UtcNow;
            _lastModifiedDate = DateTime.UtcNow;
            _isDirty = false;

            _userId = string.Empty;
            _username = string.Empty;
            _displayName = string.Empty;
            _email = string.Empty;
        }

        /// <summary>
        /// Validates the user profile;
        /// </summary>
        /// <returns>Validation result</returns>
        public ProfileValidationResult Validate()
        {
            var result = new ProfileValidationResult();

            // Data annotations validation;
            var validationContext = new ValidationContext(this);
            var validationErrors = new List<ValidationResult>();

            if (!Validator.TryValidateObject(this, validationContext, validationErrors, true))
            {
                result.IsValid = false;
                result.Errors.AddRange(validationErrors.Select(e => e.ErrorMessage ?? "Validation error"));
            }

            // Custom business logic validation;
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                result.IsValid = false;
                result.Errors.Add("Display name is required");
            }

            if (Role == UserRole.Unknown)
            {
                result.IsValid = false;
                result.Errors.Add("User role must be specified");
            }

            if (Status == ProfileStatus.Suspended)
            {
                result.Warnings.Add("User account is suspended");
            }

            if (LastLoginDate > DateTime.UtcNow)
            {
                result.IsValid = false;
                result.Errors.Add("Last login date cannot be in the future");
            }

            return result;
        }

        /// <summary>
        /// Updates the last login information;
        /// </summary>
        public void RecordLogin()
        {
            lock (_syncLock)
            {
                _lastLoginDate = DateTime.UtcNow;
                _loginCount++;
                MarkDirty();

                AddActivity(new ProfileActivity
                {
                    ActivityType = ActivityType.Login,
                    Timestamp = DateTime.UtcNow,
                    Description = "User logged in",
                    Details = $"Login count: {_loginCount}"
                });

                _logger.LogInformation("Login recorded for user {UserId}. Total logins: {LoginCount}",
                    _userId, _loginCount);
            }
        }

        /// <summary>
        /// Updates the last modified date;
        /// </summary>
        public void UpdateLastModified()
        {
            _lastModifiedDate = DateTime.UtcNow;
            MarkDirty();
        }

        /// <summary>
        /// Adds an activity to the recent activities list;
        /// </summary>
        public void AddActivity(ProfileActivity activity)
        {
            Guard.AgainstNull(activity, nameof(activity));

            lock (_syncLock)
            {
                _recentActivities.Insert(0, activity);

                // Keep only the last N activities;
                if (_recentActivities.Count > ProfileConstants.MaxRecentActivities)
                {
                    _recentActivities = _recentActivities.Take(ProfileConstants.MaxRecentActivities).ToList();
                }

                OnPropertyChanged(nameof(RecentActivities));
                MarkDirty();
            }
        }

        /// <summary>
        /// Sets a custom property value;
        /// </summary>
        public void SetCustomProperty(string key, object value)
        {
            Guard.AgainstNullOrEmpty(key, nameof(key));

            lock (_syncLock)
            {
                _customProperties[key] = value;
                OnPropertyChanged(nameof(CustomProperties));
                MarkDirty();
            }
        }

        /// <summary>
        /// Gets a custom property value;
        /// </summary>
        public T GetCustomProperty<T>(string key, T defaultValue = default!)
        {
            Guard.AgainstNullOrEmpty(key, nameof(key));

            lock (_syncLock)
            {
                if (_customProperties.TryGetValue(key, out var value))
                {
                    try
                    {
                        return (T)value;
                    }
                    catch (InvalidCastException)
                    {
                        _logger.LogWarning("Failed to cast custom property {Key} to type {Type}",
                            key, typeof(T).Name);
                        return defaultValue;
                    }
                }

                return defaultValue;
            }
        }

        /// <summary>
        /// Removes a custom property;
        /// </summary>
        public bool RemoveCustomProperty(string key)
        {
            Guard.AgainstNullOrEmpty(key, nameof(key));

            lock (_syncLock)
            {
                var removed = _customProperties.Remove(key);
                if (removed)
                {
                    OnPropertyChanged(nameof(CustomProperties));
                    MarkDirty();
                }
                return removed;
            }
        }

        /// <summary>
        /// Checks if the user has a specific permission;
        /// </summary>
        public bool HasPermission(string permission)
        {
            if (SecurityContext == null || Role == UserRole.Unknown)
                return false;

            // Check role-based permissions;
            if (RolePermissions.GetPermissions(Role).Contains(permission))
                return true;

            // Check explicit permissions in security context;
            return SecurityContext.Permissions.Contains(permission);
        }

        /// <summary>
        /// Checks if the user has all specified permissions;
        /// </summary>
        public bool HasAllPermissions(params string[] permissions)
        {
            return permissions.All(HasPermission);
        }

        /// <summary>
        /// Checks if the user has any of the specified permissions;
        /// </summary>
        public bool HasAnyPermission(params string[] permissions)
        {
            return permissions.Any(HasPermission);
        }

        /// <summary>
        /// Promotes the user to a higher role;
        /// </summary>
        public bool PromoteToRole(UserRole newRole)
        {
            if (newRole <= _role)
            {
                _logger.LogWarning("Cannot promote user {UserId} from {CurrentRole} to {NewRole} - new role must be higher",
                    _userId, _role, newRole);
                return false;
            }

            var oldRole = _role;
            _role = newRole;

            AddActivity(new ProfileActivity
            {
                ActivityType = ActivityType.RoleChange,
                Timestamp = DateTime.UtcNow,
                Description = $"User role changed from {oldRole} to {newRole}",
                Details = $"Promoted by system"
            });

            _logger.LogInformation("User {UserId} promoted from {OldRole} to {NewRole}",
                _userId, oldRole, newRole);

            return true;
        }

        /// <summary>
        /// Suspends the user account;
        /// </summary>
        public void Suspend(string reason)
        {
            Guard.AgainstNullOrEmpty(reason, nameof(reason));

            var oldStatus = _status;
            _status = ProfileStatus.Suspended;

            AddActivity(new ProfileActivity
            {
                ActivityType = ActivityType.AccountSuspended,
                Timestamp = DateTime.UtcNow,
                Description = $"Account suspended. Reason: {reason}",
                Details = $"Previous status: {oldStatus}"
            });

            _logger.LogWarning("User account {UserId} suspended. Reason: {Reason}", _userId, reason);
        }

        /// <summary>
        /// Activates a suspended user account;
        /// </summary>
        public void Activate()
        {
            if (_status != ProfileStatus.Suspended)
            {
                _logger.LogWarning("Cannot activate user {UserId} - current status is {Status}, not Suspended",
                    _userId, _status);
                return;
            }

            _status = ProfileStatus.Active;

            AddActivity(new ProfileActivity
            {
                ActivityType = ActivityType.AccountActivated,
                Timestamp = DateTime.UtcNow,
                Description = "Account activated",
                Details = "Suspension lifted"
            });

            _logger.LogInformation("User account {UserId} activated", _userId);
        }

        /// <summary>
        /// Serializes the profile to JSON;
        /// </summary>
        public string ToJson(bool includeSensitiveData = false)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // Create a copy for serialization;
            var profileCopy = includeSensitiveData ? this : CreateSafeCopy();

            return JsonSerializer.Serialize(profileCopy, options);
        }

        /// <summary>
        /// Creates a safe copy without sensitive data;
        /// </summary>
        private UserProfile CreateSafeCopy()
        {
            return new UserProfile(_logger)
            {
                UserId = _userId,
                Username = _username,
                DisplayName = _displayName,
                Email = _email,
                Role = _role,
                Preferences = _preferences?.Clone() as UserPreferences,
                Status = _status,
                CreatedDate = _createdDate,
                LastModifiedDate = _lastModifiedDate,
                LastLoginDate = _lastLoginDate,
                LoginCount = _loginCount,
                // Don't include security context in safe copy;
                SecurityContext = new SecurityContext(), // Empty security context;
                CustomProperties = _customProperties?
                    .Where(kvp => !kvp.Key.StartsWith("security.", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, object>(),
                RecentActivities = _recentActivities?
                    .Select(a => new ProfileActivity
                    {
                        ActivityType = a.ActivityType,
                        Timestamp = a.Timestamp,
                        Description = a.Description,
                        Details = a.Details?
                            .Replace("password", "***", StringComparison.OrdinalIgnoreCase)
                            .Replace("token", "***", StringComparison.OrdinalIgnoreCase)
                            .Replace("key", "***", StringComparison.OrdinalIgnoreCase)
                    })
                    .ToList() ?? new List<ProfileActivity>()
            };
        }

        /// <summary>
        /// Deserializes a profile from JSON;
        /// </summary>
        public static UserProfile FromJson(string json, ILogger<UserProfile> logger)
        {
            Guard.AgainstNullOrEmpty(json, nameof(json));
            Guard.AgainstNull(logger, nameof(logger));

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var profile = JsonSerializer.Deserialize<UserProfile>(json, options);
                if (profile == null)
                    throw new InvalidOperationException("Failed to deserialize user profile");

                // Ensure logger is set;
                var field = typeof(UserProfile).GetField("_logger",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(profile, logger);

                profile.ClearDirtyFlag();
                return profile;
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Error deserializing user profile from JSON");
                throw;
            }
        }

        /// <summary>
        /// Saves the profile to a file;
        /// </summary>
        public void SaveToFile(string filePath, SecureString? encryptionKey = null)
        {
            Guard.AgainstNullOrEmpty(filePath, nameof(filePath));

            lock (_syncLock)
            {
                try
                {
                    var json = ToJson(true);
                    byte[] data;

                    if (encryptionKey != null)
                    {
                        data = EncryptProfileData(json, encryptionKey);
                    }
                    else
                    {
                        data = Encoding.UTF8.GetBytes(json);
                    }

                    File.WriteAllBytes(filePath, data);
                    ClearDirtyFlag();

                    _logger.LogDebug("User profile saved to {FilePath}", filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving user profile to {FilePath}", filePath);
                    throw;
                }
            }
        }

        /// <summary>
        /// Loads a profile from a file;
        /// </summary>
        public static UserProfile LoadFromFile(string filePath, ILogger<UserProfile> logger,
            SecureString? encryptionKey = null)
        {
            Guard.AgainstNullOrEmpty(filePath, nameof(filePath));
            Guard.AgainstNull(logger, nameof(logger));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Profile file not found: {filePath}", filePath);

            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                string json;

                if (encryptionKey != null)
                {
                    json = DecryptProfileData(data, encryptionKey);
                }
                else
                {
                    json = Encoding.UTF8.GetString(data);
                }

                var profile = FromJson(json, logger);
                profile.ClearDirtyFlag();

                logger.LogDebug("User profile loaded from {FilePath}", filePath);
                return profile;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error loading user profile from {FilePath}", filePath);
                throw;
            }
        }

        /// <summary>
        /// Encrypts profile data;
        /// </summary>
        private byte[] EncryptProfileData(string json, SecureString encryptionKey)
        {
            using var aes = Aes.Create();
            aes.Key = SecureStringHelper.ToByteArray(encryptionKey);
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();

            // Write IV first;
            ms.Write(aes.IV, 0, aes.IV.Length);

            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(json);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Decrypts profile data;
        /// </summary>
        private static string DecryptProfileData(byte[] encryptedData, SecureString encryptionKey)
        {
            using var aes = Aes.Create();
            aes.Key = SecureStringHelper.ToByteArray(encryptionKey);

            // Read IV from beginning of data;
            byte[] iv = new byte[aes.IV.Length];
            Array.Copy(encryptedData, 0, iv, 0, iv.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(encryptedData, iv.Length, encryptedData.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);

            return sr.ReadToEnd();
        }

        /// <summary>
        /// Marks the profile as dirty (has unsaved changes)
        /// </summary>
        private void MarkDirty()
        {
            if (!_isDirty)
            {
                _isDirty = true;
                OnPropertyChanged(nameof(IsDirty));
            }
        }

        /// <summary>
        /// Clears the dirty flag;
        /// </summary>
        public void ClearDirtyFlag()
        {
            if (_isDirty)
            {
                _isDirty = false;
                OnPropertyChanged(nameof(IsDirty));
            }
        }

        /// <summary>
        /// Raises the PropertyChanged event;
        /// </summary>
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Creates a deep clone of the user profile;
        /// </summary>
        public UserProfile Clone()
        {
            var json = ToJson(true);
            return FromJson(json, _logger);
        }

        /// <summary>
        /// Disposes the user profile;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Clear sensitive data;
                    _securityContext?.Dispose();

                    // Clear custom properties that might contain sensitive data;
                    _customProperties?.Clear();

                    // Clear recent activities;
                    _recentActivities?.Clear();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Destructor;
        /// </summary>
        ~UserProfile()
        {
            Dispose(false);
        }

        /// <summary>
        /// Returns a string representation of the user profile;
        /// </summary>
        public override string ToString()
        {
            return $"UserProfile: {DisplayName} ({Username}) - Role: {Role}, Status: {Status}";
        }
    }

    /// <summary>
    /// User role enumeration;
    /// </summary>
    public enum UserRole
    {
        Unknown = 0,
        Guest = 1,
        User = 2,
        PowerUser = 3,
        Moderator = 4,
        Administrator = 5,
        SystemAdmin = 6,
        SuperAdmin = 7
    }

    /// <summary>
    /// Profile status enumeration;
    /// </summary>
    public enum ProfileStatus
    {
        Pending = 0,
        Active = 1,
        Inactive = 2,
        Suspended = 3,
        Banned = 4,
        Deleted = 5
    }

    /// <summary>
    /// Activity type enumeration;
    /// </summary>
    public enum ActivityType
    {
        Login = 1,
        Logout = 2,
        ProfileUpdate = 3,
        PasswordChange = 4,
        RoleChange = 5,
        AccountSuspended = 6,
        AccountActivated = 7,
        PermissionGranted = 8,
        PermissionRevoked = 9,
        Custom = 100
    }

    /// <summary>
    /// User preferences and settings;
    /// </summary>
    [DataContract]
    public class UserPreferences : ICloneable
    {
        [DataMember(Name = "language")]
        public string Language { get; set; } = "en-US";

        [DataMember(Name = "timezone")]
        public string Timezone { get; set; } = "UTC";

        [DataMember(Name = "theme")]
        public string Theme { get; set; } = "Dark";

        [DataMember(Name = "notificationsEnabled")]
        public bool NotificationsEnabled { get; set; } = true;

        [DataMember(Name = "emailNotifications")]
        public bool EmailNotifications { get; set; } = true;

        [DataMember(Name = "pushNotifications")]
        public bool PushNotifications { get; set; } = true;

        [DataMember(Name = "autoSave")]
        public bool AutoSave { get; set; } = true;

        [DataMember(Name = "autoSaveInterval")]
        public int AutoSaveInterval { get; set; } = 300; // seconds;

        [DataMember(Name = "uiScale")]
        public double UiScale { get; set; } = 1.0;

        [DataMember(Name = "fontSize")]
        public int FontSize { get; set; } = 12;

        [DataMember(Name = "accessibilityOptions")]
        public AccessibilityOptions Accessibility { get; set; } = new AccessibilityOptions();

        [DataMember(Name = "privacySettings")]
        public PrivacySettings Privacy { get; set; } = new PrivacySettings();

        [DataMember(Name = "customPreferences")]
        public Dictionary<string, object> CustomPreferences { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new UserPreferences
            {
                Language = this.Language,
                Timezone = this.Timezone,
                Theme = this.Theme,
                NotificationsEnabled = this.NotificationsEnabled,
                EmailNotifications = this.EmailNotifications,
                PushNotifications = this.PushNotifications,
                AutoSave = this.AutoSave,
                AutoSaveInterval = this.AutoSaveInterval,
                UiScale = this.UiScale,
                FontSize = this.FontSize,
                Accessibility = this.Accessibility?.Clone() as AccessibilityOptions ?? new AccessibilityOptions(),
                Privacy = this.Privacy?.Clone() as PrivacySettings ?? new PrivacySettings(),
                CustomPreferences = new Dictionary<string, object>(this.CustomPreferences)
            };
        }
    }

    /// <summary>
    /// Accessibility options;
    /// </summary>
    [DataContract]
    public class AccessibilityOptions : ICloneable
    {
        [DataMember(Name = "highContrast")]
        public bool HighContrast { get; set; }

        [DataMember(Name = "screenReaderSupport")]
        public bool ScreenReaderSupport { get; set; }

        [DataMember(Name = "reducedMotion")]
        public bool ReducedMotion { get; set; }

        [DataMember(Name = "colorBlindMode")]
        public bool ColorBlindMode { get; set; }

        [DataMember(Name = "largeText")]
        public bool LargeText { get; set; }

        public object Clone()
        {
            return new AccessibilityOptions
            {
                HighContrast = this.HighContrast,
                ScreenReaderSupport = this.ScreenReaderSupport,
                ReducedMotion = this.ReducedMotion,
                ColorBlindMode = this.ColorBlindMode,
                LargeText = this.LargeText
            };
        }
    }

    /// <summary>
    /// Privacy settings;
    /// </summary>
    [DataContract]
    public class PrivacySettings : ICloneable
    {
        [DataMember(Name = "shareUsageData")]
        public bool ShareUsageData { get; set; } = true;

        [DataMember(Name = "shareErrorReports")]
        public bool ShareErrorReports { get; set; } = true;

        [DataMember(Name = "publicProfile")]
        public bool PublicProfile { get; set; }

        [DataMember(Name = "showOnlineStatus")]
        public bool ShowOnlineStatus { get; set; } = true;

        [DataMember(Name = "allowTracking")]
        public bool AllowTracking { get; set; }

        [DataMember(Name = "dataRetentionDays")]
        public int DataRetentionDays { get; set; } = 90;

        public object Clone()
        {
            return new PrivacySettings
            {
                ShareUsageData = this.ShareUsageData,
                ShareErrorReports = this.ShareErrorReports,
                PublicProfile = this.PublicProfile,
                ShowOnlineStatus = this.ShowOnlineStatus,
                AllowTracking = this.AllowTracking,
                DataRetentionDays = this.DataRetentionDays
            };
        }
    }

    /// <summary>
    /// Security context for the user;
    /// </summary>
    [DataContract]
    public class SecurityContext : IDisposable
    {
        [DataMember(Name = "permissions")]
        public HashSet<string> Permissions { get; set; } = new HashSet<string>();

        [DataMember(Name = "lastPasswordChange")]
        public DateTime LastPasswordChange { get; set; } = DateTime.UtcNow;

        [DataMember(Name = "passwordExpiryDate")]
        public DateTime PasswordExpiryDate { get; set; } = DateTime.UtcNow.AddDays(90);

        [DataMember(Name = "failedLoginAttempts")]
        public int FailedLoginAttempts { get; set; }

        [DataMember(Name = "accountLockedUntil")]
        public DateTime? AccountLockedUntil { get; set; }

        [DataMember(Name = "mfaEnabled")]
        public bool MfaEnabled { get; set; }

        [DataMember(Name = "mfaMethod")]
        public string MfaMethod { get; set; } = string.Empty;

        [DataMember(Name = "lastSecurityReview")]
        public DateTime LastSecurityReview { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public bool IsLocked => AccountLockedUntil.HasValue && AccountLockedUntil > DateTime.UtcNow;

        public void Dispose()
        {
            // Clear sensitive data;
            Permissions?.Clear();
        }
    }

    /// <summary>
    /// Profile activity record;
    /// </summary>
    [DataContract]
    public class ProfileActivity
    {
        [DataMember(Name = "activityType")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActivityType ActivityType { get; set; }

        [DataMember(Name = "timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [DataMember(Name = "description")]
        public string Description { get; set; } = string.Empty;

        [DataMember(Name = "details")]
        public string Details { get; set; } = string.Empty;

        [DataMember(Name = "ipAddress")]
        public string IpAddress { get; set; } = string.Empty;

        [DataMember(Name = "userAgent")]
        public string UserAgent { get; set; } = string.Empty;
    }

    /// <summary>
    /// Profile validation result;
    /// </summary>
    public class ProfileValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public override string ToString()
        {
            if (IsValid && Errors.Count == 0 && Warnings.Count == 0)
                return "Profile validation passed";

            var messages = new List<string>();

            if (!IsValid)
                messages.Add("Validation failed");

            if (Errors.Count > 0)
                messages.Add($"Errors: {string.Join("; ", Errors)}");

            if (Warnings.Count > 0)
                messages.Add($"Warnings: {string.Join("; ", Warnings)}");

            return string.Join(". ", messages);
        }
    }

    /// <summary>
    /// Profile constants;
    /// </summary>
    public static class ProfileConstants
    {
        public const int MaxRecentActivities = 100;
        public const int MaxFailedLoginAttempts = 5;
        public const int AccountLockDurationMinutes = 15;
        public const int PasswordMinLength = 8;
        public const int PasswordMaxLength = 128;
    }

    /// <summary>
    /// Role-based permissions helper;
    /// </summary>
    public static class RolePermissions
    {
        private static readonly Dictionary<UserRole, HashSet<string>> _rolePermissions = new()
        {
            [UserRole.Guest] = new HashSet<string>
            {
                "read.public",
                "view.profile",
                "browse.content"
            },
            [UserRole.User] = new HashSet<string>
            {
                "read.public",
                "write.own",
                "edit.profile",
                "upload.files",
                "create.content"
            },
            [UserRole.PowerUser] = new HashSet<string>
            {
                "read.public",
                "write.own",
                "edit.profile",
                "upload.files",
                "create.content",
                "delete.own",
                "manage.templates"
            },
            [UserRole.Moderator] = new HashSet<string>
            {
                "read.all",
                "write.all",
                "delete.any",
                "moderate.content",
                "manage.users",
                "view.audit"
            },
            [UserRole.Administrator] = new HashSet<string>
            {
                "system.config",
                "security.manage",
                "user.manage",
                "role.manage",
                "backup.restore",
                "audit.view"
            },
            [UserRole.SystemAdmin] = new HashSet<string>
            {
                "system.admin",
                "security.admin",
                "user.admin",
                "role.admin",
                "backup.admin",
                "audit.admin"
            },
            [UserRole.SuperAdmin] = new HashSet<string>
            {
                "*"
            }
        };

        public static HashSet<string> GetPermissions(UserRole role)
        {
            return _rolePermissions.TryGetValue(role, out var permissions)
                ? permissions
                : new HashSet<string>();
        }

        public static bool RoleHasPermission(UserRole role, string permission)
        {
            if (role == UserRole.SuperAdmin)
                return true;

            return GetPermissions(role).Contains(permission) ||
                   GetPermissions(role).Contains("*");
        }
    }
}
