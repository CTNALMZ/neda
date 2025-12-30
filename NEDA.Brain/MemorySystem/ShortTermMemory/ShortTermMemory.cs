using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.ExceptionHandling.RecoveryStrategies;
using NEDA.Brain.MemorySystem.PatternStorage;

namespace NEDA.Brain.MemorySystem.ShortTermMemory;
{
    /// <summary>
    /// Kısa süreli bellek sistemini yöneten ana sınıf.
    /// Hızlı erişim için optimize edilmiş, kapasite sınırlı bellek deposu.
    /// </summary>
    public class ShortTermMemory : IShortTermMemory, IDisposable;
    {
        private readonly ConcurrentDictionary<string, ShortTermMemoryItem> _memoryStore;
        private readonly ConcurrentQueue<MemoryAccess> _accessLog;
        private readonly ConcurrentDictionary<string, MemoryChunk> _chunks;
        private readonly ConcurrentDictionary<string, object> _syncLocks;

        private readonly SemaphoreSlim _cleanupSemaphore;
        private readonly Timer _maintenanceTimer;
        private readonly RecoveryEngine _recoveryEngine;

        private volatile bool _isDisposed;
        private volatile bool _isInitialized;
        private DateTime _lastCleanup;
        private ShortTermMemoryStatistics _statistics;
        private ShortTermMemoryConfig _config;

        /// <summary>
        /// Kısa süreli bellek sisteminin geçerli kapasite kullanımı;
        /// </summary>
        public MemoryCapacityUsage CapacityUsage => CalculateCapacityUsage();

        /// <summary>
        /// Sistem istatistikleri (salt okunur)
        /// </summary>
        public ShortTermMemoryStatistics Statistics => _statistics.Clone();

        /// <summary>
        /// Sistem yapılandırması;
        /// </summary>
        public ShortTermMemoryConfig Config => _config.Clone();

        /// <summary>
        /// Kısa süreli bellek sistemini başlatır;
        /// </summary>
        public ShortTermMemory()
        {
            _config = ShortTermMemoryConfig.Default;
            _memoryStore = new ConcurrentDictionary<string, ShortTermMemoryItem>();
            _accessLog = new ConcurrentQueue<MemoryAccess>();
            _chunks = new ConcurrentDictionary<string, MemoryChunk>();
            _syncLocks = new ConcurrentDictionary<string, object>();

            _cleanupSemaphore = new SemaphoreSlim(1, 1);
            _recoveryEngine = new RecoveryEngine();

            _statistics = new ShortTermMemoryStatistics();
            _lastCleanup = DateTime.UtcNow;

            InitializeSystem();
        }

        /// <summary>
        /// Özel yapılandırma ile kısa süreli bellek sistemini başlatır;
        /// </summary>
        public ShortTermMemory(ShortTermMemoryConfig config) : this()
        {
            if (config != null)
            {
                _config = config.Clone();
            }
        }

        /// <summary>
        /// Sistemin başlangıç yapılandırmasını yapar;
        /// </summary>
        private void InitializeSystem()
        {
            try
            {
                // Temel bellek segmentlerini oluştur;
                CreateDefaultMemorySegments();

                // İstatistikleri sıfırla;
                ResetStatistics();

                // Bakım zamanlayıcısını başlat;
                _maintenanceTimer = new Timer(
                    PerformMaintenanceCallback,
                    null,
                    TimeSpan.FromMinutes(_config.MaintenanceIntervalMinutes),
                    TimeSpan.FromMinutes(_config.MaintenanceIntervalMinutes));

                _isInitialized = true;
                Logger.LogInformation("Short-term memory system initialized successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to initialize short-term memory system");
                throw new ShortTermMemoryException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Varsayılan bellek segmentlerini oluşturur;
        /// </summary>
        private void CreateDefaultMemorySegments()
        {
            // Çalışma belleği segmenti;
            var workingMemory = new MemorySegment;
            {
                SegmentId = "WorkingMemory",
                Name = "Working Memory",
                Description = "Active cognitive processing area",
                Capacity = _config.WorkingMemoryCapacity,
                RetentionPolicy = RetentionPolicy.HighPriority,
                EvictionStrategy = EvictionStrategy.LRU,
                IsVolatile = true;
            };

            // Aktif bağlam segmenti;
            var activeContext = new MemorySegment;
            {
                SegmentId = "ActiveContext",
                Name = "Active Context",
                Description = "Current situational context storage",
                Capacity = _config.ContextMemoryCapacity,
                RetentionPolicy = RetentionPolicy.Contextual,
                EvictionStrategy = EvictionStrategy.ContextBased,
                IsVolatile = true;
            };

            // Son erişimler segmenti;
            var recentAccess = new MemorySegment;
            {
                SegmentId = "RecentAccess",
                Name = "Recent Access Cache",
                Description = "Recently accessed items for quick retrieval",
                Capacity = _config.RecentAccessCapacity,
                RetentionPolicy = RetentionPolicy.Recent,
                EvictionStrategy = EvictionStrategy.FIFO,
                IsVolatile = true;
            };

            // Segmentleri kaydet;
            RegisterMemorySegment(workingMemory);
            RegisterMemorySegment(activeContext);
            RegisterMemorySegment(recentAccess);
        }

        /// <summary>
        /// Bellek segmentini kaydeder;
        /// </summary>
        private void RegisterMemorySegment(MemorySegment segment)
        {
            var chunk = new MemoryChunk;
            {
                ChunkId = segment.SegmentId,
                Segment = segment,
                Items = new ConcurrentDictionary<string, ShortTermMemoryItem>(),
                AccessQueue = new ConcurrentQueue<string>(),
                LastMaintenance = DateTime.UtcNow;
            };

            _chunks[segment.SegmentId] = chunk;
            Logger.LogDebug($"Memory segment registered: {segment.Name}");
        }

        /// <summary>
        /// Belleğe bir öğe ekler;
        /// </summary>
        public async Task<string> StoreAsync(ShortTermMemoryItem item, StoreOptions options = null)
        {
            ValidateSystemState();
            ValidateMemoryItem(item);

            options ??= StoreOptions.Default;
            var storeId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                // Kapasite kontrolü yap;
                await EnsureCapacityAsync(options.Priority);

                // Öğeyi hazırla;
                PrepareItemForStorage(item, storeId, options);

                // Uygun segmenti belirle;
                var targetSegment = DetermineTargetSegment(item, options);

                // Segmente ekle;
                var success = await AddToSegmentAsync(targetSegment, item);

                if (!success)
                {
                    // Alternatif segment dene;
                    var alternativeSegment = FindAlternativeSegment(item, options);
                    success = await AddToSegmentAsync(alternativeSegment, item);
                }

                if (!success)
                {
                    // Zorunlu yer aç;
                    await ForceMakeSpaceAsync(item, options);
                    success = await AddToSegmentAsync(targetSegment, item);
                }

                if (success)
                {
                    // Erişim kaydı ekle;
                    RecordAccess(storeId, AccessType.Store, options.Context);

                    // İstatistikleri güncelle;
                    UpdateStatistics(true, item.ContentSize, 1);

                    // Bağlantıları oluştur;
                    await CreateAssociationsAsync(item, options);

                    Logger.LogDebug($"Item stored successfully: {storeId}");
                    return storeId;
                }

                throw new ShortTermMemoryException("Failed to store item in any segment");
            }
            catch (Exception ex)
            {
                // Kurtarma stratejisi uygula;
                var fallbackId = await ApplyStorageRecoveryStrategyAsync(item, options, ex);

                // İstatistikleri güncelle;
                UpdateStatistics(false, item.ContentSize, 0);

                Logger.LogWarning(ex, $"Storage failed for item, using fallback: {fallbackId}");
                return fallbackId;
            }
        }

        /// <summary>
        /// Bellekten bir öğe alır;
        /// </summary>
        public async Task<ShortTermMemoryItem> RetrieveAsync(string memoryId, RetrievalContext context = null)
        {
            ValidateSystemState();
            ValidateMemoryId(memoryId);

            var startTime = DateTime.UtcNow;
            context ??= new RetrievalContext();

            try
            {
                // Öğeyi bul;
                var item = FindMemoryItem(memoryId);

                if (item == null)
                {
                    // Önbellekten kontrol et;
                    item = await CheckMemoryCacheAsync(memoryId);

                    if (item == null)
                    {
                        // İlişkisel zincirleri kontrol et;
                        item = await SearchAssociativeChainAsync(memoryId, context);
                    }
                }

                if (item != null)
                {
                    // Erişim kaydı ekle;
                    RecordAccess(memoryId, AccessType.Retrieve, context);

                    // Önceliği güncelle;
                    UpdateItemPriority(item, context);

                    // Önbelleğe al;
                    await CacheItemAsync(item);

                    // İstatistikleri güncelle;
                    UpdateStatistics(true, 0, 1, true);

                    Logger.LogDebug($"Item retrieved successfully: {memoryId}");
                    return item;
                }

                throw new ShortTermMemoryException($"Item not found: {memoryId}");
            }
            catch (Exception ex)
            {
                // Kurtarma stratejisi uygula;
                var fallbackItem = await ApplyRetrievalRecoveryStrategyAsync(memoryId, context, ex);

                // İstatistikleri güncelle;
                UpdateStatistics(false, 0, 0);

                Logger.LogWarning(ex, $"Retrieval failed for item: {memoryId}");
                return fallbackItem;
            }
        }

        /// <summary>
        /// Bellekten bir öğeyi kaldırır;
        /// </summary>
        public async Task<bool> RemoveAsync(string memoryId, RemovalContext context = null)
        {
            ValidateSystemState();
            ValidateMemoryId(memoryId);

            context ??= RemovalContext.Default;
            var startTime = DateTime.UtcNow;

            try
            {
                // Öğeyi bul;
                var item = FindMemoryItem(memoryId);

                if (item == null)
                {
                    Logger.LogWarning($"Item not found for removal: {memoryId}");
                    return false;
                }

                // Kaldırma politikasını kontrol et;
                if (!CanRemoveItem(item, context))
                {
                    Logger.LogWarning($"Cannot remove protected item: {memoryId}");
                    return false;
                }

                // Segmentten kaldır;
                var removed = await RemoveFromSegmentAsync(item);

                if (removed)
                {
                    // İlişkileri temizle;
                    await CleanupAssociationsAsync(item);

                    // Önbellekten kaldır;
                    await RemoveFromCacheAsync(memoryId);

                    // Erişim kaydı ekle;
                    RecordAccess(memoryId, AccessType.Remove, new RetrievalContext());

                    // İstatistikleri güncelle;
                    UpdateStatistics(true, -item.ContentSize, 0);

                    Logger.LogDebug($"Item removed successfully: {memoryId}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to remove item: {memoryId}");
                UpdateStatistics(false, 0, 0);
                return false;
            }
        }

        /// <summary>
        /// Bağlama göre bellek öğelerini arar;
        /// </summary>
        public async Task<List<ShortTermMemoryItem>> RetrieveByContextAsync(RetrievalContext context)
        {
            ValidateSystemState();
            ValidateContext(context);

            var startTime = DateTime.UtcNow;
            var results = new List<ShortTermMemoryItem>();

            try
            {
                // Aktif bağlam segmentinde ara;
                var contextSegment = GetSegment("ActiveContext");
                if (contextSegment != null)
                {
                    var contextItems = await SearchInSegmentByContextAsync(contextSegment, context);
                    results.AddRange(contextItems);
                }

                // Diğer segmentlerde ara;
                foreach (var chunk in _chunks.Values)
                {
                    if (chunk.Segment.SegmentId != "ActiveContext")
                    {
                        var segmentItems = await SearchInSegmentByContextAsync(chunk, context);
                        results.AddRange(segmentItems);
                    }
                }

                // Sonuçları derecelendir;
                var rankedResults = RankResultsByContext(results, context);

                // İstatistikleri güncelle;
                UpdateStatistics(true, 0, rankedResults.Count);

                Logger.LogDebug($"Context search completed: {rankedResults.Count} items found");
                return rankedResults;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Context search failed");
                UpdateStatistics(false, 0, 0);
                return new List<ShortTermMemoryItem>();
            }
        }

        /// <summary>
        /// Anahtar kelimelere göre bellek öğelerini arar;
        /// </summary>
        public async Task<List<ShortTermMemoryItem>> SearchAsync(string query, RetrievalContext context = null)
        {
            ValidateSystemState();

            if (string.IsNullOrWhiteSpace(query))
                return new List<ShortTermMemoryItem>();

            context ??= new RetrievalContext();
            var startTime = DateTime.UtcNow;
            var results = new List<ShortTermMemoryItem>();

            try
            {
                // Anahtar kelimeleri çıkar;
                var keywords = ExtractKeywords(query);

                // Tüm segmentlerde ara;
                foreach (var chunk in _chunks.Values)
                {
                    var segmentResults = await SearchInSegmentByKeywordsAsync(chunk, keywords, context);
                    results.AddRange(segmentResults);
                }

                // Yinelenenleri kaldır;
                results = results;
                    .GroupBy(r => r.MemoryId)
                    .Select(g => g.First())
                    .ToList();

                // Sonuçları derecelendir;
                var rankedResults = RankResultsByRelevance(results, keywords, context);

                // İstatistikleri güncelle;
                UpdateStatistics(true, 0, rankedResults.Count);

                Logger.LogDebug($"Search completed: {rankedResults.Count} items found for query: {query}");
                return rankedResults;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Search failed for query: {query}");
                UpdateStatistics(false, 0, 0);
                return new List<ShortTermMemoryItem>();
            }
        }

        /// <summary>
        /// Bellek öğesini günceller;
        /// </summary>
        public async Task<bool> UpdateAsync(string memoryId, Action<ShortTermMemoryItem> updateAction, UpdateContext context = null)
        {
            ValidateSystemState();
            ValidateMemoryId(memoryId);

            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            context ??= UpdateContext.Default;
            var startTime = DateTime.UtcNow;

            try
            {
                // Öğeyi bul;
                var item = FindMemoryItem(memoryId);

                if (item == null)
                {
                    Logger.LogWarning($"Item not found for update: {memoryId}");
                    return false;
                }

                // Güncelleme kilidi al;
                var lockKey = $"update_{memoryId}";
                var lockObject = _syncLocks.GetOrAdd(lockKey, _ => new object());

                lock (lockObject)
                {
                    // Güncelleme öncesi yedeği al;
                    var backup = item.Clone();

                    try
                    {
                        // Güncellemeyi uygula;
                        updateAction(item);

                        // Geçerliliği doğrula;
                        ValidateMemoryItem(item);

                        // Zaman damgasını güncelle;
                        item.LastModified = DateTime.UtcNow;
                        item.ModificationCount++;

                        // Versiyon oluştur;
                        if (context.CreateVersion)
                        {
                            item.Versions.Add(new MemoryVersion;
                            {
                                VersionId = Guid.NewGuid().ToString(),
                                Timestamp = DateTime.UtcNow,
                                Data = backup,
                                ChangeDescription = context.ChangeDescription;
                            });
                        }

                        // Segmenti güncelle;
                        var segmentUpdated = UpdateItemInSegment(item);

                        if (segmentUpdated)
                        {
                            // Erişim kaydı ekle;
                            RecordAccess(memoryId, AccessType.Update, new RetrievalContext());

                            // İstatistikleri güncelle;
                            UpdateStatistics(true, item.ContentSize - backup.ContentSize, 0);

                            Logger.LogDebug($"Item updated successfully: {memoryId}");
                            return true;
                        }

                        // Geri al;
                        item = backup;
                        return false;
                    }
                    catch
                    {
                        // Geri al;
                        item = backup;
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to update item: {memoryId}");
                UpdateStatistics(false, 0, 0);
                return false;
            }
        }

        /// <summary>
        /// Bellek parçalarını birleştirir;
        /// </summary>
        public async Task<bool> ConsolidateAsync(ConsolidationContext context = null)
        {
            ValidateSystemState();

            context ??= ConsolidationContext.Default;
            var startTime = DateTime.UtcNow;

            try
            {
                // Konsolidasyon kilidi al;
                if (!await _cleanupSemaphore.WaitAsync(TimeSpan.FromSeconds(30)))
                {
                    throw new ShortTermMemoryException("Could not acquire consolidation lock");
                }

                try
                {
                    Logger.LogInformation("Starting memory consolidation...");

                    // Her segment için konsolidasyon yap;
                    var totalConsolidated = 0;
                    var totalFreed = 0;

                    foreach (var chunk in _chunks.Values)
                    {
                        var result = await ConsolidateSegmentAsync(chunk, context);
                        totalConsolidated += result.ItemsConsolidated;
                        totalFreed += result.MemoryFreed;
                    }

                    // Parçalanmış öğeleri birleştir;
                    var defragResult = await DefragmentMemoryAsync(context);
                    totalConsolidated += defragResult.ItemsConsolidated;
                    totalFreed += defragResult.MemoryFreed;

                    // İstatistikleri güncelle;
                    _statistics.LastConsolidation = DateTime.UtcNow;
                    _statistics.TotalConsolidations++;

                    Logger.LogInformation($"Memory consolidation completed: {totalConsolidated} items consolidated, {totalFreed} bytes freed");
                    return totalConsolidated > 0;
                }
                finally
                {
                    _cleanupSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Memory consolidation failed");
                return false;
            }
        }

        /// <summary>
        /// Bellek durumunu temizler;
        /// </summary>
        public async Task ClearAsync(ClearContext context = null)
        {
            ValidateSystemState();

            context ??= ClearContext.Default;

            try
            {
                Logger.LogWarning("Clearing short-term memory...");

                // Temizleme kilidi al;
                if (!await _cleanupSemaphore.WaitAsync(TimeSpan.FromSeconds(30)))
                {
                    throw new ShortTermMemoryException("Could not acquire cleanup lock");
                }

                try
                {
                    // İçeriğe göre temizleme stratejisi;
                    switch (context.ClearType)
                    {
                        case ClearType.Full:
                            await ClearAllSegmentsAsync();
                            break;

                        case ClearType.Expired:
                            await ClearExpiredItemsAsync(context);
                            break;

                        case ClearType.LowPriority:
                            await ClearLowPriorityItemsAsync(context);
                            break;

                        case ClearType.ContextBased:
                            await ClearByContextAsync(context);
                            break;
                    }

                    // Önbelleği temizle;
                    await ClearCacheAsync();

                    // İstatistikleri sıfırla;
                    ResetStatistics();

                    Logger.LogInformation("Short-term memory cleared successfully");
                }
                finally
                {
                    _cleanupSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to clear short-term memory");
                throw;
            }
        }

        /// <summary>
        /// Sistem durumunu doğrular;
        /// </summary>
        public async Task<bool> ValidateHealthAsync()
        {
            try
            {
                if (!_isInitialized || _isDisposed)
                    return false;

                // Temel bileşen kontrolü;
                var componentsHealthy = _memoryStore != null &&
                                      _accessLog != null &&
                                      _chunks != null;

                if (!componentsHealthy)
                    return false;

                // Kapasite kontrolü;
                var capacityUsage = CalculateCapacityUsage();
                if (capacityUsage.UsagePercentage > 0.95)
                    return false;

                // Segment sağlığı kontrolü;
                foreach (var chunk in _chunks.Values)
                {
                    if (!await ValidateSegmentHealthAsync(chunk))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sistem istatistiklerini alır;
        /// </summary>
        public ShortTermMemoryStatistics GetStatistics()
        {
            ValidateSystemState();
            return _statistics.Clone();
        }

        /// <summary>
        /// Sistem yapılandırmasını günceller;
        /// </summary>
        public void UpdateConfig(ShortTermMemoryConfig newConfig)
        {
            ValidateSystemState();

            if (newConfig == null)
                throw new ArgumentNullException(nameof(newConfig));

            lock (_syncLocks.GetOrAdd("config", _ => new object()))
            {
                _config = newConfig.Clone();

                // Bakım zamanlayıcısını güncelle;
                _maintenanceTimer?.Change(
                    TimeSpan.FromMinutes(_config.MaintenanceIntervalMinutes),
                    TimeSpan.FromMinutes(_config.MaintenanceIntervalMinutes));

                Logger.LogInformation("Short-term memory configuration updated");
            }
        }

        /// <summary>
        /// Bellek kapasitesi kullanımını hesaplar;
        /// </summary>
        private MemoryCapacityUsage CalculateCapacityUsage()
        {
            var totalItems = 0;
            var totalSize = 0L;
            var totalCapacity = 0L;

            foreach (var chunk in _chunks.Values)
            {
                totalItems += chunk.Items.Count;
                totalSize += chunk.Items.Values.Sum(i => i.ContentSize);
                totalCapacity += chunk.Segment.Capacity;
            }

            var usagePercentage = totalCapacity > 0 ? (double)totalSize / totalCapacity : 0;

            return new MemoryCapacityUsage;
            {
                TotalItems = totalItems,
                TotalSize = totalSize,
                TotalCapacity = totalCapacity,
                UsagePercentage = usagePercentage,
                AverageItemSize = totalItems > 0 ? totalSize / totalItems : 0,
                SegmentCount = _chunks.Count;
            };
        }

        /// <summary>
        /// Kapasiteyi sağlar;
        /// </summary>
        private async Task EnsureCapacityAsync(PriorityLevel priority)
        {
            var capacityUsage = CalculateCapacityUsage();

            if (capacityUsage.UsagePercentage < _config.HighWatermark)
                return;

            // Temizleme gerekli;
            Logger.LogInformation($"Memory capacity high ({capacityUsage.UsagePercentage:P2}), performing cleanup...");

            await _cleanupSemaphore.WaitAsync();
            try
            {
                await PerformCapacityCleanupAsync(priority);
            }
            finally
            {
                _cleanupSemaphore.Release();
            }
        }

        /// <summary>
        /// Kapasite temizliği yapar;
        /// </summary>
        private async Task PerformCapacityCleanupAsync(PriorityLevel currentPriority)
        {
            var itemsToRemove = new List<ShortTermMemoryItem>();

            // Düşük öncelikli öğeleri topla;
            foreach (var chunk in _chunks.Values)
            {
                var lowPriorityItems = chunk.Items.Values;
                    .Where(i => i.Priority < currentPriority && CanEvictItem(i, chunk.Segment))
                    .OrderBy(i => i.Priority)
                    .ThenBy(i => i.LastAccessed);

                itemsToRemove.AddRange(lowPriorityItems.Take(10));
            }

            // Süresi dolmuş öğeleri topla;
            var expiredItems = _memoryStore.Values;
                .Where(i => i.ExpirationTime.HasValue && i.ExpirationTime.Value < DateTime.UtcNow)
                .ToList();

            itemsToRemove.AddRange(expiredItems);

            // En az kullanılan öğeleri topla (LRU)
            var lruItems = _memoryStore.Values;
                .OrderBy(i => i.LastAccessed)
                .Take(20)
                .ToList();

            itemsToRemove.AddRange(lruItems);

            // Yinelenenleri kaldır;
            itemsToRemove = itemsToRemove;
                .GroupBy(i => i.MemoryId)
                .Select(g => g.First())
                .Take(_config.BatchCleanupSize)
                .ToList();

            // Öğeleri kaldır;
            foreach (var item in itemsToRemove)
            {
                await RemoveFromSegmentAsync(item);
            }

            Logger.LogInformation($"Capacity cleanup completed: {itemsToRemove.Count} items removed");
        }

        /// <summary>
        /// Bellek öğesini segmentten kaldırır;
        /// </summary>
        private async Task<bool> RemoveFromSegmentAsync(ShortTermMemoryItem item)
        {
            if (string.IsNullOrEmpty(item.SegmentId))
                return false;

            if (!_chunks.TryGetValue(item.SegmentId, out var chunk))
                return false;

            var removed = chunk.Items.TryRemove(item.MemoryId, out _);

            if (removed)
            {
                // Ana depodan da kaldır;
                _memoryStore.TryRemove(item.MemoryId, out _);

                // İlişkileri temizle;
                await CleanupAssociationsAsync(item);
            }

            return removed;
        }

        /// <summary>
        /// Bellek öğesini segmentte günceller;
        /// </summary>
        private bool UpdateItemInSegment(ShortTermMemoryItem item)
        {
            if (string.IsNullOrEmpty(item.SegmentId))
                return false;

            if (!_chunks.TryGetValue(item.SegmentId, out var chunk))
                return false;

            // Öğeyi güncelle;
            chunk.Items[item.MemoryId] = item;

            // Ana depoyu da güncelle;
            _memoryStore[item.MemoryId] = item;

            return true;
        }

        /// <summary>
        /// Bellek öğesini bulur;
        /// </summary>
        private ShortTermMemoryItem FindMemoryItem(string memoryId)
        {
            // Önce ana depoda ara;
            if (_memoryStore.TryGetValue(memoryId, out var item))
                return item;

            // Segmentlerde ara;
            foreach (var chunk in _chunks.Values)
            {
                if (chunk.Items.TryGetValue(memoryId, out item))
                    return item;
            }

            return null;
        }

        /// <summary>
        /// Hedef segmenti belirler;
        /// </summary>
        private MemorySegment DetermineTargetSegment(ShortTermMemoryItem item, StoreOptions options)
        {
            // Özel segment belirtilmişse;
            if (!string.IsNullOrEmpty(options.TargetSegment))
            {
                if (_chunks.TryGetValue(options.TargetSegment, out var chunk))
                    return chunk.Segment;
            }

            // İçeriğe göre segment seç;
            if (item.ContextTags.Contains("working") || options.Priority >= PriorityLevel.High)
                return GetSegment("WorkingMemory").Segment;

            if (item.ContextTags.Contains("context") || !string.IsNullOrEmpty(options.Context?.SessionId))
                return GetSegment("ActiveContext").Segment;

            if (item.Type == MemoryType.Recent || options.StoreType == StoreType.Cached)
                return GetSegment("RecentAccess").Segment;

            // Varsayılan: Çalışma belleği;
            return GetSegment("WorkingMemory").Segment;
        }

        /// <summary>
        /// Segmenti alır;
        /// </summary>
        private MemoryChunk GetSegment(string segmentId)
        {
            return _chunks.TryGetValue(segmentId, out var chunk) ? chunk : null;
        }

        /// <summary>
        /// Segmentlere öğe ekler;
        /// </summary>
        private async Task<bool> AddToSegmentAsync(MemorySegment segment, ShortTermMemoryItem item)
        {
            if (segment == null || item == null)
                return false;

            if (!_chunks.TryGetValue(segment.SegmentId, out var chunk))
                return false;

            // Kapasite kontrolü;
            var segmentUsage = CalculateSegmentUsage(chunk);
            if (segmentUsage > 0.9 && segment.EvictionStrategy != EvictionStrategy.None)
            {
                // Yer aç;
                await EvictFromSegmentAsync(chunk, item.ContentSize);
            }

            // Öğeyi ekle;
            item.SegmentId = segment.SegmentId;
            var added = chunk.Items.TryAdd(item.MemoryId, item);

            if (added)
            {
                // Ana depoya ekle;
                _memoryStore[item.MemoryId] = item;

                // Erişim kuyruğuna ekle;
                chunk.AccessQueue.Enqueue(item.MemoryId);

                // Kuyruğu sınırla;
                while (chunk.AccessQueue.Count > _config.AccessHistorySize)
                {
                    chunk.AccessQueue.TryDequeue(out _);
                }
            }

            return added;
        }

        /// <summary>
        /// Segment kullanımını hesaplar;
        /// </summary>
        private double CalculateSegmentUsage(MemoryChunk chunk)
        {
            var totalSize = chunk.Items.Values.Sum(i => i.ContentSize);
            return chunk.Segment.Capacity > 0 ? (double)totalSize / chunk.Segment.Capacity : 1.0;
        }

        /// <summary>
        /// Segmentten öğe çıkarır;
        /// </summary>
        private async Task EvictFromSegmentAsync(MemoryChunk chunk, long requiredSpace)
        {
            var evictionCandidates = new List<ShortTermMemoryItem>();

            switch (chunk.Segment.EvictionStrategy)
            {
                case EvictionStrategy.LRU:
                    // En az kullanılanları bul;
                    evictionCandidates = chunk.Items.Values;
                        .OrderBy(i => i.LastAccessed)
                        .Take(_config.BatchCleanupSize)
                        .ToList();
                    break;

                case EvictionStrategy.FIFO:
                    // En eski eklenenleri bul;
                    evictionCandidates = chunk.Items.Values;
                        .OrderBy(i => i.CreatedTime)
                        .Take(_config.BatchCleanupSize)
                        .ToList();
                    break;

                case EvictionStrategy.ContextBased:
                    // Bağlam uyumsuz olanları bul;
                    var currentContext = GetCurrentContext();
                    evictionCandidates = chunk.Items.Values;
                        .Where(i => !IsContextRelevant(i, currentContext))
                        .OrderBy(i => i.LastAccessed)
                        .Take(_config.BatchCleanupSize)
                        .ToList();
                    break;

                case EvictionStrategy.PriorityBased:
                    // Düşük önceliklileri bul;
                    evictionCandidates = chunk.Items.Values;
                        .Where(i => i.Priority == PriorityLevel.Low)
                        .OrderBy(i => i.LastAccessed)
                        .Take(_config.BatchCleanupSize)
                        .ToList();
                    break;
            }

            // Çıkarılacak öğeleri kaldır;
            foreach (var item in evictionCandidates)
            {
                await RemoveFromSegmentAsync(item);
            }
        }

        /// <summary>
        /// Alternatif segment bulur;
        /// </summary>
        private MemorySegment FindAlternativeSegment(ShortTermMemoryItem item, StoreOptions options)
        {
            // Segmentleri kapasitelerine göre sırala;
            var availableSegments = _chunks.Values;
                .Where(c => c.Segment.SegmentId != item.SegmentId)
                .OrderBy(c => CalculateSegmentUsage(c))
                .Select(c => c.Segment)
                .ToList();

            return availableSegments.FirstOrDefault() ?? GetSegment("WorkingMemory").Segment;
        }

        /// <summary>
        /// Zorla yer açar;
        /// </summary>
        private async Task ForceMakeSpaceAsync(ShortTermMemoryItem item, StoreOptions options)
        {
            Logger.LogWarning("Forcing space creation in short-term memory");

            // Tüm segmentlerden agresif temizlik yap;
            foreach (var chunk in _chunks.Values)
            {
                var itemsToRemove = chunk.Items.Values;
                    .Where(i => CanForceEvict(i, options))
                    .OrderBy(i => i.Priority)
                    .ThenBy(i => i.LastAccessed)
                    .Take(_config.EmergencyCleanupSize)
                    .ToList();

                foreach (var itemToRemove in itemsToRemove)
                {
                    await RemoveFromSegmentAsync(itemToRemove);
                }
            }
        }

        /// <summary>
        /// Öğeyi saklamaya hazırlar;
        /// </summary>
        private void PrepareItemForStorage(ShortTermMemoryItem item, string storeId, StoreOptions options)
        {
            item.MemoryId = storeId;
            item.CreatedTime = DateTime.UtcNow;
            item.LastAccessed = DateTime.UtcNow;
            item.LastModified = DateTime.UtcNow;
            item.AccessCount = 0;
            item.ModificationCount = 0;
            item.Priority = options.Priority;
            item.StoreType = options.StoreType;

            // Süre sonu ayarla;
            if (options.TimeToLive.HasValue)
            {
                item.ExpirationTime = DateTime.UtcNow.Add(options.TimeToLive.Value);
            }
            else if (_config.DefaultTimeToLive.HasValue)
            {
                item.ExpirationTime = DateTime.UtcNow.Add(_config.DefaultTimeToLive.Value);
            }

            // İçerik boyutunu hesapla;
            item.ContentSize = CalculateContentSize(item);
        }

        /// <summary>
        /// İçerik boyutunu hesaplar;
        /// </summary>
        private long CalculateContentSize(ShortTermMemoryItem item)
        {
            var size = 0L;

            // Temel alanlar;
            size += item.MemoryId?.Length ?? 0;
            size += item.Content?.Length ?? 0;
            size += item.SemanticVector?.Length * sizeof(float) ?? 0;

            // Meta veriler;
            size += item.Metadata?.Sum(m => m.Key.Length + m.Value.Length) ?? 0;
            size += item.ContextTags?.Sum(t => t.Length) ?? 0;
            size += item.Keywords?.Sum(k => k.Length) ?? 0;

            // Sabit yük (approx)
            size += 100; // Başlık ve diğer sabit alanlar;

            return size;
        }

        /// <summary>
        /// İlişkiler oluşturur;
        /// </summary>
        private async Task CreateAssociationsAsync(ShortTermMemoryItem item, StoreOptions options)
        {
            try
            {
                // Bağlamsal ilişkiler;
                foreach (var tag in item.ContextTags)
                {
                    await AddContextAssociationAsync(item.MemoryId, tag);
                }

                // Anahtar kelime ilişkileri;
                foreach (var keyword in item.Keywords)
                {
                    await AddKeywordAssociationAsync(item.MemoryId, keyword);
                }

                // Zaman ilişkileri;
                await AddTemporalAssociationAsync(item.MemoryId, item.CreatedTime);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to create associations for item");
            }
        }

        /// <summary>
        /// İlişkileri temizler;
        /// </summary>
        private async Task CleanupAssociationsAsync(ShortTermMemoryItem item)
        {
            try
            {
                // Bağlamsal ilişkileri temizle;
                foreach (var tag in item.ContextTags)
                {
                    await RemoveContextAssociationAsync(item.MemoryId, tag);
                }

                // Anahtar kelime ilişkilerini temizle;
                foreach (var keyword in item.Keywords)
                {
                    await RemoveKeywordAssociationAsync(item.MemoryId, keyword);
                }

                // Zaman ilişkisini temizle;
                await RemoveTemporalAssociationAsync(item.MemoryId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to cleanup associations for item");
            }
        }

        /// <summary>
        /// Bağlamsal ilişki ekler;
        /// </summary>
        private async Task AddContextAssociationAsync(string memoryId, string contextTag)
        {
            // Gerçek uygulamada ilişki veritabanı kullanılır;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Anahtar kelime ilişkisi ekler;
        /// </summary>
        private async Task AddKeywordAssociationAsync(string memoryId, string keyword)
        {
            // Gerçek uygulamada ters indeks kullanılır;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Zamansal ilişki ekler;
        /// </summary>
        private async Task AddTemporalAssociationAsync(string memoryId, DateTime timestamp)
        {
            // Gerçek uygulamada zaman indeksi kullanılır;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Bağlamsal ilişkiyi kaldırır;
        /// </summary>
        private async Task RemoveContextAssociationAsync(string memoryId, string contextTag)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// Anahtar kelime ilişkisini kaldırır;
        /// </summary>
        private async Task RemoveKeywordAssociationAsync(string memoryId, string keyword)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// Zamansal ilişkiyi kaldırır;
        /// </summary>
        private async Task RemoveTemporalAssociationAsync(string memoryId)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// Önbellekten öğe kontrol eder;
        /// </summary>
        private async Task<ShortTermMemoryItem> CheckMemoryCacheAsync(string memoryId)
        {
            // Gerçek uygulamada önbellek sistemi kullanılır;
            return await Task.FromResult<ShortTermMemoryItem>(null);
        }

        /// <summary>
        /// İlişkisel zincirleri arar;
        /// </summary>
        private async Task<ShortTermMemoryItem> SearchAssociativeChainAsync(string memoryId, RetrievalContext context)
        {
            // Gerçek uygulamada ilişkisel arama yapılır;
            return await Task.FromResult<ShortTermMemoryItem>(null);
        }

        /// <summary>
        /// Öğeyi önbelleğe alır;
        /// </summary>
        private async Task CacheItemAsync(ShortTermMemoryItem item)
        {
            // Gerçek uygulamada önbelleğe alma stratejisi uygulanır;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Öğeyi önbellekten kaldırır;
        /// </summary>
        private async Task RemoveFromCacheAsync(string memoryId)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// Önbelleği temizler;
        /// </summary>
        private async Task ClearCacheAsync()
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// Segmentte bağlama göre arama yapar;
        /// </summary>
        private async Task<List<ShortTermMemoryItem>> SearchInSegmentByContextAsync(MemoryChunk chunk, RetrievalContext context)
        {
            var results = new List<ShortTermMemoryItem>();

            await Task.Run(() =>
            {
                foreach (var item in chunk.Items.Values)
                {
                    if (IsContextRelevant(item, context))
                    {
                        results.Add(item);
                    }
                }
            });

            return results;
        }

        /// <summary>
        /// Segmentte anahtar kelimelere göre arama yapar;
        /// </summary>
        private async Task<List<ShortTermMemoryItem>> SearchInSegmentByKeywordsAsync(MemoryChunk chunk, List<string> keywords, RetrievalContext context)
        {
            var results = new List<ShortTermMemoryItem>();

            await Task.Run(() =>
            {
                foreach (var item in chunk.Items.Values)
                {
                    if (HasKeywordMatch(item, keywords))
                    {
                        results.Add(item);
                    }
                }
            });

            return results;
        }

        /// <summary>
        /// Sonuçları bağlama göre derecelendirir;
        /// </summary>
        private List<ShortTermMemoryItem> RankResultsByContext(List<ShortTermMemoryItem> results, RetrievalContext context)
        {
            return results;
                .Select(item => new;
                {
                    Item = item,
                    Score = CalculateContextRelevanceScore(item, context)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .Take(_config.MaxSearchResults)
                .ToList();
        }

        /// <summary>
        /> Sonuçları ilişkiye göre derecelendirir;
        /// </summary>
        private List<ShortTermMemoryItem> RankResultsByRelevance(List<ShortTermMemoryItem> results, List<string> keywords, RetrievalContext context)
        {
            return results;
                .Select(item => new;
                {
                    Item = item,
                    Score = CalculateRelevanceScore(item, keywords, context)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .Take(_config.MaxSearchResults)
                .ToList();
        }

        /// <summary>
        /// Bağlam ilgili puanını hesaplar;
        /// </summary>
        private float CalculateContextRelevanceScore(ShortTermMemoryItem item, RetrievalContext context)
        {
            var score = 0.0f;

            // Bağlam etiketleri eşleşmesi;
            var contextMatches = item.ContextTags.Intersect(context.Tags).Count();
            score += (contextMatches / (float)Math.Max(context.Tags.Count, 1)) * 0.4f;

            // Zaman uyumu;
            if (context.TemporalReference.HasValue)
            {
                var timeDiff = Math.Abs((item.CreatedTime - context.TemporalReference.Value).TotalHours);
                var timeScore = (float)Math.Exp(-timeDiff / 24.0);
                score += timeScore * 0.3f;
            }

            // Öncelik faktörü;
            score += ((int)item.Priority / 10.0f) * 0.3f;

            return Math.Clamp(score, 0, 1);
        }

        /// <summary>
        /// İlişki puanını hesaplar;
        /// </summary>
        private float CalculateRelevanceScore(ShortTermMemoryItem item, List<string> keywords, RetrievalContext context)
        {
            var score = 0.0f;

            // Anahtar kelime eşleşmesi;
            var keywordMatches = item.Keywords.Intersect(keywords).Count();
            score += (keywordMatches / (float)Math.Max(keywords.Count, 1)) * 0.5f;

            // Bağlam puanı;
            score += CalculateContextRelevanceScore(item, context) * 0.3f;

            // Tazelik puanı;
            var freshness = CalculateFreshnessScore(item);
            score += freshness * 0.2f;

            return Math.Clamp(score, 0, 1);
        }

        /// <summary>
        /// Tazelik puanını hesaplar;
        /// </summary>
        private float CalculateFreshnessScore(ShortTermMemoryItem item)
        {
            var hoursSinceAccess = (DateTime.UtcNow - item.LastAccessed).TotalHours;
            return (float)Math.Exp(-hoursSinceAccess / 24.0);
        }

        /// <summary>
        /// Öğe önceliğini günceller;
        /// </summary>
        private void UpdateItemPriority(ShortTermMemoryItem item, RetrievalContext context)
        {
            item.LastAccessed = DateTime.UtcNow;
            item.AccessCount++;

            // Erişim sıklığına göre önceliği artır;
            if (item.AccessCount > _config.PriorityIncreaseThreshold)
            {
                if (item.Priority < PriorityLevel.Critical)
                {
                    item.Priority++;
                    Logger.LogDebug($"Priority increased for item: {item.MemoryId}");
                }
            }
        }

        /// <summary>
        /> Anahtar kelimeleri çıkarır;
        /// </summary>
        private List<string> ExtractKeywords(string query)
        {
            // Basit anahtar kelime çıkarımı;
            return query.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant())
                .Where(w => w.Length > 2)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Erişimi kaydeder;
        /// </summary>
        private void RecordAccess(string memoryId, AccessType accessType, RetrievalContext context)
        {
            var access = new MemoryAccess;
            {
                MemoryId = memoryId,
                AccessType = accessType,
                Timestamp = DateTime.UtcNow,
                Context = context?.Clone(),
                ThreadId = Thread.CurrentThread.ManagedThreadId;
            };

            _accessLog.Enqueue(access);

            // Kuyruk boyutunu sınırla;
            while (_accessLog.Count > _config.AccessHistorySize)
            {
                _accessLog.TryDequeue(out _);
            }
        }

        /// <summary>
        /// İstatistikleri günceller;
        /// </summary>
        private void UpdateStatistics(bool success, long sizeDelta, int countDelta, bool isRetrieval = false)
        {
            lock (_syncLocks.GetOrAdd("stats", _ => new object()))
            {
                _statistics.TotalOperations++;

                if (success)
                {
                    _statistics.SuccessfulOperations++;

                    if (sizeDelta > 0)
                    {
                        _statistics.TotalStoredBytes += sizeDelta;
                    }
                    else if (sizeDelta < 0)
                    {
                        _statistics.TotalRemovedBytes += -sizeDelta;
                    }

                    if (isRetrieval)
                    {
                        _statistics.TotalRetrievals++;
                    }
                }
                else;
                {
                    _statistics.FailedOperations++;
                }

                _statistics.CurrentItemCount += countDelta;
                _statistics.LastOperationTime = DateTime.UtcNow;

                // Ortalama işlem süresini güncelle;
                if (_statistics.SuccessfulOperations > 0)
                {
                    // Gerçek uygulamada zaman ölçümü yapılır;
                }
            }
        }

        /// <summary>
        /> İstatistikleri sıfırlar;
        /// </summary>
        private void ResetStatistics()
        {
            lock (_syncLocks.GetOrAdd("stats", _ => new object()))
            {
                _statistics = new ShortTermMemoryStatistics;
                {
                    SystemStartTime = DateTime.UtcNow,
                    TotalOperations = 0,
                    SuccessfulOperations = 0,
                    FailedOperations = 0,
                    TotalRetrievals = 0,
                    TotalStoredBytes = 0,
                    TotalRemovedBytes = 0,
                    CurrentItemCount = 0,
                    LastOperationTime = DateTime.MinValue,
                    LastConsolidation = DateTime.MinValue,
                    TotalConsolidations = 0;
                };
            }
        }

        /// <summary>
        /> Sistem durumunu doğrular;
        /// </summary>
        private void ValidateSystemState()
        {
            if (!_isInitialized)
                throw new ShortTermMemoryException("System is not initialized");

            if (_isDisposed)
                throw new ShortTermMemoryException("System is disposed");
        }

        /// <summary>
        /> Bellek öğesini doğrular;
        /// </summary>
        private void ValidateMemoryItem(ShortTermMemoryItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrWhiteSpace(item.Content))
                throw new ArgumentException("Memory item content cannot be empty", nameof(item));

            if (item.ContentSize <= 0)
                throw new ArgumentException("Memory item content size must be positive", nameof(item));
        }

        /// <summary>
        /> Bellek ID'sini doğrular;
        /// </summary>
        private void ValidateMemoryId(string memoryId)
        {
            if (string.IsNullOrWhiteSpace(memoryId))
                throw new ArgumentException("Memory ID cannot be empty", nameof(memoryId));
        }

        /// <summary>
        /> Bağlamı doğrular;
        /// </summary>
        private void ValidateContext(RetrievalContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /> Öğe çıkarılabilir mi kontrol eder;
        /// </summary>
        private bool CanEvictItem(ShortTermMemoryItem item, MemorySegment segment)
        {
            // Kritik öğeler çıkarılamaz;
            if (item.Priority >= PriorityLevel.Critical)
                return false;

            // Kilitli öğeler çıkarılamaz;
            if (item.IsLocked)
                return false;

            // Süresi dolmamış yüksek öncelikli öğeler;
            if (item.Priority >= PriorityLevel.High &&
                (!item.ExpirationTime.HasValue || item.ExpirationTime.Value > DateTime.UtcNow))
                return false;

            // Segment politikasına göre;
            return segment.RetentionPolicy != RetentionPolicy.Permanent;
        }

        /// <summary>
        /> Zorla çıkarılabilir mi kontrol eder;
        /// </summary>
        private bool CanForceEvict(ShortTermMemoryItem item, StoreOptions options)
        {
            // Acil durumda sadece düşük öncelikli öğeler çıkarılabilir;
            return item.Priority <= PriorityLevel.Low &&
                   !item.IsLocked &&
                   item.StoreType != StoreType.Persistent;
        }

        /// <summary>
        /> Öğe kaldırılabilir mi kontrol eder;
        /// </summary>
        private bool CanRemoveItem(ShortTermMemoryItem item, RemovalContext context)
        {
            if (item.IsLocked && !context.ForceRemove)
                return false;

            if (item.Priority >= PriorityLevel.Critical && !context.ForceRemove)
                return false;

            return true;
        }

        /// <summary>
        /> Bağlam ilgili mi kontrol eder;
        /// </summary>
        private bool IsContextRelevant(ShortTermMemoryItem item, RetrievalContext context)
        {
            if (context == null || context.Tags.Count == 0)
                return true;

            return item.ContextTags.Intersect(context.Tags).Any();
        }

        /// <summary>
        /> Bağlam ilgili mi kontrol eder(aşırı yükleme)
        /// </summary>
        private bool IsContextRelevant(ShortTermMemoryItem item, ContextSnapshot context)
        {
            if (context == null || context.Tags.Count == 0)
                return true;

            return item.ContextTags.Intersect(context.Tags).Any();
        }

        /// <summary>
        /> Anahtar kelime eşleşmesi var mı kontrol eder;
        /// </summary>
        private bool HasKeywordMatch(ShortTermMemoryItem item, List<string> keywords)
        {
            if (keywords.Count == 0)
                return false;

            return item.Keywords.Intersect(keywords).Any();
        }

        /// <summary>
        /> Mevcut bağlamı alır;
        /// </summary>
        private ContextSnapshot GetCurrentContext()
        {
            // Gerçek uygulamada sistem bağlamı yönetilir;
            return new ContextSnapshot;
            {
                Timestamp = DateTime.UtcNow,
                Tags = new HashSet<string> { "system", "active" }
            };
        }

        /// <summary>
        /> Segment sağlığını doğrular;
        /// </summary>
        private async Task<bool> ValidateSegmentHealthAsync(MemoryChunk chunk)
        {
            try
            {
                // Temel kontrol;
                if (chunk == null || chunk.Items == null)
                    return false;

                // Kapasite kontrolü;
                var usage = CalculateSegmentUsage(chunk);
                if (usage > 1.0) // Taşma;
                    return false;

                // Erişim kuyruğu kontrolü;
                if (chunk.AccessQueue.Count > chunk.Items.Count * 2)
                    return false;

                // Süresi dolmuş öğelerin oranı;
                var expiredCount = chunk.Items.Values.Count(i =>
                    i.ExpirationTime.HasValue && i.ExpirationTime.Value < DateTime.UtcNow);

                var expiredRatio = (double)expiredCount / Math.Max(chunk.Items.Count, 1);
                if (expiredRatio > 0.5) // Çok fazla süresi dolmuş öğe;
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /> Saklama kurtarma stratejisi uygular;
        /// </summary>
        private async Task<string> ApplyStorageRecoveryStrategyAsync(ShortTermMemoryItem item, StoreOptions options, Exception error)
        {
            var strategy = _recoveryEngine.DetermineRecoveryStrategy(error);

            switch (strategy)
            {
                case RecoveryStrategy.SimplifiedStore:
                    return await StoreSimplifiedItemAsync(item, options);

                case RecoveryStrategy.CompressedStore:
                    return await StoreCompressedItemAsync(item, options);

                case RecoveryStrategy.ExternalCache:
                    return await StoreInExternalCacheAsync(item, options);

                default:
                    return Guid.NewGuid().ToString(); // Boş ID döndür;
            }
        }

        /// <summary>
        /> Alım kurtarma stratejisi uygular;
        /// </summary>
        private async Task<ShortTermMemoryItem> ApplyRetrievalRecoveryStrategyAsync(string memoryId, RetrievalContext context, Exception error)
        {
            var strategy = _recoveryEngine.DetermineRecoveryStrategy(error);

            switch (strategy)
            {
                case RecoveryStrategy.CachedRetrieval:
                    return await RetrieveFromCacheAsync(memoryId);

                case RecoveryStrategy.AssociativeSearch:
                    return await SearchAssociativelyAsync(memoryId, context);

                default:
                    return ShortTermMemoryItem.Empty;
            }
        }

        /// <summary>
        /> Basitleştirilmiş öğe saklar;
        /// </summary>
        private async Task<string> StoreSimplifiedItemAsync(ShortTermMemoryItem item, StoreOptions options)
        {
            // İçeriği basitleştir;
            var simplifiedItem = item.Clone();
            simplifiedItem.Content = item.Content.Length > 1000 ?
                item.Content.Substring(0, 1000) + "..." :
                item.Content;

            // Meta verileri azalt;
            simplifiedItem.Metadata.Clear();
            simplifiedItem.SemanticVector = null;

            return await StoreAsync(simplifiedItem, options);
        }

        /// <summary>
        /> Sıkıştırılmış öğe saklar;
        /// </summary>
        private async Task<string> StoreCompressedItemAsync(ShortTermMemoryItem item, StoreOptions options)
        {
            // Gerçek uygulamada sıkıştırma uygulanır;
            return await StoreAsync(item, options);
        }

        /// <summary>
        /> Harici önbelleğe saklar;
        /// </summary>
        private async Task<string> StoreInExternalCacheAsync(ShortTermMemoryItem item, StoreOptions options)
        {
            // Gerçek uygulamada harici önbellek kullanılır;
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /> Önbellekten alır;
        /// </summary>
        private async Task<ShortTermMemoryItem> RetrieveFromCacheAsync(string memoryId)
        {
            // Gerçek uygulamada önbellekten alım yapılır;
            return await Task.FromResult(ShortTermMemoryItem.Empty);
        }

        /// <summary>
        /> İlişkisel arama yapar;
        /// </summary>
        private async Task<ShortTermMemoryItem> SearchAssociativelyAsync(string memoryId, RetrievalContext context)
        {
            // Gerçek uygulamada ilişkisel arama yapılır;
            return await Task.FromResult(ShortTermMemoryItem.Empty);
        }

        /// <summary>
        /> Segment konsolidasyonu yapar;
        /// </summary>
        private async Task<ConsolidationResult> ConsolidateSegmentAsync(MemoryChunk chunk, ConsolidationContext context)
        {
            var result = new ConsolidationResult();

            await Task.Run(() =>
            {
                var itemsToConsolidate = new List<ShortTermMemoryItem>();

                // Benzer öğeleri bul;
                var groups = chunk.Items.Values;
                    .GroupBy(i => i.Type)
                    .Where(g => g.Count() > 1);

                foreach (var group in groups)
                {
                    // Benzer içeriğe sahip öğeleri birleştir;
                    var similarItems = FindSimilarItems(group.ToList());
                    itemsToConsolidate.AddRange(similarItems);
                }

                // Birleştirme işlemi;
                foreach (var item in itemsToConsolidate.Take(context.MaxItemsToConsolidate))
                {
                    if (chunk.Items.TryRemove(item.MemoryId, out var removedItem))
                    {
                        result.ItemsConsolidated++;
                        result.MemoryFreed += removedItem.ContentSize;
                    }
                }
            });

            return result;
        }

        /// <summary>
        /> Benzer öğeleri bulur;
        /// </summary>
        private List<ShortTermMemoryItem> FindSimilarItems(List<ShortTermMemoryItem> items)
        {
            // Gerçek uygulamada benzerlik algoritması kullanılır;
            return items.Take(5).ToList();
        }

        /// <summary>
        /> Bellek parçalanmasını giderir;
        /// </summary>
        private async Task<ConsolidationResult> DefragmentMemoryAsync(ConsolidationContext context)
        {
            var result = new ConsolidationResult();

            // Gerçek uygulamada parçalanma giderme algoritması kullanılır;
            await Task.CompletedTask;

            return result;
        }

        /// <summary>
        /> Tüm segmentleri temizler;
        /// </summary>
        private async Task ClearAllSegmentsAsync()
        {
            foreach (var chunk in _chunks.Values)
            {
                chunk.Items.Clear();
                while (chunk.AccessQueue.TryDequeue(out _)) { }
            }

            _memoryStore.Clear();
            await ClearCacheAsync();
        }

        /// <summary>
        /> Süresi dolmuş öğeleri temizler;
        /// </summary>
        private async Task ClearExpiredItemsAsync(ClearContext context)
        {
            var itemsToRemove = new List<ShortTermMemoryItem>();

            foreach (var chunk in _chunks.Values)
            {
                var expiredItems = chunk.Items.Values;
                    .Where(i => i.ExpirationTime.HasValue && i.ExpirationTime.Value < DateTime.UtcNow)
                    .ToList();

                itemsToRemove.AddRange(expiredItems);
            }

            foreach (var item in itemsToRemove)
            {
                await RemoveFromSegmentAsync(item);
            }
        }

        /// <summary>
        /> Düşük öncelikli öğeleri temizler;
        /// </summary>
        private async Task ClearLowPriorityItemsAsync(ClearContext context)
        {
            var itemsToRemove = new List<ShortTermMemoryItem>();

            foreach (var chunk in _chunks.Values)
            {
                var lowPriorityItems = chunk.Items.Values;
                    .Where(i => i.Priority <= PriorityLevel.Low)
                    .OrderBy(i => i.LastAccessed)
                    .Take(context.MaxItemsToRemove)
                    .ToList();

                itemsToRemove.AddRange(lowPriorityItems);
            }

            foreach (var item in itemsToRemove)
            {
                await RemoveFromSegmentAsync(item);
            }
        }

        /// <summary>
        /> Bağlama göre temizler;
        /// </summary>
        private async Task ClearByContextAsync(ClearContext context)
        {
            if (context.ContextTags == null || context.ContextTags.Count == 0)
                return;

            var itemsToRemove = new List<ShortTermMemoryItem>();

            foreach (var chunk in _chunks.Values)
            {
                var contextItems = chunk.Items.Values;
                    .Where(i => i.ContextTags.Intersect(context.ContextTags).Any())
                    .Take(context.MaxItemsToRemove)
                    .ToList();

                itemsToRemove.AddRange(contextItems);
            }

            foreach (var item in itemsToRemove)
            {
                await RemoveFromSegmentAsync(item);
            }
        }

        /// <summary>
        /> Bakım geri çağırımı;
        /// </summary>
        private void PerformMaintenanceCallback(object state)
        {
            Task.Run(async () =>
            {
                try
                {
                    await PerformMaintenanceAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Maintenance callback failed");
                }
            });
        }

        /// <summary>
        /> Bakım işlemini yürütür;
        /// </summary>
        private async Task PerformMaintenanceAsync()
        {
            if (!_isInitialized || _isDisposed)
                return;

            try
            {
                // Bakım kilidi al;
                if (!await _cleanupSemaphore.WaitAsync(TimeSpan.FromSeconds(10)))
                    return;

                try
                {
                    Logger.LogDebug("Starting short-term memory maintenance...");

                    // Süresi dolmuş öğeleri temizle;
                    await ClearExpiredItemsAsync(new ClearContext { ClearType = ClearType.Expired });

                    // Parçalanmış öğeleri birleştir;
                    await ConsolidateAsync(new ConsolidationContext());

                    // İstatistikleri güncelle;
                    _lastCleanup = DateTime.UtcNow;

                    Logger.LogDebug("Short-term memory maintenance completed");
                }
                finally
                {
                    _cleanupSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Maintenance operation failed");
            }
        }

        /// <summary>
        /> Sistem kaynaklarını temizler;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /> Yönetilen ve yönetilmeyen kaynakları temizler;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _isDisposed = true;
                _isInitialized = false;

                // Zamanlayıcıyı durdur;
                _maintenanceTimer?.Dispose();

                // Semaforu temizle;
                _cleanupSemaphore?.Dispose();

                // Veri yapılarını temizle;
                _memoryStore.Clear();
                _chunks.Clear();
                _syncLocks.Clear();

                while (_accessLog.TryDequeue(out _)) { }

                Logger.LogInformation("Short-term memory system disposed successfully");
            }
        }

        /// <summary>
        /> Sonlandırıcı;
        /// </summary>
        ~ShortTermMemory()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /> Kısa süreli bellek sistemi arabirimi;
    /// </summary>
    public interface IShortTermMemory : IDisposable
    {
        Task<string> StoreAsync(ShortTermMemoryItem item, StoreOptions options = null);
        Task<ShortTermMemoryItem> RetrieveAsync(string memoryId, RetrievalContext context = null);
        Task<bool> RemoveAsync(string memoryId, RemovalContext context = null);
        Task<List<ShortTermMemoryItem>> RetrieveByContextAsync(RetrievalContext context);
        Task<List<ShortTermMemoryItem>> SearchAsync(string query, RetrievalContext context = null);
        Task<bool> UpdateAsync(string memoryId, Action<ShortTermMemoryItem> updateAction, UpdateContext context = null);
        Task<bool> ConsolidateAsync(ConsolidationContext context = null);
        Task ClearAsync(ClearContext context = null);
        Task<bool> ValidateHealthAsync();
        ShortTermMemoryStatistics GetStatistics();
        void UpdateConfig(ShortTermMemoryConfig newConfig);
        MemoryCapacityUsage CapacityUsage { get; }
        ShortTermMemoryConfig Config { get; }
    }

    /// <summary>
    /> Kısa süreli bellek öğesi;
    /// </summary>
    public class ShortTermMemoryItem;
    {
        public string MemoryId { get; set; } = Guid.NewGuid().ToString();
        public string Content { get; set; } = string.Empty;
        public MemoryType Type { get; set; } = MemoryType.Fact;
        public string SegmentId { get; set; } = string.Empty;
        public StoreType StoreType { get; set; } = StoreType.Volatile;
        public PriorityLevel Priority { get; set; } = PriorityLevel.Medium;
        public long ContentSize { get; set; } = 0;
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public DateTime? ExpirationTime { get; set; }
        public int AccessCount { get; set; } = 0;
        public int ModificationCount { get; set; } = 0;
        public bool IsLocked { get; set; } = false;
        public HashSet<string> ContextTags { get; set; } = new HashSet<string>();
        public List<string> Keywords { get; set; } = new List<string>();
        public float[]? SemanticVector { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        public List<MemoryVersion> Versions { get; set; } = new List<MemoryVersion>();

        public bool IsEmpty => string.IsNullOrWhiteSpace(Content);

        public ShortTermMemoryItem Clone()
        {
            return new ShortTermMemoryItem;
            {
                MemoryId = MemoryId,
                Content = Content,
                Type = Type,
                SegmentId = SegmentId,
                StoreType = StoreType,
                Priority = Priority,
                ContentSize = ContentSize,
                CreatedTime = CreatedTime,
                LastAccessed = LastAccessed,
                LastModified = LastModified,
                ExpirationTime = ExpirationTime,
                AccessCount = AccessCount,
                ModificationCount = ModificationCount,
                IsLocked = IsLocked,
                ContextTags = new HashSet<string>(ContextTags),
                Keywords = new List<string>(Keywords),
                SemanticVector = SemanticVector?.ToArray(),
                Metadata = new Dictionary<string, string>(Metadata),
                Versions = Versions.Select(v => v.Clone()).ToList()
            };
        }

        public static ShortTermMemoryItem Empty => new ShortTermMemoryItem();
    }

    /// <summary>
    /> Bellek versiyonu;
    /// </summary>
    public class MemoryVersion;
    {
        public string VersionId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public ShortTermMemoryItem Data { get; set; } = new ShortTermMemoryItem();
        public string ChangeDescription { get; set; } = string.Empty;

        public MemoryVersion Clone()
        {
            return new MemoryVersion;
            {
                VersionId = VersionId,
                Timestamp = Timestamp,
                Data = Data?.Clone(),
                ChangeDescription = ChangeDescription;
            };
        }
    }

    /// <summary>
    /> Bellek segmenti;
    /// </summary>
    public class MemorySegment;
    {
        public string SegmentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long Capacity { get; set; } = 1024 * 1024; // 1MB default;
        public RetentionPolicy RetentionPolicy { get; set; } = RetentionPolicy.Volatile;
        public EvictionStrategy EvictionStrategy { get; set; } = EvictionStrategy.LRU;
        public bool IsVolatile { get; set; } = true;
        public TimeSpan? DefaultRetention { get; set; }
    }

    /// <summary>
    /> Bellek parçası;
    /// </summary>
    public class MemoryChunk;
    {
        public string ChunkId { get; set; } = string.Empty;
        public MemorySegment Segment { get; set; } = new MemorySegment();
        public ConcurrentDictionary<string, ShortTermMemoryItem> Items { get; set; } = new ConcurrentDictionary<string, ShortTermMemoryItem>();
        public ConcurrentQueue<string> AccessQueue { get; set; } = new ConcurrentQueue<string>();
        public DateTime LastMaintenance { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /> Saklama seçenekleri;
    /// </summary>
    public class StoreOptions;
    {
        public PriorityLevel Priority { get; set; } = PriorityLevel.Medium;
        public StoreType StoreType { get; set; } = StoreType.Volatile;
        public TimeSpan? TimeToLive { get; set; }
        public string TargetSegment { get; set; } = string.Empty;
        public RetrievalContext Context { get; set; }
        public Dictionary<string, string> CustomMetadata { get; set; } = new Dictionary<string, string>();

        public static StoreOptions Default => new StoreOptions();
    }

    /// <summary>
    /> Alım bağlamı;
    /// </summary>
    public class RetrievalContext;
    {
        public string SessionId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public HashSet<string> Tags { get; set; } = new HashSet<string>();
        public DateTime? TemporalReference { get; set; }
        public PriorityLevel MinPriority { get; set; } = PriorityLevel.Low;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public RetrievalContext Clone()
        {
            return new RetrievalContext;
            {
                SessionId = SessionId,
                UserId = UserId,
                Tags = new HashSet<string>(Tags),
                TemporalReference = TemporalReference,
                MinPriority = MinPriority,
                Metadata = new Dictionary<string, string>(Metadata)
            };
        }
    }

    /// <summary>
    /> Kaldırma bağlamı;
    /// </summary>
    public class RemovalContext;
    {
        public bool ForceRemove { get; set; } = false;
        public string Reason { get; set; } = string.Empty;
        public HashSet<string> ContextTags { get; set; } = new HashSet<string>();
        public DateTime? BeforeDate { get; set; }

        public static RemovalContext Default => new RemovalContext();
    }

    /// <summary>
    /> Güncelleme bağlamı;
    /// </summary>
    public class UpdateContext;
    {
        public bool CreateVersion { get; set; } = true;
        public string ChangeDescription { get; set; } = string.Empty;
        public bool ValidateIntegrity { get; set; } = true;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public static UpdateContext Default => new UpdateContext();
    }

    /// <summary>
    /> Birleştirme bağlamı;
    /// </summary>
    public class ConsolidationContext;
    {
        public int MaxItemsToConsolidate { get; set; } = 100;
        public double SimilarityThreshold { get; set; } = 0.8;
        public bool Aggressive { get; set; } = false;
        public HashSet<string> ExcludedSegments { get; set; } = new HashSet<string>();

        public static ConsolidationContext Default => new ConsolidationContext();
    }

    /// <summary>
    /> Temizleme bağlamı;
    /// </summary>
    public class ClearContext;
    {
        public ClearType ClearType { get; set; } = ClearType.Expired;
        public HashSet<string> ContextTags { get; set; } = new HashSet<string>();
        public int MaxItemsToRemove { get; set; } = 1000;
        public bool Force { get; set; } = false;

        public static ClearContext Default => new ClearContext();
    }

    /// <summary>
    /> Birleştirme sonucu;
    /// </summary>
    public class ConsolidationResult;
    {
        public int ItemsConsolidated { get; set; } = 0;
        public long MemoryFreed { get; set; } = 0;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    }

    /// <summary>
    /> Bellek kapasitesi kullanımı;
    /// </summary>
    public class MemoryCapacityUsage;
    {
        public int TotalItems { get; set; } = 0;
        public long TotalSize { get; set; } = 0;
        public long TotalCapacity { get; set; } = 0;
        public double UsagePercentage { get; set; } = 0.0;
        public long AverageItemSize { get; set; } = 0;
        public int SegmentCount { get; set; } = 0;
    }

    /// <summary>
    /> Kısa süreli bellek istatistikleri;
    /// </summary>
    public class ShortTermMemoryStatistics;
    {
        public DateTime SystemStartTime { get; set; } = DateTime.UtcNow;
        public long TotalOperations { get; set; } = 0;
        public long SuccessfulOperations { get; set; } = 0;
        public long FailedOperations { get; set; } = 0;
        public long TotalRetrievals { get; set; } = 0;
        public long TotalStoredBytes { get; set; } = 0;
        public long TotalRemovedBytes { get; set; } = 0;
        public int CurrentItemCount { get; set; } = 0;
        public DateTime LastOperationTime { get; set; } = DateTime.MinValue;
        public DateTime LastConsolidation { get; set; } = DateTime.MinValue;
        public long TotalConsolidations { get; set; } = 0;

        public ShortTermMemoryStatistics Clone()
        {
            return new ShortTermMemoryStatistics;
            {
                SystemStartTime = SystemStartTime,
                TotalOperations = TotalOperations,
                SuccessfulOperations = SuccessfulOperations,
                FailedOperations = FailedOperations,
                TotalRetrievals = TotalRetrievals,
                TotalStoredBytes = TotalStoredBytes,
                TotalRemovedBytes = TotalRemovedBytes,
                CurrentItemCount = CurrentItemCount,
                LastOperationTime = LastOperationTime,
                LastConsolidation = LastConsolidation,
                TotalConsolidations = TotalConsolidations;
            };
        }
    }

    /// <summary>
    /> Kısa süreli bellek yapılandırması;
    /// </summary>
    public class ShortTermMemoryConfig;
    {
        public long WorkingMemoryCapacity { get; set; } = 10 * 1024 * 1024; // 10MB;
        public long ContextMemoryCapacity { get; set; } = 5 * 1024 * 1024; // 5MB;
        public long RecentAccessCapacity { get; set; } = 2 * 1024 * 1024; // 2MB;
        public double HighWatermark { get; set; } = 0.8; // %80;
        public double LowWatermark { get; set; } = 0.6; // %60;
        public int BatchCleanupSize { get; set; } = 50;
        public int EmergencyCleanupSize { get; set; } = 100;
        public int AccessHistorySize { get; set; } = 1000;
        public int MaxSearchResults { get; set; } = 100;
        public int PriorityIncreaseThreshold { get; set; } = 5;
        public int MaintenanceIntervalMinutes { get; set; } = 5;
        public TimeSpan? DefaultTimeToLive { get; set; } = TimeSpan.FromMinutes(30);
        public bool EnableCompression { get; set; } = false;
        public bool EnableEncryption { get; set; } = false;
        public int MaxConcurrentOperations { get; set; } = 100;

        public ShortTermMemoryConfig Clone()
        {
            return new ShortTermMemoryConfig;
            {
                WorkingMemoryCapacity = WorkingMemoryCapacity,
                ContextMemoryCapacity = ContextMemoryCapacity,
                RecentAccessCapacity = RecentAccessCapacity,
                HighWatermark = HighWatermark,
                LowWatermark = LowWatermark,
                BatchCleanupSize = BatchCleanupSize,
                EmergencyCleanupSize = EmergencyCleanupSize,
                AccessHistorySize = AccessHistorySize,
                MaxSearchResults = MaxSearchResults,
                PriorityIncreaseThreshold = PriorityIncreaseThreshold,
                MaintenanceIntervalMinutes = MaintenanceIntervalMinutes,
                DefaultTimeToLive = DefaultTimeToLive,
                EnableCompression = EnableCompression,
                EnableEncryption = EnableEncryption,
                MaxConcurrentOperations = MaxConcurrentOperations;
            };
        }

        public static ShortTermMemoryConfig Default => new ShortTermMemoryConfig();
    }

    /// <summary>
    /> Bellek erişimi kaydı;
    /// </summary>
    public class MemoryAccess;
    {
        public string MemoryId { get; set; } = string.Empty;
        public AccessType AccessType { get; set; } = AccessType.Retrieve;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public RetrievalContext Context { get; set; }
        public int ThreadId { get; set; }
        public long DurationMs { get; set; } = 0;
    }

    /// <summary>
    /> Bağlam anlık görüntüsü;
    /// </summary>
    public class ContextSnapshot;
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public HashSet<string> Tags { get; set; } = new HashSet<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /> Bellek türü;
    /// </summary>
    public enum MemoryType;
    {
        Fact,
        Experience,
        Skill,
        Concept,
        Procedure,
        Pattern,
        Recent,
        Contextual;
    }

    /// <summary>
    /> Saklama türü;
    /// </summary>
    public enum StoreType;
    {
        Volatile,
        Cached,
        Persistent,
        Temporary;
    }

    /// <summary>
    /> Öncelik seviyesi;
    /// </summary>
    public enum PriorityLevel;
    {
        Lowest = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /> Saklama politikası;
    /// </summary>
    public enum RetentionPolicy;
    {
        Volatile,
        Recent,
        HighPriority,
        Contextual,
        Permanent;
    }

    /// <summary>
    /> Çıkarma stratejisi;
    /// </summary>
    public enum EvictionStrategy;
    {
        None,
        LRU,        // Least Recently Used;
        FIFO,       // First In First Out;
        LFU,        // Least Frequently Used;
        PriorityBased,
        ContextBased;
    }

    /// <summary>
    /> Erişim türü;
    /// </summary>
    public enum AccessType;
    {
        Store,
        Retrieve,
        Update,
        Remove,
        Search;
    }

    /// <summary>
    /> Temizleme türü;
    /// </summary>
    public enum ClearType;
    {
        Full,
        Expired,
        LowPriority,
        ContextBased;
    }

    /// <summary>
    /> Kısa süreli bellek istisnası;
    /// </summary>
    public class ShortTermMemoryException : Exception
    {
        public ShortTermMemoryException() { }
        public ShortTermMemoryException(string message) : base(message) { }
        public ShortTermMemoryException(string message, Exception inner) : base(message, inner) { }
    }
}
