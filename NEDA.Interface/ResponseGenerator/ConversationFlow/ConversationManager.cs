using NEDA.Communication.DialogSystem.EventModels;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Messaging.EventBus;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.Communication.DialogSystem.ConversationManager;
{
    /// <summary>
    /// Manages conversation sessions, state, and flow between users and the AI system.
    /// Provides thread-safe conversation handling with session management and context tracking.
    /// </summary>
    public class ConversationManager : IConversationManager;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IExceptionHandler _exceptionHandler;

        private readonly ConcurrentDictionary<string, ConversationSession> _activeSessions;
        private readonly ConcurrentDictionary<string, ConversationContext> _contextStore;
        private readonly ConversationConfiguration _configuration;

        private bool _isInitialized;
        private readonly object _initLock = new object();

        /// <summary>
        /// Initializes a new instance of ConversationManager;
        /// </summary>
        public ConversationManager(
            ILogger logger,
            IEventBus eventBus,
            IExceptionHandler exceptionHandler,
            ConversationConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));

            _configuration = configuration ?? ConversationConfiguration.Default;
            _activeSessions = new ConcurrentDictionary<string, ConversationSession>();
            _contextStore = new ConcurrentDictionary<string, ConversationContext>();

            _logger.LogInformation("ConversationManager initialized with configuration: {Config}", _configuration);
        }

        /// <summary>
        /// Initializes the conversation manager and subscribes to required events;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            lock (_initLock)
            {
                if (_isInitialized) return;

                try
                {
                    _eventBus.Subscribe<SessionStartedEvent>(HandleSessionStarted);
                    _eventBus.Subscribe<SessionEndedEvent>(HandleSessionEnded);
                    _eventBus.Subscribe<UserMessageEvent>(HandleUserMessage);

                    _isInitialized = true;
                    _logger.LogInformation("ConversationManager initialization completed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize ConversationManager");
                    throw new ConversationManagerException("Initialization failed", ex);
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Starts a new conversation session for a user;
        /// </summary>
        /// <param name="userId">Unique identifier for the user</param>
        /// <param name="initialContext">Optional initial conversation context</param>
        /// <returns>Session ID for the created conversation</returns>
        public async Task<string> StartConversationAsync(string userId, ConversationContext initialContext = null)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                var sessionId = GenerateSessionId(userId);

                var session = new ConversationSession;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    StartTime = DateTime.UtcNow,
                    LastActivity = DateTime.UtcNow,
                    State = ConversationState.Active,
                    Messages = new List<ConversationMessage>(),
                    Context = initialContext ?? new ConversationContext()
                };

                if (!_activeSessions.TryAdd(sessionId, session))
                {
                    throw new ConversationManagerException($"Session {sessionId} already exists");
                }

                _contextStore[sessionId] = session.Context;

                await _eventBus.PublishAsync(new SessionStartedEvent;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    Timestamp = DateTime.UtcNow,
                    InitialContext = initialContext;
                });

                _logger.LogInformation("Started new conversation session {SessionId} for user {UserId}",
                    sessionId, userId);

                return sessionId;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex,
                    ErrorCodes.Conversation.StartFailed,
                    new { userId, initialContext });
                throw;
            }
        }

        /// <summary>
        /// Ends an active conversation session;
        /// </summary>
        /// <param name="sessionId">ID of the session to end</param>
        /// <param name="reason">Reason for ending the session</param>
        public async Task EndConversationAsync(string sessionId, SessionEndReason reason = SessionEndReason.UserInitiated)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            try
            {
                if (!_activeSessions.TryRemove(sessionId, out var session))
                {
                    _logger.LogWarning("Attempted to end non-existent session: {SessionId}", sessionId);
                    return;
                }

                session.State = ConversationState.Ended;
                session.EndTime = DateTime.UtcNow;
                session.EndReason = reason;

                _contextStore.TryRemove(sessionId, out _);

                await _eventBus.PublishAsync(new SessionEndedEvent;
                {
                    SessionId = sessionId,
                    UserId = session.UserId,
                    Timestamp = DateTime.UtcNow,
                    Reason = reason,
                    Duration = session.EndTime.Value - session.StartTime,
                    MessageCount = session.Messages.Count;
                });

                _logger.LogInformation("Ended conversation session {SessionId}. Reason: {Reason}, Duration: {Duration}, Messages: {MessageCount}",
                    sessionId, reason, session.EndTime.Value - session.StartTime, session.Messages.Count);
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex,
                    ErrorCodes.Conversation.EndFailed,
                    new { sessionId, reason });
                throw;
            }
        }

        /// <summary>
        /// Processes a user message in the specified conversation session;
        /// </summary>
        /// <param name="sessionId">ID of the conversation session</param>
        /// <param name="message">User message to process</param>
        /// <returns>Response message from the system</returns>
        public async Task<ConversationResponse> ProcessMessageAsync(string sessionId, UserMessage message)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    throw new ConversationManagerException($"Session {sessionId} not found");
                }

                if (session.State != ConversationState.Active)
                {
                    throw new ConversationManagerException($"Session {sessionId} is not active. State: {session.State}");
                }

                // Update session activity;
                session.LastActivity = DateTime.UtcNow;

                // Add user message to history;
                var userConversationMessage = new ConversationMessage;
                {
                    MessageId = Guid.NewGuid().ToString(),
                    SessionId = sessionId,
                    Sender = MessageSender.User,
                    Content = message.Content,
                    Timestamp = DateTime.UtcNow,
                    Metadata = message.Metadata;
                };

                session.Messages.Add(userConversationMessage);

                // Update conversation context;
                UpdateConversationContext(session, message);

                // Publish message event for other handlers;
                await _eventBus.PublishAsync(new UserMessageEvent;
                {
                    SessionId = sessionId,
                    UserId = session.UserId,
                    Message = message,
                    Timestamp = DateTime.UtcNow,
                    Context = session.Context;
                });

                // Check for inactivity timeout;
                await CheckInactivityAsync(session);

                // Generate response (in a real implementation, this would call AI engine)
                var response = await GenerateResponseAsync(session, message);

                // Add system response to history;
                var systemConversationMessage = new ConversationMessage;
                {
                    MessageId = Guid.NewGuid().ToString(),
                    SessionId = sessionId,
                    Sender = MessageSender.System,
                    Content = response.Content,
                    Timestamp = DateTime.UtcNow,
                    Metadata = response.Metadata;
                };

                session.Messages.Add(systemConversationMessage);

                _logger.LogDebug("Processed message in session {SessionId}. User: {UserMessage}, System: {SystemResponse}",
                    sessionId, message.Content, response.Content);

                return response;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex,
                    ErrorCodes.Conversation.ProcessMessageFailed,
                    new { sessionId, message });
                throw;
            }
        }

        /// <summary>
        /// Retrieves the conversation history for a session;
        /// </summary>
        /// <param name="sessionId">ID of the conversation session</param>
        /// <param name="maxMessages">Maximum number of messages to retrieve (0 for all)</param>
        /// <returns>List of conversation messages</returns>
        public async Task<IReadOnlyList<ConversationMessage>> GetConversationHistoryAsync(string sessionId, int maxMessages = 0)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    throw new ConversationManagerException($"Session {sessionId} not found");
                }

                var messages = session.Messages.AsReadOnly();

                if (maxMessages > 0 && messages.Count > maxMessages)
                {
                    messages = messages.Skip(messages.Count - maxMessages).ToList().AsReadOnly();
                }

                await Task.CompletedTask;
                return messages;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex,
                    ErrorCodes.Conversation.GetHistoryFailed,
                    new { sessionId, maxMessages });
                throw;
            }
        }

        /// <summary>
        /// Gets the current context for a conversation session;
        /// </summary>
        /// <param name="sessionId">ID of the conversation session</param>
        /// <returns>Current conversation context</returns>
        public async Task<ConversationContext> GetConversationContextAsync(string sessionId)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            try
            {
                if (_contextStore.TryGetValue(sessionId, out var context))
                {
                    await Task.CompletedTask;
                    return context;
                }

                throw new ConversationManagerException($"Context for session {sessionId} not found");
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex,
                    ErrorCodes.Conversation.GetContextFailed,
                    new { sessionId });
                throw;
            }
        }

        /// <summary>
        /// Updates the context for a conversation session;
        /// </summary>
        /// <param name="sessionId">ID of the conversation session</param>
        /// <param name="contextUpdate">Context update to apply</param>
        public async Task UpdateConversationContextAsync(string sessionId, Action<ConversationContext> contextUpdate)
        {
            ValidateInitialization();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (contextUpdate == null)
                throw new ArgumentNullException(nameof(contextUpdate));

            try
            {
                if (!_contextStore.TryGetValue(sessionId, out var context))
                {
                    throw new ConversationManagerException($"Context for session {sessionId} not found");
                }

                contextUpdate(context);
                _contextStore[sessionId] = context;

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex,
                    ErrorCodes.Conversation.UpdateContextFailed,
                    new { sessionId });
                throw;
            }
        }

        /// <summary>
        /// Gets all active conversation sessions;
        /// </summary>
        /// <returns>Dictionary of active session IDs and their user IDs</returns>
        public async Task<IReadOnlyDictionary<string, string>> GetActiveSessionsAsync()
        {
            ValidateInitialization();

            try
            {
                var activeSessions = _activeSessions;
                    .Where(kv => kv.Value.State == ConversationState.Active)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.UserId)
                    .AsReadOnly();

                await Task.CompletedTask;
                return activeSessions;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex, ErrorCodes.Conversation.GetActiveSessionsFailed);
                throw;
            }
        }

        /// <summary>
        /// Cleans up inactive or expired sessions;
        /// </summary>
        public async Task CleanupInactiveSessionsAsync()
        {
            ValidateInitialization();

            try
            {
                var now = DateTime.UtcNow;
                var sessionsToRemove = new List<string>();

                foreach (var session in _activeSessions.Values)
                {
                    if (session.State == ConversationState.Active)
                    {
                        var inactivityPeriod = now - session.LastActivity;
                        if (inactivityPeriod > _configuration.SessionTimeout)
                        {
                            sessionsToRemove.Add(session.SessionId);
                        }
                    }
                    else if (session.State == ConversationState.Ended)
                    {
                        sessionsToRemove.Add(session.SessionId);
                    }
                }

                foreach (var sessionId in sessionsToRemove)
                {
                    await EndConversationAsync(sessionId, SessionEndReason.Timeout);
                }

                _logger.LogInformation("Cleaned up {Count} inactive sessions", sessionsToRemove.Count);
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex, ErrorCodes.Conversation.CleanupFailed);
                throw;
            }
        }

        #region Private Methods;

        private void ValidateInitialization()
        {
            if (!_isInitialized)
                throw new ConversationManagerException("ConversationManager is not initialized. Call InitializeAsync first.");
        }

        private string GenerateSessionId(string userId)
        {
            var timestamp = DateTime.UtcNow.Ticks;
            var random = new Random().Next(1000, 9999);
            return $"{userId}_{timestamp}_{random}";
        }

        private void UpdateConversationContext(ConversationSession session, UserMessage message)
        {
            session.Context.LastUserMessage = message.Content;
            session.Context.LastMessageTime = DateTime.UtcNow;
            session.Context.MessageCount++;

            // Extract entities or topics from message (simplified - in real implementation would use NLP)
            if (message.Metadata != null && message.Metadata.ContainsKey("Topics"))
            {
                var topics = message.Metadata["Topics"] as List<string> ?? new List<string>();
                session.Context.RecentTopics.AddRange(topics);

                // Keep only last N topics;
                if (session.Context.RecentTopics.Count > _configuration.MaxRecentTopics)
                {
                    session.Context.RecentTopics = session.Context.RecentTopics;
                        .Skip(session.Context.RecentTopics.Count - _configuration.MaxRecentTopics)
                        .ToList();
                }
            }
        }

        private async Task<ConversationResponse> GenerateResponseAsync(ConversationSession session, UserMessage message)
        {
            // In a real implementation, this would call the AI Engine or Dialog Manager;
            // This is a placeholder that returns a simple response;

            await Task.Delay(50); // Simulate processing time;

            return new ConversationResponse;
            {
                Content = $"I received your message: '{message.Content}'. This is session {session.SessionId}.",
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    { "ResponseType", "Acknowledgment" },
                    { "ProcessingTimeMs", 50 },
                    { "SessionMessageCount", session.Messages.Count }
                }
            };
        }

        private async Task CheckInactivityAsync(ConversationSession session)
        {
            var inactivityPeriod = DateTime.UtcNow - session.LastActivity;

            if (inactivityPeriod > _configuration.SessionTimeout)
            {
                _logger.LogWarning("Session {SessionId} has been inactive for {InactivityPeriod}",
                    session.SessionId, inactivityPeriod);

                await EndConversationAsync(session.SessionId, SessionEndReason.Timeout);
            }
        }

        private async Task HandleSessionStarted(SessionStartedEvent @event)
        {
            try
            {
                _logger.LogDebug("Session started event received: {@Event}", @event);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling SessionStartedEvent");
            }
        }

        private async Task HandleSessionEnded(SessionEndedEvent @event)
        {
            try
            {
                _logger.LogDebug("Session ended event received: {@Event}", @event);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling SessionEndedEvent");
            }
        }

        private async Task HandleUserMessage(UserMessageEvent @event)
        {
            try
            {
                _logger.LogDebug("User message event received: {@Event}", @event);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling UserMessageEvent");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup active sessions;
                    foreach (var sessionId in _activeSessions.Keys.ToList())
                    {
                        EndConversationAsync(sessionId, SessionEndReason.SystemShutdown).Wait();
                    }

                    _activeSessions.Clear();
                    _contextStore.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ConversationManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    /// <summary>
    /// Interface for ConversationManager to support dependency injection and testing;
    /// </summary>
    public interface IConversationManager : IDisposable
    {
        Task InitializeAsync();
        Task<string> StartConversationAsync(string userId, ConversationContext initialContext = null);
        Task EndConversationAsync(string sessionId, SessionEndReason reason = SessionEndReason.UserInitiated);
        Task<ConversationResponse> ProcessMessageAsync(string sessionId, UserMessage message);
        Task<IReadOnlyList<ConversationMessage>> GetConversationHistoryAsync(string sessionId, int maxMessages = 0);
        Task<ConversationContext> GetConversationContextAsync(string sessionId);
        Task UpdateConversationContextAsync(string sessionId, Action<ConversationContext> contextUpdate);
        Task<IReadOnlyDictionary<string, string>> GetActiveSessionsAsync();
        Task CleanupInactiveSessionsAsync();
    }

    /// <summary>
    /// Represents a conversation session;
    /// </summary>
    public class ConversationSession;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastActivity { get; set; }
        public ConversationState State { get; set; }
        public SessionEndReason? EndReason { get; set; }
        public List<ConversationMessage> Messages { get; set; }
        public ConversationContext Context { get; set; }
    }

    /// <summary>
    /// Represents a message in a conversation;
    /// </summary>
    public class ConversationMessage;
    {
        public string MessageId { get; set; }
        public string SessionId { get; set; }
        public MessageSender Sender { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// User message model;
    /// </summary>
    public class UserMessage;
    {
        public string Content { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// System response model;
    /// </summary>
    public class ConversationResponse;
    {
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Conversation context containing state and metadata;
    /// </summary>
    public class ConversationContext;
    {
        public string LastUserMessage { get; set; }
        public DateTime LastMessageTime { get; set; }
        public int MessageCount { get; set; }
        public List<string> RecentTopics { get; set; } = new List<string>();
        public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Conversation configuration;
    /// </summary>
    public class ConversationConfiguration;
    {
        public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public int MaxRecentTopics { get; set; } = 10;
        public int MaxMessagesPerSession { get; set; } = 1000;

        public static ConversationConfiguration Default => new ConversationConfiguration();
    }

    /// <summary>
    /// Conversation states;
    /// </summary>
    public enum ConversationState;
    {
        Active,
        Paused,
        Ended;
    }

    /// <summary>
    /// Message sender types;
    /// </summary>
    public enum MessageSender;
    {
        User,
        System;
    }

    /// <summary>
    /// Reasons for session termination;
    /// </summary>
    public enum SessionEndReason;
    {
        UserInitiated,
        SystemShutdown,
        Timeout,
        Error,
        Completed;
    }

    /// <summary>
    /// Custom exception for ConversationManager errors;
    /// </summary>
    public class ConversationManagerException : Exception
    {
        public ConversationManagerException(string message) : base(message) { }
        public ConversationManagerException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Error codes for conversation management;
    /// </summary>
    public static class ErrorCodes;
    {
        public static class Conversation;
        {
            public const string StartFailed = "CONV_001";
            public const string EndFailed = "CONV_002";
            public const string ProcessMessageFailed = "CONV_003";
            public const string GetHistoryFailed = "CONV_004";
            public const string GetContextFailed = "CONV_005";
            public const string UpdateContextFailed = "CONV_006";
            public const string GetActiveSessionsFailed = "CONV_007";
            public const string CleanupFailed = "CONV_008";
        }
    }
}
