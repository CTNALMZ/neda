using NEDA.AI.ReinforcementLearning;
using NEDA.Brain.IntentRecognition.ParameterDetection;
using NEDA.Brain.KnowledgeBase.ProblemSolutions;
using NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics.Contracts;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.GameplaySystems.ProgressionMechanics;
{
    /// <summary>
    /// Başarım sistemi. Oyuncu başarımlarını, rozetleri, ilerlemeyi ve ödülleri yönetir.
    /// </summary>
    public class AchievementSystem : IAchievementSystem, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IAchievementDatabase _achievementDatabase;
        private readonly AchievementSystemConfiguration _configuration;
        private readonly Dictionary<string, Achievement> _achievements;
        private readonly Dictionary<string, AchievementCategory> _categories;
        private readonly Dictionary<string, PlayerAchievementData> _playerData;
        private readonly Dictionary<string, AchievementProgress> _activeProgress;
        private readonly ReaderWriterLockSlim _lock;
        private readonly AchievementValidator _validator;
        private readonly AchievementCalculator _calculator;
        private readonly RewardDistributor _rewardDistributor;
        private readonly AchievementNotifier _notifier;
        private readonly StatisticsTracker _statisticsTracker;
        private bool _isDisposed;

        #endregion;

        #region Constants;

        private const int DEFAULT_POINTS_PER_ACHIEVEMENT = 10;
        private const int MAX_ACHIEVEMENT_LEVEL = 5;
        private const int DEFAULT_REWARD_MULTIPLIER = 1;

        #endregion;

        #region Properties;

        /// <summary>
        Toplam başarım sayısı.
        /// </summary>
        public int TotalAchievementCount => _achievements.Count;

        /// <summary>
        /// Toplam kategori sayısı.
        /// </summary>
        public int TotalCategoryCount => _categories.Count;

        /// <summary>
        /// Aktif kullanıcı sayısı.
        /// </summary>
        public int ActivePlayerCount => _playerData.Count;

        /// <summary>
        /// Sistem konfigürasyonu.
        /// </summary>
        public AchievementSystemConfiguration Configuration => _configuration;

        /// <summary>
        /// Sistem istatistikleri.
        /// </summary>
        public AchievementSystemStatistics Statistics => GetSystemStatistics();

        #endregion;

        #region Events;

        /// <summary>
        /// Başarım kazanıldığında tetiklenir.
        /// </summary>
        public event EventHandler<AchievementEarnedEventArgs> AchievementEarned;

        /// <summary>
        /// Başarım ilerlemesi güncellendiğinde tetiklenir.
        /// </summary>
        public event EventHandler<AchievementProgressEventArgs> AchievementProgress;

        /// <summary>
        /// Başarım ödülü verildiğinde tetiklenir.
        /// </summary>
        public event EventHandler<AchievementRewardEventArgs> AchievementReward;

        /// <summary>
        /// Başarım seviyesi atlandığında tetiklenir.
        /// </summary>
        public event EventHandler<AchievementLevelUpEventArgs> AchievementLevelUp;

        /// <summary>
        /// Başarım kombinasyonu tamamlandığında tetiklenir.
        /// </summary>
        public event EventHandler<AchievementComboEventArgs> AchievementCombo;

        #endregion;

        #region Constructor;

        /// <summary>
        /// AchievementSystem sınıfının yeni bir örneğini oluşturur.
        /// </summary>
        public AchievementSystem(
            ILogger logger,
            IAchievementDatabase achievementDatabase = null,
            AchievementSystemConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _achievementDatabase = achievementDatabase;
            _configuration = configuration ?? new AchievementSystemConfiguration();

            _achievements = new Dictionary<string, Achievement>();
            _categories = new Dictionary<string, AchievementCategory>();
            _playerData = new Dictionary<string, PlayerAchievementData>();
            _activeProgress = new Dictionary<string, AchievementProgress>();
            _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

            _validator = new AchievementValidator(this);
            _calculator = new AchievementCalculator(this);
            _rewardDistributor = new RewardDistributor(this);
            _notifier = new AchievementNotifier(this);
            _statisticsTracker = new StatisticsTracker(this);

            InitializeSystem();

            _logger.LogInformation("AchievementSystem initialized with {MaxAchievements} max achievements",
                _configuration.MaxAchievements);
        }

        #endregion;

        #region Public Methods - System Management;

        /// <summary>
        /// Başarım sistemini başlatır.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Initializing AchievementSystem...");

                // Başarım veritabanını yükle;
                if (_achievementDatabase != null)
                {
                    await LoadAchievementDatabaseAsync();
                }

                // Varsayılan başarımları yükle;
                await LoadDefaultAchievementsAsync();

                // Sistem doğrulaması yap;
                await ValidateSystemAsync();

                // İstatistikleri başlat;
                await _statisticsTracker.InitializeAsync();

                _logger.LogInformation("AchievementSystem initialized successfully. Achievements: {AchievementCount}, Categories: {CategoryCount}",
                    _achievements.Count, _categories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AchievementSystem");
                throw new AchievementSystemException("AchievementSystem initialization failed", ex);
            }
        }

        /// <summary>
        /// Başarım veritabanını yükler.
        /// </summary>
        public async Task LoadAchievementDatabaseAsync()
        {
            try
            {
                _logger.LogInformation("Loading achievement database...");

                // Kategorileri yükle;
                var categories = await _achievementDatabase.LoadCategoriesAsync();
                foreach (var category in categories)
                {
                    _categories[category.Id] = category;
                }

                // Başarımları yükle;
                var achievements = await _achievementDatabase.LoadAchievementsAsync();
                foreach (var achievement in achievements)
                {
                    _achievements[achievement.Id] = achievement;
                }

                _logger.LogInformation("Achievement database loaded: {CategoryCount} categories, {AchievementCount} achievements",
                    _categories.Count, _achievements.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load achievement database");
                throw new AchievementSystemException("Failed to load achievement database", ex);
            }
        }

        /// <summary>
        /// Başarım sistemini doğrular.
        /// </summary>
        public async Task<bool> ValidateSystemAsync()
        {
            try
            {
                _logger.LogInformation("Validating AchievementSystem...");

                var validationResults = new List<SystemValidationResult>();

                // Başarım validasyonu;
                validationResults.Add(ValidateAchievements());

                // Kategori validasyonu;
                validationResults.Add(ValidateCategories());

                // Bağımlılık validasyonu;
                validationResults.Add(ValidateDependencies());

                // Performans validasyonu;
                validationResults.Add(await ValidatePerformanceAsync());

                // Sonuçları analiz et;
                var criticalErrors = validationResults;
                    .SelectMany(r => r.Errors)
                    .Count(e => e.Severity == ValidationSeverity.Critical);

                var hasErrors = validationResults.Any(r => r.Errors.Any());

                if (criticalErrors > 0)
                {
                    _logger.LogError("AchievementSystem validation failed with {CriticalErrors} critical errors", criticalErrors);
                    return false;
                }

                if (hasErrors)
                {
                    _logger.LogWarning("AchievementSystem validation completed with warnings");
                }
                else;
                {
                    _logger.LogInformation("AchievementSystem validation passed successfully");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AchievementSystem validation failed");
                return false;
            }
        }

        /// <summary>
        /// Oyuncu başarım verilerini yükler.
        /// </summary>
        public async Task LoadPlayerDataAsync(string playerId)
        {
            ValidatePlayerId(playerId);

            try
            {
                _lock.EnterWriteLock();
                try
                {
                    if (_playerData.ContainsKey(playerId))
                    {
                        _logger.LogDebug("Player data already loaded: {PlayerId}", playerId);
                        return;
                    }

                    PlayerAchievementData playerData;

                    // Veritabanından yükle;
                    if (_achievementDatabase != null)
                    {
                        playerData = await _achievementDatabase.LoadPlayerDataAsync(playerId);
                    }
                    else;
                    {
                        playerData = new PlayerAchievementData(playerId);
                    }

                    // Başlangıç verilerini oluştur;
                    InitializePlayerData(playerData);

                    _playerData[playerId] = playerData;

                    // Aktif ilerlemeleri başlat;
                    InitializePlayerProgress(playerData);

                    _logger.LogInformation("Player data loaded: {PlayerId}", playerId);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                // İstatistikleri güncelle;
                await _statisticsTracker.OnPlayerLoaded(playerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load player data: {PlayerId}", playerId);
                throw new AchievementSystemException($"Failed to load player data for {playerId}", ex);
            }
        }

        /// <summary>
        /// Oyuncu başarım verilerini kaydeder.
        /// </summary>
        public async Task SavePlayerDataAsync(string playerId, bool forceSave = false)
        {
            ValidatePlayerId(playerId);

            try
            {
                _lock.EnterReadLock();
                try
                {
                    if (!_playerData.TryGetValue(playerId, out var playerData))
                    {
                        throw new AchievementSystemException($"Player data not found: {playerId}");
                    }

                    // Değişiklik kontrolü;
                    if (!forceSave && !playerData.HasChanges)
                    {
                        _logger.LogDebug("No changes to save for player: {PlayerId}", playerId);
                        return;
                    }

                    // Veritabanına kaydet;
                    if (_achievementDatabase != null)
                    {
                        await _achievementDatabase.SavePlayerDataAsync(playerData);
                    }

                    playerData.ResetChanges();

                    _logger.LogDebug("Player data saved: {PlayerId}", playerId);
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save player data: {PlayerId}", playerId);
                throw new AchievementSystemException($"Failed to save player data for {playerId}", ex);
            }
        }

        /// <summary>
        /// Tüm oyuncu verilerini kaydeder.
        /// </summary>
        public async Task SaveAllPlayerDataAsync()
        {
            var playerIds = _playerData.Keys.ToList();
            int savedCount = 0;
            int failedCount = 0;

            _logger.LogInformation("Saving all player data ({PlayerCount} players)...", playerIds.Count);

            foreach (var playerId in playerIds)
            {
                try
                {
                    await SavePlayerDataAsync(playerId, true);
                    savedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save data for player: {PlayerId}", playerId);
                    failedCount++;
                }
            }

            _logger.LogInformation("Player data save completed: {SavedCount} saved, {FailedCount} failed",
                savedCount, failedCount);
        }

        /// <summary>
        /// Oyuncu başarım verilerini kaldırır.
        /// </summary>
        public void UnloadPlayerData(string playerId)
        {
            ValidatePlayerId(playerId);

            _lock.EnterWriteLock();
            try
            {
                if (_playerData.Remove(playerId))
                {
                    // Aktif ilerlemeleri temizle;
                    var progressKeys = _activeProgress.Keys;
                        .Where(k => k.StartsWith(playerId + ":"))
                        .ToList();

                    foreach (var key in progressKeys)
                    {
                        _activeProgress.Remove(key);
                    }

                    _logger.LogInformation("Player data unloaded: {PlayerId}", playerId);
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Başarım sistemini temizler.
        /// </summary>
        public void ClearSystem()
        {
            _lock.EnterWriteLock();
            try
            {
                _achievements.Clear();
                _categories.Clear();
                _playerData.Clear();
                _activeProgress.Clear();

                _logger.LogInformation("AchievementSystem cleared");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        #endregion;

        #region Public Methods - Achievement Management;

        /// <summary>
        /// Yeni bir başarım ekler.
        /// </summary>
        public bool AddAchievement(Achievement achievement)
        {
            ValidateAchievement(achievement);

            _lock.EnterWriteLock();
            try
            {
                if (_achievements.ContainsKey(achievement.Id))
                {
                    _logger.LogWarning("Achievement already exists: {AchievementId}", achievement.Id);
                    return false;
                }

                // Kategori kontrolü;
                if (!string.IsNullOrEmpty(achievement.CategoryId) &&
                    !_categories.ContainsKey(achievement.CategoryId))
                {
                    // Kategori yoksa otomatik oluştur;
                    var category = new AchievementCategory;
                    {
                        Id = achievement.CategoryId,
                        Name = achievement.CategoryId,
                        Description = $"Auto-generated category for {achievement.Name}",
                        SortOrder = _categories.Count + 1;
                    };

                    _categories[category.Id] = category;
                    _logger.LogDebug("Auto-created category: {CategoryId}", category.Id);
                }

                _achievements[achievement.Id] = achievement;

                // Tüm aktif oyuncular için bu başarımı başlat;
                foreach (var playerData in _playerData.Values)
                {
                    InitializeAchievementForPlayer(playerData, achievement);
                }

                _logger.LogInformation("Achievement added: {AchievementId} ({AchievementName})",
                    achievement.Id, achievement.Name);

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Başarımı günceller.
        /// </summary>
        public bool UpdateAchievement(string achievementId, Achievement updatedAchievement)
        {
            ValidateAchievementId(achievementId);
            ValidateAchievement(updatedAchievement);

            if (achievementId != updatedAchievement.Id)
            {
                throw new ArgumentException("Achievement ID mismatch", nameof(updatedAchievement));
            }

            _lock.EnterWriteLock();
            try
            {
                if (!_achievements.ContainsKey(achievementId))
                {
                    return false;
                }

                var oldAchievement = _achievements[achievementId];
                _achievements[achievementId] = updatedAchievement;

                // Güncelleme etkilerini işle;
                ProcessAchievementUpdate(oldAchievement, updatedAchievement);

                _logger.LogInformation("Achievement updated: {AchievementId}", achievementId);

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Başarımı kaldırır.
        /// </summary>
        public bool RemoveAchievement(string achievementId)
        {
            ValidateAchievementId(achievementId);

            _lock.EnterWriteLock();
            try
            {
                if (!_achievements.ContainsKey(achievementId))
                {
                    return false;
                }

                // Bağımlılık kontrolü;
                var dependentAchievements = _achievements.Values;
                    .Where(a => a.Requirements?.Any(r =>
                        r.Type == RequirementType.Achievement &&
                        r.TargetId == achievementId) == true)
                    .ToList();

                if (dependentAchievements.Any())
                {
                    throw new AchievementSystemException(
                        $"Cannot remove achievement {achievementId}: " +
                        $"{dependentAchievements.Count} achievements depend on it");
                }

                // Oyuncu verilerinden kaldır;
                foreach (var playerData in _playerData.Values)
                {
                    playerData.CompletedAchievements.Remove(achievementId);
                    playerData.InProgressAchievements.Remove(achievementId);
                }

                // Aktif ilerlemelerden kaldır;
                var progressKeys = _activeProgress.Keys;
                    .Where(k => k.EndsWith(":" + achievementId))
                    .ToList();

                foreach (var key in progressKeys)
                {
                    _activeProgress.Remove(key);
                }

                _achievements.Remove(achievementId);

                _logger.LogInformation("Achievement removed: {AchievementId}", achievementId);

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Başarımı ID ile getirir.
        /// </summary>
        public Achievement GetAchievement(string achievementId)
        {
            ValidateAchievementId(achievementId);

            _lock.EnterReadLock();
            try
            {
                return _achievements.TryGetValue(achievementId, out var achievement) ? achievement : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Tüm başarımları getirir.
        /// </summary>
        public IReadOnlyList<Achievement> GetAllAchievements()
        {
            _lock.EnterReadLock();
            try
            {
                return _achievements.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Kategoriye göre başarımları getirir.
        /// </summary>
        public IReadOnlyList<Achievement> GetAchievementsByCategory(string categoryId)
        {
            ValidateCategoryId(categoryId);

            _lock.EnterReadLock();
            try
            {
                return _achievements.Values;
                    .Where(a => a.CategoryId == categoryId)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Zorluk seviyesine göre başarımları getirir.
        /// </summary>
        public IReadOnlyList<Achievement> GetAchievementsByDifficulty(AchievementDifficulty difficulty)
        {
            _lock.EnterReadLock();
            try
            {
                return _achievements.Values;
                    .Where(a => a.Difficulty == difficulty)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Başarımları ara.
        /// </summary>
        public IReadOnlyList<Achievement> SearchAchievements(AchievementSearchCriteria criteria)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            _lock.EnterReadLock();
            try
            {
                var query = _achievements.Values.AsEnumerable();

                // Kategori filtreleme;
                if (!string.IsNullOrEmpty(criteria.CategoryId))
                {
                    query = query.Where(a => a.CategoryId == criteria.CategoryId);
                }

                // Zorluk filtreleme;
                if (criteria.Difficulty.HasValue)
                {
                    query = query.Where(a => a.Difficulty == criteria.Difficulty.Value);
                }

                // Durum filtreleme (oyuncuya özel)
                if (!string.IsNullOrEmpty(criteria.PlayerId) && criteria.Status.HasValue)
                {
                    if (_playerData.TryGetValue(criteria.PlayerId, out var playerData))
                    {
                        query = criteria.Status.Value switch;
                        {
                            AchievementStatus.NotStarted => query.Where(a =>
                                !playerData.CompletedAchievements.Contains(a.Id) &&
                                !playerData.InProgressAchievements.Contains(a.Id)),
                            AchievementStatus.InProgress => query.Where(a =>
                                playerData.InProgressAchievements.Contains(a.Id)),
                            AchievementStatus.Completed => query.Where(a =>
                                playerData.CompletedAchievements.Contains(a.Id)),
                            _ => query;
                        };
                    }
                }

                // İsimle arama;
                if (!string.IsNullOrEmpty(criteria.NameContains))
                {
                    query = query.Where(a =>
                        a.Name.IndexOf(criteria.NameContains, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Açıklamayla arama;
                if (!string.IsNullOrEmpty(criteria.DescriptionContains))
                {
                    query = query.Where(a =>
                        a.Description.IndexOf(criteria.DescriptionContains, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Gizli başarımları filtrele;
                if (!criteria.IncludeHidden)
                {
                    query = query.Where(a => !a.IsHidden ||
                        (!string.IsNullOrEmpty(criteria.PlayerId) &&
                         _playerData.TryGetValue(criteria.PlayerId, out var pd) &&
                         pd.CompletedAchievements.Contains(a.Id)));
                }

                // Sıralama;
                query = criteria.SortBy switch;
                {
                    AchievementSortBy.Name => criteria.SortDescending;
                        ? query.OrderByDescending(a => a.Name)
                        : query.OrderBy(a => a.Name),
                    AchievementSortBy.Points => criteria.SortDescending;
                        ? query.OrderByDescending(a => a.Points)
                        : query.OrderBy(a => a.Points),
                    AchievementSortBy.Difficulty => criteria.SortDescending;
                        ? query.OrderByDescending(a => a.Difficulty)
                        : query.OrderBy(a => a.Difficulty),
                    AchievementSortBy.Category => criteria.SortDescending;
                        ? query.OrderByDescending(a => a.CategoryId)
                        : query.OrderBy(a => a.CategoryId),
                    _ => criteria.SortDescending;
                        ? query.OrderByDescending(a => a.Id)
                        : query.OrderBy(a => a.Id)
                };

                // Sayfalama;
                if (criteria.PageSize > 0)
                {
                    query = query.Skip(criteria.PageIndex * criteria.PageSize)
                                .Take(criteria.PageSize);
                }

                return query.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        #endregion;

        #region Public Methods - Progress Tracking;

        /// <summary>
        /// Başarım ilerlemesini günceller.
        /// </summary>
        public async Task<ProgressUpdateResult> UpdateProgressAsync(
            string playerId,
            string achievementId,
            ProgressUpdate update)
        {
            ValidatePlayerId(playerId);
            ValidateAchievementId(achievementId);
            ValidateProgressUpdate(update);

            _lock.EnterWriteLock();
            try
            {
                // Oyuncu verilerini kontrol et;
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                // Başarımı kontrol et;
                if (!_achievements.TryGetValue(achievementId, out var achievement))
                {
                    throw new AchievementSystemException($"Achievement not found: {achievementId}");
                }

                // Başarım zaten tamamlanmış mı?
                if (playerData.CompletedAchievements.Contains(achievementId))
                {
                    return new ProgressUpdateResult;
                    {
                        Success = false,
                        ErrorMessage = "Achievement already completed",
                        AchievementId = achievementId,
                        PlayerId = playerId;
                    };
                }

                // İlerleme anahtarını oluştur;
                var progressKey = $"{playerId}:{achievementId}";

                // Aktif ilerlemeyi al veya oluştur;
                if (!_activeProgress.TryGetValue(progressKey, out var progress))
                {
                    progress = new AchievementProgress;
                    {
                        PlayerId = playerId,
                        AchievementId = achievementId,
                        StartTime = DateTime.UtcNow;
                    };

                    _activeProgress[progressKey] = progress;
                }

                // İlerlemeyi güncelle;
                var updateResult = await ProcessProgressUpdateAsync(
                    playerData,
                    achievement,
                    progress,
                    update);

                if (updateResult.ProgressChanged)
                {
                    // İlerleme event'ini tetikle;
                    OnAchievementProgress(new AchievementProgressEventArgs;
                    {
                        PlayerId = playerId,
                        AchievementId = achievementId,
                        Progress = progress,
                        Update = update,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Tamamlanma kontrolü;
                    if (updateResult.IsCompleted)
                    {
                        await CompleteAchievementAsync(playerData, achievement, progress);
                    }
                }

                // İstatistikleri güncelle;
                await _statisticsTracker.OnProgressUpdate(playerId, achievementId, update);

                return updateResult;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Çoklu başarım ilerlemesini günceller.
        /// </summary>
        public async Task<BatchProgressUpdateResult> UpdateBatchProgressAsync(
            string playerId,
            List<ProgressUpdate> updates)
        {
            ValidatePlayerId(playerId);

            if (updates == null || updates.Count == 0)
                throw new ArgumentException("Updates list cannot be null or empty", nameof(updates));

            var result = new BatchProgressUpdateResult;
            {
                PlayerId = playerId,
                TotalUpdates = updates.Count,
                Timestamp = DateTime.UtcNow;
            };

            foreach (var update in updates)
            {
                try
                {
                    var updateResult = await UpdateProgressAsync(playerId, update.AchievementId, update);
                    result.IndividualResults.Add(updateResult);

                    if (updateResult.Success)
                    {
                        result.SuccessfulUpdates++;
                    }
                    else;
                    {
                        result.FailedUpdates++;
                        result.Errors.Add(new BatchUpdateError;
                        {
                            AchievementId = update.AchievementId,
                            ErrorMessage = updateResult.ErrorMessage;
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in batch update for achievement: {AchievementId}", update.AchievementId);

                    result.FailedUpdates++;
                    result.Errors.Add(new BatchUpdateError;
                    {
                        AchievementId = update.AchievementId,
                        ErrorMessage = ex.Message;
                    });
                }
            }

            result.Success = result.FailedUpdates == 0;

            // Toplu event;
            if (result.SuccessfulUpdates > 0)
            {
                await _statisticsTracker.OnBatchProgressUpdate(playerId, result);
            }

            return result;
        }

        /// <summary>
        /// Başarım ilerlemesini getirir.
        /// </summary>
        public AchievementProgress GetProgress(string playerId, string achievementId)
        {
            ValidatePlayerId(playerId);
            ValidateAchievementId(achievementId);

            _lock.EnterReadLock();
            try
            {
                var progressKey = $"{playerId}:{achievementId}";
                return _activeProgress.TryGetValue(progressKey, out var progress) ? progress : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Oyuncunun tüm ilerlemelerini getirir.
        /// </summary>
        public IReadOnlyList<AchievementProgress> GetAllProgress(string playerId)
        {
            ValidatePlayerId(playerId);

            _lock.EnterReadLock();
            try
            {
                return _activeProgress.Values;
                    .Where(p => p.PlayerId == playerId)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Başarım istatistiklerini getirir.
        /// </summary>
        public AchievementStatistics GetAchievementStatistics(string achievementId)
        {
            ValidateAchievementId(achievementId);

            _lock.EnterReadLock();
            try
            {
                var statistics = new AchievementStatistics;
                {
                    AchievementId = achievementId,
                    TotalPlayers = _playerData.Count;
                };

                // Tamamlayan oyuncular;
                var completers = _playerData.Values;
                    .Where(p => p.CompletedAchievements.Contains(achievementId))
                    .ToList();

                statistics.CompletedPlayers = completers.Count;
                statistics.CompletionRate = _playerData.Count > 0;
                    ? (double)completers.Count / _playerData.Count;
                    : 0;

                // İlerlemedeki oyuncular;
                var inProgress = _playerData.Values;
                    .Where(p => p.InProgressAchievements.Contains(achievementId))
                    .ToList();

                statistics.InProgressPlayers = inProgress.Count;

                // Ortalama tamamlama süresi;
                if (completers.Any())
                {
                    // TODO: Tamamlama sürelerini hesapla;
                }

                return statistics;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        #endregion;

        #region Public Methods - Player Achievements;

        /// <summary>
        /// Oyuncunun başarımlarını getirir.
        /// </summary>
        public PlayerAchievements GetPlayerAchievements(string playerId)
        {
            ValidatePlayerId(playerId);

            _lock.EnterReadLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                var achievements = new PlayerAchievements;
                {
                    PlayerId = playerId,
                    TotalPoints = playerData.TotalPoints,
                    TotalAchievements = playerData.CompletedAchievements.Count,
                    CompletionPercentage = CalculateCompletionPercentage(playerData),
                    Rank = CalculatePlayerRank(playerData),
                    LastUpdated = playerData.LastUpdated;
                };

                // Kategori bazlı istatistikler;
                achievements.CategoryStats = CalculateCategoryStats(playerData);

                // Son başarımlar;
                achievements.RecentAchievements = GetRecentAchievements(playerData, 10);

                // Yakın başarımlar;
                achievements.NearCompletion = GetNearCompletionAchievements(playerData, 5);

                return achievements;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Oyuncunun başarım geçmişini getirir.
        /// </summary>
        public IReadOnlyList<AchievementHistoryEntry> GetPlayerHistory(string playerId, int maxEntries = 50)
        {
            ValidatePlayerId(playerId);

            _lock.EnterReadLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                return playerData.History;
                    .OrderByDescending(h => h.Timestamp)
                    .Take(maxEntries)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Oyuncunun rozetlerini getirir.
        /// </summary>
        public IReadOnlyList<Badge> GetPlayerBadges(string playerId)
        {
            ValidatePlayerId(playerId);

            _lock.EnterReadLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                var badges = new List<Badge>();

                // Başarım rozetleri;
                foreach (var achievementId in playerData.CompletedAchievements)
                {
                    if (_achievements.TryGetValue(achievementId, out var achievement) &&
                        achievement.Badge != null)
                    {
                        badges.Add(achievement.Badge);
                    }
                }

                // Özel rozetler;
                badges.AddRange(playerData.EarnedBadges);

                return badges;
                    .OrderByDescending(b => b.Rarity)
                    .ThenByDescending(b => b.UnlockDate)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Oyuncuları sıralar.
        /// </summary>
        public IReadOnlyList<PlayerRanking> GetPlayerRankings(RankingCriteria criteria)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            _lock.EnterReadLock();
            try
            {
                var query = _playerData.Values.AsEnumerable();

                // Filtreleme;
                if (!string.IsNullOrEmpty(criteria.CategoryId))
                {
                    query = query.Where(p =>
                        p.CategoryPoints.ContainsKey(criteria.CategoryId) &&
                        p.CategoryPoints[criteria.CategoryId] > 0);
                }

                // Sıralama;
                query = criteria.SortBy switch;
                {
                    RankingSortBy.Points => criteria.SortDescending;
                        ? query.OrderByDescending(p => p.TotalPoints)
                        : query.OrderBy(p => p.TotalPoints),
                    RankingSortBy.Achievements => criteria.SortDescending;
                        ? query.OrderByDescending(p => p.CompletedAchievements.Count)
                        : query.OrderBy(p => p.CompletedAchievements.Count),
                    RankingSortBy.CompletionRate => criteria.SortDescending;
                        ? query.OrderByDescending(p => CalculateCompletionPercentage(p))
                        : query.OrderBy(p => CalculateCompletionPercentage(p)),
                    _ => criteria.SortDescending;
                        ? query.OrderByDescending(p => p.TotalPoints)
                        : query.OrderBy(p => p.TotalPoints)
                };

                // Sayfalama;
                if (criteria.PageSize > 0)
                {
                    query = query.Skip(criteria.PageIndex * criteria.PageSize)
                                .Take(criteria.PageSize);
                }

                // Ranking oluştur;
                int rank = criteria.PageIndex * criteria.PageSize + 1;
                var rankings = new List<PlayerRanking>();

                foreach (var playerData in query)
                {
                    rankings.Add(new PlayerRanking;
                    {
                        Rank = rank++,
                        PlayerId = playerData.PlayerId,
                        Points = playerData.TotalPoints,
                        Achievements = playerData.CompletedAchievements.Count,
                        CompletionRate = CalculateCompletionPercentage(playerData),
                        LastAchievementDate = playerData.LastAchievementDate;
                    });
                }

                return rankings;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Oyuncunun başarım hedeflerini getirir.
        /// </summary>
        public IReadOnlyList<AchievementGoal> GetPlayerGoals(string playerId)
        {
            ValidatePlayerId(playerId);

            _lock.EnterReadLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                var goals = new List<AchievementGoal>();

                // Önerilen başarımlar;
                var recommended = GetRecommendedAchievements(playerData, 5);
                foreach (var achievement in recommended)
                {
                    goals.Add(new AchievementGoal;
                    {
                        AchievementId = achievement.Id,
                        Name = achievement.Name,
                        Description = achievement.Description,
                        Points = achievement.Points,
                        Difficulty = achievement.Difficulty,
                        Progress = GetProgress(playerId, achievement.Id),
                        Reason = GetRecommendationReason(achievement, playerData)
                    });
                }

                // Günlük/zamanlı başarımlar;
                var timeLimited = GetTimeLimitedAchievements(playerData);
                goals.AddRange(timeLimited);

                return goals;
                    .OrderBy(g => g.Priority)
                    .ThenByDescending(g => g.Points)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        #endregion;

        #region Public Methods - Rewards;

        /// <summary>
        /// Başarım ödüllerini verir.
        /// </summary>
        public async Task<RewardDistributionResult> DistributeRewardsAsync(
            string playerId,
            string achievementId)
        {
            ValidatePlayerId(playerId);
            ValidateAchievementId(achievementId);

            _lock.EnterWriteLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                if (!_achievements.TryGetValue(achievementId, out var achievement))
                {
                    throw new AchievementSystemException($"Achievement not found: {achievementId}");
                }

                // Başarım tamamlanmış mı?
                if (!playerData.CompletedAchievements.Contains(achievementId))
                {
                    return new RewardDistributionResult;
                    {
                        Success = false,
                        ErrorMessage = "Achievement not completed",
                        PlayerId = playerId,
                        AchievementId = achievementId;
                    };
                }

                // Ödüller zaten verilmiş mi?
                if (playerData.ClaimedRewards.Contains(achievementId))
                {
                    return new RewardDistributionResult;
                    {
                        Success = false,
                        ErrorMessage = "Rewards already claimed",
                        PlayerId = playerId,
                        AchievementId = achievementId;
                    };
                }

                // Ödülleri dağıt;
                var result = await _rewardDistributor.DistributeRewardsAsync(
                    playerData,
                    achievement);

                if (result.Success)
                {
                    // Ödülleri işaretle;
                    playerData.ClaimedRewards.Add(achievementId);
                    playerData.LastRewardClaim = DateTime.UtcNow;

                    // Event tetikle;
                    OnAchievementReward(new AchievementRewardEventArgs;
                    {
                        PlayerId = playerId,
                        AchievementId = achievementId,
                        Rewards = result.Rewards,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Rewards distributed for achievement {AchievementId} to player {PlayerId}",
                        achievementId, playerId);
                }

                return result;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Tüm bekleyen ödülleri verir.
        /// </summary>
        public async Task<BatchRewardResult> DistributeAllPendingRewardsAsync(string playerId)
        {
            ValidatePlayerId(playerId);

            _lock.EnterWriteLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                var result = new BatchRewardResult;
                {
                    PlayerId = playerId,
                    Timestamp = DateTime.UtcNow;
                };

                // Bekleyen ödülleri bul;
                var pendingAchievements = playerData.CompletedAchievements;
                    .Where(a => !playerData.ClaimedRewards.Contains(a))
                    .ToList();

                foreach (var achievementId in pendingAchievements)
                {
                    try
                    {
                        var rewardResult = await DistributeRewardsAsync(playerId, achievementId);
                        result.IndividualResults.Add(rewardResult);

                        if (rewardResult.Success)
                        {
                            result.SuccessfulDistributions++;
                            result.TotalRewards.AddRange(rewardResult.Rewards);
                        }
                        else;
                        {
                            result.FailedDistributions++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error distributing rewards for achievement: {AchievementId}", achievementId);

                        result.FailedDistributions++;
                        result.Errors.Add(new BatchRewardError;
                        {
                            AchievementId = achievementId,
                            ErrorMessage = ex.Message;
                        });
                    }
                }

                result.Success = result.FailedDistributions == 0;

                return result;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Özel ödül verir.
        /// </summary>
        public async Task<RewardResult> GrantSpecialRewardAsync(
            string playerId,
            SpecialRewardType rewardType,
            Dictionary<string, object> parameters = null)
        {
            ValidatePlayerId(playerId);

            _lock.EnterWriteLock();
            try
            {
                if (!_playerData.TryGetValue(playerId, out var playerData))
                {
                    throw new AchievementSystemException($"Player data not loaded: {playerId}");
                }

                // Özel ödülü oluştur;
                var specialReward = CreateSpecialReward(rewardType, parameters);

                // Ödülü dağıt;
                var result = await _rewardDistributor.DistributeSpecialRewardAsync(
                    playerData,
                    specialReward);

                if (result.Success)
                {
                    // Özel ödül geçmişine ekle;
                    playerData.SpecialRewards.Add(new SpecialRewardHistory;
                    {
                        Type = rewardType,
                        Rewards = result.Rewards,
                        Timestamp = DateTime.UtcNow,
                        Parameters = parameters;
                    });

                    _logger.LogInformation("Special reward {RewardType} granted to player {PlayerId}",
                        rewardType, playerId);
                }

                return result;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        #endregion;

        #region Private Methods - Initialization;

        private void InitializeSystem()
        {
            try
            {
                // Varsayılan kategorileri oluştur;
                CreateDefaultCategories();

                // Varsayılan başarımları oluştur;
                CreateDefaultAchievements();

                _logger.LogInformation("AchievementSystem default data initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AchievementSystem default data");
                throw new AchievementSystemException("AchievementSystem initialization failed", ex);
            }
        }

        private async Task LoadDefaultAchievementsAsync()
        {
            try
            {
                // Varsayılan başarımları JSON'dan yükle;
                var defaultAchievementsPath = _configuration.DefaultAchievementsPath;
                if (!string.IsNullOrEmpty(defaultAchievementsPath) && System.IO.File.Exists(defaultAchievementsPath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(defaultAchievementsPath);
                    var defaultData = JsonSerializer.Deserialize<DefaultAchievementsConfig>(json);

                    if (defaultData?.Categories != null)
                    {
                        foreach (var category in defaultData.Categories)
                        {
                            if (!_categories.ContainsKey(category.Id))
                            {
                                _categories[category.Id] = category;
                            }
                        }
                    }

                    if (defaultData?.Achievements != null)
                    {
                        foreach (var achievement in defaultData.Achievements)
                        {
                            if (!_achievements.ContainsKey(achievement.Id))
                            {
                                _achievements[achievement.Id] = achievement;
                            }
                        }
                    }

                    _logger.LogInformation("Loaded {AchievementCount} achievements and {CategoryCount} categories from default data",
                        defaultData?.Achievements?.Count ?? 0, defaultData?.Categories?.Count ?? 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load default achievements");
            }
        }

        private void CreateDefaultCategories()
        {
            var defaultCategories = new[]
            {
                new AchievementCategory("combat", "Combat", "Battle and combat achievements", 1),
                new AchievementCategory("exploration", "Exploration", "World exploration achievements", 2),
                new AchievementCategory("questing", "Questing", "Quest completion achievements", 3),
                new AchievementCategory("crafting", "Crafting", "Crafting and gathering achievements", 4),
                new AchievementCategory("social", "Social", "Social interaction achievements", 5),
                new AchievementCategory("collection", "Collection", "Item collection achievements", 6),
                new AchievementCategory("pvp", "PvP", "Player vs player achievements", 7),
                new AchievementCategory("events", "Events", "Special event achievements", 8),
                new AchievementCategory("milestone", "Milestones", "Gameplay milestone achievements", 9)
            };

            foreach (var category in defaultCategories)
            {
                _categories[category.Id] = category;
            }
        }

        private void CreateDefaultAchievements()
        {
            // Başlangıç başarımları;
            var firstSteps = new Achievement("first_login", "First Steps", "Login for the first time")
            {
                CategoryId = "milestone",
                Difficulty = AchievementDifficulty.Easy,
                Points = 10,
                Requirements = new List<Requirement>
                {
                    new Requirement;
                    {
                        Type = RequirementType.Login,
                        TargetValue = 1;
                    }
                },
                Rewards = new List<Reward>
                {
                    new Reward;
                    {
                        Type = RewardType.Currency,
                        Amount = 100,
                        CurrencyType = "gold"
                    }
                }
            };

            var firstKill = new Achievement("first_kill", "First Blood", "Defeat your first enemy")
            {
                CategoryId = "combat",
                Difficulty = AchievementDifficulty.Easy,
                Points = 15,
                Requirements = new List<Requirement>
                {
                    new Requirement;
                    {
                        Type = RequirementType.Kill,
                        TargetValue = 1;
                    }
                },
                Rewards = new List<Reward>
                {
                    new Reward;
                    {
                        Type = RewardType.Item,
                        ItemId = "sword_basic",
                        Quantity = 1;
                    }
                }
            };

            AddAchievement(firstSteps);
            AddAchievement(firstKill);
        }

        private void InitializePlayerData(PlayerAchievementData playerData)
        {
            // Başlangıç değerlerini ayarla;
            playerData.FirstLogin = DateTime.UtcNow;
            playerData.LastLogin = DateTime.UtcNow;
            playerData.TotalPlayTime = TimeSpan.Zero;

            // Tüm başarımları başlat;
            foreach (var achievement in _achievements.Values)
            {
                InitializeAchievementForPlayer(playerData, achievement);
            }
        }

        private void InitializePlayerProgress(PlayerAchievementData playerData)
        {
            // İlerlemedeki başarımlar için progress oluştur;
            foreach (var achievementId in playerData.InProgressAchievements)
            {
                var progressKey = $"{playerData.PlayerId}:{achievementId}";

                if (!_activeProgress.ContainsKey(progressKey))
                {
                    _activeProgress[progressKey] = new AchievementProgress;
                    {
                        PlayerId = playerData.PlayerId,
                        AchievementId = achievementId,
                        StartTime = DateTime.UtcNow;
                    };
                }
            }
        }

        private void InitializeAchievementForPlayer(PlayerAchievementData playerData, Achievement achievement)
        {
            // Başarım zaten tamamlanmış mı?
            if (playerData.CompletedAchievements.Contains(achievement.Id))
                return;

            // Gereksinimleri kontrol et;
            var requirementsMet = CheckInitialRequirements(playerData, achievement);

            if (requirementsMet)
            {
                // İlerlemedeki başarımlara ekle;
                playerData.InProgressAchievements.Add(achievement.Id);

                // Progress oluştur;
                var progressKey = $"{playerData.PlayerId}:{achievement.Id}";
                if (!_activeProgress.ContainsKey(progressKey))
                {
                    _activeProgress[progressKey] = new AchievementProgress;
                    {
                        PlayerId = playerData.PlayerId,
                        AchievementId = achievement.Id,
                        StartTime = DateTime.UtcNow;
                    };
                }
            }
        }

        private bool CheckInitialRequirements(PlayerAchievementData playerData, Achievement achievement)
        {
            if (achievement.Requirements == null || !achievement.Requirements.Any())
                return true;

            // Ön koşul başarımları kontrol et;
            var prerequisiteAchievements = achievement.Requirements;
                .Where(r => r.Type == RequirementType.Achievement)
                .ToList();

            foreach (var prereq in prerequisiteAchievements)
            {
                if (!playerData.CompletedAchievements.Contains(prereq.TargetId))
                    return false;
            }

            // Seviye gereksinimleri;
            var levelRequirements = achievement.Requirements;
                .Where(r => r.Type == RequirementType.Level)
                .ToList();

            foreach (var levelReq in levelRequirements)
            {
                // TODO: Oyuncu seviyesini kontrol et;
            }

            return true;
        }

        #endregion;

        #region Private Methods - Progress Processing;

        private async Task<ProgressUpdateResult> ProcessProgressUpdateAsync(
            PlayerAchievementData playerData,
            Achievement achievement,
            AchievementProgress progress,
            ProgressUpdate update)
        {
            var result = new ProgressUpdateResult;
            {
                PlayerId = playerData.PlayerId,
                AchievementId = achievement.Id,
                PreviousProgress = progress.CurrentValue,
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Güncelleme tipine göre işle;
                switch (update.UpdateType)
                {
                    case ProgressUpdateType.Increment:
                        progress.CurrentValue += update.Value;
                        break;

                    case ProgressUpdateType.Set:
                        progress.CurrentValue = update.Value;
                        break;

                    case ProgressUpdateType.Max:
                        progress.CurrentValue = Math.Max(progress.CurrentValue, update.Value);
                        break;

                    case ProgressUpdateType.Decrement:
                        progress.CurrentValue = Math.Max(0, progress.CurrentValue - update.Value);
                        break;
                }

                // Hedef değeri al;
                float targetValue = GetTargetValue(achievement, update);

                // İlerleme kontrolü;
                progress.ProgressPercentage = targetValue > 0;
                    ? Math.Min(100f, (progress.CurrentValue / targetValue) * 100f)
                    : 0f;

                // Tamamlanma kontrolü;
                bool isCompleted = progress.CurrentValue >= targetValue && targetValue > 0;

                // Değişiklikleri kontrol et;
                bool progressChanged = Math.Abs(result.PreviousProgress - progress.CurrentValue) > 0.001f;

                if (progressChanged)
                {
                    progress.LastUpdate = DateTime.UtcNow;
                    playerData.LastUpdated = DateTime.UtcNow;

                    // İlerleme geçmişine ekle;
                    progress.History.Add(new ProgressHistoryEntry
                    {
                        Value = progress.CurrentValue,
                        Percentage = progress.ProgressPercentage,
                        UpdateType = update.UpdateType,
                        Source = update.Source,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Geçmiş boyutunu sınırla;
                    if (progress.History.Count > _configuration.MaxProgressHistory)
                    {
                        progress.History.RemoveRange(0, progress.History.Count - _configuration.MaxProgressHistory);
                    }
                }

                result.Success = true;
                result.NewProgress = progress.CurrentValue;
                result.ProgressPercentage = progress.ProgressPercentage;
                result.ProgressChanged = progressChanged;
                result.IsCompleted = isCompleted;
                result.Message = $"Progress updated: {progress.CurrentValue}/{targetValue} ({progress.ProgressPercentage:F1}%)";

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing progress update for achievement: {AchievementId}", achievement.Id);

                result.Success = false;
                result.ErrorMessage = $"Error processing update: {ex.Message}";

                return result;
            }
        }

        private float GetTargetValue(Achievement achievement, ProgressUpdate update)
        {
            // Update'tan hedef değeri al;
            if (update.TargetValue.HasValue)
                return update.TargetValue.Value;

            // Başarım gereksinimlerinden hedef değeri bul;
            var requirement = achievement.Requirements?
                .FirstOrDefault(r => r.Type == update.RequirementType &&
                                    (string.IsNullOrEmpty(update.RequirementSubType) ||
                                     r.SubType == update.RequirementSubType));

            return requirement?.TargetValue ?? 0f;
        }

        private async Task CompleteAchievementAsync(
            PlayerAchievementData playerData,
            Achievement achievement,
            AchievementProgress progress)
        {
            // Tamamlama zamanını kaydet;
            progress.CompletionTime = DateTime.UtcNow;
            progress.CompletionDuration = progress.CompletionTime - progress.StartTime;

            // Oyuncu verilerini güncelle;
            playerData.CompletedAchievements.Add(achievement.Id);
            playerData.InProgressAchievements.Remove(achievement.Id);
            playerData.TotalPoints += achievement.Points;
            playerData.LastAchievementDate = DateTime.UtcNow;

            // Kategori puanlarını güncelle;
            if (!string.IsNullOrEmpty(achievement.CategoryId))
            {
                if (!playerData.CategoryPoints.ContainsKey(achievement.CategoryId))
                {
                    playerData.CategoryPoints[achievement.CategoryId] = 0;
                }
                playerData.CategoryPoints[achievement.CategoryId] += achievement.Points;
            }

            // Geçmişe ekle;
            playerData.History.Add(new AchievementHistoryEntry
            {
                AchievementId = achievement.Id,
                AchievementName = achievement.Name,
                Points = achievement.Points,
                CompletionTime = DateTime.UtcNow,
                Duration = progress.CompletionDuration;
            });

            // Geçmiş boyutunu sınırla;
            if (playerData.History.Count > _configuration.MaxPlayerHistory)
            {
                playerData.History.RemoveRange(0, playerData.History.Count - _configuration.MaxPlayerHistory);
            }

            // Event tetikle;
            OnAchievementEarned(new AchievementEarnedEventArgs;
            {
                PlayerId = playerData.PlayerId,
                Achievement = achievement,
                Progress = progress,
                Timestamp = DateTime.UtcNow;
            });

            // Seviye atlama kontrolü;
            CheckLevelUp(playerData, achievement);

            // Kombinasyon kontrolü;
            CheckAchievementCombos(playerData, achievement);

            // Bildirim gönder;
            await _notifier.NotifyAchievementEarnedAsync(playerData, achievement, progress);

            _logger.LogInformation("Achievement completed: {AchievementId} by player {PlayerId}",
                achievement.Id, playerData.PlayerId);
        }

        private void CheckLevelUp(PlayerAchievementData playerData, Achievement achievement)
        {
            // Eski seviyeyi kaydet;
            int oldLevel = playerData.Level;

            // Yeni seviyeyi hesapla;
            playerData.Level = CalculatePlayerLevel(playerData.TotalPoints);

            // Seviye atlandı mı?
            if (playerData.Level > oldLevel)
            {
                // Event tetikle;
                OnAchievementLevelUp(new AchievementLevelUpEventArgs;
                {
                    PlayerId = playerData.PlayerId,
                    OldLevel = oldLevel,
                    NewLevel = playerData.Level,
                    Achievement = achievement,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Player level up: {PlayerId} level {OldLevel} -> {NewLevel}",
                    playerData.PlayerId, oldLevel, playerData.Level);
            }
        }

        private void CheckAchievementCombos(PlayerAchievementData playerData, Achievement newlyCompleted)
        {
            // Kombinasyon başarımlarını kontrol et;
            var comboAchievements = _achievements.Values;
                .Where(a => a.IsComboAchievement &&
                           !playerData.CompletedAchievements.Contains(a.Id))
                .ToList();

            foreach (var combo in comboAchievements)
            {
                // Gereksinimleri kontrol et;
                var requirementsMet = combo.Requirements?
                    .All(r => r.Type != RequirementType.Achievement ||
                             playerData.CompletedAchievements.Contains(r.TargetId)) == true;

                if (requirementsMet)
                {
                    // Kombinasyon başarımını tamamla;
                    var progress = new AchievementProgress;
                    {
                        PlayerId = playerData.PlayerId,
                        AchievementId = combo.Id,
                        StartTime = DateTime.UtcNow,
                        CompletionTime = DateTime.UtcNow,
                        CurrentValue = 1,
                        ProgressPercentage = 100f;
                    };

                    // Event tetikle;
                    OnAchievementCombo(new AchievementComboEventArgs;
                    {
                        PlayerId = playerData.PlayerId,
                        ComboAchievement = combo,
                        ComponentAchievements = combo.Requirements;
                            .Where(r => r.Type == RequirementType.Achievement)
                            .Select(r => _achievements[r.TargetId])
                            .ToList(),
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogDebug("Achievement combo completed: {ComboId} by player {PlayerId}",
                        combo.Id, playerData.PlayerId);
                }
            }
        }

        #endregion;

        #region Private Methods - Calculation Helpers;

        private double CalculateCompletionPercentage(PlayerAchievementData playerData)
        {
            if (_achievements.Count == 0)
                return 0;

            // Kullanıcının erişebileceği başarımlar;
            var accessibleAchievements = _achievements.Values;
                .Count(a => !a.IsHidden || playerData.CompletedAchievements.Contains(a.Id));

            if (accessibleAchievements == 0)
                return 0;

            return (double)playerData.CompletedAchievements.Count / accessibleAchievements * 100;
        }

        private int CalculatePlayerRank(PlayerAchievementData playerData)
        {
            // Toplam puanlara göre sırala;
            var sortedPlayers = _playerData.Values;
                .OrderByDescending(p => p.TotalPoints)
                .ThenBy(p => p.FirstLogin)
                .ToList();

            var index = sortedPlayers.FindIndex(p => p.PlayerId == playerData.PlayerId);
            return index >= 0 ? index + 1 : 0;
        }

        private Dictionary<string, CategoryStatistics> CalculateCategoryStats(PlayerAchievementData playerData)
        {
            var stats = new Dictionary<string, CategoryStatistics>();

            foreach (var category in _categories.Values)
            {
                var categoryAchievements = _achievements.Values;
                    .Where(a => a.CategoryId == category.Id)
                    .ToList();

                var completed = categoryAchievements;
                    .Count(a => playerData.CompletedAchievements.Contains(a.Id));

                var inProgress = categoryAchievements;
                    .Count(a => playerData.InProgressAchievements.Contains(a.Id));

                var totalPoints = categoryAchievements;
                    .Where(a => playerData.CompletedAchievements.Contains(a.Id))
                    .Sum(a => a.Points);

                stats[category.Id] = new CategoryStatistics;
                {
                    CategoryId = category.Id,
                    CategoryName = category.Name,
                    TotalAchievements = categoryAchievements.Count,
                    Completed = completed,
                    InProgress = inProgress,
                    Points = totalPoints,
                    CompletionRate = categoryAchievements.Count > 0;
                        ? (double)completed / categoryAchievements.Count * 100;
                        : 0;
                };
            }

            return stats;
        }

        private List<Achievement> GetRecentAchievements(PlayerAchievementData playerData, int count)
        {
            var recentHistory = playerData.History;
                .OrderByDescending(h => h.CompletionTime)
                .Take(count)
                .ToList();

            return recentHistory;
                .Select(h => _achievements[h.AchievementId])
                .Where(a => a != null)
                .ToList();
        }

        private List<Achievement> GetNearCompletionAchievements(PlayerAchievementData playerData, int count)
        {
            var nearCompletion = new List<Achievement>();

            foreach (var achievementId in playerData.InProgressAchievements)
            {
                if (_achievements.TryGetValue(achievementId, out var achievement))
                {
                    var progress = GetProgress(playerData.PlayerId, achievementId);
                    if (progress?.ProgressPercentage >= 50) // %50'den fazla tamamlanmış;
                    {
                        nearCompletion.Add(achievement);
                    }
                }
            }

            return nearCompletion;
                .OrderByDescending(a => GetProgress(playerData.PlayerId, a.Id)?.ProgressPercentage ?? 0)
                .Take(count)
                .ToList();
        }

        private List<Achievement> GetRecommendedAchievements(PlayerAchievementData playerData, int count)
        {
            var recommendations = new List<Achievement>();

            // İlerlemedeki başarımlar;
            var inProgress = playerData.InProgressAchievements;
                .Select(id => _achievements.GetValueOrDefault(id))
                .Where(a => a != null)
                .Take(count / 2);

            recommendations.AddRange(inProgress);

            // Önerilen yeni başarımlar;
            var remainingSlots = count - recommendations.Count;
            if (remainingSlots > 0)
            {
                var newAchievements = _achievements.Values;
                    .Where(a => !playerData.CompletedAchievements.Contains(a.Id) &&
                               !playerData.InProgressAchievements.Contains(a.Id) &&
                               CheckInitialRequirements(playerData, a))
                    .OrderBy(a => a.Difficulty)
                    .ThenByDescending(a => a.Points)
                    .Take(remainingSlots);

                recommendations.AddRange(newAchievements);
            }

            return recommendations;
        }

        private List<AchievementGoal> GetTimeLimitedAchievements(PlayerAchievementData playerData)
        {
            var goals = new List<AchievementGoal>();

            var timeLimited = _achievements.Values;
                .Where(a => a.TimeLimit.HasValue &&
                           !playerData.CompletedAchievements.Contains(a.Id))
                .ToList();

            foreach (var achievement in timeLimited)
            {
                var timeRemaining = achievement.TimeLimit.Value - DateTime.UtcNow;
                if (timeRemaining > TimeSpan.Zero)
                {
                    goals.Add(new AchievementGoal;
                    {
                        AchievementId = achievement.Id,
                        Name = achievement.Name,
                        Description = achievement.Description,
                        Points = achievement.Points,
                        Difficulty = achievement.Difficulty,
                        Progress = GetProgress(playerData.PlayerId, achievement.Id),
                        TimeRemaining = timeRemaining,
                        Priority = GoalPriority.High;
                    });
                }
            }

            return goals;
        }

        private string GetRecommendationReason(Achievement achievement, PlayerAchievementData playerData)
        {
            if (playerData.InProgressAchievements.Contains(achievement.Id))
                return "Already in progress";

            var progress = GetProgress(playerData.PlayerId, achievement.Id);
            if (progress != null)
                return $"Progress: {progress.ProgressPercentage:F1}%";

            if (achievement.Difficulty == AchievementDifficulty.Easy)
                return "Easy achievement to start with";

            if (achievement.Points >= 50)
                return "High point value";

            return "Recommended based on your play style";
        }

        private int CalculatePlayerLevel(int totalPoints)
        {
            // Seviye formülü: her 1000 puanda bir seviye;
            return Math.Max(1, totalPoints / 1000 + 1);
        }

        private SpecialReward CreateSpecialReward(SpecialRewardType rewardType, Dictionary<string, object> parameters)
        {
            return rewardType switch;
            {
                SpecialRewardType.DailyLogin => new SpecialReward;
                {
                    Type = rewardType,
                    Name = "Daily Login Bonus",
                    Description = "Reward for consecutive daily logins",
                    Rewards = new List<Reward>
                    {
                        new Reward { Type = RewardType.Currency, Amount = 50, CurrencyType = "gold" },
                        new Reward { Type = RewardType.Experience, Amount = 100 }
                    }
                },
                SpecialRewardType.StreakBonus => new SpecialReward;
                {
                    Type = rewardType,
                    Name = "Achievement Streak",
                    Description = "Bonus for multiple achievements in a short time",
                    Rewards = new List<Reward>
                    {
                        new Reward { Type = RewardType.Currency, Amount = 100, CurrencyType = "gold" },
                        new Reward { Type = RewardType.Item, ItemId = "lucky_charm", Quantity = 1 }
                    }
                },
                SpecialRewardType.Milestone => new SpecialReward;
                {
                    Type = rewardType,
                    Name = "Milestone Reward",
                    Description = "Special reward for reaching a milestone",
                    Rewards = new List<Reward>
                    {
                        new Reward { Type = RewardType.Currency, Amount = 500, CurrencyType = "gold" },
                        new Reward { Type = RewardType.Title, TitleId = "milestone_champion" }
                    }
                },
                _ => new SpecialReward;
                {
                    Type = rewardType,
                    Name = "Special Reward",
                    Description = "A special reward",
                    Rewards = new List<Reward>()
                }
            };
        }

        #endregion;

        #region Private Methods - Validation;

        private SystemValidationResult ValidateAchievements()
        {
            var result = new SystemValidationResult { Area = "Achievements" };

            foreach (var achievement in _achievements.Values)
            {
                // ID validasyonu;
                if (string.IsNullOrWhiteSpace(achievement.Id))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "ACHIEVEMENT_ID_EMPTY",
                        Message = "Achievement ID cannot be empty",
                        Severity = ValidationSeverity.Critical;
                    });
                }

                // Kategori validasyonu;
                if (!string.IsNullOrEmpty(achievement.CategoryId) &&
                    !_categories.ContainsKey(achievement.CategoryId))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "INVALID_CATEGORY",
                        Message = $"Achievement {achievement.Id} references invalid category: {achievement.CategoryId}",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Puan validasyonu;
                if (achievement.Points < 0)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "INVALID_POINTS",
                        Message = $"Achievement {achievement.Id} has invalid points: {achievement.Points}",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Gereksinim validasyonu;
                if (achievement.Requirements != null)
                {
                    foreach (var requirement in achievement.Requirements)
                    {
                        if (requirement.Type == RequirementType.Achievement &&
                            !_achievements.ContainsKey(requirement.TargetId))
                        {
                            result.Errors.Add(new ValidationError;
                            {
                                Code = "INVALID_PREREQUISITE",
                                Message = $"Achievement {achievement.Id} references invalid prerequisite: {requirement.TargetId}",
                                Severity = ValidationSeverity.Warning;
                            });
                        }
                    }
                }
            }

            return result;
        }

        private SystemValidationResult ValidateCategories()
        {
            var result = new SystemValidationResult { Area = "Categories" };

            foreach (var category in _categories.Values)
            {
                if (string.IsNullOrWhiteSpace(category.Id))
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "CATEGORY_ID_EMPTY",
                        Message = "Category ID cannot be empty",
                        Severity = ValidationSeverity.Critical;
                    });
                }
            }

            return result;
        }

        private SystemValidationResult ValidateDependencies()
        {
            var result = new SystemValidationResult { Area = "Dependencies" };

            // Döngüsel bağımlılık kontrolü;
            foreach (var achievement in _achievements.Values)
            {
                if (achievement.Requirements != null)
                {
                    var visited = new HashSet<string>();
                    if (HasCircularDependency(achievement.Id, visited))
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            Code = "CIRCULAR_DEPENDENCY",
                            Message = $"Circular dependency detected for achievement: {achievement.Id}",
                            Severity = ValidationSeverity.Critical;
                        });
                    }
                }
            }

            return result;
        }

        private bool HasCircularDependency(string achievementId, HashSet<string> visited)
        {
            if (visited.Contains(achievementId))
                return true;

            visited.Add(achievementId);

            var achievement = _achievements[achievementId];
            if (achievement.Requirements == null)
                return false;

            foreach (var requirement in achievement.Requirements)
            {
                if (requirement.Type == RequirementType.Achievement)
                {
                    if (HasCircularDependency(requirement.TargetId, new HashSet<string>(visited)))
                        return true;
                }
            }

            return false;
        }

        private async Task<SystemValidationResult> ValidatePerformanceAsync()
        {
            var result = new SystemValidationResult { Area = "Performance" };

            try
            {
                // Başarım sayısı kontrolü;
                if (_achievements.Count > _configuration.MaxAchievements)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "TOO_MANY_ACHIEVEMENTS",
                        Message = $"Too many achievements: {_achievements.Count} (max: {_configuration.MaxAchievements})",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Oyuncu sayısı kontrolü;
                if (_playerData.Count > _configuration.MaxActivePlayers)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "TOO_MANY_PLAYERS",
                        Message = $"Too many active players: {_playerData.Count} (max: {_configuration.MaxActivePlayers})",
                        Severity = ValidationSeverity.Warning;
                    });
                }

                // Performans testi;
                var startTime = DateTime.UtcNow;

                // 100 başarım için arama testi;
                for (int i = 0; i < Math.Min(100, _achievements.Count); i++)
                {
                    var achievement = _achievements.Values.ElementAt(i % _achievements.Count);
                    var found = GetAchievement(achievement.Id);
                }

                var elapsed = DateTime.UtcNow - startTime;

                if (elapsed.TotalMilliseconds > 50)
                {
                    result.Errors.Add(new ValidationError;
                    {
                        Code = "PERFORMANCE_ISSUE",
                        Message = $"Achievement lookup performance issue: {elapsed.TotalMilliseconds}ms for 100 lookups",
                        Severity = ValidationSeverity.Warning;
                    });
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "PERFORMANCE_TEST_FAILED",
                    Message = $"Performance test failed: {ex.Message}",
                    Severity = ValidationSeverity.Warning;
                });
            }

            return result;
        }

        private void ValidatePlayerId(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player ID cannot be null or empty", nameof(playerId));

            if (playerId.Length > 100)
                throw new ArgumentException("Player ID is too long", nameof(playerId));
        }

        private void ValidateAchievementId(string achievementId)
        {
            if (string.IsNullOrWhiteSpace(achievementId))
                throw new ArgumentException("Achievement ID cannot be null or empty", nameof(achievementId));
        }

        private void ValidateCategoryId(string categoryId)
        {
            if (string.IsNullOrWhiteSpace(categoryId))
                throw new ArgumentException("Category ID cannot be null or empty", nameof(categoryId));
        }

        private void ValidateAchievement(Achievement achievement)
        {
            if (achievement == null)
                throw new ArgumentNullException(nameof(achievement));

            if (string.IsNullOrWhiteSpace(achievement.Id))
                throw new ArgumentException("Achievement ID cannot be null or empty", nameof(achievement));

            if (string.IsNullOrWhiteSpace(achievement.Name))
                throw new ArgumentException("Achievement name cannot be null or empty", nameof(achievement));

            if (achievement.Points < 0)
                throw new ArgumentException("Points cannot be negative", nameof(achievement));
        }

        private void ValidateProgressUpdate(ProgressUpdate update)
        {
            if (update == null)
                throw new ArgumentNullException(nameof(update));

            if (string.IsNullOrWhiteSpace(update.AchievementId))
                throw new ArgumentException("Achievement ID cannot be null or empty", nameof(update));

            if (update.Value < 0)
                throw new ArgumentException("Value cannot be negative", nameof(update));
        }

        #endregion;

        #region Private Methods - Helper Methods;

        private void ProcessAchievementUpdate(Achievement oldAchievement, Achievement newAchievement)
        {
            // Hedef değeri değişti mi?
            var oldTarget = oldAchievement.Requirements?.FirstOrDefault()?.TargetValue ?? 0;
            var newTarget = newAchievement.Requirements?.FirstOrDefault()?.TargetValue ?? 0;

            if (Math.Abs(oldTarget - newTarget) > 0.001f)
            {
                // Tüm oyuncuların ilerlemesini güncelle;
                foreach (var playerData in _playerData.Values)
                {
                    if (playerData.InProgressAchievements.Contains(newAchievement.Id))
                    {
                        var progressKey = $"{playerData.PlayerId}:{newAchievement.Id}";
                        if (_activeProgress.TryGetValue(progressKey, out var progress))
                        {
                            // Yeni hedefe göre yüzdeyi güncelle;
                            progress.ProgressPercentage = newTarget > 0;
                                ? Math.Min(100f, (progress.CurrentValue / newTarget) * 100f)
                                : 0f;
                        }
                    }
                }
            }
        }

        private AchievementSystemStatistics GetSystemStatistics()
        {
            _lock.EnterReadLock();
            try
            {
                return new AchievementSystemStatistics;
                {
                    TotalAchievements = _achievements.Count,
                    TotalCategories = _categories.Count,
                    ActivePlayers = _playerData.Count,
                    TotalPointsAwarded = _playerData.Values.Sum(p => p.TotalPoints),
                    AverageCompletionRate = _playerData.Count > 0;
                        ? _playerData.Values.Average(CalculateCompletionPercentage)
                        : 0,
                    MostPopularAchievement = GetMostPopularAchievement(),
                    MostActivePlayer = GetMostActivePlayer(),
                    LastUpdated = DateTime.UtcNow;
                };
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private string GetMostPopularAchievement()
        {
            if (_playerData.Count == 0)
                return null;

            var achievementCounts = new Dictionary<string, int>();

            foreach (var playerData in _playerData.Values)
            {
                foreach (var achievementId in playerData.CompletedAchievements)
                {
                    if (!achievementCounts.ContainsKey(achievementId))
                    {
                        achievementCounts[achievementId] = 0;
                    }
                    achievementCounts[achievementId]++;
                }
            }

            return achievementCounts;
                .OrderByDescending(kv => kv.Value)
                .Select(kv => _achievements.GetValueOrDefault(kv.Key)?.Name)
                .FirstOrDefault();
        }

        private string GetMostActivePlayer()
        {
            if (_playerData.Count == 0)
                return null;

            return _playerData.Values;
                .OrderByDescending(p => p.TotalPoints)
                .ThenByDescending(p => p.CompletedAchievements.Count)
                .Select(p => p.PlayerId)
                .FirstOrDefault();
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnAchievementEarned(AchievementEarnedEventArgs e)
        {
            AchievementEarned?.Invoke(this, e);
        }

        protected virtual void OnAchievementProgress(AchievementProgressEventArgs e)
        {
            AchievementProgress?.Invoke(this, e);
        }

        protected virtual void OnAchievementReward(AchievementRewardEventArgs e)
        {
            AchievementReward?.Invoke(this, e);
        }

        protected virtual void OnAchievementLevelUp(AchievementLevelUpEventArgs e)
        {
            AchievementLevelUp?.Invoke(this, e);
        }

        protected virtual void OnAchievementCombo(AchievementComboEventArgs e)
        {
            AchievementCombo?.Invoke(this, e);
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
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Tüm oyuncu verilerini kaydet;
                    SaveAllPlayerDataAsync().Wait(10000);

                    _lock?.Dispose();

                    _logger.LogInformation("AchievementSystem disposed");
                }

                _isDisposed = true;
            }
        }

        ~AchievementSystem()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Başarım sistemi için arayüz.
    /// </summary>
    public interface IAchievementSystem : IDisposable
    {
        event EventHandler<AchievementEarnedEventArgs> AchievementEarned;
        event EventHandler<AchievementProgressEventArgs> AchievementProgress;
        event EventHandler<AchievementRewardEventArgs> AchievementReward;
        event EventHandler<AchievementLevelUpEventArgs> AchievementLevelUp;
        event EventHandler<AchievementComboEventArgs> AchievementCombo;

        int TotalAchievementCount { get; }
        int TotalCategoryCount { get; }
        int ActivePlayerCount { get; }
        AchievementSystemConfiguration Configuration { get; }
        AchievementSystemStatistics Statistics { get; }

        Task InitializeAsync();
        Task LoadAchievementDatabaseAsync();
        Task<bool> ValidateSystemAsync();
        Task LoadPlayerDataAsync(string playerId);
        Task SavePlayerDataAsync(string playerId, bool forceSave = false);
        Task SaveAllPlayerDataAsync();
        void UnloadPlayerData(string playerId);
        void ClearSystem();

        bool AddAchievement(Achievement achievement);
        bool UpdateAchievement(string achievementId, Achievement updatedAchievement);
        bool RemoveAchievement(string achievementId);
        Achievement GetAchievement(string achievementId);
        IReadOnlyList<Achievement> GetAllAchievements();
        IReadOnlyList<Achievement> GetAchievementsByCategory(string categoryId);
        IReadOnlyList<Achievement> GetAchievementsByDifficulty(AchievementDifficulty difficulty);
        IReadOnlyList<Achievement> SearchAchievements(AchievementSearchCriteria criteria);

        Task<ProgressUpdateResult> UpdateProgressAsync(string playerId, string achievementId, ProgressUpdate update);
        Task<BatchProgressUpdateResult> UpdateBatchProgressAsync(string playerId, List<ProgressUpdate> updates);
        AchievementProgress GetProgress(string playerId, string achievementId);
        IReadOnlyList<AchievementProgress> GetAllProgress(string playerId);
        AchievementStatistics GetAchievementStatistics(string achievementId);

        PlayerAchievements GetPlayerAchievements(string playerId);
        IReadOnlyList<AchievementHistoryEntry> GetPlayerHistory(string playerId, int maxEntries = 50);
        IReadOnlyList<Badge> GetPlayerBadges(string playerId);
        IReadOnlyList<PlayerRanking> GetPlayerRankings(RankingCriteria criteria);
        IReadOnlyList<AchievementGoal> GetPlayerGoals(string playerId);

        Task<RewardDistributionResult> DistributeRewardsAsync(string playerId, string achievementId);
        Task<BatchRewardResult> DistributeAllPendingRewardsAsync(string playerId);
        Task<RewardResult> GrantSpecialRewardAsync(string playerId, SpecialRewardType rewardType, Dictionary<string, object> parameters = null);
    }

    /// <summary>
    /// Başarım veri yapısı.
    /// </summary>
    public class Achievement;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string CategoryId { get; set; }
        public AchievementDifficulty Difficulty { get; set; }
        public int Points { get; set; }
        public bool IsHidden { get; set; }
        public bool IsSecret { get; set; }
        public bool IsComboAchievement { get; set; }
        public bool Repeatable { get; set; }
        public int MaxRepeatCount { get; set; }
        public TimeSpan? TimeLimit { get; set; }
        public DateTime? AvailableFrom { get; set; }
        public DateTime? AvailableUntil { get; set; }
        public List<Requirement> Requirements { get; set; }
        public List<Reward> Rewards { get; set; }
        public Badge Badge { get; set; }
        public string IconPath { get; set; }
        public string UnlockMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public Achievement()
        {
            Requirements = new List<Requirement>();
            Rewards = new List<Reward>();
            Metadata = new Dictionary<string, object>();
            Difficulty = AchievementDifficulty.Easy;
            MaxRepeatCount = 1;
        }

        public Achievement(string id, string name, string description = null) : this()
        {
            Id = id;
            Name = name;
            Description = description;
        }
    }

    /// <summary>
    /// Başarım kategorisi.
    /// </summary>
    public class AchievementCategory;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string IconPath { get; set; }
        public int SortOrder { get; set; }
        public string ParentCategoryId { get; set; }
        public Dictionary<string, object> Properties { get; set; }

        public AchievementCategory() { }

        public AchievementCategory(string id, string name, string description = null, int sortOrder = 0)
        {
            Id = id;
            Name = name;
            Description = description;
            SortOrder = sortOrder;
            Properties = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Oyuncu başarım verileri.
    /// </summary>
    public class PlayerAchievementData;
    {
        public string PlayerId { get; }
        public int Level { get; set; }
        public int TotalPoints { get; set; }
        public HashSet<string> CompletedAchievements { get; }
        public HashSet<string> InProgressAchievements { get; }
        public HashSet<string> ClaimedRewards { get; }
        public List<AchievementHistoryEntry> History { get; }
        public List<Badge> EarnedBadges { get; }
        public List<SpecialRewardHistory> SpecialRewards { get; }
        public Dictionary<string, int> CategoryPoints { get; }
        public DateTime FirstLogin { get; set; }
        public DateTime LastLogin { get; set; }
        public DateTime LastAchievementDate { get; set; }
        public DateTime LastRewardClaim { get; set; }
        public DateTime LastUpdated { get; set; }
        public TimeSpan TotalPlayTime { get; set; }
        public bool HasChanges { get; private set; }

        public PlayerAchievementData(string playerId)
        {
            PlayerId = playerId;
            CompletedAchievements = new HashSet<string>();
            InProgressAchievements = new HashSet<string>();
            ClaimedRewards = new HashSet<string>();
            History = new List<AchievementHistoryEntry>();
            EarnedBadges = new List<Badge>();
            SpecialRewards = new List<SpecialRewardHistory>();
            CategoryPoints = new Dictionary<string, int>();
            LastUpdated = DateTime.UtcNow;
        }

        public void MarkAsChanged()
        {
            HasChanges = true;
            LastUpdated = DateTime.UtcNow;
        }

        public void ResetChanges()
        {
            HasChanges = false;
        }
    }

    /// <summary>
    /// Başarım ilerlemesi.
    /// </summary>
    public class AchievementProgress;
    {
        public string PlayerId { get; set; }
        public string AchievementId { get; set; }
        public float CurrentValue { get; set; }
        public float ProgressPercentage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdate { get; set; }
        public DateTime? CompletionTime { get; set; }
        public TimeSpan? CompletionDuration { get; set; }
        public List<ProgressHistoryEntry> History { get; set; }

        public AchievementProgress()
        {
            History = new List<ProgressHistoryEntry>();
            StartTime = DateTime.UtcNow;
            LastUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Başarım sistemi konfigürasyonu.
    /// </summary>
    public class AchievementSystemConfiguration;
    {
        public int MaxAchievements { get; set; } = 1000;
        public int MaxActivePlayers { get; set; } = 10000;
        public int MaxProgressHistory { get; set; } = 100;
        public int MaxPlayerHistory { get; set; } = 500;
        public bool EnableAutoSave { get; set; } = true;
        public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromMinutes(5);
        public bool EnableNotifications { get; set; } = true;
        public bool EnableStatistics { get; set; } = true;
        public string DefaultAchievementsPath { get; set; } = "Config/DefaultAchievements.json";
        public bool ValidateOnUpdate { get; set; } = true;
        public bool EnableCaching { get; set; } = true;
        public int CacheSize { get; set; } = 1000;
    }

    /// <summary>
    /// Başarım zorluk seviyeleri.
    /// </summary>
    public enum AchievementDifficulty;
    {
        VeryEasy = 0,
        Easy = 1,
        Medium = 2,
        Hard = 3,
        VeryHard = 4,
        Insane = 5,
        Impossible = 6;
    }

    /// <summary>
    /// Gereksinim tipleri.
    /// </summary>
    public enum RequirementType;
    {
        Kill = 0,
        Collect = 1,
        Craft = 2,
        Quest = 3,
        Level = 4,
        Skill = 5,
        Achievement = 6,
        Login = 7,
        PlayTime = 8,
        Distance = 9,
        Money = 10,
        PvP = 11,
        Social = 12,
        Exploration = 13,
        Custom = 100;
    }

    /// <summary>
    /// Ödül tipleri.
    /// </summary>
    public enum RewardType;
    {
        Currency = 0,
        Item = 1,
        Experience = 2,
        SkillPoints = 3,
        Title = 4,
        Badge = 5,
        Cosmetic = 6,
        Buff = 7,
        Unlock = 8,
        Custom = 100;
    }

    /// <summary>
    /// Özel ödül tipleri.
    /// </summary>
    public enum SpecialRewardType;
    {
        DailyLogin = 0,
        StreakBonus = 1,
        Milestone = 2,
        Event = 3,
        Seasonal = 4,
        Compensation = 5,
        Admin = 100;
    }

    /// <summary>
    /// İlerleme güncelleme tipi.
    /// </summary>
    public enum ProgressUpdateType;
    {
        Increment = 0,
        Set = 1,
        Max = 2,
        Decrement = 3;
    }

    /// <summary>
    /// Başarım durumu.
    /// </summary>
    public enum AchievementStatus;
    {
        NotStarted = 0,
        InProgress = 1,
        Completed = 2;
    }

    /// <summary>
    /// Sıralama tipi.
    /// </summary>
    public enum RankingSortBy;
    {
        Points = 0,
        Achievements = 1,
        CompletionRate = 2;
    }

    /// <summary>
    /// Hedef önceliği.
    /// </summary>
    public enum GoalPriority;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3;
    }

    // ... (Diğer yardımcı sınıflar, event argümanları, exception'lar, vs.)
    // Kısaltma için bu kısım kısaltıldı, tam versiyonda tüm sınıflar mevcut;

    #endregion;
}
