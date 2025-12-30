using NEDA.API.Versioning;
using NEDA.Brain.IntentRecognition;
using NEDA.Brain.IntentRecognition.CommandParser;
using NEDA.Brain.IntentRecognition.ContextBuilder;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.CharacterSystems.DialogueSystem.BranchingNarratives;
using NEDA.Communication.DialogSystem.ClarificationEngine;
using NEDA.Communication.DialogSystem.ConversationManager;
using NEDA.Communication.DialogSystem.HumorPersonality;
using NEDA.Communication.DialogSystem.TopicHandler;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.ContextKeeper;
using NEDA.Interface.ConversationHistory;
using NEDA.Interface.InteractionManager.ContextKeeper;
using NEDA.Interface.SessionHandler;
using NEDA.Services.Messaging.EventBus;
using NEDA.Services.Messaging.SignalRHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.Brain.IntentRecognition.ContextBuilder.HistoryTracker;
using static NEDA.Brain.IntentRecognition.IntentDetector;
using static NEDA.CharacterSystems.DialogueSystem.BranchingNarratives.NarrativeEngine;
using static NEDA.Communication.EmotionalIntelligence.EmotionRecognition.EmotionDetector;

namespace NEDA.Communication.DialogSystem.ConversationManager;
{
    /// <summary>
    /// Main controller for chat conversations, orchestrating all dialog components;
    /// Handles real-time chat sessions with multi-modal interaction support;
    /// </summary>
    public class ChatController : IChatController, IAsyncDisposable;
    {
        private readonly ILogger _logger;
        private readonly IConversationEngine _conversationEngine;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IIntentDetector _intentDetector;
        private readonly IAmbiguityResolver _ambiguityResolver;
        private readonly IEmotionDetector _emotionDetector;
        private readonly ITopicManager _topicManager;
        private readonly IPersonalityEngine _personalityEngine;
        private readonly IHistoryManager _historyManager;
        private readonly IContextManager _contextManager;
        private readonly ISessionManager _sessionManager;
        private readonly IEventBus _eventBus;
        private readonly IChatHub _chatHub;
        private readonly ChatControllerConfig _config;

        private readonly Dictionary<string, ChatSession> _activeSessions;
        private readonly SemaphoreSlim _sessionLock = new SemaphoreSlim(1, 1);
        private bool _isDisposed;

        /// <summary>
        /// Initializes a new instance of the ChatController;
        /// </summary>
        public ChatController(
            ILogger logger,
            IConversationEngine conversationEngine,
            ISemanticAnalyzer semanticAnalyzer,
            IIntentDetector intentDetector,
            IAmbiguityResolver ambiguityResolver,
            IEmotionDetector emotionDetector,
            ITopicManager topicManager,
            IPersonalityEngine personalityEngine,
            IHistoryManager historyManager,
            IContextManager contextManager,
            ISessionManager sessionManager,
            IEventBus eventBus,
            IChatHub chatHub,
            ChatControllerConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _conversationEngine = conversationEngine ?? throw new ArgumentNullException(nameof(conversationEngine));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _intentDetector = intentDetector ?? throw new ArgumentNullException(nameof(intentDetector));
            _ambiguityResolver = ambiguityResolver ?? throw new ArgumentNullException(nameof(ambiguityResolver));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _topicManager = topicManager ?? throw new ArgumentNullException(nameof(topicManager));
            _personalityEngine = personalityEngine ?? throw new ArgumentNullException(nameof(personalityEngine));
            _historyManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
            _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _chatHub = chatHub ?? throw new ArgumentNullException(nameof(chatHub));
            _config = config ?? ChatControllerConfig.Default;

            _activeSessions = new Dictionary<string, ChatSession>();

            InitializeEventSubscriptions();

            _logger.Info("ChatController initialized successfully", new;
            {
                MaxSessions = _config.MaxConcurrentSessions,
                SessionTimeout = _config.SessionTimeout,
                EnableEmotionDetection = _config.EnableEmotionDetection;
            });
        }

        /// <summary>
        /// Processes incoming chat message and generates response;
        /// </summary>
        public async Task<ChatResponse> ProcessMessageAsync(
            ChatMessage message,
            CancellationToken cancellationToken = default)
        {
            ValidateMessage(message);

            try
            {
                _logger.Debug($"Processing chat message from {message.UserId} in session {message.SessionId}");

                var session = await GetOrCreateSessionAsync(message.SessionId, message.UserId, cancellationToken);

                await UpdateSessionActivityAsync(session.SessionId, cancellationToken);

                var context = await BuildConversationContextAsync(session, message, cancellationToken);

                var processingResult = await ProcessMessagePipelineAsync(message, context, cancellationToken);

                var response = await GenerateResponseAsync(processingResult, session, cancellationToken);

                await FinalizeMessageProcessingAsync(session, message, response, cancellationToken);

                _logger.Info($"Chat message processed successfully", new;
                {
                    SessionId = message.SessionId,
                    MessageId = message.MessageId,
                    ResponseTime = response.ProcessingTime;
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to process chat message: {message.MessageId}", ex);
                return await GenerateErrorResponseAsync(message, ex, cancellationToken);
            }
        }

        /// <summary>
        /// Starts a new chat session;
        /// </summary>
        public async Task<ChatSession> StartSessionAsync(
            string userId,
            SessionParameters parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);

                if (_activeSessions.Count >= _config.MaxConcurrentSessions)
                {
                    await CleanupInactiveSessionsAsync(cancellationToken);

                    if (_activeSessions.Count >= _config.MaxConcurrentSessions)
                    {
                        throw new ChatSessionException(
                            $"Maximum concurrent sessions ({_config.MaxConcurrentSessions}) reached");
                    }
                }

                var sessionId = GenerateSessionId(userId);

                if (_activeSessions.ContainsKey(sessionId))
                {
                    await EndSessionAsync(sessionId, cancellationToken);
                }

                var session = await CreateNewSessionAsync(sessionId, userId, parameters, cancellationToken);

                _activeSessions[sessionId] = session;

                await _eventBus.PublishAsync(new SessionStartedEvent;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    Timestamp = DateTime.UtcNow,
                    Parameters = parameters;
                }, cancellationToken);

                _logger.Info($"New chat session started", new { SessionId = sessionId, UserId = userId });

                return session;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Ends an existing chat session;
        /// </summary>
        public async Task EndSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));

            try
            {
                await _sessionLock.WaitAsync(cancellationToken);

                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    await FinalizeSessionAsync(session, cancellationToken);

                    _activeSessions.Remove(sessionId);

                    await _eventBus.PublishAsync(new SessionEndedEvent;
                    {
                        SessionId = sessionId,
                        UserId = session.UserId,
                        Duration = DateTime.UtcNow - session.StartTime,
                        MessageCount = session.MessageCount,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.Info($"Chat session ended", new;
                    {
                        SessionId = sessionId,
                        Duration = DateTime.UtcNow - session.StartTime,
                        MessageCount = session.MessageCount;
                    });
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Gets session information;
        /// </summary>
        public async Task<ChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));

            await _sessionLock.WaitAsync(cancellationToken);
            try
            {
                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    if (session.LastActivity.Add(_config.SessionTimeout) < DateTime.UtcNow)
                    {
                        await EndSessionAsync(sessionId, cancellationToken);
                        return null;
                    }

                    return session;
                }

                return null;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Updates session parameters;
        /// </summary>
        public async Task UpdateSessionParametersAsync(
            string sessionId,
            SessionParameters parameters,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));

            var session = await GetSessionAsync(sessionId, cancellationToken);

            if (session == null)
                throw new ChatSessionException($"Session not found: {sessionId}");

            session.Parameters = MergeParameters(session.Parameters, parameters);
            session.LastModified = DateTime.UtcNow;

            await _contextManager.UpdateSessionContextAsync(sessionId, session.Parameters, cancellationToken);

            _logger.Debug($"Session parameters updated", new { SessionId = sessionId });
        }

        /// <summary>
        /// Gets conversation history for a session;
        /// </summary>
        public async Task<ConversationHistory> GetConversationHistoryAsync(
            string sessionId,
            int maxMessages = 50,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));

            return await _historyManager.GetSessionHistoryAsync(sessionId, maxMessages, cancellationToken);
        }

        /// <summary>
        /// Clears conversation history for a session;
        /// </summary>
        public async Task ClearConversationHistoryAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));

            await _historyManager.ClearSessionHistoryAsync(sessionId, cancellationToken);

            var session = await GetSessionAsync(sessionId, cancellationToken);
            if (session != null)
            {
                session.MessageCount = 0;
            }

            _logger.Info($"Conversation history cleared", new { SessionId = sessionId });
        }

        /// <summary>
        /// Sends typing indicator;
        /// </summary>
        public async Task SendTypingIndicatorAsync(string sessionId, bool isTyping, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(sessionId));

            var session = await GetSessionAsync(sessionId, cancellationToken);

            if (session == null)
                return;

            await _chatHub.SendTypingIndicatorAsync(sessionId, session.UserId, isTyping, cancellationToken);

            _logger.Debug($"Typing indicator sent", new { SessionId = sessionId, IsTyping = isTyping });
        }

        /// <summary>
        /// Disposes the controller and cleans up resources;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                await _sessionLock.WaitAsync();

                var cleanupTasks = _activeSessions.Keys;
                    .Select(sessionId => EndSessionAsync(sessionId, CancellationToken.None))
                    .ToArray();

                await Task.WhenAll(cleanupTasks);

                _sessionLock.Dispose();

                _logger.Info("ChatController disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("Error during ChatController disposal", ex);
            }
        }

        #region Private Methods;

        /// <summary>
        /// Validates incoming chat message;
        /// </summary>
        private void ValidateMessage(ChatMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (string.IsNullOrWhiteSpace(message.SessionId))
                throw new ArgumentException("Session ID cannot be empty", nameof(message.SessionId));

            if (string.IsNullOrWhiteSpace(message.UserId))
                throw new ArgumentException("User ID cannot be empty", nameof(message.UserId));

            if (string.IsNullOrWhiteSpace(message.Text) && message.Attachments?.Count == 0)
                throw new ArgumentException("Message must contain text or attachments", nameof(message.Text));

            if (message.Text?.Length > _config.MaxMessageLength)
                throw new ArgumentException($"Message exceeds maximum length of {_config.MaxMessageLength} characters", nameof(message.Text));
        }

        /// <summary>
        /// Gets or creates a chat session;
        /// </summary>
        private async Task<ChatSession> GetOrCreateSessionAsync(
            string sessionId,
            string userId,
            CancellationToken cancellationToken)
        {
            var session = await GetSessionAsync(sessionId, cancellationToken);

            if (session == null)
            {
                session = await StartSessionAsync(userId, new SessionParameters;
                {
                    AutoCreateSession = true,
                    Source = MessageSource.Chat;
                }, cancellationToken);
            }

            if (session.UserId != userId)
            {
                throw new ChatSessionException($"Session {sessionId} belongs to different user");
            }

            return session;
        }

        /// <summary>
        /// Updates session activity timestamp;
        /// </summary>
        private async Task UpdateSessionActivityAsync(string sessionId, CancellationToken cancellationToken)
        {
            var session = await GetSessionAsync(sessionId, cancellationToken);

            if (session != null)
            {
                session.LastActivity = DateTime.UtcNow;
                session.MessageCount++;
            }
        }

        /// <summary>
        /// Builds conversation context for message processing;
        /// </summary>
        private async Task<ConversationContext> BuildConversationContextAsync(
            ChatSession session,
            ChatMessage message,
            CancellationToken cancellationToken)
        {
            var history = await _historyManager.GetRecentHistoryAsync(session.SessionId, 10, cancellationToken);

            var context = await _contextManager.GetSessionContextAsync(session.SessionId, cancellationToken)
                ?? new ConversationContext();

            context.SessionId = session.SessionId;
            context.UserId = session.UserId;
            context.CurrentMessage = message.Text;
            context.MessageHistory = history.Messages;
            context.Topic = await _topicManager.GetCurrentTopicAsync(session.SessionId, cancellationToken);
            context.Parameters = session.Parameters;
            context.Timestamp = DateTime.UtcNow;

            if (_config.EnableEmotionDetection)
            {
                context.UserEmotion = await _emotionDetector.DetectEmotionAsync(message.Text, cancellationToken);
            }

            return context;
        }

        /// <summary>
        /// Processes message through the complete pipeline;
        /// </summary>
        private async Task<MessageProcessingResult> ProcessMessagePipelineAsync(
            ChatMessage message,
            ConversationContext context,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            // Step 1: Semantic Analysis;
            var semanticResult = await _semanticAnalyzer.AnalyzeAsync(message.Text, context, cancellationToken);

            // Step 2: Intent Detection;
            var intentResult = await _intentDetector.DetectIntentAsync(message.Text, context, cancellationToken);

            // Step 3: Ambiguity Resolution if needed;
            DetectedIntent resolvedIntent;
            List<ClarificationQuestion> clarificationQuestions = null;

            if (intentResult.Confidence < _config.ConfidenceThreshold)
            {
                var resolved = await _ambiguityResolver.ResolveAmbiguityAsync(message.Text, context, cancellationToken);
                resolvedIntent = resolved.Intent;
                clarificationQuestions = resolved.ClarificationQuestions;
            }
            else;
            {
                resolvedIntent = intentResult.PrimaryIntent;
            }

            // Step 4: Topic Management;
            var topicUpdate = await _topicManager.ProcessMessageAsync(message.Text, context, cancellationToken);

            // Step 5: Personality Adaptation;
            var personalityTraits = await _personalityEngine.AdaptPersonalityAsync(context, cancellationToken);

            var processingTime = DateTime.UtcNow - startTime;

            return new MessageProcessingResult;
            {
                OriginalMessage = message,
                SemanticAnalysis = semanticResult,
                DetectedIntent = resolvedIntent,
                Confidence = intentResult.Confidence,
                ClarificationQuestions = clarificationQuestions,
                TopicUpdate = topicUpdate,
                PersonalityTraits = personalityTraits,
                Context = context,
                ProcessingTime = processingTime,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Generates response based on processing result;
        /// </summary>
        private async Task<ChatResponse> GenerateResponseAsync(
            MessageProcessingResult processingResult,
            ChatSession session,
            CancellationToken cancellationToken)
        {
            var responseStartTime = DateTime.UtcNow;

            var responseOptions = await _conversationEngine.GenerateResponseAsync(processingResult, cancellationToken);

            var selectedResponse = await SelectBestResponseAsync(responseOptions, processingResult, cancellationToken);

            // Apply personality and emotion;
            selectedResponse = await _personalityEngine.ApplyPersonalityAsync(
                selectedResponse,
                processingResult.PersonalityTraits,
                cancellationToken);

            if (_config.EnableEmotionDetection && processingResult.Context.UserEmotion != null)
            {
                selectedResponse = await _emotionDetector.AdaptResponseToEmotionAsync(
                    selectedResponse,
                    processingResult.Context.UserEmotion,
                    cancellationToken);
            }

            var response = new ChatResponse;
            {
                ResponseId = Guid.NewGuid().ToString(),
                SessionId = session.SessionId,
                UserId = session.UserId,
                Text = selectedResponse.Text,
                Alternatives = responseOptions;
                    .Where(r => r.Id != selectedResponse.Id)
                    .Take(_config.MaxAlternativeResponses)
                    .Select(r => r.Text)
                    .ToList(),
                Intent = processingResult.DetectedIntent.IntentName,
                Confidence = processingResult.Confidence,
                RequiresClarification = processingResult.ClarificationQuestions?.Count > 0,
                ClarificationQuestions = processingResult.ClarificationQuestions,
                SuggestedTopics = processingResult.TopicUpdate.SuggestedTopics,
                Emotion = processingResult.Context.UserEmotion,
                ProcessingTime = DateTime.UtcNow - responseStartTime,
                Timestamp = DateTime.UtcNow,
                Metadata = new ResponseMetadata;
                {
                    ModelUsed = selectedResponse.Model,
                    Temperature = selectedResponse.Temperature,
                    PersonalityTraits = processingResult.PersonalityTraits,
                    ContextRelevance = CalculateContextRelevance(processingResult)
                }
            };

            return response;
        }

        /// <summary>
        /// Finalizes message processing and updates state;
        /// </summary>
        private async Task FinalizeMessageProcessingAsync(
            ChatSession session,
            ChatMessage message,
            ChatResponse response,
            CancellationToken cancellationToken)
        {
            // Store in history;
            await _historyManager.AddMessageAsync(session.SessionId, message, response, cancellationToken);

            // Update context;
            await _contextManager.UpdateMessageContextAsync(
                session.SessionId,
                message,
                response,
                cancellationToken);

            // Update topic;
            await _topicManager.UpdateTopicAsync(
                session.SessionId,
                message.Text,
                response.Text,
                cancellationToken);

            // Publish event;
            await _eventBus.PublishAsync(new MessageProcessedEvent;
            {
                SessionId = session.SessionId,
                UserId = session.UserId,
                MessageId = message.MessageId,
                ResponseId = response.ResponseId,
                ProcessingTime = response.ProcessingTime,
                Intent = response.Intent,
                Confidence = response.Confidence,
                Timestamp = DateTime.UtcNow;
            }, cancellationToken);

            // Send via chat hub;
            await _chatHub.SendMessageAsync(response, cancellationToken);
        }

        /// <summary>
        /// Generates error response for failed processing;
        /// </summary>
        private async Task<ChatResponse> GenerateErrorResponseAsync(
            ChatMessage message,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var errorResponse = await _conversationEngine.GenerateErrorResponseAsync(
                message.Text,
                exception,
                cancellationToken);

            return new ChatResponse;
            {
                ResponseId = Guid.NewGuid().ToString(),
                SessionId = message.SessionId,
                UserId = message.UserId,
                Text = errorResponse.Text,
                IsError = true,
                ErrorCode = exception is ChatException chatEx ? chatEx.ErrorCode : "CHAT001",
                ErrorMessage = exception.Message,
                ProcessingTime = TimeSpan.Zero,
                Timestamp = DateTime.UtcNow,
                Metadata = new ResponseMetadata;
                {
                    IsFallbackResponse = true,
                    ErrorType = exception.GetType().Name;
                }
            };
        }

        /// <summary>
        /// Creates a new session;
        /// </summary>
        private async Task<ChatSession> CreateNewSessionAsync(
            string sessionId,
            string userId,
            SessionParameters parameters,
            CancellationToken cancellationToken)
        {
            var session = new ChatSession;
            {
                SessionId = sessionId,
                UserId = userId,
                StartTime = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                Parameters = parameters ?? new SessionParameters(),
                MessageCount = 0,
                State = SessionState.Active,
                Metadata = new Dictionary<string, object>
                {
                    ["CreatedBy"] = "ChatController",
                    ["Version"] = "1.0",
                    ["ClientInfo"] = parameters?.ClientInfo;
                }
            };

            await _sessionManager.CreateSessionAsync(session, cancellationToken);
            await _contextManager.InitializeSessionContextAsync(sessionId, parameters, cancellationToken);

            return session;
        }

        /// <summary>
        /// Finalizes and cleans up a session;
        /// </summary>
        private async Task FinalizeSessionAsync(ChatSession session, CancellationToken cancellationToken)
        {
            session.State = SessionState.Ended;
            session.EndTime = DateTime.UtcNow;

            await _sessionManager.UpdateSessionAsync(session, cancellationToken);
            await _contextManager.FinalizeSessionContextAsync(session.SessionId, cancellationToken);

            // Archive conversation if enabled;
            if (_config.EnableConversationArchiving)
            {
                await ArchiveConversationAsync(session, cancellationToken);
            }
        }

        /// <summary>
        /// Archives conversation history;
        /// </summary>
        private async Task ArchiveConversationAsync(ChatSession session, CancellationToken cancellationToken)
        {
            try
            {
                var history = await _historyManager.GetSessionHistoryAsync(
                    session.SessionId,
                    int.MaxValue,
                    cancellationToken);

                await _historyManager.ArchiveHistoryAsync(session.SessionId, history, cancellationToken);

                _logger.Debug($"Conversation archived", new { SessionId = session.SessionId });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to archive conversation: {session.SessionId}", ex);
            }
        }

        /// <summary>
        /// Cleans up inactive sessions;
        /// </summary>
        private async Task CleanupInactiveSessionsAsync(CancellationToken cancellationToken)
        {
            var inactiveSessions = _activeSessions;
                .Where(kvp => kvp.Value.LastActivity.Add(_config.SessionTimeout) < DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var sessionId in inactiveSessions)
            {
                try
                {
                    await EndSessionAsync(sessionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to cleanup inactive session: {sessionId}", ex);
                }
            }
        }

        /// <summary>
        /// Generates unique session ID;
        /// </summary>
        private string GenerateSessionId(string userId)
        {
            return $"{userId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Merges session parameters;
        /// </summary>
        private SessionParameters MergeParameters(SessionParameters original, SessionParameters updates)
        {
            if (updates == null)
                return original;

            return new SessionParameters;
            {
                Language = updates.Language ?? original.Language,
                Personality = updates.Personality ?? original.Personality,
                ResponseStyle = updates.ResponseStyle ?? original.ResponseStyle,
                MaxResponseLength = updates.MaxResponseLength > 0 ? updates.MaxResponseLength : original.MaxResponseLength,
                EnableProfanityFilter = updates.EnableProfanityFilter ?? original.EnableProfanityFilter,
                EnableEmotionDetection = updates.EnableEmotionDetection ?? original.EnableEmotionDetection,
                AutoCreateSession = updates.AutoCreateSession ?? original.AutoCreateSession,
                Source = updates.Source ?? original.Source,
                ClientInfo = updates.ClientInfo ?? original.ClientInfo,
                CustomSettings = original.CustomSettings;
                    .Concat(updates.CustomSettings)
                    .GroupBy(kvp => kvp.Key)
                    .ToDictionary(g => g.Key, g => g.Last().Value)
            };
        }

        /// <summary>
        /// Selects the best response from options;
        /// </summary>
        private async Task<GeneratedResponse> SelectBestResponseAsync(
            List<GeneratedResponse> responses,
            MessageProcessingResult processingResult,
            CancellationToken cancellationToken)
        {
            if (responses.Count == 0)
                return await _conversationEngine.GenerateFallbackResponseAsync(processingResult, cancellationToken);

            if (responses.Count == 1)
                return responses[0];

            // Score each response based on multiple factors;
            var scoredResponses = new List<(GeneratedResponse Response, double Score)>();

            foreach (var response in responses)
            {
                var score = CalculateResponseScore(response, processingResult);
                scoredResponses.Add((response, score));
            }

            // Select best scoring response;
            return scoredResponses;
                .OrderByDescending(x => x.Score)
                .First()
                .Response;
        }

        /// <summary>
        /// Calculates response quality score;
        /// </summary>
        private double CalculateResponseScore(GeneratedResponse response, MessageProcessingResult processingResult)
        {
            double score = 0.0;

            // Relevance to intent;
            score += response.RelevanceScore * 0.4;

            // Context relevance;
            score += CalculateContextRelevance(processingResult) * 0.3;

            // Response quality metrics;
            score += response.Confidence * 0.2;

            // Length appropriateness (penalize too short/long)
            var lengthScore = CalculateLengthScore(response.Text.Length);
            score += lengthScore * 0.1;

            return score;
        }

        /// <summary>
        /// Calculates context relevance score;
        /// </summary>
        private double CalculateContextRelevance(MessageProcessingResult processingResult)
        {
            if (processingResult.Context?.MessageHistory == null)
                return 0.5;

            var recentMessages = processingResult.Context.MessageHistory;
                .TakeLast(3)
                .Select(m => m.Text)
                .ToList();

            if (recentMessages.Count == 0)
                return 0.5;

            // Simple word overlap calculation;
            var currentWords = processingResult.OriginalMessage.Text;
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant())
                .ToHashSet();

            var overlapCount = recentMessages;
                .SelectMany(m => m.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Count(w => currentWords.Contains(w.ToLowerInvariant()));

            return Math.Min(1.0, overlapCount / (double)Math.Max(1, currentWords.Count * 2));
        }

        /// <summary>
        /// Calculates length appropriateness score;
        /// </summary>
        private double CalculateLengthScore(int length)
        {
            const int optimalMin = 20;
            const int optimalMax = 200;

            if (length < optimalMin)
                return 0.3;

            if (length <= optimalMax)
                return 1.0;

            if (length <= optimalMax * 2)
                return 0.7;

            return 0.4;
        }

        /// <summary>
        /// Initializes event bus subscriptions;
        /// </summary>
        private void InitializeEventSubscriptions()
        {
            _eventBus.Subscribe<SessionTimeoutEvent>(OnSessionTimeout);
            _eventBus.Subscribe<UserDisconnectedEvent>(OnUserDisconnected);
            _eventBus.Subscribe<SystemShutdownEvent>(OnSystemShutdown);
        }

        /// <summary>
        /// Handles session timeout events;
        /// </summary>
        private async Task OnSessionTimeout(SessionTimeoutEvent timeoutEvent)
        {
            try
            {
                await EndSessionAsync(timeoutEvent.SessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to handle session timeout: {timeoutEvent.SessionId}", ex);
            }
        }

        /// <summary>
        /// Handles user disconnection events;
        /// </summary>
        private async Task OnUserDisconnected(UserDisconnectedEvent disconnectEvent)
        {
            try
            {
                var userSessions = _activeSessions;
                    .Where(kvp => kvp.Value.UserId == disconnectEvent.UserId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var sessionId in userSessions)
                {
                    await EndSessionAsync(sessionId, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to handle user disconnect: {disconnectEvent.UserId}", ex);
            }
        }

        /// <summary>
        /// Handles system shutdown events;
        /// </summary>
        private async Task OnSystemShutdown(SystemShutdownEvent shutdownEvent)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to handle system shutdown", ex);
            }
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    /// <summary>
    /// Interface for chat controller;
    /// </summary>
    public interface IChatController : IAsyncDisposable;
    {
        Task<ChatResponse> ProcessMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
        Task<ChatSession> StartSessionAsync(string userId, SessionParameters parameters = null, CancellationToken cancellationToken = default);
        Task EndSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<ChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task UpdateSessionParametersAsync(string sessionId, SessionParameters parameters, CancellationToken cancellationToken = default);
        Task<ConversationHistory> GetConversationHistoryAsync(string sessionId, int maxMessages = 50, CancellationToken cancellationToken = default);
        Task ClearConversationHistoryAsync(string sessionId, CancellationToken cancellationToken = default);
        Task SendTypingIndicatorAsync(string sessionId, bool isTyping, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Chat message from user;
    /// </summary>
    public class ChatMessage;
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Text { get; set; }
        public List<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public MessageSource Source { get; set; } = MessageSource.Chat;
    }

    /// <summary>
    /// Chat response from system;
    /// </summary>
    public class ChatResponse;
    {
        public string ResponseId { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Text { get; set; }
        public List<string> Alternatives { get; set; } = new List<string>();
        public string Intent { get; set; }
        public double Confidence { get; set; }
        public bool RequiresClarification { get; set; }
        public List<ClarificationQuestion> ClarificationQuestions { get; set; }
        public List<string> SuggestedTopics { get; set; } = new List<string>();
        public DetectedEmotion Emotion { get; set; }
        public bool IsError { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public ResponseMetadata Metadata { get; set; } = new ResponseMetadata();
    }

    /// <summary>
    /// Chat session information;
    /// </summary>
    public class ChatSession;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastActivity { get; set; }
        public DateTime LastModified { get; set; }
        public SessionParameters Parameters { get; set; } = new SessionParameters();
        public int MessageCount { get; set; }
        public SessionState State { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Session parameters;
    /// </summary>
    public class SessionParameters;
    {
        public string Language { get; set; } = "en-US";
        public string Personality { get; set; } = "neutral";
        public string ResponseStyle { get; set; } = "balanced";
        public int MaxResponseLength { get; set; } = 500;
        public bool? EnableProfanityFilter { get; set; } = true;
        public bool? EnableEmotionDetection { get; set; } = true;
        public bool? AutoCreateSession { get; set; } = false;
        public MessageSource Source { get; set; } = MessageSource.Chat;
        public Dictionary<string, object> ClientInfo { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Message processing result;
    /// </summary>
    public class MessageProcessingResult;
    {
        public ChatMessage OriginalMessage { get; set; }
        public SemanticAnalysisResult SemanticAnalysis { get; set; }
        public DetectedIntent DetectedIntent { get; set; }
        public double Confidence { get; set; }
        public List<ClarificationQuestion> ClarificationQuestions { get; set; }
        public TopicUpdateResult TopicUpdate { get; set; }
        public PersonalityTraits PersonalityTraits { get; set; }
        public ConversationContext Context { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Generated response from conversation engine;
    /// </summary>
    public class GeneratedResponse;
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public double RelevanceScore { get; set; }
        public string Model { get; set; }
        public double Temperature { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Response metadata;
    /// </summary>
    public class ResponseMetadata;
    {
        public string ModelUsed { get; set; }
        public double Temperature { get; set; }
        public PersonalityTraits PersonalityTraits { get; set; }
        public double ContextRelevance { get; set; }
        public bool IsFallbackResponse { get; set; }
        public string ErrorType { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Message attachment;
    /// </summary>
    public class MessageAttachment;
    {
        public string Id { get; set; }
        public string Type { get; set; } // image, audio, video, file;
        public string Url { get; set; }
        public string Filename { get; set; }
        public long Size { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Configuration for chat controller;
    /// </summary>
    public class ChatControllerConfig;
    {
        public static ChatControllerConfig Default => new ChatControllerConfig;
        {
            MaxConcurrentSessions = 1000,
            SessionTimeout = TimeSpan.FromMinutes(30),
            MaxMessageLength = 2000,
            ConfidenceThreshold = 0.7,
            MaxAlternativeResponses = 3,
            EnableEmotionDetection = true,
            EnableConversationArchiving = true,
            EnableTypingIndicators = true,
            ResponseTimeout = TimeSpan.FromSeconds(30)
        };

        public int MaxConcurrentSessions { get; set; }
        public TimeSpan SessionTimeout { get; set; }
        public int MaxMessageLength { get; set; }
        public double ConfidenceThreshold { get; set; }
        public int MaxAlternativeResponses { get; set; }
        public bool EnableEmotionDetection { get; set; }
        public bool EnableConversationArchiving { get; set; }
        public bool EnableTypingIndicators { get; set; }
        public TimeSpan ResponseTimeout { get; set; }
    }

    /// <summary>
    /// Session states;
    /// </summary>
    public enum SessionState;
    {
        Active,
        Paused,
        Ended,
        TimedOut,
        Error;
    }

    /// <summary>
    /// Message sources;
    /// </summary>
    public enum MessageSource;
    {
        Chat,
        Voice,
        Email,
        API,
        System;
    }

    /// <summary>
    /// Custom exceptions for chat system;
    /// </summary>
    public class ChatException : NEDAException;
    {
        public string SessionId { get; }
        public string UserId { get; }

        public ChatException(string message, string errorCode, string sessionId = null, string userId = null)
            : base($"{errorCode}: {message}")
        {
            SessionId = sessionId;
            UserId = userId;
        }

        public ChatException(string message, Exception innerException, string errorCode, string sessionId = null, string userId = null)
            : base($"{errorCode}: {message}", innerException)
        {
            SessionId = sessionId;
            UserId = userId;
        }
    }

    public class ChatSessionException : ChatException;
    {
        public ChatSessionException(string message, string sessionId = null)
            : base(message, "CHAT001", sessionId) { }
    }

    public class MessageProcessingException : ChatException;
    {
        public string MessageId { get; }

        public MessageProcessingException(string message, string messageId, string sessionId = null)
            : base(message, "CHAT002", sessionId)
        {
            MessageId = messageId;
        }
    }

    #endregion;
}
