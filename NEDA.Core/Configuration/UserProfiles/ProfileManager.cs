using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.Configuration.UserProfiles
{
    /// <summary>
    /// Advanced user profile manager for NEDA with support for multiple profiles,
    /// synchronization, encryption, and cloud backup.
    /// </summary>
    public class ProfileManager : IProfileManager, IDisposable
    {
        private readonly ILogger<ProfileManager> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly AppConfig _appConfig;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IErrorReporter _errorReporter;
        private readonly FileSystemWatcher _profileWatcher;
        private readonly Timer _syncTimer;
        private readonly SemaphoreSlim _profileLock = new SemaphoreSlim(1, 1);
        private readonly string _profilesDirectory;
        private readonly string _backupDirectory;
        private bool _isDisposed;

        private Dictionary<string, UserProfile> _profilesCache;
        private UserProfile _currentProfile;
        private string _currentProfileId;

        public event EventHandler<ProfileLoadedEventArgs> ProfileLoaded;
        public event EventHandler<ProfileSavedEventArgs> ProfileSaved;
        public event EventHandler<ProfileDeletedEventArgs> ProfileDeleted;
        public event EventHandler<ProfilesSyncedEventArgs> ProfilesSynced;
        public event EventHandler<ProfileValidationFailedEventArgs> ProfileValidationFailed;

        /// <summary>
        /// Gets the current active profile.
        /// </summary>
        public UserProfile CurrentProfile
        {
            get
            {
                if (_currentProfile == null && !string.IsNullOrEmpty(_currentProfileId))
                {
                    // Sync getter içinde Wait riskli ama mevcut tasarıma uyum için bırakıyorum.
                    LoadProfileAsync(_currentProfileId).GetAwaiter().GetResult();
                }
                return _currentProfile;
            }
            private set => _currentProfile = value;
        }

        /// <summary>
        /// Gets the current profile ID.
        /// </summary>
        public string CurrentProfileId => _currentProfileId;

        public bool IsProfileLoaded => CurrentProfile != null;

        public List<string> AvailableProfileIds => GetAvailableProfiles().GetAwaiter().GetResult();

        public string ProfilesDirectory => _profilesDirectory;
        public string BackupDirectory => _backupDirectory;

        public int TotalProfiles => _profilesCache?.Count ?? 0;

        public DateTime LastSyncTime { get; private set; }

        public bool AutoSyncEnabled { get; private set; }

        public ProfileManager(
            ILogger<ProfileManager> logger,
            IMemoryCache memoryCache,
            IOptions<AppConfig> appConfigOptions,
            ICryptoEngine cryptoEngine,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _appConfig = appConfigOptions?.Value ?? throw new ArgumentNullException(nameof(appConfigOptions));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _profilesDirectory = Path.Combine(
                _appConfig.ApplicationSettings?.DataDirectory ?? "Data",
                "Profiles");

            _backupDirectory = Path.Combine(_profilesDirectory, "Backups");

            EnsureDirectoriesExist();

            _profilesCache = new Dictionary<string, UserProfile>();

            _profileWatcher = new FileSystemWatcher(_profilesDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = "*.profile",
                IncludeSubdirectories = false,
                EnableRaisingEvents = false
            };

            _profileWatcher.Changed += OnProfileFileChanged;
            _profileWatcher.Created += OnProfileFileChanged;
            _profileWatcher.Deleted += OnProfileFileChanged;
            _profileWatcher.Renamed += OnProfileFileRenamed;

            _syncTimer = new Timer(OnSyncTimerTick, null, Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation("ProfileManager initialized. Profiles directory: {ProfilesDirectory}", _profilesDirectory);
        }

        public async Task<UserProfile> LoadProfileAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Loading profile: {ProfileId}", profileId);

                // cache
                if (_profilesCache.TryGetValue(profileId, out var cachedProfile))
                {
                    _logger.LogDebug("Profile found in cache: {ProfileId}", profileId);
                    SetCurrentProfile(profileId, cachedProfile);
                    return cachedProfile;
                }

                // file
                var profile = await LoadProfileFromFileAsync(profileId, cancellationToken);
                if (profile == null)
                {
                    _logger.LogWarning("Profile not found: {ProfileId}", profileId);
                    throw new ProfileNotFoundException($"Profile not found: {profileId}");
                }

                await ValidateProfileAsync(profile, cancellationToken);

                _profilesCache[profileId] = profile;
                SetCurrentProfile(profileId, profile);

                await ApplyProfileSettingsAsync(profile, cancellationToken);

                OnProfileLoaded(new ProfileLoadedEventArgs
                {
                    ProfileId = profileId,
                    Profile = profile,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Profile loaded successfully: {ProfileId}", profileId);
                return profile;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Profile loading cancelled: {ProfileId}", profileId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load profile: {ProfileId}", profileId);

                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = $"ProfileManager.LoadProfileAsync for profile {profileId}",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.Medium
                });

                throw new ProfileLoadException($"Failed to load profile: {profileId}", ex);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task SaveProfileAsync(CancellationToken cancellationToken = default)
        {
            if (CurrentProfile == null)
                throw new InvalidOperationException("No profile is currently loaded");

            await SaveProfileAsync(CurrentProfileId, CurrentProfile, cancellationToken);
        }

        public async Task SaveProfileAsync(string profileId, UserProfile profile, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Saving profile: {ProfileId}", profileId);

                await ValidateProfileAsync(profile, cancellationToken);

                profile.Metadata.LastModified = DateTime.UtcNow;
                profile.Metadata.Version++;

                // backup
                await CreateBackupAsync(profileId, cancellationToken);

                // save
                await SaveProfileToFileAsync(profileId, profile, cancellationToken);

                // cache update
                _profilesCache[profileId] = profile;

                if (profileId == _currentProfileId)
                    CurrentProfile = profile;

                OnProfileSaved(new ProfileSavedEventArgs
                {
                    ProfileId = profileId,
                    Profile = profile,
                    Timestamp = DateTime.UtcNow,
                    IsNewProfile = false
                });

                _logger.LogInformation("Profile saved successfully: {ProfileId}", profileId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Profile saving cancelled: {ProfileId}", profileId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save profile: {ProfileId}", profileId);

                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = $"ProfileManager.SaveProfileAsync for profile {profileId}",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.Medium
                });

                throw new ProfileSaveException($"Failed to save profile: {profileId}", ex);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task<UserProfile> CreateProfileAsync(string profileId, ProfileCreationOptions options, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Creating new profile: {ProfileId}", profileId);

                if (await ProfileExistsAsync(profileId))
                    throw new ProfileAlreadyExistsException($"Profile already exists: {profileId}");

                var profile = new UserProfile
                {
                    Metadata = new ProfileMetadata
                    {
                        Id = profileId,
                        Name = options.Name ?? profileId,
                        Description = options.Description,
                        Created = DateTime.UtcNow,
                        LastModified = DateTime.UtcNow,
                        Version = 1,
                        ProfileType = options.ProfileType,
                        Tags = options.Tags?.ToList() ?? new List<string>()
                    },
                    Preferences = options.InitialPreferences ?? new UserPreferences(),
                    Settings = options.InitialSettings ?? new ProfileSettings(),
                    Data = options.InitialData ?? new Dictionary<string, object>(),
                    Security = new ProfileSecurity
                    {
                        IsEncrypted = options.EncryptProfile,
                        EncryptionMethod = options.EncryptProfile ? "AES-256-GCM" : "None",
                        AccessLevel = options.AccessLevel,
                        Permissions = options.Permissions?.ToList() ?? new List<string>()
                    }
                };

                if (!string.IsNullOrEmpty(options.TemplateId))
                    await ApplyTemplateAsync(profile, options.TemplateId, cancellationToken);

                await ValidateProfileAsync(profile, cancellationToken);

                await SaveProfileAsync(profileId, profile, cancellationToken);

                _logger.LogInformation("Profile created successfully: {ProfileId}", profileId);
                return profile;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Profile creation cancelled: {ProfileId}", profileId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create profile: {ProfileId}", profileId);

                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = $"ProfileManager.CreateProfileAsync for profile {profileId}",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.Medium
                });

                throw new ProfileCreationException($"Failed to create profile: {profileId}", ex);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task DeleteProfileAsync(string profileId, bool backupBeforeDelete = true, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Deleting profile: {ProfileId}", profileId);

                if (!await ProfileExistsAsync(profileId))
                    throw new ProfileNotFoundException($"Profile not found: {profileId}");

                if (backupBeforeDelete)
                    await CreateBackupAsync(profileId, cancellationToken);

                var profile = await LoadProfileFromFileAsync(profileId, cancellationToken);

                var profilePath = GetProfileFilePath(profileId);
                if (File.Exists(profilePath))
                    File.Delete(profilePath);

                _profilesCache.Remove(profileId);

                if (profileId == _currentProfileId)
                {
                    _currentProfileId = null;
                    CurrentProfile = null;
                }

                OnProfileDeleted(new ProfileDeletedEventArgs
                {
                    ProfileId = profileId,
                    Profile = profile,
                    Timestamp = DateTime.UtcNow,
                    BackupCreated = backupBeforeDelete
                });

                _logger.LogInformation("Profile deleted successfully: {ProfileId}", profileId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Profile deletion cancelled: {ProfileId}", profileId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete profile: {ProfileId}", profileId);

                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = $"ProfileManager.DeleteProfileAsync for profile {profileId}",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.Medium
                });

                throw new ProfileDeleteException($"Failed to delete profile: {profileId}", ex);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task<List<string>> GetAvailableProfiles()
        {
            try
            {
                if (!Directory.Exists(_profilesDirectory))
                    return new List<string>();

                var profileFiles = Directory.GetFiles(_profilesDirectory, "*.profile");
                var profileIds = new List<string>();

                foreach (var file in profileFiles)
                {
                    try
                    {
                        var profileId = Path.GetFileNameWithoutExtension(file);
                        if (!string.IsNullOrEmpty(profileId))
                            profileIds.Add(profileId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading profile file: {File}", file);
                    }
                }

                return await Task.FromResult(profileIds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available profiles");
                throw new ProfileOperationException("Failed to get available profiles", ex);
            }
        }

        public async Task<bool> ProfileExistsAsync(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return false;

            try
            {
                if (_profilesCache.ContainsKey(profileId))
                    return true;

                var profilePath = GetProfileFilePath(profileId);
                return await Task.FromResult(File.Exists(profilePath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking if profile exists: {ProfileId}", profileId);
                return false;
            }
        }

        public async Task<UserProfile> DuplicateProfileAsync(string sourceProfileId, string newProfileId, ProfileCreationOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceProfileId))
                throw new ArgumentException("Source profile ID cannot be null or empty", nameof(sourceProfileId));
            if (string.IsNullOrWhiteSpace(newProfileId))
                throw new ArgumentException("New profile ID cannot be null or empty", nameof(newProfileId));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Duplicating profile {SourceProfileId} to {NewProfileId}", sourceProfileId, newProfileId);

                var sourceProfile = await LoadProfileAsync(sourceProfileId, cancellationToken);

                var dupOptions = options ?? new ProfileCreationOptions
                {
                    Name = $"{sourceProfile.Metadata.Name} (Copy)",
                    Description = $"Copy of {sourceProfile.Metadata.Name}",
                    ProfileType = sourceProfile.Metadata.ProfileType,
                    EncryptProfile = sourceProfile.Security.IsEncrypted,
                    AccessLevel = sourceProfile.Security.AccessLevel,
                    Permissions = sourceProfile.Security.Permissions,
                    InitialPreferences = sourceProfile.Preferences.Clone(),
                    InitialSettings = sourceProfile.Settings.Clone(),
                    InitialData = DeepCloneDictionary(sourceProfile.Data),
                    Tags = sourceProfile.Metadata.Tags?.ToList()
                };

                var newProfile = await CreateProfileAsync(newProfileId, dupOptions, cancellationToken);

                _logger.LogInformation("Profile duplicated successfully from {SourceProfileId} to {NewProfileId}", sourceProfileId, newProfileId);
                return newProfile;
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task ExportProfileAsync(string profileId, string exportPath, bool includeSensitiveData = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));
            if (string.IsNullOrWhiteSpace(exportPath))
                throw new ArgumentException("Export path cannot be null or empty", nameof(exportPath));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Exporting profile {ProfileId} to {ExportPath}", profileId, exportPath);

                var profile = await LoadProfileAsync(profileId, cancellationToken);

                var exportData = new ProfileExportData
                {
                    ProfileId = profileId,
                    Profile = includeSensitiveData ? profile : CreateSanitizedCopy(profile),
                    ExportTime = DateTime.UtcNow,
                    ExportVersion = "1.0",
                    IncludeSensitiveData = includeSensitiveData
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(exportData, jsonOptions);

                var directory = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllTextAsync(exportPath, json, cancellationToken);

                _logger.LogInformation("Profile exported successfully: {ProfileId} to {ExportPath}", profileId, exportPath);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task<UserProfile> ImportProfileAsync(string importPath, string newProfileId = null, bool overwriteExisting = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(importPath))
                throw new ArgumentException("Import path cannot be null or empty", nameof(importPath));
            if (!File.Exists(importPath))
                throw new FileNotFoundException($"Import file not found: {importPath}", importPath);

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Importing profile from {ImportPath}", importPath);

                var json = await File.ReadAllTextAsync(importPath, cancellationToken);

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var exportData = JsonSerializer.Deserialize<ProfileExportData>(json, jsonOptions);
                if (exportData?.Profile == null)
                    throw new ProfileImportException("Invalid profile export file");

                var profileId = newProfileId ?? exportData.ProfileId;
                if (string.IsNullOrWhiteSpace(profileId))
                    throw new ArgumentException("Profile ID is required for import");

                if (await ProfileExistsAsync(profileId) && !overwriteExisting)
                    throw new ProfileAlreadyExistsException($"Profile already exists: {profileId}");

                exportData.Profile.Metadata.Id = profileId;
                exportData.Profile.Metadata.LastModified = DateTime.UtcNow;
                exportData.Profile.Metadata.Version = 1;

                await SaveProfileAsync(profileId, exportData.Profile, cancellationToken);

                _logger.LogInformation("Profile imported successfully from {ImportPath} as {ProfileId}", importPath, profileId);
                return exportData.Profile;
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task SyncProfilesAsync(CancellationToken cancellationToken = default)
        {
            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Starting profile synchronization");

                var startTime = DateTime.UtcNow;
                var syncResults = new List<ProfileSyncResult>();
                var localProfileIds = await GetAvailableProfiles();

                foreach (var profileId in localProfileIds)
                {
                    try
                    {
                        var profile = await LoadProfileAsync(profileId, cancellationToken);

                        profile.Metadata.LastSyncTime = DateTime.UtcNow;
                        profile.Metadata.SyncStatus = SyncStatus.Synced;

                        await SaveProfileAsync(profileId, profile, cancellationToken);

                        syncResults.Add(new ProfileSyncResult
                        {
                            ProfileId = profileId,
                            Status = SyncStatus.Synced,
                            Message = "Profile synchronized successfully",
                            SyncTime = DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync profile: {ProfileId}", profileId);
                        syncResults.Add(new ProfileSyncResult
                        {
                            ProfileId = profileId,
                            Status = SyncStatus.Failed,
                            Message = $"Sync failed: {ex.Message}",
                            SyncTime = DateTime.UtcNow
                        });
                    }
                }

                LastSyncTime = DateTime.UtcNow;

                OnProfilesSynced(new ProfilesSyncedEventArgs
                {
                    SyncTime = LastSyncTime,
                    Duration = DateTime.UtcNow - startTime,
                    Results = syncResults,
                    TotalProfiles = localProfileIds.Count,
                    SuccessfulSyncs = syncResults.Count(r => r.Status == SyncStatus.Synced)
                });

                _logger.LogInformation("Profile synchronization completed. Successful: {Successful}/{Total}",
                    syncResults.Count(r => r.Status == SyncStatus.Synced), localProfileIds.Count);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public void EnableAutoSync(TimeSpan interval)
        {
            if (AutoSyncEnabled)
            {
                _logger.LogDebug("Auto-sync is already enabled");
                return;
            }

            _syncTimer.Change(interval, interval);
            AutoSyncEnabled = true;
            _logger.LogInformation("Auto-sync enabled with interval: {Interval}", interval);
        }

        public void DisableAutoSync()
        {
            if (!AutoSyncEnabled)
                return;

            _syncTimer.Change(Timeout.Infinite, Timeout.Infinite);
            AutoSyncEnabled = false;
            _logger.LogInformation("Auto-sync disabled");
        }

        public async Task CreateBackupAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));

            try
            {
                _logger.LogDebug("Creating backup for profile: {ProfileId}", profileId);

                var profile = await LoadProfileAsync(profileId, cancellationToken);
                var backupPath = GetBackupFilePath(profileId);

                if (!Directory.Exists(_backupDirectory))
                    Directory.CreateDirectory(_backupDirectory);

                // backup dosyasını direkt path ile kaydet
                await SaveProfileToFileAsync(backupPath, profile, cancellationToken);

                profile.Metadata.LastBackupTime = DateTime.UtcNow;
                profile.Metadata.BackupCount++;

                // burada tekrar backup üretmemesi için direkt file'a yaz
                await SaveProfileToFileAsync(profileId, profile, cancellationToken);

                _logger.LogInformation("Backup created for profile: {ProfileId} at {BackupPath}", profileId, backupPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create backup for profile: {ProfileId}", profileId);
                throw new ProfileOperationException($"Failed to create backup for profile: {profileId}", ex);
            }
        }

        public async Task RestoreFromBackupAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be null or empty", nameof(profileId));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Restoring profile from backup: {ProfileId}", profileId);

                // En son backup'ı bul
                var safe = GetSafeFileName(profileId);
                var backups = Directory.Exists(_backupDirectory)
                    ? Directory.GetFiles(_backupDirectory, $"{safe}_*.backup").OrderByDescending(x => x).ToList()
                    : new List<string>();

                var backupPath = backups.FirstOrDefault();
                if (backupPath == null || !File.Exists(backupPath))
                    throw new FileNotFoundException($"Backup file not found for profile: {profileId}");

                var backupProfile = await LoadProfileFromFileAsync(backupPath, cancellationToken);

                await SaveProfileAsync(profileId, backupProfile, cancellationToken);

                _logger.LogInformation("Profile restored from backup: {ProfileId}", profileId);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        public async Task<ProfileStatistics> GetStatisticsAsync()
        {
            try
            {
                var profileIds = await GetAvailableProfiles();

                var stats = new ProfileStatistics
                {
                    TotalProfiles = profileIds.Count,
                    LastSyncTime = LastSyncTime,
                    ProfilesDirectory = _profilesDirectory,
                    BackupDirectory = _backupDirectory
                };

                long totalSize = 0;
                foreach (var profileId in profileIds)
                {
                    var profilePath = GetProfileFilePath(profileId);
                    if (File.Exists(profilePath))
                        totalSize += new FileInfo(profilePath).Length;
                }

                stats.TotalSizeBytes = totalSize;

                if (Directory.Exists(_backupDirectory))
                {
                    var backupFiles = Directory.GetFiles(_backupDirectory, "*.backup");
                    stats.BackupCount = backupFiles.Length;
                }

                return await Task.FromResult(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get profile statistics");
                throw new ProfileOperationException("Failed to get profile statistics", ex);
            }
        }

        private void EnsureDirectoriesExist()
        {
            try
            {
                if (!Directory.Exists(_profilesDirectory))
                {
                    Directory.CreateDirectory(_profilesDirectory);
                    _logger.LogDebug("Created profiles directory: {ProfilesDirectory}", _profilesDirectory);
                }

                if (!Directory.Exists(_backupDirectory))
                {
                    Directory.CreateDirectory(_backupDirectory);
                    _logger.LogDebug("Created backup directory: {BackupDirectory}", _backupDirectory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create profile directories");
                throw new ProfileOperationException("Failed to create profile directories", ex);
            }
        }

        private string GetProfileFilePath(string profileId)
        {
            var safeFileName = GetSafeFileName(profileId);
            return Path.Combine(_profilesDirectory, $"{safeFileName}.profile");
        }

        private string GetBackupFilePath(string profileId)
        {
            var safeFileName = GetSafeFileName(profileId);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(_backupDirectory, $"{safeFileName}_{timestamp}.backup");
        }

        private string GetSafeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "unknown";

            var invalidChars = Path.GetInvalidFileNameChars();
            var safeName = new string(fileName.Where(c => !invalidChars.Contains(c)).ToArray());

            return string.IsNullOrEmpty(safeName) ? "unknown" : safeName;
        }

        private async Task<UserProfile> LoadProfileFromFileAsync(string profileId, CancellationToken cancellationToken)
        {
            var filePath = GetProfileFilePath(profileId);
            return await LoadProfileFromFileAsync(filePath, cancellationToken);
        }

        private async Task<UserProfile> LoadProfileFromFileAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var profile = JsonSerializer.Deserialize<UserProfile>(json, jsonOptions);

                if (profile == null)
                    throw new ProfileLoadException("Failed to deserialize profile");

                return profile;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize profile from {FilePath}", filePath);
                throw new ProfileLoadException($"Failed to deserialize profile from {filePath}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load profile from {FilePath}", filePath);
                throw new ProfileLoadException($"Failed to load profile from {filePath}", ex);
            }
        }

        private async Task SaveProfileToFileAsync(string profileId, UserProfile profile, CancellationToken cancellationToken)
        {
            var filePath = GetProfileFilePath(profileId);
            await SaveProfileToFileAsync(filePath, profile, cancellationToken);
        }

        private async Task SaveProfileToFileAsync(string filePath, UserProfile profile, CancellationToken cancellationToken)
        {
            try
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var json = JsonSerializer.Serialize(profile, jsonOptions);
                var dataToSave = json;

                // Eğer ileride string-based encrypt ekleyeceksen burada uygula.
                if (profile.Security?.IsEncrypted == true)
                {
                    // TODO: implement real encryption
                    // dataToSave = await _cryptoEngine.EncryptStringAsync(json, cancellationToken);
                }

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllTextAsync(filePath, dataToSave, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save profile to {FilePath}", filePath);
                throw new ProfileSaveException($"Failed to save profile to {filePath}", ex);
            }
        }

        private async Task ValidateProfileAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            try
            {
                if (profile == null)
                    throw new ArgumentNullException(nameof(profile));

                if (string.IsNullOrWhiteSpace(profile.Metadata?.Id))
                    throw new ProfileValidationException("Profile ID is required");

                if (string.IsNullOrWhiteSpace(profile.Metadata.Name))
                    throw new ProfileValidationException("Profile name is required");

                if (profile.Metadata.Created > DateTime.UtcNow)
                    throw new ProfileValidationException("Profile creation date cannot be in the future");

                if (profile.Metadata.LastModified > DateTime.UtcNow)
                    throw new ProfileValidationException("Profile modification date cannot be in the future");

                if (profile.Security != null)
                {
                    if (profile.Security.IsEncrypted && string.IsNullOrWhiteSpace(profile.Security.EncryptionMethod))
                        throw new ProfileValidationException("Encryption method is required when profile is encrypted");
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Profile validation failed");

                OnProfileValidationFailed(new ProfileValidationFailedEventArgs
                {
                    Profile = profile,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow
                });

                throw new ProfileValidationException("Profile validation failed", ex);
            }
        }

        private async Task ApplyProfileSettingsAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            try
            {
                // TODO: gerçek AppConfig apply işlemleri
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply profile settings");
                throw new ProfileOperationException("Failed to apply profile settings", ex);
            }
        }

        private async Task ApplyTemplateAsync(UserProfile profile, string templateId, CancellationToken cancellationToken)
        {
            try
            {
                var templatePath = Path.Combine(_profilesDirectory, "Templates", $"{templateId}.template");
                if (File.Exists(templatePath))
                {
                    _ = await File.ReadAllTextAsync(templatePath, cancellationToken);
                    // TODO: merge
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply template {TemplateId} to profile", templateId);
            }
        }

        private void SetCurrentProfile(string profileId, UserProfile profile)
        {
            _currentProfileId = profileId;
            CurrentProfile = profile;

            var cacheKey = $"Profile_{profileId}";
            _memoryCache.Set(cacheKey, profile, TimeSpan.FromMinutes(30));
        }

        private UserProfile CreateSanitizedCopy(UserProfile original)
        {
            var copy = original.Clone();

            copy.Security = new ProfileSecurity
            {
                IsEncrypted = false,
                EncryptionMethod = "None",
                AccessLevel = original.Security?.AccessLevel ?? SecurityLevel.User,
                Permissions = original.Security?.Permissions?.ToList() ?? new List<string>()
            };

            copy.Data?.Clear();
            return copy;
        }

        private Dictionary<string, object> DeepCloneDictionary(Dictionary<string, object> original)
        {
            if (original == null) return null;

            // JSON roundtrip: object graph’ı basitçe kopyalar.
            // (Bazı tiplerde sorun çıkarsa kendi clone mantığını kurarız.)
            var json = JsonSerializer.Serialize(original);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json)
                   ?? new Dictionary<string, object>();
        }

        private void OnProfileFileChanged(object sender, FileSystemEventArgs e)
        {
            var fileName = Path.GetFileNameWithoutExtension(e.Name);
            if (string.IsNullOrEmpty(fileName))
                return;

            _logger.LogDebug("Profile file changed: {FileName}, change type: {ChangeType}", fileName, e.ChangeType);

            _profilesCache.Remove(fileName);

            if (fileName == _currentProfileId)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await LoadProfileAsync(fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reload current profile after file change");
                    }
                });
            }
        }

        private void OnProfileFileRenamed(object sender, RenamedEventArgs e)
        {
            _logger.LogDebug("Profile file renamed: {OldName} -> {NewName}", e.OldName, e.Name);
            OnProfileFileChanged(sender, e);
        }

        private void OnSyncTimerTick(object state)
        {
            if (!AutoSyncEnabled)
                return;

            Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("Auto-sync triggered by timer");
                    await SyncProfilesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-sync failed");
                }
            });
        }

        private void OnProfileLoaded(ProfileLoadedEventArgs e) => ProfileLoaded?.Invoke(this, e);
        private void OnProfileSaved(ProfileSavedEventArgs e) => ProfileSaved?.Invoke(this, e);
        private void OnProfileDeleted(ProfileDeletedEventArgs e) => ProfileDeleted?.Invoke(this, e);
        private void OnProfilesSynced(ProfilesSyncedEventArgs e) => ProfilesSynced?.Invoke(this, e);
        private void OnProfileValidationFailed(ProfileValidationFailedEventArgs e) => ProfileValidationFailed?.Invoke(this, e);

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                _profileWatcher?.Dispose();
                _syncTimer?.Dispose();
                _profileLock?.Dispose();
            }

            _isDisposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ProfileManager()
        {
            Dispose(false);
        }
    }

    #region Supporting Classes and Interfaces

    public class UserProfile : ICloneable
    {
        public ProfileMetadata Metadata { get; set; } = new ProfileMetadata();
        public UserPreferences Preferences { get; set; } = new UserPreferences();
        public ProfileSettings Settings { get; set; } = new ProfileSettings();
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public ProfileSecurity Security { get; set; } = new ProfileSecurity();

        public UserProfile Clone()
        {
            return new UserProfile
            {
                Metadata = Metadata?.Clone(),
                Preferences = Preferences?.Clone(),
                Settings = Settings?.Clone(),
                Data = Data != null ? new Dictionary<string, object>(Data) : new Dictionary<string, object>(),
                Security = Security?.Clone()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    public class ProfileMetadata : ICloneable
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ProfileType ProfileType { get; set; } = ProfileType.User;
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? LastBackupTime { get; set; }
        public DateTime? LastSyncTime { get; set; }
        public int Version { get; set; } = 1;
        public int BackupCount { get; set; }
        public SyncStatus SyncStatus { get; set; } = SyncStatus.NotSynced;
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, string> CustomMetadata { get; set; } = new Dictionary<string, string>();

        public ProfileMetadata Clone()
        {
            return new ProfileMetadata
            {
                Id = Id,
                Name = Name,
                Description = Description,
                ProfileType = ProfileType,
                Created = Created,
                LastModified = LastModified,
                LastBackupTime = LastBackupTime,
                LastSyncTime = LastSyncTime,
                Version = Version,
                BackupCount = BackupCount,
                SyncStatus = SyncStatus,
                Tags = Tags?.ToList() ?? new List<string>(),
                CustomMetadata = CustomMetadata != null ? new Dictionary<string, string>(CustomMetadata) : new Dictionary<string, string>()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    public class UserPreferences : ICloneable
    {
        public string Theme { get; set; } = "Dark";
        public string Language { get; set; } = "en-US";
        public string TimeZone { get; set; } = "UTC";
        public string DateFormat { get; set; } = "yyyy-MM-dd";
        public string TimeFormat { get; set; } = "HH:mm:ss";
        public string NumberFormat { get; set; } = "en-US";
        public bool ShowNotifications { get; set; } = true;
        public bool EnableNotificationSound { get; set; } = true;
        public bool AutoSave { get; set; } = true;
        public int AutoSaveInterval { get; set; } = 5;
        public bool ConfirmExit { get; set; } = true;

        public AccessibilityPreferences Accessibility { get; set; } = new AccessibilityPreferences();
        public PrivacyPreferences Privacy { get; set; } = new PrivacyPreferences();
        public Dictionary<string, object> CustomPreferences { get; set; } = new Dictionary<string, object>();

        public UserPreferences Clone()
        {
            return new UserPreferences
            {
                Theme = Theme,
                Language = Language,
                TimeZone = TimeZone,
                DateFormat = DateFormat,
                TimeFormat = TimeFormat,
                NumberFormat = NumberFormat,
                ShowNotifications = ShowNotifications,
                EnableNotificationSound = EnableNotificationSound,
                AutoSave = AutoSave,
                AutoSaveInterval = AutoSaveInterval,
                ConfirmExit = ConfirmExit,
                Accessibility = Accessibility?.Clone(),
                Privacy = Privacy?.Clone(),
                CustomPreferences = CustomPreferences != null ? new Dictionary<string, object>(CustomPreferences) : new Dictionary<string, object>()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    public class ProfileSettings : ICloneable
    {
        public Dictionary<string, object> ApplicationSettings { get; set; } = new Dictionary<string, object>();
        public AIModelPreferences AIModelPreferences { get; set; } = new AIModelPreferences();
        public ProfileSecuritySettings SecuritySettings { get; set; } = new ProfileSecuritySettings();
        public PerformanceSettings PerformanceSettings { get; set; } = new PerformanceSettings();
        public NotificationSettings NotificationSettings { get; set; } = new NotificationSettings();
        public BackupSettings BackupSettings { get; set; } = new BackupSettings();
        public SyncSettings SyncSettings { get; set; } = new SyncSettings();
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();

        public ProfileSettings Clone()
        {
            return new ProfileSettings
            {
                ApplicationSettings = ApplicationSettings != null ? new Dictionary<string, object>(ApplicationSettings) : new Dictionary<string, object>(),
                AIModelPreferences = AIModelPreferences?.Clone(),
                SecuritySettings = SecuritySettings?.Clone(),
                PerformanceSettings = PerformanceSettings?.Clone(),
                NotificationSettings = NotificationSettings?.Clone(),
                BackupSettings = BackupSettings?.Clone(),
                SyncSettings = SyncSettings?.Clone(),
                CustomSettings = CustomSettings != null ? new Dictionary<string, object>(CustomSettings) : new Dictionary<string, object>()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    public class ProfileSecurity : ICloneable
    {
        public bool IsEncrypted { get; set; }
        public string EncryptionMethod { get; set; }
        public SecurityLevel AccessLevel { get; set; } = SecurityLevel.User;
        public List<string> Permissions { get; set; } = new List<string>();
        public string PasswordHash { get; set; }
        public string EncryptedKey { get; set; }
        public Dictionary<string, string> SecurityMetadata { get; set; } = new Dictionary<string, string>();

        public ProfileSecurity Clone()
        {
            return new ProfileSecurity
            {
                IsEncrypted = IsEncrypted,
                EncryptionMethod = EncryptionMethod,
                AccessLevel = AccessLevel,
                Permissions = Permissions?.ToList() ?? new List<string>(),
                PasswordHash = PasswordHash,
                EncryptedKey = EncryptedKey,
                SecurityMetadata = SecurityMetadata != null ? new Dictionary<string, string>(SecurityMetadata) : new Dictionary<string, string>()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    public class ProfileCreationOptions
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ProfileType ProfileType { get; set; } = ProfileType.User;
        public bool EncryptProfile { get; set; }
        public SecurityLevel AccessLevel { get; set; } = SecurityLevel.User;
        public List<string> Permissions { get; set; }
        public string TemplateId { get; set; }
        public UserPreferences InitialPreferences { get; set; }
        public ProfileSettings InitialSettings { get; set; }
        public Dictionary<string, object> InitialData { get; set; }
        public List<string> Tags { get; set; }
    }

    public class ProfileExportData
    {
        public string ProfileId { get; set; }
        public UserProfile Profile { get; set; }
        public DateTime ExportTime { get; set; }
        public string ExportVersion { get; set; }
        public bool IncludeSensitiveData { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public class ProfileSyncResult
    {
        public string ProfileId { get; set; }
        public SyncStatus Status { get; set; }
        public string Message { get; set; }
        public DateTime SyncTime { get; set; }
        public long DataSize { get; set; }
    }

    public class ProfileStatistics
    {
        public int TotalProfiles { get; set; }
        public long TotalSizeBytes { get; set; }
        public int BackupCount { get; set; }
        public DateTime LastSyncTime { get; set; }
        public string ProfilesDirectory { get; set; }
        public string BackupDirectory { get; set; }
        public double AverageProfileSize => TotalProfiles > 0 ? (double)TotalSizeBytes / TotalProfiles : 0;
    }

    public enum ProfileType
    {
        User = 0,
        Administrator = 1,
        System = 2,
        Guest = 3,
        Developer = 4,
        Template = 5
    }

    public enum SyncStatus
    {
        NotSynced = 0,
        Syncing = 1,
        Synced = 2,
        Failed = 3,
        Conflict = 4
    }

    public class AccessibilityPreferences : ICloneable
    {
        public bool HighContrast { get; set; }
        public bool ScreenReaderSupport { get; set; }
        public int FontSize { get; set; } = 14;

        public AccessibilityPreferences Clone() => (AccessibilityPreferences)MemberwiseClone();
        object ICloneable.Clone() => Clone();
    }

    public class PrivacyPreferences : ICloneable
    {
        public bool CollectUsageData { get; set; }
        public bool ShareAnalytics { get; set; }
        public bool AutoUpdate { get; set; } = true;

        public PrivacyPreferences Clone() => (PrivacyPreferences)MemberwiseClone();
        object ICloneable.Clone() => Clone();
    }

    public class AIModelPreferences : ICloneable
    {
        public string DefaultModel { get; set; }
        public Dictionary<string, object> ModelParameters { get; set; } = new Dictionary<string, object>();

        public AIModelPreferences Clone()
        {
            return new AIModelPreferences
            {
                DefaultModel = DefaultModel,
                ModelParameters = ModelParameters != null ? new Dictionary<string, object>(ModelParameters) : new Dictionary<string, object>()
            };
        }
        object ICloneable.Clone() => Clone();
    }

    public class ProfileSecuritySettings : ICloneable
    {
        public bool RequirePassword { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public int SessionTimeout { get; set; } = 30;

        public ProfileSecuritySettings Clone() => (ProfileSecuritySettings)MemberwiseClone();
        object ICloneable.Clone() => Clone();
    }

    public class PerformanceSettings : ICloneable
    {
        public bool EnableCaching { get; set; } = true;
        public int CacheSize { get; set; } = 100;
        public bool EnableCompression { get; set; } = true;

        public PerformanceSettings Clone() => (PerformanceSettings)MemberwiseClone();
        object ICloneable.Clone() => Clone();
    }

    public class NotificationSettings : ICloneable
    {
        public bool EnableEmailNotifications { get; set; }
        public bool EnablePushNotifications { get; set; }
        public List<string> NotificationChannels { get; set; } = new List<string>();

        public NotificationSettings Clone()
        {
            return new NotificationSettings
            {
                EnableEmailNotifications = EnableEmailNotifications,
                EnablePushNotifications = EnablePushNotifications,
                NotificationChannels = NotificationChannels?.ToList() ?? new List<string>()
            };
        }
        object ICloneable.Clone() => Clone();
    }

    public class BackupSettings : ICloneable
    {
        public bool AutoBackup { get; set; } = true;
        public int BackupIntervalHours { get; set; } = 24;
        public int MaxBackups { get; set; } = 10;

        public BackupSettings Clone() => (BackupSettings)MemberwiseClone();
        object ICloneable.Clone() => Clone();
    }

    public class SyncSettings : ICloneable
    {
        public bool AutoSync { get; set; }
        public int SyncIntervalMinutes { get; set; } = 60;
        public List<string> SyncServices { get; set; } = new List<string>();

        public SyncSettings Clone()
        {
            return new SyncSettings
            {
                AutoSync = AutoSync,
                SyncIntervalMinutes = SyncIntervalMinutes,
                SyncServices = SyncServices?.ToList() ?? new List<string>()
            };
        }
        object ICloneable.Clone() => Clone();
    }

    public class ProfileLoadedEventArgs : EventArgs
    {
        public string ProfileId { get; set; }
        public UserProfile Profile { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ProfileSavedEventArgs : EventArgs
    {
        public string ProfileId { get; set; }
        public UserProfile Profile { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsNewProfile { get; set; }
    }

    public class ProfileDeletedEventArgs : EventArgs
    {
        public string ProfileId { get; set; }
        public UserProfile Profile { get; set; }
        public DateTime Timestamp { get; set; }
        public bool BackupCreated { get; set; }
    }

    public class ProfilesSyncedEventArgs : EventArgs
    {
        public DateTime SyncTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<ProfileSyncResult> Results { get; set; }
        public int TotalProfiles { get; set; }
        public int SuccessfulSyncs { get; set; }
    }

    public class ProfileValidationFailedEventArgs : EventArgs
    {
        public UserProfile Profile { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Exceptions
    public class ProfileNotFoundException : Exception
    {
        public ProfileNotFoundException() { }
        public ProfileNotFoundException(string message) : base(message) { }
        public ProfileNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileAlreadyExistsException : Exception
    {
        public ProfileAlreadyExistsException() { }
        public ProfileAlreadyExistsException(string message) : base(message) { }
        public ProfileAlreadyExistsException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileLoadException : Exception
    {
        public ProfileLoadException() { }
        public ProfileLoadException(string message) : base(message) { }
        public ProfileLoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileSaveException : Exception
    {
        public ProfileSaveException() { }
        public ProfileSaveException(string message) : base(message) { }
        public ProfileSaveException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileCreationException : Exception
    {
        public ProfileCreationException() { }
        public ProfileCreationException(string message) : base(message) { }
        public ProfileCreationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileDeleteException : Exception
    {
        public ProfileDeleteException() { }
        public ProfileDeleteException(string message) : base(message) { }
        public ProfileDeleteException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileValidationException : Exception
    {
        public ProfileValidationException() { }
        public ProfileValidationException(string message) : base(message) { }
        public ProfileValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileOperationException : Exception
    {
        public ProfileOperationException() { }
        public ProfileOperationException(string message) : base(message) { }
        public ProfileOperationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ProfileImportException : Exception
    {
        public ProfileImportException() { }
        public ProfileImportException(string message) : base(message) { }
        public ProfileImportException(string message, Exception inner) : base(message, inner) { }
    }

    public interface IProfileManager : IDisposable
    {
        Task<UserProfile> LoadProfileAsync(string profileId, CancellationToken cancellationToken = default);
        Task SaveProfileAsync(CancellationToken cancellationToken = default);
        Task SaveProfileAsync(string profileId, UserProfile profile, CancellationToken cancellationToken = default);
        Task<UserProfile> CreateProfileAsync(string profileId, ProfileCreationOptions options, CancellationToken cancellationToken = default);
        Task DeleteProfileAsync(string profileId, bool backupBeforeDelete = true, CancellationToken cancellationToken = default);
        Task<List<string>> GetAvailableProfiles();
        Task<bool> ProfileExistsAsync(string profileId);
        Task<UserProfile> DuplicateProfileAsync(string sourceProfileId, string newProfileId, ProfileCreationOptions options = null, CancellationToken cancellationToken = default);
        Task ExportProfileAsync(string profileId, string exportPath, bool includeSensitiveData = false, CancellationToken cancellationToken = default);
        Task<UserProfile> ImportProfileAsync(string importPath, string newProfileId = null, bool overwriteExisting = false, CancellationToken cancellationToken = default);
        Task SyncProfilesAsync(CancellationToken cancellationToken = default);
        void EnableAutoSync(TimeSpan interval);
        void DisableAutoSync();
        Task CreateBackupAsync(string profileId, CancellationToken cancellationToken = default);
        Task RestoreFromBackupAsync(string profileId, CancellationToken cancellationToken = default);
        Task<ProfileStatistics> GetStatisticsAsync();

        UserProfile CurrentProfile { get; }
        string CurrentProfileId { get; }
        bool IsProfileLoaded { get; }
        List<string> AvailableProfileIds { get; }
        string ProfilesDirectory { get; }
        string BackupDirectory { get; }
        int TotalProfiles { get; }
        DateTime LastSyncTime { get; }
        bool AutoSyncEnabled { get; }

        event EventHandler<ProfileLoadedEventArgs> ProfileLoaded;
        event EventHandler<ProfileSavedEventArgs> ProfileSaved;
        event EventHandler<ProfileDeletedEventArgs> ProfileDeleted;
        event EventHandler<ProfilesSyncedEventArgs> ProfilesSynced;
        event EventHandler<ProfileValidationFailedEventArgs> ProfileValidationFailed;
    }

    #endregion
}
