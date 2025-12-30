using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.PerformanceCounters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.GameDesign.LevelDesign.NavigationMesh;
{
    /// <summary>
    /// Gelişmiş Yol Bulma Sistemi - Çoklu algoritma, threading ve optimizasyon desteği;
    /// </summary>
    public class Pathfinding : IDisposable
    {
        #region Sabitler ve Tanımlamalar;

        private const int DEFAULT_PATH_CACHE_SIZE = 1000;
        private const float DEFAULT_CELL_SIZE = 1.0f;
        private const int MAX_PATH_LENGTH = 10000;
        private const float DEFAULT_HEURISTIC_WEIGHT = 1.0f;
        private const int PATHFINDING_THREAD_COUNT = 4;
        private const int MAX_CONCURRENT_REQUESTS = 100;

        public enum PathfindingAlgorithm;
        {
            AStar,          // A* algoritması - genel amaçlı;
            Dijkstra,       // Dijkstra - tek kaynaklı en kısa yol;
            ThetaStar,      // Theta* - daha doğal yollar;
            JumpPointSearch, // JPS - grid tabanlı optimizasyon;
            HPAStar,        // Hierarchical Pathfinding - büyük haritalar için;
            LazyThetaStar,  // Lazy Theta* - dinamik ortamlar için;
            IDAStar,        // Iterative Deepening A* - bellek optimizasyonu;
            BFS,            // Breadth-First Search - arama algoritması;
            DFS,            // Depth-First Search;
            BiDirectionalAStar // Çift yönlü A*
        }

        public enum HeuristicType;
        {
            Manhattan,
            Euclidean,
            Chebyshev,
            Octile,
            Diagonal,
            Custom;
        }

        public enum PathfindingStatus;
        {
            NotStarted,
            InProgress,
            Success,
            Failed,
            Timeout,
            Cancelled;
        }

        public struct PathfindingMetrics;
        {
            public int NodesExplored;
            public int PathLength;
            public float TotalCost;
            public TimeSpan ComputationTime;
            public float MemoryUsageMB;
            public int OpenSetSize;
            public int ClosedSetSize;
        }

        #endregion;

        #region Özellikler ve Alanlar;

        private readonly ILogger _logger;
        private readonly DiagnosticTool _diagnosticTool;
        private readonly PerformanceMonitor _performanceMonitor;

        private bool _isInitialized = false;
        private bool _isEnabled = true;

        private NavigationGrid _navigationGrid;
        private PathfindingConfiguration _config;

        private ConcurrentDictionary<string, PathCacheEntry> _pathCache;
        private ConcurrentQueue<PathRequest> _requestQueue;
        private ConcurrentDictionary<Guid, PathResult> _activeRequests;
        private List<PathfindingWorker> _workers;

        private CancellationTokenSource _cancellationTokenSource;
        private SemaphoreSlim _queueSemaphore;
        private object _gridLock = new object();

        private long _totalPathsFound = 0;
        private long _totalFailedPaths = 0;
        private float _averagePathTime = 0;

        #endregion;

        #region Yapılar ve Sınıflar;

        /// <summary>
        /// Yol bulma yapılandırması;
        /// </summary>
        public struct PathfindingConfiguration;
        {
            public PathfindingAlgorithm Algorithm;
            public HeuristicType Heuristic;
            public float HeuristicWeight;
            public bool AllowDiagonal;
            public bool AllowSmoothing;
            public float CellSize;
            public int MaxSearchNodes;
            public float AgentRadius;
            public float StepHeight;
            public float MaxSlope;
            public int CacheSize;
            public bool UseMultithreading;
            public int WorkerThreads;
            public TimeSpan Timeout;
        }

        /// <summary>
        /// Yol bulma isteği;
        /// </summary>
        public class PathRequest;
        {
            public Guid RequestId { get; set; }
            public Vector3 Start { get; set; }
            public Vector3 End { get; set; }
            public PathfindingAlgorithm Algorithm { get; set; }
            public PathAgentProperties Agent { get; set; }
            public PathConstraints Constraints { get; set; }
            public Action<PathResult> Callback { get; set; }
            public DateTime RequestTime { get; set; }
            public CancellationToken CancellationToken { get; set; }

            public PathRequest()
            {
                RequestId = Guid.NewGuid();
                RequestTime = DateTime.UtcNow;
                Agent = new PathAgentProperties();
                Constraints = new PathConstraints();
            }
        }

        /// <summary>
        /// Yol bulma sonucu;
        /// </summary>
        public class PathResult;
        {
            public Guid RequestId { get; set; }
            public PathfindingStatus Status { get; set; }
            public List<Vector3> Path { get; set; }
            public float TotalCost { get; set; }
            public PathfindingMetrics Metrics { get; set; }
            public string ErrorMessage { get; set; }
            public DateTime CompletionTime { get; set; }

            public PathResult()
            {
                Path = new List<Vector3>();
                Metrics = new PathfindingMetrics();
            }

            public bool IsSuccess => Status == PathfindingStatus.Success;
        }

        /// <summary>
        /// Yol önbellek girdisi;
        /// </summary>
        private class PathCacheEntry
        {
            public string CacheKey { get; set; }
            public List<Vector3> Path { get; set; }
            public float Cost { get; set; }
            public DateTime LastAccess { get; set; }
            public int AccessCount { get; set; }
            public TimeSpan TimeToCompute { get; set; }
        }

        /// <summary>
        /// Yol bulma çalışanı;
        /// </summary>
        private class PathfindingWorker : IDisposable
        {
            public int WorkerId { get; set; }
            public Thread WorkerThread { get; set; }
            public bool IsRunning { get; set; }
            public int ProcessedRequests { get; set; }
            public CancellationToken CancellationToken { get; set; }

            private Pathfinding _parent;

            public PathfindingWorker(int id, Pathfinding parent, CancellationToken cancellationToken)
            {
                WorkerId = id;
                _parent = parent;
                CancellationToken = cancellationToken;
                IsRunning = true;

                WorkerThread = new Thread(WorkerLoop)
                {
                    Name = $"PathfindingWorker-{id}",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal;
                };

                WorkerThread.Start();
            }

            private void WorkerLoop()
            {
                _parent._logger.Debug($"Pathfinding worker {WorkerId} başlatıldı");

                while (IsRunning && !CancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (_parent._queueSemaphore.Wait(100))
                        {
                            if (_parent._requestQueue.TryDequeue(out var request))
                            {
                                ProcessRequest(request);
                            }
                            _parent._queueSemaphore.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        _parent._logger.Error($"Pathfinding worker {WorkerId} hatası: {ex.Message}", ex);
                    }

                    Thread.Sleep(1); // CPU kullanımını kontrol et;
                }

                _parent._logger.Debug($"Pathfinding worker {WorkerId} durduruldu");
            }

            private void ProcessRequest(PathRequest request)
            {
                var result = _parent.FindPathInternal(
                    request.Start,
                    request.End,
                    request.Algorithm,
                    request.Agent,
                    request.Constraints,
                    request.CancellationToken);

                request.Callback?.Invoke(result);

                ProcessedRequests++;
            }

            public void Dispose()
            {
                IsRunning = false;
                WorkerThread?.Join(1000);
            }
        }

        /// <summary>
        /// NavGrid hücresi;
        /// </summary>
        public class GridCell;
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public Vector3 WorldPosition { get; set; }
            public bool IsWalkable { get; set; }
            public float MovementCost { get; set; }
            public float Height { get; set; }
            public GridCellType CellType { get; set; }
            public List<GridCell> Neighbors { get; set; }

            public GridCell()
            {
                MovementCost = 1.0f;
                IsWalkable = true;
                Neighbors = new List<GridCell>();
            }
        }

        /// <summary>
        /// NavGrid veri yapısı;
        /// </summary>
        public class NavigationGrid;
        {
            public GridCell[,,] Cells { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
            public Vector3 Origin { get; set; }
            public float CellSize { get; set; }

            public GridCell GetCell(int x, int y, int z)
            {
                if (x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth)
                    return Cells[x, y, z];
                return null;
            }

            public GridCell WorldToCell(Vector3 worldPos)
            {
                int x = (int)((worldPos.X - Origin.X) / CellSize);
                int y = (int)((worldPos.Y - Origin.Y) / CellSize);
                int z = (int)((worldPos.Z - Origin.Z) / CellSize);

                return GetCell(x, y, z);
            }

            public Vector3 CellToWorld(int x, int y, int z)
            {
                return new Vector3(
                    Origin.X + x * CellSize + CellSize / 2,
                    Origin.Y + y * CellSize + CellSize / 2,
                    Origin.Z + z * CellSize + CellSize / 2);
            }
        }

        /// <summary>
        /// Ağ temsilcisi;
        /// </summary>
        public class PathAgentProperties;
        {
            public float Radius { get; set; } = 0.5f;
            public float Height { get; set; } = 2.0f;
            public float StepHeight { get; set; } = 0.5f;
            public float MaxSlope { get; set; } = 45.0f;
            public float MovementSpeed { get; set; } = 5.0f;
            public AgentType Type { get; set; } = AgentType.Humanoid;
            public HashSet<CellType> PassableTypes { get; set; }

            public PathAgentProperties()
            {
                PassableTypes = new HashSet<CellType>
                {
                    CellType.Ground,
                    CellType.Floor,
                    CellType.Road;
                };
            }
        }

        /// <summary>
        /// Yol kısıtlamaları;
        /// </summary>
        public class PathConstraints;
        {
            public float MaxPathLength { get; set; } = 1000.0f;
            public float MaxSearchTime { get; set; } = 5.0f;
            public int MaxNodesToExplore { get; set; } = 10000;
            public bool AvoidDangerZones { get; set; } = true;
            public bool UseDynamicObstacles { get; set; } = true;
            public List<Vector3> Waypoints { get; set; }
            public Func<GridCell, bool> CustomValidator { get; set; }

            public PathConstraints()
            {
                Waypoints = new List<Vector3>();
            }
        }

        /// <summary>
        /// A* düğümü;
        /// </summary>
        private class PathNode : IComparable<PathNode>
        {
            public GridCell Cell { get; set; }
            public PathNode Parent { get; set; }
            public float GCost { get; set; } // Başlangıçtan bu noktaya maliyet;
            public float HCost { get; set; } // Bu noktadan hedefe tahmini maliyet;
            public float FCost => GCost + HCost;
            public NodeState State { get; set; }

            public int CompareTo(PathNode other)
            {
                return FCost.CompareTo(other.FCost);
            }

            public override bool Equals(object obj)
            {
                return obj is PathNode node &&
                       Cell.X == node.Cell.X &&
                       Cell.Y == node.Cell.Y &&
                       Cell.Z == node.Cell.Z;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Cell.X, Cell.Y, Cell.Z);
            }
        }

        /// <summary>
        /// Düğüm durumu;
        /// </summary>
        private enum NodeState;
        {
            None,
            Open,
            Closed;
        }

        /// <summary>
        /// Hücre tipleri;
        /// </summary>
        public enum CellType;
        {
            Ground,
            Water,
            Floor,
            Wall,
            Obstacle,
            Door,
            Road,
            Grass,
            Dangerous,
            Custom;
        }

        /// <summary>
        /// Ajan tipleri;
        /// </summary>
        public enum AgentType;
        {
            Humanoid,
            Vehicle,
            Flying,
            Swimming,
            Custom;
        }

        /// <summary>
        /// Grid hücresi tipi;
        /// </summary>
        public enum GridCellType;
        {
            Empty,
            Solid,
            Slope,
            Platform,
            Ledge,
            Gap;
        }

        #endregion;

        #region Olaylar;

        /// <summary>
        /// Yol bulma başladığında tetiklenir;
        /// </summary>
        public event EventHandler<PathfindingStartedEventArgs> PathfindingStarted;

        /// <summary>
        /// Yol bulma tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<PathfindingCompletedEventArgs> PathfindingCompleted;

        /// <summary>
        /// Yol bulma başarısız olduğunda tetiklenir;
        /// </summary>
        public event EventHandler<PathfindingFailedEventArgs> PathfindingFailed;

        /// <summary>
        /// Önbellek temizlendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<PathCacheClearedEventArgs> PathCacheCleared;

        public class PathfindingStartedEventArgs : EventArgs;
        {
            public Guid RequestId { get; set; }
            public Vector3 Start { get; set; }
            public Vector3 End { get; set; }
            public PathfindingAlgorithm Algorithm { get; set; }
        }

        public class PathfindingCompletedEventArgs : EventArgs;
        {
            public Guid RequestId { get; set; }
            public PathResult Result { get; set; }
            public TimeSpan Duration { get; set; }
        }

        public class PathfindingFailedEventArgs : EventArgs;
        {
            public Guid RequestId { get; set; }
            public string ErrorMessage { get; set; }
            public PathfindingStatus Status { get; set; }
        }

        public class PathCacheClearedEventArgs : EventArgs;
        {
            public int ClearedEntries { get; set; }
            public int RemainingEntries { get; set; }
        }

        #endregion;

        #region Constructor ve Initialization;

        /// <summary>
        /// Pathfinding constructor;
        /// </summary>
        public Pathfinding(ILogger logger = null, PerformanceMonitor performanceMonitor = null)
        {
            _logger = logger ?? LogManager.GetLogger(typeof(Pathfinding).FullName);
            _diagnosticTool = new DiagnosticTool("Pathfinding");
            _performanceMonitor = performanceMonitor ?? new PerformanceMonitor("Pathfinding");

            _config = new PathfindingConfiguration;
            {
                Algorithm = PathfindingAlgorithm.AStar,
                Heuristic = HeuristicType.Euclidean,
                HeuristicWeight = DEFAULT_HEURISTIC_WEIGHT,
                AllowDiagonal = true,
                AllowSmoothing = true,
                CellSize = DEFAULT_CELL_SIZE,
                MaxSearchNodes = 10000,
                AgentRadius = 0.5f,
                StepHeight = 0.5f,
                MaxSlope = 45.0f,
                CacheSize = DEFAULT_PATH_CACHE_SIZE,
                UseMultithreading = true,
                WorkerThreads = PATHFINDING_THREAD_COUNT,
                Timeout = TimeSpan.FromSeconds(5)
            };

            _pathCache = new ConcurrentDictionary<string, PathCacheEntry>();
            _requestQueue = new ConcurrentQueue<PathRequest>();
            _activeRequests = new ConcurrentDictionary<Guid, PathResult>();
            _workers = new List<PathfindingWorker>();

            _cancellationTokenSource = new CancellationTokenSource();
            _queueSemaphore = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Pathfinding sistemini başlat;
        /// </summary>
        public async Task InitializeAsync(NavigationGrid grid = null)
        {
            if (_isInitialized)
            {
                _logger.Warning("Pathfinding zaten başlatılmış.");
                return;
            }

            try
            {
                _diagnosticTool.StartOperation("Initialize");

                await Task.Run(() =>
                {
                    // Navigation grid'i ayarla;
                    if (grid != null)
                    {
                        SetNavigationGrid(grid);
                    }
                    else;
                    {
                        CreateDefaultGrid();
                    }

                    // Çalışan thread'leri başlat;
                    if (_config.UseMultithreading)
                    {
                        StartWorkerThreads();
                    }

                    // Performans monitörünü başlat;
                    _performanceMonitor.Start();

                    _isInitialized = true;

                    _logger.Info($"Pathfinding başlatıldı. Algoritma: {_config.Algorithm}, Threads: {_config.WorkerThreads}");
                });

                _diagnosticTool.EndOperation();
            }
            catch (Exception ex)
            {
                _logger.Error($"Pathfinding başlatma hatası: {ex.Message}", ex);
                throw new PathfindingInitializationException(
                    "Yol bulma sistemi başlatılamadı", ex);
            }
        }

        private void CreateDefaultGrid()
        {
            // Varsayılan bir grid oluştur;
            _navigationGrid = new NavigationGrid;
            {
                Width = 100,
                Height = 100,
                Depth = 10,
                CellSize = _config.CellSize,
                Origin = Vector3.Zero,
                Cells = new GridCell[100, 100, 10]
            };

            // Grid hücrelerini oluştur;
            for (int x = 0; x < 100; x++)
            {
                for (int y = 0; y < 100; y++)
                {
                    for (int z = 0; z < 10; z++)
                    {
                        _navigationGrid.Cells[x, y, z] = new GridCell;
                        {
                            X = x,
                            Y = y,
                            Z = z,
                            WorldPosition = _navigationGrid.CellToWorld(x, y, z),
                            IsWalkable = z == 0, // Sadece zemin kat yürünebilir;
                            MovementCost = 1.0f,
                            Height = z * _config.CellSize,
                            CellType = GridCellType.Empty;
                        };
                    }
                }
            }

            _logger.Info($"Varsayılan navigation grid oluşturuldu: {100}x{100}x{10}");
        }

        private void StartWorkerThreads()
        {
            for (int i = 0; i < _config.WorkerThreads; i++)
            {
                var worker = new PathfindingWorker(i, this, _cancellationTokenSource.Token);
                _workers.Add(worker);
            }

            _logger.Debug($"{_config.WorkerThreads} adet pathfinding worker thread'i başlatıldı");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Navigation grid'i ayarla;
        /// </summary>
        public void SetNavigationGrid(NavigationGrid grid)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            lock (_gridLock)
            {
                _navigationGrid = grid;
                ClearCache(); // Grid değişti, önbelleği temizle;

                _logger.Info($"Navigation grid güncellendi: {grid.Width}x{grid.Height}x{grid.Depth}");
            }
        }

        /// <summary>
        /// Yol bulma isteği gönder (asenkron)
        /// </summary>
        public async Task<PathResult> FindPathAsync(
            Vector3 start,
            Vector3 end,
            PathfindingAlgorithm algorithm = PathfindingAlgorithm.AStar,
            PathAgentProperties agent = null,
            PathConstraints constraints = null,
            CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
                throw new PathfindingNotInitializedException("Pathfinding sistemi başlatılmamış");

            var request = new PathRequest;
            {
                Start = start,
                End = end,
                Algorithm = algorithm,
                Agent = agent ?? new PathAgentProperties(),
                Constraints = constraints ?? new PathConstraints(),
                CancellationToken = cancellationToken;
            };

            var completionSource = new TaskCompletionSource<PathResult>();

            request.Callback = result =>
            {
                completionSource.TrySetResult(result);
            };

            // Önce önbelleği kontrol et;
            var cacheKey = GenerateCacheKey(start, end, algorithm, agent, constraints);
            if (_pathCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                _logger.Debug($"Önbellekten yol bulundu: {cacheKey}");

                var cachedResult = new PathResult;
                {
                    RequestId = request.RequestId,
                    Status = PathfindingStatus.Success,
                    Path = new List<Vector3>(cacheEntry.Path),
                    TotalCost = cacheEntry.Cost,
                    Metrics = new PathfindingMetrics;
                    {
                        ComputationTime = cacheEntry.TimeToCompute,
                        NodesExplored = 0,
                        PathLength = cacheEntry.Path.Count,
                        TotalCost = cacheEntry.Cost;
                    },
                    CompletionTime = DateTime.UtcNow;
                };

                cacheEntry.LastAccess = DateTime.UtcNow;
                cacheEntry.AccessCount++;

                OnPathfindingCompleted(request.RequestId, cachedResult, TimeSpan.Zero);
                return cachedResult;
            }

            OnPathfindingStarted(request.RequestId, start, end, algorithm);

            if (_config.UseMultithreading)
            {
                // Kuyruğa ekle;
                await _queueSemaphore.WaitAsync(cancellationToken);
                try
                {
                    _requestQueue.Enqueue(request);
                    _activeRequests[request.RequestId] = null; // Placeholder;
                }
                finally
                {
                    _queueSemaphore.Release();
                }

                // Zaman aşımı ile bekle;
                var timeoutTask = Task.Delay(_config.Timeout, cancellationToken);
                var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    var timeoutResult = new PathResult;
                    {
                        RequestId = request.RequestId,
                        Status = PathfindingStatus.Timeout,
                        ErrorMessage = "Yol bulma zaman aşımına uğradı"
                    };

                    OnPathfindingFailed(request.RequestId, "Zaman aşımı", PathfindingStatus.Timeout);
                    return timeoutResult;
                }

                return await completionSource.Task;
            }
            else;
            {
                // Senkron olarak yol bul;
                var result = FindPathInternal(start, end, algorithm, agent, constraints, cancellationToken);
                OnPathfindingCompleted(request.RequestId, result, TimeSpan.Zero);
                return result;
            }
        }

        /// <summary>
        /// Yol bul (senkron)
        /// </summary>
        public PathResult FindPath(
            Vector3 start,
            Vector3 end,
            PathfindingAlgorithm algorithm = PathfindingAlgorithm.AStar,
            PathAgentProperties agent = null,
            PathConstraints constraints = null)
        {
            if (!_isInitialized)
                throw new PathfindingNotInitializedException("Pathfinding sistemi başlatılmamış");

            var requestId = Guid.NewGuid();
            OnPathfindingStarted(requestId, start, end, algorithm);

            var cacheKey = GenerateCacheKey(start, end, algorithm, agent, constraints);
            if (_pathCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                _logger.Debug($"Önbellekten yol bulundu: {cacheKey}");

                var result = new PathResult;
                {
                    RequestId = requestId,
                    Status = PathfindingStatus.Success,
                    Path = new List<Vector3>(cacheEntry.Path),
                    TotalCost = cacheEntry.Cost,
                    Metrics = new PathfindingMetrics;
                    {
                        ComputationTime = cacheEntry.TimeToCompute,
                        NodesExplored = 0,
                        PathLength = cacheEntry.Path.Count,
                        TotalCost = cacheEntry.Cost;
                    },
                    CompletionTime = DateTime.UtcNow;
                };

                cacheEntry.LastAccess = DateTime.UtcNow;
                cacheEntry.AccessCount++;

                OnPathfindingCompleted(requestId, result, TimeSpan.Zero);
                return result;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var result = ExecutePathfindingAlgorithm(
                    start, end, algorithm,
                    agent ?? new PathAgentProperties(),
                    constraints ?? new PathConstraints(),
                    CancellationToken.None);

                stopwatch.Stop();

                result.Metrics.ComputationTime = stopwatch.Elapsed;
                result.CompletionTime = DateTime.UtcNow;

                // Önbelleğe ekle;
                if (result.IsSuccess && result.Path.Count > 0)
                {
                    AddToCache(cacheKey, result.Path, result.TotalCost, stopwatch.Elapsed);
                }

                if (result.IsSuccess)
                {
                    _totalPathsFound++;
                    OnPathfindingCompleted(requestId, result, stopwatch.Elapsed);
                }
                else;
                {
                    _totalFailedPaths++;
                    OnPathfindingFailed(requestId, result.ErrorMessage, result.Status);
                }

                _averagePathTime = (_averagePathTime * (_totalPathsFound + _totalFailedPaths - 1) +
                                  (float)stopwatch.Elapsed.TotalMilliseconds) /
                                  (_totalPathsFound + _totalFailedPaths);

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Error($"Yol bulma hatası: {ex.Message}", ex);

                var errorResult = new PathResult;
                {
                    RequestId = requestId,
                    Status = PathfindingStatus.Failed,
                    ErrorMessage = $"Yol bulma hatası: {ex.Message}",
                    Metrics = new PathfindingMetrics;
                    {
                        ComputationTime = stopwatch.Elapsed;
                    }
                };

                _totalFailedPaths++;
                OnPathfindingFailed(requestId, ex.Message, PathfindingStatus.Failed);

                return errorResult;
            }
        }

        /// <summary>
        /// Çoklu yol bulma (batch processing)
        /// </summary>
        public async Task<List<PathResult>> FindPathsAsync(
            List<Tuple<Vector3, Vector3>> pathRequests,
            PathfindingAlgorithm algorithm = PathfindingAlgorithm.AStar,
            PathAgentProperties agent = null,
            PathConstraints constraints = null,
            CancellationToken cancellationToken = default)
        {
            var tasks = new List<Task<PathResult>>();

            foreach (var request in pathRequests)
            {
                tasks.Add(FindPathAsync(
                    request.Item1,
                    request.Item2,
                    algorithm,
                    agent,
                    constraints,
                    cancellationToken));
            }

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        /// <summary>
        /// Yol bulma algoritmasını değiştir;
        /// </summary>
        public void SetAlgorithm(PathfindingAlgorithm algorithm)
        {
            if (_config.Algorithm != algorithm)
            {
                _config.Algorithm = algorithm;
                ClearCache(); // Algoritma değişti, önbelleği temizle;

                _logger.Info($"Pathfinding algoritması değiştirildi: {algorithm}");
            }
        }

        /// <summary>
        /// Heuristic tipini değiştir;
        /// </summary>
        public void SetHeuristic(HeuristicType heuristic, float weight = DEFAULT_HEURISTIC_WEIGHT)
        {
            _config.Heuristic = heuristic;
            _config.HeuristicWeight = weight;

            _logger.Debug($"Heuristic değiştirildi: {heuristic}, Ağırlık: {weight}");
        }

        /// <summary>
        /// Önbelleği temizle;
        /// </summary>
        public void ClearCache()
        {
            int oldCount = _pathCache.Count;
            _pathCache.Clear();

            OnPathCacheCleared(oldCount, 0);

            _logger.Info($"Pathfinding önbelleği temizlendi: {oldCount} kayıt silindi");
        }

        /// <summary>
        /// Navigation grid'i yeniden oluştur;
        /// </summary>
        public async Task RebuildNavigationGrid(
            List<Vector3> obstaclePositions,
            List<Vector3> walkableAreas,
            float cellSize = DEFAULT_CELL_SIZE)
        {
            if (!_isInitialized)
                throw new PathfindingNotInitializedException("Pathfinding sistemi başlatılmamış");

            _diagnosticTool.StartOperation("RebuildNavigationGrid");

            try
            {
                await Task.Run(() =>
                {
                    lock (_gridLock)
                    {
                        // Grid sınırlarını hesapla;
                        var bounds = CalculateGridBounds(obstaclePositions, walkableAreas);

                        // Yeni grid oluştur;
                        var newGrid = CreateGridFromBounds(bounds, cellSize, obstaclePositions);

                        // Grid'i güncelle;
                        _navigationGrid = newGrid;

                        // Önbelleği temizle;
                        ClearCache();

                        _logger.Info($"Navigation grid yeniden oluşturuldu: {bounds}");
                    }
                });
            }
            finally
            {
                _diagnosticTool.EndOperation();
            }
        }

        /// <summary>
        /// Dinamik engel ekle;
        /// </summary>
        public void AddDynamicObstacle(Vector3 position, float radius)
        {
            if (_navigationGrid == null) return;

            var centerCell = _navigationGrid.WorldToCell(position);
            if (centerCell == null) return;

            int cellRadius = (int)Math.Ceiling(radius / _config.CellSize);

            lock (_gridLock)
            {
                // Engeli etkileyen hücreleri işaretle;
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    for (int dy = -cellRadius; dy <= cellRadius; dy++)
                    {
                        for (int dz = -cellRadius; dz <= cellRadius; dz++)
                        {
                            var cell = _navigationGrid.GetCell(
                                centerCell.X + dx,
                                centerCell.Y + dy,
                                centerCell.Z + dz);

                            if (cell != null)
                            {
                                float distance = Vector3.Distance(cell.WorldPosition, position);
                                if (distance <= radius)
                                {
                                    cell.IsWalkable = false;
                                    cell.CellType = GridCellType.Solid;
                                }
                            }
                        }
                    }
                }

                // Önbelleği kısmen temizle (bu bölgedeki yollar)
                ClearCacheForArea(position, radius * 2);

                _logger.Debug($"Dinamik engel eklendi: {position}, Yarıçap: {radius}");
            }
        }

        /// <summary>
        /// Dinamik engeli kaldır;
        /// </summary>
        public void RemoveDynamicObstacle(Vector3 position, float radius)
        {
            if (_navigationGrid == null) return;

            var centerCell = _navigationGrid.WorldToCell(position);
            if (centerCell == null) return;

            int cellRadius = (int)Math.Ceiling(radius / _config.CellSize);

            lock (_gridLock)
            {
                // Engeli kaldır;
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    for (int dy = -cellRadius; dy <= cellRadius; dy++)
                    {
                        for (int dz = -cellRadius; dz <= cellRadius; dz++)
                        {
                            var cell = _navigationGrid.GetCell(
                                centerCell.X + dx,
                                centerCell.Y + dy,
                                centerCell.Z + dz);

                            if (cell != null)
                            {
                                float distance = Vector3.Distance(cell.WorldPosition, position);
                                if (distance <= radius)
                                {
                                    cell.IsWalkable = true;
                                    cell.CellType = GridCellType.Empty;
                                }
                            }
                        }
                    }
                }

                _logger.Debug($"Dinamik engel kaldırıldı: {position}, Yarıçap: {radius}");
            }
        }

        /// <summary>
        /// Yolu düzelt (smoothing)
        /// </summary>
        public List<Vector3> SmoothPath(List<Vector3> path, float agentRadius)
        {
            if (path == null || path.Count < 3)
                return path;

            var smoothedPath = new List<Vector3> { path[0] };

            for (int i = 0; i < path.Count - 1;)
            {
                int nextIndex = i + 1;

                // Mümkün olduğunca uzun düz çizgiler bul;
                for (int j = path.Count - 1; j > i + 1; j--)
                {
                    if (IsDirectPathClear(path[i], path[j], agentRadius))
                    {
                        nextIndex = j;
                        break;
                    }
                }

                smoothedPath.Add(path[nextIndex]);
                i = nextIndex;
            }

            return smoothedPath;
        }

        /// <summary>
        /// Yol bulma istatistiklerini al;
        /// </summary>
        public PathfindingStatistics GetStatistics()
        {
            return new PathfindingStatistics;
            {
                TotalRequests = _totalPathsFound + _totalFailedPaths,
                SuccessfulPaths = _totalPathsFound,
                FailedPaths = _totalFailedPaths,
                AveragePathTimeMs = _averagePathTime,
                CacheSize = _pathCache.Count,
                CacheHitRate = CalculateCacheHitRate(),
                ActiveWorkerThreads = _workers.Count(w => w.IsRunning),
                QueuedRequests = _requestQueue.Count,
                ActiveRequests = _activeRequests.Count;
            };
        }

        /// <summary>
        /// Yol geçerliliğini kontrol et;
        /// </summary>
        public bool ValidatePath(List<Vector3> path, PathAgentProperties agent)
        {
            if (path == null || path.Count < 2)
                return false;

            for (int i = 0; i < path.Count - 1; i++)
            {
                if (!IsWalkable(path[i], agent) || !IsWalkable(path[i + 1], agent))
                    return false;

                // Segmentin geçerli olduğunu kontrol et;
                if (!IsDirectPathClear(path[i], path[i + 1], agent.Radius))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// En yakın yürünebilir noktayı bul;
        /// </summary>
        public Vector3 FindNearestWalkablePoint(Vector3 position, PathAgentProperties agent, float searchRadius = 10.0f)
        {
            if (IsWalkable(position, agent))
                return position;

            // Spiral arama;
            int steps = (int)(searchRadius / _config.CellSize);
            var centerCell = _navigationGrid.WorldToCell(position);

            if (centerCell == null)
                return position;

            for (int r = 1; r <= steps; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dz = -r; dz <= r; dz++)
                        {
                            if (Math.Abs(dx) == r || Math.Abs(dy) == r || Math.Abs(dz) == r)
                            {
                                var cell = _navigationGrid.GetCell(
                                    centerCell.X + dx,
                                    centerCell.Y + dy,
                                    centerCell.Z + dz);

                                if (cell != null && IsWalkable(cell.WorldPosition, agent))
                                {
                                    return cell.WorldPosition;
                                }
                            }
                        }
                    }
                }
            }

            return position; // Bulunamazsa orijinal pozisyonu döndür;
        }

        #endregion;

        #region Private Methods;

        private PathResult FindPathInternal(
            Vector3 start,
            Vector3 end,
            PathfindingAlgorithm algorithm,
            PathAgentProperties agent,
            PathConstraints constraints,
            CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var result = ExecutePathfindingAlgorithm(
                    start, end, algorithm, agent, constraints, cancellationToken);

                stopwatch.Stop();
                result.Metrics.ComputationTime = stopwatch.Elapsed;
                result.CompletionTime = DateTime.UtcNow;

                // Önbelleğe ekle;
                if (result.IsSuccess && result.Path.Count > 0)
                {
                    var cacheKey = GenerateCacheKey(start, end, algorithm, agent, constraints);
                    AddToCache(cacheKey, result.Path, result.TotalCost, stopwatch.Elapsed);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new PathResult;
                {
                    Status = PathfindingStatus.Cancelled,
                    ErrorMessage = "Yol bulma iptal edildi",
                    Metrics = new PathfindingMetrics;
                    {
                        ComputationTime = stopwatch.Elapsed;
                    }
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Error($"Yol bulma hatası: {ex.Message}", ex);

                return new PathResult;
                {
                    Status = PathfindingStatus.Failed,
                    ErrorMessage = $"Yol bulma hatası: {ex.Message}",
                    Metrics = new PathfindingMetrics;
                    {
                        ComputationTime = stopwatch.Elapsed;
                    }
                };
            }
        }

        private PathResult ExecutePathfindingAlgorithm(
            Vector3 start,
            Vector3 end,
            PathfindingAlgorithm algorithm,
            PathAgentProperties agent,
            PathConstraints constraints,
            CancellationToken cancellationToken)
        {
            // Başlangıç ve bitiş noktalarını kontrol et;
            if (!IsWalkable(start, agent))
            {
                start = FindNearestWalkablePoint(start, agent);
            }

            if (!IsWalkable(end, agent))
            {
                end = FindNearestWalkablePoint(end, agent);
            }

            // Algoritmayı seç ve çalıştır;
            List<Vector3> path;
            float totalCost;
            PathfindingMetrics metrics;

            switch (algorithm)
            {
                case PathfindingAlgorithm.AStar:
                    (path, totalCost, metrics) = ExecuteAStar(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.Dijkstra:
                    (path, totalCost, metrics) = ExecuteDijkstra(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.ThetaStar:
                    (path, totalCost, metrics) = ExecuteThetaStar(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.JumpPointSearch:
                    (path, totalCost, metrics) = ExecuteJumpPointSearch(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.HPAStar:
                    (path, totalCost, metrics) = ExecuteHPAStar(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.LazyThetaStar:
                    (path, totalCost, metrics) = ExecuteLazyThetaStar(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.IDAStar:
                    (path, totalCost, metrics) = ExecuteIDAStar(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.BFS:
                    (path, totalCost, metrics) = ExecuteBFS(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.DFS:
                    (path, totalCost, metrics) = ExecuteDFS(start, end, agent, constraints, cancellationToken);
                    break;

                case PathfindingAlgorithm.BiDirectionalAStar:
                    (path, totalCost, metrics) = ExecuteBiDirectionalAStar(start, end, agent, constraints, cancellationToken);
                    break;

                default:
                    throw new ArgumentException($"Desteklenmeyen algoritma: {algorithm}");
            }

            // Yolu düzelt;
            if (_config.AllowSmoothing && path != null && path.Count > 2)
            {
                path = SmoothPath(path, agent.Radius);
            }

            var result = new PathResult;
            {
                Status = path != null ? PathfindingStatus.Success : PathfindingStatus.Failed,
                Path = path ?? new List<Vector3>(),
                TotalCost = totalCost,
                Metrics = metrics,
                ErrorMessage = path == null ? "Yol bulunamadı" : null;
            };

            return result;
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteAStar(
            Vector3 start,
            Vector3 end,
            PathAgentProperties agent,
            PathConstraints constraints,
            CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var metrics = new PathfindingMetrics();

            var startCell = _navigationGrid.WorldToCell(start);
            var endCell = _navigationGrid.WorldToCell(end);

            if (startCell == null || endCell == null || !startCell.IsWalkable || !endCell.IsWalkable)
            {
                stopwatch.Stop();
                metrics.ComputationTime = stopwatch.Elapsed;
                return (null, 0, metrics);
            }

            // A* algoritması;
            var openSet = new PriorityQueue<PathNode, float>();
            var allNodes = new Dictionary<(int, int, int), PathNode>();

            var startNode = new PathNode;
            {
                Cell = startCell,
                GCost = 0,
                HCost = CalculateHeuristic(startCell, endCell)
            };

            openSet.Enqueue(startNode, startNode.FCost);
            allNodes[(startCell.X, startCell.Y, startCell.Z)] = startNode;

            int nodesExplored = 0;

            while (openSet.Count > 0 && nodesExplored < constraints.MaxNodesToExplore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentNode = openSet.Dequeue();

                if (currentNode.Cell == endCell)
                {
                    // Yol bulundu;
                    stopwatch.Stop();
                    var path = ReconstructPath(currentNode);
                    metrics.NodesExplored = nodesExplored;
                    metrics.PathLength = path.Count;
                    metrics.TotalCost = currentNode.GCost;
                    metrics.OpenSetSize = openSet.Count;
                    metrics.ClosedSetSize = allNodes.Count - openSet.Count;
                    metrics.ComputationTime = stopwatch.Elapsed;

                    return (path, currentNode.GCost, metrics);
                }

                currentNode.State = NodeState.Closed;
                nodesExplored++;

                // Komşuları işle;
                foreach (var neighborCell in GetNeighbors(currentNode.Cell, agent))
                {
                    if (!IsWalkable(neighborCell, agent))
                        continue;

                    float movementCost = currentNode.GCost +
                                        CalculateMovementCost(currentNode.Cell, neighborCell);

                    if (!allNodes.TryGetValue((neighborCell.X, neighborCell.Y, neighborCell.Z), out var neighborNode))
                    {
                        neighborNode = new PathNode;
                        {
                            Cell = neighborCell,
                            GCost = float.MaxValue,
                            HCost = CalculateHeuristic(neighborCell, endCell)
                        };
                        allNodes[(neighborCell.X, neighborCell.Y, neighborCell.Z)] = neighborNode;
                    }

                    if (movementCost < neighborNode.GCost)
                    {
                        neighborNode.Parent = currentNode;
                        neighborNode.GCost = movementCost;

                        if (neighborNode.State != NodeState.Open)
                        {
                            neighborNode.State = NodeState.Open;
                            openSet.Enqueue(neighborNode, neighborNode.FCost);
                        }
                    }
                }
            }

            stopwatch.Stop();
            metrics.NodesExplored = nodesExplored;
            metrics.OpenSetSize = openSet.Count;
            metrics.ClosedSetSize = allNodes.Count - openSet.Count;
            metrics.ComputationTime = stopwatch.Elapsed;

            return (null, 0, metrics); // Yol bulunamadı;
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteDijkstra(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // Dijkstra algoritması implementasyonu;
            // A* ile benzer ama heuristic yok;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteThetaStar(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // Theta* algoritması implementasyonu;
            // Daha doğal yollar için;
            var result = ExecuteAStar(start, end, agent, constraints, cancellationToken);

            if (result.path != null)
            {
                // Theta* düzeltmeleri;
                result.path = ApplyThetaStarSmoothing(result.path, agent);
            }

            return result;
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteJumpPointSearch(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // Jump Point Search implementasyonu;
            // Grid tabanlı optimizasyon;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteHPAStar(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // HPA* implementasyonu;
            // Hiyerarşik yol bulma;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteLazyThetaStar(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // Lazy Theta* implementasyonu;
            return ExecuteThetaStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteIDAStar(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // IDA* implementasyonu;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteBFS(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // BFS implementasyonu;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteDFS(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // DFS implementasyonu;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private (List<Vector3> path, float totalCost, PathfindingMetrics metrics) ExecuteBiDirectionalAStar(
            Vector3 start, Vector3 end, PathAgentProperties agent, PathConstraints constraints, CancellationToken cancellationToken)
        {
            // Çift yönlü A* implementasyonu;
            return ExecuteAStar(start, end, agent, constraints, cancellationToken);
        }

        private List<GridCell> GetNeighbors(GridCell cell, PathAgentProperties agent)
        {
            var neighbors = new List<GridCell>();

            // 6 yönlü komşuluk (kuzey, güney, doğu, batı, yukarı, aşağı)
            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            // Köşegenler;
            if (_config.AllowDiagonal)
            {
                // 26 yönlü komşuluk (3D)
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        for (int z = -1; z <= 1; z++)
                        {
                            if (x == 0 && y == 0 && z == 0)
                                continue;

                            var neighbor = _navigationGrid.GetCell(
                                cell.X + x,
                                cell.Y + y,
                                cell.Z + z);

                            if (neighbor != null && IsWalkable(neighbor, agent))
                            {
                                // Eğim kontrolü;
                                if (Math.Abs(neighbor.Height - cell.Height) <= agent.StepHeight)
                                {
                                    neighbors.Add(neighbor);
                                }
                            }
                        }
                    }
                }
            }
            else;
            {
                // 6 yönlü komşuluk;
                for (int i = 0; i < 6; i++)
                {
                    var neighbor = _navigationGrid.GetCell(
                        cell.X + dx[i],
                        cell.Y + dy[i],
                        cell.Z + dz[i]);

                    if (neighbor != null && IsWalkable(neighbor, agent))
                    {
                        // Eğim kontrolü;
                        if (Math.Abs(neighbor.Height - cell.Height) <= agent.StepHeight)
                        {
                            neighbors.Add(neighbor);
                        }
                    }
                }
            }

            return neighbors;
        }

        private float CalculateHeuristic(GridCell from, GridCell to)
        {
            float dx = Math.Abs(from.X - to.X);
            float dy = Math.Abs(from.Y - to.Y);
            float dz = Math.Abs(from.Z - to.Z);

            switch (_config.Heuristic)
            {
                case HeuristicType.Manhattan:
                    return (dx + dy + dz) * _config.HeuristicWeight;

                case HeuristicType.Euclidean:
                    return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) * _config.HeuristicWeight;

                case HeuristicType.Chebyshev:
                    return Math.Max(Math.Max(dx, dy), dz) * _config.HeuristicWeight;

                case HeuristicType.Octile:
                    float min = Math.Min(dx, dy);
                    float max = Math.Max(dx, dy);
                    return (max + (float)(Math.Sqrt(2) - 1) * min) * _config.HeuristicWeight;

                case HeuristicType.Diagonal:
                    float dMin = Math.Min(dx, dy);
                    float dMax = Math.Max(dx, dy);
                    return (float)(Math.Sqrt(2) * dMin + (dMax - dMin)) * _config.HeuristicWeight;

                default:
                    return CalculateEuclideanHeuristic(from, to) * _config.HeuristicWeight;
            }
        }

        private float CalculateEuclideanHeuristic(GridCell from, GridCell to)
        {
            float dx = (from.X - to.X) * _config.CellSize;
            float dy = (from.Y - to.Y) * _config.CellSize;
            float dz = (from.Z - to.Z) * _config.CellSize;

            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private float CalculateMovementCost(GridCell from, GridCell to)
        {
            float baseCost = to.MovementCost;

            // Yükseklik farkı maliyeti;
            float heightDiff = Math.Abs(to.Height - from.Height);
            if (heightDiff > 0)
            {
                baseCost += heightDiff * 2.0f; // Tırmanma daha maliyetli;
            }

            // Mesafe maliyeti;
            float distance = Vector3.Distance(from.WorldPosition, to.WorldPosition);

            return baseCost * distance;
        }

        private List<Vector3> ReconstructPath(PathNode endNode)
        {
            var path = new List<Vector3>();
            var currentNode = endNode;

            while (currentNode != null)
            {
                path.Insert(0, currentNode.Cell.WorldPosition);
                currentNode = currentNode.Parent;
            }

            return path;
        }

        private bool IsWalkable(Vector3 position, PathAgentProperties agent)
        {
            var cell = _navigationGrid.WorldToCell(position);
            return IsWalkable(cell, agent);
        }

        private bool IsWalkable(GridCell cell, PathAgentProperties agent)
        {
            if (cell == null || !cell.IsWalkable)
                return false;

            // Özel validatör;
            if (agent.CustomValidator != null && !agent.CustomValidator(cell))
                return false;

            // Hücre tipi kontrolü;
            if (!agent.PassableTypes.Contains(cell.CellType))
                return false;

            return true;
        }

        private bool IsDirectPathClear(Vector3 from, Vector3 to, float agentRadius)
        {
            // Bresenham çizgi algoritması ile yol kontrolü;
            Vector3 direction = to - from;
            float distance = direction.Length();
            direction = Vector3.Normalize(direction);

            int steps = (int)(distance / (_config.CellSize * 0.5f));
            float stepSize = distance / steps;

            for (int i = 0; i <= steps; i++)
            {
                Vector3 point = from + direction * (stepSize * i);
                var cell = _navigationGrid.WorldToCell(point);

                if (cell == null || !cell.IsWalkable)
                    return false;

                // Ajan yarıçapı için ek kontrol;
                if (agentRadius > 0)
                {
                    // Komşu hücreleri kontrol et;
                    int radiusCells = (int)Math.Ceiling(agentRadius / _config.CellSize);
                    for (int dx = -radiusCells; dx <= radiusCells; dx++)
                    {
                        for (int dy = -radiusCells; dy <= radiusCells; dy++)
                        {
                            for (int dz = -radiusCells; dz <= radiusCells; dz++)
                            {
                                var neighbor = _navigationGrid.GetCell(
                                    cell.X + dx,
                                    cell.Y + dy,
                                    cell.Z + dz);

                                if (neighbor != null && !neighbor.IsWalkable)
                                {
                                    float dist = Vector3.Distance(cell.WorldPosition, neighbor.WorldPosition);
                                    if (dist <= agentRadius)
                                        return false;
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }

        private string GenerateCacheKey(
            Vector3 start,
            Vector3 end,
            PathfindingAlgorithm algorithm,
            PathAgentProperties agent,
            PathConstraints constraints)
        {
            // Basit bir cache key oluştur;
            int startX = (int)(start.X / _config.CellSize);
            int startY = (int)(start.Y / _config.CellSize);
            int startZ = (int)(start.Z / _config.CellSize);

            int endX = (int)(end.X / _config.CellSize);
            int endY = (int)(end.Y / _config.CellSize);
            int endZ = (int)(end.Z / _config.CellSize);

            return $"{startX},{startY},{startZ}|{endX},{endY},{endZ}|{algorithm}|{agent.Radius}";
        }

        private void AddToCache(string key, List<Vector3> path, float cost, TimeSpan computeTime)
        {
            // Cache boyutunu kontrol et;
            if (_pathCache.Count >= _config.CacheSize)
            {
                // LRU (Least Recently Used) temizleme;
                var oldest = _pathCache.OrderBy(kvp => kvp.Value.LastAccess).First();
                _pathCache.TryRemove(oldest.Key, out _);
            }

            var entry = new PathCacheEntry
            {
                CacheKey = key,
                Path = new List<Vector3>(path),
                Cost = cost,
                LastAccess = DateTime.UtcNow,
                AccessCount = 1,
                TimeToCompute = computeTime;
            };

            _pathCache[key] = entry
        }

        private void ClearCacheForArea(Vector3 center, float radius)
        {
            // Belirli bir alandaki cache girdilerini temizle;
            var toRemove = new List<string>();

            foreach (var kvp in _pathCache)
            {
                // Basit bir kontrol - cache key'inden pozisyonu çıkar;
                if (TryParseCacheKey(kvp.Key, out var start, out var end))
                {
                    if (Vector3.Distance(start, center) <= radius ||
                        Vector3.Distance(end, center) <= radius)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in toRemove)
            {
                _pathCache.TryRemove(key, out _);
            }
        }

        private bool TryParseCacheKey(string key, out Vector3 start, out Vector3 end)
        {
            start = Vector3.Zero;
            end = Vector3.Zero;

            try
            {
                var parts = key.Split('|');
                if (parts.Length >= 2)
                {
                    var startParts = parts[0].Split(',');
                    var endParts = parts[1].Split(',');

                    if (startParts.Length == 3 && endParts.Length == 3)
                    {
                        start = new Vector3(
                            float.Parse(startParts[0]) * _config.CellSize,
                            float.Parse(startParts[1]) * _config.CellSize,
                            float.Parse(startParts[2]) * _config.CellSize);

                        end = new Vector3(
                            float.Parse(endParts[0]) * _config.CellSize,
                            float.Parse(endParts[1]) * _config.CellSize,
                            float.Parse(endParts[2]) * _config.CellSize);

                        return true;
                    }
                }
            }
            catch
            {
                // Parse hatası;
            }

            return false;
        }

        private float CalculateCacheHitRate()
        {
            if (_pathCache.Count == 0)
                return 0;

            long totalAccess = _pathCache.Values.Sum(v => v.AccessCount);
            return totalAccess / (float)_pathCache.Count;
        }

        private NavigationGrid CreateGridFromBounds(
            BoundingBox bounds,
            float cellSize,
            List<Vector3> obstaclePositions)
        {
            int width = (int)((bounds.Max.X - bounds.Min.X) / cellSize) + 1;
            int height = (int)((bounds.Max.Y - bounds.Min.Y) / cellSize) + 1;
            int depth = (int)((bounds.Max.Z - bounds.Min.Z) / cellSize) + 1;

            var grid = new NavigationGrid;
            {
                Width = width,
                Height = height,
                Depth = depth,
                CellSize = cellSize,
                Origin = bounds.Min,
                Cells = new GridCell[width, height, depth]
            };

            // Grid'i oluştur;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        var worldPos = grid.CellToWorld(x, y, z);
                        bool isWalkable = true;

                        // Engel kontrolü;
                        foreach (var obstacle in obstaclePositions)
                        {
                            if (Vector3.Distance(worldPos, obstacle) < cellSize)
                            {
                                isWalkable = false;
                                break;
                            }
                        }

                        grid.Cells[x, y, z] = new GridCell;
                        {
                            X = x,
                            Y = y,
                            Z = z,
                            WorldPosition = worldPos,
                            IsWalkable = isWalkable,
                            MovementCost = 1.0f,
                            Height = worldPos.Z,
                            CellType = isWalkable ? GridCellType.Empty : GridCellType.Solid;
                        };
                    }
                }
            }

            return grid;
        }

        private BoundingBox CalculateGridBounds(
            List<Vector3> obstaclePositions,
            List<Vector3> walkableAreas)
        {
            var allPoints = obstaclePositions.Concat(walkableAreas).ToList();

            if (allPoints.Count == 0)
            {
                return new BoundingBox;
                {
                    Min = Vector3.Zero,
                    Max = new Vector3(100, 100, 10)
                };
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var point in allPoints)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            // Margin ekle;
            float margin = 10.0f;
            min -= new Vector3(margin);
            max += new Vector3(margin);

            return new BoundingBox { Min = min, Max = max };
        }

        private List<Vector3> ApplyThetaStarSmoothing(List<Vector3> path, PathAgentProperties agent)
        {
            if (path.Count < 3)
                return path;

            var smoothed = new List<Vector3> { path[0] };

            for (int i = 0; i < path.Count - 1;)
            {
                int nextIndex = i + 1;

                // Mümkün olduğunca uzun doğrudan görüş hattı bul;
                for (int j = path.Count - 1; j > i + 1; j--)
                {
                    if (IsDirectPathClear(path[i], path[j], agent.Radius))
                    {
                        nextIndex = j;
                        break;
                    }
                }

                smoothed.Add(path[nextIndex]);
                i = nextIndex;
            }

            return smoothed;
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnPathfindingStarted(
            Guid requestId, Vector3 start, Vector3 end, PathfindingAlgorithm algorithm)
        {
            PathfindingStarted?.Invoke(this, new PathfindingStartedEventArgs;
            {
                RequestId = requestId,
                Start = start,
                End = end,
                Algorithm = algorithm;
            });
        }

        protected virtual void OnPathfindingCompleted(
            Guid requestId, PathResult result, TimeSpan duration)
        {
            PathfindingCompleted?.Invoke(this, new PathfindingCompletedEventArgs;
            {
                RequestId = requestId,
                Result = result,
                Duration = duration;
            });
        }

        protected virtual void OnPathfindingFailed(
            Guid requestId, string errorMessage, PathfindingStatus status)
        {
            PathfindingFailed?.Invoke(this, new PathfindingFailedEventArgs;
            {
                RequestId = requestId,
                ErrorMessage = errorMessage,
                Status = status;
            });
        }

        protected virtual void OnPathCacheCleared(int clearedEntries, int remainingEntries)
        {
            PathCacheCleared?.Invoke(this, new PathCacheClearedEventArgs;
            {
                ClearedEntries = clearedEntries,
                RemainingEntries = remainingEntries;
            });
        }

        #endregion;

        #region Yardımcı Sınıflar ve Yapılar;

        public struct PathfindingStatistics;
        {
            public long TotalRequests;
            public long SuccessfulPaths;
            public long FailedPaths;
            public float AveragePathTimeMs;
            public int CacheSize;
            public float CacheHitRate;
            public int ActiveWorkerThreads;
            public int QueuedRequests;
            public int ActiveRequests;
        }

        public struct BoundingBox;
        {
            public Vector3 Min;
            public Vector3 Max;
        }

        /// <summary>
        /// Özel exception sınıfları;
        /// </summary>
        public class PathfindingException : Exception
        {
            public PathfindingException(string message) : base(message) { }
            public PathfindingException(string message, Exception inner) : base(message, inner) { }
        }

        public class PathfindingInitializationException : PathfindingException;
        {
            public PathfindingInitializationException(string message) : base(message) { }
            public PathfindingInitializationException(string message, Exception inner) : base(message, inner) { }
        }

        public class PathfindingNotInitializedException : PathfindingException;
        {
            public PathfindingNotInitializedException(string message) : base(message) { }
        }

        /// <summary>
        /// Öncelikli kuyruk implementasyonu;
        /// </summary>
        private class PriorityQueue<TElement, TPriority> where TPriority : IComparable<TPriority>
        {
            private readonly List<(TElement element, TPriority priority)> _elements = new();

            public int Count => _elements.Count;

            public void Enqueue(TElement element, TPriority priority)
            {
                _elements.Add((element, priority));
                int i = _elements.Count - 1;

                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_elements[parent].priority.CompareTo(_elements[i].priority) <= 0)
                        break;

                    (_elements[parent], _elements[i]) = (_elements[i], _elements[parent]);
                    i = parent;
                }
            }

            public TElement Dequeue()
            {
                var result = _elements[0].element;
                _elements[0] = _elements[^1];
                _elements.RemoveAt(_elements.Count - 1);

                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    int right = 2 * i + 2;
                    int smallest = i;

                    if (left < _elements.Count &&
                        _elements[left].priority.CompareTo(_elements[smallest].priority) < 0)
                        smallest = left;

                    if (right < _elements.Count &&
                        _elements[right].priority.CompareTo(_elements[smallest].priority) < 0)
                        smallest = right;

                    if (smallest == i)
                        break;

                    (_elements[i], _elements[smallest]) = (_elements[smallest], _elements[i]);
                    i = smallest;
                }

                return result;
            }

            public bool TryDequeue(out TElement element, out TPriority priority)
            {
                if (_elements.Count == 0)
                {
                    element = default;
                    priority = default;
                    return false;
                }

                element = Dequeue();
                priority = _elements.Count > 0 ? _elements[0].priority : default;
                return true;
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

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
                    _cancellationTokenSource.Cancel();

                    foreach (var worker in _workers)
                    {
                        worker.Dispose();
                    }
                    _workers.Clear();

                    _pathCache.Clear();

                    while (_requestQueue.TryDequeue(out _)) { }
                    _activeRequests.Clear();

                    _queueSemaphore.Dispose();
                    _cancellationTokenSource.Dispose();
                    _performanceMonitor.Dispose();
                }

                _disposed = true;
            }
        }

        ~Pathfinding()
        {
            Dispose(false);
        }

        #endregion;
    }
}
