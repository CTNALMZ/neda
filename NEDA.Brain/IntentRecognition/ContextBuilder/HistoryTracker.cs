using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.KnowledgeBase.DataManagement.Repositories;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Brain.IntentRecognition.ContextBuilder;
{
    /// <summary>
    /// Kullanıcı etkileşim geçmişini, oturum durumunu ve bağlam bilgilerini yönetir.
    /// Tüm konuşma geçmişini, komutları ve kullanıcı tercihlerini takip eder.
    /// </summary>
    public interface IHistoryTracker : IDisposable
    {
        /// <summary>
        /// Kullanıcı oturumunu başlatır;
        /// </summary>
        Task<SessionContext> StartSessionAsync(string userId, string sessionId = null);

        /// <summary>
        /// Mevcut oturumu getirir;
        /// </summary>
        Task<SessionContext> GetCurrentSessionAsync();

        /// <summary>
        /// Kullanıcı etkileşimini geçmişe kaydeder;
        /// </summary>
        Task LogInteractionAsync(UserInteraction interaction);

        /// <summary>
        /// Son N etkileşimi getirir;
        /// </summary>
        Task<IEnumerable<UserInteraction>> GetRecentInteractionsAsync(int count = 10);

        /// <summary>
        /// Belirli bir türdeki son etkileşimleri getirir;
        /// </summary>
        Task<IEnumerable<UserInteraction>> GetInteractionsByTypeAsync(InteractionType type, int count = 5);

        /// <summary>
        /// Kullanıcı tercihini kaydeder;
        /// </summary>
        Task SetUserPreferenceAsync(string key, object value);

        /// <summary>
        /// Kullanıcı tercihini getirir;
        /// </summary>
        Task<T> GetUserPreferenceAsync<T>(string key, T defaultValue = default);

        /// <summary>
        /// Geçmişten pattern'ları çıkarır;
        /// </summary>
        Task<List<InteractionPattern>> ExtractPatternsAsync(TimeSpan timeRange);

        /// <summary>
        /// Oturumu sonlandırır;
        /// </summary>
        Task EndSessionAsync();

        /// <summary>
        /// Geçmişi temizler (GDPR uyumlu)
        /// </summary>
        Task ClearHistoryAsync(bool permanent = false);

        /// <summary>
        /// Bağlam tabanlı öneriler üretir;
        /// </summary>
        Task<List<ContextualSuggestion>> GenerateSuggestionsAsync();

        /// <summary>
        /// Kullanıcının alışkanlıklarını analiz eder;
        /// </summary>
        Task<UserHabits> AnalyzeHabitsAsync();

        /// <summary>
        /// Belirli bir zaman aralığındaki etkileşimleri getirir;
        /// </summary>
        Task<IEnumerable<UserInteraction>> GetInteractionsByTimeRangeAsync(DateTime start, DateTime end);

        /// <summary>
        /// Geçmişe dayalı tahminler yapar;
        /// </summary>
        Task<PredictionResult> PredictNextActionAsync();
    }

    /// <summary>
    /// Etkileşim türleri;
    /// </summary>
    public enum InteractionType;
    {
        Command,
        Question,
        Response,
        Error,
        System,
        Feedback,
        Correction,
        Suggestion,
        Confirmation,
        Navigation;
    }

    /// <summary>
    /// Kullanıcı etkileşim modeli;
    /// </summary>
    public class UserInteraction;
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonProperty("userId")]
        public string UserId { get; set; }

        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("type")]
        public InteractionType Type { get; set; }

        [JsonProperty("input")]
        public string Input { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }

        [JsonProperty("intent")]
        public string Intent { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("entities")]
        public Dictionary<string, object> Entities { get; set; } = new Dictionary<string, object>();

        [JsonProperty("context")]
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();

        [JsonProperty("metadata")]
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        [JsonProperty("processingTime")]
        public TimeSpan ProcessingTime { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("feedbackScore")]
        public int? FeedbackScore { get; set; }

        [JsonProperty("deviceInfo")]
        public DeviceInfo Device { get; set; }

        [JsonProperty("location")]
        public GeoLocation Location { get; set; }
    }

    /// <summary>
    /// Oturum bağlamı;
    /// </summary>
    public class SessionContext;
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public bool IsActive { get; set; } = true;
        public Dictionary<string, object> SessionData { get; set; } = new Dictionary<string, object>();
        public Stack<ConversationState> ConversationStack { get; set; } = new Stack<ConversationState>();
        public List<string> RecentTopics { get; set; } = new List<string>();
        public Dictionary<string, int> CommandFrequency { get; set; } = new Dictionary<string, int>();
        public UserPreferences Preferences { get; set; } = new UserPreferences();
        public PerformanceMetrics Metrics { get; set; } = new PerformanceMetrics();
    }

    /// <summary>
    /// Konuşma durumu;
    /// </summary>
    public class ConversationState;
    {
        public string Topic { get; set; }
        public DateTime Started { get; set; }
        public int TurnCount { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
        public List<UserInteraction> Interactions { get; set; } = new List<UserInteraction>();
    }

    /// <summary>
    /// Kullanıcı tercihleri;
    /// </summary>
    public class UserPreferences;
    {
        public string PreferredLanguage { get; set; } = "en-US";
        public bool UseVoiceInput { get; set; } = true;
        public bool DetailedResponses { get; set; } = false;
        public int ResponseSpeed { get; set; } = 1; // 1-5;
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
        public List<string> BlacklistedCommands { get; set; } = new List<string>();
        public List<string> FavoriteTopics { get; set; } = new List<string>();
    }

    /// <summary>
    /// Etkileşim pattern'ı;
    /// </summary>
    public class InteractionPattern;
    {
        public string PatternId { get; set; }
        public string Description { get; set; }
        public List<InteractionType> Sequence { get; set; }
        public TimeSpan AverageInterval { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, int> Frequency { get; set; }
        public DateTime FirstObserved { get; set; }
        public DateTime LastObserved { get; set; }
    }

    /// <summary>
    /// Bağlamsal öneri;
    /// </summary>
    public class ContextualSuggestion;
    {
        public string SuggestionId { get; set; }
        public string Text { get; set; }
        public SuggestionType Type { get; set; }
        public double Relevance { get; set; }
        public Dictionary<string, object> Context { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public enum SuggestionType;
    {
        Command,
        Question,
        Reminder,
        Tip,
        Warning;
    }

    /// <summary>
    /// Kullanıcı alışkanlıkları;
    /// </summary>
    public class UserHabits;
    {
        public Dictionary<string, TimePattern> TimePatterns { get; set; } = new Dictionary<string, TimePattern>();
        public List<string> CommonCommands { get; set; } = new List<string>();
        public Dictionary<string, int> TopicFrequency { get; set; } = new Dictionary<string, int>();
        public List<PeakUsageTime> PeakTimes { get; set; } = new List<PeakUsageTime>();
        public Dictionary<string, double> SuccessRates { get; set; } = new Dictionary<string, double>();
    }

    public class TimePattern;
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public DayOfWeek Day { get; set; }
        public double Probability { get; set; }
    }

    public class PeakUsageTime;
    {
        public TimeSpan TimeOfDay { get; set; }
        public DayOfWeek DayOfWeek { get; set; }
        public int InteractionCount { get; set; }
    }

    /// <summary>
    /// Tahmin sonucu;
    /// </summary>
    public class PredictionResult;
    {
        public string PredictedAction { get; set; }
        public double Confidence { get; set; }
        public List<string> Alternatives { get; set; } = new List<string>();
        public Dictionary<string, double> Probabilities { get; set; } = new Dictionary<string, double>();
        public TimeSpan ExpectedTime { get; set; }
    }

    /// <summary>
    /// Performans metrikleri;
    /// </summary>
    public class PerformanceMetrics;
    {
        public int TotalInteractions { get; set; }
        public int SuccessfulInteractions { get; set; }
        public double AverageResponseTime { get; set; }
        public Dictionary<string, int> ErrorCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, TimeSpan> CommandTimes { get; set; } = new Dictionary<string, TimeSpan>();
    }

    /// <summary>
    /// Cihaz bilgisi;
    /// </summary>
    public class DeviceInfo;
    {
        public string DeviceId { get; set; }
        public string Model { get; set; }
        public string OS { get; set; }
        public string Version { get; set; }
        public string Platform { get; set; }
    }

    /// <summary>
    /// Coğrafi konum;
    /// </summary>
    public class GeoLocation;
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Accuracy { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// HistoryTracker implementasyonu;
    /// </summary>
    public class HistoryTracker : IHistoryTracker;
    {
        private readonly ILogger<HistoryTracker> _logger;
        private readonly IRepository<UserInteraction> _interactionRepository;
        private readonly IRepository<SessionContext> _sessionRepository;
        private readonly IRepository<UserPreferences> _preferenceRepository;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IEventBus _eventBus;
        private readonly MemoryCacheManager _cache;

        private SessionContext _currentSession;
        private bool _disposed = false;
        private readonly object _lock = new object();
        private readonly int _maxRecentInteractions = 100;
        private readonly Queue<UserInteraction> _recentInteractions = new Queue<UserInteraction>();

        public HistoryTracker(
            ILogger<HistoryTracker> logger,
            IRepository<UserInteraction> interactionRepository,
            IRepository<SessionContext> sessionRepository,
            IRepository<UserPreferences> preferenceRepository,
            IDiagnosticTool diagnosticTool,
            IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _interactionRepository = interactionRepository ?? throw new ArgumentNullException(nameof(interactionRepository));
            _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
            _preferenceRepository = preferenceRepository ?? throw new ArgumentNullException(nameof(preferenceRepository));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _cache = new MemoryCacheManager(TimeSpan.FromMinutes(30));

            _logger.LogInformation("HistoryTracker initialized");
        }

        public async Task<SessionContext> StartSessionAsync(string userId, string sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                // Mevcut oturumu sonlandır (eğer varsa)
                if (_currentSession != null && _currentSession.IsActive)
                {
                    await EndSessionAsync();
                }

                // Yeni oturum oluştur;
                _currentSession = new SessionContext;
                {
                    SessionId = sessionId ?? Guid.NewGuid().ToString(),
                    UserId = userId,
                    StartTime = DateTime.UtcNow,
                    IsActive = true;
                };

                // Kullanıcı tercihlerini yükle;
                var preferences = await _preferenceRepository.GetByIdAsync(userId);
                if (preferences != null)
                {
                    _currentSession.Preferences = preferences;
                }

                // Oturumu veritabanına kaydet;
                await _sessionRepository.AddAsync(_currentSession);
                await _sessionRepository.SaveChangesAsync();

                // Olay yayınla;
                await _eventBus.PublishAsync(new SessionStartedEvent;
                {
                    SessionId = _currentSession.SessionId,
                    UserId = userId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Session started: {SessionId} for user {UserId}",
                    _currentSession.SessionId, userId);

                return _currentSession;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start session for user {UserId}", userId);
                throw new HistoryTrackerException("Failed to start session", ex, ErrorCodes.HistoryTracker.SessionStartFailed);
            }
        }

        public async Task<SessionContext> GetCurrentSessionAsync()
        {
            if (_currentSession == null || !_currentSession.IsActive)
            {
                throw new InvalidOperationException("No active session found");
            }

            // Oturum verilerini güncelle;
            _currentSession.Metrics.TotalInteractions = _recentInteractions.Count;
            _currentSession.Metrics.SuccessfulInteractions = _recentInteractions.Count(i => i.Success);

            if (_recentInteractions.Any())
            {
                _currentSession.Metrics.AverageResponseTime = TimeSpan.FromMilliseconds(
                    _recentInteractions.Average(i => i.ProcessingTime.TotalMilliseconds));
            }

            return _currentSession;
        }

        public async Task LogInteractionAsync(UserInteraction interaction)
        {
            if (interaction == null)
                throw new ArgumentNullException(nameof(interaction));

            if (_currentSession == null || !_currentSession.IsActive)
            {
                throw new InvalidOperationException("Cannot log interaction without active session");
            }

            try
            {
                // Oturum bilgilerini ekle;
                interaction.SessionId = _currentSession.SessionId;
                interaction.UserId = _currentSession.UserId;

                // Performans ölçümü;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Cache'e ekle;
                lock (_lock)
                {
                    _recentInteractions.Enqueue(interaction);

                    // Queue boyutunu kontrol et;
                    if (_recentInteractions.Count > _maxRecentInteractions)
                    {
                        _recentInteractions.Dequeue();
                    }
                }

                // Veritabanına kaydet;
                await _interactionRepository.AddAsync(interaction);
                await _interactionRepository.SaveChangesAsync();

                // Oturum metriklerini güncelle;
                UpdateSessionMetrics(interaction);

                // Pattern analizi için olay tetikle;
                await _eventBus.PublishAsync(new InteractionLoggedEvent;
                {
                    Interaction = interaction,
                    SessionId = _currentSession.SessionId;
                });

                // Cache'i temizle;
                _cache.Remove($"recent_interactions_{_currentSession.UserId}");
                _cache.Remove($"patterns_{_currentSession.UserId}");

                stopwatch.Stop();
                _logger.LogDebug("Interaction logged in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log interaction");
                throw new HistoryTrackerException("Failed to log interaction", ex, ErrorCodes.HistoryTracker.LoggingFailed);
            }
        }

        private void UpdateSessionMetrics(UserInteraction interaction)
        {
            if (_currentSession == null) return;

            // Komut sıklığını güncelle;
            if (!string.IsNullOrEmpty(interaction.Intent))
            {
                if (_currentSession.CommandFrequency.ContainsKey(interaction.Intent))
                {
                    _currentSession.CommandFrequency[interaction.Intent]++;
                }
                else;
                {
                    _currentSession.CommandFrequency[interaction.Intent] = 1;
                }
            }

            // Hata sayılarını güncelle;
            if (!interaction.Success && !string.IsNullOrEmpty(interaction.Error))
            {
                var errorType = interaction.Error.Split(':').FirstOrDefault() ?? "Unknown";
                if (_currentSession.Metrics.ErrorCounts.ContainsKey(errorType))
                {
                    _currentSession.Metrics.ErrorCounts[errorType]++;
                }
                else;
                {
                    _currentSession.Metrics.ErrorCounts[errorType] = 1;
                }
            }

            // İşlem sürelerini güncelle;
            var commandKey = interaction.Intent ?? "unknown";
            _currentSession.Metrics.CommandTimes[commandKey] = interaction.ProcessingTime;

            // Konuşma stack'ini güncelle;
            if (_currentSession.ConversationStack.Count > 0)
            {
                var currentConversation = _currentSession.ConversationStack.Peek();
                currentConversation.Interactions.Add(interaction);
                currentConversation.TurnCount++;
            }
        }

        public async Task<IEnumerable<UserInteraction>> GetRecentInteractionsAsync(int count = 10)
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            var cacheKey = $"recent_interactions_{_currentSession.UserId}_{count}";

            return await _cache.GetOrCreateAsync(cacheKey, async () =>
            {
                try
                {
                    // Önce memory cache'den kontrol et;
                    lock (_lock)
                    {
                        var recent = _recentInteractions.Take(count).ToList();
                        if (recent.Count >= count)
                        {
                            return recent;
                        }
                    }

                    // Veritabanından getir;
                    var interactions = await _interactionRepository.FindAsync(
                        filter: i => i.UserId == _currentSession.UserId && i.SessionId == _currentSession.SessionId,
                        orderBy: q => q.OrderByDescending(i => i.Timestamp),
                        take: count);

                    return interactions.ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get recent interactions");
                    throw new HistoryTrackerException("Failed to get recent interactions", ex,
                        ErrorCodes.HistoryTracker.RetrievalFailed);
                }
            }, TimeSpan.FromMinutes(5));
        }

        public async Task<IEnumerable<UserInteraction>> GetInteractionsByTypeAsync(InteractionType type, int count = 5)
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            try
            {
                return await _interactionRepository.FindAsync(
                    filter: i => i.UserId == _currentSession.UserId &&
                                i.SessionId == _currentSession.SessionId &&
                                i.Type == type,
                    orderBy: q => q.OrderByDescending(i => i.Timestamp),
                    take: count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get interactions by type {Type}", type);
                throw new HistoryTrackerException($"Failed to get interactions by type {type}", ex,
                    ErrorCodes.HistoryTracker.RetrievalFailed);
            }
        }

        public async Task SetUserPreferenceAsync(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Preference key cannot be null or empty", nameof(key));

            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            try
            {
                // Session preferences'i güncelle;
                _currentSession.Preferences.CustomSettings[key] = value;

                // Persistent storage'a kaydet;
                var preferences = await _preferenceRepository.GetByIdAsync(_currentSession.UserId);
                if (preferences == null)
                {
                    preferences = new UserPreferences;
                    {
                        PreferredLanguage = _currentSession.Preferences.PreferredLanguage;
                    };
                    await _preferenceRepository.AddAsync(preferences);
                }

                preferences.CustomSettings[key] = value;
                await _preferenceRepository.UpdateAsync(preferences);
                await _preferenceRepository.SaveChangesAsync();

                // Cache'i temizle;
                _cache.Remove($"preferences_{_currentSession.UserId}");

                _logger.LogInformation("Preference set: {Key} = {Value}", key, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set preference {Key}", key);
                throw new HistoryTrackerException($"Failed to set preference {key}", ex,
                    ErrorCodes.HistoryTracker.PreferenceUpdateFailed);
            }
        }

        public async Task<T> GetUserPreferenceAsync<T>(string key, T defaultValue = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Preference key cannot be null or empty", nameof(key));

            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            var cacheKey = $"preference_{_currentSession.UserId}_{key}";

            return await _cache.GetOrCreateAsync(cacheKey, async () =>
            {
                try
                {
                    // Önce session'dan kontrol et;
                    if (_currentSession.Preferences.CustomSettings.TryGetValue(key, out var sessionValue))
                    {
                        return (T)Convert.ChangeType(sessionValue, typeof(T));
                    }

                    // Veritabanından getir;
                    var preferences = await _preferenceRepository.GetByIdAsync(_currentSession.UserId);
                    if (preferences != null && preferences.CustomSettings.TryGetValue(key, out var dbValue))
                    {
                        _currentSession.Preferences.CustomSettings[key] = dbValue;
                        return (T)Convert.ChangeType(dbValue, typeof(T));
                    }

                    return defaultValue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get preference {Key}", key);
                    return defaultValue;
                }
            }, TimeSpan.FromMinutes(10));
        }

        public async Task<List<InteractionPattern>> ExtractPatternsAsync(TimeSpan timeRange)
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            var cacheKey = $"patterns_{_currentSession.UserId}_{timeRange.TotalMinutes}";

            return await _cache.GetOrCreateAsync(cacheKey, async () =>
            {
                try
                {
                    var cutoffTime = DateTime.UtcNow - timeRange;
                    var interactions = await _interactionRepository.FindAsync(
                        filter: i => i.UserId == _currentSession.UserId &&
                                    i.Timestamp >= cutoffTime,
                        orderBy: q => q.OrderBy(i => i.Timestamp));

                    var patterns = new List<InteractionPattern>();
                    var interactionList = interactions.ToList();

                    if (!interactionList.Any())
                        return patterns;

                    // Sequence pattern'larını bul;
                    var sequences = FindSequences(interactionList);

                    // Time pattern'larını bul;
                    var timePatterns = FindTimePatterns(interactionList);

                    // Command pattern'larını bul;
                    var commandPatterns = FindCommandPatterns(interactionList);

                    patterns.AddRange(sequences);
                    patterns.AddRange(timePatterns);
                    patterns.AddRange(commandPatterns);

                    return patterns;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to extract patterns");
                    throw new HistoryTrackerException("Failed to extract patterns", ex,
                        ErrorCodes.HistoryTracker.PatternExtractionFailed);
                }
            }, TimeSpan.FromMinutes(15));
        }

        private List<InteractionPattern> FindSequences(List<UserInteraction> interactions)
        {
            var patterns = new List<InteractionPattern>();

            // 3 veya daha fazla etkileşimli sequence'ları bul;
            for (int i = 0; i < interactions.Count - 2; i++)
            {
                var sequence = interactions.Skip(i).Take(3).Select(x => x.Type).ToList();

                // Aynı sequence'ı ara;
                var occurrences = new List<int>();
                for (int j = i + 1; j < interactions.Count - 2; j++)
                {
                    var testSequence = interactions.Skip(j).Take(3).Select(x => x.Type).ToList();
                    if (sequence.SequenceEqual(testSequence))
                    {
                        occurrences.Add(j);
                    }
                }

                if (occurrences.Count >= 2) // En az 3 kere görülmüş;
                {
                    var pattern = new InteractionPattern;
                    {
                        PatternId = $"SEQ_{Guid.NewGuid():N}",
                        Description = $"Sequence pattern: {string.Join(" -> ", sequence)}",
                        Sequence = sequence,
                        Confidence = occurrences.Count / (double)interactions.Count,
                        FirstObserved = interactions[i].Timestamp,
                        LastObserved = interactions[occurrences.Last() + 2].Timestamp;
                    };

                    patterns.Add(pattern);
                }
            }

            return patterns.DistinctBy(p => string.Join(",", p.Sequence)).ToList();
        }

        private List<InteractionPattern> FindTimePatterns(List<UserInteraction> interactions)
        {
            var patterns = new List<InteractionPattern>();

            // Günün saatlerine göre grupla;
            var hourlyGroups = interactions;
                .GroupBy(i => i.Timestamp.Hour)
                .Where(g => g.Count() > 5); // En az 5 etkileşim;

            foreach (var group in hourlyGroups)
            {
                var pattern = new InteractionPattern;
                {
                    PatternId = $"TIME_{Guid.NewGuid():N}",
                    Description = $"Hourly pattern around {group.Key}:00",
                    Confidence = group.Count() / (double)interactions.Count,
                    FirstObserved = group.Min(i => i.Timestamp),
                    LastObserved = group.Max(i => i.Timestamp),
                    Frequency = new Dictionary<string, int>
                    {
                        ["hour"] = group.Key,
                        ["count"] = group.Count()
                    }
                };

                patterns.Add(pattern);
            }

            return patterns;
        }

        private List<InteractionPattern> FindCommandPatterns(List<UserInteraction> interactions)
        {
            var patterns = new List<InteractionPattern>();

            // Komut kombinasyonlarını bul;
            var commandGroups = interactions;
                .Where(i => !string.IsNullOrEmpty(i.Intent))
                .GroupBy(i => i.Intent)
                .Where(g => g.Count() > 3); // En az 3 kere kullanılmış;

            foreach (var group in commandGroups)
            {
                var relatedCommands = interactions;
                    .Where(i => i.Timestamp >= group.Min(x => x.Timestamp) - TimeSpan.FromMinutes(5) &&
                               i.Timestamp <= group.Max(x => x.Timestamp) + TimeSpan.FromMinutes(5) &&
                               i.Intent != group.Key)
                    .Select(i => i.Intent)
                    .Where(i => !string.IsNullOrEmpty(i))
                    .Distinct()
                    .ToList();

                if (relatedCommands.Any())
                {
                    var pattern = new InteractionPattern;
                    {
                        PatternId = $"CMD_{Guid.NewGuid():N}",
                        Description = $"Command '{group.Key}' often used with: {string.Join(", ", relatedCommands.Take(3))}",
                        Confidence = group.Count() / (double)interactions.Count(i => !string.IsNullOrEmpty(i.Intent)),
                        FirstObserved = group.Min(i => i.Timestamp),
                        LastObserved = group.Max(i => i.Timestamp),
                        Frequency = new Dictionary<string, int>
                        {
                            ["command"] = group.Count(),
                            ["related_count"] = relatedCommands.Count;
                        }
                    };

                    patterns.Add(pattern);
                }
            }

            return patterns;
        }

        public async Task EndSessionAsync()
        {
            if (_currentSession == null || !_currentSession.IsActive)
                return;

            try
            {
                _currentSession.EndTime = DateTime.UtcNow;
                _currentSession.IsActive = false;

                // Oturumu veritabanında güncelle;
                await _sessionRepository.UpdateAsync(_currentSession);
                await _sessionRepository.SaveChangesAsync();

                // Analytics verilerini topla;
                await GenerateSessionAnalyticsAsync();

                // Olay yayınla;
                await _eventBus.PublishAsync(new SessionEndedEvent;
                {
                    SessionId = _currentSession.SessionId,
                    UserId = _currentSession.UserId,
                    StartTime = _currentSession.StartTime,
                    EndTime = _currentSession.EndTime.Value,
                    Duration = _currentSession.EndTime.Value - _currentSession.StartTime,
                    InteractionCount = _currentSession.Metrics.TotalInteractions;
                });

                _logger.LogInformation("Session ended: {SessionId} with {Count} interactions",
                    _currentSession.SessionId, _currentSession.Metrics.TotalInteractions);

                _currentSession = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to end session");
                throw new HistoryTrackerException("Failed to end session", ex,
                    ErrorCodes.HistoryTracker.SessionEndFailed);
            }
        }

        private async Task GenerateSessionAnalyticsAsync()
        {
            try
            {
                // Alışkanlık analizi;
                var habits = await AnalyzeHabitsAsync();

                // Pattern extraction;
                var patterns = await ExtractPatternsAsync(TimeSpan.FromDays(7));

                // Öneri üret;
                var suggestions = await GenerateSuggestionsAsync();

                // Analytics verilerini kaydet;
                var analytics = new;
                {
                    SessionId = _currentSession.SessionId,
                    UserId = _currentSession.UserId,
                    Habits = habits,
                    Patterns = patterns.Take(5).Select(p => p.Description),
                    Suggestions = suggestions.Take(3).Select(s => s.Text),
                    Metrics = _currentSession.Metrics;
                };

                _logger.LogInformation("Session analytics: {@Analytics}", analytics);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate session analytics");
            }
        }

        public async Task ClearHistoryAsync(bool permanent = false)
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            try
            {
                if (permanent)
                {
                    // Kalıcı silme (GDPR uyumlu)
                    var interactions = await _interactionRepository.FindAsync(
                        filter: i => i.UserId == _currentSession.UserId);

                    foreach (var interaction in interactions)
                    {
                        await _interactionRepository.DeleteAsync(interaction.Id);
                    }

                    await _interactionRepository.SaveChangesAsync();

                    _logger.LogWarning("Permanently cleared history for user {UserId}", _currentSession.UserId);
                }
                else;
                {
                    // Sadece cache temizleme;
                    lock (_lock)
                    {
                        _recentInteractions.Clear();
                    }

                    _cache.Clear();

                    _logger.LogInformation("Cleared cached history for user {UserId}", _currentSession.UserId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear history");
                throw new HistoryTrackerException("Failed to clear history", ex,
                    ErrorCodes.HistoryTracker.ClearFailed);
            }
        }

        public async Task<List<ContextualSuggestion>> GenerateSuggestionsAsync()
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            var suggestions = new List<ContextualSuggestion>();

            try
            {
                // Son etkileşimlere bak;
                var recentInteractions = await GetRecentInteractionsAsync(10);
                var recentList = recentInteractions.ToList();

                if (!recentList.Any())
                    return suggestions;

                // 1. Sık kullanılan komutlara dayalı öneriler;
                var frequentCommands = _currentSession.CommandFrequency;
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => kv.Key);

                foreach (var command in frequentCommands)
                {
                    suggestions.Add(new ContextualSuggestion;
                    {
                        SuggestionId = $"CMD_{Guid.NewGuid():N}",
                        Text = $"Would you like to use the '{command}' command again?",
                        Type = SuggestionType.Command,
                        Relevance = 0.8,
                        ExpiresAt = DateTime.UtcNow.AddHours(1)
                    });
                }

                // 2. Hatalara dayalı öneriler;
                var recentErrors = recentList.Where(i => !i.Success).ToList();
                if (recentErrors.Any())
                {
                    var commonError = recentErrors.GroupBy(e => e.Error)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault();

                    if (commonError != null)
                    {
                        suggestions.Add(new ContextualSuggestion;
                        {
                            SuggestionId = $"ERR_{Guid.NewGuid():N}",
                            Text = $"I noticed an error with '{commonError.Key}'. Would you like help with that?",
                            Type = SuggestionType.Tip,
                            Relevance = 0.9,
                            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
                        });
                    }
                }

                // 3. Zaman bazlı öneriler;
                var hour = DateTime.UtcNow.Hour;
                if (hour >= 9 && hour <= 17) // Çalışma saatleri;
                {
                    suggestions.Add(new ContextualSuggestion;
                    {
                        SuggestionId = $"TIME_{Guid.NewGuid():N}",
                        Text = "It's work hours. Need help with any productivity tasks?",
                        Type = SuggestionType.Question,
                        Relevance = 0.6,
                        ExpiresAt = DateTime.UtcNow.AddHours(2)
                    });
                }

                // 4. Pattern bazlı öneriler;
                var patterns = await ExtractPatternsAsync(TimeSpan.FromDays(1));
                foreach (var pattern in patterns.Take(2))
                {
                    suggestions.Add(new ContextualSuggestion;
                    {
                        SuggestionId = $"PAT_{Guid.NewGuid():N}",
                        Text = $"You usually do this sequence around this time. Continue?",
                        Type = SuggestionType.Reminder,
                        Relevance = pattern.Confidence,
                        Context = new Dictionary<string, object> { ["pattern"] = pattern.Description },
                        ExpiresAt = DateTime.UtcNow.AddMinutes(15)
                    });
                }

                return suggestions.OrderByDescending(s => s.Relevance).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate suggestions");
                return suggestions;
            }
        }

        public async Task<UserHabits> AnalyzeHabitsAsync()
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            try
            {
                var lastWeekInteractions = await GetInteractionsByTimeRangeAsync(
                    DateTime.UtcNow.AddDays(-7),
                    DateTime.UtcNow);

                var interactions = lastWeekInteractions.ToList();

                if (!interactions.Any())
                    return new UserHabits();

                var habits = new UserHabits();

                // Zaman pattern'larını analiz et;
                var hourlyGroups = interactions;
                    .GroupBy(i => new { i.Timestamp.Hour, i.Timestamp.DayOfWeek })
                    .Where(g => g.Count() > 2);

                foreach (var group in hourlyGroups)
                {
                    habits.TimePatterns[$"{group.Key.DayOfWeek}_{group.Key.Hour}"] = new TimePattern;
                    {
                        Day = group.Key.DayOfWeek,
                        StartTime = TimeSpan.FromHours(group.Key.Hour),
                        EndTime = TimeSpan.FromHours(group.Key.Hour + 1),
                        Probability = group.Count() / (double)interactions.Count;
                    };
                }

                // Tepe kullanım zamanlarını bul;
                var peakTimes = interactions;
                    .GroupBy(i => new { i.Timestamp.Hour, i.Timestamp.DayOfWeek })
                    .Select(g => new PeakUsageTime;
                    {
                        TimeOfDay = TimeSpan.FromHours(g.Key.Hour),
                        DayOfWeek = g.Key.DayOfWeek,
                        InteractionCount = g.Count()
                    })
                    .OrderByDescending(p => p.InteractionCount)
                    .Take(5)
                    .ToList();

                habits.PeakTimes = peakTimes;

                // Sık kullanılan komutları bul;
                var commandFreq = interactions;
                    .Where(i => !string.IsNullOrEmpty(i.Intent))
                    .GroupBy(i => i.Intent)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => g.Key)
                    .ToList();

                habits.CommonCommands = commandFreq;

                // Konu frekanslarını hesapla;
                var topics = interactions;
                    .Where(i => i.Entities.ContainsKey("topic"))
                    .Select(i => i.Entities["topic"].ToString())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .GroupBy(t => t)
                    .ToDictionary(g => g.Key, g => g.Count());

                habits.TopicFrequency = topics;

                // Başarı oranlarını hesapla;
                var commandGroups = interactions;
                    .Where(i => !string.IsNullOrEmpty(i.Intent))
                    .GroupBy(i => i.Intent);

                foreach (var group in commandGroups)
                {
                    var successRate = group.Count(i => i.Success) / (double)group.Count();
                    habits.SuccessRates[group.Key] = successRate;
                }

                return habits;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze habits");
                throw new HistoryTrackerException("Failed to analyze habits", ex,
                    ErrorCodes.HistoryTracker.HabitAnalysisFailed);
            }
        }

        public async Task<IEnumerable<UserInteraction>> GetInteractionsByTimeRangeAsync(DateTime start, DateTime end)
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            try
            {
                return await _interactionRepository.FindAsync(
                    filter: i => i.UserId == _currentSession.UserId &&
                                i.Timestamp >= start &&
                                i.Timestamp <= end,
                    orderBy: q => q.OrderByDescending(i => i.Timestamp));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get interactions by time range");
                throw new HistoryTrackerException("Failed to get interactions by time range", ex,
                    ErrorCodes.HistoryTracker.RetrievalFailed);
            }
        }

        public async Task<PredictionResult> PredictNextActionAsync()
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session");

            try
            {
                var recentInteractions = await GetRecentInteractionsAsync(20);
                var recentList = recentInteractions.ToList();

                if (!recentList.Any())
                    return new PredictionResult();

                var predictions = new Dictionary<string, double>();
                var alternatives = new List<string>();

                // 1. Sequence pattern'larına göre tahmin;
                var patterns = await ExtractPatternsAsync(TimeSpan.FromHours(1));
                foreach (var pattern in patterns)
                {
                    if (pattern.Sequence.Count >= 2)
                    {
                        var lastTwo = recentList.TakeLast(2).Select(i => i.Type).ToList();
                        if (pattern.Sequence.Take(2).SequenceEqual(lastTwo))
                        {
                            var nextAction = pattern.Sequence[2];
                            predictions[nextAction.ToString()] = pattern.Confidence;
                            alternatives.Add($"Continue {pattern.Description}");
                        }
                    }
                }

                // 2. Zaman bazlı tahmin;
                var hour = DateTime.UtcNow.Hour;
                var day = DateTime.UtcNow.DayOfWeek;

                var timeBasedAction = GetTimeBasedPrediction(hour, day);
                if (!string.IsNullOrEmpty(timeBasedAction))
                {
                    predictions[timeBasedAction] = 0.7;
                }

                // 3. Sıklık bazlı tahmin;
                var mostFrequent = _currentSession.CommandFrequency;
                    .OrderByDescending(kv => kv.Value)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(mostFrequent.Key))
                {
                    predictions[mostFrequent.Key] = 0.6;
                }

                if (!predictions.Any())
                    return new PredictionResult();

                var topPrediction = predictions.OrderByDescending(kv => kv.Value).First();

                return new PredictionResult;
                {
                    PredictedAction = topPrediction.Key,
                    Confidence = topPrediction.Value,
                    Alternatives = alternatives,
                    Probabilities = predictions,
                    ExpectedTime = TimeSpan.FromSeconds(30) // Ortalama bekleme süresi;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to predict next action");
                return new PredictionResult();
            }
        }

        private string GetTimeBasedPrediction(int hour, DayOfWeek day)
        {
            // Basit zaman bazlı tahmin kuralları;
            if (hour >= 9 && hour <= 12 && day >= DayOfWeek.Monday && day <= DayOfWeek.Friday)
            {
                return "work_start";
            }
            else if (hour >= 13 && hour <= 14)
            {
                return "lunch_break";
            }
            else if (hour >= 18 && hour <= 20)
            {
                return "evening_routine";
            }
            else if (hour >= 22 || hour <= 6)
            {
                return "night_mode";
            }

            return null;
        }

        #region Memory Cache Manager;
        private class MemoryCacheManager;
        {
            private readonly Dictionary<string, CacheItem> _cache = new Dictionary<string, CacheItem>();
            private readonly TimeSpan _defaultExpiration;
            private readonly object _cacheLock = new object();

            public MemoryCacheManager(TimeSpan defaultExpiration)
            {
                _defaultExpiration = defaultExpiration;
            }

            public T GetOrCreate<T>(string key, Func<T> factory, TimeSpan? expiration = null)
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(key, out var item) && !item.IsExpired)
                    {
                        return (T)item.Value;
                    }

                    var value = factory();
                    Set(key, value, expiration);
                    return value;
                }
            }

            public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null)
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(key, out var item) && !item.IsExpired)
                    {
                        return (T)item.Value;
                    }
                }

                var value = await factory();
                Set(key, value, expiration);
                return value;
            }

            public void Set<T>(string key, T value, TimeSpan? expiration = null)
            {
                lock (_cacheLock)
                {
                    _cache[key] = new CacheItem;
                    {
                        Value = value,
                        Expiration = DateTime.UtcNow.Add(expiration ?? _defaultExpiration)
                    };
                }
            }

            public bool Remove(string key)
            {
                lock (_cacheLock)
                {
                    return _cache.Remove(key);
                }
            }

            public void Clear()
            {
                lock (_cacheLock)
                {
                    _cache.Clear();
                }
            }

            private class CacheItem;
            {
                public object Value { get; set; }
                public DateTime Expiration { get; set; }
                public bool IsExpired => DateTime.UtcNow > Expiration;
            }
        }
        #endregion;

        #region Events;
        public class SessionStartedEvent : IEvent;
        {
            public string SessionId { get; set; }
            public string UserId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class SessionEndedEvent : IEvent;
        {
            public string SessionId { get; set; }
            public string UserId { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public int InteractionCount { get; set; }
        }

        public class InteractionLoggedEvent : IEvent;
        {
            public UserInteraction Interaction { get; set; }
            public string SessionId { get; set; }
        }
        #endregion;

        #region Error Codes;
        public static class ErrorCodes;
        {
            public const string SessionStartFailed = "HISTORY_001";
            public const string SessionEndFailed = "HISTORY_002";
            public const string LoggingFailed = "HISTORY_003";
            public const string RetrievalFailed = "HISTORY_004";
            public const string PreferenceUpdateFailed = "HISTORY_005";
            public const string PatternExtractionFailed = "HISTORY_006";
            public const string ClearFailed = "HISTORY_007";
            public const string HabitAnalysisFailed = "HISTORY_008";
        }
        #endregion;

        #region Exceptions;
        public class HistoryTrackerException : Exception
        {
            public string ErrorCode { get; }

            public HistoryTrackerException(string message, Exception innerException, string errorCode)
                : base(message, innerException)
            {
                ErrorCode = errorCode;
            }

            public HistoryTrackerException(string message, string errorCode)
                : base(message)
            {
                ErrorCode = errorCode;
            }
        }
        #endregion;

        #region IDisposable;
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
                    try
                    {
                        if (_currentSession?.IsActive == true)
                        {
                            EndSessionAsync().Wait(TimeSpan.FromSeconds(5));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during disposal");
                    }

                    _cache?.Clear();
                }
                _disposed = true;
            }
        }

        ~HistoryTracker()
        {
            Dispose(false);
        }
        #endregion;
    }
}
