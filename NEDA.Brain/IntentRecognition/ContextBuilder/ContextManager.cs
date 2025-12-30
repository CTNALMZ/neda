using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.Common.Utilities;
using NEDA.Communication.DialogSystem.ConversationManager;
using NEDA.Interface.InteractionManager.SessionHandler;
using NEDA.Interface.InteractionManager.UserProfileManager;
using NEDA.Interface.InteractionManager.ConversationHistory;

namespace NEDA.Interface.InteractionManager.ContextKeeper;
{
    /// <summary>
    /// Context management service for maintaining conversation and session context;
    /// across multiple interactions and user sessions.
    /// </summary>
    public interface IContextManager : IDisposable
    {
        /// <summary>
        /// Creates a new context for a user session;
        /// </summary>
        Task<ContextSession> CreateContextAsync(string userId, string sessionId, ContextOptions options = null);

        /// <summary>
        /// Retrieves the current context for a session;
        /// </summary>
        Task<ContextSession> GetContextAsync(string sessionId);

        /// <summary>
        /// Updates context with new interaction data;
        /// </summary>
        Task UpdateContextAsync(string sessionId, ContextUpdate update);

        /// <summary>
        /// Saves context state to persistent storage;
        /// </summary>
        Task SaveContextAsync(string sessionId);

        /// <summary>
        /// Restores context from persistent storage;
        /// </summary>
        Task<ContextSession> RestoreContextAsync(string sessionId);

        /// <summary>
        /// Clears context for a session;
        /// </summary>
        Task ClearContextAsync(string sessionId);

        /// <summary>
        /// Merges multiple contexts (for multi-modal interactions)
        /// </summary>
        Task<ContextSession> MergeContextsAsync(params string[] sessionIds);

        /// <summary>
        /// Gets contextual suggestions based on current state;
        /// </summary>
        Task<IEnumerable<ContextualSuggestion>> GetSuggestionsAsync(string sessionId, SuggestionType type);

        /// <summary>
        /// Checks if context has expired and needs refresh;
        /// </summary>
        Task<bool> IsContextExpiredAsync(string sessionId);

        /// <summary>
        /// Validates context integrity;
        /// </summary>
        Task<ContextValidationResult> ValidateContextAsync(string sessionId);
    }

    /// <summary>
    /// Implementation of context management service;
    /// </summary>
    public class ContextManager : IContextManager;
    {
        private readonly ILogger<ContextManager> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IUserSessionManager _sessionManager;
        private readonly IConversationHistoryManager _historyManager;
        private readonly IUserProfileManager _profileManager;
        private readonly ConcurrentDictionary<string, ContextSession> _activeContexts;
        private readonly SemaphoreSlim _contextLock = new(1, 1);
        private readonly TimeSpan _contextTimeout = TimeSpan.FromMinutes(30);
        private readonly ContextPersistenceService _persistenceService;
        private bool _disposed;

        public ContextManager(
            ILogger<ContextManager> logger,
            IServiceProvider serviceProvider,
            IUserSessionManager sessionManager,
            IConversationHistoryManager historyManager,
            IUserProfileManager profileManager,
            ContextPersistenceService persistenceService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _historyManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
            _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
            _activeContexts = new ConcurrentDictionary<string, ContextSession>();

            _logger.LogInformation("ContextManager initialized with {Timeout} timeout", _contextTimeout);
        }

        /// <summary>
        /// Creates a new context session with initial state;
        /// </summary>
        public async Task<ContextSession> CreateContextAsync(string userId, string sessionId, ContextOptions options = null)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _contextLock.WaitAsync();
            try
            {
                // Check if context already exists;
                if (_activeContexts.TryGetValue(sessionId, out var existingContext))
                {
                    _logger.LogWarning("Context already exists for session {SessionId}, returning existing", sessionId);
                    return existingContext;
                }

                // Get user profile for context initialization;
                var userProfile = await _profileManager.GetUserProfileAsync(userId);

                // Get session details;
                var session = await _sessionManager.GetSessionAsync(sessionId);

                // Create new context;
                var context = new ContextSession;
                {
                    Id = Guid.NewGuid().ToString(),
                    SessionId = sessionId,
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    State = ContextState.Active,
                    Properties = new ConcurrentDictionary<string, object>(),

                    // Initialize with profile data;
                    UserPreferences = userProfile?.Preferences ?? new UserPreferences(),
                    CurrentTopics = new List<string>(),
                    RecentActions = new Queue<ContextAction>(options?.MaxActionHistory ?? 50),
                    PendingTasks = new List<ContextTask>(),
                    ConversationFlow = new ConversationFlow(),

                    // System properties;
                    SystemContext = new SystemContext;
                    {
                        DeviceInfo = session?.DeviceInfo,
                        Location = session?.Location,
                        Language = userProfile?.Language ?? "en-US",
                        TimeZone = userProfile?.TimeZone ?? TimeZoneInfo.Utc.Id;
                    },

                    // Emotional context;
                    EmotionalState = new EmotionalContext;
                    {
                        CurrentEmotion = Emotion.Neutral,
                        EmotionalHistory = new List<EmotionalSnapshot>(),
                        StressLevel = 0,
                        EngagementLevel = 1.0f;
                    },

                    // Options;
                    Options = options ?? ContextOptions.Default;
                };

                // Set default properties;
                context.Properties["Initialized"] = true;
                context.Properties["CreationSource"] = "ContextManager";

                // Add to active contexts;
                if (_activeContexts.TryAdd(sessionId, context))
                {
                    _logger.LogInformation("Created new context {ContextId} for user {UserId} session {SessionId}",
                        context.Id, userId, sessionId);

                    // Initialize conversation flow;
                    await InitializeConversationFlowAsync(context);

                    // Load recent history if available;
                    await LoadRecentHistoryAsync(context);

                    return context;
                }

                throw new InvalidOperationException($"Failed to create context for session {sessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating context for user {UserId} session {SessionId}", userId, sessionId);
                throw new ContextCreationException($"Failed to create context: {ex.Message}", ex);
            }
            finally
            {
                _contextLock.Release();
            }
        }

        /// <summary>
        /// Retrieves the current context for a session;
        /// </summary>
        public async Task<ContextSession> GetContextAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            // Check memory cache first;
            if (_activeContexts.TryGetValue(sessionId, out var context))
            {
                // Check if context is expired;
                if (await IsContextExpiredAsync(sessionId))
                {
                    _logger.LogWarning("Context for session {SessionId} has expired", sessionId);
                    await HandleExpiredContextAsync(context);
                    return null;
                }

                context.LastAccessed = DateTime.UtcNow;
                return context;
            }

            // Try to restore from persistence;
            _logger.LogInformation("Context not found in memory for session {SessionId}, attempting restore", sessionId);
            return await RestoreContextAsync(sessionId);
        }

        /// <summary>
        /// Updates context with new interaction data;
        /// </summary>
        public async Task UpdateContextAsync(string sessionId, ContextUpdate update)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (update == null)
                throw new ArgumentNullException(nameof(update));

            await _contextLock.WaitAsync();
            try
            {
                var context = await GetContextAsync(sessionId);
                if (context == null)
                {
                    throw new ContextNotFoundException($"Context not found for session {sessionId}");
                }

                // Apply updates based on type;
                switch (update.Type)
                {
                    case ContextUpdateType.ConversationUpdate:
                        await ApplyConversationUpdateAsync(context, update);
                        break;

                    case ContextUpdateType.TaskUpdate:
                        await ApplyTaskUpdateAsync(context, update);
                        break;

                    case ContextUpdateType.PreferenceUpdate:
                        await ApplyPreferenceUpdateAsync(context, update);
                        break;

                    case ContextUpdateType.SystemUpdate:
                        await ApplySystemUpdateAsync(context, update);
                        break;

                    case ContextUpdateType.EmotionalUpdate:
                        await ApplyEmotionalUpdateAsync(context, update);
                        break;

                    case ContextUpdateType.CustomPropertyUpdate:
                        await ApplyCustomPropertyUpdateAsync(context, update);
                        break;
                }

                // Update metadata;
                context.LastUpdated = DateTime.UtcNow;
                context.Version++;

                // Record action in history;
                var action = new ContextAction;
                {
                    Id = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    Type = update.Type.ToString(),
                    Description = update.Description,
                    Data = update.Data;
                };

                context.RecentActions.Enqueue(action);
                if (context.RecentActions.Count > context.Options.MaxActionHistory)
                {
                    context.RecentActions.Dequeue();
                }

                // Trigger context changed event;
                await OnContextChangedAsync(context, update);

                _logger.LogDebug("Updated context {ContextId} with {UpdateType}", context.Id, update.Type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating context for session {SessionId}", sessionId);
                throw new ContextUpdateException($"Failed to update context: {ex.Message}", ex);
            }
            finally
            {
                _contextLock.Release();
            }
        }

        /// <summary>
        /// Saves context state to persistent storage;
        /// </summary>
        public async Task SaveContextAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _contextLock.WaitAsync();
            try
            {
                var context = await GetContextAsync(sessionId);
                if (context == null)
                {
                    throw new ContextNotFoundException($"Context not found for session {sessionId}");
                }

                // Serialize and save context;
                await _persistenceService.SaveContextAsync(context);

                // Update context state;
                context.IsDirty = false;
                context.LastSaved = DateTime.UtcNow;

                _logger.LogInformation("Saved context {ContextId} to persistent storage", context.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving context for session {SessionId}", sessionId);
                throw new ContextPersistenceException($"Failed to save context: {ex.Message}", ex);
            }
            finally
            {
                _contextLock.Release();
            }
        }

        /// <summary>
        /// Restores context from persistent storage;
        /// </summary>
        public async Task<ContextSession> RestoreContextAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _contextLock.WaitAsync();
            try
            {
                // Try to restore from persistence service;
                var context = await _persistenceService.RestoreContextAsync(sessionId);
                if (context != null)
                {
                    // Add to active contexts;
                    if (_activeContexts.TryAdd(sessionId, context))
                    {
                        context.LastRestored = DateTime.UtcNow;
                        context.State = ContextState.Restored;

                        _logger.LogInformation("Restored context {ContextId} from persistent storage", context.Id);
                        return context;
                    }
                }

                _logger.LogWarning("Unable to restore context for session {SessionId}", sessionId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring context for session {SessionId}", sessionId);
                throw new ContextRestorationException($"Failed to restore context: {ex.Message}", ex);
            }
            finally
            {
                _contextLock.Release();
            }
        }

        /// <summary>
        /// Clears context for a session;
        /// </summary>
        public async Task ClearContextAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _contextLock.WaitAsync();
            try
            {
                if (_activeContexts.TryRemove(sessionId, out var context))
                {
                    // Save before clearing if dirty;
                    if (context.IsDirty)
                    {
                        await _persistenceService.SaveContextAsync(context);
                    }

                    // Clear from persistence;
                    await _persistenceService.DeleteContextAsync(sessionId);

                    // Dispose resources;
                    context.Dispose();

                    _logger.LogInformation("Cleared context for session {SessionId}", sessionId);
                }
                else;
                {
                    _logger.LogWarning("No active context found to clear for session {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing context for session {SessionId}", sessionId);
                throw new ContextClearException($"Failed to clear context: {ex.Message}", ex);
            }
            finally
            {
                _contextLock.Release();
            }
        }

        /// <summary>
        /// Merges multiple contexts;
        /// </summary>
        public async Task<ContextSession> MergeContextsAsync(params string[] sessionIds)
        {
            if (sessionIds == null || sessionIds.Length == 0)
                throw new ArgumentException("At least one session ID is required", nameof(sessionIds));

            await _contextLock.WaitAsync();
            try
            {
                var contexts = new List<ContextSession>();

                // Retrieve all contexts;
                foreach (var sessionId in sessionIds)
                {
                    var context = await GetContextAsync(sessionId);
                    if (context != null)
                    {
                        contexts.Add(context);
                    }
                }

                if (contexts.Count == 0)
                    return null;

                if (contexts.Count == 1)
                    return contexts[0];

                // Create merged context;
                var primaryContext = contexts[0];
                var mergedContext = primaryContext.Clone();
                mergedContext.Id = Guid.NewGuid().ToString();
                mergedContext.IsMerged = true;
                mergedContext.SourceContexts = contexts.Select(c => c.Id).ToList();

                // Merge properties;
                foreach (var context in contexts.Skip(1))
                {
                    // Merge topics;
                    if (context.CurrentTopics != null)
                    {
                        mergedContext.CurrentTopics = mergedContext.CurrentTopics;
                            .Union(context.CurrentTopics)
                            .Distinct()
                            .ToList();
                    }

                    // Merge recent actions (take most recent from all contexts)
                    var allActions = contexts;
                        .SelectMany(c => c.RecentActions)
                        .OrderByDescending(a => a.Timestamp)
                        .Take(mergedContext.Options.MaxActionHistory);

                    mergedContext.RecentActions = new Queue<ContextAction>(allActions);

                    // Merge emotional context;
                    mergedContext.EmotionalState.StressLevel = contexts.Average(c => c.EmotionalState.StressLevel);
                    mergedContext.EmotionalState.EngagementLevel = contexts.Average(c => c.EmotionalState.EngagementLevel);

                    // Merge custom properties;
                    foreach (var prop in context.Properties)
                    {
                        if (!mergedContext.Properties.ContainsKey(prop.Key))
                        {
                            mergedContext.Properties[prop.Key] = prop.Value;
                        }
                    }
                }

                // Create new session ID for merged context;
                var mergedSessionId = $"merged_{Guid.NewGuid():N}";
                _activeContexts.TryAdd(mergedSessionId, mergedContext);

                _logger.LogInformation("Merged {Count} contexts into new context {MergedId}",
                    contexts.Count, mergedContext.Id);

                return mergedContext;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error merging contexts for sessions {SessionIds}", string.Join(",", sessionIds));
                throw new ContextMergeException($"Failed to merge contexts: {ex.Message}", ex);
            }
            finally
            {
                _contextLock.Release();
            }
        }

        /// <summary>
        /// Gets contextual suggestions;
        /// </summary>
        public async Task<IEnumerable<ContextualSuggestion>> GetSuggestionsAsync(string sessionId, SuggestionType type)
        {
            var context = await GetContextAsync(sessionId);
            if (context == null)
                return Enumerable.Empty<ContextualSuggestion>();

            var suggestions = new List<ContextualSuggestion>();

            switch (type)
            {
                case SuggestionType.Conversation:
                    suggestions.AddRange(await GetConversationSuggestionsAsync(context));
                    break;

                case SuggestionType.Task:
                    suggestions.AddRange(await GetTaskSuggestionsAsync(context));
                    break;

                case SuggestionType.Preference:
                    suggestions.AddRange(await GetPreferenceSuggestionsAsync(context));
                    break;

                case SuggestionType.System:
                    suggestions.AddRange(await GetSystemSuggestionsAsync(context));
                    break;
            }

            return suggestions.OrderByDescending(s => s.RelevanceScore);
        }

        /// <summary>
        /// Checks if context has expired;
        /// </summary>
        public async Task<bool> IsContextExpiredAsync(string sessionId)
        {
            var context = await GetContextAsync(sessionId);
            if (context == null)
                return true;

            var timeSinceUpdate = DateTime.UtcNow - context.LastUpdated;
            return timeSinceUpdate > _contextTimeout;
        }

        /// <summary>
        /// Validates context integrity;
        /// </summary>
        public async Task<ContextValidationResult> ValidateContextAsync(string sessionId)
        {
            var context = await GetContextAsync(sessionId);
            if (context == null)
            {
                return new ContextValidationResult;
                {
                    IsValid = false,
                    Errors = new List<string> { "Context not found" }
                };
            }

            var errors = new List<string>();

            // Validate required properties;
            if (string.IsNullOrEmpty(context.UserId))
                errors.Add("User ID is missing");

            if (string.IsNullOrEmpty(context.SessionId))
                errors.Add("Session ID is missing");

            if (context.CreatedAt > DateTime.UtcNow)
                errors.Add("Invalid creation timestamp");

            if (context.LastUpdated < context.CreatedAt)
                errors.Add("Last updated before creation");

            // Validate data integrity;
            if (context.RecentActions.Count > context.Options.MaxActionHistory * 2)
                errors.Add("Action history exceeds maximum limit");

            // Check for data corruption;
            if (context.Properties.ContainsKey("Corrupted"))
                errors.Add("Context data is corrupted");

            return new ContextValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                ContextId = context.Id,
                ValidationTime = DateTime.UtcNow;
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _contextLock?.Dispose();

            // Clear all contexts;
            foreach (var context in _activeContexts.Values)
            {
                context.Dispose();
            }
            _activeContexts.Clear();

            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private async Task InitializeConversationFlowAsync(ContextSession context)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var conversationEngine = scope.ServiceProvider.GetRequiredService<IConversationEngine>();

                context.ConversationFlow = await conversationEngine.InitializeFlowAsync(
                    context.UserId,
                    context.SystemContext.Language);

                _logger.LogDebug("Initialized conversation flow for context {ContextId}", context.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing conversation flow for context {ContextId}", context.Id);
                // Continue with default flow;
                context.ConversationFlow = new ConversationFlow();
            }
        }

        private async Task LoadRecentHistoryAsync(ContextSession context)
        {
            try
            {
                var recentHistory = await _historyManager.GetRecentConversationsAsync(
                    context.UserId,
                    TimeSpan.FromDays(7),
                    10);

                if (recentHistory.Any())
                {
                    context.Properties["RecentHistoryLoaded"] = true;
                    context.Properties["LastHistoryTimestamp"] = recentHistory.Max(h => h.Timestamp);

                    // Extract topics from recent history;
                    var topics = recentHistory;
                        .SelectMany(h => h.Topics)
                        .Distinct()
                        .Take(5)
                        .ToList();

                    context.CurrentTopics.AddRange(topics);

                    _logger.LogDebug("Loaded {Count} recent conversations for context {ContextId}",
                        recentHistory.Count, context.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading recent history for context {ContextId}", context.Id);
            }
        }

        private async Task ApplyConversationUpdateAsync(ContextSession context, ContextUpdate update)
        {
            if (update.Data is ConversationUpdateData conversationData)
            {
                // Update current topics;
                if (conversationData.Topics != null)
                {
                    context.CurrentTopics = context.CurrentTopics;
                        .Union(conversationData.Topics)
                        .Distinct()
                        .Take(context.Options.MaxTopics)
                        .ToList();
                }

                // Update conversation flow;
                if (conversationData.FlowUpdate != null)
                {
                    context.ConversationFlow.ApplyUpdate(conversationData.FlowUpdate);
                }

                // Update interaction count;
                context.InteractionCount++;

                // Save to history;
                if (conversationData.SaveToHistory && _historyManager != null)
                {
                    await _historyManager.RecordInteractionAsync(
                        context.UserId,
                        context.SessionId,
                        conversationData.Message,
                        conversationData.Response);
                }
            }
        }

        private async Task ApplyTaskUpdateAsync(ContextSession context, ContextUpdate update)
        {
            if (update.Data is TaskUpdateData taskData)
            {
                var existingTask = context.PendingTasks.FirstOrDefault(t => t.Id == taskData.TaskId);
                if (existingTask != null)
                {
                    // Update existing task;
                    existingTask.Status = taskData.Status;
                    existingTask.Progress = taskData.Progress;
                    existingTask.LastUpdated = DateTime.UtcNow;

                    if (taskData.Result != null)
                    {
                        existingTask.Result = taskData.Result;
                    }
                }
                else if (taskData.NewTask != null)
                {
                    // Add new task;
                    context.PendingTasks.Add(taskData.NewTask);
                }

                // Remove completed tasks if configured;
                if (context.Options.AutoCleanupTasks)
                {
                    context.PendingTasks = context.PendingTasks;
                        .Where(t => t.Status != TaskStatus.Completed && t.Status != TaskStatus.Failed)
                        .Take(context.Options.MaxPendingTasks)
                        .ToList();
                }
            }
        }

        private async Task ApplyPreferenceUpdateAsync(ContextSession context, ContextUpdate update)
        {
            if (update.Data is PreferenceUpdateData preferenceData && _profileManager != null)
            {
                // Update local context;
                if (preferenceData.Preferences != null)
                {
                    foreach (var pref in preferenceData.Preferences)
                    {
                        context.UserPreferences.SetPreference(pref.Key, pref.Value);
                    }
                }

                // Update user profile if persistent;
                if (preferenceData.SaveToProfile)
                {
                    await _profileManager.UpdatePreferencesAsync(
                        context.UserId,
                        preferenceData.Preferences);
                }
            }
        }

        private async Task ApplySystemUpdateAsync(ContextSession context, ContextUpdate update)
        {
            if (update.Data is SystemUpdateData systemData)
            {
                if (systemData.DeviceInfo != null)
                {
                    context.SystemContext.DeviceInfo = systemData.DeviceInfo;
                }

                if (systemData.Location != null)
                {
                    context.SystemContext.Location = systemData.Location;
                }

                if (!string.IsNullOrEmpty(systemData.Language))
                {
                    context.SystemContext.Language = systemData.Language;
                }

                if (!string.IsNullOrEmpty(systemData.TimeZone))
                {
                    context.SystemContext.TimeZone = systemData.TimeZone;
                }
            }
        }

        private async Task ApplyEmotionalUpdateAsync(ContextSession context, ContextUpdate update)
        {
            if (update.Data is EmotionalUpdateData emotionalData)
            {
                context.EmotionalState.CurrentEmotion = emotionalData.Emotion;
                context.EmotionalState.StressLevel = emotionalData.StressLevel;
                context.EmotionalState.EngagementLevel = emotionalData.EngagementLevel;

                // Record emotional snapshot;
                context.EmotionalState.EmotionalHistory.Add(new EmotionalSnapshot;
                {
                    Timestamp = DateTime.UtcNow,
                    Emotion = emotionalData.Emotion,
                    StressLevel = emotionalData.StressLevel,
                    Source = emotionalData.Source;
                });

                // Trim history if needed;
                if (context.EmotionalState.EmotionalHistory.Count > 100)
                {
                    context.EmotionalState.EmotionalHistory = context.EmotionalState.EmotionalHistory;
                        .Skip(context.EmotionalState.EmotionalHistory.Count - 100)
                        .ToList();
                }
            }
        }

        private async Task ApplyCustomPropertyUpdateAsync(ContextSession context, ContextUpdate update)
        {
            if (update.Data is CustomPropertyUpdateData propertyData)
            {
                foreach (var property in propertyData.Properties)
                {
                    if (property.Value == null)
                    {
                        context.Properties.TryRemove(property.Key, out _);
                    }
                    else;
                    {
                        context.Properties[property.Key] = property.Value;
                    }
                }
            }
        }

        private async Task OnContextChangedAsync(ContextSession context, ContextUpdate update)
        {
            context.IsDirty = true;

            // Auto-save if configured;
            if (context.Options.AutoSave && context.IsDirty)
            {
                var timeSinceLastSave = DateTime.UtcNow - (context.LastSaved ?? DateTime.MinValue);
                if (timeSinceLastSave > TimeSpan.FromSeconds(context.Options.AutoSaveIntervalSeconds))
                {
                    await SaveContextAsync(context.SessionId);
                }
            }

            // Notify subscribers if any;
            // This could be extended with an event system;
        }

        private async Task HandleExpiredContextAsync(ContextSession context)
        {
            _logger.LogInformation("Handling expired context {ContextId}", context.Id);

            // Save before expiration;
            if (context.IsDirty)
            {
                await SaveContextAsync(context.SessionId);
            }

            // Remove from active contexts;
            _activeContexts.TryRemove(context.SessionId, out _);

            // Dispose resources;
            context.Dispose();
        }

        private async Task<IEnumerable<ContextualSuggestion>> GetConversationSuggestionsAsync(ContextSession context)
        {
            var suggestions = new List<ContextualSuggestion>();

            // Suggest based on recent topics;
            if (context.CurrentTopics.Any())
            {
                var recentTopic = context.CurrentTopics.Last();
                suggestions.Add(new ContextualSuggestion;
                {
                    Type = SuggestionType.Conversation,
                    Title = $"Continue discussing {recentTopic}",
                    Description = "Based on recent conversation topic",
                    Action = $"ask about {recentTopic}",
                    RelevanceScore = 0.8f;
                });
            }

            // Suggest based on time of day;
            var hour = DateTime.UtcNow.Hour;
            if (hour >= 6 && hour < 12)
            {
                suggestions.Add(new ContextualSuggestion;
                {
                    Type = SuggestionType.Conversation,
                    Title = "Morning planning",
                    Description = "Start your day with planning",
                    Action = "plan my day",
                    RelevanceScore = 0.6f;
                });
            }

            return suggestions;
        }

        private async Task<IEnumerable<ContextualSuggestion>> GetTaskSuggestionsAsync(ContextSession context)
        {
            var suggestions = new List<ContextualSuggestion>();

            // Suggest next steps for pending tasks;
            foreach (var task in context.PendingTasks.Where(t => t.Status == TaskStatus.InProgress))
            {
                suggestions.Add(new ContextualSuggestion;
                {
                    Type = SuggestionType.Task,
                    Title = $"Continue: {task.Name}",
                    Description = $"Progress: {task.Progress:P0}",
                    Action = $"work on {task.Id}",
                    RelevanceScore = 0.9f - (task.Progress * 0.3f) // Higher relevance for less complete tasks;
                });
            }

            return suggestions;
        }

        private async Task<IEnumerable<ContextualSuggestion>> GetPreferenceSuggestionsAsync(ContextSession context)
        {
            // Analyze user preferences and suggest optimizations;
            var suggestions = new List<ContextualSuggestion>();

            // Example: Suggest enabling features based on usage patterns;
            if (context.InteractionCount > 10 && !context.UserPreferences.GetPreference<bool>("notifications_enabled", false))
            {
                suggestions.Add(new ContextualSuggestion;
                {
                    Type = SuggestionType.Preference,
                    Title = "Enable notifications",
                    Description = "Get alerts for important updates",
                    Action = "enable notifications",
                    RelevanceScore = 0.7f;
                });
            }

            return suggestions;
        }

        private async Task<IEnumerable<ContextualSuggestion>> GetSystemSuggestionsAsync(ContextSession context)
        {
            var suggestions = new List<ContextualSuggestion>();

            // System optimization suggestions;
            if (context.Properties.TryGetValue("PerformanceMetrics", out var metricsObj) &&
                metricsObj is Dictionary<string, double> metrics)
            {
                if (metrics.TryGetValue("MemoryUsage", out var memoryUsage) && memoryUsage > 80)
                {
                    suggestions.Add(new ContextualSuggestion;
                    {
                        Type = SuggestionType.System,
                        Title = "High memory usage",
                        Description = "Consider closing unused applications",
                        Action = "optimize memory",
                        RelevanceScore = 0.9f;
                    });
                }
            }

            return suggestions;
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Represents a context session with all state information;
    /// </summary>
    public class ContextSession : IDisposable
    {
        public string Id { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? LastAccessed { get; set; }
        public DateTime? LastSaved { get; set; }
        public DateTime? LastRestored { get; set; }
        public ContextState State { get; set; }
        public int Version { get; set; }
        public bool IsDirty { get; set; }
        public bool IsMerged { get; set; }
        public List<string> SourceContexts { get; set; }

        public UserPreferences UserPreferences { get; set; }
        public List<string> CurrentTopics { get; set; }
        public Queue<ContextAction> RecentActions { get; set; }
        public List<ContextTask> PendingTasks { get; set; }
        public ConversationFlow ConversationFlow { get; set; }
        public SystemContext SystemContext { get; set; }
        public EmotionalContext EmotionalState { get; set; }
        public ConcurrentDictionary<string, object> Properties { get; set; }
        public ContextOptions Options { get; set; }

        public int InteractionCount { get; set; }

        public ContextSession Clone()
        {
            return new ContextSession;
            {
                Id = Guid.NewGuid().ToString(),
                SessionId = this.SessionId,
                UserId = this.UserId,
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                State = ContextState.Active,
                Version = 0,

                UserPreferences = this.UserPreferences?.Clone(),
                CurrentTopics = new List<string>(this.CurrentTopics ?? new List<string>()),
                RecentActions = new Queue<ContextAction>(this.RecentActions ?? new Queue<ContextAction>()),
                PendingTasks = new List<ContextTask>(this.PendingTasks?.Select(t => t.Clone()) ?? new List<ContextTask>()),
                ConversationFlow = this.ConversationFlow?.Clone(),
                SystemContext = this.SystemContext?.Clone(),
                EmotionalState = this.EmotionalState?.Clone(),
                Properties = new ConcurrentDictionary<string, object>(this.Properties ?? new ConcurrentDictionary<string, object>()),
                Options = this.Options?.Clone() ?? ContextOptions.Default,

                InteractionCount = 0;
            };
        }

        public void Dispose()
        {
            // Clean up resources if needed;
            Properties?.Clear();
            RecentActions?.Clear();
            PendingTasks?.Clear();
            CurrentTopics?.Clear();
            SourceContexts?.Clear();
        }
    }

    public enum ContextState;
    {
        Active,
        Inactive,
        Expired,
        Restored,
        Corrupted;
    }

    public enum ContextUpdateType;
    {
        ConversationUpdate,
        TaskUpdate,
        PreferenceUpdate,
        SystemUpdate,
        EmotionalUpdate,
        CustomPropertyUpdate;
    }

    public enum SuggestionType;
    {
        Conversation,
        Task,
        Preference,
        System;
    }

    public enum Emotion;
    {
        Neutral,
        Happy,
        Sad,
        Angry,
        Surprised,
        Fearful,
        Disgusted,
        Excited,
        Bored,
        Focused;
    }

    public enum TaskStatus;
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Cancelled;
    }

    public class ContextOptions;
    {
        public static ContextOptions Default => new ContextOptions;
        {
            MaxActionHistory = 50,
            MaxTopics = 10,
            MaxPendingTasks = 20,
            AutoSave = true,
            AutoSaveIntervalSeconds = 60,
            AutoCleanupTasks = true,
            EnableEmotionalTracking = true,
            EnableSystemContext = true;
        };

        public int MaxActionHistory { get; set; }
        public int MaxTopics { get; set; }
        public int MaxPendingTasks { get; set; }
        public bool AutoSave { get; set; }
        public int AutoSaveIntervalSeconds { get; set; }
        public bool AutoCleanupTasks { get; set; }
        public bool EnableEmotionalTracking { get; set; }
        public bool EnableSystemContext { get; set; }

        public ContextOptions Clone()
        {
            return new ContextOptions;
            {
                MaxActionHistory = this.MaxActionHistory,
                MaxTopics = this.MaxTopics,
                MaxPendingTasks = this.MaxPendingTasks,
                AutoSave = this.AutoSave,
                AutoSaveIntervalSeconds = this.AutoSaveIntervalSeconds,
                AutoCleanupTasks = this.AutoCleanupTasks,
                EnableEmotionalTracking = this.EnableEmotionalTracking,
                EnableSystemContext = this.EnableSystemContext;
            };
        }
    }

    public class ContextUpdate;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public ContextUpdateType Type { get; set; }
        public string Description { get; set; }
        public object Data { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Source { get; set; }
    }

    public class ContextAction;
    {
        public string Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public object Data { get; set; }
    }

    public class ContextTask;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public TaskStatus Status { get; set; }
        public float Progress { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUpdated { get; set; }
        public DateTime? DueDate { get; set; }
        public object Result { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();

        public ContextTask Clone()
        {
            return new ContextTask;
            {
                Id = this.Id,
                Name = this.Name,
                Description = this.Description,
                Status = this.Status,
                Progress = this.Progress,
                CreatedAt = this.CreatedAt,
                LastUpdated = this.LastUpdated,
                DueDate = this.DueDate,
                Result = this.Result,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    public class SystemContext;
    {
        public object DeviceInfo { get; set; }
        public object Location { get; set; }
        public string Language { get; set; }
        public string TimeZone { get; set; }

        public SystemContext Clone()
        {
            return new SystemContext;
            {
                DeviceInfo = this.DeviceInfo,
                Location = this.Location,
                Language = this.Language,
                TimeZone = this.TimeZone;
            };
        }
    }

    public class EmotionalContext;
    {
        public Emotion CurrentEmotion { get; set; }
        public List<EmotionalSnapshot> EmotionalHistory { get; set; }
        public float StressLevel { get; set; } // 0-1 scale;
        public float EngagementLevel { get; set; } // 0-1 scale;

        public EmotionalContext Clone()
        {
            return new EmotionalContext;
            {
                CurrentEmotion = this.CurrentEmotion,
                EmotionalHistory = new List<EmotionalSnapshot>(this.EmotionalHistory ?? new List<EmotionalSnapshot>()),
                StressLevel = this.StressLevel,
                EngagementLevel = this.EngagementLevel;
            };
        }
    }

    public class EmotionalSnapshot;
    {
        public DateTime Timestamp { get; set; }
        public Emotion Emotion { get; set; }
        public float StressLevel { get; set; }
        public string Source { get; set; }
    }

    public class ContextualSuggestion;
    {
        public SuggestionType Type { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Action { get; set; }
        public float RelevanceScore { get; set; } // 0-1;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class ContextValidationResult;
    {
        public bool IsValid { get; set; }
        public string ContextId { get; set; }
        public List<string> Errors { get; set; } = new();
        public DateTime ValidationTime { get; set; }
    }

    // Data transfer objects for updates;
    public class ConversationUpdateData;
    {
        public string Message { get; set; }
        public string Response { get; set; }
        public List<string> Topics { get; set; }
        public object FlowUpdate { get; set; }
        public bool SaveToHistory { get; set; } = true;
    }

    public class TaskUpdateData;
    {
        public string TaskId { get; set; }
        public TaskStatus Status { get; set; }
        public float Progress { get; set; }
        public object Result { get; set; }
        public ContextTask NewTask { get; set; }
    }

    public class PreferenceUpdateData;
    {
        public Dictionary<string, object> Preferences { get; set; }
        public bool SaveToProfile { get; set; } = true;
    }

    public class SystemUpdateData;
    {
        public object DeviceInfo { get; set; }
        public object Location { get; set; }
        public string Language { get; set; }
        public string TimeZone { get; set; }
    }

    public class EmotionalUpdateData;
    {
        public Emotion Emotion { get; set; }
        public float StressLevel { get; set; }
        public float EngagementLevel { get; set; }
        public string Source { get; set; }
    }

    public class CustomPropertyUpdateData;
    {
        public Dictionary<string, object> Properties { get; set; }
    }

    // Exception classes;
    public class ContextCreationException : Exception
    {
        public ContextCreationException(string message) : base(message) { }
        public ContextCreationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContextNotFoundException : Exception
    {
        public ContextNotFoundException(string message) : base(message) { }
    }

    public class ContextUpdateException : Exception
    {
        public ContextUpdateException(string message) : base(message) { }
        public ContextUpdateException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContextPersistenceException : Exception
    {
        public ContextPersistenceException(string message) : base(message) { }
        public ContextPersistenceException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContextRestorationException : Exception
    {
        public ContextRestorationException(string message) : base(message) { }
        public ContextRestorationException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContextClearException : Exception
    {
        public ContextClearException(string message) : base(message) { }
        public ContextClearException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContextMergeException : Exception
    {
        public ContextMergeException(string message) : base(message) { }
        public ContextMergeException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
