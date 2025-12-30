using MaterialDesignThemes.Wpf;
using NEDA.Common.Utilities;
using NEDA.Communication.DialogSystem;
using NEDA.Communication.DialogSystem.ConversationManager;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Interface.TextInput.ChatWindow;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;

namespace NEDA.Interface.TextInput.ChatWindow;
{
    /// <summary>
    /// Chat interface for managing conversations and user interactions;
    /// </summary>
    public interface IChatInterface : INotifyPropertyChanged, IDisposable;
    {
        /// <summary>
        /// Initializes the chat interface;
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Starts a new conversation;
        /// </summary>
        Task StartNewConversationAsync();

        /// <summary>
        /// Loads an existing conversation;
        /// </summary>
        Task LoadConversationAsync(string conversationId);

        /// <summary>
        /// Sends a message to the current conversation;
        /// </summary>
        Task SendMessageAsync(string message);

        /// <summary>
        /// Sends a message with attachments;
        /// </summary>
        Task SendMessageAsync(ChatMessage message);

        /// <summary>
        /// Edits an existing message;
        /// </summary>
        Task EditMessageAsync(string messageId, string newContent);

        /// <summary>
        /// Deletes a message;
        /// </summary>
        Task DeleteMessageAsync(string messageId);

        /// <summary>
        /// Searches messages in current conversation;
        /// </summary>
        Task<List<ChatMessage>> SearchMessagesAsync(string searchText);

        /// <summary>
        /// Clears the current conversation;
        /// </summary>
        Task ClearConversationAsync();

        /// <summary>
        /// Gets conversation history;
        /// </summary>
        Task<List<ConversationSummary>> GetConversationHistoryAsync();

        /// <summary>
        /// Sets user preferences for chat;
        /// </summary>
        Task SetUserPreferencesAsync(ChatPreferences preferences);

        /// <summary>
        /// Gets the current conversation;
        /// </summary>
        Conversation CurrentConversation { get; }

        /// <summary>
        /// Gets all messages in current conversation;
        /// </summary>
        ObservableCollection<ChatMessage> Messages { get; }

        /// <summary>
        /// Gets whether chat is connected;
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets whether chat is busy;
        /// </summary>
        bool IsBusy { get; }

        /// <summary>
        /// Gets the current user;
        /// </summary>
        ChatUser CurrentUser { get; }

        /// <summary>
        /// Event raised when a new message is received;
        /// </summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// Event raised when a message is sent;
        /// </summary>
        event EventHandler<MessageSentEventArgs> MessageSent;

        /// <summary>
        /// Event raised when conversation changes;
        /// </summary>
        event EventHandler<ConversationChangedEventArgs> ConversationChanged;

        /// <summary>
        /// Event raised when connection status changes;
        /// </summary>
        event EventHandler<ConnectionStatusChangedEventArgs> ConnectionStatusChanged;

        /// <summary>
        /// Event raised when typing indicator changes;
        /// </summary>
        event EventHandler<TypingIndicatorEventArgs> TypingIndicatorChanged;
    }

    /// <summary>
    /// Main chat interface implementation;
    /// </summary>
    public class ChatInterface : IChatInterface;
    {
        private readonly ILogger _logger;
        private readonly IMessageHandler _messageHandler;
        private readonly IConversationManager _conversationManager;
        private readonly IEventBus _eventBus;
        private readonly IChatConnectionManager _connectionManager;
        private readonly IChatStorage _chatStorage;
        private readonly IChatNotificationService _notificationService;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private bool _isBusy = false;
        private bool _isConnected = false;
        private Conversation _currentConversation;
        private ChatUser _currentUser;

        /// <summary>
        /// Messages in current conversation;
        /// </summary>
        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();

        /// <summary>
        /// Current conversation;
        /// </summary>
        public Conversation CurrentConversation;
        {
            get => _currentConversation;
            private set;
            {
                if (_currentConversation != value)
                {
                    _currentConversation = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Connection status;
        /// </summary>
        public bool IsConnected;
        {
            get => _isConnected;
            private set;
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Busy status;
        /// </summary>
        public bool IsBusy;
        {
            get => _isBusy;
            private set;
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Current user;
        /// </summary>
        public ChatUser CurrentUser;
        {
            get => _currentUser;
            private set;
            {
                if (_currentUser != value)
                {
                    _currentUser = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Event raised when a new message is received;
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        /// <summary>
        /// Event raised when a message is sent;
        /// </summary>
        public event EventHandler<MessageSentEventArgs>? MessageSent;

        /// <summary>
        /// Event raised when conversation changes;
        /// </summary>
        public event EventHandler<ConversationChangedEventArgs>? ConversationChanged;

        /// <summary>
        /// Event raised when connection status changes;
        /// </summary>
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        /// <summary>
        /// Event raised when typing indicator changes;
        /// </summary>
        public event EventHandler<TypingIndicatorEventArgs>? TypingIndicatorChanged;

        /// <summary>
        /// Property changed event;
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Initializes a new instance of ChatInterface;
        /// </summary>
        public ChatInterface(
            ILogger logger,
            IMessageHandler messageHandler,
            IConversationManager conversationManager,
            IEventBus eventBus,
            IChatConnectionManager connectionManager = null,
            IChatStorage chatStorage = null,
            IChatNotificationService notificationService = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _conversationManager = conversationManager ?? throw new ArgumentNullException(nameof(conversationManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _connectionManager = connectionManager ?? new DefaultChatConnectionManager();
            _chatStorage = chatStorage ?? new DefaultChatStorage();
            _notificationService = notificationService ?? new DefaultChatNotificationService();

            // Subscribe to events;
            _messageHandler.MessageReceived += OnMessageHandlerMessageReceived;
            _messageHandler.MessageSent += OnMessageHandlerMessageSent;
            _connectionManager.ConnectionStatusChanged += OnConnectionStatusChanged;

            _logger.LogInformation("ChatInterface initialized");
        }

        /// <summary>
        /// Initializes the chat interface;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                _logger.LogDebug("Initializing ChatInterface...");

                // Initialize user;
                await InitializeUserAsync();

                // Connect to chat service;
                await _connectionManager.ConnectAsync();

                // Load last conversation or start new;
                await LoadOrCreateConversationAsync();

                // Subscribe to event bus;
                await _eventBus.SubscribeAsync<ChatEvent>(OnChatEventReceived);

                IsConnected = true;
                _logger.LogInformation("ChatInterface initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize ChatInterface: {ex.Message}", ex);
                throw new ChatInitializationException("Failed to initialize chat interface", ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Starts a new conversation;
        /// </summary>
        public async Task StartNewConversationAsync()
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                _logger.LogDebug("Starting new conversation...");

                // Clear current messages;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Messages.Clear();
                });

                // Create new conversation;
                var newConversation = new Conversation;
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = $"Conversation {DateTime.Now:yyyyMMdd_HHmmss}",
                    CreatedAt = DateTime.UtcNow,
                    Participants = new List<ChatUser> { CurrentUser },
                    LastActivity = DateTime.UtcNow;
                };

                // Save conversation;
                await _chatStorage.SaveConversationAsync(newConversation);

                // Update current conversation;
                CurrentConversation = newConversation;

                // Raise conversation changed event;
                OnConversationChanged(new ConversationChangedEventArgs;
                {
                    OldConversationId = CurrentConversation?.Id,
                    NewConversationId = newConversation.Id,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"New conversation started: {newConversation.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start new conversation: {ex.Message}", ex);
                throw new ConversationException("Failed to start new conversation", ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Loads an existing conversation;
        /// </summary>
        public async Task LoadConversationAsync(string conversationId)
        {
            Guard.AgainstNullOrEmpty(conversationId, nameof(conversationId));

            if (IsBusy || CurrentConversation?.Id == conversationId)
                return;

            IsBusy = true;

            try
            {
                _logger.LogDebug($"Loading conversation: {conversationId}");

                // Load conversation;
                var conversation = await _chatStorage.LoadConversationAsync(conversationId);
                if (conversation == null)
                {
                    throw new ConversationNotFoundException($"Conversation {conversationId} not found");
                }

                // Load messages;
                var messages = await _chatStorage.LoadMessagesAsync(conversationId, 100);

                // Update UI;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Messages.Clear();
                    foreach (var message in messages.OrderBy(m => m.Timestamp))
                    {
                        Messages.Add(message);
                    }
                });

                var oldConversationId = CurrentConversation?.Id;
                CurrentConversation = conversation;

                // Raise conversation changed event;
                OnConversationChanged(new ConversationChangedEventArgs;
                {
                    OldConversationId = oldConversationId,
                    NewConversationId = conversationId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Conversation loaded: {conversationId}, Messages: {messages.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load conversation {conversationId}: {ex.Message}", ex);
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Sends a message to the current conversation;
        /// </summary>
        public async Task SendMessageAsync(string message)
        {
            Guard.AgainstNullOrEmpty(message, nameof(message));

            if (CurrentConversation == null)
            {
                await StartNewConversationAsync();
            }

            var chatMessage = new ChatMessage;
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = CurrentConversation.Id,
                SenderId = CurrentUser.Id,
                SenderName = CurrentUser.Name,
                Content = message,
                Type = MessageType.Text,
                Timestamp = DateTime.UtcNow,
                Status = MessageStatus.Sending;
            };

            await SendMessageAsync(chatMessage);
        }

        /// <summary>
        /// Sends a message with attachments;
        /// </summary>
        public async Task SendMessageAsync(ChatMessage message)
        {
            Guard.AgainstNull(message, nameof(message));

            if (CurrentConversation == null)
            {
                await StartNewConversationAsync();
                message.ConversationId = CurrentConversation.Id;
            }

            if (IsBusy)
            {
                _logger.LogWarning("Cannot send message - chat is busy");
                return;
            }

            IsBusy = true;

            try
            {
                _logger.LogDebug($"Sending message: {message.Id}");

                // Add to UI immediately;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Messages.Add(message);
                });

                // Send via message handler;
                var result = await _messageHandler.SendMessageAsync(message);

                if (result.Success)
                {
                    // Update message status;
                    message.Status = MessageStatus.Sent;
                    message.Timestamp = result.ServerTimestamp;

                    // Save message;
                    await _chatStorage.SaveMessageAsync(message);

                    // Update conversation;
                    CurrentConversation.LastActivity = DateTime.UtcNow;
                    await _chatStorage.SaveConversationAsync(CurrentConversation);

                    _logger.LogInformation($"Message sent successfully: {message.Id}");
                }
                else;
                {
                    message.Status = MessageStatus.Failed;
                    _logger.LogError($"Failed to send message {message.Id}: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                message.Status = MessageStatus.Failed;
                _logger.LogError($"Failed to send message {message.Id}: {ex.Message}", ex);
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Edits an existing message;
        /// </summary>
        public async Task EditMessageAsync(string messageId, string newContent)
        {
            Guard.AgainstNullOrEmpty(messageId, nameof(messageId));
            Guard.AgainstNullOrEmpty(newContent, nameof(newContent));

            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                _logger.LogDebug($"Editing message: {messageId}");

                // Find message;
                var message = await _chatStorage.LoadMessageAsync(messageId);
                if (message == null)
                {
                    throw new MessageNotFoundException($"Message {messageId} not found");
                }

                // Check if user can edit;
                if (message.SenderId != CurrentUser.Id)
                {
                    throw new UnauthorizedAccessException("Cannot edit other user's messages");
                }

                // Check if message is too old to edit;
                if ((DateTime.UtcNow - message.Timestamp).TotalMinutes > 15)
                {
                    throw new InvalidOperationException("Cannot edit message older than 15 minutes");
                }

                // Update message;
                var oldContent = message.Content;
                message.Content = newContent;
                message.IsEdited = true;
                message.EditedAt = DateTime.UtcNow;

                // Update in storage;
                await _chatStorage.UpdateMessageAsync(message);

                // Update in UI;
                await UpdateMessageInUIAsync(message);

                _logger.LogInformation($"Message edited: {messageId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to edit message {messageId}: {ex.Message}", ex);
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Deletes a message;
        /// </summary>
        public async Task DeleteMessageAsync(string messageId)
        {
            Guard.AgainstNullOrEmpty(messageId, nameof(messageId));

            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                _logger.LogDebug($"Deleting message: {messageId}");

                // Find message;
                var message = await _chatStorage.LoadMessageAsync(messageId);
                if (message == null)
                {
                    throw new MessageNotFoundException($"Message {messageId} not found");
                }

                // Check if user can delete;
                if (message.SenderId != CurrentUser.Id)
                {
                    throw new UnauthorizedAccessException("Cannot delete other user's messages");
                }

                // Delete via message handler;
                var success = await _messageHandler.DeleteMessageAsync(messageId);
                if (success)
                {
                    // Remove from UI;
                    await RemoveMessageFromUIAsync(messageId);

                    _logger.LogInformation($"Message deleted: {messageId}");
                }
                else;
                {
                    throw new MessageDeleteException($"Failed to delete message {messageId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete message {messageId}: {ex.Message}", ex);
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Searches messages in current conversation;
        /// </summary>
        public async Task<List<ChatMessage>> SearchMessagesAsync(string searchText)
        {
            Guard.AgainstNullOrEmpty(searchText, nameof(searchText));

            if (CurrentConversation == null)
                return new List<ChatMessage>();

            try
            {
                _logger.LogDebug($"Searching messages for: {searchText}");

                var criteria = new MessageSearchCriteria;
                {
                    ConversationId = CurrentConversation.Id,
                    SearchText = searchText,
                    MaxResults = 50,
                    SortOrder = SearchSortOrder.Descending;
                };

                var results = await _messageHandler.SearchMessagesAsync(criteria);

                _logger.LogInformation($"Search found {results.Count} messages");

                return new List<ChatMessage>(results);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to search messages: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Clears the current conversation;
        /// </summary>
        public async Task ClearConversationAsync()
        {
            if (IsBusy || CurrentConversation == null)
                return;

            IsBusy = true;

            try
            {
                _logger.LogDebug($"Clearing conversation: {CurrentConversation.Id}");

                // Clear messages in UI;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Messages.Clear();
                });

                // Clear messages in storage;
                await _chatStorage.ClearMessagesAsync(CurrentConversation.Id);

                // Reset conversation;
                CurrentConversation.LastActivity = DateTime.UtcNow;
                await _chatStorage.SaveConversationAsync(CurrentConversation);

                _logger.LogInformation($"Conversation cleared: {CurrentConversation.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to clear conversation: {ex.Message}", ex);
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Gets conversation history;
        /// </summary>
        public async Task<List<ConversationSummary>> GetConversationHistoryAsync()
        {
            try
            {
                _logger.LogDebug("Getting conversation history");

                var conversations = await _chatStorage.LoadConversationSummariesAsync(50);

                _logger.LogInformation($"Loaded {conversations.Count} conversations from history");

                return conversations;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get conversation history: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Sets user preferences for chat;
        /// </summary>
        public async Task SetUserPreferencesAsync(ChatPreferences preferences)
        {
            Guard.AgainstNull(preferences, nameof(preferences));

            try
            {
                _logger.LogDebug("Setting user preferences");

                CurrentUser.Preferences = preferences;
                await _chatStorage.SaveUserPreferencesAsync(CurrentUser.Id, preferences);

                _logger.LogInformation("User preferences updated");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to set user preferences: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Unsubscribe from events;
                    _messageHandler.MessageReceived -= OnMessageHandlerMessageReceived;
                    _messageHandler.MessageSent -= OnMessageHandlerMessageSent;
                    _connectionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;

                    // Dispose managed resources;
                    _connectionManager?.Dispose();
                    _chatStorage?.Dispose();
                    _notificationService?.Dispose();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Raises property changed event;
        /// </summary>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Raises message received event;
        /// </summary>
        protected virtual void OnMessageReceived(MessageReceivedEventArgs e)
        {
            MessageReceived?.Invoke(this, e);
        }

        /// <summary>
        /// Raises message sent event;
        /// </summary>
        protected virtual void OnMessageSent(MessageSentEventArgs e)
        {
            MessageSent?.Invoke(this, e);
        }

        /// <summary>
        /// Raises conversation changed event;
        /// </summary>
        protected virtual void OnConversationChanged(ConversationChangedEventArgs e)
        {
            ConversationChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Raises connection status changed event;
        /// </summary>
        protected virtual void OnConnectionStatusChanged(ConnectionStatusChangedEventArgs e)
        {
            ConnectionStatusChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Raises typing indicator changed event;
        /// </summary>
        protected virtual void OnTypingIndicatorChanged(TypingIndicatorEventArgs e)
        {
            TypingIndicatorChanged?.Invoke(this, e);
        }

        private async Task InitializeUserAsync()
        {
            // Load current user from storage or create new;
            var userId = "current_user"; // In real app, get from authentication;
            var user = await _chatStorage.LoadUserAsync(userId);

            if (user == null)
            {
                user = new ChatUser;
                {
                    Id = userId,
                    Name = Environment.UserName,
                    Email = Environment.UserName + "@local",
                    CreatedAt = DateTime.UtcNow,
                    Preferences = new ChatPreferences(),
                    IsOnline = true;
                };

                await _chatStorage.SaveUserAsync(user);
            }

            CurrentUser = user;
            _logger.LogInformation($"User initialized: {CurrentUser.Name}");
        }

        private async Task LoadOrCreateConversationAsync()
        {
            // Try to load last conversation;
            var conversations = await _chatStorage.LoadConversationSummariesAsync(1);

            if (conversations.Count > 0)
            {
                await LoadConversationAsync(conversations[0].Id);
            }
            else;
            {
                await StartNewConversationAsync();
            }
        }

        private async void OnMessageHandlerMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                // Check if message belongs to current conversation;
                if (CurrentConversation?.Id != e.Message.ConversationId)
                    return;

                // Add message to UI;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Messages.Add(e.Message);
                });

                // Save message;
                await _chatStorage.SaveMessageAsync(e.Message);

                // Update conversation;
                CurrentConversation.LastActivity = DateTime.UtcNow;
                await _chatStorage.SaveConversationAsync(CurrentConversation);

                // Raise event;
                OnMessageReceived(e);

                // Send notification;
                await _notificationService.NotifyMessageReceivedAsync(e.Message);

                _logger.LogDebug($"Message received: {e.Message.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to process received message: {ex.Message}", ex);
            }
        }

        private async void OnMessageHandlerMessageSent(object sender, MessageSentEventArgs e)
        {
            try
            {
                // Update message status in UI;
                var message = e.Message;
                message.Status = MessageStatus.Sent;

                await UpdateMessageInUIAsync(message);

                // Raise event;
                OnMessageSent(e);

                _logger.LogDebug($"Message sent confirmed: {e.Message.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to process sent message: {ex.Message}", ex);
            }
        }

        private void OnConnectionStatusChanged(object sender, ConnectionStatusChangedEventArgs e)
        {
            IsConnected = e.IsConnected;
            OnConnectionStatusChanged(e);
        }

        private async Task OnChatEventReceived(ChatEvent chatEvent)
        {
            try
            {
                switch (chatEvent.EventType)
                {
                    case ChatEventType.TypingStarted:
                        OnTypingIndicatorChanged(new TypingIndicatorEventArgs;
                        {
                            UserId = chatEvent.SenderId,
                            IsTyping = true,
                            Timestamp = DateTime.UtcNow;
                        });
                        break;

                    case ChatEventType.TypingStopped:
                        OnTypingIndicatorChanged(new TypingIndicatorEventArgs;
                        {
                            UserId = chatEvent.SenderId,
                            IsTyping = false,
                            Timestamp = DateTime.UtcNow;
                        });
                        break;

                    case ChatEventType.UserJoined:
                        // Update conversation participants;
                        if (CurrentConversation?.Id == chatEvent.ConversationId)
                        {
                            // In real implementation, load user and add to participants;
                            _logger.LogDebug($"User {chatEvent.SenderId} joined conversation");
                        }
                        break;

                    case ChatEventType.UserLeft:
                        // Update conversation participants;
                        if (CurrentConversation?.Id == chatEvent.ConversationId)
                        {
                            // In real implementation, remove user from participants;
                            _logger.LogDebug($"User {chatEvent.SenderId} left conversation");
                        }
                        break;

                    case ChatEventType.MessageRead:
                        // Update message status;
                        var message = await _chatStorage.LoadMessageAsync(chatEvent.MessageId);
                        if (message != null && message.SenderId == CurrentUser.Id)
                        {
                            message.Status = MessageStatus.Read;
                            await UpdateMessageInUIAsync(message);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to process chat event: {ex.Message}", ex);
            }
        }

        private async Task UpdateMessageInUIAsync(ChatMessage message)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Id == message.Id)
                    {
                        Messages[i] = message;
                        break;
                    }
                }
            });
        }

        private async Task RemoveMessageFromUIAsync(string messageId)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Id == messageId)
                    {
                        Messages.RemoveAt(i);
                        break;
                    }
                }
            });
        }
    }

    /// <summary>
    /// Conversation class;
    /// </summary>
    public class Conversation;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public List<ChatUser> Participants { get; set; } = new List<ChatUser>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public bool IsArchived { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Conversation summary for list views;
    /// </summary>
    public class ConversationSummary;
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string LastMessagePreview { get; set; } = string.Empty;
        public DateTime LastActivity { get; set; }
        public int UnreadCount { get; set; }
        public List<string> ParticipantNames { get; set; } = new List<string>();
    }

    /// <summary>
    /// Chat user;
    /// </summary>
    public class ChatUser;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsOnline { get; set; }
        public ChatPreferences Preferences { get; set; } = new ChatPreferences();
    }

    /// <summary>
    /// Chat preferences;
    /// </summary>
    public class ChatPreferences;
    {
        public bool EnableNotifications { get; set; } = true;
        public bool SoundEnabled { get; set; } = true;
        public string Theme { get; set; } = "Dark";
        public bool ShowTimestamps { get; set; } = true;
        public bool ShowAvatars { get; set; } = true;
        public int MessageHistorySize { get; set; } = 100;
        public bool AutoLoadImages { get; set; } = true;
        public bool SpellCheck { get; set; } = true;
        public string Language { get; set; } = "en-US";
    }

    /// <summary>
    /// Event arguments for conversation changed;
    /// </summary>
    public class ConversationChangedEventArgs : EventArgs;
    {
        public string? OldConversationId { get; set; }
        public string NewConversationId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for connection status changed;
    /// </summary>
    public class ConnectionStatusChangedEventArgs : EventArgs;
    {
        public bool IsConnected { get; set; }
        public string? Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for typing indicator;
    /// </summary>
    public class TypingIndicatorEventArgs : EventArgs;
    {
        public string UserId { get; set; } = string.Empty;
        public bool IsTyping { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Chat event types;
    /// </summary>
    public enum ChatEventType;
    {
        MessageReceived,
        MessageSent,
        TypingStarted,
        TypingStopped,
        UserJoined,
        UserLeft,
        MessageRead,
        ConversationCreated,
        ConversationDeleted;
    }

    /// <summary>
    /// Chat event for event bus;
    /// </summary>
    public class ChatEvent : IEvent;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public ChatEventType EventType { get; set; }
        public string SenderId { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Interface for chat connection management;
    /// </summary>
    public interface IChatConnectionManager : IDisposable
    {
        event EventHandler<ConnectionStatusChangedEventArgs> ConnectionStatusChanged;
        Task ConnectAsync();
        Task DisconnectAsync();
        Task<bool> PingAsync();
        bool IsConnected { get; }
    }

    /// <summary>
    /// Interface for chat storage;
    /// </summary>
    public interface IChatStorage : IDisposable
    {
        Task SaveMessageAsync(ChatMessage message);
        Task<ChatMessage?> LoadMessageAsync(string messageId);
        Task UpdateMessageAsync(ChatMessage message);
        Task<List<ChatMessage>> LoadMessagesAsync(string conversationId, int maxMessages);
        Task ClearMessagesAsync(string conversationId);
        Task SaveConversationAsync(Conversation conversation);
        Task<Conversation?> LoadConversationAsync(string conversationId);
        Task<List<ConversationSummary>> LoadConversationSummariesAsync(int maxConversations);
        Task SaveUserAsync(ChatUser user);
        Task<ChatUser?> LoadUserAsync(string userId);
        Task SaveUserPreferencesAsync(string userId, ChatPreferences preferences);
    }

    /// <summary>
    /// Interface for chat notification service;
    /// </summary>
    public interface IChatNotificationService : IDisposable
    {
        Task NotifyMessageReceivedAsync(ChatMessage message);
        Task NotifyTypingAsync(string conversationId, bool isTyping);
        Task NotifyConversationUpdatedAsync(Conversation conversation);
    }

    /// <summary>
    /// Custom exceptions for chat operations;
    /// </summary>
    public class ChatException : Exception
    {
        public ChatException(string message) : base(message) { }
        public ChatException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ChatInitializationException : ChatException;
    {
        public ChatInitializationException(string message) : base(message) { }
        public ChatInitializationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ConversationException : ChatException;
    {
        public ConversationException(string message) : base(message) { }
        public ConversationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ConversationNotFoundException : ConversationException;
    {
        public ConversationNotFoundException(string message) : base(message) { }
        public ConversationNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MessageNotFoundException : ChatException;
    {
        public MessageNotFoundException(string message) : base(message) { }
        public MessageNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MessageDeleteException : ChatException;
    {
        public MessageDeleteException(string message) : base(message) { }
        public MessageDeleteException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Default implementations;
    /// </summary>
    public class DefaultChatConnectionManager : IChatConnectionManager;
    {
        public event EventHandler<ConnectionStatusChangedEventArgs>? ConnectionStatusChanged;
        public bool IsConnected { get; private set; }

        public Task ConnectAsync()
        {
            IsConnected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs;
            {
                IsConnected = true,
                Timestamp = DateTime.UtcNow;
            });
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs;
            {
                IsConnected = false,
                Timestamp = DateTime.UtcNow;
            });
            return Task.CompletedTask;
        }

        public Task<bool> PingAsync() => Task.FromResult(IsConnected);
        public void Dispose() { }
    }

    public class DefaultChatStorage : IChatStorage;
    {
        private readonly Dictionary<string, ChatMessage> _messages = new Dictionary<string, ChatMessage>();
        private readonly Dictionary<string, Conversation> _conversations = new Dictionary<string, Conversation>();
        private readonly Dictionary<string, ChatUser> _users = new Dictionary<string, ChatUser>();
        private readonly object _lock = new object();

        public Task SaveMessageAsync(ChatMessage message)
        {
            lock (_lock)
            {
                _messages[message.Id] = message;
            }
            return Task.CompletedTask;
        }

        public Task<ChatMessage?> LoadMessageAsync(string messageId)
        {
            lock (_lock)
            {
                _messages.TryGetValue(messageId, out var message);
                return Task.FromResult(message);
            }
        }

        public Task UpdateMessageAsync(ChatMessage message)
        {
            lock (_lock)
            {
                _messages[message.Id] = message;
            }
            return Task.CompletedTask;
        }

        public Task<List<ChatMessage>> LoadMessagesAsync(string conversationId, int maxMessages)
        {
            lock (_lock)
            {
                var messages = _messages.Values;
                    .Where(m => m.ConversationId == conversationId)
                    .OrderBy(m => m.Timestamp)
                    .Take(maxMessages)
                    .ToList();
                return Task.FromResult(messages);
            }
        }

        public Task ClearMessagesAsync(string conversationId)
        {
            lock (_lock)
            {
                var keysToRemove = _messages.Where(kvp => kvp.Value.ConversationId == conversationId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _messages.Remove(key);
                }
            }

            return Task.CompletedTask;
        }

        public Task SaveConversationAsync(Conversation conversation)
        {
            lock (_lock)
            {
                _conversations[conversation.Id] = conversation;
            }
            return Task.CompletedTask;
        }

        public Task<Conversation?> LoadConversationAsync(string conversationId)
        {
            lock (_lock)
            {
                _conversations.TryGetValue(conversationId, out var conversation);
                return Task.FromResult(conversation);
            }
        }

        public Task<List<ConversationSummary>> LoadConversationSummariesAsync(int maxConversations)
        {
            lock (_lock)
            {
                var summaries = _conversations.Values;
                    .OrderByDescending(c => c.LastActivity)
                    .Take(maxConversations)
                    .Select(c => new ConversationSummary;
                    {
                        Id = c.Id,
                        Title = c.Title,
                        LastActivity = c.LastActivity,
                        ParticipantNames = c.Participants.Select(p => p.Name).ToList()
                    })
                    .ToList();

                return Task.FromResult(summaries);
            }
        }

        public Task SaveUserAsync(ChatUser user)
        {
            lock (_lock)
            {
                _users[user.Id] = user;
            }
            return Task.CompletedTask;
        }

        public Task<ChatUser?> LoadUserAsync(string userId)
        {
            lock (_lock)
            {
                _users.TryGetValue(userId, out var user);
                return Task.FromResult(user);
            }
        }

        public Task SaveUserPreferencesAsync(string userId, ChatPreferences preferences)
        {
            lock (_lock)
            {
                if (_users.TryGetValue(userId, out var user))
                {
                    user.Preferences = preferences;
                }
            }
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    public class DefaultChatNotificationService : IChatNotificationService;
    {
        public Task NotifyMessageReceivedAsync(ChatMessage message) => Task.CompletedTask;
        public Task NotifyTypingAsync(string conversationId, bool isTyping) => Task.CompletedTask;
        public Task NotifyConversationUpdatedAsync(Conversation conversation) => Task.CompletedTask;
        public void Dispose() { }
    }
}
