using MaterialDesignThemes.Wpf;
using NEDA.Common.Utilities;
using NEDA.Communication.DialogSystem;
using NEDA.Communication.DialogSystem.ConversationManager;
using NEDA.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NEDA.Interface.TextInput.ChatWindow;
{
    /// <summary>
    /// Message handling interface for chat operations;
    /// </summary>
    public interface IMessageHandler;
    {
        /// <summary>
        /// Processes incoming messages;
        /// </summary>
        Task<MessageResponse> ProcessMessageAsync(ChatMessage message);

        /// <summary>
        /// Sends a message;
        /// </summary>
        Task<MessageResult> SendMessageAsync(ChatMessage message);

        /// <summary>
        /// Gets message history for a conversation;
        /// </summary>
        Task<IReadOnlyList<ChatMessage>> GetMessageHistoryAsync(string conversationId, int maxMessages = 50);

        /// <summary>
        /// Deletes a message;
        /// </summary>
        Task<bool> DeleteMessageAsync(string messageId);

        /// <summary>
        /// Edits an existing message;
        /// </summary>
        Task<bool> EditMessageAsync(string messageId, string newContent);

        /// <summary>
        /// Marks messages as read;
        /// </summary>
        Task<bool> MarkAsReadAsync(params string[] messageIds);

        /// <summary>
        /// Forwards messages to another conversation;
        /// </summary>
        Task<bool> ForwardMessagesAsync(string[] messageIds, string targetConversationId);

        /// <summary>
        /// Searches messages by criteria;
        /// </summary>
        Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(MessageSearchCriteria criteria);

        /// <summary>
        /// Event raised when a new message is received;
        /// </summary>
        event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// Event raised when a message is sent;
        /// </summary>
        event EventHandler<MessageSentEventArgs> MessageSent;

        /// <summary>
        /// Event raised when a message status changes;
        /// </summary>
        event EventHandler<MessageStatusChangedEventArgs> MessageStatusChanged;
    }

    /// <summary>
    /// Chat message structure;
    /// </summary>
    public class ChatMessage;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ConversationId { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public MessageType Type { get; set; } = MessageType.Text;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public MessageStatus Status { get; set; } = MessageStatus.Sent;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public List<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
        public string ReplyToMessageId { get; set; } = string.Empty;
        public bool IsEdited { get; set; }
        public DateTime? EditedAt { get; set; }
        public string Language { get; set; } = "en";
    }

    /// <summary>
    /// Message attachment;
    /// </summary>
    public class MessageAttachment;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string Url { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public bool IsUploaded { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Message response from processing;
    /// </summary>
    public class MessageResponse;
    {
        public bool Success { get; set; }
        public ChatMessage OriginalMessage { get; set; } = new ChatMessage();
        public List<ChatMessage> Responses { get; set; } = new List<ChatMessage>();
        public ProcessingResult ProcessingResult { get; set; } = new ProcessingResult();
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message processing result;
    /// </summary>
    public class ProcessingResult;
    {
        public bool RequiresAction { get; set; }
        public List<string> DetectedIntents { get; set; } = new List<string>();
        public Dictionary<string, object> ExtractedData { get; set; } = new Dictionary<string, object>();
        public string SuggestedResponse { get; set; } = string.Empty;
        public MessagePriority Priority { get; set; } = MessagePriority.Normal;
        public bool IsSpam { get; set; }
        public double ConfidenceScore { get; set; }
    }

    /// <summary>
    /// Message sending result;
    /// </summary>
    public class MessageResult;
    {
        public bool Success { get; set; }
        public ChatMessage Message { get; set; } = new ChatMessage();
        public DateTime ServerTimestamp { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public DeliveryStatus DeliveryStatus { get; set; }
    }

    /// <summary>
    /// Message search criteria;
    /// </summary>
    public class MessageSearchCriteria;
    {
        public string ConversationId { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string SearchText { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public MessageType? MessageType { get; set; }
        public bool IncludeAttachments { get; set; }
        public int MaxResults { get; set; } = 100;
        public SearchSortOrder SortOrder { get; set; } = SearchSortOrder.Descending;
    }

    /// <summary>
    /// Event arguments for message received;
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs;
    {
        public ChatMessage Message { get; set; } = new ChatMessage();
        public string ConversationId { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }
    }

    /// <summary>
    /// Event arguments for message sent;
    /// </summary>
    public class MessageSentEventArgs : EventArgs;
    {
        public ChatMessage Message { get; set; } = new ChatMessage();
        public MessageResult Result { get; set; } = new MessageResult();
        public DateTime SentAt { get; set; }
    }

    /// <summary>
    /// Event arguments for message status change;
    /// </summary>
    public class MessageStatusChangedEventArgs : EventArgs;
    {
        public string MessageId { get; set; } = string.Empty;
        public MessageStatus OldStatus { get; set; }
        public MessageStatus NewStatus { get; set; }
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message types;
    /// </summary>
    public enum MessageType;
    {
        Text,
        Image,
        Video,
        Audio,
        File,
        Location,
        Contact,
        System,
        Command,
        RichText;
    }

    /// <summary>
    /// Message status;
    /// </summary>
    public enum MessageStatus;
    {
        Draft,
        Sent,
        Delivered,
        Read,
        Failed,
        Pending,
        Deleted;
    }

    /// <summary>
    /// Delivery status;
    /// </summary>
    public enum DeliveryStatus;
    {
        Pending,
        SentToServer,
        DeliveredToDevice,
        ReadByRecipient,
        Failed;
    }

    /// <summary>
    /// Message priority;
    /// </summary>
    public enum MessagePriority;
    {
        Low,
        Normal,
        High,
        Urgent;
    }

    /// <summary>
    /// Search sort order;
    /// </summary>
    public enum SearchSortOrder;
    {
        Ascending,
        Descending;
    }

    /// <summary>
    /// Main message handler for chat operations;
    /// </summary>
    public class MessageHandler : IMessageHandler, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IConversationManager _conversationManager;
        private readonly IMessageValidator _messageValidator;
        private readonly IMessageProcessor _messageProcessor;
        private readonly IMessageStorage _messageStorage;
        private readonly IAttachmentHandler _attachmentHandler;
        private readonly ISpamDetector _spamDetector;
        private readonly IMessageQueue _messageQueue;
        private bool _disposed = false;

        /// <summary>
        /// Event raised when a new message is received;
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        /// <summary>
        /// Event raised when a message is sent;
        /// </summary>
        public event EventHandler<MessageSentEventArgs>? MessageSent;

        /// <summary>
        /// Event raised when a message status changes;
        /// </summary>
        public event EventHandler<MessageStatusChangedEventArgs>? MessageStatusChanged;

        /// <summary>
        /// Initializes a new instance of MessageHandler;
        /// </summary>
        public MessageHandler(
            ILogger logger,
            IConversationManager conversationManager,
            IMessageValidator messageValidator,
            IMessageProcessor messageProcessor,
            IMessageStorage messageStorage,
            IAttachmentHandler attachmentHandler,
            ISpamDetector spamDetector,
            IMessageQueue messageQueue)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _conversationManager = conversationManager ?? throw new ArgumentNullException(nameof(conversationManager));
            _messageValidator = messageValidator ?? throw new ArgumentNullException(nameof(messageValidator));
            _messageProcessor = messageProcessor ?? throw new ArgumentNullException(nameof(messageProcessor));
            _messageStorage = messageStorage ?? throw new ArgumentNullException(nameof(messageStorage));
            _attachmentHandler = attachmentHandler ?? throw new ArgumentNullException(nameof(attachmentHandler));
            _spamDetector = spamDetector ?? throw new ArgumentNullException(nameof(spamDetector));
            _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));

            _logger.LogInformation("MessageHandler initialized successfully");
        }

        /// <summary>
        /// Processes incoming messages;
        /// </summary>
        public async Task<MessageResponse> ProcessMessageAsync(ChatMessage message)
        {
            Guard.AgainstNull(message, nameof(message));
            Guard.AgainstNullOrEmpty(message.Content, nameof(message.Content));

            try
            {
                _logger.LogDebug($"Processing incoming message: {message.Id}");

                // Validate message;
                var validationResult = await _messageValidator.ValidateAsync(message);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning($"Message validation failed: {validationResult.ErrorMessage}");
                    return new MessageResponse;
                    {
                        Success = false,
                        OriginalMessage = message,
                        ErrorMessage = validationResult.ErrorMessage;
                    };
                }

                // Check for spam;
                var spamCheck = await _spamDetector.CheckSpamAsync(message);
                if (spamCheck.IsSpam)
                {
                    _logger.LogWarning($"Message detected as spam: {message.Id}");
                    message.Status = MessageStatus.Failed;
                    message.Metadata["SpamReason"] = spamCheck.Reason;

                    return new MessageResponse;
                    {
                        Success = false,
                        OriginalMessage = message,
                        ErrorMessage = "Message rejected as spam"
                    };
                }

                // Process message content;
                var processingResult = await _messageProcessor.ProcessAsync(message);

                // Handle attachments if any;
                if (message.Attachments?.Count > 0)
                {
                    await ProcessAttachmentsAsync(message);
                }

                // Store message;
                await _messageStorage.StoreMessageAsync(message);

                // Update conversation;
                await _conversationManager.UpdateConversationAsync(message.ConversationId, message);

                // Raise message received event;
                OnMessageReceived(new MessageReceivedEventArgs;
                {
                    Message = message,
                    ConversationId = message.ConversationId,
                    ReceivedAt = DateTime.UtcNow;
                });

                _logger.LogInformation($"Message processed successfully: {message.Id}");

                return new MessageResponse;
                {
                    Success = true,
                    OriginalMessage = message,
                    Responses = await GenerateResponsesAsync(message, processingResult),
                    ProcessingResult = processingResult;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to process message {message.Id}: {ex.Message}", ex);
                return new MessageResponse;
                {
                    Success = false,
                    OriginalMessage = message,
                    ErrorMessage = ex.Message;
                };
            }
        }

        /// <summary>
        /// Sends a message;
        /// </summary>
        public async Task<MessageResult> SendMessageAsync(ChatMessage message)
        {
            Guard.AgainstNull(message, nameof(message));

            try
            {
                _logger.LogDebug($"Sending message: {message.Id}");

                // Prepare message;
                message.Status = MessageStatus.Sent;
                message.Timestamp = DateTime.UtcNow;

                // Validate before sending;
                var validationResult = await _messageValidator.ValidateAsync(message);
                if (!validationResult.IsValid)
                {
                    message.Status = MessageStatus.Failed;
                    _logger.LogError($"Message validation failed before sending: {validationResult.ErrorMessage}");

                    return new MessageResult;
                    {
                        Success = false,
                        Message = message,
                        ErrorMessage = validationResult.ErrorMessage;
                    };
                }

                // Handle attachments;
                if (message.Attachments?.Count > 0)
                {
                    var uploadResults = await _attachmentHandler.UploadAttachmentsAsync(message.Attachments);
                    if (!uploadResults.All(r => r.Success))
                    {
                        message.Status = MessageStatus.Failed;
                        _logger.LogError("Failed to upload one or more attachments");

                        return new MessageResult;
                        {
                            Success = false,
                            Message = message,
                            ErrorMessage = "Attachment upload failed"
                        };
                    }
                }

                // Queue message for delivery;
                var queueResult = await _messageQueue.EnqueueAsync(message);
                if (!queueResult.Success)
                {
                    message.Status = MessageStatus.Failed;
                    _logger.LogError($"Failed to queue message: {queueResult.ErrorMessage}");

                    return new MessageResult;
                    {
                        Success = false,
                        Message = message,
                        ErrorMessage = queueResult.ErrorMessage;
                    };
                }

                // Store message;
                await _messageStorage.StoreMessageAsync(message);

                // Update conversation;
                await _conversationManager.UpdateConversationAsync(message.ConversationId, message);

                var result = new MessageResult;
                {
                    Success = true,
                    Message = message,
                    ServerTimestamp = DateTime.UtcNow,
                    MessageId = message.Id,
                    DeliveryStatus = DeliveryStatus.SentToServer;
                };

                // Raise message sent event;
                OnMessageSent(new MessageSentEventArgs;
                {
                    Message = message,
                    Result = result,
                    SentAt = DateTime.UtcNow;
                });

                _logger.LogInformation($"Message sent successfully: {message.Id}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to send message {message.Id}: {ex.Message}", ex);

                return new MessageResult;
                {
                    Success = false,
                    Message = message,
                    ErrorMessage = ex.Message,
                    DeliveryStatus = DeliveryStatus.Failed;
                };
            }
        }

        /// <summary>
        /// Gets message history for a conversation;
        /// </summary>
        public async Task<IReadOnlyList<ChatMessage>> GetMessageHistoryAsync(string conversationId, int maxMessages = 50)
        {
            Guard.AgainstNullOrEmpty(conversationId, nameof(conversationId));

            try
            {
                _logger.LogDebug($"Getting message history for conversation: {conversationId}");

                var messages = await _messageStorage.GetMessagesAsync(conversationId, maxMessages);

                _logger.LogInformation($"Retrieved {messages.Count} messages for conversation: {conversationId}");

                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get message history for {conversationId}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Deletes a message;
        /// </summary>
        public async Task<bool> DeleteMessageAsync(string messageId)
        {
            Guard.AgainstNullOrEmpty(messageId, nameof(messageId));

            try
            {
                _logger.LogDebug($"Deleting message: {messageId}");

                var message = await _messageStorage.GetMessageAsync(messageId);
                if (message == null)
                {
                    _logger.LogWarning($"Message not found for deletion: {messageId}");
                    return false;
                }

                // Update message status;
                message.Status = MessageStatus.Deleted;

                // Store updated message;
                await _messageStorage.UpdateMessageAsync(message);

                // Update conversation;
                await _conversationManager.UpdateConversationAsync(message.ConversationId, message);

                // Raise status change event;
                OnMessageStatusChanged(new MessageStatusChangedEventArgs;
                {
                    MessageId = messageId,
                    OldStatus = MessageStatus.Sent,
                    NewStatus = MessageStatus.Deleted,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = "System"
                });

                _logger.LogInformation($"Message deleted: {messageId}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete message {messageId}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Edits an existing message;
        /// </summary>
        public async Task<bool> EditMessageAsync(string messageId, string newContent)
        {
            Guard.AgainstNullOrEmpty(messageId, nameof(messageId));
            Guard.AgainstNullOrEmpty(newContent, nameof(newContent));

            try
            {
                _logger.LogDebug($"Editing message: {messageId}");

                var message = await _messageStorage.GetMessageAsync(messageId);
                if (message == null)
                {
                    _logger.LogWarning($"Message not found for editing: {messageId}");
                    return false;
                }

                // Store old content;
                var oldContent = message.Content;

                // Update message;
                message.Content = newContent;
                message.IsEdited = true;
                message.EditedAt = DateTime.UtcNow;

                // Validate edited message;
                var validationResult = await _messageValidator.ValidateAsync(message);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning($"Edited message validation failed: {validationResult.ErrorMessage}");
                    return false;
                }

                // Store updated message;
                await _messageStorage.UpdateMessageAsync(message);

                _logger.LogInformation($"Message edited: {messageId}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to edit message {messageId}: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Marks messages as read;
        /// </summary>
        public async Task<bool> MarkAsReadAsync(params string[] messageIds)
        {
            Guard.AgainstNull(messageIds, nameof(messageIds));
            if (messageIds.Length == 0) return true;

            try
            {
                _logger.LogDebug($"Marking {messageIds.Length} messages as read");

                foreach (var messageId in messageIds)
                {
                    var message = await _messageStorage.GetMessageAsync(messageId);
                    if (message != null && message.Status != MessageStatus.Read)
                    {
                        var oldStatus = message.Status;
                        message.Status = MessageStatus.Read;

                        await _messageStorage.UpdateMessageAsync(message);

                        // Raise status change event;
                        OnMessageStatusChanged(new MessageStatusChangedEventArgs;
                        {
                            MessageId = messageId,
                            OldStatus = oldStatus,
                            NewStatus = MessageStatus.Read,
                            ChangedAt = DateTime.UtcNow,
                            ChangedBy = "User"
                        });
                    }
                }

                _logger.LogInformation($"Marked {messageIds.Length} messages as read");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to mark messages as read: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Forwards messages to another conversation;
        /// </summary>
        public async Task<bool> ForwardMessagesAsync(string[] messageIds, string targetConversationId)
        {
            Guard.AgainstNull(messageIds, nameof(messageIds));
            Guard.AgainstNullOrEmpty(targetConversationId, nameof(targetConversationId));

            try
            {
                _logger.LogDebug($"Forwarding {messageIds.Length} messages to conversation: {targetConversationId}");

                foreach (var messageId in messageIds)
                {
                    var originalMessage = await _messageStorage.GetMessageAsync(messageId);
                    if (originalMessage == null) continue;

                    // Create forwarded message;
                    var forwardedMessage = new ChatMessage;
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = targetConversationId,
                        SenderId = originalMessage.SenderId,
                        SenderName = originalMessage.SenderName,
                        Type = originalMessage.Type,
                        Content = $"Forwarded: {originalMessage.Content}",
                        Timestamp = DateTime.UtcNow,
                        Status = MessageStatus.Sent,
                        Metadata = new Dictionary<string, string>
                        {
                            { "ForwardedFrom", originalMessage.Id },
                            { "OriginalSender", originalMessage.SenderId },
                            { "ForwardedAt", DateTime.UtcNow.ToString("O") }
                        },
                        Attachments = originalMessage.Attachments,
                        ReplyToMessageId = string.Empty;
                    };

                    // Send forwarded message;
                    await SendMessageAsync(forwardedMessage);
                }

                _logger.LogInformation($"Forwarded {messageIds.Length} messages to {targetConversationId}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to forward messages: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Searches messages by criteria;
        /// </summary>
        public async Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(MessageSearchCriteria criteria)
        {
            Guard.AgainstNull(criteria, nameof(criteria));

            try
            {
                _logger.LogDebug($"Searching messages with criteria: {criteria.SearchText}");

                var results = await _messageStorage.SearchMessagesAsync(criteria);

                _logger.LogInformation($"Search found {results.Count} messages");

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to search messages: {ex.Message}", ex);
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
                    _messageQueue?.Dispose();
                    _attachmentHandler?.Dispose();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Raises the MessageReceived event;
        /// </summary>
        protected virtual void OnMessageReceived(MessageReceivedEventArgs e)
        {
            MessageReceived?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the MessageSent event;
        /// </summary>
        protected virtual void OnMessageSent(MessageSentEventArgs e)
        {
            MessageSent?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the MessageStatusChanged event;
        /// </summary>
        protected virtual void OnMessageStatusChanged(MessageStatusChangedEventArgs e)
        {
            MessageStatusChanged?.Invoke(this, e);
        }

        private async Task ProcessAttachmentsAsync(ChatMessage message)
        {
            foreach (var attachment in message.Attachments)
            {
                if (!attachment.IsUploaded)
                {
                    var result = await _attachmentHandler.ProcessAttachmentAsync(attachment);
                    if (result.Success)
                    {
                        attachment.IsUploaded = true;
                        attachment.Url = result.Url;
                        attachment.ThumbnailUrl = result.ThumbnailUrl;
                    }
                }
            }
        }

        private async Task<List<ChatMessage>> GenerateResponsesAsync(ChatMessage message, ProcessingResult processingResult)
        {
            var responses = new List<ChatMessage>();

            if (!string.IsNullOrEmpty(processingResult.SuggestedResponse))
            {
                var responseMessage = new ChatMessage;
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = message.ConversationId,
                    SenderId = "System",
                    SenderName = "NEDA Assistant",
                    Type = MessageType.Text,
                    Content = processingResult.SuggestedResponse,
                    Timestamp = DateTime.UtcNow,
                    Status = MessageStatus.Sent,
                    ReplyToMessageId = message.Id,
                    Language = message.Language;
                };

                responses.Add(responseMessage);
            }

            // Add automated responses based on intents;
            foreach (var intent in processingResult.DetectedIntents)
            {
                if (IntentResponseMap.TryGetValue(intent, out var responseContent))
                {
                    var intentResponse = new ChatMessage;
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = message.ConversationId,
                        SenderId = "System",
                        SenderName = "NEDA Assistant",
                        Type = MessageType.Text,
                        Content = responseContent,
                        Timestamp = DateTime.UtcNow.AddSeconds(1),
                        Status = MessageStatus.Sent,
                        ReplyToMessageId = message.Id,
                        Language = message.Language,
                        Metadata = new Dictionary<string, string>
                        {
                            { "GeneratedForIntent", intent },
                            { "Confidence", processingResult.ConfidenceScore.ToString() }
                        }
                    };

                    responses.Add(intentResponse);
                }
            }

            return responses;
        }

        private static readonly Dictionary<string, string> IntentResponseMap = new Dictionary<string, string>
        {
            { "greeting", "Hello! How can I help you today?" },
            { "farewell", "Goodbye! Have a great day!" },
            { "thank_you", "You're welcome! Is there anything else I can help with?" },
            { "help_request", "I'm here to help! Please tell me what you need assistance with." },
            { "status_check", "I'm operating normally. All systems are functional." }
        };
    }

    /// <summary>
    /// Interface for message validation;
    /// </summary>
    public interface IMessageValidator;
    {
        Task<ValidationResult> ValidateAsync(ChatMessage message);
    }

    /// <summary>
    /// Validation result;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
    }

    /// <summary>
    /// Validation error details;
    /// </summary>
    public class ValidationError;
    {
        public string Field { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ValidationErrorType ErrorType { get; set; }
    }

    /// <summary>
    /// Validation error types;
    /// </summary>
    public enum ValidationErrorType;
    {
        Required,
        InvalidFormat,
        TooLong,
        TooShort,
        InvalidContent,
        SecurityViolation;
    }

    /// <summary>
    /// Interface for message processing;
    /// </summary>
    public interface IMessageProcessor;
    {
        Task<ProcessingResult> ProcessAsync(ChatMessage message);
    }

    /// <summary>
    /// Interface for message storage;
    /// </summary>
    public interface IMessageStorage;
    {
        Task StoreMessageAsync(ChatMessage message);
        Task<ChatMessage?> GetMessageAsync(string messageId);
        Task UpdateMessageAsync(ChatMessage message);
        Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, int maxMessages);
        Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(MessageSearchCriteria criteria);
    }

    /// <summary>
    /// Interface for attachment handling;
    /// </summary>
    public interface IAttachmentHandler : IDisposable
    {
        Task<AttachmentResult> ProcessAttachmentAsync(MessageAttachment attachment);
        Task<List<AttachmentResult>> UploadAttachmentsAsync(List<MessageAttachment> attachments);
        Task<bool> DeleteAttachmentAsync(string attachmentId);
    }

    /// <summary>
    /// Attachment processing result;
    /// </summary>
    public class AttachmentResult;
    {
        public bool Success { get; set; }
        public string AttachmentId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Interface for spam detection;
    /// </summary>
    public interface ISpamDetector;
    {
        Task<SpamCheckResult> CheckSpamAsync(ChatMessage message);
    }

    /// <summary>
    /// Spam check result;
    /// </summary>
    public class SpamCheckResult;
    {
        public bool IsSpam { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public List<string> DetectedPatterns { get; set; } = new List<string>();
    }

    /// <summary>
    /// Interface for message queuing;
    /// </summary>
    public interface IMessageQueue : IDisposable
    {
        Task<QueueResult> EnqueueAsync(ChatMessage message);
        Task<QueueResult> DequeueAsync();
        Task<int> GetQueueLengthAsync();
    }

    /// <summary>
    /// Queue operation result;
    /// </summary>
    public class QueueResult;
    {
        public bool Success { get; set; }
        public ChatMessage? Message { get; set; }
        public string QueueId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
