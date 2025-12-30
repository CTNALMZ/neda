using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.ExceptionHandling;
using NEDA.Interface;

namespace NEDA.Brain.IntentRecognition.ContextBuilder;
{
    /// <summary>
    /// Represents the context of a user session including conversation history,
    /// user preferences, and current interaction state.
    /// </summary>
    public class SessionContext : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private bool _disposed = false;

        // Core session properties;
        public Guid SessionId { get; private set; }
        public string UserId { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime LastActivity { get; private set; }
        public SessionStatus Status { get; private set; }

        // Context data;
        private Dictionary<string, object> _contextData;
        private List<ConversationTurn> _conversationHistory;
        private Stack<ContextFrame> _contextStack;
        private Dictionary<string, List<object>> _temporaryMemory;

        // Preferences and state;
        public UserPreferences UserPreferences { get; private set; }
        public InteractionState CurrentState { get; private set; }
        public DeviceContext DeviceContext { get; set; }
        public EnvironmentContext EnvironmentContext { get; set; }

        // Performance and limits;
        private readonly int _maxHistorySize = 100;
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
        private readonly object _syncLock = new object();

        /// <summary>
        /// Initializes a new instance of SessionContext;
        /// </summary>
        public SessionContext(string userId, ILogger logger, ISecurityManager securityManager)
        {
            ValidateParameters(userId, logger, securityManager);

            SessionId = Guid.NewGuid();
            UserId = userId;
            CreatedAt = DateTime.UtcNow;
            LastActivity = DateTime.UtcNow;
            Status = SessionStatus.Active;

            _logger = logger;
            _securityManager = securityManager;

            InitializeContext();

            _logger.LogInformation($"Session {SessionId} created for user {UserId}");
        }

        private void ValidateParameters(string userId, ILogger logger, ISecurityManager securityManager)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (securityManager == null)
                throw new ArgumentNullException(nameof(securityManager));
        }

        private void InitializeContext()
        {
            _contextData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _conversationHistory = new List<ConversationTurn>();
            _contextStack = new Stack<ContextFrame>();
            _temporaryMemory = new Dictionary<string, List<object>>();

            UserPreferences = new UserPreferences();
            CurrentState = new InteractionState();
            DeviceContext = new DeviceContext();
            EnvironmentContext = new EnvironmentContext();

            // Initialize with default context;
            PushContextFrame("Initial");
        }

        /// <summary>
        /// Updates the session activity timestamp and validates session status;
        /// </summary>
        public void UpdateActivity()
        {
            lock (_syncLock)
            {
                LastActivity = DateTime.UtcNow;

                // Check for timeout;
                if (DateTime.UtcNow - LastActivity > _sessionTimeout)
                {
                    Status = SessionStatus.Expired;
                    _logger.LogWarning($"Session {SessionId} expired due to inactivity");
                }
            }
        }

        /// <summary>
        /// Adds a conversation turn to the history;
        /// </summary>
        public void AddConversationTurn(ConversationTurn turn)
        {
            if (turn == null)
                throw new ArgumentNullException(nameof(turn));

            lock (_syncLock)
            {
                UpdateActivity();

                _conversationHistory.Add(turn);

                // Enforce maximum history size;
                if (_conversationHistory.Count > _maxHistorySize)
                {
                    _conversationHistory.RemoveAt(0);
                }

                // Update context with latest conversation;
                UpdateContextFromConversation(turn);
            }
        }

        /// <summary>
        /// Gets the conversation history within the specified range;
        /// </summary>
        public List<ConversationTurn> GetConversationHistory(int maxTurns = 10)
        {
            lock (_syncLock)
            {
                var startIndex = Math.Max(0, _conversationHistory.Count - maxTurns);
                var count = Math.Min(maxTurns, _conversationHistory.Count - startIndex);

                return _conversationHistory;
                    .Skip(startIndex)
                    .Take(count)
                    .ToList();
            }
        }

        /// <summary>
        /// Sets a value in the context data;
        /// </summary>
        public void SetContextValue(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            lock (_syncLock)
            {
                UpdateActivity();

                if (_contextData.ContainsKey(key))
                {
                    _contextData[key] = value;
                }
                else;
                {
                    _contextData.Add(key, value);
                }

                _logger.LogDebug($"Context updated: {key} = {value}");
            }
        }

        /// <summary>
        /// Gets a value from the context data;
        /// </summary>
        public T GetContextValue<T>(string key, T defaultValue = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            lock (_syncLock)
            {
                if (_contextData.TryGetValue(key, out var value))
                {
                    try
                    {
                        return (T)value;
                    }
                    catch (InvalidCastException)
                    {
                        _logger.LogWarning($"Type mismatch for context key {key}. Expected {typeof(T)}, got {value?.GetType()}");
                        return defaultValue;
                    }
                }

                return defaultValue;
            }
        }

        /// <summary>
        /// Checks if a context key exists;
        /// </summary>
        public bool HasContextValue(string key)
        {
            lock (_syncLock)
            {
                return _contextData.ContainsKey(key);
            }
        }

        /// <summary>
        /// Removes a value from the context data;
        /// </summary>
        public bool RemoveContextValue(string key)
        {
            lock (_syncLock)
            {
                UpdateActivity();
                return _contextData.Remove(key);
            }
        }

        /// <summary>
        /// Pushes a new context frame onto the stack;
        /// </summary>
        public void PushContextFrame(string frameName)
        {
            lock (_syncLock)
            {
                UpdateActivity();

                var frame = new ContextFrame;
                {
                    FrameId = Guid.NewGuid(),
                    Name = frameName,
                    Timestamp = DateTime.UtcNow,
                    ContextSnapshot = new Dictionary<string, object>(_contextData)
                };

                _contextStack.Push(frame);
                _logger.LogDebug($"Context frame pushed: {frameName}");
            }
        }

        /// <summary>
        /// Pops the current context frame from the stack;
        /// </summary>
        public bool PopContextFrame()
        {
            lock (_syncLock)
            {
                if (_contextStack.Count <= 1) // Keep the initial frame;
                    return false;

                var frame = _contextStack.Pop();

                // Restore context from previous frame;
                if (_contextStack.Count > 0)
                {
                    var previousFrame = _contextStack.Peek();
                    _contextData = new Dictionary<string, object>(previousFrame.ContextSnapshot);
                }

                _logger.LogDebug($"Context frame popped: {frame.Name}");
                UpdateActivity();
                return true;
            }
        }

        /// <summary>
        /// Stores temporary data that expires with session;
        /// </summary>
        public void StoreTemporary(string category, object data)
        {
            lock (_syncLock)
            {
                UpdateActivity();

                if (!_temporaryMemory.ContainsKey(category))
                {
                    _temporaryMemory[category] = new List<object>();
                }

                _temporaryMemory[category].Add(data);

                // Limit temporary storage per category;
                if (_temporaryMemory[category].Count > 50)
                {
                    _temporaryMemory[category].RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Retrieves temporary data by category;
        /// </summary>
        public List<T> GetTemporary<T>(string category)
        {
            lock (_syncLock)
            {
                if (_temporaryMemory.TryGetValue(category, out var data))
                {
                    return data.OfType<T>().ToList();
                }

                return new List<T>();
            }
        }

        /// <summary>
        /// Clears temporary data for a category;
        /// </summary>
        public void ClearTemporary(string category)
        {
            lock (_syncLock)
            {
                _temporaryMemory.Remove(category);
            }
        }

        /// <summary>
        /// Updates user preferences;
        /// </summary>
        public void UpdatePreferences(Action<UserPreferences> updateAction)
        {
            lock (_syncLock)
            {
                UpdateActivity();
                updateAction?.Invoke(UserPreferences);
                _logger.LogInformation($"Preferences updated for session {SessionId}");
            }
        }

        /// <summary>
        /// Updates interaction state;
        /// </summary>
        public void UpdateState(Action<InteractionState> updateAction)
        {
            lock (_syncLock)
            {
                UpdateActivity();
                updateAction?.Invoke(CurrentState);
            }
        }

        /// <summary>
        /// Serializes the session context for persistence;
        /// </summary>
        public string Serialize()
        {
            lock (_syncLock)
            {
                var sessionData = new SessionData;
                {
                    SessionId = SessionId,
                    UserId = UserId,
                    CreatedAt = CreatedAt,
                    LastActivity = LastActivity,
                    Status = Status,
                    ContextData = _contextData,
                    ConversationHistory = _conversationHistory,
                    UserPreferences = UserPreferences,
                    CurrentState = CurrentState;
                };

                return JsonSerializer.Serialize(sessionData, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                });
            }
        }

        /// <summary>
        /// Deserializes session context from JSON;
        /// </summary>
        public static SessionContext Deserialize(string json, ILogger logger, ISecurityManager securityManager)
        {
            try
            {
                var sessionData = JsonSerializer.Deserialize<SessionData>(json);

                var session = new SessionContext(sessionData.UserId, logger, securityManager)
                {
                    SessionId = sessionData.SessionId,
                    CreatedAt = sessionData.CreatedAt,
                    LastActivity = sessionData.LastActivity,
                    Status = sessionData.Status,
                    UserPreferences = sessionData.UserPreferences,
                    CurrentState = sessionData.CurrentState;
                };

                // Restore context data;
                foreach (var kvp in sessionData.ContextData)
                {
                    session._contextData[kvp.Key] = kvp.Value;
                }

                // Restore conversation history;
                session._conversationHistory = sessionData.ConversationHistory;

                logger.LogInformation($"Session {sessionData.SessionId} deserialized successfully");
                return session;
            }
            catch (JsonException ex)
            {
                logger.LogError($"Failed to deserialize session: {ex.Message}");
                throw new SessionContextException("Failed to deserialize session context", ex);
            }
        }

        /// <summary>
        /// Validates session security and permissions;
        /// </summary>
        public bool ValidateSecurity(string requiredPermission)
        {
            try
            {
                return _securityManager.HasPermission(UserId, requiredPermission) &&
                       Status == SessionStatus.Active;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Security validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Cleans up expired temporary data;
        /// </summary>
        public void CleanupExpiredData()
        {
            lock (_syncLock)
            {
                // Implement cleanup logic for expired data;
                // This could include removing old temporary entries,
                // compressing history, etc.

                var cutoffTime = DateTime.UtcNow.AddHours(-1);
                var expiredCategories = new List<string>();

                foreach (var category in _temporaryMemory.Keys)
                {
                    // Example cleanup logic - adjust based on your needs;
                    if (_temporaryMemory[category].Count > 100)
                    {
                        _temporaryMemory[category] = _temporaryMemory[category]
                            .TakeLast(50)
                            .ToList();
                    }
                }

                _logger.LogDebug($"Expired data cleaned up for session {SessionId}");
            }
        }

        private void UpdateContextFromConversation(ConversationTurn turn)
        {
            // Extract entities and update context;
            if (turn.Entities != null)
            {
                foreach (var entity in turn.Entities)
                {
                    SetContextValue($"Entity_{entity.Type}", entity.Value);
                }
            }

            // Update conversation context;
            SetContextValue("LastUserInput", turn.UserInput);
            SetContextValue("LastAIResponse", turn.AIResponse);
            SetContextValue("LastIntent", turn.Intent);

            // Update turn count;
            var turnCount = GetContextValue<int>("ConversationTurnCount", 0) + 1;
            SetContextValue("ConversationTurnCount", turnCount);
        }

        /// <summary>
        /// Gets session metadata for monitoring;
        /// </summary>
        public SessionMetadata GetMetadata()
        {
            lock (_syncLock)
            {
                return new SessionMetadata;
                {
                    SessionId = SessionId,
                    UserId = UserId,
                    Duration = DateTime.UtcNow - CreatedAt,
                    TurnCount = _conversationHistory.Count,
                    ContextSize = _contextData.Count,
                    MemoryUsage = _temporaryMemory.Sum(x => x.Value.Count),
                    Status = Status;
                };
            }
        }

        /// <summary>
        /// Resets the session context while maintaining the session ID;
        /// </summary>
        public void Reset()
        {
            lock (_syncLock)
            {
                _contextData.Clear();
                _conversationHistory.Clear();
                _contextStack.Clear();
                _temporaryMemory.Clear();

                InitializeContext();

                Status = SessionStatus.Active;
                LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Session {SessionId} reset");
            }
        }

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _contextData?.Clear();
                    _conversationHistory?.Clear();
                    _contextStack?.Clear();
                    _temporaryMemory?.Clear();

                    Status = SessionStatus.Terminated;
                    _logger.LogInformation($"Session {SessionId} disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SessionContext()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Represents a conversation turn with user input and AI response;
    /// </summary>
    public class ConversationTurn;
    {
        public DateTime Timestamp { get; set; }
        public string UserInput { get; set; }
        public string AIResponse { get; set; }
        public string Intent { get; set; }
        public double Confidence { get; set; }
        public List<ContextEntity> Entities { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public ConversationTurn()
        {
            Timestamp = DateTime.UtcNow;
            Entities = new List<ContextEntity>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Represents a context entity extracted from conversation;
    /// </summary>
    public class ContextEntity;
    {
        public string Type { get; set; }
        public string Value { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Attributes { get; set; }

        public ContextEntity()
        {
            Attributes = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Represents a frame in the context stack;
    /// </summary>
    public class ContextFrame;
    {
        public Guid FrameId { get; set; }
        public string Name { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> ContextSnapshot { get; set; }

        public ContextFrame()
        {
            ContextSnapshot = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// User preferences for the session;
    /// </summary>
    public class UserPreferences;
    {
        public string Language { get; set; } = "en-US";
        public string VoiceGender { get; set; } = "Neutral";
        public int ResponseSpeed { get; set; } = 1; // 1-5 scale;
        public bool DetailedResponses { get; set; } = true;
        public List<string> PreferredTopics { get; set; }
        public Dictionary<string, object> CustomPreferences { get; set; }

        public UserPreferences()
        {
            PreferredTopics = new List<string>();
            CustomPreferences = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Current interaction state;
    /// </summary>
    public class InteractionState;
    {
        public bool IsProcessing { get; set; }
        public string CurrentTask { get; set; }
        public DateTime TaskStartTime { get; set; }
        public List<string> ActiveCommands { get; set; }
        public Dictionary<string, object> TaskState { get; set; }

        public InteractionState()
        {
            ActiveCommands = new List<string>();
            TaskState = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Device context information;
    /// </summary>
    public class DeviceContext;
    {
        public string DeviceType { get; set; }
        public string OperatingSystem { get; set; }
        public string ScreenResolution { get; set; }
        public List<string> InputMethods { get; set; }
        public Dictionary<string, object> DeviceCapabilities { get; set; }

        public DeviceContext()
        {
            InputMethods = new List<string>();
            DeviceCapabilities = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Environment context information;
    /// </summary>
    public class EnvironmentContext;
    {
        public string Location { get; set; }
        public string TimeZone { get; set; }
        public DateTime LocalTime { get; set; }
        public string AmbientNoiseLevel { get; set; } // Low, Medium, High;
        public Dictionary<string, object> EnvironmentalFactors { get; set; }

        public EnvironmentContext()
        {
            LocalTime = DateTime.Now;
            EnvironmentalFactors = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Session status enumeration;
    /// </summary>
    public enum SessionStatus;
    {
        Active,
        Inactive,
        Expired,
        Terminated;
    }

    /// <summary>
    /// Session metadata for monitoring;
    /// </summary>
    public class SessionMetadata;
    {
        public Guid SessionId { get; set; }
        public string UserId { get; set; }
        public TimeSpan Duration { get; set; }
        public int TurnCount { get; set; }
        public int ContextSize { get; set; }
        public int MemoryUsage { get; set; }
        public SessionStatus Status { get; set; }
    }

    /// <summary>
    /// Data transfer object for session serialization;
    /// </summary>
    internal class SessionData;
    {
        public Guid SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivity { get; set; }
        public SessionStatus Status { get; set; }
        public Dictionary<string, object> ContextData { get; set; }
        public List<ConversationTurn> ConversationHistory { get; set; }
        public UserPreferences UserPreferences { get; set; }
        public InteractionState CurrentState { get; set; }
    }

    /// <summary>
    /// Custom exception for session context errors;
    /// </summary>
    public class SessionContextException : Exception
    {
        public SessionContextException(string message) : base(message) { }
        public SessionContextException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
