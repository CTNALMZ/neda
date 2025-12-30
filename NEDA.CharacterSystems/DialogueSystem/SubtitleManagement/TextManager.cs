using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.CharacterSystems.DialogueSystem.SubtitleManagement.Contracts;

namespace NEDA.CharacterSystems.DialogueSystem.SubtitleManagement;
{
    /// <summary>
    /// Altyazı metinlerini yöneten, zamanlama kontrolü sağlayan ve metin işleme operasyonlarını gerçekleştiren sınıf.
    /// </summary>
    public class TextManager : ITextManager, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly ITimingController _timingController;
        private readonly SubtitleConfiguration _configuration;
        private readonly Dictionary<string, SubtitleTrack> _activeTracks;
        private readonly List<SubtitleEvent> _subtitleHistory;
        private readonly ReaderWriterLockSlim _lock;
        private readonly Timer _cleanupTimer;
        private bool _isDisposed;

        #endregion;

        #region Events;

        /// <summary>
        /// Yeni altyazı eklendiğinde tetiklenir.
        /// </summary>
        public event EventHandler<SubtitleAddedEventArgs> SubtitleAdded;

        /// <summary>
        /// Altyazı kaldırıldığında tetiklenir.
        /// </summary>
        public event EventHandler<SubtitleRemovedEventArgs> SubtitleRemoved;

        /// <summary>
        /// Altyazı güncellendiğinde tetiklenir.
        /// </summary>
        public event EventHandler<SubtitleUpdatedEventArgs> SubtitleUpdated;

        /// <summary>
        /// Altyazı görüntüleme süresi dolduğunda tetiklenir.
        /// </summary>
        public event EventHandler<SubtitleExpiredEventArgs> SubtitleExpired;

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif altyazı sayısını getirir.
        /// </summary>
        public int ActiveSubtitleCount;
        {
            get;
            {
                _lock.EnterReadLock();
                try
                {
                    return _activeTracks.Values.Sum(track => track.Subtitles.Count);
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Maksimum eşzamanlı altyazı sayısı.
        /// </summary>
        public int MaxConcurrentSubtitles => _configuration.MaxConcurrentSubtitles;

        /// <summary>
        /// Varsayılan altyazı görüntüleme süresi (milisaniye).
        /// </summary>
        public int DefaultDisplayDuration => _configuration.DefaultDisplayDuration;

        /// <summary>
        /// Altyazı geçmişi boyutu.
        /// </summary>
        public int HistorySize => _configuration.HistorySize;

        #endregion;

        #region Constructor;

        /// <summary>
        /// TextManager sınıfının yeni bir örneğini oluşturur.
        /// </summary>
        /// <param name="logger">Loglama için kullanılacak logger</param>
        /// <param name="timingController">Zamanlama kontrolü için timing controller</param>
        /// <param name="configuration">Altyazı konfigürasyonu</param>
        public TextManager(ILogger logger, ITimingController timingController, SubtitleConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timingController = timingController ?? throw new ArgumentNullException(nameof(timingController));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _activeTracks = new Dictionary<string, SubtitleTrack>();
            _subtitleHistory = new List<SubtitleEvent>(_configuration.HistorySize);
            _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

            // Temizleme timer'ını başlat;
            _cleanupTimer = new Timer(CleanupExpiredSubtitles, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            _logger.LogInformation("TextManager initialized with {MaxSubtitles} max concurrent subtitles.",
                _configuration.MaxConcurrentSubtitles);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Yeni bir altyazı ekler.
        /// </summary>
        /// <param name="subtitle">Eklenecek altyazı</param>
        /// <param name="trackId">Altyazı track ID'si</param>
        /// <returns>Eklenen altyazının ID'si</returns>
        public async Task<string> AddSubtitleAsync(Subtitle subtitle, string trackId = "default")
        {
            ValidateSubtitle(subtitle);

            if (string.IsNullOrWhiteSpace(trackId))
                trackId = "default";

            _lock.EnterWriteLock();
            try
            {
                // Maksimum altyazı kontrolü;
                if (ActiveSubtitleCount >= _configuration.MaxConcurrentSubtitles)
                {
                    var oldestSubtitle = FindOldestSubtitle();
                    if (oldestSubtitle != null)
                    {
                        RemoveSubtitleInternal(oldestSubtitle.TrackId, oldestSubtitle.Id);
                    }
                }

                // Track yoksa oluştur;
                if (!_activeTracks.ContainsKey(trackId))
                {
                    _activeTracks[trackId] = new SubtitleTrack(trackId);
                }

                var track = _activeTracks[trackId];

                // Altyazıyı ekle;
                subtitle.Id = Guid.NewGuid().ToString();
                subtitle.CreatedAt = DateTime.UtcNow;
                subtitle.ExpiresAt = DateTime.UtcNow.AddMilliseconds(
                    subtitle.DisplayDuration > 0 ? subtitle.DisplayDuration : _configuration.DefaultDisplayDuration);

                track.Subtitles[subtitle.Id] = subtitle;

                // Geçmişe ekle;
                AddToHistory(new SubtitleEvent;
                {
                    EventType = SubtitleEventType.Added,
                    SubtitleId = subtitle.Id,
                    TrackId = trackId,
                    Timestamp = DateTime.UtcNow,
                    Text = subtitle.Text;
                });

                _logger.LogDebug("Subtitle added: {SubtitleId} to track {TrackId}", subtitle.Id, trackId);

                // Event tetikle;
                OnSubtitleAdded(new SubtitleAddedEventArgs;
                {
                    Subtitle = subtitle,
                    TrackId = trackId,
                    Timestamp = DateTime.UtcNow;
                });

                return subtitle.Id;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Altyazıyı kaldırır.
        /// </summary>
        /// <param name="trackId">Track ID'si</param>
        /// <param name="subtitleId">Altyazı ID'si</param>
        /// <returns>İşlem başarılı mı?</returns>
        public bool RemoveSubtitle(string trackId, string subtitleId)
        {
            _lock.EnterWriteLock();
            try
            {
                return RemoveSubtitleInternal(trackId, subtitleId);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Belirtilen track'taki tüm altyazıları kaldırır.
        /// </summary>
        /// <param name="trackId">Track ID'si</param>
        /// <returns>Kaldırılan altyazı sayısı</returns>
        public int ClearTrack(string trackId)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_activeTracks.TryGetValue(trackId, out var track))
                {
                    var count = track.Subtitles.Count;

                    foreach (var subtitle in track.Subtitles.Values.ToList())
                    {
                        RemoveSubtitleInternal(trackId, subtitle.Id);
                    }

                    _logger.LogInformation("Cleared track {TrackId}: {Count} subtitles removed", trackId, count);
                    return count;
                }

                return 0;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Tüm altyazıları kaldırır.
        /// </summary>
        /// <returns>Toplam kaldırılan altyazı sayısı</returns>
        public int ClearAllSubtitles()
        {
            _lock.EnterWriteLock();
            try
            {
                var totalCount = 0;
                var trackIds = _activeTracks.Keys.ToList();

                foreach (var trackId in trackIds)
                {
                    totalCount += ClearTrack(trackId);
                }

                _logger.LogInformation("Cleared all subtitles: {TotalCount} removed", totalCount);
                return totalCount;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Altyazıyı günceller.
        /// </summary>
        /// <param name="trackId">Track ID'si</param>
        /// <param name="subtitleId">Altyazı ID'si</param>
        /// <param name="newText">Yeni metin</param>
        /// <param name="displayDuration">Yeni görüntüleme süresi (ms)</param>
        /// <returns>Güncellenen altyazı</returns>
        public Subtitle UpdateSubtitle(string trackId, string subtitleId, string newText, int displayDuration = 0)
        {
            ValidateText(newText);

            _lock.EnterWriteLock();
            try
            {
                if (_activeTracks.TryGetValue(trackId, out var track))
                {
                    if (track.Subtitles.TryGetValue(subtitleId, out var subtitle))
                    {
                        var oldText = subtitle.Text;
                        subtitle.Text = newText;

                        if (displayDuration > 0)
                        {
                            subtitle.DisplayDuration = displayDuration;
                            subtitle.ExpiresAt = DateTime.UtcNow.AddMilliseconds(displayDuration);
                        }

                        subtitle.UpdatedAt = DateTime.UtcNow;

                        // Geçmişe ekle;
                        AddToHistory(new SubtitleEvent;
                        {
                            EventType = SubtitleEventType.Updated,
                            SubtitleId = subtitleId,
                            TrackId = trackId,
                            Timestamp = DateTime.UtcNow,
                            Text = newText,
                            PreviousText = oldText;
                        });

                        // Event tetikle;
                        OnSubtitleUpdated(new SubtitleUpdatedEventArgs;
                        {
                            Subtitle = subtitle,
                            TrackId = trackId,
                            OldText = oldText,
                            Timestamp = DateTime.UtcNow;
                        });

                        _logger.LogDebug("Subtitle updated: {SubtitleId} on track {TrackId}", subtitleId, trackId);

                        return subtitle;
                    }
                }

                throw new SubtitleNotFoundException($"Subtitle not found: {subtitleId} on track {trackId}");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Belirtilen track'taki altyazıları getirir.
        /// </summary>
        /// <param name="trackId">Track ID'si</param>
        /// <returns>Altyazı listesi</returns>
        public IReadOnlyList<Subtitle> GetSubtitlesByTrack(string trackId)
        {
            _lock.EnterReadLock();
            try
            {
                if (_activeTracks.TryGetValue(trackId, out var track))
                {
                    return track.Subtitles.Values.ToList();
                }

                return new List<Subtitle>();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Tüm aktif altyazıları getirir.
        /// </summary>
        /// <returns>Altyazı listesi</returns>
        public IReadOnlyList<Subtitle> GetAllActiveSubtitles()
        {
            _lock.EnterReadLock();
            try
            {
                var result = new List<Subtitle>();

                foreach (var track in _activeTracks.Values)
                {
                    result.AddRange(track.Subtitles.Values);
                }

                return result;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Altyazı geçmişini getirir.
        /// </summary>
        /// <param name="maxCount">Maksimum kayıt sayısı</param>
        /// <returns>Geçmiş listesi</returns>
        public IReadOnlyList<SubtitleEvent> GetHistory(int maxCount = 100)
        {
            _lock.EnterReadLock();
            try
            {
                return _subtitleHistory;
                    .OrderByDescending(h => h.Timestamp)
                    .Take(maxCount)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Altyazıyı arar.
        /// </summary>
        /// <param name="searchText">Aranacak metin</param>
        /// <param name="caseSensitive">Büyük/küçük harf duyarlı mı?</param>
        /// <returns>Bulunan altyazılar</returns>
        public IReadOnlyList<Subtitle> SearchSubtitles(string searchText, bool caseSensitive = false)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return new List<Subtitle>();

            _lock.EnterReadLock();
            try
            {
                var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var result = new List<Subtitle>();

                foreach (var track in _activeTracks.Values)
                {
                    foreach (var subtitle in track.Subtitles.Values)
                    {
                        if (subtitle.Text.IndexOf(searchText, comparison) >= 0)
                        {
                            result.Add(subtitle);
                        }
                    }
                }

                return result;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Süresi dolmuş altyazıları temizler.
        /// </summary>
        /// <returns>Temizlenen altyazı sayısı</returns>
        public int CleanupExpiredSubtitles()
        {
            var removedCount = 0;
            var now = DateTime.UtcNow;

            _lock.EnterWriteLock();
            try
            {
                foreach (var trackPair in _activeTracks.ToList())
                {
                    var trackId = trackPair.Key;
                    var track = trackPair.Value;

                    var expiredIds = track.Subtitles;
                        .Where(p => p.Value.ExpiresAt < now)
                        .Select(p => p.Key)
                        .ToList();

                    foreach (var subtitleId in expiredIds)
                    {
                        if (RemoveSubtitleInternal(trackId, subtitleId))
                        {
                            removedCount++;
                        }
                    }
                }

                if (removedCount > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} expired subtitles", removedCount);
                }

                return removedCount;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Metni belirtilen uzunlukta böler.
        /// </summary>
        /// <param name="text">Bölünecek metin</param>
        /// <param name="maxLineLength">Maksimum satır uzunluğu</param>
        /// <returns>Bölünmüş metin parçaları</returns>
        public static IReadOnlyList<string> SplitTextForDisplay(string text, int maxLineLength = 40)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();

            if (maxLineLength <= 0)
                throw new ArgumentException("Max line length must be greater than 0", nameof(maxLineLength));

            var lines = new List<string>();
            var words = text.Split(' ');
            var currentLine = new StringBuilder();

            foreach (var word in words)
            {
                if (currentLine.Length + word.Length + 1 <= maxLineLength)
                {
                    if (currentLine.Length > 0)
                        currentLine.Append(' ');
                    currentLine.Append(word);
                }
                else;
                {
                    if (currentLine.Length > 0)
                    {
                        lines.Add(currentLine.ToString());
                        currentLine.Clear();
                    }

                    // Eğer tek kelime maxLineLength'den uzunsa, kelimeyi böl;
                    if (word.Length > maxLineLength)
                    {
                        var parts = SplitLongWord(word, maxLineLength);
                        lines.AddRange(parts.SkipLast(1));
                        currentLine.Append(parts.Last());
                    }
                    else;
                    {
                        currentLine.Append(word);
                    }
                }
            }

            if (currentLine.Length > 0)
            {
                lines.Add(currentLine.ToString());
            }

            return lines;
        }

        /// <summary>
        /// Metnin ekranda görüntülenme süresini hesaplar.
        /// </summary>
        /// <param name="text">Metin</param>
        /// <param name="wordsPerMinute">Dakikada okunacak kelime sayısı</param>
        /// <param name="minimumDuration">Minimum süre (ms)</param>
        /// <returns>Önerilen görüntüleme süresi (ms)</returns>
        public static int CalculateDisplayDuration(string text, int wordsPerMinute = 150, int minimumDuration = 1000)
        {
            if (string.IsNullOrEmpty(text))
                return minimumDuration;

            var wordCount = CountWords(text);
            var readingTimeMs = (wordCount / (double)wordsPerMinute) * 60000;

            return Math.Max((int)readingTimeMs, minimumDuration);
        }

        #endregion;

        #region Private Methods;

        private bool RemoveSubtitleInternal(string trackId, string subtitleId)
        {
            if (_activeTracks.TryGetValue(trackId, out var track))
            {
                if (track.Subtitles.TryGetValue(subtitleId, out var subtitle))
                {
                    track.Subtitles.Remove(subtitleId);

                    // Geçmişe ekle;
                    AddToHistory(new SubtitleEvent;
                    {
                        EventType = SubtitleEventType.Removed,
                        SubtitleId = subtitleId,
                        TrackId = trackId,
                        Timestamp = DateTime.UtcNow,
                        Text = subtitle.Text;
                    });

                    // Event tetikle;
                    OnSubtitleRemoved(new SubtitleRemovedEventArgs;
                    {
                        Subtitle = subtitle,
                        TrackId = trackId,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Eğer track boşsa, track'ı da kaldır;
                    if (track.Subtitles.Count == 0)
                    {
                        _activeTracks.Remove(trackId);
                    }

                    _logger.LogDebug("Subtitle removed: {SubtitleId} from track {TrackId}", subtitleId, trackId);

                    return true;
                }
            }

            return false;
        }

        private void AddToHistory(SubtitleEvent historyEvent)
        {
            _subtitleHistory.Add(historyEvent);

            // Geçmiş boyutunu kontrol et;
            if (_subtitleHistory.Count > _configuration.HistorySize)
            {
                var excess = _subtitleHistory.Count - _configuration.HistorySize;
                _subtitleHistory.RemoveRange(0, excess);
            }
        }

        private Subtitle FindOldestSubtitle()
        {
            Subtitle oldest = null;

            foreach (var track in _activeTracks.Values)
            {
                foreach (var subtitle in track.Subtitles.Values)
                {
                    if (oldest == null || subtitle.CreatedAt < oldest.CreatedAt)
                    {
                        oldest = subtitle;
                    }
                }
            }

            return oldest;
        }

        private static string[] SplitLongWord(string word, int maxLength)
        {
            var parts = new List<string>();

            for (int i = 0; i < word.Length; i += maxLength)
            {
                var length = Math.Min(maxLength, word.Length - i);
                parts.Add(word.Substring(i, length));
            }

            return parts.ToArray();
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var wordCount = 0;
            var inWord = false;

            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (!inWord)
                    {
                        wordCount++;
                        inWord = true;
                    }
                }
                else;
                {
                    inWord = false;
                }
            }

            return wordCount;
        }

        private void ValidateSubtitle(Subtitle subtitle)
        {
            if (subtitle == null)
                throw new ArgumentNullException(nameof(subtitle));

            ValidateText(subtitle.Text);

            if (subtitle.DisplayDuration < 0)
                throw new ArgumentException("Display duration cannot be negative", nameof(subtitle.DisplayDuration));
        }

        private void ValidateText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            if (text.Length > _configuration.MaxTextLength)
                throw new ArgumentException($"Text exceeds maximum length of {_configuration.MaxTextLength} characters", nameof(text));
        }

        private void CleanupExpiredSubtitles(object state)
        {
            try
            {
                CleanupExpiredSubtitles();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during subtitle cleanup");
            }
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnSubtitleAdded(SubtitleAddedEventArgs e)
        {
            SubtitleAdded?.Invoke(this, e);
        }

        protected virtual void OnSubtitleRemoved(SubtitleRemovedEventArgs e)
        {
            SubtitleRemoved?.Invoke(this, e);
        }

        protected virtual void OnSubtitleUpdated(SubtitleUpdatedEventArgs e)
        {
            SubtitleUpdated?.Invoke(this, e);
        }

        protected virtual void OnSubtitleExpired(SubtitleExpiredEventArgs e)
        {
            SubtitleExpired?.Invoke(this, e);
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
                    _cleanupTimer?.Dispose();
                    _lock?.Dispose();

                    ClearAllSubtitles();

                    _logger.LogInformation("TextManager disposed");
                }

                _isDisposed = true;
            }
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Altyazı yönetimi için arayüz.
    /// </summary>
    public interface ITextManager : IDisposable
    {
        event EventHandler<SubtitleAddedEventArgs> SubtitleAdded;
        event EventHandler<SubtitleRemovedEventArgs> SubtitleRemoved;
        event EventHandler<SubtitleUpdatedEventArgs> SubtitleUpdated;
        event EventHandler<SubtitleExpiredEventArgs> SubtitleExpired;

        int ActiveSubtitleCount { get; }
        int MaxConcurrentSubtitles { get; }
        int DefaultDisplayDuration { get; }
        int HistorySize { get; }

        Task<string> AddSubtitleAsync(Subtitle subtitle, string trackId = "default");
        bool RemoveSubtitle(string trackId, string subtitleId);
        int ClearTrack(string trackId);
        int ClearAllSubtitles();
        Subtitle UpdateSubtitle(string trackId, string subtitleId, string newText, int displayDuration = 0);
        IReadOnlyList<Subtitle> GetSubtitlesByTrack(string trackId);
        IReadOnlyList<Subtitle> GetAllActiveSubtitles();
        IReadOnlyList<SubtitleEvent> GetHistory(int maxCount = 100);
        IReadOnlyList<Subtitle> SearchSubtitles(string searchText, bool caseSensitive = false);
        int CleanupExpiredSubtitles();
    }

    /// <summary>
    /// Altyazı veri yapısı.
    /// </summary>
    public class Subtitle;
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public int DisplayDuration { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string SpeakerId { get; set; }
        public SubtitlePriority Priority { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public Subtitle()
        {
            Metadata = new Dictionary<string, object>();
            Priority = SubtitlePriority.Normal;
        }

        public Subtitle(string text, int displayDuration = 0) : this()
        {
            Text = text;
            DisplayDuration = displayDuration;
        }
    }

    /// <summary>
    /// Altyazı track'ı.
    /// </summary>
    internal class SubtitleTrack;
    {
        public string Id { get; }
        public Dictionary<string, Subtitle> Subtitles { get; }

        public SubtitleTrack(string id)
        {
            Id = id;
            Subtitles = new Dictionary<string, Subtitle>();
        }
    }

    /// <summary>
    /// Altyazı geçmişi olayı.
    /// </summary>
    public class SubtitleEvent;
    {
        public SubtitleEventType EventType { get; set; }
        public string SubtitleId { get; set; }
        public string TrackId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Text { get; set; }
        public string PreviousText { get; set; }
    }

    /// <summary>
    /// Altyazı olay tipi.
    /// </summary>
    public enum SubtitleEventType;
    {
        Added,
        Removed,
        Updated,
        Expired;
    }

    /// <summary>
    /// Altyazı önceliği.
    /// </summary>
    public enum SubtitlePriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    /// <summary>
    /// Altyazı konfigürasyonu.
    /// </summary>
    public class SubtitleConfiguration;
    {
        public int MaxConcurrentSubtitles { get; set; } = 10;
        public int DefaultDisplayDuration { get; set; } = 3000;
        public int HistorySize { get; set; } = 1000;
        public int MaxTextLength { get; set; } = 500;
        public int CleanupIntervalMinutes { get; set; } = 1;
    }

    /// <summary>
    /// Altyazı eklendiği zaman tetiklenen event argümanları.
    /// </summary>
    public class SubtitleAddedEventArgs : EventArgs;
    {
        public Subtitle Subtitle { get; set; }
        public string TrackId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Altyazı kaldırıldığı zaman tetiklenen event argümanları.
    /// </summary>
    public class SubtitleRemovedEventArgs : EventArgs;
    {
        public Subtitle Subtitle { get; set; }
        public string TrackId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Altyazı güncellendiği zaman tetiklenen event argümanları.
    /// </summary>
    public class SubtitleUpdatedEventArgs : EventArgs;
    {
        public Subtitle Subtitle { get; set; }
        public string TrackId { get; set; }
        public string OldText { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Altyazı süresi dolduğu zaman tetiklenen event argümanları.
    /// </summary>
    public class SubtitleExpiredEventArgs : EventArgs;
    {
        public Subtitle Subtitle { get; set; }
        public string TrackId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Altyazı bulunamadı hatası.
    /// </summary>
    public class SubtitleNotFoundException : Exception
    {
        public SubtitleNotFoundException() { }
        public SubtitleNotFoundException(string message) : base(message) { }
        public SubtitleNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
