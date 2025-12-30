using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.Interface.InteractionManager.SessionHandler;
using NEDA.Interface.InteractionManager.UserProfileManager;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.InteractionManager.ConversationHistory;
{
    /// <summary>
    /// Konuşma geçmişi kayıtları;
    /// </summary>
    public class ConversationRecord;
    {
        public string RecordId { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserInput { get; set; }
        public string SystemResponse { get; set; }
        public string Intent { get; set; }
        public List<string> Entities { get; set; }
        public Dictionary<string, object> ContextData { get; set; }
        public double Confidence { get; set; }
        public TimeSpan ResponseTime { get; set; }
        public string Source { get; set; }
        public string Language { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public ConversationRecord()
        {
            Entities = new List<string>();
            ContextData = new Dictionary<string, object>();
            Metadata = new Dictionary<string, object>();
            RecordId = GenerateRecordId();
            Timestamp = DateTime.UtcNow;
        }

        private string GenerateRecordId()
        {
            return $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }
    }

    /// <summary>
    /// Konuşma geçmişi filtreleri;
    /// </summary>
    public class HistoryFilter;
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public string Intent { get; set; }
        public string SearchText { get; set; }
        public int? MinConfidence { get; set; }
        public List<string> Sources { get; set; }
        public int? MaxRecords { get; set; }
        public bool? IncludeMetadata { get; set; }

        public HistoryFilter()
        {
            Sources = new List<string>();
        }
    }

    /// <summary>
    /// Geçmiş yönetim durumu;
    /// </summary>
    public class HistoryManagerState;
    {
        public int TotalRecords { get; set; }
        public int ActiveSessions { get; set; }
        public long StorageSize { get; set; }
        public DateTime LastBackup { get; set; }
        public bool IsCompressed { get; set; }
        public Dictionary<string, int> IntentStatistics { get; set; }
        public Dictionary<string, int> UserStatistics { get; set; }

        public HistoryManagerState()
        {
            IntentStatistics = new Dictionary<string, int>();
            UserStatistics = new Dictionary<string, int>();
        }
    }

    /// <summary>
    /// Konuşma geçmişi yöneticisi;
    /// </summary>
    public interface IHistoryManager : IDisposable
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<ConversationRecord> SaveRecordAsync(ConversationRecord record, CancellationToken cancellationToken = default);
        Task<IEnumerable<ConversationRecord>> GetRecordsAsync(HistoryFilter filter, CancellationToken cancellationToken = default);
        Task<ConversationRecord> GetRecordByIdAsync(string recordId, CancellationToken cancellationToken = default);
        Task<IEnumerable<ConversationRecord>> GetSessionRecordsAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<IEnumerable<ConversationRecord>> GetUserRecordsAsync(string userId, int maxRecords = 100, CancellationToken cancellationToken = default);
        Task<bool> DeleteRecordAsync(string recordId, CancellationToken cancellationToken = default);
        Task<int> DeleteRecordsAsync(HistoryFilter filter, CancellationToken cancellationToken = default);
        Task<int> CompactHistoryAsync(CancellationToken cancellationToken = default);
        Task BackupHistoryAsync(string backupPath, CancellationToken cancellationToken = default);
        Task RestoreHistoryAsync(string backupPath, CancellationToken cancellationToken = default);
        Task<HistoryManagerState> GetStateAsync(CancellationToken cancellationToken = default);
        Task ClearOldRecordsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
        Task<IEnumerable<string>> SearchRecordsAsync(string searchQuery, CancellationToken cancellationToken = default);
        Task ExportHistoryAsync(string exportPath, ExportFormat format, HistoryFilter filter = null, CancellationToken cancellationToken = default);
        Task<string> GenerateStatisticsReportAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default);
        event EventHandler<HistoryChangedEventArgs> HistoryChanged;
    }

    /// <summary>
    /// Geçmiş değişiklik event argümanları;
    /// </summary>
    public class HistoryChangedEventArgs : EventArgs;
    {
        public string RecordId { get; set; }
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public HistoryChangeType ChangeType { get; set; }
        public DateTime Timestamp { get; set; }

        public HistoryChangedEventArgs(string recordId, string sessionId, string userId, HistoryChangeType changeType)
        {
            RecordId = recordId;
            SessionId = sessionId;
            UserId = userId;
            ChangeType = changeType;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Geçmiş değişiklik türleri;
    /// </summary>
    public enum HistoryChangeType;
    {
        RecordAdded,
        RecordUpdated,
        RecordDeleted,
        RecordsCleared,
        BackupCreated,
        Restored;
    }

    /// <summary>
    /// Export formatları;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml,
        Csv,
        Text;
    }

    /// <summary>
    /// Konuşma geçmişi yöneticisi implementasyonu;
    /// </summary>
    public class HistoryManager : IHistoryManager;
    {
        private readonly ILogger<HistoryManager> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly ISessionManager _sessionManager;
        private readonly IProfileManager _profileManager;
        private readonly AppConfig _appConfig;

        private readonly ConcurrentDictionary<string, ConversationRecord> _memoryCache;
        private readonly ConcurrentDictionary<string, List<string>> _sessionIndex;
        private readonly ConcurrentDictionary<string, List<string>> _userIndex;
        private readonly ConcurrentDictionary<string, List<string>> _intentIndex;

        private readonly string _storagePath;
        private readonly SemaphoreSlim _storageLock;
        private readonly Timer _cleanupTimer;
        private readonly Timer _backupTimer;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// Geçmiş değişiklik event'i;
        /// </summary>
        public event EventHandler<HistoryChangedEventArgs> HistoryChanged;

        /// <summary>
        /// HistoryManager constructor;
        /// </summary>
        public HistoryManager(
            ILogger<HistoryManager> logger,
            IOptions<AppConfig> appConfig,
            IExceptionHandler exceptionHandler,
            ISessionManager sessionManager,
            IProfileManager profileManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig?.Value ?? throw new ArgumentNullException(nameof(appConfig));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));

            _memoryCache = new ConcurrentDictionary<string, ConversationRecord>();
            _sessionIndex = new ConcurrentDictionary<string, List<string>>();
            _userIndex = new ConcurrentDictionary<string, List<string>>();
            _intentIndex = new ConcurrentDictionary<string, List<string>>();

            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NEDA",
                "ConversationHistory");

            _storageLock = new SemaphoreSlim(1, 1);

            // Her 1 saatte bir eski kayıtları temizle;
            _cleanupTimer = new Timer(CleanupOldRecordsCallback, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            // Her 6 saatte bir yedekleme yap;
            _backupTimer = new Timer(BackupHistoryCallback, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
        }

        /// <summary>
        /// HistoryManager'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("HistoryManager başlatılıyor...");

                if (_isInitialized)
                {
                    _logger.LogWarning("HistoryManager zaten başlatılmış");
                    return;
                }

                await _storageLock.WaitAsync(cancellationToken);
                try
                {
                    // Depolama dizinini oluştur;
                    Directory.CreateDirectory(_storagePath);

                    // Mevcut geçmişi yükle;
                    await LoadHistoryFromStorageAsync(cancellationToken);

                    _isInitialized = true;
                    _logger.LogInformation("HistoryManager başarıyla başlatıldı. Toplam kayıt: {RecordCount}", _memoryCache.Count);
                }
                finally
                {
                    _storageLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HistoryManager başlatılırken hata oluştu");
                throw;
            }
        }

        /// <summary>
        /// Yeni konuşma kaydı ekler;
        /// </summary>
        public async Task<ConversationRecord> SaveRecordAsync(ConversationRecord record, CancellationToken cancellationToken = default)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            await ValidateRecordAsync(record, cancellationToken);

            try
            {
                await _storageLock.WaitAsync(cancellationToken);
                try
                {
                    // Belleğe kaydet;
                    _memoryCache[record.RecordId] = record;

                    // İndeksleri güncelle;
                    UpdateIndexes(record);

                    // Dosyaya kaydet;
                    await SaveRecordToFileAsync(record, cancellationToken);

                    // Event tetikle;
                    OnHistoryChanged(new HistoryChangedEventArgs(
                        record.RecordId,
                        record.SessionId,
                        record.UserId,
                        HistoryChangeType.RecordAdded));

                    _logger.LogDebug("Konuşma kaydı eklendi. RecordId: {RecordId}, SessionId: {SessionId}",
                        record.RecordId, record.SessionId);

                    return record;
                }
                finally
                {
                    _storageLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Konuşma kaydı eklenirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.SaveRecordAsync");
                throw;
            }
        }

        /// <summary>
        /// Filtrelenmiş kayıtları getirir;
        /// </summary>
        public async Task<IEnumerable<ConversationRecord>> GetRecordsAsync(HistoryFilter filter, CancellationToken cancellationToken = default)
        {
            if (filter == null)
                filter = new HistoryFilter();

            try
            {
                var records = _memoryCache.Values.AsEnumerable();

                // Filtreleme;
                if (filter.StartDate.HasValue)
                    records = records.Where(r => r.Timestamp >= filter.StartDate.Value);

                if (filter.EndDate.HasValue)
                    records = records.Where(r => r.Timestamp <= filter.EndDate.Value);

                if (!string.IsNullOrEmpty(filter.UserId))
                    records = records.Where(r => r.UserId == filter.UserId);

                if (!string.IsNullOrEmpty(filter.SessionId))
                    records = records.Where(r => r.SessionId == filter.SessionId);

                if (!string.IsNullOrEmpty(filter.Intent))
                    records = records.Where(r => r.Intent == filter.Intent);

                if (filter.MinConfidence.HasValue)
                    records = records.Where(r => r.Confidence >= filter.MinConfidence.Value);

                if (filter.Sources?.Any() == true)
                    records = records.Where(r => filter.Sources.Contains(r.Source));

                if (!string.IsNullOrEmpty(filter.SearchText))
                    records = records.Where(r =>
                        (r.UserInput?.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) == true) ||
                        (r.SystemResponse?.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) == true));

                // Sıralama ve sınırlama;
                records = records.OrderByDescending(r => r.Timestamp);

                if (filter.MaxRecords.HasValue)
                    records = records.Take(filter.MaxRecords.Value);

                // Metadata filtresi;
                if (filter.IncludeMetadata.HasValue && !filter.IncludeMetadata.Value)
                {
                    records = records.Select(r =>
                    {
                        var copy = CloneRecord(r);
                        copy.Metadata.Clear();
                        return copy;
                    });
                }

                return records.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kayıtlar getirilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.GetRecordsAsync");
                throw;
            }
        }

        /// <summary>
        /// ID'ye göre kayıt getirir;
        /// </summary>
        public async Task<ConversationRecord> GetRecordByIdAsync(string recordId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(recordId))
                throw new ArgumentException("RecordId boş olamaz", nameof(recordId));

            try
            {
                if (_memoryCache.TryGetValue(recordId, out var record))
                {
                    return CloneRecord(record);
                }

                // Dosyadan yükle;
                var filePath = GetRecordFilePath(recordId);
                if (File.Exists(filePath))
                {
                    var loadedRecord = await LoadRecordFromFileAsync(filePath, cancellationToken);
                    if (loadedRecord != null)
                    {
                        _memoryCache[recordId] = loadedRecord;
                        return loadedRecord;
                    }
                }

                _logger.LogWarning("Kayıt bulunamadı. RecordId: {RecordId}", recordId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kayıt getirilirken hata oluştu. RecordId: {RecordId}", recordId);
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.GetRecordByIdAsync");
                throw;
            }
        }

        /// <summary>
        /// Session'a ait kayıtları getirir;
        /// </summary>
        public async Task<IEnumerable<ConversationRecord>> GetSessionRecordsAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("SessionId boş olamaz", nameof(sessionId));

            try
            {
                if (_sessionIndex.TryGetValue(sessionId, out var recordIds))
                {
                    var records = new List<ConversationRecord>();
                    foreach (var recordId in recordIds)
                    {
                        var record = await GetRecordByIdAsync(recordId, cancellationToken);
                        if (record != null)
                            records.Add(record);
                    }
                    return records.OrderByDescending(r => r.Timestamp);
                }

                return Enumerable.Empty<ConversationRecord>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session kayıtları getirilirken hata oluştu. SessionId: {SessionId}", sessionId);
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.GetSessionRecordsAsync");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcıya ait kayıtları getirir;
        /// </summary>
        public async Task<IEnumerable<ConversationRecord>> GetUserRecordsAsync(string userId, int maxRecords = 100, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                var filter = new HistoryFilter;
                {
                    UserId = userId,
                    MaxRecords = maxRecords;
                };

                return await GetRecordsAsync(filter, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı kayıtları getirilirken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.GetUserRecordsAsync");
                throw;
            }
        }

        /// <summary>
        /// Kaydı siler;
        /// </summary>
        public async Task<bool> DeleteRecordAsync(string recordId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(recordId))
                throw new ArgumentException("RecordId boş olamaz", nameof(recordId));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);
                try
                {
                    if (_memoryCache.TryRemove(recordId, out var record))
                    {
                        // İndekslerden kaldır;
                        RemoveFromIndexes(record);

                        // Dosyayı sil;
                        var filePath = GetRecordFilePath(recordId);
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }

                        // Event tetikle;
                        OnHistoryChanged(new HistoryChangedEventArgs(
                            recordId,
                            record.SessionId,
                            record.UserId,
                            HistoryChangeType.RecordDeleted));

                        _logger.LogInformation("Kayıt silindi. RecordId: {RecordId}", recordId);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    _storageLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kayıt silinirken hata oluştu. RecordId: {RecordId}", recordId);
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.DeleteRecordAsync");
                throw;
            }
        }

        /// <summary>
        /// Filtrelenmiş kayıtları siler;
        /// </summary>
        public async Task<int> DeleteRecordsAsync(HistoryFilter filter, CancellationToken cancellationToken = default)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            try
            {
                var recordsToDelete = await GetRecordsAsync(filter, cancellationToken);
                int deletedCount = 0;

                foreach (var record in recordsToDelete)
                {
                    if (await DeleteRecordAsync(record.RecordId, cancellationToken))
                    {
                        deletedCount++;
                    }
                }

                _logger.LogInformation("{DeletedCount} kayıt silindi", deletedCount);
                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kayıtlar silinirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.DeleteRecordsAsync");
                throw;
            }
        }

        /// <summary>
        /// Geçmişi sıkıştırır ve optimize eder;
        /// </summary>
        public async Task<int> CompactHistoryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _storageLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogInformation("Geçmiş sıkıştırma başlatılıyor...");

                    int compactedCount = 0;
                    var allFiles = Directory.GetFiles(_storagePath, "*.json");

                    foreach (var file in allFiles)
                    {
                        try
                        {
                            var recordId = Path.GetFileNameWithoutExtension(file);

                            if (!_memoryCache.ContainsKey(recordId))
                            {
                                // Bellekte olmayan ama dosyada olan kayıtları temizle;
                                File.Delete(file);
                                compactedCount++;
                                _logger.LogDebug("Kullanılmayan kayıt dosyası silindi: {FileName}", Path.GetFileName(file));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Dosya işlenirken hata oluştu: {FileName}", Path.GetFileName(file));
                        }
                    }

                    _logger.LogInformation("Geçmiş sıkıştırma tamamlandı. {CompactedCount} kayıt temizlendi", compactedCount);
                    return compactedCount;
                }
                finally
                {
                    _storageLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geçmiş sıkıştırılırken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.CompactHistoryAsync");
                throw;
            }
        }

        /// <summary>
        /// Geçmişi yedekler;
        /// </summary>
        public async Task BackupHistoryAsync(string backupPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(backupPath))
                throw new ArgumentException("Backup yolu boş olamaz", nameof(backupPath));

            try
            {
                await _storageLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogInformation("Geçmiş yedekleme başlatılıyor. Hedef: {BackupPath}", backupPath);

                    Directory.CreateDirectory(backupPath);

                    var backupTime = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    var backupFolder = Path.Combine(backupPath, $"HistoryBackup_{backupTime}");
                    Directory.CreateDirectory(backupFolder);

                    // Tüm kayıtları yedekle;
                    foreach (var record in _memoryCache.Values)
                    {
                        var backupFile = Path.Combine(backupFolder, $"{record.RecordId}.json");
                        var json = JsonConvert.SerializeObject(record, Formatting.Indented);
                        await File.WriteAllTextAsync(backupFile, json, Encoding.UTF8, cancellationToken);
                    }

                    // İndeksleri yedekle;
                    var indexData = new;
                    {
                        SessionIndex = _sessionIndex,
                        UserIndex = _userIndex,
                        IntentIndex = _intentIndex,
                        BackupTime = DateTime.UtcNow,
                        TotalRecords = _memoryCache.Count;
                    };

                    var indexFile = Path.Combine(backupFolder, "_index.json");
                    var indexJson = JsonConvert.SerializeObject(indexData, Formatting.Indented);
                    await File.WriteAllTextAsync(indexFile, indexJson, Encoding.UTF8, cancellationToken);

                    // Event tetikle;
                    OnHistoryChanged(new HistoryChangedEventArgs(
                        null, null, null, HistoryChangeType.BackupCreated));

                    _logger.LogInformation("Geçmiş yedekleme tamamlandı. {RecordCount} kayıt yedeklendi", _memoryCache.Count);
                }
                finally
                {
                    _storageLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geçmiş yedeklenirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.BackupHistoryAsync");
                throw;
            }
        }

        /// <summary>
        /// Yedekten geri yükler;
        /// </summary>
        public async Task RestoreHistoryAsync(string backupPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(backupPath))
                throw new ArgumentException("Backup yolu boş olamaz", nameof(backupPath));

            if (!Directory.Exists(backupPath))
                throw new DirectoryNotFoundException($"Backup dizini bulunamadı: {backupPath}");

            try
            {
                await _storageLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogInformation("Geçmiş geri yükleme başlatılıyor. Kaynak: {BackupPath}", backupPath);

                    // Mevcut geçmişi temizle;
                    ClearMemoryCache();

                    // Yedek dosyalarını yükle;
                    var jsonFiles = Directory.GetFiles(backupPath, "*.json");
                    int restoredCount = 0;

                    foreach (var file in jsonFiles)
                    {
                        try
                        {
                            if (Path.GetFileName(file) == "_index.json")
                                continue;

                            var json = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
                            var record = JsonConvert.DeserializeObject<ConversationRecord>(json);

                            if (record != null)
                            {
                                _memoryCache[record.RecordId] = record;
                                UpdateIndexes(record);

                                // Dosyaya kaydet;
                                await SaveRecordToFileAsync(record, cancellationToken);
                                restoredCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Kayıt geri yüklenirken hata oluştu: {FileName}", Path.GetFileName(file));
                        }
                    }

                    // Event tetikle;
                    OnHistoryChanged(new HistoryChangedEventArgs(
                        null, null, null, HistoryChangeType.Restored));

                    _logger.LogInformation("Geçmiş geri yükleme tamamlandı. {RestoredCount} kayıt geri yüklendi", restoredCount);
                }
                finally
                {
                    _storageLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geçmiş geri yüklenirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.RestoreHistoryAsync");
                throw;
            }
        }

        /// <summary>
        /// Manager durumunu getirir;
        /// </summary>
        public async Task<HistoryManagerState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var state = new HistoryManagerState;
                {
                    TotalRecords = _memoryCache.Count,
                    ActiveSessions = _sessionIndex.Count,
                    StorageSize = await CalculateStorageSizeAsync(cancellationToken),
                    LastBackup = GetLastBackupTime(),
                    IsCompressed = IsHistoryCompressed()
                };

                // İstatistikleri hesapla;
                state.IntentStatistics = CalculateIntentStatistics();
                state.UserStatistics = CalculateUserStatistics();

                return state;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Durum bilgisi getirilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.GetStateAsync");
                throw;
            }
        }

        /// <summary>
        /// Eski kayıtları temizler;
        /// </summary>
        public async Task ClearOldRecordsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow - olderThan;
                var filter = new HistoryFilter;
                {
                    EndDate = cutoffDate;
                };

                int deletedCount = await DeleteRecordsAsync(filter, cancellationToken);
                _logger.LogInformation("{DeletedCount} eski kayıt temizlendi (daha eski: {CutoffDate})",
                    deletedCount, cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Eski kayıtlar temizlenirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.ClearOldRecordsAsync");
                throw;
            }
        }

        /// <summary>
        /// Kayıtlarda arama yapar;
        /// </summary>
        public async Task<IEnumerable<string>> SearchRecordsAsync(string searchQuery, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(searchQuery))
                return Enumerable.Empty<string>();

            try
            {
                var results = new List<string>();
                var searchTerms = searchQuery.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                foreach (var record in _memoryCache.Values)
                {
                    var searchText = $"{record.UserInput} {record.SystemResponse}".ToLower();

                    if (searchTerms.All(term => searchText.Contains(term)))
                    {
                        results.Add(record.RecordId);
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kayıt aranırken hata oluştu. Query: {SearchQuery}", searchQuery);
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.SearchRecordsAsync");
                throw;
            }
        }

        /// <summary>
        /// Geçmişi dışa aktarır;
        /// </summary>
        public async Task ExportHistoryAsync(string exportPath, ExportFormat format, HistoryFilter filter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(exportPath))
                throw new ArgumentException("Export yolu boş olamaz", nameof(exportPath));

            try
            {
                var records = await GetRecordsAsync(filter ?? new HistoryFilter(), cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath));

                switch (format)
                {
                    case ExportFormat.Json:
                        var json = JsonConvert.SerializeObject(records, Formatting.Indented);
                        await File.WriteAllTextAsync(exportPath, json, Encoding.UTF8, cancellationToken);
                        break;

                    case ExportFormat.Xml:
                        var xml = ConvertToXml(records);
                        await File.WriteAllTextAsync(exportPath, xml, Encoding.UTF8, cancellationToken);
                        break;

                    case ExportFormat.Csv:
                        var csv = ConvertToCsv(records);
                        await File.WriteAllTextAsync(exportPath, csv, Encoding.UTF8, cancellationToken);
                        break;

                    case ExportFormat.Text:
                        var text = ConvertToText(records);
                        await File.WriteAllTextAsync(exportPath, text, Encoding.UTF8, cancellationToken);
                        break;

                    default:
                        throw new ArgumentException($"Desteklenmeyen format: {format}");
                }

                _logger.LogInformation("Geçmiş dışa aktarıldı. Format: {Format}, Kayıt: {RecordCount}, Yol: {ExportPath}",
                    format, records.Count(), exportPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geçmiş dışa aktarılırken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.ExportHistoryAsync");
                throw;
            }
        }

        /// <summary>
        /// İstatistik raporu oluşturur;
        /// </summary>
        public async Task<string> GenerateStatisticsReportAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            try
            {
                var filter = new HistoryFilter;
                {
                    StartDate = startDate,
                    EndDate = endDate;
                };

                var records = await GetRecordsAsync(filter, cancellationToken);
                var report = new StringBuilder();

                report.AppendLine("=== KONUŞMA GEÇMİŞİ İSTATİSTİK RAPORU ===");
                report.AppendLine($"Periyot: {startDate:dd.MM.yyyy} - {endDate:dd.MM.yyyy}");
                report.AppendLine($"Toplam Kayıt: {records.Count()}");
                report.AppendLine();

                // Kullanıcı istatistikleri;
                var userStats = records;
                    .GroupBy(r => r.UserId)
                    .Select(g => new { UserId = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(10);

                report.AppendLine("EN AKTİF 10 KULLANICI:");
                foreach (var stat in userStats)
                {
                    report.AppendLine($"  {stat.UserId}: {stat.Count} kayıt");
                }
                report.AppendLine();

                // Intent istatistikleri;
                var intentStats = records;
                    .Where(r => !string.IsNullOrEmpty(r.Intent))
                    .GroupBy(r => r.Intent)
                    .Select(g => new { Intent = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(10);

                report.AppendLine("EN SIK KULLANILAN 10 İNTENT:");
                foreach (var stat in intentStats)
                {
                    report.AppendLine($"  {stat.Intent}: {stat.Count} kez");
                }
                report.AppendLine();

                // Ortalama yanıt süresi;
                var avgResponseTime = records.Any() ?
                    TimeSpan.FromMilliseconds(records.Average(r => r.ResponseTime.TotalMilliseconds)) :
                    TimeSpan.Zero;

                report.AppendLine("PERFORMANS METRİKLERİ:");
                report.AppendLine($"  Ortalama Yanıt Süresi: {avgResponseTime.TotalMilliseconds:F0} ms");
                report.AppendLine($"  Ortalama Güven Skoru: {records.Average(r => r.Confidence):F2}");
                report.AppendLine();

                // Zaman dağılımı;
                var hourlyStats = records;
                    .GroupBy(r => r.Timestamp.Hour)
                    .Select(g => new { Hour = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Hour);

                report.AppendLine("SAATLİK DAĞILIM:");
                foreach (var stat in hourlyStats)
                {
                    report.AppendLine($"  {stat.Hour:00}:00 - {stat.Count} kayıt");
                }

                return report.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "İstatistik raporu oluşturulurken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "HistoryManager.GenerateStatisticsReportAsync");
                throw;
            }
        }

        #region Yardımcı Metodlar;

        /// <summary>
        /// Geçmişi depolamadan yükler;
        /// </summary>
        private async Task LoadHistoryFromStorageAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(_storagePath))
                    return;

                var files = Directory.GetFiles(_storagePath, "*.json");
                int loadedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var record = await LoadRecordFromFileAsync(file, cancellationToken);
                        if (record != null)
                        {
                            _memoryCache[record.RecordId] = record;
                            UpdateIndexes(record);
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Dosya yüklenirken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                _logger.LogDebug("{LoadedCount} kayıt depolamadan yüklendi", loadedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geçmiş depolamadan yüklenirken hata oluştu");
                throw;
            }
        }

        /// <summary>
        /// Kaydı dosyaya kaydeder;
        /// </summary>
        private async Task SaveRecordToFileAsync(ConversationRecord record, CancellationToken cancellationToken)
        {
            var filePath = GetRecordFilePath(record.RecordId);
            var json = JsonConvert.SerializeObject(record, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, cancellationToken);
        }

        /// <summary>
        /// Kaydı dosyadan yükler;
        /// </summary>
        private async Task<ConversationRecord> LoadRecordFromFileAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
                return JsonConvert.DeserializeObject<ConversationRecord>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kayıt dosyası okunurken hata oluştu: {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// Kayıt dosya yolunu oluşturur;
        /// </summary>
        private string GetRecordFilePath(string recordId)
        {
            return Path.Combine(_storagePath, $"{recordId}.json");
        }

        /// <summary>
        /// İndeksleri günceller;
        /// </summary>
        private void UpdateIndexes(ConversationRecord record)
        {
            // Session indeksi;
            _sessionIndex.AddOrUpdate(
                record.SessionId,
                new List<string> { record.RecordId },
                (key, existingList) =>
                {
                    if (!existingList.Contains(record.RecordId))
                        existingList.Add(record.RecordId);
                    return existingList;
                });

            // User indeksi;
            if (!string.IsNullOrEmpty(record.UserId))
            {
                _userIndex.AddOrUpdate(
                    record.UserId,
                    new List<string> { record.RecordId },
                    (key, existingList) =>
                    {
                        if (!existingList.Contains(record.RecordId))
                            existingList.Add(record.RecordId);
                        return existingList;
                    });
            }

            // Intent indeksi;
            if (!string.IsNullOrEmpty(record.Intent))
            {
                _intentIndex.AddOrUpdate(
                    record.Intent,
                    new List<string> { record.RecordId },
                    (key, existingList) =>
                    {
                        if (!existingList.Contains(record.RecordId))
                            existingList.Add(record.RecordId);
                        return existingList;
                    });
            }
        }

        /// <summary>
        /// İndekslerden kaldırır;
        /// </summary>
        private void RemoveFromIndexes(ConversationRecord record)
        {
            // Session indeksi;
            if (_sessionIndex.TryGetValue(record.SessionId, out var sessionList))
            {
                sessionList.Remove(record.RecordId);
                if (sessionList.Count == 0)
                    _sessionIndex.TryRemove(record.SessionId, out _);
            }

            // User indeksi;
            if (!string.IsNullOrEmpty(record.UserId) &&
                _userIndex.TryGetValue(record.UserId, out var userList))
            {
                userList.Remove(record.RecordId);
                if (userList.Count == 0)
                    _userIndex.TryRemove(record.UserId, out _);
            }

            // Intent indeksi;
            if (!string.IsNullOrEmpty(record.Intent) &&
                _intentIndex.TryGetValue(record.Intent, out var intentList))
            {
                intentList.Remove(record.RecordId);
                if (intentList.Count == 0)
                    _intentIndex.TryRemove(record.Intent, out _);
            }
        }

        /// <summary>
        /// Kaydı klonlar;
        /// </summary>
        private ConversationRecord CloneRecord(ConversationRecord original)
        {
            return new ConversationRecord;
            {
                RecordId = original.RecordId,
                SessionId = original.SessionId,
                UserId = original.UserId,
                Timestamp = original.Timestamp,
                UserInput = original.UserInput,
                SystemResponse = original.SystemResponse,
                Intent = original.Intent,
                Entities = new List<string>(original.Entities),
                ContextData = new Dictionary<string, object>(original.ContextData),
                Confidence = original.Confidence,
                ResponseTime = original.ResponseTime,
                Source = original.Source,
                Language = original.Language,
                Metadata = new Dictionary<string, object>(original.Metadata)
            };
        }

        /// <summary>
        /// Kaydı doğrular;
        /// </summary>
        private async Task ValidateRecordAsync(ConversationRecord record, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(record.SessionId))
                throw new ArgumentException("SessionId boş olamaz");

            if (string.IsNullOrEmpty(record.UserInput))
                throw new ArgumentException("UserInput boş olamaz");

            // Session'ın geçerli olduğunu kontrol et;
            var session = await _sessionManager.GetSessionAsync(record.SessionId, cancellationToken);
            if (session == null)
                throw new InvalidOperationException($"Geçersiz session: {record.SessionId}");

            // User'ın geçerli olduğunu kontrol et;
            if (!string.IsNullOrEmpty(record.UserId))
            {
                var profile = await _profileManager.GetProfileAsync(record.UserId, cancellationToken);
                if (profile == null)
                    _logger.LogWarning("Kayıt için geçersiz userId: {UserId}", record.UserId);
            }
        }

        /// <summary>
        /// Bellek önbelleğini temizler;
        /// </summary>
        private void ClearMemoryCache()
        {
            _memoryCache.Clear();
            _sessionIndex.Clear();
            _userIndex.Clear();
            _intentIndex.Clear();
        }

        /// <summary>
        /// Depolama boyutunu hesaplar;
        /// </summary>
        private async Task<long> CalculateStorageSizeAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(_storagePath))
                    return 0;

                var files = Directory.GetFiles(_storagePath, "*.*", SearchOption.AllDirectories);
                return files.Sum(file => new FileInfo(file).Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Depolama boyutu hesaplanırken hata oluştu");
                return 0;
            }
        }

        /// <summary>
        /// Son yedekleme zamanını getirir;
        /// </summary>
        private DateTime GetLastBackupTime()
        {
            var backupDir = Path.Combine(_storagePath, "Backups");
            if (!Directory.Exists(backupDir))
                return DateTime.MinValue;

            var backupFiles = Directory.GetDirectories(backupDir);
            if (!backupFiles.Any())
                return DateTime.MinValue;

            var latestBackup = backupFiles;
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();

            return latestBackup?.LastWriteTimeUtc ?? DateTime.MinValue;
        }

        /// <summary>
        /// Geçmişin sıkıştırılıp sıkıştırılmadığını kontrol eder;
        /// </summary>
        private bool IsHistoryCompressed()
        {
            try
            {
                var files = Directory.GetFiles(_storagePath, "*.json");
                return files.Length <= _memoryCache.Count * 0.9; // %90'dan az dosya varsa sıkıştırılmış;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Intent istatistiklerini hesaplar;
        /// </summary>
        private Dictionary<string, int> CalculateIntentStatistics()
        {
            return _memoryCache.Values;
                .Where(r => !string.IsNullOrEmpty(r.Intent))
                .GroupBy(r => r.Intent)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Kullanıcı istatistiklerini hesaplar;
        /// </summary>
        private Dictionary<string, int> CalculateUserStatistics()
        {
            return _memoryCache.Values;
                .Where(r => !string.IsNullOrEmpty(r.UserId))
                .GroupBy(r => r.UserId)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// XML'e dönüştürür;
        /// </summary>
        private string ConvertToXml(IEnumerable<ConversationRecord> records)
        {
            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine("<ConversationHistory>");

            foreach (var record in records)
            {
                xml.AppendLine("  <Record>");
                xml.AppendLine($"    <RecordId>{SecurityElement.Escape(record.RecordId)}</RecordId>");
                xml.AppendLine($"    <SessionId>{SecurityElement.Escape(record.SessionId)}</SessionId>");
                xml.AppendLine($"    <UserId>{SecurityElement.Escape(record.UserId)}</UserId>");
                xml.AppendLine($"    <Timestamp>{record.Timestamp:o}</Timestamp>");
                xml.AppendLine($"    <UserInput>{SecurityElement.Escape(record.UserInput)}</UserInput>");
                xml.AppendLine($"    <SystemResponse>{SecurityElement.Escape(record.SystemResponse)}</SystemResponse>");
                xml.AppendLine($"    <Intent>{SecurityElement.Escape(record.Intent)}</Intent>");
                xml.AppendLine($"    <Confidence>{record.Confidence}</Confidence>");
                xml.AppendLine("  </Record>");
            }

            xml.AppendLine("</ConversationHistory>");
            return xml.ToString();
        }

        /// <summary>
        /// CSV'ye dönüştürür;
        /// </summary>
        private string ConvertToCsv(IEnumerable<ConversationRecord> records)
        {
            var csv = new StringBuilder();
            csv.AppendLine("RecordId,SessionId,UserId,Timestamp,UserInput,SystemResponse,Intent,Confidence");

            foreach (var record in records)
            {
                var userInput = EscapeCsvField(record.UserInput);
                var systemResponse = EscapeCsvField(record.SystemResponse);
                var intent = EscapeCsvField(record.Intent);

                csv.AppendLine($"\"{record.RecordId}\",\"{record.SessionId}\",\"{record.UserId}\",\"{record.Timestamp:o}\",\"{userInput}\",\"{systemResponse}\",\"{intent}\",{record.Confidence}");
            }

            return csv.ToString();
        }

        /// <summary>
        /// Metne dönüştürür;
        /// </summary>
        private string ConvertToText(IEnumerable<ConversationRecord> records)
        {
            var text = new StringBuilder();

            foreach (var record in records)
            {
                text.AppendLine($"=== Kayıt: {record.RecordId} ===");
                text.AppendLine($"Zaman: {record.Timestamp:dd.MM.yyyy HH:mm:ss}");
                text.AppendLine($"Session: {record.SessionId}");
                text.AppendLine($"Kullanıcı: {record.UserId}");
                text.AppendLine($"Intent: {record.Intent}");
                text.AppendLine($"Güven: {record.Confidence:P0}");
                text.AppendLine();
                text.AppendLine($"Kullanıcı: {record.UserInput}");
                text.AppendLine($"Sistem: {record.SystemResponse}");
                text.AppendLine(new string('-', 80));
                text.AppendLine();
            }

            return text.ToString();
        }

        /// <summary>
        /// CSV alanını temizler;
        /// </summary>
        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            if (field.Contains("\"") || field.Contains(",") || field.Contains("\n"))
                return field.Replace("\"", "\"\"");

            return field;
        }

        /// <summary>
        /// Eski kayıtları temizleme callback'i;
        /// </summary>
        private async void CleanupOldRecordsCallback(object state)
        {
            try
            {
                if (_isDisposed)
                    return;

                // 30 günden eski kayıtları temizle;
                await ClearOldRecordsAsync(TimeSpan.FromDays(30), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Eski kayıtlar temizlenirken hata oluştu (timer callback)");
            }
        }

        /// <summary>
        /// Yedekleme callback'i;
        /// </summary>
        private async void BackupHistoryCallback(object state)
        {
            try
            {
                if (_isDisposed)
                    return;

                var backupDir = Path.Combine(_storagePath, "Backups",
                    $"AutoBackup_{DateTime.UtcNow:yyyyMMdd}");

                await BackupHistoryAsync(backupDir, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Otomatik yedekleme yapılırken hata oluştu (timer callback)");
            }
        }

        /// <summary>
        /// HistoryChanged event'ini tetikler;
        /// </summary>
        protected virtual void OnHistoryChanged(HistoryChangedEventArgs e)
        {
            HistoryChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _cleanupTimer?.Dispose();
                    _backupTimer?.Dispose();
                    _storageLock?.Dispose();

                    // Önbelleği dosyaya kaydet;
                    try
                    {
                        Task.Run(async () => await CompactHistoryAsync()).Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "HistoryManager dispose edilirken hata oluştu");
                    }
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~HistoryManager()
        {
            Dispose(false);
        }

        #endregion;
    }
}
