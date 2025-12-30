using NEDA.Brain.IntentRecognition;
using NEDA.Brain.MemorySystem;
using NEDA.Communication.DialogSystem;
using NEDA.Communication.DialogSystem.ConversationManager;
using NEDA.Core.Common;
using NEDA.Core.Engine;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.ResponseGenerator.ConversationFlow;
{
    /// <summary>
    /// Dialog akışını yöneten, konuşma durum makinesini kontrol eden sınıf;
    /// </summary>
    public class DialogFlow : IDialogFlow, IDisposable;
    {
        private readonly IIntentRecognizer _intentRecognizer;
        private readonly IConversationManager _conversationManager;
        private readonly IMemoryRecall _memoryRecall;
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly DialogStateMachine _stateMachine;
        private readonly Dictionary<string, DialogContext> _activeSessions;
        private readonly SemaphoreSlim _sessionLock;
        private bool _disposed;

        /// <summary>
        /// DialogFlow constructor;
        /// </summary>
        public DialogFlow(
            IIntentRecognizer intentRecognizer,
            IConversationManager conversationManager,
            IMemoryRecall memoryRecall,
            ILogger logger,
            IEventBus eventBus)
        {
            _intentRecognizer = intentRecognizer ?? throw new ArgumentNullException(nameof(intentRecognizer));
            _conversationManager = conversationManager ?? throw new ArgumentNullException(nameof(conversationManager));
            _memoryRecall = memoryRecall ?? throw new ArgumentNullException(nameof(memoryRecall));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _activeSessions = new Dictionary<string, DialogContext>();
            _sessionLock = new SemaphoreSlim(1, 1);
            _stateMachine = new DialogStateMachine();

            InitializeEventHandlers();
            _logger.LogInformation("DialogFlow initialized successfully");
        }

        /// <summary>
        /// Yeni bir diyalog oturumu başlatır;
        /// </summary>
        public async Task<DialogSession> StartNewSessionAsync(string userId, SessionPreferences preferences = null)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            await _sessionLock.WaitAsync();
            try
            {
                var sessionId = GenerateSessionId(userId);

                if (_activeSessions.ContainsKey(sessionId))
                {
                    _logger.LogWarning($"Session already exists for user {userId}");
                    return await GetSessionAsync(sessionId);
                }

                var context = new DialogContext;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    StartTime = DateTime.UtcNow,
                    State = DialogState.Initializing,
                    Preferences = preferences ?? new SessionPreferences(),
                    ConversationHistory = new List<DialogTurn>(),
                    Metadata = new Dictionary<string, object>()
                };

                _activeSessions[sessionId] = context;

                // Durum makinesini başlat;
                await _stateMachine.TransitionToStateAsync(context, DialogState.Active);

                _logger.LogInformation($"New dialog session started: {sessionId} for user {userId}");

                await _eventBus.PublishAsync(new DialogSessionStartedEvent;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    Timestamp = DateTime.UtcNow;
                });

                return new DialogSession;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    StartTime = context.StartTime,
                    State = context.State;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start new session for user {userId}");
                throw new DialogFlowException($"Failed to start new session: {ex.Message}", ex);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Kullanıcı mesajını işler ve yanıt oluşturur;
        /// </summary>
        public async Task<DialogResponse> ProcessMessageAsync(
            string sessionId,
            UserMessage message,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var context))
                {
                    throw new SessionNotFoundException($"Session not found: {sessionId}");
                }

                // Oturum durumunu kontrol et;
                if (context.State == DialogState.Ended || context.State == DialogState.Error)
                {
                    throw new InvalidSessionStateException($"Session is in invalid state: {context.State}");
                }

                _logger.LogDebug($"Processing message for session {sessionId}: {message.Content.Substring(0, Math.Min(100, message.Content.Length))}...");

                // 1. Intent tanıma;
                var intentResult = await _intentRecognizer.RecognizeIntentAsync(message, context, cancellationToken);

                // 2. Bağlam oluşturma;
                await UpdateContextWithIntentAsync(context, intentResult, message);

                // 3. Konuşma yönetimi;
                var conversationResult = await _conversationManager.ProcessTurnAsync(
                    context,
                    message,
                    intentResult,
                    cancellationToken);

                // 4. Bellek güncelleme;
                await UpdateMemoryAsync(context, message, intentResult, conversationResult);

                // 5. Yanıt oluşturma;
                var response = await BuildResponseAsync(
                    context,
                    message,
                    intentResult,
                    conversationResult,
                    cancellationToken);

                // 6. Geçmişi güncelle;
                AddToConversationHistory(context, message, response, intentResult);

                // 7. Durum güncelleme;
                await UpdateDialogStateAsync(context, intentResult, conversationResult);

                _logger.LogInformation($"Message processed successfully for session {sessionId}");

                await _eventBus.PublishAsync(new MessageProcessedEvent;
                {
                    SessionId = sessionId,
                    MessageId = message.MessageId,
                    Intent = intentResult.Intent,
                    Timestamp = DateTime.UtcNow;
                });

                return response;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Message processing cancelled for session {sessionId}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing message for session {sessionId}");
                await HandleProcessingErrorAsync(sessionId, ex, message);
                throw new DialogProcessingException($"Failed to process message: {ex.Message}", ex, sessionId);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Oturum durumunu günceller;
        /// </summary>
        public async Task UpdateSessionStateAsync(string sessionId, DialogState newState, string reason = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _sessionLock.WaitAsync();
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var context))
                {
                    throw new SessionNotFoundException($"Session not found: {sessionId}");
                }

                var previousState = context.State;
                await _stateMachine.TransitionToStateAsync(context, newState);

                _logger.LogInformation($"Session {sessionId} state changed: {previousState} -> {newState}. Reason: {reason ?? "N/A"}");

                await _eventBus.PublishAsync(new SessionStateChangedEvent;
                {
                    SessionId = sessionId,
                    PreviousState = previousState,
                    NewState = newState,
                    Reason = reason,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update session state for {sessionId}");
                throw;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Aktif oturumu getirir;
        /// </summary>
        public async Task<DialogSession> GetSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _sessionLock.WaitAsync();
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var context))
                {
                    return null;
                }

                return new DialogSession;
                {
                    SessionId = context.SessionId,
                    UserId = context.UserId,
                    StartTime = context.StartTime,
                    State = context.State,
                    LastActivity = context.LastActivityTime,
                    TurnCount = context.ConversationHistory.Count;
                };
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Oturumu sonlandırır;
        /// </summary>
        public async Task EndSessionAsync(string sessionId, string reason = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _sessionLock.WaitAsync();
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var context))
                {
                    _logger.LogWarning($"Attempted to end non-existent session: {sessionId}");
                    return;
                }

                await _stateMachine.TransitionToStateAsync(context, DialogState.Ended);

                // Geçmişi temizle (opsiyonel - ayarlara bağlı)
                if (context.Preferences.ClearHistoryOnEnd)
                {
                    context.ConversationHistory.Clear();
                }

                _activeSessions.Remove(sessionId);

                _logger.LogInformation($"Session ended: {sessionId}. Reason: {reason ?? "Normal termination"}");

                await _eventBus.PublishAsync(new DialogSessionEndedEvent;
                {
                    SessionId = sessionId,
                    UserId = context.UserId,
                    Reason = reason,
                    Duration = DateTime.UtcNow - context.StartTime,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error ending session {sessionId}");
                throw;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Zaman aşımına uğramış oturumları temizler;
        /// </summary>
        public async Task CleanupInactiveSessionsAsync(TimeSpan timeout)
        {
            await _sessionLock.WaitAsync();
            try
            {
                var cutoffTime = DateTime.UtcNow - timeout;
                var sessionsToRemove = _activeSessions;
                    .Where(kvp => kvp.Value.LastActivityTime < cutoffTime && kvp.Value.State != DialogState.Active)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var sessionId in sessionsToRemove)
                {
                    _logger.LogInformation($"Cleaning up inactive session: {sessionId}");
                    _activeSessions.Remove(sessionId);

                    await _eventBus.PublishAsync(new SessionCleanedUpEvent;
                    {
                        SessionId = sessionId,
                        Reason = $"Inactive for more than {timeout.TotalMinutes} minutes",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                if (sessionsToRemove.Count > 0)
                {
                    _logger.LogInformation($"Cleaned up {sessionsToRemove.Count} inactive sessions");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up inactive sessions");
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Oturum bağlamını günceller;
        /// </summary>
        public async Task UpdateContextAsync(string sessionId, Action<DialogContext> updateAction)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            await _sessionLock.WaitAsync();
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var context))
                {
                    throw new SessionNotFoundException($"Session not found: {sessionId}");
                }

                updateAction(context);
                context.LastActivityTime = DateTime.UtcNow;

                _logger.LogDebug($"Context updated for session {sessionId}");
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private void InitializeEventHandlers()
        {
            _eventBus.Subscribe<EmergencyShutdownEvent>(async @event =>
            {
                _logger.LogWarning($"Emergency shutdown received, ending all sessions");
                await EndAllSessionsAsync("Emergency shutdown");
            });

            _eventBus.Subscribe<UserDisconnectedEvent>(async @event =>
            {
                var session = await GetSessionAsync(@event.SessionId);
                if (session != null)
                {
                    await EndSessionAsync(@event.SessionId, "User disconnected");
                }
            });
        }

        private async Task EndAllSessionsAsync(string reason)
        {
            await _sessionLock.WaitAsync();
            try
            {
                var sessionIds = _activeSessions.Keys.ToList();
                foreach (var sessionId in sessionIds)
                {
                    try
                    {
                        await EndSessionAsync(sessionId, reason);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error ending session {sessionId} during emergency shutdown");
                    }
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task UpdateContextWithIntentAsync(DialogContext context, IntentResult intentResult, UserMessage message)
        {
            context.LastIntent = intentResult.Intent;
            context.LastActivityTime = DateTime.UtcNow;
            context.Metadata["LastIntentConfidence"] = intentResult.Confidence;

            if (intentResult.Entities?.Any() == true)
            {
                context.Entities ??= new List<Entity>();
                context.Entities.AddRange(intentResult.Entities);
            }
        }

        private async Task UpdateMemoryAsync(
            DialogContext context,
            UserMessage message,
            IntentResult intentResult,
            ConversationResult conversationResult)
        {
            try
            {
                var memoryUpdate = new MemoryUpdate;
                {
                    SessionId = context.SessionId,
                    UserId = context.UserId,
                    Intent = intentResult.Intent,
                    Content = message.Content,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["ConversationTopic"] = conversationResult.Topic,
                        ["Sentiment"] = intentResult.Sentiment;
                    }
                };

                await _memoryRecall.StoreAsync(memoryUpdate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update memory for dialog");
                // Bellek güncelleme hatası diyalog akışını durdurmamalı;
            }
        }

        private async Task<DialogResponse> BuildResponseAsync(
            DialogContext context,
            UserMessage message,
            IntentResult intentResult,
            ConversationResult conversationResult,
            CancellationToken cancellationToken)
        {
            var responseBuilder = new DialogResponseBuilder();

            // Temel yanıt bileşenleri;
            responseBuilder;
                .WithSessionId(context.SessionId)
                .WithMessageId(Guid.NewGuid().ToString())
                .WithTimestamp(DateTime.UtcNow)
                .WithContent(conversationResult.ResponseText)
                .WithIntent(intentResult.Intent)
                .WithConfidence(intentResult.Confidence);

            // Bağlam ve meta veriler;
            if (conversationResult.SuggestedActions?.Any() == true)
            {
                responseBuilder.WithSuggestedActions(conversationResult.SuggestedActions);
            }

            if (conversationResult.RequiresFollowUp)
            {
                responseBuilder.WithFollowUpRequired(conversationResult.FollowUpContext);
            }

            // Duygu ve ton;
            responseBuilder;
                .WithSentiment(conversationResult.Sentiment)
                .WithTone(conversationResult.Tone);

            return responseBuilder.Build();
        }

        private void AddToConversationHistory(
            DialogContext context,
            UserMessage message,
            DialogResponse response,
            IntentResult intentResult)
        {
            var turn = new DialogTurn;
            {
                TurnId = Guid.NewGuid().ToString(),
                UserMessage = message,
                SystemResponse = response,
                Intent = intentResult.Intent,
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["Confidence"] = intentResult.Confidence,
                    ["ProcessingTimeMs"] = (DateTime.UtcNow - message.Timestamp).TotalMilliseconds;
                }
            };

            context.ConversationHistory.Add(turn);

            // Geçmiş boyutunu sınırla;
            if (context.ConversationHistory.Count > context.Preferences.MaxHistorySize)
            {
                context.ConversationHistory.RemoveAt(0);
            }
        }

        private async Task UpdateDialogStateAsync(
            DialogContext context,
            IntentResult intentResult,
            ConversationResult conversationResult)
        {
            // Intent'e ve konuşma sonucuna göre durum güncelle;
            if (intentResult.Intent == "goodbye" || conversationResult.ShouldEndConversation)
            {
                await _stateMachine.TransitionToStateAsync(context, DialogState.Ending);
            }
            else if (intentResult.RequiresConfirmation)
            {
                await _stateMachine.TransitionToStateAsync(context, DialogState.AwaitingConfirmation);
            }
            else;
            {
                await _stateMachine.TransitionToStateAsync(context, DialogState.Active);
            }
        }

        private async Task HandleProcessingErrorAsync(string sessionId, Exception exception, UserMessage message)
        {
            try
            {
                // Hata durumuna geç;
                if (_activeSessions.TryGetValue(sessionId, out var context))
                {
                    await _stateMachine.TransitionToStateAsync(context, DialogState.Error);
                    context.LastError = exception;
                }

                // Hata olayını yayınla;
                await _eventBus.PublishAsync(new DialogErrorEvent;
                {
                    SessionId = sessionId,
                    ErrorMessage = exception.Message,
                    ErrorType = exception.GetType().Name,
                    MessageContent = message?.Content,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling processing error");
            }
        }

        private string GenerateSessionId(string userId)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            return $"{userId}_{timestamp}_{random}";
        }

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
                    _sessionLock?.Dispose();
                    _eventBus?.Dispose();
                    _logger.LogInformation("DialogFlow disposed");
                }
                _disposed = true;
            }
        }

        /// <summary>
        /// Dialog durum makinesi;
        /// </summary>
        private class DialogStateMachine;
        {
            private static readonly Dictionary<DialogState, List<DialogState>> _validTransitions = new()
            {
                [DialogState.Initializing] = new() { DialogState.Active, DialogState.Error },
                [DialogState.Active] = new() { DialogState.AwaitingConfirmation, DialogState.Paused, DialogState.Ending, DialogState.Error },
                [DialogState.AwaitingConfirmation] = new() { DialogState.Active, DialogState.Error },
                [DialogState.Paused] = new() { DialogState.Active, DialogState.Ending, DialogState.Error },
                [DialogState.Ending] = new() { DialogState.Ended, DialogState.Error },
                [DialogState.Error] = new() { DialogState.Active, DialogState.Ended },
                [DialogState.Ended] = new() { }
            };

            public async Task TransitionToStateAsync(DialogContext context, DialogState newState)
            {
                var currentState = context.State;

                if (!IsValidTransition(currentState, newState))
                {
                    throw new InvalidStateTransitionException(
                        $"Invalid transition from {currentState} to {newState}");
                }

                // Çıkış işlemleri;
                await ExecuteExitActionsAsync(context, currentState);

                // Durum güncelleme;
                context.PreviousState = currentState;
                context.State = newState;
                context.StateChangedTime = DateTime.UtcNow;

                // Giriş işlemleri;
                await ExecuteEnterActionsAsync(context, newState);
            }

            private bool IsValidTransition(DialogState fromState, DialogState toState)
            {
                return _validTransitions.ContainsKey(fromState) &&
                       _validTransitions[fromState].Contains(toState);
            }

            private async Task ExecuteExitActionsAsync(DialogContext context, DialogState state)
            {
                // Duruma özel çıkış işlemleri;
                switch (state)
                {
                    case DialogState.Active:
                        context.Metadata["ActiveDuration"] = DateTime.UtcNow - context.StateChangedTime;
                        break;
                    case DialogState.AwaitingConfirmation:
                        // Zaman aşımı kontrolü;
                        break;
                }

                await Task.CompletedTask;
            }

            private async Task ExecuteEnterActionsAsync(DialogContext context, DialogState state)
            {
                // Duruma özel giriş işlemleri;
                switch (state)
                {
                    case DialogState.Error:
                        context.ErrorCount++;
                        break;
                    case DialogState.Ended:
                        context.EndTime = DateTime.UtcNow;
                        break;
                }

                await Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Dialog akışı interface'i;
    /// </summary>
    public interface IDialogFlow : IDisposable
    {
        Task<DialogSession> StartNewSessionAsync(string userId, SessionPreferences preferences = null);
        Task<DialogResponse> ProcessMessageAsync(string sessionId, UserMessage message, CancellationToken cancellationToken = default);
        Task UpdateSessionStateAsync(string sessionId, DialogState newState, string reason = null);
        Task<DialogSession> GetSessionAsync(string sessionId);
        Task EndSessionAsync(string sessionId, string reason = null);
        Task CleanupInactiveSessionsAsync(TimeSpan timeout);
        Task UpdateContextAsync(string sessionId, Action<DialogContext> updateAction);
    }

    /// <summary>
    /// Dialog durumları;
    /// </summary>
    public enum DialogState;
    {
        Initializing,
        Active,
        AwaitingConfirmation,
        Paused,
        Ending,
        Error,
        Ended;
    }

    /// <summary>
    /// Dialog oturumu;
    /// </summary>
    public class DialogSession;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? LastActivity { get; set; }
        public DialogState State { get; set; }
        public int TurnCount { get; set; }
    }

    /// <summary>
    /// Dialog bağlamı;
    /// </summary>
    public class DialogContext;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastActivityTime { get; set; }
        public DateTime StateChangedTime { get; set; }
        public DialogState State { get; set; }
        public DialogState PreviousState { get; set; }
        public string LastIntent { get; set; }
        public SessionPreferences Preferences { get; set; }
        public List<DialogTurn> ConversationHistory { get; set; }
        public List<Entity> Entities { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public int ErrorCount { get; set; }
        public Exception LastError { get; set; }
    }

    /// <summary>
    /// Oturum tercihleri;
    /// </summary>
    public class SessionPreferences;
    {
        public int MaxHistorySize { get; set; } = 50;
        public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public bool ClearHistoryOnEnd { get; set; } = false;
        public string PreferredLanguage { get; set; } = "en-US";
        public bool EnableProfanityFilter { get; set; } = true;
    }

    /// <summary>
    /// Dialog turu (bir kullanıcı mesajı ve sistem yanıtı)
    /// </summary>
    public class DialogTurn;
    {
        public string TurnId { get; set; }
        public UserMessage UserMessage { get; set; }
        public DialogResponse SystemResponse { get; set; }
        public string Intent { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Dialog yanıtı;
    /// </summary>
    public class DialogResponse;
    {
        public string ResponseId { get; set; }
        public string SessionId { get; set; }
        public string MessageId { get; set; }
        public string Content { get; set; }
        public string Intent { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
        public List<string> SuggestedActions { get; set; }
        public bool RequiresFollowUp { get; set; }
        public string FollowUpContext { get; set; }
        public string Sentiment { get; set; }
        public string Tone { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Dialog yanıt builder'ı;
    /// </summary>
    public class DialogResponseBuilder;
    {
        private readonly DialogResponse _response;

        public DialogResponseBuilder()
        {
            _response = new DialogResponse();
        }

        public DialogResponseBuilder WithSessionId(string sessionId)
        {
            _response.SessionId = sessionId;
            return this;
        }

        public DialogResponseBuilder WithMessageId(string messageId)
        {
            _response.MessageId = messageId;
            return this;
        }

        public DialogResponseBuilder WithTimestamp(DateTime timestamp)
        {
            _response.Timestamp = timestamp;
            return this;
        }

        public DialogResponseBuilder WithContent(string content)
        {
            _response.Content = content;
            return this;
        }

        public DialogResponseBuilder WithIntent(string intent)
        {
            _response.Intent = intent;
            return this;
        }

        public DialogResponseBuilder WithConfidence(double confidence)
        {
            _response.Confidence = confidence;
            return this;
        }

        public DialogResponseBuilder WithSuggestedActions(List<string> actions)
        {
            _response.SuggestedActions = actions;
            return this;
        }

        public DialogResponseBuilder WithFollowUpRequired(string context)
        {
            _response.RequiresFollowUp = true;
            _response.FollowUpContext = context;
            return this;
        }

        public DialogResponseBuilder WithSentiment(string sentiment)
        {
            _response.Sentiment = sentiment;
            return this;
        }

        public DialogResponseBuilder WithTone(string tone)
        {
            _response.Tone = tone;
            return this;
        }

        public DialogResponse Build()
        {
            _response.ResponseId = Guid.NewGuid().ToString();
            _response.Metadata ??= new Dictionary<string, object>();
            return _response;
        }
    }

    /// <summary>
    /// Özel exception'lar;
    /// </summary>
    public class DialogFlowException : Exception
    {
        public DialogFlowException(string message) : base(message) { }
        public DialogFlowException(string message, Exception inner) : base(message, inner) { }
    }

    public class DialogProcessingException : DialogFlowException;
    {
        public string SessionId { get; }

        public DialogProcessingException(string message, Exception inner, string sessionId)
            : base(message, inner)
        {
            SessionId = sessionId;
        }
    }

    public class SessionNotFoundException : DialogFlowException;
    {
        public SessionNotFoundException(string message) : base(message) { }
    }

    public class InvalidSessionStateException : DialogFlowException;
    {
        public InvalidSessionStateException(string message) : base(message) { }
    }

    public class InvalidStateTransitionException : DialogFlowException;
    {
        public InvalidStateTransitionException(string message) : base(message) { }
    }

    /// <summary>
    /// Event'ler;
    /// </summary>
    public class DialogSessionStartedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class DialogSessionEndedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Reason { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MessageProcessedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string MessageId { get; set; }
        public string Intent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SessionStateChangedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public DialogState PreviousState { get; set; }
        public DialogState NewState { get; set; }
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class DialogErrorEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorType { get; set; }
        public string MessageContent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SessionCleanedUpEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// External event'ler;
    /// </summary>
    public class EmergencyShutdownEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
    }

    public class UserDisconnectedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
