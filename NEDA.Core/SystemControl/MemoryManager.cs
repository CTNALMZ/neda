using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring.Diagnostics;
using NEDA.Core.Monitoring.HealthChecks;
using NEDA.Core.Monitoring.PerformanceCounters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.Caching;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// MemoryCache name conflict fix:
using RuntimeMemoryCache = System.Runtime.Caching.MemoryCache;

namespace NEDA.Core.SystemControl
{
    /// <summary>
    /// Advanced Memory Management System with intelligent allocation, garbage collection optimization,
    /// memory leak detection, and performance monitoring for high-performance applications;
    /// </summary>
    public class MemoryManager : IMemoryManager, IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly DiagnosticTool _diagnosticTool;

        // Memory pools for different allocation sizes;
        private readonly ConcurrentDictionary<int, MemoryPool> _memoryPools;
        private readonly ConcurrentDictionary<string, MemoryCacheRegion> _cacheRegions;
        private readonly RuntimeMemoryCache _globalCache;

        // Memory monitoring;
        private readonly Timer _monitoringTimer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _gcOptimizationTimer;
        private readonly ConcurrentQueue<MemoryAllocation> _allocationHistory;

        // Statistics;
        private long _totalAllocations;
        private long _totalDeallocations;
        private long _totalCacheHits;
        private long _totalCacheMisses;
        private long _memoryLeakDetections;
        private long _gcCollections;

        // Configuration;
        private readonly MemoryManagerConfig _config;
        private bool _disposed;

        // Native memory management;
        private IntPtr _nativeMemoryPool;
        private readonly object _nativeMemoryLock = new object();

        // Constants;
        private const int DEFAULT_POOL_SIZE = 1024 * 1024 * 100; // 100MB default pool;
        private const int MAX_ALLOCATION_HISTORY = 10000;
        private const int MONITORING_INTERVAL_MS = 5000;
        private const int CLEANUP_INTERVAL_MS = 60000;

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Memory allocation information for tracking and analysis;
        /// </summary>
        public class MemoryAllocation
        {
            public string Id { get; set; }
            public long Size { get; set; }
            public AllocationType Type { get; set; }
            public string Category { get; set; }
            public string Caller { get; set; }
            public DateTime Timestamp { get; set; }
            public StackTrace StackTrace { get; set; }
            public bool IsPinned { get; set; }
            public IntPtr NativePointer { get; set; }
            public TimeSpan Lifetime { get; set; }
            public string PoolName { get; set; }

            public MemoryAllocation()
            {
                Id = Guid.NewGuid().ToString();
                Timestamp = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Memory pool for efficient allocation of specific size ranges;
        /// </summary>
        private class MemoryPool : IDisposable
        {
            public string Name { get; }
            public int BlockSize { get; }
            public int PoolSize { get; }
            public int MaxBlocks { get; }
            public AllocationStrategy Strategy { get; }

            private readonly ConcurrentStack<IntPtr> _freeBlocks;
            private readonly ConcurrentDictionary<IntPtr, MemoryAllocation> _allocatedBlocks;
            private readonly IntPtr _poolStart;
            private int _totalBlocks;
            private bool _disposed;

            public MemoryPool(string name, int blockSize, int poolSize, AllocationStrategy strategy)
            {
                Name = name;
                BlockSize = blockSize;
                PoolSize = poolSize;
                Strategy = strategy;
                MaxBlocks = poolSize / blockSize;

                _freeBlocks = new ConcurrentStack<IntPtr>();
                _allocatedBlocks = new ConcurrentDictionary<IntPtr, MemoryAllocation>();

                // Allocate native memory for the pool;
                _poolStart = Marshal.AllocHGlobal(poolSize);
                InitializeBlocks();
            }

            private void InitializeBlocks()
            {
                for (int i = 0; i < MaxBlocks; i++)
                {
                    var blockPtr = _poolStart + (i * BlockSize);
                    _freeBlocks.Push(blockPtr);
                }
                _totalBlocks = MaxBlocks;
            }

            public IntPtr AllocateBlock(string category, string caller)
            {
                if (_freeBlocks.TryPop(out var blockPtr))
                {
                    var allocation = new MemoryAllocation
                    {
                        Category = category,
                        Caller = caller,
                        Type = AllocationType.Pooled,
                        Size = BlockSize,
                        PoolName = Name,
                        NativePointer = blockPtr
                    };

                    _allocatedBlocks.TryAdd(blockPtr, allocation);
                    return blockPtr;
                }

                // Pool exhausted - allocate new block;
                return IntPtr.Zero;
            }

            public bool FreeBlock(IntPtr blockPtr)
            {
                if (_allocatedBlocks.TryRemove(blockPtr, out _))
                {
                    _freeBlocks.Push(blockPtr);
                    return true;
                }
                return false;
            }

            public MemoryPoolStatistics GetStatistics()
            {
                return new MemoryPoolStatistics
                {
                    Name = Name,
                    BlockSize = BlockSize,
                    TotalBlocks = _totalBlocks,
                    FreeBlocks = _freeBlocks.Count,
                    AllocatedBlocks = _allocatedBlocks.Count,
                    Utilization = _allocatedBlocks.Count / (double)_totalBlocks * 100,
                    TotalSize = PoolSize
                };
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                Marshal.FreeHGlobal(_poolStart);
                _disposed = true;
            }
        }

        /// <summary>
        /// Cache region with specific policies;
        /// </summary>
        private class MemoryCacheRegion
        {
            public string Name { get; }
            public CachePolicy Policy { get; }
            public DateTime Created { get; }

            private readonly RuntimeMemoryCache _cache;
            private readonly ConcurrentDictionary<string, CacheItem> _items;

            public MemoryCacheRegion(string name, CachePolicy policy)
            {
                Name = name;
                Policy = policy;
                Created = DateTime.UtcNow;

                _cache = new RuntimeMemoryCache(name);
                _items = new ConcurrentDictionary<string, CacheItem>();
            }

            public bool Add(string key, object value, CacheItemPolicy policy = null)
            {
                policy ??= new CacheItemPolicy
                {
                    SlidingExpiration = Policy.DefaultSlidingExpiration ?? ObjectCache.NoSlidingExpiration,
                    AbsoluteExpiration = Policy.DefaultAbsoluteExpiration ?? ObjectCache.InfiniteAbsoluteExpiration
                };

                var cacheItem = new CacheItem(key, value);
                _cache.Set(cacheItem, policy);
                _items.TryAdd(key, cacheItem);

                return true;
            }

            public object Get(string key)
            {
                return _cache.Get(key);
            }

            public bool Remove(string key)
            {
                _cache.Remove(key);
                _items.TryRemove(key, out _);
                return true;
            }

            public void Clear()
            {
                foreach (var key in _items.Keys)
                {
                    _cache.Remove(key);
                }
                _items.Clear();
            }

            public CacheRegionStatistics GetStatistics()
            {
                return new CacheRegionStatistics
                {
                    Name = Name,
                    ItemCount = _items.Count,
                    Created = Created,
                    Policy = Policy.Name,
                    MemorySize = GetEstimatedSize()
                };
            }

            private long GetEstimatedSize()
            {
                // Estimate memory usage;
                return _items.Count * Policy.EstimatedItemSize;
            }
        }

        /// <summary>
        /// Configuration for memory manager;
        /// </summary>
        public class MemoryManagerConfig
        {
            public bool EnableMemoryPools { get; set; }
            public bool EnableCacheRegions { get; set; }
            public bool EnableMemoryMonitoring { get; set; }
            public bool EnableGCOptimization { get; set; }
            public bool EnableMemoryLeakDetection { get; set; }
            public long MaxMemoryUsageBytes { get; set; }
            public int MaxCacheRegions { get; set; }
            public int MemoryPoolBlockSize { get; set; }
            public int MemoryPoolSize { get; set; }
            public TimeSpan MonitoringInterval { get; set; }
            public TimeSpan CleanupInterval { get; set; }
            public double MemoryPressureThreshold { get; set; }
            public List<CachePolicy> CachePolicies { get; set; }
            public Dictionary<string, int> PoolConfigurations { get; set; }

            public MemoryManagerConfig()
            {
                EnableMemoryPools = true;
                EnableCacheRegions = true;
                EnableMemoryMonitoring = true;
                EnableGCOptimization = true;
                EnableMemoryLeakDetection = true;
                MaxMemoryUsageBytes = 1024 * 1024 * 1024; // 1GB;
                MaxCacheRegions = 10;
                MemoryPoolBlockSize = 4096; // 4KB;
                MemoryPoolSize = DEFAULT_POOL_SIZE;
                MonitoringInterval = TimeSpan.FromMilliseconds(MONITORING_INTERVAL_MS);
                CleanupInterval = TimeSpan.FromMilliseconds(CLEANUP_INTERVAL_MS);
                MemoryPressureThreshold = 0.85; // 85%
                CachePolicies = new List<CachePolicy>();
                PoolConfigurations = new Dictionary<string, int>();

                InitializeDefaultPolicies();
            }

            private void InitializeDefaultPolicies()
            {
                CachePolicies.Add(new CachePolicy
                {
                    Name = "ShortTerm",
                    Description = "Short-term caching (5 minutes)",
                    DefaultSlidingExpiration = TimeSpan.FromMinutes(5),
                    EstimatedItemSize = 1024
                });

                CachePolicies.Add(new CachePolicy
                {
                    Name = "LongTerm",
                    Description = "Long-term caching (1 hour)",
                    DefaultSlidingExpiration = TimeSpan.FromHours(1),
                    EstimatedItemSize = 4096
                });

                CachePolicies.Add(new CachePolicy
                {
                    Name = "Persistent",
                    Description = "Persistent caching (until manual removal)",
                    DefaultAbsoluteExpiration = DateTimeOffset.MaxValue,
                    EstimatedItemSize = 8192
                });

                // Default pool configurations;
                PoolConfigurations["Small"] = 256;      // 256 bytes;
                PoolConfigurations["Medium"] = 4096;    // 4KB;
                PoolConfigurations["Large"] = 65536;    // 64KB;
                PoolConfigurations["Huge"] = 1048576;   // 1MB;
            }
        }

        /// <summary>
        /// Cache policy configuration;
        /// </summary>
        public class CachePolicy
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public TimeSpan? DefaultSlidingExpiration { get; set; }
            public DateTimeOffset? DefaultAbsoluteExpiration { get; set; }
            public int EstimatedItemSize { get; set; }
            public int Priority { get; set; }
        }

        /// <summary>
        /// Memory allocation types;
        /// </summary>
        public enum AllocationType
        {
            Managed,
            Native,
            Pooled,
            Cached,
            Pinned
        }

        /// <summary>
        /// Allocation strategies;
        /// </summary>
        public enum AllocationStrategy
        {
            FirstFit,
            BestFit,
            WorstFit,
            NextFit
        }

        /// <summary>
        /// Memory pressure levels;
        /// </summary>
        public enum MemoryPressure
        {
            Low,
            Medium,
            High,
            Critical
        }

        #endregion;

        #region Constructor and Initialization;

        public MemoryManager(
            ILogger logger = null,
            AppConfig appConfig = null,
            PerformanceMonitor performanceMonitor = null,
            DiagnosticTool diagnosticTool = null)
        {
            _logger = logger ?? Logger.CreateLogger<MemoryManager>();
            _appConfig = appConfig ?? SettingsManager.LoadConfiguration();
            _performanceMonitor = performanceMonitor ?? new PerformanceMonitor();
            _diagnosticTool = diagnosticTool ?? new DiagnosticTool();

            // Load configuration;
            _config = LoadConfiguration();

            // Initialize collections;
            _memoryPools = new ConcurrentDictionary<int, MemoryPool>();
            _cacheRegions = new ConcurrentDictionary<string, MemoryCacheRegion>();
            _allocationHistory = new ConcurrentQueue<MemoryAllocation>();

            // Initialize global cache;
            _globalCache = new RuntimeMemoryCache("NEDA_GlobalCache",
                new System.Collections.Specialized.NameValueCollection
                {
                    { "cacheMemoryLimitMegabytes", "100" },
                    { "physicalMemoryLimitPercentage", "20" },
                    { "pollingInterval", "00:05:00" }
                });

            // Initialize memory pools if enabled;
            if (_config.EnableMemoryPools)
            {
                InitializeMemoryPools();
            }

            // Initialize cache regions if enabled;
            if (_config.EnableCacheRegions)
            {
                InitializeCacheRegions();
            }

            // Start monitoring timers;
            if (_config.EnableMemoryMonitoring)
            {
                _monitoringTimer = new Timer(MonitorMemoryUsage, null,
                    TimeSpan.Zero, _config.MonitoringInterval);
            }

            _cleanupTimer = new Timer(CleanupMemory, null,
                _config.CleanupInterval, _config.CleanupInterval);

            if (_config.EnableGCOptimization)
            {
                _gcOptimizationTimer = new Timer(OptimizeGarbageCollection, null,
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            }

            // Register with performance monitor;
            _performanceMonitor.RegisterCounter("MemoryManager",
                () => GetMemoryStatistics().ManagedMemory);

            _logger.LogInformation("MemoryManager initialized with configuration: {Config}",
                JsonSerializer.Serialize(_config));
        }

        // ... (geri kalan kodunda da aynı tipteki `new X; {}` ve `else;` / `catch` hataları
        // varsa aynı şekilde düzeltmen gerekiyor)

        public MemoryPressure GetMemoryPressure()
        {
            var totalMemory = GC.GetTotalMemory(false);
            var availableMemory = GetAvailableSystemMemory();

            var memoryUsageRatio = (double)totalMemory / availableMemory;

            if (memoryUsageRatio >= _config.MemoryPressureThreshold)
                return MemoryPressure.Critical;
            else if (memoryUsageRatio >= _config.MemoryPressureThreshold * 0.75)
                return MemoryPressure.High;
            else if (memoryUsageRatio >= _config.MemoryPressureThreshold * 0.5)
                return MemoryPressure.Medium;
            else
                return MemoryPressure.Low;
        }

        private string GetCallerMethod()
        {
            try
            {
                var frame = new StackFrame(2, true);
                var method = frame.GetMethod();
                return $"{method?.DeclaringType?.Name}.{method?.Name}";
            }
            catch
            {
                return "Unknown";
            }
        }

        private long GetAvailableSystemMemory()
        {
            try
            {
                var computerInfo = new Microsoft.VisualBasic.Devices.ComputerInfo();
                return (long)computerInfo.AvailablePhysicalMemory;
            }
            catch
            {
                var process = Process.GetCurrentProcess();
                return process.MaxWorkingSet.ToInt64();
            }
        }

        // (Dispose kısmında, ClearAllCaches içinde _globalCache.Dispose() çağırıp
        // sonra tekrar _globalCache.Dispose() çağırıyorsun. Bu compile hatası değil ama gereksiz.)

        #endregion;
    }

    public interface IMemoryManager : IDisposable
    {
        IntPtr AllocateMemory(long size, string category = "General",
            bool usePool = true, string caller = null);

        void FreeMemory(IntPtr memoryPtr, bool validatePointer = true);

        GCHandle PinMemory(object obj, GCHandleType handleType = GCHandleType.Pinned);

        void CacheObject(string key, object value, string region = "ShortTerm",
            TimeSpan? expiration = null);

        object GetCachedObject(string key, string region = "ShortTerm");

        void ForceGarbageCollection(GCCollectionMode mode = GCCollectionMode.Forced,
            bool blocking = true, bool compacting = false);

        void CompactMemory();

        MemoryManager.MemoryLeakReport DetectMemoryLeaks(TimeSpan threshold);

        MemoryManager.MemoryStatistics GetMemoryStatistics();

        MemoryManager.MemoryPressure GetMemoryPressure();

        void ClearAllCaches();
    }

    public class MemoryException : Exception
    {
        public string ErrorCode { get; }
        public long RequestedSize { get; }
        public long AvailableMemory { get; }

        public MemoryException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public MemoryException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public MemoryException(string errorCode, string message, long requestedSize, long availableMemory)
            : base(message)
        {
            ErrorCode = errorCode;
            RequestedSize = requestedSize;
            AvailableMemory = availableMemory;
        }
    }

    internal static class JsonSerializer
    {
        public static string Serialize(object obj)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(obj);
        }
    }
}
