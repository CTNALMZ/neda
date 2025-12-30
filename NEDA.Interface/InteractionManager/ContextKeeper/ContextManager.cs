// NEDA.Interface/InteractionManager/ContextKeeper/ContextManager.cs;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.AI.MachineLearning;
using NEDA.API.Versioning;
using NEDA.Brain.MemorySystem;
using NEDA.CharacterSystems.DialogueSystem.BranchingNarratives;
using NEDA.Communication.DialogSystem.TopicHandler;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.UserProfiles;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Interface.InteractionManager.ConversationHistory;
using NEDA.Interface.InteractionManager.PreferenceLearner;
using NEDA.Interface.InteractionManager.SessionHandler;
using NEDA.Interface.InteractionManager.UserProfileManager;
using NEDA.SecurityModules.AdvancedSecurity.Authorization;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.InteractionManager.ContextKeeper;
{
    /// <summary>
    /// Bağlam yöneticisi - kullanıcı etkileşimlerini, oturum durumlarını ve çevresel bağlamları yönetir;
    /// Derin bağlam anlayışı ve durum takibi sağlar;
    /// </summary>
    public class ContextManager : IContextManager, IDisposable;
    {
        private readonly ILogger<ContextManager> _logger;
        private readonly ContextManagerConfig _config;
        private readonly IEventBus _eventBus;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ILongTermMemory _longTermMemory;
        private readonly ISessionManager _sessionManager;
        private readonly IProfileManager _profileManager;
        private readonly IPreferenceEngine _preferenceEngine;
        private readonly IHistoryManager _historyManager;
        private readonly IMLModel _mlModel;
        private readonly IPermissionService _permissionService;

        private readonly ConcurrentDictionary<string, InteractionContext> _activeContexts;
        private readonly ConcurrentDictionary<string, EnvironmentContext> _environmentContexts;
        private readonly ConcurrentDictionary<string, List<ContextEvent>> _contextHistory;

        private readonly CancellationTokenSource _cleanupCts;
        private Task _cleanupTask;
        private bool _isDisposed;
        private readonly System.Timers.Timer _healthTimer;
        private readonly object _syncRoot = new object();

        /// <summary>
        /// Bağlam değiştiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<ContextChangedEventArgs> ContextChanged;

        /// <summary>
        /// Bağlam uyumsuzluğu tespit edildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<ContextConflictEventArgs> ContextConflictDetected;

        /// <summary>
        /// Bağlam yöneticisi;
        /// </summary>
        public ContextManager(
            ILogger<ContextManager> logger,
            IOptions<ContextManagerConfig> config,
            IEventBus eventBus,
            IShortTermMemory shortTermMemory,
            ILongTermMemory longTermMemory,
            ISessionManager sessionManager,
            IProfileManager profileManager,
            IPreferenceEngine preferenceEngine,
            IHistoryManager historyManager,
            IMLModel mlModel,
            IPermissionService permissionService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
            _preferenceEngine = preferenceEngine ?? throw new ArgumentNullException(nameof(preferenceEngine));
            _historyManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
            _mlModel = mlModel ?? throw new ArgumentNullException(nameof(mlModel));
            _permissionService = permissionService ?? throw new ArgumentNullException(nameof(permissionService));

            _activeContexts = new ConcurrentDictionary<string, InteractionContext>();
            _environmentContexts = new ConcurrentDictionary<string, EnvironmentContext>();
            _contextHistory = new ConcurrentDictionary<string, List<ContextEvent>>();

            _cleanupCts = new CancellationTokenSource();

            // Sağlık kontrol timer'ı;
            _healthTimer = new System.Timers.Timer(_config.HealthCheckIntervalMs);
            _healthTimer.Elapsed += OnHealthCheck;
            _healthTimer.AutoReset = true;

            InitializeEventSubscriptions();
            _logger.LogInformation("ContextManager initialized with {@Config}", _config);
        }

        /// <summary>
        /// Bağlam yöneticisini başlat;
        /// </summary>
        public void StartContextManagement()
        {
            if (_cleanupTask != null && !_cleanupTask.IsCompleted)
            {
                _logger.LogWarning("Context management is already running");
                return;
            }

            _cleanupTask = Task.Run(async () => await PerformContextCleanupAsync(_cleanupCts.Token));
            _healthTimer.Start();

            _logger.LogInformation("Context management started");

            LogContextState("ContextManager_Started", "Context management system started", ContextState.Active);
        }

        /// <summary>
        /// Bağlam yöneticisini durdur;
        /// </summary>
        public async Task StopContextManagementAsync()
        {
            _healthTimer.Stop();
            _cleanupCts.Cancel();

            try
            {
                if (_cleanupTask != null)
                {
                    await _cleanupTask;
                }
            }
            catch (OperationCanceledException)
            {
                // Task iptal edildi, normal durum;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping context management");
            }

            // Tüm bağlamları kaydet;
            await SaveAllContextsAsync();

            _logger.LogInformation("Context management stopped");
        }

        /// <summary>
        /// Yeni bir etkileşim bağlamı oluştur;
        /// </summary>
        public async Task<InteractionContext> CreateInteractionContextAsync(
            string sessionId,
            string userId,
            ContextType contextType,
            Dictionary<string, object> initialData = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                LogContextState("CreateContext_Start",
                    $"Creating context for session {sessionId}, user {userId}, type {contextType}",
                    ContextState.Creating);

                // Kullanıcı ve oturum doğrulaması;
                var session = await _sessionManager.GetSessionAsync(sessionId);
                if (session == null)
                {
                    throw new InvalidOperationException($"Session not found: {sessionId}");
                }

                var userProfile = await _profileManager.GetUserProfileAsync(userId);
                if (userProfile == null)
                {
                    throw new InvalidOperationException($"User profile not found: {userId}");
                }

                // Bağlam ID'si oluştur;
                var contextId = GenerateContextId(sessionId, userId, contextType);

                // Eğer bağlam zaten varsa güncelle;
                if (_activeContexts.TryGetValue(contextId, out var existingContext))
                {
                    _logger.LogInformation("Context already exists, updating: {ContextId}", contextId);
                    await UpdateContextAsync(contextId, initialData);
                    return existingContext;
                }

                // Yeni bağlam oluştur;
                var context = new InteractionContext;
                {
                    ContextId = contextId,
                    SessionId = sessionId,
                    UserId = userId,
                    ContextType = contextType,
                    CreatedAt = DateTime.UtcNow,
                    LastUpdatedAt = DateTime.UtcNow,
                    State = ContextState.Active,
                    Data = initialData ?? new Dictionary<string, object>(),
                    Metadata = new ContextMetadata;
                    {
                        Priority = CalculateInitialPriority(contextType),
                        Complexity = ContextComplexity.Medium,
                        Sensitivity = ContextSensitivity.Normal,
                        IsPersistent = contextType == ContextType.LongTerm || contextType == ContextType.System,
                        Version = 1;
                    }
                };

                // Kullanıcı tercihlerini yükle;
                await LoadUserPreferencesIntoContextAsync(context, userId);

                // Geçmiş bağlamları yükle;
                await LoadHistoricalContextAsync(context, userId, contextType);

                // Ortam bağlamını yükle;
                await LoadEnvironmentContextAsync(context, sessionId);

                // Bağlamı kaydet;
                if (!_activeContexts.TryAdd(contextId, context))
                {
                    throw new InvalidOperationException($"Failed to add context: {contextId}");
                }

                // Geçmişe kaydet;
                await AddContextEventAsync(contextId, new ContextEvent;
                {
                    EventId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = ContextEventType.Created,
                    ContextId = contextId,
                    SessionId = sessionId,
                    UserId = userId,
                    Description = $"Context created: {contextType}",
                    Metadata = new Dictionary<string, object>
                    {
                        ["ContextType"] = contextType,
                        ["InitialDataKeys"] = initialData?.Keys.ToList() ?? new List<string>()
                    }
                });

                // Bağlam değişikliğini bildir;
                OnContextChanged(new ContextChangedEventArgs;
                {
                    ContextId = contextId,
                    OldState = ContextState.None,
                    NewState = ContextState.Active,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = userId,
                    ChangeType = ContextChangeType.Created,
                    Metadata = new Dictionary<string, object>
                    {
                        ["ContextType"] = contextType,
                        ["SessionId"] = sessionId;
                    }
                });

                // Bağlamı kısa süreli belleğe kaydet;
                await _shortTermMemory.StoreAsync($"context:{contextId}", context, TimeSpan.FromMinutes(30));

                _logger.LogInformation("Interaction context created: {ContextId} ({ContextType})",
                    contextId, contextType);

                LogContextState("CreateContext_Success",
                    $"Context created successfully: {contextId}",
                    ContextState.Active);

                return context;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating interaction context for session {SessionId}, user {UserId}",
                    sessionId, userId);
                LogContextState("CreateContext_Failed",
                    $"Context creation failed: {ex.Message}",
                    ContextState.Error);
                throw;
            }
        }

        /// <summary>
        /// Bağlamı güncelle;
        /// </summary>
        public async Task<InteractionContext> UpdateContextAsync(
            string contextId,
            Dictionary<string, object> updates,
            string updatedBy = null)
        {
            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be null or empty", nameof(contextId));

            if (updates == null || !updates.Any())
                throw new ArgumentException("Updates cannot be null or empty", nameof(updates));

            try
            {
                LogContextState("UpdateContext_Start",
                    $"Updating context {contextId} with {updates.Count} updates",
                    ContextState.Updating);

                if (!_activeContexts.TryGetValue(contextId, out var context))
                {
                    throw new KeyNotFoundException($"Context not found: {contextId}");
                }

                // Yetki kontrolü;
                if (!string.IsNullOrEmpty(updatedBy) && updatedBy != context.UserId)
                {
                    var hasPermission = await _permissionService.CheckPermissionAsync(
                        updatedBy, "Context", "Update", contextId);

                    if (!hasPermission)
                    {
                        throw new UnauthorizedAccessException(
                            $"User {updatedBy} does not have permission to update context {contextId}");
                    }
                }

                // Eski durumu kaydet;
                var oldState = context.State;
                var oldData = new Dictionary<string, object>(context.Data);

                // Güncellemeleri uygula;
                foreach (var update in updates)
                {
                    context.Data[update.Key] = update.Value;
                }

                context.LastUpdatedAt = DateTime.UtcNow;
                context.Version++;
                updatedBy ??= context.UserId;

                // Durum analizi yap;
                await AnalyzeContextStateAsync(context);

                // Bağlam uyumluluğunu kontrol et;
                var conflicts = await DetectContextConflictsAsync(context);
                if (conflicts.Any())
                {
                    await HandleContextConflictsAsync(context, conflicts);
                }

                // Geçmişe kaydet;
                await AddContextEventAsync(contextId, new ContextEvent;
                {
                    EventId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = ContextEventType.Updated,
                    ContextId = contextId,
                    SessionId = context.SessionId,
                    UserId = context.UserId,
                    Description = $"Context updated with {updates.Count} changes",
                    Metadata = new Dictionary<string, object>
                    {
                        ["UpdatedBy"] = updatedBy,
                        ["UpdatedKeys"] = updates.Keys.ToList(),
                        ["OldValues"] = updates.Keys;
                            .Where(k => oldData.ContainsKey(k))
                            .ToDictionary(k => k, k => oldData[k]),
                        ["Version"] = context.Version;
                    }
                });

                // Bağlam değişikliğini bildir;
                OnContextChanged(new ContextChangedEventArgs;
                {
                    ContextId = contextId,
                    OldState = oldState,
                    NewState = context.State,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = updatedBy,
                    ChangeType = ContextChangeType.Updated,
                    Metadata = new Dictionary<string, object>
                    {
                        ["UpdatedKeys"] = updates.Keys.ToList(),
                        ["Version"] = context.Version;
                    }
                });

                // Kısa süreli belleği güncelle;
                await _shortTermMemory.UpdateAsync($"context:{contextId}", context, TimeSpan.FromMinutes(30));

                // Uzun süreli bağlam ise kalıcı depoya kaydet;
                if (context.Metadata.IsPersistent && context.Version % _config.PersistentSaveInterval == 0)
                {
                    await SaveContextToLongTermStorageAsync(context);
                }

                _logger.LogDebug("Context updated: {ContextId}, version: {Version}",
                    contextId, context.Version);

                return context;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating context {ContextId}", contextId);
                LogContextState("UpdateContext_Failed",
                    $"Context update failed: {ex.Message}",
                    ContextState.Error);
                throw;
            }
        }

        /// <summary>
        /// Bağlamı getir;
        /// </summary>
        public async Task<InteractionContext> GetContextAsync(string contextId, bool includeHistory = false)
        {
            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be null or empty", nameof(contextId));

            try
            {
                // Önce aktif bağlamlardan getir;
                if (_activeContexts.TryGetValue(contextId, out var context))
                {
                    if (includeHistory)
                    {
                        context.History = await GetContextHistoryAsync(contextId);
                    }
                    return context;
                }

                // Kısa süreli bellekten getir;
                var cachedContext = await _shortTermMemory.RetrieveAsync<InteractionContext>($"context:{contextId}");
                if (cachedContext != null)
                {
                    // Aktif bağlamlara ekle;
                    _activeContexts.TryAdd(contextId, cachedContext);

                    if (includeHistory)
                    {
                        cachedContext.History = await GetContextHistoryAsync(contextId);
                    }
                    return cachedContext;
                }

                // Uzun süreli depodan getir (eğer kalıcıysa)
                var storedContext = await LoadContextFromLongTermStorageAsync(contextId);
                if (storedContext != null)
                {
                    // Aktif bağlamlara ekle;
                    _activeContexts.TryAdd(contextId, storedContext);

                    if (includeHistory)
                    {
                        storedContext.History = await GetContextHistoryAsync(contextId);
                    }
                    return storedContext;
                }

                throw new KeyNotFoundException($"Context not found: {contextId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving context {ContextId}", contextId);
                throw;
            }
        }

        /// <summary>
        /// Bağlamı sil;
        /// </summary>
        public async Task<bool> DeleteContextAsync(string contextId, string deletedBy = null, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be null or empty", nameof(contextId));

            try
            {
                LogContextState("DeleteContext_Start",
                    $"Deleting context {contextId}, force: {force}",
                    ContextState.Deleting);

                if (!_activeContexts.TryRemove(contextId, out var context))
                {
                    // Zaten silinmiş veya hiç yok;
                    return false;
                }

                // Yetki kontrolü;
                if (!string.IsNullOrEmpty(deletedBy) && deletedBy != context.UserId && !force)
                {
                    var hasPermission = await _permissionService.CheckPermissionAsync(
                        deletedBy, "Context", "Delete", contextId);

                    if (!hasPermission)
                    {
                        // Silineni geri ekle;
                        _activeContexts.TryAdd(contextId, context);
                        throw new UnauthorizedAccessException(
                            $"User {deletedBy} does not have permission to delete context {contextId}");
                    }
                }

                // Kalıcı bağlam ise arşivle;
                if (context.Metadata.IsPersistent && !force)
                {
                    await ArchiveContextAsync(context);
                }
                else;
                {
                    // Tamamen sil;
                    await PurgeContextAsync(contextId);
                }

                // Geçmişe kaydet;
                await AddContextEventAsync(contextId, new ContextEvent;
                {
                    EventId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = force ? ContextEventType.ForceDeleted : ContextEventType.Deleted,
                    ContextId = contextId,
                    SessionId = context.SessionId,
                    UserId = context.UserId,
                    Description = force ? "Context force deleted" : "Context deleted",
                    Metadata = new Dictionary<string, object>
                    {
                        ["DeletedBy"] = deletedBy ?? context.UserId,
                        ["Force"] = force,
                        ["WasPersistent"] = context.Metadata.IsPersistent,
                        ["ContextType"] = context.ContextType;
                    }
                });

                // Bağlam değişikliğini bildir;
                OnContextChanged(new ContextChangedEventArgs;
                {
                    ContextId = contextId,
                    OldState = context.State,
                    NewState = ContextState.Deleted,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = deletedBy ?? context.UserId,
                    ChangeType = force ? ContextChangeType.ForceDeleted : ContextChangeType.Deleted,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Force"] = force,
                        ["ContextType"] = context.ContextType;
                    }
                });

                // Kısa süreli bellekten sil;
                await _shortTermMemory.RemoveAsync($"context:{contextId}");

                _logger.LogInformation("Context deleted: {ContextId}, force: {Force}", contextId, force);

                LogContextState("DeleteContext_Success",
                    $"Context deleted successfully: {contextId}",
                    ContextState.Deleted);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting context {ContextId}", contextId);
                LogContextState("DeleteContext_Failed",
                    $"Context deletion failed: {ex.Message}",
                    ContextState.Error);
                throw;
            }
        }

        /// <summary>
        /// Bağlamı arşivle;
        /// </summary>
        public async Task<bool> ArchiveContextAsync(string contextId, string archivedBy = null)
        {
            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be null or empty", nameof(contextId));

            try
            {
                LogContextState("ArchiveContext_Start",
                    $"Archiving context {contextId}",
                    ContextState.Archiving);

                if (!_activeContexts.TryGetValue(contextId, out var context))
                {
                    throw new KeyNotFoundException($"Context not found: {contextId}");
                }

                // Bağlam durumunu güncelle;
                context.State = ContextState.Archived;
                context.LastUpdatedAt = DateTime.UtcNow;
                context.Version++;

                // Uzun süreli depoya kaydet;
                await SaveContextToLongTermStorageAsync(context);

                // Aktif bağlamlardan kaldır;
                _activeContexts.TryRemove(contextId, out _);

                // Geçmişe kaydet;
                await AddContextEventAsync(contextId, new ContextEvent;
                {
                    EventId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = ContextEventType.Archived,
                    ContextId = contextId,
                    SessionId = context.SessionId,
                    UserId = context.UserId,
                    Description = "Context archived",
                    Metadata = new Dictionary<string, object>
                    {
                        ["ArchivedBy"] = archivedBy ?? context.UserId,
                        ["ContextType"] = context.ContextType,
                        ["Version"] = context.Version;
                    }
                });

                // Bağlam değişikliğini bildir;
                OnContextChanged(new ContextChangedEventArgs;
                {
                    ContextId = contextId,
                    OldState = ContextState.Active,
                    NewState = ContextState.Archived,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = archivedBy ?? context.UserId,
                    ChangeType = ContextChangeType.Archived,
                    Metadata = new Dictionary<string, object>
                    {
                        ["ContextType"] = context.ContextType;
                    }
                });

                _logger.LogInformation("Context archived: {ContextId}", contextId);

                LogContextState("ArchiveContext_Success",
                    $"Context archived successfully: {contextId}",
                    ContextState.Archived);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error archiving context {ContextId}", contextId);
                LogContextState("ArchiveContext_Failed",
                    $"Context archiving failed: {ex.Message}",
                    ContextState.Error);
                throw;
            }
        }

        /// <summary>
        /// Bağlamı geri yükle;
        /// </summary>
        public async Task<InteractionContext> RestoreContextAsync(string contextId, string restoredBy = null)
        {
            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be null or empty", nameof(contextId));

            try
            {
                LogContextState("RestoreContext_Start",
                    $"Restoring context {contextId}",
                    ContextState.Restoring);

                // Uzun süreli depodan yükle;
                var context = await LoadContextFromLongTermStorageAsync(contextId);
                if (context == null)
                {
                    throw new KeyNotFoundException($"Context not found in storage: {contextId}");
                }

                // Durumu güncelle;
                context.State = ContextState.Active;
                context.LastUpdatedAt = DateTime.UtcNow;
                context.Version++;

                // Aktif bağlamlara ekle;
                if (!_activeContexts.TryAdd(contextId, context))
                {
                    _activeContexts[contextId] = context;
                }

                // Kısa süreli belleğe ekle;
                await _shortTermMemory.StoreAsync($"context:{contextId}", context, TimeSpan.FromMinutes(30));

                // Geçmişe kaydet;
                await AddContextEventAsync(contextId, new ContextEvent;
                {
                    EventId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    EventType = ContextEventType.Restored,
                    ContextId = contextId,
                    SessionId = context.SessionId,
                    UserId = context.UserId,
                    Description = "Context restored from archive",
                    Metadata = new Dictionary<string, object>
                    {
                        ["RestoredBy"] = restoredBy ?? context.UserId,
                        ["ContextType"] = context.ContextType,
                        ["Version"] = context.Version;
                    }
                });

                // Bağlam değişikliğini bildir;
                OnContextChanged(new ContextChangedEventArgs;
                {
                    ContextId = contextId,
                    OldState = ContextState.Archived,
                    NewState = ContextState.Active,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = restoredBy ?? context.UserId,
                    ChangeType = ContextChangeType.Restored,
                    Metadata = new Dictionary<string, object>
                    {
                        ["ContextType"] = context.ContextType;
                    }
                });

                _logger.LogInformation("Context restored: {ContextId}", contextId);

                LogContextState("RestoreContext_Success",
                    $"Context restored successfully: {contextId}",
                    ContextState.Active);

                return context;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring context {ContextId}", contextId);
                LogContextState("RestoreContext_Failed",
                    $"Context restoration failed: {ex.Message}",
                    ContextState.Error);
                throw;
            }
        }

        /// <summary>
        /// Bağlamları ara;
        /// </summary>
        public async Task<List<InteractionContext>> SearchContextsAsync(
            ContextSearchCriteria criteria,
            int page = 1,
            int pageSize = 50)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            try
            {
                var results = new List<InteractionContext>();

                // Aktif bağlamlarda ara;
                foreach (var context in _activeContexts.Values)
                {
                    if (MatchesCriteria(context, criteria))
                    {
                        results.Add(context);
                    }
                }

                // Sayfalama;
                var skip = (page - 1) * pageSize;
                var pagedResults = results;
                    .Skip(skip)
                    .Take(pageSize)
                    .ToList();

                // Kısa süreli bellekte de ara (eğer yeterli sonuç yoksa)
                if (pagedResults.Count < pageSize && criteria.IncludeInactive)
                {
                    // Bu örnekte basit bir implementasyon;
                    // Gerçek implementasyonda kısa süreli bellek ve uzun süreli depo araması yapılır;
                }

                _logger.LogDebug("Context search completed: {Count} results for page {Page}",
                    pagedResults.Count, page);

                return pagedResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching contexts");
                throw;
            }
        }

        /// <summary>
        /// Bağlam geçmişini getir;
        /// </summary>
        public async Task<List<ContextEvent>> GetContextHistoryAsync(
            string contextId,
            DateTime? startDate = null,
            DateTime? endDate = null,
            ContextEventType? eventType = null)
        {
            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be null or empty", nameof(contextId));

            try
            {
                if (!_contextHistory.TryGetValue(contextId, out var history))
                {
                    // Geçmiş depodan yükle;
                    history = await LoadContextHistoryFromStorageAsync(contextId);
                    _contextHistory[contextId] = history ?? new List<ContextEvent>();
                }

                var query = history.AsEnumerable();

                if (startDate.HasValue)
                {
                    query = query.Where(e => e.Timestamp >= startDate.Value);
                }

                if (endDate.HasValue)
                {
                    query = query.Where(e => e.Timestamp <= endDate.Value);
                }

                if (eventType.HasValue)
                {
                    query = query.Where(e => e.EventType == eventType.Value);
                }

                return query;
                    .OrderByDescending(e => e.Timestamp)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting context history for {ContextId}", contextId);
                throw;
            }
        }

        /// <summary>
        /// Ortam bağlamını güncelle;
        /// </summary>
        public async Task<EnvironmentContext> UpdateEnvironmentContextAsync(
            string sessionId,
            EnvironmentContext environment)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            try
            {
                LogContextState("UpdateEnvironment_Start",
                    $"Updating environment context for session {sessionId}",
                    ContextState.Updating);

                environment.SessionId = sessionId;
                environment.LastUpdated = DateTime.UtcNow;
                environment.Version++;

                _environmentContexts[sessionId] = environment;

                // İlgili tüm bağlamları güncelle;
                await UpdateRelatedContextsWithEnvironmentAsync(sessionId, environment);

                // Ortam değişikliğini logla;
                await AddEnvironmentEventAsync(sessionId, new EnvironmentEvent;
                {
                    EventId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    SessionId = sessionId,
                    EventType = EnvironmentEventType.Updated,
                    Description = "Environment context updated",
                    EnvironmentData = environment.EnvironmentData,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Version"] = environment.Version,
                        ["UpdateCount"] = environment.EnvironmentData.Count;
                    }
                });

                _logger.LogDebug("Environment context updated for session {SessionId}, version: {Version}",
                    sessionId, environment.Version);

                return environment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating environment context for session {SessionId}", sessionId);
                throw;
            }
        }

        /// <summary>
        /// Mevcut bağlam durumunu al;
        /// </summary>
        public ContextStatus GetContextStatus()
        {
            lock (_syncRoot)
            {
                return new ContextStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ActiveContexts = _activeContexts.Count,
                    EnvironmentContexts = _environmentContexts.Count,
                    TotalHistoryEntries = _contextHistory.Sum(kv => kv.Value.Count),
                    MemoryUsage = CalculateMemoryUsage(),
                    AverageContextAge = CalculateAverageContextAge(),
                    ContextDistribution = GetContextDistribution(),
                    HealthScore = CalculateHealthScore()
                };
            }
        }

        /// <summary>
        /// Bağlam tanılama testi çalıştır;
        /// </summary>
        public async Task<ContextDiagnostics> RunDiagnosticsAsync()
        {
            var diagnostics = new ContextDiagnostics;
            {
                Timestamp = DateTime.UtcNow,
                TestId = Guid.NewGuid(),
                ContextStatus = GetContextStatus()
            };

            try
            {
                _logger.LogInformation("Starting context diagnostics");

                // 1. Bağlam tutarlılık testi;
                diagnostics.ConsistencyTest = await TestContextConsistencyAsync();
                diagnostics.Tests.Add(new ContextTest;
                {
                    TestName = "Context Consistency",
                    Success = diagnostics.ConsistencyTest.Success,
                    Details = diagnostics.ConsistencyTest.Details,
                    DurationMs = diagnostics.ConsistencyTest.DurationMs;
                });

                // 2. Bellek yönetimi testi;
                diagnostics.MemoryTest = await TestMemoryManagementAsync();
                diagnostics.Tests.Add(new ContextTest;
                {
                    TestName = "Memory Management",
                    Success = diagnostics.MemoryTest.Success,
                    Details = diagnostics.MemoryTest.Details,
                    DurationMs = diagnostics.MemoryTest.DurationMs;
                });

                // 3. Geçmiş tutarlılık testi;
                diagnostics.HistoryTest = await TestHistoryConsistencyAsync();
                diagnostics.Tests.Add(new ContextTest;
                {
                    TestName = "History Consistency",
                    Success = diagnostics.HistoryTest.Success,
                    Details = diagnostics.HistoryTest.Details,
                    DurationMs = diagnostics.HistoryTest.DurationMs;
                });

                // 4. Performans testi;
                diagnostics.PerformanceTest = await TestPerformanceAsync();
                diagnostics.Tests.Add(new ContextTest;
                {
                    TestName = "Performance",
                    Success = diagnostics.PerformanceTest.Success,
                    Details = diagnostics.PerformanceTest.Details,
                    DurationMs = diagnostics.PerformanceTest.DurationMs;
                });

                // 5. Güvenlik testi;
                diagnostics.SecurityTest = await TestSecurityAsync();
                diagnostics.Tests.Add(new ContextTest;
                {
                    TestName = "Security",
                    Success = diagnostics.SecurityTest.Success,
                    Details = diagnostics.SecurityTest.Details,
                    DurationMs = diagnostics.SecurityTest.DurationMs;
                });

                // Genel durum;
                diagnostics.OverallStatus = diagnostics.Tests.All(t => t.Success)
                    ? DiagnosticsStatus.Passed;
                    : DiagnosticsStatus.Failed;

                diagnostics.Message = diagnostics.OverallStatus == DiagnosticsStatus.Passed;
                    ? "All context diagnostics passed"
                    : "Some context diagnostics failed";

                _logger.LogInformation("Context diagnostics completed: {Status}", diagnostics.OverallStatus);

                return diagnostics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during context diagnostics");
                diagnostics.OverallStatus = DiagnosticsStatus.Error;
                diagnostics.Message = $"Diagnostics error: {ex.Message}";
                return diagnostics;
            }
        }

        /// <summary>
        /// Bağlam geçmişini temizle;
        /// </summary>
        public void ClearContextHistory(string contextId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(contextId))
                {
                    // Tüm geçmişi temizle;
                    _contextHistory.Clear();
                    _logger.LogInformation("All context history cleared");
                }
                else;
                {
                    // Belirli bir bağlamın geçmişini temizle;
                    _contextHistory.TryRemove(contextId, out _);
                    _logger.LogInformation("Context history cleared for: {ContextId}", contextId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing context history");
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
            _cleanupCts?.Cancel();
            _cleanupCts?.Dispose();

            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        /// <summary>
        /// Event aboneliklerini başlat;
        /// </summary>
        private void InitializeEventSubscriptions()
        {
            // Oturum olaylarına abone ol;
            _eventBus.Subscribe<SessionStartedEvent>(OnSessionStarted);
            _eventBus.Subscribe<SessionEndedEvent>(OnSessionEnded);
            _eventBus.Subscribe<UserProfileUpdatedEvent>(OnUserProfileUpdated);
            _eventBus.Subscribe<PreferenceChangedEvent>(OnPreferenceChanged);

            _logger.LogDebug("Event subscriptions initialized");
        }

        /// <summary>
        /// Bağlam temizleme işlemi;
        /// </summary>
        private async Task PerformContextCleanupAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting context cleanup");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.CleanupIntervalMs, cancellationToken);

                    // Eski bağlamları temizle;
                    await CleanupOldContextsAsync();

                    // Bellek optimizasyonu;
                    await OptimizeMemoryUsageAsync();

                    // Geçmiş temizliği;
                    await CleanupOldHistoryAsync();

                    // Sağlık kontrolü;
                    await PerformHealthCheckAsync();
                }
                catch (OperationCanceledException)
                {
                    // İptal edildi, döngüden çık;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in context cleanup");
                    await Task.Delay(5000, cancellationToken);
                }
            }

            _logger.LogInformation("Context cleanup stopped");
        }

        /// <summary>
        /// Eski bağlamları temizle;
        /// </summary>
        private async Task CleanupOldContextsAsync()
        {
            try
            {
                var now = DateTime.UtcNow;
                var contextsToRemove = new List<string>();

                foreach (var kvp in _activeContexts)
                {
                    var context = kvp.Value;
                    var age = now - context.LastUpdatedAt;

                    // Kısa süreli bağlamlar için zaman aşımı kontrolü;
                    if (!context.Metadata.IsPersistent && age > _config.ShortTermContextTimeout)
                    {
                        contextsToRemove.Add(kvp.Key);
                    }
                    // Uzun süreli bağlamlar için inaktivite kontrolü;
                    else if (context.Metadata.IsPersistent && age > _config.LongTermContextInactivityTimeout)
                    {
                        await ArchiveContextAsync(kvp.Key, "System");
                    }
                }

                // Bağlamları kaldır;
                foreach (var contextId in contextsToRemove)
                {
                    _activeContexts.TryRemove(contextId, out _);
                    _logger.LogDebug("Removed old context: {ContextId}", contextId);
                }

                if (contextsToRemove.Count > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old contexts", contextsToRemove.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old contexts");
            }
        }

        /// <summary>
        /// Bellek kullanımını optimize et;
        /// </summary>
        private async Task OptimizeMemoryUsageAsync()
        {
            try
            {
                var memoryUsage = CalculateMemoryUsage();

                if (memoryUsage > _config.MaxMemoryUsageMB)
                {
                    _logger.LogWarning("High memory usage: {MemoryUsage}MB, starting optimization", memoryUsage);

                    // En az kullanılan bağlamları arşivle;
                    var leastUsedContexts = _activeContexts.Values;
                        .Where(c => !c.Metadata.IsPersistent)
                        .OrderBy(c => c.LastAccessedAt ?? c.CreatedAt)
                        .Take(_config.OptimizationBatchSize)
                        .ToList();

                    foreach (var context in leastUsedContexts)
                    {
                        _activeContexts.TryRemove(context.ContextId, out _);
                        await _shortTermMemory.RemoveAsync($"context:{context.ContextId}");
                    }

                    _logger.LogInformation("Memory optimization completed, removed {Count} contexts",
                        leastUsedContexts.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing memory usage");
            }
        }

        /// <summary>
        /// Eski geçmişi temizle;
        /// </summary>
        private async Task CleanupOldHistoryAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-_config.HistoryRetentionDays);
                var totalRemoved = 0;

                foreach (var kvp in _contextHistory)
                {
                    var oldCount = kvp.Value.Count;
                    kvp.Value.RemoveAll(e => e.Timestamp < cutoffDate);
                    totalRemoved += oldCount - kvp.Value.Count;
                }

                if (totalRemoved > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old history entries", totalRemoved);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old history");
            }
        }

        /// <summary>
        /// Sağlık kontrolü yap;
        /// </summary>
        private async Task PerformHealthCheckAsync()
        {
            try
            {
                var health = new ContextHealth;
                {
                    Timestamp = DateTime.UtcNow,
                    ActiveContexts = _activeContexts.Count,
                    MemoryUsage = CalculateMemoryUsage(),
                    AverageContextAge = CalculateAverageContextAge(),
                    HealthScore = CalculateHealthScore()
                };

                if (health.HealthScore < _config.CriticalHealthThreshold)
                {
                    _logger.LogError("Critical context health: {HealthScore}%", health.HealthScore);
                    await NotifyCriticalHealthAsync(health);
                }
                else if (health.HealthScore < _config.WarningHealthThreshold)
                {
                    _logger.LogWarning("Warning context health: {HealthScore}%", health.HealthScore);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing health check");
            }
        }

        /// <summary>
        /// Tüm bağlamları kaydet;
        /// </summary>
        private async Task SaveAllContextsAsync()
        {
            try
            {
                _logger.LogInformation("Saving all contexts to storage");

                var saveTasks = new List<Task>();

                foreach (var context in _activeContexts.Values)
                {
                    if (context.Metadata.IsPersistent)
                    {
                        saveTasks.Add(SaveContextToLongTermStorageAsync(context));
                    }
                }

                await Task.WhenAll(saveTasks);

                _logger.LogInformation("Saved {Count} contexts to storage", saveTasks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving all contexts");
            }
        }

        /// <summary>
        /// Bağlam ID'si oluştur;
        /// </summary>
        private string GenerateContextId(string sessionId, string userId, ContextType contextType)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{contextType}_{userId}_{sessionId}_{timestamp}_{random}";
        }

        /// <summary>
        /// Başlangıç önceliği hesapla;
        /// </summary>
        private ContextPriority CalculateInitialPriority(ContextType contextType)
        {
            return contextType switch;
            {
                ContextType.Critical => ContextPriority.High,
                ContextType.RealTime => ContextPriority.High,
                ContextType.LongTerm => ContextPriority.Medium,
                ContextType.System => ContextPriority.Medium,
                ContextType.Background => ContextPriority.Low,
                _ => ContextPriority.Medium;
            };
        }

        /// <summary>
        /// Kullanıcı tercihlerini bağlama yükle;
        /// </summary>
        private async Task LoadUserPreferencesIntoContextAsync(InteractionContext context, string userId)
        {
            try
            {
                var preferences = await _preferenceEngine.GetPreferencesAsync(userId);
                if (preferences != null && preferences.Any())
                {
                    context.Data["UserPreferences"] = preferences;
                    context.Metadata.PreferencesLoaded = true;

                    _logger.LogDebug("Loaded {Count} preferences for user {UserId}",
                        preferences.Count, userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user preferences for context");
            }
        }

        /// <summary>
        /// Geçmiş bağlamları yükle;
        /// </summary>
        private async Task LoadHistoricalContextAsync(InteractionContext context, string userId, ContextType contextType)
        {
            try
            {
                var history = await _historyManager.GetRecentContextsAsync(userId, contextType, 5);
                if (history != null && history.Any())
                {
                    context.Data["HistoricalContexts"] = history;
                    context.Metadata.HistoryLoaded = true;

                    // Geçmişten öğrenme;
                    await LearnFromHistoryAsync(context, history);

                    _logger.LogDebug("Loaded {Count} historical contexts for user {UserId}",
                        history.Count, userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading historical context");
            }
        }

        /// <summary>
        /// Ortam bağlamını yükle;
        /// </summary>
        private async Task LoadEnvironmentContextAsync(InteractionContext context, string sessionId)
        {
            try
            {
                if (_environmentContexts.TryGetValue(sessionId, out var environment))
                {
                    context.Environment = environment;
                    context.Metadata.EnvironmentLoaded = true;

                    _logger.LogDebug("Loaded environment context for session {SessionId}", sessionId);
                }
                else;
                {
                    // Varsayılan ortam bağlamı oluştur;
                    context.Environment = new EnvironmentContext;
                    {
                        SessionId = sessionId,
                        Created = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow,
                        EnvironmentData = new Dictionary<string, object>
                        {
                            ["DeviceType"] = "Unknown",
                            ["Location"] = "Unknown",
                            ["TimeZone"] = TimeZoneInfo.Local.Id,
                            ["Language"] = "en-US",
                            ["Accessibility"] = new Dictionary<string, object>()
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading environment context");
            }
        }

        /// <summary>
        /// Geçmişten öğren;
        /// </summary>
        private async Task LearnFromHistoryAsync(InteractionContext context, List<HistoricalContext> history)
        {
            try
            {
                // Geçmiş bağlamlardan desen çıkarma;
                var patterns = await _mlModel.AnalyzePatternsAsync(history);
                if (patterns != null && patterns.Any())
                {
                    context.Data["LearnedPatterns"] = patterns;
                    context.Metadata.PatternsLearned = true;

                    // Desenleri uzun süreli belleğe kaydet;
                    await _longTermMemory.StorePatternAsync($"user:{context.UserId}:patterns", patterns);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error learning from history");
            }
        }

        /// <summary>
        /// Bağlam durumunu analiz et;
        /// </summary>
        private async Task AnalyzeContextStateAsync(InteractionContext context)
        {
            try
            {
                // Durum analizi için ML modelini kullan;
                var analysis = await _mlModel.AnalyzeContextAsync(context);
                if (analysis != null)
                {
                    // Analiz sonuçlarını bağlama ekle;
                    context.Data["StateAnalysis"] = analysis;

                    // Durumu güncelle;
                    if (analysis.ContainsKey("RecommendedState"))
                    {
                        var recommendedState = (ContextState)analysis["RecommendedState"];
                        if (context.State != recommendedState)
                        {
                            var oldState = context.State;
                            context.State = recommendedState;

                            _logger.LogDebug("Context state changed: {ContextId}, {OldState} -> {NewState}",
                                context.ContextId, oldState, recommendedState);
                        }
                    }

                    // Önceliği güncelle;
                    if (analysis.ContainsKey("PriorityScore"))
                    {
                        var priorityScore = (double)analysis["PriorityScore"];
                        context.Metadata.Priority = CalculatePriorityFromScore(priorityScore);
                    }

                    // Karmaşıklığı güncelle;
                    if (analysis.ContainsKey("ComplexityScore"))
                    {
                        var complexityScore = (double)analysis["ComplexityScore"];
                        context.Metadata.Complexity = CalculateComplexityFromScore(complexityScore);
                    }

                    context.Metadata.LastAnalyzed = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing context state");
            }
        }

        /// <summary>
        /// Bağlam uyumsuzluklarını tespit et;
        /// </summary>
        private async Task<List<ContextConflict>> DetectContextConflictsAsync(InteractionContext context)
        {
            var conflicts = new List<ContextConflict>();

            try
            {
                // Aynı kullanıcı için çakışan bağlamları bul;
                var userContexts = _activeContexts.Values;
                    .Where(c => c.UserId == context.UserId && c.ContextId != context.ContextId)
                    .ToList();

                foreach (var otherContext in userContexts)
                {
                    var conflict = await DetectConflictBetweenContextsAsync(context, otherContext);
                    if (conflict != null)
                    {
                        conflicts.Add(conflict);
                    }
                }

                // Sistem geneli çakışmaları kontrol et;
                var systemConflicts = await DetectSystemWideConflictsAsync(context);
                conflicts.AddRange(systemConflicts);

                return conflicts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting context conflicts");
                return conflicts;
            }
        }

        /// <summary>
        /// İki bağlam arasındaki çakışmayı tespit et;
        /// </summary>
        private async Task<ContextConflict> DetectConflictBetweenContextsAsync(
            InteractionContext context1,
            InteractionContext context2)
        {
            try
            {
                var conflict = new ContextConflict;
                {
                    ConflictId = Guid.NewGuid(),
                    Timestamp = DateTime.UtcNow,
                    ContextId1 = context1.ContextId,
                    ContextId2 = context2.ContextId,
                    UserId = context1.UserId,
                    ConflictType = ContextConflictType.None,
                    Severity = ConflictSeverity.Low;
                };

                // Veri çakışmalarını kontrol et;
                var dataConflicts = FindDataConflicts(context1.Data, context2.Data);
                if (dataConflicts.Any())
                {
                    conflict.ConflictType |= ContextConflictType.Data;
                    conflict.DataConflicts = dataConflicts;
                    conflict.Severity = ConflictSeverity.Medium;
                }

                // Durum çakışmalarını kontrol et;
                if (context1.State == ContextState.Error && context2.State == ContextState.Error)
                {
                    conflict.ConflictType |= ContextConflictType.State;
                    conflict.Severity = ConflictSeverity.High;
                }

                // Öncelik çakışmalarını kontrol et;
                if (context1.Metadata.Priority == ContextPriority.High &&
                    context2.Metadata.Priority == ContextPriority.High)
                {
                    conflict.ConflictType |= ContextConflictType.Priority;
                    conflict.Severity = (ConflictSeverity)Math.Max((int)conflict.Severity, (int)ConflictSeverity.Medium);
                }

                // ML tabanlı çakışma tespiti;
                var mlConflict = await _mlModel.DetectContextConflictAsync(context1, context2);
                if (mlConflict != null && mlConflict.Confidence > 0.7)
                {
                    conflict.ConflictType |= ContextConflictType.Predicted;
                    conflict.Severity = (ConflictSeverity)Math.Max((int)conflict.Severity, (int)mlConflict.Severity);
                    conflict.PredictionConfidence = mlConflict.Confidence;
                }

                return conflict.ConflictType != ContextConflictType.None ? conflict : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting conflict between contexts");
                return null;
            }
        }

        /// <summary>
        /// Sistem geneli çakışmaları tespit et;
        /// </summary>
        private async Task<List<ContextConflict>> DetectSystemWideConflictsAsync(InteractionContext context)
        {
            var conflicts = new List<ContextConflict>();

            try
            {
                // Kaynak çakışmalarını kontrol et;
                if (context.Data.ContainsKey("ResourceRequirements"))
                {
                    var resourceConflicts = await DetectResourceConflictsAsync(context);
                    conflicts.AddRange(resourceConflicts);
                }

                // Zamanlama çakışmalarını kontrol et;
                if (context.Data.ContainsKey("TimeConstraints"))
                {
                    var timeConflicts = await DetectTimeConflictsAsync(context);
                    conflicts.AddRange(timeConflicts);
                }

                return conflicts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting system-wide conflicts");
                return conflicts;
            }
        }

        /// <summary>
        /// Veri çakışmalarını bul;
        /// </summary>
        private List<DataConflict> FindDataConflicts(
            Dictionary<string, object> data1,
            Dictionary<string, object> data2)
        {
            var conflicts = new List<DataConflict>();

            try
            {
                var commonKeys = data1.Keys.Intersect(data2.Keys);

                foreach (var key in commonKeys)
                {
                    var value1 = data1[key];
                    var value2 = data2[key];

                    if (!AreValuesEqual(value1, value2))
                    {
                        conflicts.Add(new DataConflict;
                        {
                            Key = key,
                            Value1 = value1,
                            Value2 = value2,
                            ConflictType = DataConflictType.ValueMismatch;
                        });
                    }
                }

                return conflicts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding data conflicts");
                return conflicts;
            }
        }

        /// <summary>
        /// Değerlerin eşit olup olmadığını kontrol et;
        /// </summary>
        private bool AreValuesEqual(object value1, object value2)
        {
            if (value1 == null && value2 == null) return true;
            if (value1 == null || value2 == null) return false;

            try
            {
                if (value1.GetType() != value2.GetType()) return false;

                return value1.Equals(value2) ||
                       value1.ToString() == value2.ToString();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kaynak çakışmalarını tespit et;
        /// </summary>
        private async Task<List<ContextConflict>> DetectResourceConflictsAsync(InteractionContext context)
        {
            // Basit kaynak çakışma tespiti;
            // Gerçek implementasyonda kaynak yönetim sistemi entegrasyonu gerekir;
            return new List<ContextConflict>();
        }

        /// <summary>
        /// Zaman çakışmalarını tespit et;
        /// </summary>
        private async Task<List<ContextConflict>> DetectTimeConflictsAsync(InteractionContext context)
        {
            // Basit zaman çakışma tespiti;
            return new List<ContextConflict>();
        }

        /// <summary>
        /// Bağlam çakışmalarını işle;
        /// </summary>
        private async Task HandleContextConflictsAsync(InteractionContext context, List<ContextConflict> conflicts)
        {
            try
            {
                foreach (var conflict in conflicts)
                {
                    // Çakışmayı logla;
                    await AddConflictEventAsync(conflict);

                    // Çakışma türüne göre işlem yap;
                    switch (conflict.ConflictType)
                    {
                        case var type when type.HasFlag(ContextConflictType.Data):
                            await ResolveDataConflictsAsync(context, conflict);
                            break;

                        case var type when type.HasFlag(ContextConflictType.State):
                            await ResolveStateConflictsAsync(context, conflict);
                            break;

                        case var type when type.HasFlag(ContextConflictType.Priority):
                            await ResolvePriorityConflictsAsync(context, conflict);
                            break;
                    }

                    // Çakışma tespit edildi event'i tetikle;
                    OnContextConflictDetected(new ContextConflictEventArgs;
                    {
                        Conflict = conflict,
                        DetectedAt = DateTime.UtcNow,
                        Resolved = false;
                    });

                    _logger.LogWarning("Context conflict detected: {ConflictType}, Severity: {Severity}",
                        conflict.ConflictType, conflict.Severity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling context conflicts");
            }
        }

        /// <summary>
        /// Veri çakışmalarını çöz;
        /// </summary>
        private async Task ResolveDataConflictsAsync(InteractionContext context, ContextConflict conflict)
        {
            try
            {
                // En güncel veriyi kullan;
                var otherContext = await GetContextAsync(conflict.ContextId2);
                if (otherContext != null && context.LastUpdatedAt < otherContext.LastUpdatedAt)
                {
                    // Diğer bağlam daha güncel, veriyi güncelle;
                    foreach (var dataConflict in conflict.DataConflicts)
                    {
                        context.Data[dataConflict.Key] = otherContext.Data[dataConflict.Key];
                    }

                    _logger.LogInformation("Resolved data conflicts by using newer context data");
                }

                conflict.Resolved = true;
                conflict.ResolvedAt = DateTime.UtcNow;
                conflict.Resolution = "Used newer context data";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving data conflicts");
            }
        }

        /// <summary>
        /// Durum çakışmalarını çöz;
        /// </summary>
        private async Task ResolveStateConflictsAsync(InteractionContext context, ContextConflict conflict)
        {
            try
            {
                // Durumu daha iyi duruma çek;
                if (context.State == ContextState.Error)
                {
                    context.State = ContextState.Warning;
                    _logger.LogInformation("Resolved state conflict by changing error to warning");
                }

                conflict.Resolved = true;
                conflict.ResolvedAt = DateTime.UtcNow;
                conflict.Resolution = "Adjusted context state";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving state conflicts");
            }
        }

        /// <summary>
        /// Öncelik çakışmalarını çöz;
        /// </summary>
        private async Task ResolvePriorityConflictsAsync(InteractionContext context, ContextConflict conflict)
        {
            try
            {
                // Önceliği düşür;
                if (context.Metadata.Priority == ContextPriority.High)
                {
                    context.Metadata.Priority = ContextPriority.Medium;
                    _logger.LogInformation("Resolved priority conflict by reducing priority");
                }

                conflict.Resolved = true;
                conflict.ResolvedAt = DateTime.UtcNow;
                conflict.Resolution = "Adjusted context priority";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving priority conflicts");
            }
        }

        /// <summary>
        /// Öncelik skorundan öncelik seviyesi hesapla;
        /// </summary>
        private ContextPriority CalculatePriorityFromScore(double score)
        {
            return score switch;
            {
                > 0.8 => ContextPriority.High,
                > 0.5 => ContextPriority.Medium,
                _ => ContextPriority.Low;
            };
        }

        /// <summary>
        /// Karmaşıklık skorundan karmaşıklık seviyesi hesapla;
        /// </summary>
        private ContextComplexity CalculateComplexityFromScore(double score)
        {
            return score switch;
            {
                > 0.7 => ContextComplexity.High,
                > 0.4 => ContextComplexity.Medium,
                _ => ContextComplexity.Low;
            };
        }

        /// <summary>
        /// Bağlamı uzun süreli depoya kaydet;
        /// </summary>
        private async Task SaveContextToLongTermStorageAsync(InteractionContext context)
        {
            try
            {
                // Uzun süreli belleğe kaydet;
                await _longTermMemory.StoreAsync($"context:{context.ContextId}", context);

                // Event yayınla;
                await _eventBus.PublishAsync(new ContextPersistedEvent;
                {
                    ContextId = context.ContextId,
                    UserId = context.UserId,
                    ContextType = context.ContextType,
                    Timestamp = DateTime.UtcNow,
                    Version = context.Version;
                });

                _logger.LogDebug("Context saved to long-term storage: {ContextId}", context.ContextId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving context to long-term storage");
            }
        }

        /// <summary>
        /// Bağlamı uzun süreli depodan yükle;
        /// </summary>
        private async Task<InteractionContext> LoadContextFromLongTermStorageAsync(string contextId)
        {
            try
            {
                return await _longTermMemory.RetrieveAsync<InteractionContext>($"context:{contextId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading context from long-term storage");
                return null;
            }
        }

        /// <summary>
        /// Bağlam geçmişini depodan yükle;
        /// </summary>
        private async Task<List<ContextEvent>> LoadContextHistoryFromStorageAsync(string contextId)
        {
            try
            {
                return await _historyManager.GetContextHistoryAsync(contextId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading context history from storage");
                return new List<ContextEvent>();
            }
        }

        /// <summary>
        /// Bağlamı arşivle;
        /// </summary>
        private async Task ArchiveContextAsync(InteractionContext context)
        {
            try
            {
                // Arşiv verisi oluştur;
                var archive = new ContextArchive;
                {
                    ArchiveId = Guid.NewGuid(),
                    ContextId = context.ContextId,
                    UserId = context.UserId,
                    ContextType = context.ContextType,
                    ArchivedAt = DateTime.UtcNow,
                    ContextData = context.Data,
                    Metadata = context.Metadata,
                    Version = context.Version;
                };

                // Arşivi kaydet;
                await _longTermMemory.StoreAsync($"archive:{context.ContextId}", archive);

                _logger.LogDebug("Context archived: {ContextId}", context.ContextId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error archiving context");
            }
        }

        /// <summary>
        /// Bağlamı tamamen sil;
        /// </summary>
        private async Task PurgeContextAsync(string contextId)
        {
            try
            {
                // Uzun süreli bellekten sil;
                await _longTermMemory.RemoveAsync($"context:{contextId}");
                await _longTermMemory.RemoveAsync($"archive:{contextId}");

                // Geçmişi sil;
                _contextHistory.TryRemove(contextId, out _);

                _logger.LogDebug("Context purged: {ContextId}", contextId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error purging context");
            }
        }

        /// <summary>
        /// İlgili bağlamları ortamla güncelle;
        /// </summary>
        private async Task UpdateRelatedContextsWithEnvironmentAsync(string sessionId, EnvironmentContext environment)
        {
            try
            {
                var relatedContexts = _activeContexts.Values;
                    .Where(c => c.SessionId == sessionId)
                    .ToList();

                foreach (var context in relatedContexts)
                {
                    context.Environment = environment;
                    context.LastUpdatedAt = DateTime.UtcNow;

                    _logger.LogDebug("Updated environment for context: {ContextId}", context.ContextId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating related contexts with environment");
            }
        }

        /// <summary>
        /// Bağlam olayı ekle;
        /// </summary>
        private async Task AddContextEventAsync(string contextId, ContextEvent contextEvent)
        {
            try
            {
                var history = _contextHistory.GetOrAdd(contextId, _ => new List<ContextEvent>());
                history.Add(contextEvent);

                // Geçmiş yöneticisine kaydet;
                await _historyManager.AddContextEventAsync(contextEvent);

                // Event'i yayınla;
                await _eventBus.PublishAsync(new ContextEventMessage;
                {
                    EventId = contextEvent.EventId,
                    ContextId = contextId,
                    EventType = contextEvent.EventType.ToString(),
                    Timestamp = contextEvent.Timestamp,
                    Description = contextEvent.Description,
                    Metadata = contextEvent.Metadata;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding context event");
            }
        }

        /// <summary>
        /// Ortam olayı ekle;
        /// </summary>
        private async Task AddEnvironmentEventAsync(string sessionId, EnvironmentEvent environmentEvent)
        {
            try
            {
                // Event'i yayınla;
                await _eventBus.PublishAsync(new EnvironmentEventMessage;
                {
                    EventId = environmentEvent.EventId,
                    SessionId = sessionId,
                    EventType = environmentEvent.EventType.ToString(),
                    Timestamp = environmentEvent.Timestamp,
                    Description = environmentEvent.Description,
                    EnvironmentData = environmentEvent.EnvironmentData;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding environment event");
            }
        }

        /// <summary>
        /// Çakışma olayı ekle;
        /// </summary>
        private async Task AddConflictEventAsync(ContextConflict conflict)
        {
            try
            {
                // Event'i yayınla;
                await _eventBus.PublishAsync(new ContextConflictMessage;
                {
                    ConflictId = conflict.ConflictId,
                    Timestamp = conflict.Timestamp,
                    ContextId1 = conflict.ContextId1,
                    ContextId2 = conflict.ContextId2,
                    ConflictType = conflict.ConflictType.ToString(),
                    Severity = conflict.Severity.ToString(),
                    Resolved = conflict.Resolved,
                    Resolution = conflict.Resolution;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding conflict event");
            }
        }

        /// <summary>
        /// Bellek kullanımını hesapla;
        /// </summary>
        private double CalculateMemoryUsage()
        {
            try
            {
                // Basit bellek kullanımı hesaplaması;
                var contextSize = _activeContexts.Count * 1024; // Her bağlam ~1KB;
                var environmentSize = _environmentContexts.Count * 512; // Her ortam ~0.5KB;
                var historySize = _contextHistory.Sum(kv => kv.Value.Count * 256); // Her olay ~0.25KB;

                var totalBytes = contextSize + environmentSize + historySize;
                return totalBytes / (1024.0 * 1024.0); // MB cinsinden;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Ortalama bağlam yaşını hesapla;
        /// </summary>
        private TimeSpan CalculateAverageContextAge()
        {
            try
            {
                if (!_activeContexts.Any())
                    return TimeSpan.Zero;

                var now = DateTime.UtcNow;
                var totalAge = _activeContexts.Values;
                    .Sum(c => (now - c.CreatedAt).TotalSeconds);

                var averageSeconds = totalAge / _activeContexts.Count;
                return TimeSpan.FromSeconds(averageSeconds);
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Bağlam dağılımını al;
        /// </summary>
        private Dictionary<ContextType, int> GetContextDistribution()
        {
            try
            {
                return _activeContexts.Values;
                    .GroupBy(c => c.ContextType)
                    .ToDictionary(g => g.Key, g => g.Count());
            }
            catch
            {
                return new Dictionary<ContextType, int>();
            }
        }

        /// <summary>
        /// Sağlık skorunu hesapla;
        /// </summary>
        private double CalculateHealthScore()
        {
            try
            {
                var score = 100.0;

                // Bellek kullanımı puanı;
                var memoryUsage = CalculateMemoryUsage();
                if (memoryUsage > _config.MaxMemoryUsageMB)
                {
                    var penalty = (memoryUsage - _config.MaxMemoryUsageMB) * 10;
                    score -= Math.Min(penalty, 50);
                }

                // Çakışma puanı;
                var recentEvents = _contextHistory.Values;
                    .SelectMany(list => list)
                    .Where(e => e.Timestamp > DateTime.UtcNow.AddHours(-1))
                    .ToList();

                var conflictEvents = recentEvents;
                    .Count(e => e.EventType == ContextEventType.ConflictDetected);

                if (conflictEvents > 0)
                {
                    score -= conflictEvents * 5;
                }

                // Hata puanı;
                var errorEvents = recentEvents;
                    .Count(e => e.EventType == ContextEventType.Error);

                if (errorEvents > 0)
                {
                    score -= errorEvents * 10;
                }

                return Math.Max(0.0, Math.Min(100.0, score));
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Arama kriterlerine uygunluğu kontrol et;
        /// </summary>
        private bool MatchesCriteria(InteractionContext context, ContextSearchCriteria criteria)
        {
            try
            {
                // Kullanıcı ID kontrolü;
                if (!string.IsNullOrEmpty(criteria.UserId) && context.UserId != criteria.UserId)
                    return false;

                // Oturum ID kontrolü;
                if (!string.IsNullOrEmpty(criteria.SessionId) && context.SessionId != criteria.SessionId)
                    return false;

                // Bağlam türü kontrolü;
                if (criteria.ContextTypes != null && criteria.ContextTypes.Any() &&
                    !criteria.ContextTypes.Contains(context.ContextType))
                    return false;

                // Durum kontrolü;
                if (criteria.States != null && criteria.States.Any() &&
                    !criteria.States.Contains(context.State))
                    return false;

                // Tarih aralığı kontrolü;
                if (criteria.CreatedAfter.HasValue && context.CreatedAt < criteria.CreatedAfter.Value)
                    return false;

                if (criteria.CreatedBefore.HasValue && context.CreatedAt > criteria.CreatedBefore.Value)
                    return false;

                // Anahtar kelime araması;
                if (!string.IsNullOrEmpty(criteria.Keyword))
                {
                    var keyword = criteria.Keyword.ToLowerInvariant();
                    var hasKeyword = context.Data.Any(kv =>
                        kv.Value?.ToString()?.ToLowerInvariant().Contains(keyword) == true) ||
                        context.ContextType.ToString().ToLowerInvariant().Contains(keyword) ||
                        context.UserId.ToLowerInvariant().Contains(keyword);

                    if (!hasKeyword)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        #region Tanılama Testleri;

        /// <summary>
        /// Bağlam tutarlılık testi;
        /// </summary>
        private async Task<ContextTestResult> TestContextConsistencyAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var inconsistencies = new List<string>();

                // Tüm bağlamları kontrol et;
                foreach (var context in _activeContexts.Values)
                {
                    // Geçerli bağlam kontrolü;
                    if (string.IsNullOrEmpty(context.ContextId) ||
                        string.IsNullOrEmpty(context.UserId) ||
                        string.IsNullOrEmpty(context.SessionId))
                    {
                        inconsistencies.Add($"Invalid context: {context.ContextId}");
                    }

                    // Veri tutarlılığı kontrolü;
                    if (context.Data == null)
                    {
                        inconsistencies.Add($"Null data in context: {context.ContextId}");
                    }

                    // Meta veri tutarlılığı kontrolü;
                    if (context.Metadata == null)
                    {
                        inconsistencies.Add($"Null metadata in context: {context.ContextId}");
                    }
                }

                stopwatch.Stop();

                return new ContextTestResult;
                {
                    Success = !inconsistencies.Any(),
                    Details = inconsistencies.Any()
                        ? $"Found {inconsistencies.Count} inconsistencies: {string.Join(", ", inconsistencies.Take(5))}"
                        : "All contexts are consistent",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Inconsistencies = inconsistencies;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContextTestResult;
                {
                    Success = false,
                    Details = $"Consistency test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// Bellek yönetimi testi;
        /// </summary>
        private async Task<ContextTestResult> TestMemoryManagementAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var memoryUsage = CalculateMemoryUsage();
                var isHealthy = memoryUsage <= _config.MaxMemoryUsageMB;

                stopwatch.Stop();

                return new ContextTestResult;
                {
                    Success = isHealthy,
                    Details = isHealthy;
                        ? $"Memory usage healthy: {memoryUsage:F2}MB"
                        : $"High memory usage: {memoryUsage:F2}MB (max: {_config.MaxMemoryUsageMB}MB)",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContextTestResult;
                {
                    Success = false,
                    Details = $"Memory test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// Geçmiş tutarlılık testi;
        /// </summary>
        private async Task<ContextTestResult> TestHistoryConsistencyAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var inconsistencies = new List<string>();

                // Her bağlam için geçmiş tutarlılığını kontrol et;
                foreach (var kvp in _contextHistory)
                {
                    var contextId = kvp.Key;
                    var history = kvp.Value;

                    // Zaman sıralaması kontrolü;
                    for (int i = 1; i < history.Count; i++)
                    {
                        if (history[i].Timestamp < history[i - 1].Timestamp)
                        {
                            inconsistencies.Add($"History out of order: {contextId}");
                            break;
                        }
                    }

                    // Eksik bağlam kontrolü;
                    if (!_activeContexts.ContainsKey(contextId) &&
                        !history.Any(e => e.EventType == ContextEventType.Deleted ||
                                         e.EventType == ContextEventType.Archived))
                    {
                        inconsistencies.Add($"Missing context with history: {contextId}");
                    }
                }

                stopwatch.Stop();

                return new ContextTestResult;
                {
                    Success = !inconsistencies.Any(),
                    Details = inconsistencies.Any()
                        ? $"Found {inconsistencies.Count} history inconsistencies"
                        : "All history entries are consistent",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Inconsistencies = inconsistencies;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContextTestResult;
                {
                    Success = false,
                    Details = $"History test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// Performans testi;
        /// </summary>
        private async Task<ContextTestResult> TestPerformanceAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Bağlam oluşturma performansı;
                var createStart = DateTime.UtcNow;
                var testContext = await CreateInteractionContextAsync(
                    "TEST_SESSION_" + Guid.NewGuid().ToString("N"),
                    "TEST_USER_" + Guid.NewGuid().ToString("N"),
                    ContextType.Background);
                var createTime = (DateTime.UtcNow - createStart).TotalMilliseconds;

                // Bağlam güncelleme performansı;
                var updateStart = DateTime.UtcNow;
                await UpdateContextAsync(testContext.ContextId, new Dictionary<string, object>
                {
                    ["TestKey"] = "TestValue",
                    ["Performance"] = "Test"
                });
                var updateTime = (DateTime.UtcNow - updateStart).TotalMilliseconds;

                // Bağlam getirme performansı;
                var getStart = DateTime.UtcNow;
                await GetContextAsync(testContext.ContextId);
                var getTime = (DateTime.UtcNow - getStart).TotalMilliseconds;

                // Test bağlamını temizle;
                await DeleteContextAsync(testContext.ContextId, "System", true);

                stopwatch.Stop();

                var isHealthy = createTime < 100 && updateTime < 50 && getTime < 20;

                return new ContextTestResult;
                {
                    Success = isHealthy,
                    Details = $"Create: {createTime:F0}ms, Update: {updateTime:F0}ms, Get: {getTime:F0}ms",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    PerformanceMetrics = new Dictionary<string, double>
                    {
                        ["CreateTimeMs"] = createTime,
                        ["UpdateTimeMs"] = updateTime,
                        ["GetTimeMs"] = getTime;
                    }
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContextTestResult;
                {
                    Success = false,
                    Details = $"Performance test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        /// <summary>
        /// Güvenlik testi;
        /// </summary>
        private async Task<ContextTestResult> TestSecurityAsync()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var securityIssues = new List<string>();

                // Hassas veri kontrolü;
                foreach (var context in _activeContexts.Values)
                {
                    if (context.Metadata.Sensitivity == ContextSensitivity.High ||
                        context.Metadata.Sensitivity == ContextSensitivity.Critical)
                    {
                        // Şifreleme kontrolü;
                        if (!context.Data.ContainsKey("IsEncrypted") ||
                            !(bool)context.Data.GetValueOrDefault("IsEncrypted", false))
                        {
                            securityIssues.Add($"Unencrypted sensitive context: {context.ContextId}");
                        }

                        // Erişim kontrolü;
                        if (!context.Data.ContainsKey("AccessControl") ||
                            context.Data["AccessControl"] == null)
                        {
                            securityIssues.Add($"Missing access control for sensitive context: {context.ContextId}");
                        }
                    }
                }

                stopwatch.Stop();

                return new ContextTestResult;
                {
                    Success = !securityIssues.Any(),
                    Details = securityIssues.Any()
                        ? $"Found {securityIssues.Count} security issues"
                        : "All security checks passed",
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Inconsistencies = securityIssues;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new ContextTestResult;
                {
                    Success = false,
                    Details = $"Security test error: {ex.Message}",
                    DurationMs = stopwatch.ElapsedMilliseconds;
                };
            }
        }

        #endregion;

        #region Bildirim Metodları;

        /// <summary>
        /// Kritik sağlık bildirimi;
        /// </summary>
        private async Task NotifyCriticalHealthAsync(ContextHealth health)
        {
            try
            {
                var healthAlert = new ContextHealthAlert;
                {
                    Timestamp = DateTime.UtcNow,
                    HealthScore = health.HealthScore,
                    ActiveContexts = health.ActiveContexts,
                    MemoryUsage = health.MemoryUsage,
                    AverageContextAge = health.AverageContextAge,
                    Message = $"Critical context health: {health.HealthScore}%",
                    RequiresAttention = true;
                };

                await _eventBus.PublishAsync(healthAlert);
                _logger.LogWarning("Critical health alert sent: {HealthScore}%", health.HealthScore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending health alert");
            }
        }

        #endregion;

        #region Loglama Metodları;

        /// <summary>
        /// Bağlam durumunu logla;
        /// </summary>
        private void LogContextState(string state, string message, ContextState contextState)
        {
            try
            {
                var logEntry = new;
                {
                    Timestamp = DateTime.UtcNow,
                    Component = "ContextManager",
                    State = state,
                    Message = message,
                    ContextState = contextState,
                    ActiveContexts = _activeContexts.Count,
                    MemoryUsage = CalculateMemoryUsage()
                };

                _logger.LogDebug("Context state: {@LogEntry}", logEntry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging context state");
            }
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// Bağlam değişti event handler;
        /// </summary>
        private void OnContextChanged(ContextChangedEventArgs e)
        {
            try
            {
                ContextChanged?.Invoke(this, e);
                LogContextState("Context_Changed",
                    $"Context changed: {e.ContextId}, {e.OldState} -> {e.NewState}",
                    e.NewState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in context changed event");
            }
        }

        /// <summary>
        /// Bağlam çakışması tespit edildi event handler;
        /// </summary>
        private void OnContextConflictDetected(ContextConflictEventArgs e)
        {
            try
            {
                ContextConflictDetected?.Invoke(this, e);
                LogContextState("Context_Conflict",
                    $"Context conflict detected: {e.Conflict.ConflictType}, Severity: {e.Conflict.Severity}",
                    ContextState.Warning);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in context conflict detected event");
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
                var status = GetContextStatus();
                if (status.HealthScore < 80)
                {
                    _logger.LogDebug("Context health check: {HealthScore}%", status.HealthScore);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health check timer");
            }
        }

        private async Task OnSessionStarted(SessionStartedEvent sessionEvent)
        {
            _ = Task.Run(async () =>
            {
                _logger.LogInformation("Session started, creating initial context: {SessionId}",
                    sessionEvent.SessionId);

                // Oturum için başlangıç bağlamı oluştur;
                await CreateInteractionContextAsync(
                    sessionEvent.SessionId,
                    sessionEvent.UserId,
                    ContextType.RealTime,
                    new Dictionary<string, object>
                    {
                        ["SessionStartTime"] = sessionEvent.StartedAt,
                        ["ClientInfo"] = sessionEvent.ClientInfo,
                        ["InitialAction"] = "SessionStart"
                    });
            });
        }

        private async Task OnSessionEnded(SessionEndedEvent sessionEvent)
        {
            _ = Task.Run(async () =>
            {
                _logger.LogInformation("Session ended, archiving contexts: {SessionId}",
                    sessionEvent.SessionId);

                // Oturumla ilişkili tüm bağlamları arşivle;
                var sessionContexts = _activeContexts.Values;
                    .Where(c => c.SessionId == sessionEvent.SessionId)
                    .ToList();

                foreach (var context in sessionContexts)
                {
                    await ArchiveContextAsync(context.ContextId, "System");
                }
            });
        }

        private async Task OnUserProfileUpdated(UserProfileUpdatedEvent profileEvent)
        {
            _ = Task.Run(async () =>
            {
                _logger.LogDebug("User profile updated, updating contexts: {UserId}",
                    profileEvent.UserId);

                // Kullanıcının tüm bağlamlarını güncelle;
                var userContexts = _activeContexts.Values;
                    .Where(c => c.UserId == profileEvent.UserId)
                    .ToList();

                foreach (var context in userContexts)
                {
                    await UpdateContextAsync(context.ContextId, new Dictionary<string, object>
                    {
                        ["ProfileUpdated"] = true,
                        ["ProfileUpdateTime"] = profileEvent.UpdatedAt,
                        ["ProfileChanges"] = profileEvent.Changes;
                    }, "System");
                }
            });
        }

        private async Task OnPreferenceChanged(PreferenceChangedEvent preferenceEvent)
        {
            _ = Task.Run(async () =>
            {
                _logger.LogDebug("Preference changed, updating contexts: {UserId}",
                    preferenceEvent.UserId);

                // Kullanıcının tüm bağlamlarını güncelle;
                var userContexts = _activeContexts.Values;
                    .Where(c => c.UserId == preferenceEvent.UserId)
                    .ToList();

                foreach (var context in userContexts)
                {
                    await UpdateContextAsync(context.ContextId, new Dictionary<string, object>
                    {
                        ["PreferencesUpdated"] = true,
                        ["PreferenceChanges"] = preferenceEvent.Changes;
                    }, "System");
                }
            });
        }

        #endregion;

        #endregion;
    }

    /// <summary>
    /// Bağlam yöneticisi konfigürasyonu;
    /// </summary>
    public class ContextManagerConfig;
    {
        public int CleanupIntervalMs { get; set; } = 300000; // 5 dakika;
        public int HealthCheckIntervalMs { get; set; } = 60000; // 1 dakika;

        public TimeSpan ShortTermContextTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan LongTermContextInactivityTimeout { get; set; } = TimeSpan.FromDays(7);
        public TimeSpan HistoryRetentionDays { get; set; } = TimeSpan.FromDays(90);

        public double MaxMemoryUsageMB { get; set; } = 100.0;
        public int OptimizationBatchSize { get; set; } = 10;
        public int PersistentSaveInterval { get; set; } = 10; // Her 10. versiyonda kaydet;

        public double CriticalHealthThreshold { get; set; } = 50.0;
        public double WarningHealthThreshold { get; set; } = 80.0;

        public bool EnableConflictDetection { get; set; } = true;
        public bool EnableAutoResolution { get; set; } = true;
        public bool EnableHistoryTracking { get; set; } = true;
    }

    /// <summary>
    /// Bağlam durumları;
    /// </summary>
    public enum ContextState;
    {
        None = 0,
        Active = 1,
        Inactive = 2,
        Archived = 3,
        Deleted = 4,
        Error = 5,
        Warning = 6,
        Creating = 7,
        Updating = 8,
        Deleting = 9,
        Restoring = 10,
        Archiving = 11;
    }

    /// <summary>
    /// Bağlam türleri;
    /// </summary>
    public enum ContextType;
    {
        Unknown = 0,
        RealTime = 1,
        LongTerm = 2,
        System = 3,
        Background = 4,
        Critical = 5,
        User = 6,
        Environment = 7;
    }

    /// <summary>
    /// Bağlam öncelik seviyeleri;
    /// </summary>
    public enum ContextPriority;
    {
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /// Bağlam karmaşıklık seviyeleri;
    /// </summary>
    public enum ContextComplexity;
    {
        Low = 1,
        Medium = 2,
        High = 3;
    }

    /// <summary>
    /// Bağlam hassasiyet seviyeleri;
    /// </summary>
    public enum ContextSensitivity;
    {
        Normal = 1,
        Sensitive = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /// Bağlam event tipleri;
    /// </summary>
    public enum ContextEventType;
    {
        Unknown = 0,
        Created = 1,
        Updated = 2,
        Deleted = 3,
        Archived = 4,
        Restored = 5,
        ConflictDetected = 6,
        ConflictResolved = 7,
        StateChanged = 8,
        Error = 9,
        ForceDeleted = 10;
    }

    /// <summary>
    /// Bağlam değişiklik türleri;
    /// </summary>
    public enum ContextChangeType;
    {
        Unknown = 0,
        Created = 1,
        Updated = 2,
        Deleted = 3,
        Archived = 4,
        Restored = 5,
        StateChanged = 6,
        ForceDeleted = 7;
    }

    /// <summary>
    /// Bağlam çakışma türleri;
    /// </summary>
    [Flags]
    public enum ContextConflictType;
    {
        None = 0,
        Data = 1,
        State = 2,
        Priority = 4,
        Resource = 8,
        Time = 16,
        Predicted = 32,
        All = Data | State | Priority | Resource | Time | Predicted;
    }

    /// <summary>
    /// Çakışma şiddet seviyeleri;
    /// </summary>
    public enum ConflictSeverity;
    {
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /// Veri çakışma türleri;
    /// </summary>
    public enum DataConflictType;
    {
        None = 0,
        ValueMismatch = 1,
        TypeMismatch = 2,
        MissingKey = 3,
        DuplicateKey = 4;
    }

    /// <summary>
    /// Ortam event tipleri;
    /// </summary>
    public enum EnvironmentEventType;
    {
        Unknown = 0,
        Created = 1,
        Updated = 2,
        DeviceChanged = 3,
        LocationChanged = 4,
        SettingsChanged = 5;
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
    /// Etkileşim bağlamı;
    /// </summary>
    public class InteractionContext;
    {
        public string ContextId { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public ContextType ContextType { get; set; }
        public ContextState State { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public int Version { get; set; }

        public Dictionary<string, object> Data { get; set; }
        public ContextMetadata Metadata { get; set; }
        public EnvironmentContext Environment { get; set; }
        public List<ContextEvent> History { get; set; }

        public Dictionary<string, object> Tags { get; set; }
        public List<string> RelatedContexts { get; set; }
    }

    /// <summary>
    /// Bağlam meta verileri;
    /// </summary>
    public class ContextMetadata;
    {
        public ContextPriority Priority { get; set; }
        public ContextComplexity Complexity { get; set; }
        public ContextSensitivity Sensitivity { get; set; }
        public bool IsPersistent { get; set; }
        public bool PreferencesLoaded { get; set; }
        public bool HistoryLoaded { get; set; }
        public bool EnvironmentLoaded { get; set; }
        public bool PatternsLearned { get; set; }
        public DateTime? LastAnalyzed { get; set; }
        public int Version { get; set; }

        public Dictionary<string, object> CustomMetadata { get; set; }
    }

    /// <summary>
    /// Ortam bağlamı;
    /// </summary>
    public class EnvironmentContext;
    {
        public string SessionId { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }

        public Dictionary<string, object> EnvironmentData { get; set; }
        public Dictionary<string, object> DeviceInfo { get; set; }
        public Dictionary<string, object> LocationInfo { get; set; }
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>
    /// Bağlam event'i;
    /// </summary>
    public class ContextEvent;
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public ContextEventType EventType { get; set; }
        public string ContextId { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Ortam event'i;
    /// </summary>
    public class EnvironmentEvent;
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public EnvironmentEventType EventType { get; set; }
        public string SessionId { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> EnvironmentData { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Bağlam durumu;
    /// </summary>
    public class ContextStatus;
    {
        public DateTime Timestamp { get; set; }
        public int ActiveContexts { get; set; }
        public int EnvironmentContexts { get; set; }
        public int TotalHistoryEntries { get; set; }
        public double MemoryUsage { get; set; }
        public TimeSpan AverageContextAge { get; set; }
        public Dictionary<ContextType, int> ContextDistribution { get; set; }
        public double HealthScore { get; set; }
    }

    /// <summary>
    /// Bağlam değişti event args;
    /// </summary>
    public class ContextChangedEventArgs : EventArgs;
    {
        public string ContextId { get; set; }
        public ContextState OldState { get; set; }
        public ContextState NewState { get; set; }
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; }
        public ContextChangeType ChangeType { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Bağlam çakışması event args;
    /// </summary>
    public class ContextConflictEventArgs : EventArgs;
    {
        public ContextConflict Conflict { get; set; }
        public DateTime DetectedAt { get; set; }
        public bool Resolved { get; set; }
    }

    /// <summary>
    /// Bağlam çakışması;
    /// </summary>
    public class ContextConflict;
    {
        public Guid ConflictId { get; set; }
        public DateTime Timestamp { get; set; }
        public string ContextId1 { get; set; }
        public string ContextId2 { get; set; }
        public string UserId { get; set; }
        public ContextConflictType ConflictType { get; set; }
        public ConflictSeverity Severity { get; set; }
        public List<DataConflict> DataConflicts { get; set; }
        public double? PredictionConfidence { get; set; }
        public bool Resolved { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string Resolution { get; set; }
    }

    /// <summary>
    /// Veri çakışması;
    /// </summary>
    public class DataConflict;
    {
        public string Key { get; set; }
        public object Value1 { get; set; }
        public object Value2 { get; set; }
        public DataConflictType ConflictType { get; set; }
    }

    /// <summary>
    /// Bağlam arama kriterleri;
    /// </summary>
    public class ContextSearchCriteria;
    {
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public List<ContextType> ContextTypes { get; set; }
        public List<ContextState> States { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
        public string Keyword { get; set; }
        public bool IncludeInactive { get; set; }
        public bool IncludeArchived { get; set; }
    }

    /// <summary>
    /// Geçmiş bağlam;
    /// </summary>
    public class HistoricalContext;
    {
        public string ContextId { get; set; }
        public ContextType ContextType { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public Dictionary<string, object> Summary { get; set; }
        public double SuccessRate { get; set; }
    }

    /// <summary>
    /// Bağlam arşivi;
    /// </summary>
    public class ContextArchive;
    {
        public Guid ArchiveId { get; set; }
        public string ContextId { get; set; }
        public string UserId { get; set; }
        public ContextType ContextType { get; set; }
        public DateTime ArchivedAt { get; set; }
        public Dictionary<string, object> ContextData { get; set; }
        public ContextMetadata Metadata { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Bağlam sağlığı;
    /// </summary>
    public class ContextHealth;
    {
        public DateTime Timestamp { get; set; }
        public int ActiveContexts { get; set; }
        public double MemoryUsage { get; set; }
        public TimeSpan AverageContextAge { get; set; }
        public double HealthScore { get; set; }
    }

    /// <summary>
    /// Bağlam tanılama;
    /// </summary>
    public class ContextDiagnostics;
    {
        public DateTime Timestamp { get; set; }
        public Guid TestId { get; set; }
        public DiagnosticsStatus OverallStatus { get; set; }
        public string Message { get; set; }
        public ContextTestResult ConsistencyTest { get; set; }
        public ContextTestResult MemoryTest { get; set; }
        public ContextTestResult HistoryTest { get; set; }
        public ContextTestResult PerformanceTest { get; set; }
        public ContextTestResult SecurityTest { get; set; }
        public List<ContextTest> Tests { get; set; } = new List<ContextTest>();
        public ContextStatus ContextStatus { get; set; }
    }

    /// <summary>
    /// Bağlam testi;
    /// </summary>
    public class ContextTest;
    {
        public string TestName { get; set; }
        public bool Success { get; set; }
        public string Details { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// Bağlam test sonucu;
    /// </summary>
    public class ContextTestResult;
    {
        public bool Success { get; set; }
        public string Details { get; set; }
        public long DurationMs { get; set; }
        public List<string> Inconsistencies { get; set; }
        public Dictionary<string, double> PerformanceMetrics { get; set; }
    }

    /// <summary>
    /// Bağlam yöneticisi interface'i;
    /// </summary>
    public interface IContextManager : IDisposable
    {
        event EventHandler<ContextChangedEventArgs> ContextChanged;
        event EventHandler<ContextConflictEventArgs> ContextConflictDetected;

        void StartContextManagement();
        Task StopContextManagementAsync();

        Task<InteractionContext> CreateInteractionContextAsync(
            string sessionId, string userId, ContextType contextType, Dictionary<string, object> initialData = null);
        Task<InteractionContext> UpdateContextAsync(
            string contextId, Dictionary<string, object> updates, string updatedBy = null);
        Task<InteractionContext> GetContextAsync(string contextId, bool includeHistory = false);
        Task<bool> DeleteContextAsync(string contextId, string deletedBy = null, bool force = false);
        Task<bool> ArchiveContextAsync(string contextId, string archivedBy = null);
        Task<InteractionContext> RestoreContextAsync(string contextId, string restoredBy = null);

        Task<List<InteractionContext>> SearchContextsAsync(
            ContextSearchCriteria criteria, int page = 1, int pageSize = 50);
        Task<List<ContextEvent>> GetContextHistoryAsync(
            string contextId, DateTime? startDate = null, DateTime? endDate = null, ContextEventType? eventType = null);

        Task<EnvironmentContext> UpdateEnvironmentContextAsync(string sessionId, EnvironmentContext environment);

        ContextStatus GetContextStatus();
        Task<ContextDiagnostics> RunDiagnosticsAsync();
        void ClearContextHistory(string contextId = null);
    }

    #region Event Sınıfları;

    /// <summary>
    /// Oturum başladı event;
    /// </summary>
    public class SessionStartedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime StartedAt { get; set; }
        public Dictionary<string, object> ClientInfo { get; set; }
    }

    /// <summary>
    /// Oturum bitti event;
    /// </summary>
    public class SessionEndedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime EndedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Kullanıcı profili güncellendi event;
    /// </summary>
    public class UserProfileUpdatedEvent : IEvent;
    {
        public string UserId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public Dictionary<string, object> Changes { get; set; }
    }

    /// <summary>
    /// Tercih değişti event;
    /// </summary>
    public class PreferenceChangedEvent : IEvent;
    {
        public string UserId { get; set; }
        public DateTime ChangedAt { get; set; }
        public Dictionary<string, object> Changes { get; set; }
    }

    /// <summary>
    /// Bağlam kalıcı hale getirildi event;
    /// </summary>
    public class ContextPersistedEvent : IEvent;
    {
        public string ContextId { get; set; }
        public string UserId { get; set; }
        public ContextType ContextType { get; set; }
        public DateTime Timestamp { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Bağlam event mesajı;
    /// </summary>
    public class ContextEventMessage : IEvent;
    {
        public Guid EventId { get; set; }
        public string ContextId { get; set; }
        public string EventType { get; set; }
        public DateTime Timestamp { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Ortam event mesajı;
    /// </summary>
    public class EnvironmentEventMessage : IEvent;
    {
        public Guid EventId { get; set; }
        public string SessionId { get; set; }
        public string EventType { get; set; }
        public DateTime Timestamp { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> EnvironmentData { get; set; }
    }

    /// <summary>
    /// Bağlam çakışma mesajı;
    /// </summary>
    public class ContextConflictMessage : IEvent;
    {
        public Guid ConflictId { get; set; }
        public DateTime Timestamp { get; set; }
        public string ContextId1 { get; set; }
        public string ContextId2 { get; set; }
        public string ConflictType { get; set; }
        public string Severity { get; set; }
        public bool Resolved { get; set; }
        public string Resolution { get; set; }
    }

    /// <summary>
    /// Bağlam sağlık alarmı;
    /// </summary>
    public class ContextHealthAlert : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public double HealthScore { get; set; }
        public int ActiveContexts { get; set; }
        public double MemoryUsage { get; set; }
        public TimeSpan AverageContextAge { get; set; }
        public string Message { get; set; }
        public bool RequiresAttention { get; set; }
    }

    #endregion;
}
