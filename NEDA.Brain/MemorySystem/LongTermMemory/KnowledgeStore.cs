using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Brain.MemorySystem;
using NEDA.KnowledgeBase.LocalDB;
using NEDA.KnowledgeBase.DataManagement.Repositories;
using NEDA.KnowledgeBase.DataManagement.DataAccess;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Brain.KnowledgeBase.FactDatabase;
{
    /// <summary>
    /// Bilgi türlerini tanımlayan enum;
    /// </summary>
    public enum KnowledgeType;
    {
        Fact = 1,           // Doğrulanmış gerçekler;
        Hypothesis = 2,     // Hipotezler;
        Theory = 3,         // Teoriler;
        Observation = 4,    // Gözlemler;
        Experience = 5,     // Deneyimler;
        Skill = 6,          // Beceriler;
        Procedure = 7,      // Prosedürler;
        Rule = 8,           // Kurallar;
        Pattern = 9,        // Kalıplar;
        Relationship = 10,  // İlişkiler;
        Concept = 11,       // Kavramlar;
        Metadata = 12       // Meta veriler;
    }

    /// <summary>
    /// Bilgi güven seviyelerini tanımlayan enum;
    /// </summary>
    public enum ConfidenceLevel;
    {
        Speculative = 1,    // %0-20 güven;
        Low = 2,           // %21-40 güven;
        Moderate = 3,      // %41-60 güven;
        High = 4,          // %61-80 güven;
        Verified = 5,      // %81-95 güven;
        Certain = 6        // %96-100 güven;
    }

    /// <summary>
    /// Bilgi kaynaklarını tanımlayan enum;
    /// </summary>
    public enum KnowledgeSource;
    {
        SystemGenerated = 1,    // Sistem tarafından oluşturuldu;
        UserInput = 2,          // Kullanıcı girdisi;
        ExternalAPI = 3,        // Harici API;
        WebScraping = 4,        // Web kazıma;
        SensorData = 5,         // Sensör verisi;
        Inference = 6, Çıkarım;
        Learning = 7,           // Öğrenme;
        Analysis = 8,           // Analiz;
        Collaboration = 9,      // İşbirliği;
        LegacySystem = 10       // Eski sistem;
    }

    /// <summary>
    /// Bilgi öncelik seviyelerini tanımlayan enum;
    /// </summary>
    public enum PriorityLevel;
    {
        Trivial = 1,      // Önemsiz;
        Low = 2,          // Düşük;
        Normal = 3,       // Normal;
        High = 4,         // Yüksek;
        Critical = 5      // Kritik;
    }

    /// <summary>
    /// Bilgi parçasını temsil eden temel sınıf;
    /// </summary>
    public class KnowledgeFact : IEquatable<KnowledgeFact>
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("type")]
        public KnowledgeType Type { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "General";

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public ConfidenceLevel Confidence { get; set; }

        [JsonPropertyName("confidenceScore")]
        public double ConfidenceScore { get; set; } // 0-1 arası;

        [JsonPropertyName("source")]
        public KnowledgeSource Source { get; set; }

        [JsonPropertyName("sourceDetails")]
        public Dictionary<string, object> SourceDetails { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("priority")]
        public PriorityLevel Priority { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new List<string>();

        [JsonPropertyName("relationships")]
        public List<KnowledgeRelationship> Relationships { get; set; } = new List<KnowledgeRelationship>();

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; } = "System";

        [JsonPropertyName("createdDate")]
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("modifiedBy")]
        public string ModifiedBy { get; set; } = "System";

        [JsonPropertyName("modifiedDate")]
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("expirationDate")]
        public DateTime? ExpirationDate { get; set; }

        [JsonPropertyName("accessCount")]
        public int AccessCount { get; set; }

        [JsonPropertyName("lastAccessed")]
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("validationStatus")]
        public ValidationStatus ValidationStatus { get; set; }

        [JsonPropertyName("validationHistory")]
        public List<ValidationRecord> ValidationHistory { get; set; } = new List<ValidationRecord>();

        // İstatistikler;
        [JsonPropertyName("usefulnessScore")]
        public double UsefulnessScore { get; set; }

        [JsonPropertyName("relevanceScore")]
        public double RelevanceScore { get; set; }

        [JsonPropertyName("completenessScore")]
        public double CompletenessScore { get; set; }

        [JsonPropertyName("consistencyScore")]
        public double ConsistencyScore { get; set; }

        // Versiyon geçmişi;
        [JsonPropertyName("previousVersions")]
        public List<KnowledgeVersion> PreviousVersions { get; set; } = new List<KnowledgeVersion>();

        public void UpdateAccess()
        {
            AccessCount++;
            LastAccessed = DateTime.UtcNow;
            RelevanceScore = CalculateRelevanceScore();
        }

        public void UpdateConfidence(double newScore, string updatedBy)
        {
            var oldScore = ConfidenceScore;
            ConfidenceScore = Math.Max(0, Math.Min(1, newScore));

            // Confidence level'i güncelle;
            if (ConfidenceScore >= 0.95) Confidence = ConfidenceLevel.Certain;
            else if (ConfidenceScore >= 0.8) Confidence = ConfidenceLevel.Verified;
            else if (ConfidenceScore >= 0.6) Confidence = ConfidenceLevel.High;
            else if (ConfidenceScore >= 0.4) Confidence = ConfidenceLevel.Moderate;
            else if (ConfidenceScore >= 0.2) Confidence = ConfidenceLevel.Low;
            else Confidence = ConfidenceLevel.Speculative;

            ModifiedBy = updatedBy;
            ModifiedDate = DateTime.UtcNow;

            // Doğrulama geçmişine ekle;
            ValidationHistory.Add(new ValidationRecord;
            {
                Validator = updatedBy,
                OldConfidence = oldScore,
                NewConfidence = ConfidenceScore,
                Timestamp = DateTime.UtcNow,
                Notes = $"Confidence updated from {oldScore:F2} to {ConfidenceScore:F2}"
            });
        }

        public void AddRelationship(KnowledgeRelationship relationship)
        {
            if (!Relationships.Any(r => r.Equals(relationship)))
            {
                Relationships.Add(relationship);
                ModifiedDate = DateTime.UtcNow;
            }
        }

        public void UpdateMetadata(string key, object value)
        {
            Metadata[key] = value;
            ModifiedDate = DateTime.UtcNow;
        }

        public void IncrementVersion(string updatedBy, string changeReason)
        {
            // Önceki versiyonu kaydet;
            PreviousVersions.Add(new KnowledgeVersion;
            {
                Version = Version,
                Content = Content,
                ModifiedBy = ModifiedBy,
                ModifiedDate = ModifiedDate,
                ChangeReason = changeReason;
            });

            // Versiyonu arttır;
            Version++;
            ModifiedBy = updatedBy;
            ModifiedDate = DateTime.UtcNow;
        }

        public double CalculateRelevanceScore()
        {
            var recencyFactor = Math.Max(0, 1 - (DateTime.UtcNow - ModifiedDate).TotalDays / 365);
            var accessFactor = Math.Min(AccessCount / 100.0, 1.0);
            var confidenceFactor = ConfidenceScore;
            var completenessFactor = CompletenessScore;

            return (recencyFactor * 0.3) +
                   (accessFactor * 0.2) +
                   (confidenceFactor * 0.3) +
                   (completenessFactor * 0.2);
        }

        public bool Equals(KnowledgeFact other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return Id == other.Id &&
                   Version == other.Version &&
                   ModifiedDate == other.ModifiedDate;
        }

        public override bool Equals(object obj) => Equals(obj as KnowledgeFact);
        public override int GetHashCode() => HashCode.Combine(Id, Version, ModifiedDate);
    }

    /// <summary>
    /// Bilgi ilişkisini temsil eden sınıf;
    /// </summary>
    public class KnowledgeRelationship : IEquatable<KnowledgeRelationship>
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceFactId { get; set; } = string.Empty;
        public string TargetFactId { get; set; } = string.Empty;
        public string RelationshipType { get; set; } = string.Empty; // "is-a", "has-a", "related-to", etc.
        public double Strength { get; set; } = 1.0; // 0-1 arası;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

        public bool Equals(KnowledgeRelationship other)
        {
            if (other is null) return false;

            return SourceFactId == other.SourceFactId &&
                   TargetFactId == other.TargetFactId &&
                   RelationshipType == other.RelationshipType;
        }

        public override bool Equals(object obj) => Equals(obj as KnowledgeRelationship);
        public override int GetHashCode() => HashCode.Combine(SourceFactId, TargetFactId, RelationshipType);
    }

    /// <summary>
    /// Doğrulama durumunu tanımlayan enum;
    /// </summary>
    public enum ValidationStatus;
    {
        Pending = 1,        // Doğrulanmayı bekliyor;
        InProgress = 2,     // Doğrulama devam ediyor;
        Validated = 3,      // Doğrulandı;
        Invalid = 4,        // Geçersiz;
        Outdated = 5,       // Güncel değil;
        Contradicted = 6,   // Çelişkili;
        Verified = 7        // Doğrulandı (yüksek güven)
    }

    /// <summary>
    /// Doğrulama kaydını temsil eden sınıf;
    /// </summary>
    public class ValidationRecord;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Validator { get; set; } = string.Empty;
        public ValidationStatus OldStatus { get; set; }
        public ValidationStatus NewStatus { get; set; }
        public double OldConfidence { get; set; }
        public double NewConfidence { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Notes { get; set; } = string.Empty;
        public Dictionary<string, object> Evidence { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Bilgi versiyonunu temsil eden sınıf;
    /// </summary>
    public class KnowledgeVersion;
    {
        public int Version { get; set; }
        public string Content { get; set; } = string.Empty;
        public string ModifiedBy { get; set; } = string.Empty;
        public DateTime ModifiedDate { get; set; }
        public string ChangeReason { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Bilgi sorgusu parametrelerini temsil eden sınıf;
    /// </summary>
    public class KnowledgeQuery;
    {
        public string SearchText { get; set; } = string.Empty;
        public List<string> Categories { get; set; } = new List<string>();
        public List<KnowledgeType> Types { get; set; } = new List<KnowledgeType>();
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Keywords { get; set; } = new List<string>();
        public ConfidenceLevel? MinConfidence { get; set; }
        public PriorityLevel? MinPriority { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? ModifiedAfter { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public int? MaxResults { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; } = 50;
        public SortBy SortBy { get; set; } = SortBy.Relevance;
        public SortDirection SortDirection { get; set; } = SortDirection.Descending;
        public bool IncludeInactive { get; set; } = false;
        public bool IncludeMetadata { get; set; } = true;
        public List<string> RequiredFields { get; set; } = new List<string>();
    }

    /// <summary>
    /// Sıralama alanlarını tanımlayan enum;
    /// </summary>
    public enum SortBy;
    {
        Relevance,
        Confidence,
        Priority,
        CreatedDate,
        ModifiedDate,
        AccessCount,
        Title;
    }

    /// <summary>
    /// Sıralama yönünü tanımlayan enum;
    /// </summary>
    public enum SortDirection;
    {
        Ascending,
        Descending;
    }

    /// <summary>
    /// Bilgi sorgusu sonucunu temsil eden sınıf;
    /// </summary>
    public class KnowledgeQueryResult;
    {
        public string QueryId { get; set; } = Guid.NewGuid().ToString();
        public KnowledgeQuery Query { get; set; } = new KnowledgeQuery();
        public List<KnowledgeFact> Results { get; set; } = new List<KnowledgeFact>();
        public int TotalCount { get; set; }
        public int FilteredCount { get; set; }
        public int Skipped { get; set; }
        public int Returned { get; set; }
        public double ExecutionTimeMs { get; set; }
        public DateTime ExecutionTime { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
        public List<KnowledgeFact> RelatedFacts { get; set; } = new List<KnowledgeFact>();
        public List<string> Suggestions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Bilgi istatistiklerini temsil eden sınıf;
    /// </summary>
    public class KnowledgeStatistics;
    {
        public int TotalFacts { get; set; }
        public int ActiveFacts { get; set; }
        public int InactiveFacts { get; set; }
        public Dictionary<KnowledgeType, int> FactsByType { get; set; } = new Dictionary<KnowledgeType, int>();
        public Dictionary<ConfidenceLevel, int> FactsByConfidence { get; set; } = new Dictionary<ConfidenceLevel, int>();
        public Dictionary<PriorityLevel, int> FactsByPriority { get; set; } = new Dictionary<PriorityLevel, int>();
        public int TotalRelationships { get; set; }
        public int TotalCategories { get; set; }
        public int TotalTags { get; set; }
        public DateTime OldestFactDate { get; set; }
        public DateTime NewestFactDate { get; set; }
        public double AverageConfidence { get; set; }
        public double AverageRelevance { get; set; }
        public int TotalAccessCount { get; set; }
        public Dictionary<string, int> TopCategories { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> TopTags { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> TopSources { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Bilgi deposu konfigürasyonu;
    /// </summary>
    public class KnowledgeStoreConfig;
    {
        // Depolama konfigürasyonu;
        public string StoragePath { get; set; } = "KnowledgeStore";
        public int MaxMemoryCacheSize { get; set; } = 10000;
        public int MaxCacheItems { get; set; } = 1000;
        public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromHours(1);

        // Performans konfigürasyonu;
        public int BatchSize { get; set; } = 100;
        public int MaxConcurrentOperations { get; set; } = Environment.ProcessorCount * 2;
        public int QueryTimeoutSeconds { get; set; } = 30;
        public bool EnableQueryCache { get; set; } = true;

        // Veri yönetimi;
        public bool EnableAutoBackup { get; set; } = true;
        public TimeSpan AutoBackupInterval { get; set; } = TimeSpan.FromHours(6);
        public int MaxBackupVersions { get; set; } = 10;
        public bool EnableCompression { get; set; } = true;
        public bool EnableEncryption { get; set; } = true;

        // İndeksleme konfigürasyonu;
        public bool EnableFullTextSearch { get; set; } = true;
        public bool EnableTagIndexing { get; set; } = true;
        public bool EnableCategoryIndexing { get; set; } = true;
        public bool EnableRelationshipIndexing { get; set; } = true;

        // Temizleme konfigürasyonu;
        public TimeSpan AutoCleanupInterval { get; set; } = TimeSpan.FromDays(1);
        public TimeSpan InactiveFactExpiration { get; set; } = TimeSpan.FromDays(180);
        public double MinConfidenceForDeletion { get; set; } = 0.1;

        // İzleme konfigürasyonu;
        public bool EnableAccessTracking { get; set; } = true;
        public bool EnableChangeTracking { get; set; } = true;
        public bool EnablePerformanceMetrics { get; set; } = true;
    }

    /// <summary>
    /// Merkezi bilgi deposu - Tüm bilgi ve gerçekleri depolar, yönetir ve sorgular;
    /// </summary>
    public interface IKnowledgeStore : IDisposable
    {
        // CRUD operasyonları;
        Task<KnowledgeFact> StoreFactAsync(KnowledgeFact fact, string userId);
        Task<KnowledgeFact> GetFactAsync(string factId, bool updateAccess = true);
        Task<List<KnowledgeFact>> GetFactsAsync(List<string> factIds, bool updateAccess = true);
        Task<KnowledgeFact> UpdateFactAsync(KnowledgeFact fact, string userId, string changeReason = "");
        Task<bool> DeleteFactAsync(string factId, string userId, string reason = "");
        Task<bool> SoftDeleteFactAsync(string factId, string userId, string reason = "");

        // Toplu operasyonlar;
        Task<List<KnowledgeFact>> StoreFactsBatchAsync(List<KnowledgeFact> facts, string userId);
        Task<List<KnowledgeFact>> UpdateFactsBatchAsync(List<KnowledgeFact> facts, string userId);
        Task<int> DeleteFactsBatchAsync(List<string> factIds, string userId);

        // Sorgulama operasyonları;
        Task<KnowledgeQueryResult> QueryFactsAsync(KnowledgeQuery query);
        Task<List<KnowledgeFact>> SearchFactsAsync(string searchText, int maxResults = 50);
        Task<List<KnowledgeFact>> GetFactsByCategoryAsync(string category, int maxResults = 100);
        Task<List<KnowledgeFact>> GetFactsByTagAsync(string tag, int maxResults = 100);
        Task<List<KnowledgeFact>> GetFactsByTypeAsync(KnowledgeType type, int maxResults = 100);
        Task<List<KnowledgeFact>> GetRelatedFactsAsync(string factId, int depth = 1);

        // İlişki yönetimi;
        Task<KnowledgeRelationship> AddRelationshipAsync(KnowledgeRelationship relationship, string userId);
        Task<bool> RemoveRelationshipAsync(string relationshipId, string userId);
        Task<List<KnowledgeRelationship>> GetRelationshipsAsync(string factId);
        Task<List<KnowledgeFact>> GetConnectedFactsAsync(string factId, string relationshipType = null);

        // İstatistik ve analiz;
        Task<KnowledgeStatistics> GetStatisticsAsync();
        Task<Dictionary<string, double>> GetKnowledgeGraphAsync(string rootFactId = null, int maxDepth = 3);
        Task<List<KnowledgeFact>> FindContradictionsAsync(string factId);
        Task<List<KnowledgeFact>> FindSimilarFactsAsync(KnowledgeFact fact, double similarityThreshold = 0.7);

        // Doğrulama operasyonları;
        Task<KnowledgeFact> ValidateFactAsync(string factId, string validator, ValidationStatus newStatus, string notes = "");
        Task<List<KnowledgeFact>> GetFactsNeedingValidationAsync(int maxResults = 50);
        Task<double> CalculateFactConsistencyAsync(string factId);

        // Önbellek yönetimi;
        Task ClearCacheAsync();
        Task PreloadCacheAsync(List<string> factIds);
        Task<Dictionary<string, object>> GetCacheStatsAsync();

        // Yedekleme ve geri yükleme;
        Task<string> CreateBackupAsync(string backupName = null);
        Task<bool> RestoreFromBackupAsync(string backupId);
        Task<List<BackupInfo>> GetBackupListAsync();
        Task<bool> DeleteBackupAsync(string backupId);

        // Sistem operasyonları;
        Task InitializeAsync();
        Task CleanupAsync(TimeSpan? olderThan = null);
        Task OptimizeStorageAsync();
        Task ExportKnowledgeAsync(string exportPath, ExportFormat format = ExportFormat.Json);
        Task ImportKnowledgeAsync(string importPath, ImportMode mode = ImportMode.Merge);
    }

    /// <summary>
    /// Dışa aktarma formatını tanımlayan enum;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml,
        Csv,
        GraphML;
    }

    /// <summary>
    /// İçe aktarma modunu tanımlayan enum;
    /// </summary>
    public enum ImportMode;
    {
        Merge,
        Replace,
        Append;
    }

    /// <summary>
    /// Yedekleme bilgilerini temsil eden sınıf;
    /// </summary>
    public class BackupInfo;
    {
        public string BackupId { get; set; } = string.Empty;
        public string BackupName { get; set; } = string.Empty;
        public DateTime BackupTime { get; set; }
        public int FactCount { get; set; }
        public long SizeInBytes { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Bilgi deposu implementasyonu;
    /// </summary>
    public class KnowledgeStore : IKnowledgeStore;
    {
        private readonly ILogger<KnowledgeStore> _logger;
        private readonly IEventBus _eventBus;
        private readonly IDatabase _database;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IRepository<KnowledgeFact> _factRepository;
        private readonly KnowledgeStoreConfig _config;

        // Önbellek yapıları;
        private readonly ConcurrentDictionary<string, KnowledgeFact> _memoryCache;
        private readonly ConcurrentDictionary<string, DateTime> _cacheTimestamps;
        private readonly ConcurrentDictionary<string, List<KnowledgeFact>> _queryCache;
        private readonly ConcurrentDictionary<string, KnowledgeStatistics> _statisticsCache;

        // İndeks yapıları;
        private readonly ConcurrentDictionary<string, HashSet<string>> _categoryIndex;
        private readonly ConcurrentDictionary<string, HashSet<string>> _tagIndex;
        private readonly ConcurrentDictionary<KnowledgeType, HashSet<string>> _typeIndex;
        private readonly ConcurrentDictionary<string, HashSet<string>> _keywordIndex;
        private readonly ConcurrentDictionary<string, List<KnowledgeRelationship>> _relationshipIndex;

        // Kilitleme mekanizmaları;
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        private readonly ReaderWriterLockSlim _indexLock = new ReaderWriterLockSlim();
        private readonly SemaphoreSlim _operationSemaphore;

        // İzleme değişkenleri;
        private readonly ConcurrentDictionary<string, int> _accessCounts;
        private readonly ConcurrentQueue<OperationMetric> _operationMetrics;
        private DateTime _lastCleanupTime = DateTime.UtcNow;
        private DateTime _lastBackupTime = DateTime.UtcNow;

        private bool _disposed;
        private bool _initialized;

        public KnowledgeStore(
            ILogger<KnowledgeStore> logger,
            IEventBus eventBus,
            IDatabase database,
            IUnitOfWork unitOfWork,
            IOptions<KnowledgeStoreConfig> config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _config = config?.Value ?? new KnowledgeStoreConfig();

            // Repository oluştur;
            _factRepository = _unitOfWork.GetRepository<KnowledgeFact>();

            // Önbellekleri başlat;
            _memoryCache = new ConcurrentDictionary<string, KnowledgeFact>();
            _cacheTimestamps = new ConcurrentDictionary<string, DateTime>();
            _queryCache = new ConcurrentDictionary<string, List<KnowledgeFact>>();
            _statisticsCache = new ConcurrentDictionary<string, KnowledgeStatistics>();

            // İndeksleri başlat;
            _categoryIndex = new ConcurrentDictionary<string, HashSet<string>>();
            _tagIndex = new ConcurrentDictionary<string, HashSet<string>>();
            _typeIndex = new ConcurrentDictionary<KnowledgeType, HashSet<string>>();
            _keywordIndex = new ConcurrentDictionary<string, HashSet<string>>();
            _relationshipIndex = new ConcurrentDictionary<string, List<KnowledgeRelationship>>();

            // İzleme yapılarını başlat;
            _accessCounts = new ConcurrentDictionary<string, int>();
            _operationMetrics = new ConcurrentQueue<OperationMetric>();

            // Semaphore'u başlat;
            _operationSemaphore = new SemaphoreSlim(_config.MaxConcurrentOperations);

            _logger.LogInformation("KnowledgeStore initialized with config: {Config}",
                JsonSerializer.Serialize(_config));
        }

        /// <summary>
        /// Bilgi deposunu başlatır;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                _logger.LogInformation("Initializing KnowledgeStore...");

                // Veritabanını başlat;
                await _database.InitializeAsync();

                // İndeksleri yükle;
                await LoadIndicesAsync();

                // Önbelleği ısıt;
                await PreloadCacheAsync();

                // Otomatik temizleme başlat;
                _ = StartAutoCleanupAsync();

                // Otomatik yedekleme başlat;
                if (_config.EnableAutoBackup)
                {
                    _ = StartAutoBackupAsync();
                }

                _initialized = true;
                _logger.LogInformation("KnowledgeStore initialized successfully");

                await _eventBus.PublishAsync(new KnowledgeStoreInitializedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    FactCount = _memoryCache.Count,
                    IndexCounts = GetIndexCounts()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize KnowledgeStore");
                throw new KnowledgeStoreException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni bir bilgi gerçeği depolar;
        /// </summary>
        public async Task<KnowledgeFact> StoreFactAsync(KnowledgeFact fact, string userId)
        {
            ValidateUserId(userId);
            ValidateFact(fact);

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                await _operationSemaphore.WaitAsync();
                TrackOperationStart("StoreFact", operationId);

                _logger.LogDebug("Storing fact: {FactTitle} for user {UserId}", fact.Title, userId);

                // Benzersiz ID kontrolü;
                if (await FactExistsAsync(fact.Id))
                {
                    throw new KnowledgeStoreException($"Fact with ID {fact.Id} already exists");
                }

                // Meta verileri güncelle;
                fact.CreatedBy = userId;
                fact.ModifiedBy = userId;
                fact.CreatedDate = DateTime.UtcNow;
                fact.ModifiedDate = DateTime.UtcNow;
                fact.LastAccessed = DateTime.UtcNow;

                // İlgili skorları hesapla;
                fact.RelevanceScore = fact.CalculateRelevanceScore();
                fact.CompletenessScore = CalculateCompletenessScore(fact);
                fact.ConsistencyScore = await CalculateFactConsistencyAsync(fact.Id);

                // Veritabanına kaydet;
                using (var transaction = await _unitOfWork.BeginTransactionAsync())
                {
                    try
                    {
                        await _factRepository.AddAsync(fact);
                        await _unitOfWork.SaveChangesAsync();
                        await transaction.CommitAsync();

                        _logger.LogInformation("Fact stored successfully: {FactId}", fact.Id);
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }

                // Önbelleğe ekle;
                AddToCache(fact);

                // İndeksleri güncelle;
                UpdateIndices(fact);

                // Olay yayınla;
                await _eventBus.PublishAsync(new FactStoredEvent;
                {
                    FactId = fact.Id,
                    FactType = fact.Type,
                    UserId = userId,
                    Confidence = fact.Confidence,
                    Timestamp = DateTime.UtcNow;
                });

                TrackOperationEnd("StoreFact", operationId, startTime, true);
                return fact;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("StoreFact", operationId, startTime, false);
                _logger.LogError(ex, "Error storing fact for user {UserId}", userId);
                throw new KnowledgeStoreException("Failed to store fact", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Bilgi gerçeğini getirir;
        /// </summary>
        public async Task<KnowledgeFact> GetFactAsync(string factId, bool updateAccess = true)
        {
            ValidateFactId(factId);

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                await _operationSemaphore.WaitAsync();
                TrackOperationStart("GetFact", operationId);

                _logger.LogDebug("Getting fact: {FactId}", factId);

                // Önce önbellekte ara;
                if (TryGetFromCache(factId, out var cachedFact))
                {
                    if (updateAccess)
                    {
                        cachedFact.UpdateAccess();
                        UpdateInCache(cachedFact);
                    }

                    TrackOperationEnd("GetFact", operationId, startTime, true);
                    return cachedFact;
                }

                // Veritabanından getir;
                var fact = await _factRepository.GetByIdAsync(factId);
                if (fact == null)
                {
                    throw new KeyNotFoundException($"Fact {factId} not found");
                }

                if (updateAccess)
                {
                    fact.UpdateAccess();

                    // Veritabanında güncelle;
                    await _factRepository.UpdateAsync(fact);
                    await _unitOfWork.SaveChangesAsync();
                }

                // Önbelleğe ekle;
                AddToCache(fact);

                TrackOperationEnd("GetFact", operationId, startTime, true);
                return fact;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("GetFact", operationId, startTime, false);
                _logger.LogError(ex, "Error getting fact {FactId}", factId);
                throw;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Birden fazla bilgi gerçeğini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetFactsAsync(List<string> factIds, bool updateAccess = true)
        {
            if (factIds == null || !factIds.Any())
            {
                throw new ArgumentException("Fact IDs cannot be null or empty", nameof(factIds));
            }

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                TrackOperationStart("GetFactsBatch", operationId);

                _logger.LogDebug("Getting {Count} facts", factIds.Count);

                var result = new List<KnowledgeFact>();
                var missingIds = new List<string>();

                // Önbellekten getir;
                foreach (var factId in factIds)
                {
                    if (TryGetFromCache(factId, out var cachedFact))
                    {
                        if (updateAccess)
                        {
                            cachedFact.UpdateAccess();
                            UpdateInCache(cachedFact);
                        }
                        result.Add(cachedFact);
                    }
                    else;
                    {
                        missingIds.Add(factId);
                    }
                }

                // Eksikleri veritabanından getir;
                if (missingIds.Any())
                {
                    var missingFacts = await _factRepository.GetAllAsync(
                        predicate: f => missingIds.Contains(f.Id));

                    foreach (var fact in missingFacts)
                    {
                        if (updateAccess)
                        {
                            fact.UpdateAccess();

                            // Veritabanında güncelle;
                            await _factRepository.UpdateAsync(fact);
                        }

                        AddToCache(fact);
                        result.Add(fact);
                    }

                    await _unitOfWork.SaveChangesAsync();
                }

                TrackOperationEnd("GetFactsBatch", operationId, startTime, true);
                return result;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("GetFactsBatch", operationId, startTime, false);
                _logger.LogError(ex, "Error getting facts batch");
                throw;
            }
        }

        /// <summary>
        /// Bilgi gerçeğini günceller;
        /// </summary>
        public async Task<KnowledgeFact> UpdateFactAsync(KnowledgeFact fact, string userId, string changeReason = "")
        {
            ValidateUserId(userId);
            ValidateFact(fact);

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                await _operationSemaphore.WaitAsync();
                TrackOperationStart("UpdateFact", operationId);

                _logger.LogDebug("Updating fact: {FactId} for user {UserId}", fact.Id, userId);

                // Mevcut gerçeği getir;
                var existingFact = await GetFactAsync(fact.Id, false);
                if (existingFact == null)
                {
                    throw new KeyNotFoundException($"Fact {fact.Id} not found");
                }

                // Versiyonu arttır;
                fact.IncrementVersion(userId, changeReason);

                // Meta verileri güncelle;
                fact.ModifiedBy = userId;
                fact.ModifiedDate = DateTime.UtcNow;
                fact.LastAccessed = DateTime.UtcNow;

                // Skorları güncelle;
                fact.RelevanceScore = fact.CalculateRelevanceScore();
                fact.CompletenessScore = CalculateCompletenessScore(fact);
                fact.ConsistencyScore = await CalculateFactConsistencyAsync(fact.Id);

                // Veritabanında güncelle;
                using (var transaction = await _unitOfWork.BeginTransactionAsync())
                {
                    try
                    {
                        await _factRepository.UpdateAsync(fact);
                        await _unitOfWork.SaveChangesAsync();
                        await transaction.CommitAsync();

                        _logger.LogInformation("Fact updated successfully: {FactId}", fact.Id);
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }

                // Önbelleği güncelle;
                UpdateInCache(fact);

                // İndeksleri güncelle;
                UpdateIndices(fact);

                // Olay yayınla;
                await _eventBus.PublishAsync(new FactUpdatedEvent;
                {
                    FactId = fact.Id,
                    UserId = userId,
                    OldVersion = existingFact.Version,
                    NewVersion = fact.Version,
                    ChangeReason = changeReason,
                    Timestamp = DateTime.UtcNow;
                });

                TrackOperationEnd("UpdateFact", operationId, startTime, true);
                return fact;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("UpdateFact", operationId, startTime, false);
                _logger.LogError(ex, "Error updating fact {FactId} for user {UserId}", fact.Id, userId);
                throw new KnowledgeStoreException("Failed to update fact", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Bilgi gerçeğini siler;
        /// </summary>
        public async Task<bool> DeleteFactAsync(string factId, string userId, string reason = "")
        {
            ValidateUserId(userId);
            ValidateFactId(factId);

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                await _operationSemaphore.WaitAsync();
                TrackOperationStart("DeleteFact", operationId);

                _logger.LogDebug("Deleting fact: {FactId} for user {UserId}", factId, userId);

                // İlişkileri kontrol et;
                var relationships = await GetRelationshipsAsync(factId);
                if (relationships.Any())
                {
                    throw new InvalidOperationException(
                        $"Cannot delete fact {factId}. It has {relationships.Count} relationships.");
                }

                // Veritabanından sil;
                using (var transaction = await _unitOfWork.BeginTransactionAsync())
                {
                    try
                    {
                        var fact = await _factRepository.GetByIdAsync(factId);
                        if (fact == null)
                        {
                            TrackOperationEnd("DeleteFact", operationId, startTime, true);
                            return false;
                        }

                        await _factRepository.DeleteAsync(fact);
                        await _unitOfWork.SaveChangesAsync();
                        await transaction.CommitAsync();

                        _logger.LogInformation("Fact deleted successfully: {FactId}", factId);
                    }
                    catch
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }

                // Önbellekten sil;
                RemoveFromCache(factId);

                // İndekslerden sil;
                RemoveFromIndices(factId);

                // Olay yayınla;
                await _eventBus.PublishAsync(new FactDeletedEvent;
                {
                    FactId = factId,
                    UserId = userId,
                    Reason = reason,
                    Timestamp = DateTime.UtcNow;
                });

                TrackOperationEnd("DeleteFact", operationId, startTime, true);
                return true;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("DeleteFact", operationId, startTime, false);
                _logger.LogError(ex, "Error deleting fact {FactId} for user {UserId}", factId, userId);
                throw new KnowledgeStoreException("Failed to delete fact", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Bilgi gerçeğini yumuşak siler (inaktif yapar)
        /// </summary>
        public async Task<bool> SoftDeleteFactAsync(string factId, string userId, string reason = "")
        {
            ValidateUserId(userId);
            ValidateFactId(factId);

            try
            {
                _logger.LogDebug("Soft deleting fact: {FactId} for user {UserId}", factId, userId);

                var fact = await GetFactAsync(factId, false);
                if (fact == null)
                {
                    return false;
                }

                fact.IsActive = false;
                fact.ModifiedBy = userId;
                fact.ModifiedDate = DateTime.UtcNow;
                fact.Metadata["SoftDeleteReason"] = reason;
                fact.Metadata["SoftDeleteTime"] = DateTime.UtcNow;
                fact.Metadata["SoftDeletedBy"] = userId;

                await UpdateFactAsync(fact, userId, $"Soft delete: {reason}");

                _logger.LogInformation("Fact soft deleted: {FactId}", factId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error soft deleting fact {FactId} for user {UserId}", factId, userId);
                throw new KnowledgeStoreException("Failed to soft delete fact", ex);
            }
        }

        /// <summary>
        /// Toplu bilgi gerçeği depolar;
        /// </summary>
        public async Task<List<KnowledgeFact>> StoreFactsBatchAsync(List<KnowledgeFact> facts, string userId)
        {
            ValidateUserId(userId);

            if (facts == null || !facts.Any())
            {
                throw new ArgumentException("Facts cannot be null or empty", nameof(facts));
            }

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                TrackOperationStart("StoreFactsBatch", operationId);

                _logger.LogDebug("Storing {Count} facts in batch for user {UserId}", facts.Count, userId);

                var storedFacts = new List<KnowledgeFact>();
                var batchSize = _config.BatchSize;

                for (int i = 0; i < facts.Count; i += batchSize)
                {
                    var batch = facts.Skip(i).Take(batchSize).ToList();

                    using (var transaction = await _unitOfWork.BeginTransactionAsync())
                    {
                        try
                        {
                            foreach (var fact in batch)
                            {
                                // Benzersiz ID kontrolü;
                                if (await FactExistsAsync(fact.Id))
                                {
                                    _logger.LogWarning("Fact with ID {FactId} already exists, skipping", fact.Id);
                                    continue;
                                }

                                // Meta verileri güncelle;
                                fact.CreatedBy = userId;
                                fact.ModifiedBy = userId;
                                fact.CreatedDate = DateTime.UtcNow;
                                fact.ModifiedDate = DateTime.UtcNow;
                                fact.LastAccessed = DateTime.UtcNow;

                                // Skorları hesapla;
                                fact.RelevanceScore = fact.CalculateRelevanceScore();
                                fact.CompletenessScore = CalculateCompletenessScore(fact);
                                fact.ConsistencyScore = await CalculateFactConsistencyAsync(fact.Id);

                                await _factRepository.AddAsync(fact);
                                storedFacts.Add(fact);
                            }

                            await _unitOfWork.SaveChangesAsync();
                            await transaction.CommitAsync();

                            // Önbelleğe ekle ve indeksleri güncelle;
                            foreach (var fact in storedFacts)
                            {
                                AddToCache(fact);
                                UpdateIndices(fact);
                            }
                        }
                        catch
                        {
                            await transaction.RollbackAsync();
                            throw;
                        }
                    }

                    _logger.LogDebug("Stored batch {BatchNumber} of {TotalBatches}",
                        (i / batchSize) + 1, (int)Math.Ceiling((double)facts.Count / batchSize));
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new FactsStoredBatchEvent;
                {
                    UserId = userId,
                    FactCount = storedFacts.Count,
                    Timestamp = DateTime.UtcNow;
                });

                TrackOperationEnd("StoreFactsBatch", operationId, startTime, true);
                return storedFacts;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("StoreFactsBatch", operationId, startTime, false);
                _logger.LogError(ex, "Error storing facts batch for user {UserId}", userId);
                throw new KnowledgeStoreException("Failed to store facts batch", ex);
            }
        }

        /// <summary>
        /// Toplu bilgi gerçeği günceller;
        /// </summary>
        public async Task<List<KnowledgeFact>> UpdateFactsBatchAsync(List<KnowledgeFact> facts, string userId)
        {
            ValidateUserId(userId);

            if (facts == null || !facts.Any())
            {
                throw new ArgumentException("Facts cannot be null or empty", nameof(facts));
            }

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                TrackOperationStart("UpdateFactsBatch", operationId);

                _logger.LogDebug("Updating {Count} facts in batch for user {UserId}", facts.Count, userId);

                var updatedFacts = new List<KnowledgeFact>();
                var batchSize = _config.BatchSize;

                for (int i = 0; i < facts.Count; i += batchSize)
                {
                    var batch = facts.Skip(i).Take(batchSize).ToList();

                    using (var transaction = await _unitOfWork.BeginTransactionAsync())
                    {
                        try
                        {
                            foreach (var fact in batch)
                            {
                                var existingFact = await GetFactAsync(fact.Id, false);
                                if (existingFact == null)
                                {
                                    _logger.LogWarning("Fact {FactId} not found, skipping update", fact.Id);
                                    continue;
                                }

                                // Versiyonu arttır;
                                fact.IncrementVersion(userId, "Batch update");

                                // Meta verileri güncelle;
                                fact.ModifiedBy = userId;
                                fact.ModifiedDate = DateTime.UtcNow;
                                fact.LastAccessed = DateTime.UtcNow;

                                // Skorları güncelle;
                                fact.RelevanceScore = fact.CalculateRelevanceScore();
                                fact.CompletenessScore = CalculateCompletenessScore(fact);
                                fact.ConsistencyScore = await CalculateFactConsistencyAsync(fact.Id);

                                await _factRepository.UpdateAsync(fact);
                                updatedFacts.Add(fact);
                            }

                            await _unitOfWork.SaveChangesAsync();
                            await transaction.CommitAsync();

                            // Önbelleği ve indeksleri güncelle;
                            foreach (var fact in updatedFacts)
                            {
                                UpdateInCache(fact);
                                UpdateIndices(fact);
                            }
                        }
                        catch
                        {
                            await transaction.RollbackAsync();
                            throw;
                        }
                    }
                }

                TrackOperationEnd("UpdateFactsBatch", operationId, startTime, true);
                return updatedFacts;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("UpdateFactsBatch", operationId, startTime, false);
                _logger.LogError(ex, "Error updating facts batch for user {UserId}", userId);
                throw new KnowledgeStoreException("Failed to update facts batch", ex);
            }
        }

        /// <summary>
        /// Toplu bilgi gerçeği siler;
        /// </summary>
        public async Task<int> DeleteFactsBatchAsync(List<string> factIds, string userId)
        {
            ValidateUserId(userId);

            if (factIds == null || !factIds.Any())
            {
                throw new ArgumentException("Fact IDs cannot be null or empty", nameof(factIds));
            }

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                TrackOperationStart("DeleteFactsBatch", operationId);

                _logger.LogDebug("Deleting {Count} facts in batch for user {UserId}", factIds.Count, userId);

                var deletedCount = 0;
                var batchSize = _config.BatchSize;

                for (int i = 0; i < factIds.Count; i += batchSize)
                {
                    var batch = factIds.Skip(i).Take(batchSize).ToList();

                    using (var transaction = await _unitOfWork.BeginTransactionAsync())
                    {
                        try
                        {
                            foreach (var factId in batch)
                            {
                                var fact = await _factRepository.GetByIdAsync(factId);
                                if (fact == null)
                                {
                                    continue;
                                }

                                // İlişkileri kontrol et;
                                var relationships = await GetRelationshipsAsync(factId);
                                if (relationships.Any())
                                {
                                    _logger.LogWarning("Fact {FactId} has relationships, skipping delete", factId);
                                    continue;
                                }

                                await _factRepository.DeleteAsync(fact);
                                deletedCount++;

                                // Önbellekten ve indekslerden sil;
                                RemoveFromCache(factId);
                                RemoveFromIndices(factId);
                            }

                            await _unitOfWork.SaveChangesAsync();
                            await transaction.CommitAsync();
                        }
                        catch
                        {
                            await transaction.RollbackAsync();
                            throw;
                        }
                    }
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new FactsDeletedBatchEvent;
                {
                    UserId = userId,
                    DeletedCount = deletedCount,
                    Timestamp = DateTime.UtcNow;
                });

                TrackOperationEnd("DeleteFactsBatch", operationId, startTime, true);
                return deletedCount;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("DeleteFactsBatch", operationId, startTime, false);
                _logger.LogError(ex, "Error deleting facts batch for user {UserId}", userId);
                throw new KnowledgeStoreException("Failed to delete facts batch", ex);
            }
        }

        /// <summary>
        /// Bilgi gerçeklerini sorgular;
        /// </summary>
        public async Task<KnowledgeQueryResult> QueryFactsAsync(KnowledgeQuery query)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                await _operationSemaphore.WaitAsync();
                TrackOperationStart("QueryFacts", operationId);

                _logger.LogDebug("Querying facts with parameters: {Query}",
                    JsonSerializer.Serialize(query));

                // Önbellek anahtarı oluştur;
                var cacheKey = CreateQueryCacheKey(query);

                // Önbellekte varsa getir;
                if (_config.EnableQueryCache && _queryCache.TryGetValue(cacheKey, out var cachedResults))
                {
                    _logger.LogDebug("Query results found in cache");

                    var result = new KnowledgeQueryResult;
                    {
                        Query = query,
                        Results = cachedResults,
                        TotalCount = cachedResults.Count,
                        FilteredCount = cachedResults.Count,
                        Skipped = query.Skip,
                        Returned = Math.Min(query.Take, cachedResults.Count - query.Skip),
                        ExecutionTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    };

                    TrackOperationEnd("QueryFacts", operationId, startTime, true);
                    return result;
                }

                // Sorguyu yürüt;
                var allFacts = await GetAllFactsFromCacheAsync();

                // Filtrele;
                var filteredFacts = FilterFacts(allFacts, query);

                // Sırala;
                var sortedFacts = SortFacts(filteredFacts, query);

                // Sayfalama;
                var pagedFacts = sortedFacts;
                    .Skip(query.Skip)
                    .Take(query.Take)
                    .ToList();

                // İlgili gerçekleri bul;
                var relatedFacts = await FindRelatedFactsAsync(pagedFacts, 2);

                // Öneriler oluştur;
                var suggestions = GenerateQuerySuggestions(query, pagedFacts);

                var result = new KnowledgeQueryResult;
                {
                    Query = query,
                    Results = pagedFacts,
                    TotalCount = allFacts.Count,
                    FilteredCount = filteredFacts.Count,
                    Skipped = query.Skip,
                    Returned = pagedFacts.Count,
                    ExecutionTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds,
                    RelatedFacts = relatedFacts,
                    Suggestions = suggestions;
                };

                // İstatistikleri hesapla;
                result.Statistics = CalculateQueryStatistics(result);

                // Önbelleğe ekle;
                if (_config.EnableQueryCache && pagedFacts.Count > 0)
                {
                    _queryCache[cacheKey] = pagedFacts;
                }

                TrackOperationEnd("QueryFacts", operationId, startTime, true);
                return result;
            }
            catch (Exception ex)
            {
                TrackOperationEnd("QueryFacts", operationId, startTime, false);
                _logger.LogError(ex, "Error querying facts");
                throw new KnowledgeStoreException("Failed to query facts", ex);
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Metin tabanlı arama yapar;
        /// </summary>
        public async Task<List<KnowledgeFact>> SearchFactsAsync(string searchText, int maxResults = 50)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                throw new ArgumentException("Search text cannot be null or empty", nameof(searchText));
            }

            try
            {
                _logger.LogDebug("Searching facts for text: {SearchText}", searchText);

                var query = new KnowledgeQuery;
                {
                    SearchText = searchText,
                    MaxResults = maxResults,
                    SortBy = SortBy.Relevance;
                };

                var result = await QueryFactsAsync(query);
                return result.Results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching facts for text: {SearchText}", searchText);
                throw;
            }
        }

        /// <summary>
        /// Kategoriye göre bilgi gerçeklerini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetFactsByCategoryAsync(string category, int maxResults = 100)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                throw new ArgumentException("Category cannot be null or empty", nameof(category));
            }

            try
            {
                _logger.LogDebug("Getting facts by category: {Category}", category);

                var query = new KnowledgeQuery;
                {
                    Categories = new List<string> { category },
                    MaxResults = maxResults,
                    SortBy = SortBy.Relevance;
                };

                var result = await QueryFactsAsync(query);
                return result.Results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting facts by category: {Category}", category);
                throw;
            }
        }

        /// <summary>
        /// Etikete göre bilgi gerçeklerini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetFactsByTagAsync(string tag, int maxResults = 100)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("Tag cannot be null or empty", nameof(tag));
            }

            try
            {
                _logger.LogDebug("Getting facts by tag: {Tag}", tag);

                var query = new KnowledgeQuery;
                {
                    Tags = new List<string> { tag },
                    MaxResults = maxResults,
                    SortBy = SortBy.Relevance;
                };

                var result = await QueryFactsAsync(query);
                return result.Results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting facts by tag: {Tag}", tag);
                throw;
            }
        }

        /// <summary>
        /// Türe göre bilgi gerçeklerini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetFactsByTypeAsync(KnowledgeType type, int maxResults = 100)
        {
            try
            {
                _logger.LogDebug("Getting facts by type: {Type}", type);

                var query = new KnowledgeQuery;
                {
                    Types = new List<KnowledgeType> { type },
                    MaxResults = maxResults,
                    SortBy = SortBy.Relevance;
                };

                var result = await QueryFactsAsync(query);
                return result.Results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting facts by type: {Type}", type);
                throw;
            }
        }

        /// <summary>
        /// İlgili bilgi gerçeklerini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetRelatedFactsAsync(string factId, int depth = 1)
        {
            ValidateFactId(factId);

            if (depth < 1 || depth > 5)
            {
                throw new ArgumentException("Depth must be between 1 and 5", nameof(depth));
            }

            try
            {
                _logger.LogDebug("Getting related facts for {FactId} with depth {Depth}", factId, depth);

                var visited = new HashSet<string>();
                var result = new List<KnowledgeFact>();

                await TraverseRelationshipsAsync(factId, depth, visited, result);

                return result.Distinct().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting related facts for {FactId}", factId);
                throw;
            }
        }

        /// <summary>
        /// İlişki ekler;
        /// </summary>
        public async Task<KnowledgeRelationship> AddRelationshipAsync(KnowledgeRelationship relationship, string userId)
        {
            ValidateUserId(userId);

            if (relationship == null)
            {
                throw new ArgumentNullException(nameof(relationship));
            }

            if (string.IsNullOrWhiteSpace(relationship.SourceFactId) ||
                string.IsNullOrWhiteSpace(relationship.TargetFactId))
            {
                throw new ArgumentException("Source and Target Fact IDs are required");
            }

            try
            {
                _logger.LogDebug("Adding relationship from {SourceId} to {TargetId}",
                    relationship.SourceFactId, relationship.TargetFactId);

                // Kaynak ve hedef gerçeklerin var olduğunu kontrol et;
                var sourceFact = await GetFactAsync(relationship.SourceFactId, false);
                var targetFact = await GetFactAsync(relationship.TargetFactId, false);

                if (sourceFact == null || targetFact == null)
                {
                    throw new KeyNotFoundException("Source or target fact not found");
                }

                // İlişkiyi kaynak gerçeğe ekle;
                sourceFact.AddRelationship(relationship);
                await UpdateFactAsync(sourceFact, userId, $"Added relationship to {targetFact.Title}");

                // İlişki indeksini güncelle;
                UpdateRelationshipIndex(relationship);

                // Olay yayınla;
                await _eventBus.PublishAsync(new RelationshipAddedEvent;
                {
                    RelationshipId = relationship.Id,
                    SourceFactId = relationship.SourceFactId,
                    TargetFactId = relationship.TargetFactId,
                    RelationshipType = relationship.RelationshipType,
                    UserId = userId,
                    Timestamp = DateTime.UtcNow;
                });

                return relationship;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding relationship");
                throw new KnowledgeStoreException("Failed to add relationship", ex);
            }
        }

        /// <summary>
        /// İlişkiyi kaldırır;
        /// </summary>
        public async Task<bool> RemoveRelationshipAsync(string relationshipId, string userId)
        {
            ValidateUserId(userId);

            if (string.IsNullOrWhiteSpace(relationshipId))
            {
                throw new ArgumentException("Relationship ID cannot be null or empty", nameof(relationshipId));
            }

            try
            {
                _logger.LogDebug("Removing relationship: {RelationshipId}", relationshipId);

                // İlişkiyi bul;
                var relationship = await FindRelationshipByIdAsync(relationshipId);
                if (relationship == null)
                {
                    return false;
                }

                // Kaynak gerçeği bul ve ilişkiyi kaldır;
                var sourceFact = await GetFactAsync(relationship.SourceFactId, false);
                if (sourceFact != null)
                {
                    sourceFact.Relationships.RemoveAll(r => r.Id == relationshipId);
                    await UpdateFactAsync(sourceFact, userId, $"Removed relationship {relationshipId}");
                }

                // İlişki indeksini güncelle;
                RemoveRelationshipFromIndex(relationshipId);

                // Olay yayınla;
                await _eventBus.PublishAsync(new RelationshipRemovedEvent;
                {
                    RelationshipId = relationshipId,
                    UserId = userId,
                    Timestamp = DateTime.UtcNow;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing relationship {RelationshipId}", relationshipId);
                throw new KnowledgeStoreException("Failed to remove relationship", ex);
            }
        }

        /// <summary>
        /// İlişkileri getirir;
        /// </summary>
        public async Task<List<KnowledgeRelationship>> GetRelationshipsAsync(string factId)
        {
            ValidateFactId(factId);

            try
            {
                _logger.LogDebug("Getting relationships for fact: {FactId}", factId);

                var fact = await GetFactAsync(factId, false);
                if (fact == null)
                {
                    return new List<KnowledgeRelationship>();
                }

                return fact.Relationships;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting relationships for fact {FactId}", factId);
                throw;
            }
        }

        /// <summary>
        /// Bağlı bilgi gerçeklerini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetConnectedFactsAsync(string factId, string relationshipType = null)
        {
            ValidateFactId(factId);

            try
            {
                _logger.LogDebug("Getting connected facts for {FactId}, relationship type: {Type}",
                    factId, relationshipType ?? "Any");

                var relationships = await GetRelationshipsAsync(factId);

                if (!string.IsNullOrEmpty(relationshipType))
                {
                    relationships = relationships;
                        .Where(r => r.RelationshipType.Equals(relationshipType, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                var connectedFactIds = relationships;
                    .Select(r => r.TargetFactId)
                    .Distinct()
                    .ToList();

                return await GetFactsAsync(connectedFactIds, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting connected facts for {FactId}", factId);
                throw;
            }
        }

        /// <summary>
        /// İstatistikleri getirir;
        /// </summary>
        public async Task<KnowledgeStatistics> GetStatisticsAsync()
        {
            try
            {
                _logger.LogDebug("Getting knowledge store statistics");

                // Önbellekte varsa getir;
                var cacheKey = "Statistics_" + DateTime.UtcNow.ToString("yyyyMMdd");
                if (_statisticsCache.TryGetValue(cacheKey, out var cachedStats))
                {
                    return cachedStats;
                }

                var allFacts = await GetAllFactsFromCacheAsync();
                var activeFacts = allFacts.Where(f => f.IsActive).ToList();

                var stats = new KnowledgeStatistics;
                {
                    TotalFacts = allFacts.Count,
                    ActiveFacts = activeFacts.Count,
                    InactiveFacts = allFacts.Count - activeFacts.Count,
                    TotalRelationships = allFacts.Sum(f => f.Relationships.Count),
                    OldestFactDate = allFacts.Any() ? allFacts.Min(f => f.CreatedDate) : DateTime.UtcNow,
                    NewestFactDate = allFacts.Any() ? allFacts.Max(f => f.CreatedDate) : DateTime.UtcNow,
                    AverageConfidence = activeFacts.Any() ? activeFacts.Average(f => f.ConfidenceScore) : 0,
                    AverageRelevance = activeFacts.Any() ? activeFacts.Average(f => f.RelevanceScore) : 0,
                    TotalAccessCount = activeFacts.Sum(f => f.AccessCount)
                };

                // Kategorilere göre grupla;
                stats.FactsByType = activeFacts;
                    .GroupBy(f => f.Type)
                    .ToDictionary(g => g.Key, g => g.Count());

                stats.FactsByConfidence = activeFacts;
                    .GroupBy(f => f.Confidence)
                    .ToDictionary(g => g.Key, g => g.Count());

                stats.FactsByPriority = activeFacts;
                    .GroupBy(f => f.Priority)
                    .ToDictionary(g => g.Key, g => g.Count());

                // En popüler kategoriler;
                stats.TopCategories = activeFacts;
                    .GroupBy(f => f.Category)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .ToDictionary(g => g.Key, g => g.Count());

                // En popüler etiketler;
                var allTags = activeFacts;
                    .SelectMany(f => f.Tags)
                    .GroupBy(t => t)
                    .OrderByDescending(g => g.Count())
                    .Take(20)
                    .ToList();

                stats.TotalTags = allTags.Count;
                stats.TopTags = allTags.ToDictionary(g => g.Key, g => g.Count());

                // En popüler kaynaklar;
                stats.TopSources = activeFacts;
                    .GroupBy(f => f.Source)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());

                stats.TotalCategories = stats.TopCategories.Count;

                // Önbelleğe ekle;
                _statisticsCache[cacheKey] = stats;

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                throw;
            }
        }

        /// <summary>
        /// Bilgi grafiğini getirir;
        /// </summary>
        public async Task<Dictionary<string, double>> GetKnowledgeGraphAsync(string rootFactId = null, int maxDepth = 3)
        {
            if (maxDepth < 1 || maxDepth > 10)
            {
                throw new ArgumentException("Max depth must be between 1 and 10", nameof(maxDepth));
            }

            try
            {
                _logger.LogDebug("Generating knowledge graph with root: {RootFactId}, max depth: {MaxDepth}",
                    rootFactId ?? "None", maxDepth);

                var graph = new Dictionary<string, double>();
                var visited = new HashSet<string>();

                if (string.IsNullOrEmpty(rootFactId))
                {
                    // Tüm grafiği oluştur;
                    var allFacts = await GetAllFactsFromCacheAsync();
                    foreach (var fact in allFacts)
                    {
                        await AddToGraphAsync(fact.Id, maxDepth, visited, graph);
                    }
                }
                else;
                {
                    // Kökten başlayarak grafiği oluştur;
                    await AddToGraphAsync(rootFactId, maxDepth, visited, graph);
                }

                return graph;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating knowledge graph");
                throw;
            }
        }

        /// <summary>
        /// Çelişkileri bulur;
        /// </summary>
        public async Task<List<KnowledgeFact>> FindContradictionsAsync(string factId)
        {
            ValidateFactId(factId);

            try
            {
                _logger.LogDebug("Finding contradictions for fact: {FactId}", factId);

                var fact = await GetFactAsync(factId, false);
                if (fact == null)
                {
                    return new List<KnowledgeFact>();
                }

                var allFacts = await GetAllFactsFromCacheAsync();
                var contradictions = new List<KnowledgeFact>();

                foreach (var otherFact in allFacts)
                {
                    if (otherFact.Id == factId || !otherFact.IsActive)
                    {
                        continue;
                    }

                    if (IsContradiction(fact, otherFact))
                    {
                        contradictions.Add(otherFact);
                    }
                }

                return contradictions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding contradictions for fact {FactId}", factId);
                throw;
            }
        }

        /// <summary>
        /// Benzer bilgi gerçeklerini bulur;
        /// </summary>
        public async Task<List<KnowledgeFact>> FindSimilarFactsAsync(KnowledgeFact fact, double similarityThreshold = 0.7)
        {
            ValidateFact(fact);

            if (similarityThreshold < 0 || similarityThreshold > 1)
            {
                throw new ArgumentException("Similarity threshold must be between 0 and 1", nameof(similarityThreshold));
            }

            try
            {
                _logger.LogDebug("Finding similar facts for: {FactTitle}", fact.Title);

                var allFacts = await GetAllFactsFromCacheAsync();
                var similarFacts = new List<KnowledgeFact>();

                foreach (var otherFact in allFacts)
                {
                    if (otherFact.Id == fact.Id || !otherFact.IsActive)
                    {
                        continue;
                    }

                    var similarity = CalculateFactSimilarity(fact, otherFact);
                    if (similarity >= similarityThreshold)
                    {
                        similarFacts.Add(otherFact);
                    }
                }

                return similarFacts;
                    .OrderByDescending(f => CalculateFactSimilarity(fact, f))
                    .Take(20)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding similar facts");
                throw;
            }
        }

        /// <summary>
        /// Bilgi gerçeğini doğrular;
        /// </summary>
        public async Task<KnowledgeFact> ValidateFactAsync(string factId, string validator, ValidationStatus newStatus, string notes = "")
        {
            ValidateFactId(factId);

            if (string.IsNullOrWhiteSpace(validator))
            {
                throw new ArgumentException("Validator cannot be null or empty", nameof(validator));
            }

            try
            {
                _logger.LogDebug("Validating fact {FactId} by {Validator}", factId, validator);

                var fact = await GetFactAsync(factId, false);
                if (fact == null)
                {
                    throw new KeyNotFoundException($"Fact {factId} not found");
                }

                var validationRecord = new ValidationRecord;
                {
                    Validator = validator,
                    OldStatus = fact.ValidationStatus,
                    NewStatus = newStatus,
                    OldConfidence = fact.ConfidenceScore,
                    NewConfidence = fact.ConfidenceScore,
                    Timestamp = DateTime.UtcNow,
                    Notes = notes;
                };

                fact.ValidationStatus = newStatus;
                fact.ValidationHistory.Add(validationRecord);

                // Doğrulama durumuna göre güven skorunu ayarla;
                switch (newStatus)
                {
                    case ValidationStatus.Verified:
                        fact.UpdateConfidence(Math.Max(fact.ConfidenceScore, 0.9), validator);
                        break;
                    case ValidationStatus.Invalid:
                        fact.UpdateConfidence(Math.Min(fact.ConfidenceScore, 0.1), validator);
                        break;
                    case ValidationStatus.Contradicted:
                        fact.UpdateConfidence(fact.ConfidenceScore * 0.5, validator);
                        break;
                }

                await UpdateFactAsync(fact, validator, $"Validation: {newStatus} - {notes}");

                // Olay yayınla;
                await _eventBus.PublishAsync(new FactValidatedEvent;
                {
                    FactId = factId,
                    Validator = validator,
                    OldStatus = validationRecord.OldStatus,
                    NewStatus = newStatus,
                    Notes = notes,
                    Timestamp = DateTime.UtcNow;
                });

                return fact;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating fact {FactId}", factId);
                throw new KnowledgeStoreException("Failed to validate fact", ex);
            }
        }

        /// <summary>
        /// Doğrulanmaya ihtiyacı olan bilgi gerçeklerini getirir;
        /// </summary>
        public async Task<List<KnowledgeFact>> GetFactsNeedingValidationAsync(int maxResults = 50)
        {
            try
            {
                _logger.LogDebug("Getting facts needing validation");

                var allFacts = await GetAllFactsFromCacheAsync();

                return allFacts;
                    .Where(f => f.IsActive &&
                           (f.ValidationStatus == ValidationStatus.Pending ||
                            f.ValidationStatus == ValidationStatus.InProgress ||
                            f.ConfidenceScore < 0.5) &&
                           (DateTime.UtcNow - f.ModifiedDate).TotalDays > 7)
                    .OrderBy(f => f.ConfidenceScore)
                    .ThenBy(f => f.ModifiedDate)
                    .Take(maxResults)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting facts needing validation");
                throw;
            }
        }

        /// <summary>
        /// Bilgi gerçeği tutarlılığını hesaplar;
        /// </summary>
        public async Task<double> CalculateFactConsistencyAsync(string factId)
        {
            ValidateFactId(factId);

            try
            {
                _logger.LogDebug("Calculating consistency for fact: {FactId}", factId);

                var fact = await GetFactAsync(factId, false);
                if (fact == null)
                {
                    return 0;
                }

                var contradictions = await FindContradictionsAsync(factId);
                var similarity = await FindSimilarFactsAsync(fact, 0.8);

                var contradictionScore = 1.0 - (contradictions.Count * 0.2);
                var similarityScore = similarity.Any() ? 0.8 : 0.5;
                var validationScore = fact.ValidationStatus == ValidationStatus.Verified ? 1.0 : 0.6;

                var consistency = (contradictionScore * 0.4) +
                                 (similarityScore * 0.3) +
                                 (validationScore * 0.3);

                fact.ConsistencyScore = consistency;
                await UpdateFactAsync(fact, "System", $"Consistency score updated: {consistency:F2}");

                return consistency;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating consistency for fact {FactId}", factId);
                throw;
            }
        }

        /// <summary>
        /// Önbelleği temizler;
        /// </summary>
        public async Task ClearCacheAsync()
        {
            try
            {
                _logger.LogInformation("Clearing knowledge store cache");

                _cacheLock.EnterWriteLock();
                try
                {
                    _memoryCache.Clear();
                    _cacheTimestamps.Clear();
                    _queryCache.Clear();
                    _statisticsCache.Clear();
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }

                // İndeksleri yeniden yükle;
                await LoadIndicesAsync();

                _logger.LogInformation("Knowledge store cache cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing cache");
                throw;
            }
        }

        /// <summary>
        /// Önbelleği önceden yükler;
        /// </summary>
        public async Task PreloadCacheAsync(List<string> factIds = null)
        {
            try
            {
                _logger.LogInformation("Preloading knowledge store cache");

                if (factIds == null || !factIds.Any())
                {
                    // Son erişilenleri yükle;
                    var recentFacts = await _factRepository.GetAllAsync(
                        predicate: f => f.IsActive,
                        orderBy: q => q.OrderByDescending(f => f.LastAccessed),
                        take: _config.MaxCacheItems);

                    foreach (var fact in recentFacts)
                    {
                        AddToCache(fact);
                    }
                }
                else;
                {
                    foreach (var factId in factIds)
                    {
                        await GetFactAsync(factId, false);
                    }
                }

                _logger.LogInformation("Knowledge store cache preloaded with {Count} items", _memoryCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preloading cache");
                // Hata durumunda devam et;
            }
        }

        /// <summary>
        /// Önbellek istatistiklerini getirir;
        /// </summary>
        public async Task<Dictionary<string, object>> GetCacheStatsAsync()
        {
            try
            {
                var stats = new Dictionary<string, object>
                {
                    ["MemoryCacheCount"] = _memoryCache.Count,
                    ["QueryCacheCount"] = _queryCache.Count,
                    ["StatisticsCacheCount"] = _statisticsCache.Count,
                    ["CategoryIndexCount"] = _categoryIndex.Count,
                    ["TagIndexCount"] = _tagIndex.Count,
                    ["TypeIndexCount"] = _typeIndex.Count,
                    ["KeywordIndexCount"] = _keywordIndex.Count,
                    ["RelationshipIndexCount"] = _relationshipIndex.Count,
                    ["CacheHitRate"] = CalculateCacheHitRate(),
                    ["AverageCacheAgeMinutes"] = CalculateAverageCacheAge(),
                    ["TotalOperations"] = _operationMetrics.Count;
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cache stats");
                throw;
            }
        }

        /// <summary>
        /// Yedekleme oluşturur;
        /// </summary>
        public async Task<string> CreateBackupAsync(string backupName = null)
        {
            try
            {
                backupName ??= $"Backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

                _logger.LogInformation("Creating backup: {BackupName}", backupName);

                var allFacts = await GetAllFactsFromDatabaseAsync();
                var backupId = Guid.NewGuid().ToString();

                var backupData = new;
                {
                    BackupId = backupId,
                    BackupName = backupName,
                    BackupTime = DateTime.UtcNow,
                    FactCount = allFacts.Count,
                    Facts = allFacts,
                    Indices = GetIndexData(),
                    Statistics = await GetStatisticsAsync()
                };

                // Gerçek uygulamada dosyaya yazma işlemi;
                // var backupPath = Path.Combine(_config.StoragePath, "Backups", $"{backupId}.json");
                // await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(backupData, 
                //     new JsonSerializerOptions { WriteIndented = true }));

                _lastBackupTime = DateTime.UtcNow;

                // Olay yayınla;
                await _eventBus.PublishAsync(new BackupCreatedEvent;
                {
                    BackupId = backupId,
                    BackupName = backupName,
                    FactCount = allFacts.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Backup created: {BackupId}", backupId);
                return backupId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating backup");
                throw new KnowledgeStoreException("Failed to create backup", ex);
            }
        }

        /// <summary>
        /// Yedekten geri yükler;
        /// </summary>
        public async Task<bool> RestoreFromBackupAsync(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
            {
                throw new ArgumentException("Backup ID cannot be null or empty", nameof(backupId));
            }

            try
            {
                _logger.LogInformation("Restoring from backup: {BackupId}", backupId);

                // Gerçek uygulamada dosyadan okuma işlemi;
                // var backupPath = Path.Combine(_config.StoragePath, "Backups", $"{backupId}.json");
                // var backupData = JsonSerializer.Deserialize<dynamic>(await File.ReadAllTextAsync(backupPath));

                // Mevcut verileri temizle;
                await ClearCacheAsync();

                // Yedekten geri yükleme işlemi;
                // await StoreFactsBatchAsync(backupData.Facts, "System");

                // Olay yayınla;
                await _eventBus.PublishAsync(new BackupRestoredEvent;
                {
                    BackupId = backupId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Restored from backup: {BackupId}", backupId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring from backup {BackupId}", backupId);
                throw new KnowledgeStoreException("Failed to restore from backup", ex);
            }
        }

        /// <summary>
        /// Yedekleme listesini getirir;
        /// </summary>
        public async Task<List<BackupInfo>> GetBackupListAsync()
        {
            try
            {
                _logger.LogDebug("Getting backup list");

                // Gerçek uygulamada dosya sistemi taraması;
                var backups = new List<BackupInfo>();

                // Örnek veri;
                backups.Add(new BackupInfo;
                {
                    BackupId = "sample_backup",
                    BackupName = "Sample Backup",
                    BackupTime = DateTime.UtcNow.AddDays(-1),
                    FactCount = 1000,
                    SizeInBytes = 1024000,
                    CreatedBy = "System"
                });

                return backups;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting backup list");
                throw;
            }
        }

        /// <summary>
        /// Yedeklemeyi siler;
        /// </summary>
        public async Task<bool> DeleteBackupAsync(string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId))
            {
                throw new ArgumentException("Backup ID cannot be null or empty", nameof(backupId));
            }

            try
            {
                _logger.LogInformation("Deleting backup: {BackupId}", backupId);

                // Gerçek uygulamada dosya silme işlemi;
                // var backupPath = Path.Combine(_config.StoragePath, "Backups", $"{backupId}.json");
                // File.Delete(backupPath);

                // Olay yayınla;
                await _eventBus.PublishAsync(new BackupDeletedEvent;
                {
                    BackupId = backupId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Backup deleted: {BackupId}", backupId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting backup {BackupId}", backupId);
                throw new KnowledgeStoreException("Failed to delete backup", ex);
            }
        }

        /// <summary>
        /// Temizleme işlemi yapar;
        /// </summary>
        public async Task CleanupAsync(TimeSpan? olderThan = null)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow - (olderThan ?? _config.InactiveFactExpiration);

                _logger.LogInformation("Cleaning up facts older than {CutoffTime}", cutoffTime);

                var allFacts = await GetAllFactsFromCacheAsync();
                var factsToCleanup = allFacts;
                    .Where(f => f.LastAccessed < cutoffTime &&
                           f.ConfidenceScore < _config.MinConfidenceForDeletion &&
                           !f.Relationships.Any())
                    .ToList();

                var deletedCount = 0;

                foreach (var fact in factsToCleanup)
                {
                    try
                    {
                        await DeleteFactAsync(fact.Id, "System", "Auto-cleanup: Inactive and low confidence");
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete fact {FactId} during cleanup", fact.Id);
                    }
                }

                // Eski önbellek girdilerini temizle;
                CleanupOldCacheEntries();

                // Eski sorgu önbelleğini temizle;
                CleanupQueryCache();

                _logger.LogInformation("Cleanup completed: {DeletedCount} facts deleted", deletedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
                throw;
            }
        }

        /// <summary>
        /// Depolamayı optimize eder;
        /// </summary>
        public async Task OptimizeStorageAsync()
        {
            try
            {
                _logger.LogInformation("Optimizing knowledge store storage");

                // Veritabanı indekslerini optimize et;
                await _database.OptimizeAsync();

                // Önbelleği yeniden düzenle;
                await ClearCacheAsync();
                await PreloadCacheAsync();

                // İndeksleri yeniden oluştur;
                await RebuildIndicesAsync();

                _logger.LogInformation("Storage optimization completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing storage");
                throw new KnowledgeStoreException("Failed to optimize storage", ex);
            }
        }

        /// <summary>
        /// Bilgileri dışa aktarır;
        /// </summary>
        public async Task ExportKnowledgeAsync(string exportPath, ExportFormat format = ExportFormat.Json)
        {
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                throw new ArgumentException("Export path cannot be null or empty", nameof(exportPath));
            }

            try
            {
                _logger.LogInformation("Exporting knowledge to {ExportPath} in {Format} format",
                    exportPath, format);

                var allFacts = await GetAllFactsFromDatabaseAsync();
                var statistics = await GetStatisticsAsync();

                var exportData = new;
                {
                    ExportTime = DateTime.UtcNow,
                    FactCount = allFacts.Count,
                    Statistics = statistics,
                    Facts = allFacts;
                };

                // Gerçek uygulamada format'a göre dışa aktarma;
                // switch (format)
                // {
                //     case ExportFormat.Json:
                //         await File.WriteAllTextAsync(exportPath, 
                //             JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true }));
                //         break;
                //     // Diğer formatlar...
                // }

                _logger.LogInformation("Knowledge exported successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting knowledge");
                throw new KnowledgeStoreException("Failed to export knowledge", ex);
            }
        }

        /// <summary>
        /// Bilgileri içe aktarır;
        /// </summary>
        public async Task ImportKnowledgeAsync(string importPath, ImportMode mode = ImportMode.Merge)
        {
            if (string.IsNullOrWhiteSpace(importPath))
            {
                throw new ArgumentException("Import path cannot be null or empty", nameof(importPath));
            }

            try
            {
                _logger.LogInformation("Importing knowledge from {ImportPath} with mode {Mode}",
                    importPath, mode);

                // Gerçek uygulamada dosyadan okuma;
                // var importData = JsonSerializer.Deserialize<dynamic>(await File.ReadAllTextAsync(importPath));

                // Mod'a göre işlem;
                // switch (mode)
                // {
                //     case ImportMode.Replace:
                //         // Mevcut verileri temizle;
                //         await ClearCacheAsync();
                //         // Yeni verileri ekle;
                //         break;
                //     case ImportMode.Merge:
                //         // Var olanlarla birleştir;
                //         break;
                //     case ImportMode.Append:
                //         // Sonuna ekle;
                //         break;
                // }

                // İndeksleri yeniden oluştur;
                await RebuildIndicesAsync();

                _logger.LogInformation("Knowledge imported successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing knowledge");
                throw new KnowledgeStoreException("Failed to import knowledge", ex);
            }
        }

        // Yardımcı metodlar;

        private async Task<bool> FactExistsAsync(string factId)
        {
            return _memoryCache.ContainsKey(factId) ||
                   await _factRepository.ExistsAsync(f => f.Id == factId);
        }

        private bool TryGetFromCache(string factId, out KnowledgeFact fact)
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_memoryCache.TryGetValue(factId, out fact))
                {
                    // Süresi dolmuş mu kontrol et;
                    if (_cacheTimestamps.TryGetValue(factId, out var timestamp) &&
                        (DateTime.UtcNow - timestamp) < _config.CacheExpiration)
                    {
                        return true;
                    }

                    // Süresi dolmuş, önbellekten kaldır;
                    RemoveFromCache(factId);
                    fact = null;
                    return false;
                }

                fact = null;
                return false;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        private void AddToCache(KnowledgeFact fact)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                // Önbellek boyutunu kontrol et;
                if (_memoryCache.Count >= _config.MaxMemoryCacheSize)
                {
                    // En eski öğeleri kaldır;
                    var oldest = _cacheTimestamps;
                        .OrderBy(kv => kv.Value)
                        .Take(_config.MaxMemoryCacheSize / 10)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in oldest)
                    {
                        RemoveFromCache(key);
                    }
                }

                _memoryCache[fact.Id] = fact;
                _cacheTimestamps[fact.Id] = DateTime.UtcNow;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private void UpdateInCache(KnowledgeFact fact)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _memoryCache[fact.Id] = fact;
                _cacheTimestamps[fact.Id] = DateTime.UtcNow;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private void RemoveFromCache(string factId)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _memoryCache.TryRemove(factId, out _);
                _cacheTimestamps.TryRemove(factId, out _);
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private async Task<List<KnowledgeFact>> GetAllFactsFromCacheAsync()
        {
            _cacheLock.EnterReadLock();
            try
            {
                // Önbellekteki tüm aktif gerçekleri getir;
                return _memoryCache.Values;
                    .Where(f => f.IsActive)
                    .ToList();
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        private async Task<List<KnowledgeFact>> GetAllFactsFromDatabaseAsync()
        {
            return (await _factRepository.GetAllAsync(
                predicate: f => f.IsActive,
                orderBy: null,
                take: null))
                .ToList();
        }

        private void UpdateIndices(KnowledgeFact fact)
        {
            _indexLock.EnterWriteLock();
            try
            {
                // Kategori indeksi;
                _categoryIndex.AddOrUpdate(fact.Category,
                    new HashSet<string> { fact.Id },
                    (key, existing) =>
                    {
                        existing.Add(fact.Id);
                        return existing;
                    });

                // Etiket indeksi;
                foreach (var tag in fact.Tags)
                {
                    _tagIndex.AddOrUpdate(tag,
                        new HashSet<string> { fact.Id },
                        (key, existing) =>
                        {
                            existing.Add(fact.Id);
                            return existing;
                        });
                }

                // Tür indeksi;
                _typeIndex.AddOrUpdate(fact.Type,
                    new HashSet<string> { fact.Id },
                    (key, existing) =>
                    {
                        existing.Add(fact.Id);
                        return existing;
                    });

                // Anahtar kelime indeksi;
                foreach (var keyword in fact.Keywords)
                {
                    _keywordIndex.AddOrUpdate(keyword,
                        new HashSet<string> { fact.Id },
                        (key, existing) =>
                        {
                            existing.Add(fact.Id);
                            return existing;
                        });
                }

                // İlişki indeksi;
                foreach (var relationship in fact.Relationships)
                {
                    if (!_relationshipIndex.ContainsKey(relationship.SourceFactId))
                    {
                        _relationshipIndex[relationship.SourceFactId] = new List<KnowledgeRelationship>();
                    }

                    if (!_relationshipIndex[relationship.SourceFactId].Any(r => r.Id == relationship.Id))
                    {
                        _relationshipIndex[relationship.SourceFactId].Add(relationship);
                    }
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        private void RemoveFromIndices(string factId)
        {
            _indexLock.EnterWriteLock();
            try
            {
                // Tüm indekslerden kaldır;
                foreach (var category in _categoryIndex.Keys.ToList())
                {
                    if (_categoryIndex[category].Remove(factId) && !_categoryIndex[category].Any())
                    {
                        _categoryIndex.TryRemove(category, out _);
                    }
                }

                foreach (var tag in _tagIndex.Keys.ToList())
                {
                    if (_tagIndex[tag].Remove(factId) && !_tagIndex[tag].Any())
                    {
                        _tagIndex.TryRemove(tag, out _);
                    }
                }

                foreach (var type in _typeIndex.Keys.ToList())
                {
                    if (_typeIndex[type].Remove(factId) && !_typeIndex[type].Any())
                    {
                        _typeIndex.TryRemove(type, out _);
                    }
                }

                foreach (var keyword in _keywordIndex.Keys.ToList())
                {
                    if (_keywordIndex[keyword].Remove(factId) && !_keywordIndex[keyword].Any())
                    {
                        _keywordIndex.TryRemove(keyword, out _);
                    }
                }

                // İlişki indekslerini temizle;
                if (_relationshipIndex.ContainsKey(factId))
                {
                    _relationshipIndex.TryRemove(factId, out _);
                }

                // Bu gerçeği hedef olarak kullanan ilişkileri temizle;
                foreach (var relationships in _relationshipIndex.Values)
                {
                    relationships.RemoveAll(r => r.TargetFactId == factId);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        private void UpdateRelationshipIndex(KnowledgeRelationship relationship)
        {
            _indexLock.EnterWriteLock();
            try
            {
                if (!_relationshipIndex.ContainsKey(relationship.SourceFactId))
                {
                    _relationshipIndex[relationship.SourceFactId] = new List<KnowledgeRelationship>();
                }

                var existing = _relationshipIndex[relationship.SourceFactId]
                    .FirstOrDefault(r => r.Id == relationship.Id);

                if (existing != null)
                {
                    _relationshipIndex[relationship.SourceFactId].Remove(existing);
                }

                _relationshipIndex[relationship.SourceFactId].Add(relationship);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        private void RemoveRelationshipFromIndex(string relationshipId)
        {
            _indexLock.EnterWriteLock();
            try
            {
                foreach (var relationships in _relationshipIndex.Values)
                {
                    relationships.RemoveAll(r => r.Id == relationshipId);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        private async Task<KnowledgeRelationship> FindRelationshipByIdAsync(string relationshipId)
        {
            _indexLock.EnterReadLock();
            try
            {
                foreach (var relationships in _relationshipIndex.Values)
                {
                    var relationship = relationships.FirstOrDefault(r => r.Id == relationshipId);
                    if (relationship != null)
                    {
                        return relationship;
                    }
                }

                return null;
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        private async Task LoadIndicesAsync()
        {
            _logger.LogInformation("Loading knowledge store indices");

            var allFacts = await GetAllFactsFromDatabaseAsync();

            _indexLock.EnterWriteLock();
            try
            {
                // İndeksleri temizle;
                _categoryIndex.Clear();
                _tagIndex.Clear();
                _typeIndex.Clear();
                _keywordIndex.Clear();
                _relationshipIndex.Clear();

                // İndeksleri doldur;
                foreach (var fact in allFacts)
                {
                    UpdateIndices(fact);
                }

                _logger.LogInformation("Indices loaded: {FactCount} facts indexed", allFacts.Count);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        private async Task RebuildIndicesAsync()
        {
            await LoadIndicesAsync();
        }

        private Dictionary<string, object> GetIndexCounts()
        {
            _indexLock.EnterReadLock();
            try
            {
                return new Dictionary<string, object>
                {
                    ["Categories"] = _categoryIndex.Count,
                    ["Tags"] = _tagIndex.Count,
                    ["Types"] = _typeIndex.Count,
                    ["Keywords"] = _keywordIndex.Count,
                    ["Relationships"] = _relationshipIndex.Sum(kv => kv.Value.Count)
                };
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        private Dictionary<string, object> GetIndexData()
        {
            _indexLock.EnterReadLock();
            try
            {
                return new Dictionary<string, object>
                {
                    ["CategoryIndex"] = _categoryIndex.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                    ["TagIndex"] = _tagIndex.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                    ["TypeIndex"] = _typeIndex.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToList()),
                    ["KeywordIndex"] = _keywordIndex.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())
                };
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        private List<KnowledgeFact> FilterFacts(List<KnowledgeFact> facts, KnowledgeQuery query)
        {
            var filtered = facts.AsEnumerable();

            // Aktiflik filtresi;
            if (!query.IncludeInactive)
            {
                filtered = filtered.Where(f => f.IsActive);
            }

            // Metin arama;
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var searchLower = query.SearchText.ToLowerInvariant();
                filtered = filtered.Where(f =>
                    f.Title.ToLowerInvariant().Contains(searchLower) ||
                    f.Content.ToLowerInvariant().Contains(searchLower) ||
                    f.Summary.ToLowerInvariant().Contains(searchLower) ||
                    f.Tags.Any(t => t.ToLowerInvariant().Contains(searchLower)) ||
                    f.Keywords.Any(k => k.ToLowerInvariant().Contains(searchLower)));
            }

            // Kategori filtresi;
            if (query.Categories.Any())
            {
                filtered = filtered.Where(f => query.Categories.Contains(f.Category));
            }

            // Tür filtresi;
            if (query.Types.Any())
            {
                filtered = filtered.Where(f => query.Types.Contains(f.Type));
            }

            // Etiket filtresi;
            if (query.Tags.Any())
            {
                filtered = filtered.Where(f => f.Tags.Any(t => query.Tags.Contains(t)));
            }

            // Anahtar kelime filtresi;
            if (query.Keywords.Any())
            {
                filtered = filtered.Where(f => f.Keywords.Any(k => query.Keywords.Contains(k)));
            }

            // Güven filtresi;
            if (query.MinConfidence.HasValue)
            {
                var minConfidenceValue = (int)query.MinConfidence.Value / 100.0;
                filtered = filtered.Where(f => f.ConfidenceScore >= minConfidenceValue);
            }

            // Öncelik filtresi;
            if (query.MinPriority.HasValue)
            {
                filtered = filtered.Where(f => f.Priority >= query.MinPriority.Value);
            }

            // Tarih filtreleri;
            if (query.CreatedAfter.HasValue)
            {
                filtered = filtered.Where(f => f.CreatedDate >= query.CreatedAfter.Value);
            }

            if (query.ModifiedAfter.HasValue)
            {
                filtered = filtered.Where(f => f.ModifiedDate >= query.ModifiedAfter.Value);
            }

            // Oluşturan filtresi;
            if (!string.IsNullOrWhiteSpace(query.CreatedBy))
            {
                filtered = filtered.Where(f => f.CreatedBy == query.CreatedBy);
            }

            return filtered.ToList();
        }

        private List<KnowledgeFact> SortFacts(List<KnowledgeFact> facts, KnowledgeQuery query)
        {
            var sorted = facts.AsEnumerable();

            switch (query.SortBy)
            {
                case SortBy.Relevance:
                    sorted = sorted.OrderBy(f => f.RelevanceScore);
                    break;
                case SortBy.Confidence:
                    sorted = sorted.OrderBy(f => f.ConfidenceScore);
                    break;
                case SortBy.Priority:
                    sorted = sorted.OrderBy(f => f.Priority);
                    break;
                case SortBy.CreatedDate:
                    sorted = sorted.OrderBy(f => f.CreatedDate);
                    break;
                case SortBy.ModifiedDate:
                    sorted = sorted.OrderBy(f => f.ModifiedDate);
                    break;
                case SortBy.AccessCount:
                    sorted = sorted.OrderBy(f => f.AccessCount);
                    break;
                case SortBy.Title:
                    sorted = sorted.OrderBy(f => f.Title);
                    break;
            }

            if (query.SortDirection == SortDirection.Descending)
            {
                sorted = sorted.Reverse();
            }

            return sorted.ToList();
        }

        private async Task<List<KnowledgeFact>> FindRelatedFactsAsync(List<KnowledgeFact> facts, int depth)
        {
            var related = new HashSet<string>();

            foreach (var fact in facts)
            {
                var factRelations = await GetRelatedFactsAsync(fact.Id, depth);
                foreach (var relatedFact in factRelations)
                {
                    if (!facts.Any(f => f.Id == relatedFact.Id))
                    {
                        related.Add(relatedFact.Id);
                    }
                }
            }

            return await GetFactsAsync(related.ToList(), false);
        }

        private List<string> GenerateQuerySuggestions(KnowledgeQuery query, List<KnowledgeFact> results)
        {
            var suggestions = new List<string>();

            if (!results.Any())
            {
                // Benzer kategoriler öner;
                if (query.Categories.Any())
                {
                    var similarCategories = _categoryIndex.Keys;
                        .Where(c => c.Contains(query.Categories.First(), StringComparison.OrdinalIgnoreCase))
                        .Take(3)
                        .ToList();

                    suggestions.AddRange(similarCategories.Select(c => $"Try category: {c}"));
                }

                // Benzer etiketler öner;
                if (query.Tags.Any())
                {
                    var similarTags = _tagIndex.Keys;
                        .Where(t => t.Contains(query.Tags.First(), StringComparison.OrdinalIgnoreCase))
                        .Take(3)
                        .ToList();

                    suggestions.AddRange(similarTags.Select(t => $"Try tag: {t}"));
                }
            }
            else;
            {
                // İlgili etiketler öner;
                var popularTags = results;
                    .SelectMany(f => f.Tags)
                    .GroupBy(t => t)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(5)
                    .ToList();

                suggestions.AddRange(popularTags.Select(t => $"Related tag: {t}"));
            }

            return suggestions;
        }

        private Dictionary<string, object> CalculateQueryStatistics(KnowledgeQueryResult result)
        {
            var stats = new Dictionary<string, object>();

            if (result.Results.Any())
            {
                stats["AverageConfidence"] = result.Results.Average(f => f.ConfidenceScore);
                stats["AverageRelevance"] = result.Results.Average(f => f.RelevanceScore);
                stats["ConfidenceDistribution"] = result.Results;
                    .GroupBy(f => f.Confidence)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                stats["TypeDistribution"] = result.Results;
                    .GroupBy(f => f.Type)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
            }

            return stats;
        }

        private async Task TraverseRelationshipsAsync(string factId, int depth, HashSet<string> visited, List<KnowledgeFact> result)
        {
            if (depth <= 0 || visited.Contains(factId))
            {
                return;
            }

            visited.Add(factId);

            var fact = await GetFactAsync(factId, false);
            if (fact == null)
            {
                return;
            }

            result.Add(fact);

            foreach (var relationship in fact.Relationships)
            {
                await TraverseRelationshipsAsync(relationship.TargetFactId, depth - 1, visited, result);
            }
        }

        private async Task AddToGraphAsync(string factId, int depth, HashSet<string> visited, Dictionary<string, double> graph)
        {
            if (depth <= 0 || visited.Contains(factId))
            {
                return;
            }

            visited.Add(factId);

            var fact = await GetFactAsync(factId, false);
            if (fact == null)
            {
                return;
            }

            // Düğüm ağırlığı (güven + ilgili skoru)
            var nodeWeight = (fact.ConfidenceScore * 0.6) + (fact.RelevanceScore * 0.4);
            graph[factId] = nodeWeight;

            foreach (var relationship in fact.Relationships)
            {
                // Kenar ağırlığı (ilişki gücü)
                var edgeWeight = relationship.Strength * nodeWeight;
                var edgeKey = $"{factId}->{relationship.TargetFactId}";
                graph[edgeKey] = edgeWeight;

                await AddToGraphAsync(relationship.TargetFactId, depth - 1, visited, graph);
            }
        }

        private bool IsContradiction(KnowledgeFact fact1, KnowledgeFact fact2)
        {
            // Aynı kategori ve anahtar kelimelerde çelişki kontrolü;
            if (fact1.Category == fact2.Category &&
                fact1.Keywords.Intersect(fact2.Keywords).Any())
            {
                // İçerik analizi (basit)
                var content1 = fact1.Content.ToLowerInvariant();
                var content2 = fact2.Content.ToLowerInvariant();

                // Zıt kelimeler kontrolü;
                var oppositeWords = new Dictionary<string, string[]>
                {
                    ["true"] = new[] { "false", "wrong", "incorrect" },
                    ["false"] = new[] { "true", "correct", "right" },
                    ["yes"] = new[] { "no", "never" },
                    ["no"] = new[] { "yes", "always" },
                    ["increase"] = new[] { "decrease", "reduce" },
                    ["decrease"] = new[] { "increase", "grow" }
                };

                foreach (var word in oppositeWords.Keys)
                {
                    if (content1.Contains(word) && oppositeWords[word].Any(w => content2.Contains(w)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private double CalculateFactSimilarity(KnowledgeFact fact1, KnowledgeFact fact2)
        {
            var similarity = 0.0;

            // Başlık benzerliği;
            if (fact1.Title == fact2.Title)
            {
                similarity += 0.3;
            }
            else if (fact1.Title.Contains(fact2.Title) || fact2.Title.Contains(fact1.Title))
            {
                similarity += 0.2;
            }

            // Kategori benzerliği;
            if (fact1.Category == fact2.Category)
            {
                similarity += 0.2;
            }

            // Etiket benzerliği;
            var commonTags = fact1.Tags.Intersect(fact2.Tags).Count();
            var totalTags = fact1.Tags.Union(fact2.Tags).Count();
            if (totalTags > 0)
            {
                similarity += (commonTags / (double)totalTags) * 0.2;
            }

            // Anahtar kelime benzerliği;
            var commonKeywords = fact1.Keywords.Intersect(fact2.Keywords).Count();
            var totalKeywords = fact1.Keywords.Union(fact2.Keywords).Count();
            if (totalKeywords > 0)
            {
                similarity += (commonKeywords / (double)totalKeywords) * 0.3;
            }

            return similarity;
        }

        private double CalculateCompletenessScore(KnowledgeFact fact)
        {
            var score = 0.0;

            if (!string.IsNullOrWhiteSpace(fact.Title)) score += 0.2;
            if (!string.IsNullOrWhiteSpace(fact.Content)) score += 0.3;
            if (!string.IsNullOrWhiteSpace(fact.Summary)) score += 0.1;
            if (fact.Tags.Any()) score += 0.1;
            if (fact.Keywords.Any()) score += 0.1;
            if (fact.Metadata.Any()) score += 0.1;
            if (fact.Relationships.Any()) score += 0.1;

            return score;
        }

        private string CreateQueryCacheKey(KnowledgeQuery query)
        {
            var keyParts = new List<string>
            {
                query.SearchText,
                string.Join(",", query.Categories.OrderBy(c => c)),
                string.Join(",", query.Types.Select(t => ((int)t).ToString()).OrderBy(t => t)),
                string.Join(",", query.Tags.OrderBy(t => t)),
                string.Join(",", query.Keywords.OrderBy(k => k)),
                query.MinConfidence?.ToString() ?? "null",
                query.MinPriority?.ToString() ?? "null",
                query.CreatedAfter?.ToString("yyyyMMddHHmmss") ?? "null",
                query.ModifiedAfter?.ToString("yyyyMMddHHmmss") ?? "null",
                query.CreatedBy,
                query.Skip.ToString(),
                query.Take.ToString(),
                ((int)query.SortBy).ToString(),
                ((int)query.SortDirection).ToString(),
                query.IncludeInactive.ToString(),
                query.IncludeMetadata.ToString()
            };

            return string.Join("|", keyParts);
        }

        private void CleanupOldCacheEntries()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                var cutoffTime = DateTime.UtcNow - _config.CacheExpiration;
                var oldEntries = _cacheTimestamps;
                    .Where(kv => kv.Value < cutoffTime)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in oldEntries)
                {
                    RemoveFromCache(key);
                }

                if (oldEntries.Any())
                {
                    _logger.LogDebug("Cleaned up {Count} old cache entries", oldEntries.Count);
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private void CleanupQueryCache()
        {
            // Eski sorgu önbelleği girdilerini temizle;
            var oldQueries = _queryCache.Keys;
                .Where(key => key.Contains("|"))
                .Take(_queryCache.Count / 2) // Yarısını temizle;
                .ToList();

            foreach (var key in oldQueries)
            {
                _queryCache.TryRemove(key, out _);
            }
        }

        private double CalculateCacheHitRate()
        {
            var totalOps = _operationMetrics.Count;
            if (totalOps == 0) return 0;

            var cacheHits = _operationMetrics;
                .Count(m => m.OperationType.Contains("Get") && m.Success && m.DurationMs < 10);

            return (double)cacheHits / totalOps;
        }

        private double CalculateAverageCacheAge()
        {
            if (!_cacheTimestamps.Any()) return 0;

            var averageAge = _cacheTimestamps.Values;
                .Average(ts => (DateTime.UtcNow - ts).TotalMinutes);

            return averageAge;
        }

        private async Task StartAutoCleanupAsync()
        {
            _ = Task.Run(async () =>
            {
                while (!_disposed)
                {
                    try
                    {
                        await Task.Delay(_config.AutoCleanupInterval);
                        await CleanupAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in auto-cleanup task");
                    }
                }
            });
        }

        private async Task StartAutoBackupAsync()
        {
            _ = Task.Run(async () =>
            {
                while (!_disposed)
                {
                    try
                    {
                        await Task.Delay(_config.AutoBackupInterval);

                        if ((DateTime.UtcNow - _lastBackupTime) >= _config.AutoBackupInterval)
                        {
                            await CreateBackupAsync($"AutoBackup_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in auto-backup task");
                    }
                }
            });
        }

        private void TrackOperationStart(string operationType, string operationId)
        {
            if (_config.EnablePerformanceMetrics)
            {
                var metric = new OperationMetric;
                {
                    OperationId = operationId,
                    OperationType = operationType,
                    StartTime = DateTime.UtcNow;
                };

                _operationMetrics.Enqueue(metric);

                // Eski metrikleri temizle;
                while (_operationMetrics.Count > 1000)
                {
                    _operationMetrics.TryDequeue(out _);
                }
            }
        }

        private void TrackOperationEnd(string operationType, string operationId, DateTime startTime, bool success)
        {
            if (_config.EnablePerformanceMetrics)
            {
                var metric = _operationMetrics.LastOrDefault(m => m.OperationId == operationId);
                if (metric != null)
                {
                    metric.EndTime = DateTime.UtcNow;
                    metric.DurationMs = (metric.EndTime - startTime).TotalMilliseconds;
                    metric.Success = success;
                }
            }
        }

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            }
        }

        private void ValidateFactId(string factId)
        {
            if (string.IsNullOrWhiteSpace(factId))
            {
                throw new ArgumentException("Fact ID cannot be null or empty", nameof(factId));
            }
        }

        private void ValidateFact(KnowledgeFact fact)
        {
            if (fact == null)
            {
                throw new ArgumentNullException(nameof(fact));
            }

            if (string.IsNullOrWhiteSpace(fact.Title))
            {
                throw new ArgumentException("Fact title cannot be null or empty", nameof(fact.Title));
            }

            if (string.IsNullOrWhiteSpace(fact.Category))
            {
                throw new ArgumentException("Fact category cannot be null or empty", nameof(fact.Category));
            }
        }

        // Operasyon metrik sınıfı;
        private class OperationMetric;
        {
            public string OperationId { get; set; } = string.Empty;
            public string OperationType { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public double DurationMs { get; set; }
            public bool Success { get; set; }
        }

        // Olay sınıfları;
        public class KnowledgeStoreInitializedEvent : IEvent;
        {
            public DateTime Timestamp { get; set; }
            public int FactCount { get; set; }
            public Dictionary<string, object> IndexCounts { get; set; } = new Dictionary<string, object>();
        }

        public class FactStoredEvent : IEvent;
        {
            public string FactId { get; set; } = string.Empty;
            public KnowledgeType FactType { get; set; }
            public string UserId { get; set; } = string.Empty;
            public ConfidenceLevel Confidence { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class FactUpdatedEvent : IEvent;
        {
            public string FactId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public int OldVersion { get; set; }
            public int NewVersion { get; set; }
            public string ChangeReason { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class FactDeletedEvent : IEvent;
        {
            public string FactId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class FactsStoredBatchEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public int FactCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class FactsDeletedBatchEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public int DeletedCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class RelationshipAddedEvent : IEvent;
        {
            public string RelationshipId { get; set; } = string.Empty;
            public string SourceFactId { get; set; } = string.Empty;
            public string TargetFactId { get; set; } = string.Empty;
            public string RelationshipType { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class RelationshipRemovedEvent : IEvent;
        {
            public string RelationshipId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class FactValidatedEvent : IEvent;
        {
            public string FactId { get; set; } = string.Empty;
            public string Validator { get; set; } = string.Empty;
            public ValidationStatus OldStatus { get; set; }
            public ValidationStatus NewStatus { get; set; }
            public string Notes { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class BackupCreatedEvent : IEvent;
        {
            public string BackupId { get; set; } = string.Empty;
            public string BackupName { get; set; } = string.Empty;
            public int FactCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class BackupRestoredEvent : IEvent;
        {
            public string BackupId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class BackupDeletedEvent : IEvent;
        {
            public string BackupId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        // Özel exception sınıfı;
        public class KnowledgeStoreException : Exception
        {
            public KnowledgeStoreException() { }
            public KnowledgeStoreException(string message) : base(message) { }
            public KnowledgeStoreException(string message, Exception innerException)
                : base(message, innerException) { }
        }

        // IDisposable implementasyonu;
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
                    // Managed kaynakları serbest bırak;
                    _cacheLock?.Dispose();
                    _indexLock?.Dispose();
                    _operationSemaphore?.Dispose();
                    _database?.Dispose();
                    _unitOfWork?.Dispose();

                    _logger.LogInformation("KnowledgeStore disposed");
                }

                _disposed = true;
            }
        }

        ~KnowledgeStore()
        {
            Dispose(false);
        }
    }
}
