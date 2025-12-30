using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using Microsoft.Extensions.Logging;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Common.Utilities;
using NEDA.Core.SystemControl;
using NEDA.AI.MachineLearning;
using NEDA.CharacterSystems.AI_Behaviors;
using NEDA.Brain.DecisionMaking;
using NEDA.Brain.MemorySystem;

namespace NEDA.GameDesign.LevelDesign.NavigationMesh;
{
    /// <summary>
    /// AI Navigation system for pathfinding and movement in 3D environments.
    /// Provides advanced navigation capabilities including dynamic obstacle avoidance,
    /// terrain analysis, and intelligent path planning.
    /// </summary>
    public class AINavigation : IDisposable
    {
        #region Constants;

        private const float DEFAULT_AGENT_RADIUS = 0.5f;
        private const float DEFAULT_AGENT_HEIGHT = 2.0f;
        private const float DEFAULT_STEP_HEIGHT = 0.4f;
        private const float DEFAULT_MAX_SLOPE = 45.0f;
        private const int DEFAULT_MAX_NODES = 2048;
        private const float DEFAULT_CELL_SIZE = 0.3f;
        private const float DEFAULT_CELL_HEIGHT = 0.2f;

        #endregion;

        #region Fields;

        private readonly ILogger<AINavigation> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly ISystemManager _systemManager;
        private readonly DecisionEngine _decisionEngine;
        private readonly IMemorySystem _memorySystem;
        private readonly IMLModel _pathfindingModel;

        private NavigationMesh _navMesh;
        private PathfindingGrid _pathfindingGrid;
        private readonly Dictionary<Guid, NavigationAgent> _activeAgents;
        private readonly List<NavigationObstacle> _dynamicObstacles;
        private readonly object _navMeshLock = new object();
        private readonly object _agentsLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private NavigationConfig _config;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the current navigation mesh;
        /// </summary>
        public NavigationMesh NavMesh => _navMesh;

        /// <summary>
        /// Gets the pathfinding grid;
        /// </summary>
        public PathfindingGrid PathfindingGrid => _pathfindingGrid;

        /// <summary>
        /// Gets the navigation configuration;
        /// </summary>
        public NavigationConfig Config => _config;

        /// <summary>
        /// Gets the number of active navigation agents;
        /// </summary>
        public int ActiveAgentCount;
        {
            get;
            {
                lock (_agentsLock)
                {
                    return _activeAgents.Count;
                }
            }
        }

        /// <summary>
        /// Gets the number of dynamic obstacles;
        /// </summary>
        public int DynamicObstacleCount => _dynamicObstacles.Count;

        /// <summary>
        /// Gets whether the navigation system is initialized;
        /// </summary>
        public bool IsInitialized => _isInitialized;

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when a navigation mesh is built or updated;
        /// </summary>
        public event EventHandler<NavigationMeshUpdatedEventArgs> OnNavigationMeshUpdated;

        /// <summary>
        /// Event raised when an agent reaches its destination;
        /// </summary>
        public event EventHandler<AgentDestinationReachedEventArgs> OnAgentDestinationReached;

        /// <summary>
        /// Event raised when an agent encounters an obstacle;
        /// </summary>
        public event EventHandler<AgentObstacleEncounteredEventArgs> OnAgentObstacleEncountered;

        /// <summary>
        /// Event raised when the pathfinding grid is updated;
        /// </summary>
        public event EventHandler<PathfindingGridUpdatedEventArgs> OnPathfindingGridUpdated;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of the AINavigation class;
        /// </summary>
        public AINavigation(NavigationConfig config = null)
        {
            _config = config ?? new NavigationConfig();
            _activeAgents = new Dictionary<Guid, NavigationAgent>();
            _dynamicObstacles = new List<NavigationObstacle>();

            // Initialize dependencies;
            _logger = LogManager.GetLogger<AINavigation>();
            _exceptionHandler = GlobalExceptionHandler.Instance;
            _systemManager = SystemManager.Instance;
            _decisionEngine = new DecisionEngine();
            _memorySystem = new MemorySystem();
            _pathfindingModel = new MLModel();

            _logger.LogInformation("AINavigation system initialized");
        }

        /// <summary>
        /// Initializes a new instance with dependency injection support;
        /// </summary>
        public AINavigation(
            ILogger<AINavigation> logger,
            IExceptionHandler exceptionHandler,
            ISystemManager systemManager,
            DecisionEngine decisionEngine,
            IMemorySystem memorySystem,
            IMLModel pathfindingModel,
            NavigationConfig config = null)
        {
            _config = config ?? new NavigationConfig();
            _activeAgents = new Dictionary<Guid, NavigationAgent>();
            _dynamicObstacles = new List<NavigationObstacle>();

            _logger = logger;
            _exceptionHandler = exceptionHandler;
            _systemManager = systemManager;
            _decisionEngine = decisionEngine;
            _memorySystem = memorySystem;
            _pathfindingModel = pathfindingModel;

            _logger.LogInformation("AINavigation system initialized with dependency injection");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the navigation system asynchronously;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("Navigation system is already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing AI Navigation system...");

                // Initialize pathfinding model;
                await _pathfindingModel.InitializeAsync();

                // Load navigation configuration;
                await LoadConfigurationAsync();

                // Initialize decision engine for navigation decisions;
                await _decisionEngine.InitializeAsync();

                // Initialize memory system for navigation memory;
                await _memorySystem.InitializeAsync();

                _isInitialized = true;
                _logger.LogInformation("AI Navigation system initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AI Navigation system");
                await _exceptionHandler.HandleExceptionAsync(ex, "AINavigation.InitializeAsync");
                throw;
            }
        }

        /// <summary>
        /// Builds a navigation mesh from the provided geometry
        /// </summary>
        /// <param name="geometry">The 3D geometry to build navmesh from</param>
        /// <param name="buildOptions">Options for building the navigation mesh</param>
        public async Task<NavigationMesh> BuildNavigationMeshAsync(NavMeshGeometry geometry, NavMeshBuildOptions buildOptions = null)
        {
            ValidateInitialized();

            try
            {
                _logger.LogInformation($"Building navigation mesh from geometry with {geometry.Vertices.Count} vertices...");

                buildOptions ??= new NavMeshBuildOptions();

                // Perform the navigation mesh build;
                var navMesh = await Task.Run(() => BuildNavigationMeshInternal(geometry, buildOptions));

                lock (_navMeshLock)
                {
                    _navMesh = navMesh;
                    _pathfindingGrid = CreatePathfindingGrid(_navMesh);
                }

                // Raise event;
                OnNavigationMeshUpdated?.Invoke(this, new NavigationMeshUpdatedEventArgs;
                {
                    NavMesh = _navMesh,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Navigation mesh built successfully: {_navMesh.Polygons.Count} polygons, {_navMesh.Vertices.Count} vertices");

                return _navMesh;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build navigation mesh");
                await _exceptionHandler.HandleExceptionAsync(ex, "AINavigation.BuildNavigationMeshAsync");
                throw;
            }
        }

        /// <summary>
        /// Creates a navigation agent for pathfinding;
        /// </summary>
        /// <param name="agentConfig">Agent configuration</param>
        /// <returns>The created navigation agent</returns>
        public NavigationAgent CreateAgent(NavigationAgentConfig agentConfig = null)
        {
            ValidateInitialized();

            agentConfig ??= new NavigationAgentConfig();

            var agent = new NavigationAgent(agentConfig)
            {
                Id = Guid.NewGuid(),
                Position = Vector3.Zero,
                Velocity = Vector3.Zero,
                CurrentSpeed = agentConfig.MaxSpeed,
                Status = AgentStatus.Idle;
            };

            lock (_agentsLock)
            {
                _activeAgents[agent.Id] = agent;
            }

            _logger.LogDebug($"Created navigation agent: {agent.Id}");

            return agent;
        }

        /// <summary>
        /// Finds a path from start to destination for the specified agent;
        /// </summary>
        /// <param name="agentId">The agent ID</param>
        /// <param name="destination">Destination position</param>
        /// <param name="pathOptions">Pathfinding options</param>
        /// <returns>The calculated path</returns>
        public async Task<NavigationPath> FindPathAsync(Guid agentId, Vector3 destination, PathOptions pathOptions = null)
        {
            ValidateInitialized();
            ValidateNavMeshExists();

            var agent = GetAgent(agentId);
            pathOptions ??= new PathOptions();

            try
            {
                _logger.LogDebug($"Finding path for agent {agentId} to {destination}");

                // Get agent position;
                var start = agent.Position;

                // Find nearest points on navmesh;
                var startPoint = FindNearestPointOnNavMesh(start);
                var endPoint = FindNearestPointOnNavMesh(destination);

                if (!startPoint.HasValue || !endPoint.HasValue)
                {
                    throw new InvalidOperationException("Start or destination point is not on navigation mesh");
                }

                // Calculate path using appropriate algorithm;
                NavigationPath path;
                if (pathOptions.UseAdvancedPathfinding && _pathfindingModel.IsTrained)
                {
                    path = await CalculatePathWithMLAsync(startPoint.Value, endPoint.Value, agent, pathOptions);
                }
                else;
                {
                    path = CalculatePathWithAStar(startPoint.Value, endPoint.Value, agent, pathOptions);
                }

                // Optimize path if requested;
                if (pathOptions.OptimizePath)
                {
                    path = OptimizePath(path, agent);
                }

                // Store path in agent;
                agent.CurrentPath = path;
                agent.Status = AgentStatus.Moving;

                // Store in memory for learning;
                await StorePathInMemoryAsync(agentId, path);

                _logger.LogDebug($"Path found for agent {agentId}: {path.Waypoints.Count} waypoints, distance: {path.TotalDistance:F2}");

                return path;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to find path for agent {agentId}");
                await _exceptionHandler.HandleExceptionAsync(ex, "AINavigation.FindPathAsync");
                throw;
            }
        }

        /// <summary>
        /// Updates agent movement along its current path;
        /// </summary>
        /// <param name="agentId">The agent ID</param>
        /// <param name="deltaTime">Time since last update</param>
        public async Task UpdateAgentMovementAsync(Guid agentId, float deltaTime)
        {
            ValidateInitialized();

            var agent = GetAgent(agentId);

            if (agent.Status != AgentStatus.Moving || agent.CurrentPath == null)
                return;

            try
            {
                // Get current waypoint;
                var currentWaypoint = agent.CurrentPath.GetCurrentWaypoint();
                if (!currentWaypoint.HasValue)
                {
                    // Path completed;
                    agent.Status = AgentStatus.Idle;
                    OnAgentDestinationReached?.Invoke(this, new AgentDestinationReachedEventArgs;
                    {
                        AgentId = agentId,
                        Destination = agent.CurrentPath.Destination,
                        Timestamp = DateTime.UtcNow;
                    });
                    return;
                }

                // Calculate movement towards waypoint;
                var direction = Vector3.Normalize(currentWaypoint.Value - agent.Position);
                var distanceToWaypoint = Vector3.Distance(agent.Position, currentWaypoint.Value);

                // Check for obstacles;
                var obstacle = await CheckForObstaclesAsync(agent, direction);
                if (obstacle != null)
                {
                    await HandleObstacleAsync(agent, obstacle);
                    return;
                }

                // Move agent;
                var movement = direction * agent.CurrentSpeed * deltaTime;

                // If close to waypoint, snap to it and move to next;
                if (distanceToWaypoint < agent.Config.WaypointThreshold)
                {
                    agent.Position = currentWaypoint.Value;
                    agent.CurrentPath.AdvanceToNextWaypoint();
                }
                else;
                {
                    agent.Position += movement;
                }

                // Update velocity;
                agent.Velocity = direction * agent.CurrentSpeed;

                // Update rotation to face movement direction;
                if (direction.LengthSquared() > 0.01f)
                {
                    agent.Rotation = Quaternion.CreateFromYawPitchRoll(
                        (float)Math.Atan2(direction.X, direction.Z),
                        0,
                        0;
                    );
                }

                // Check if destination reached;
                if (agent.CurrentPath.IsComplete)
                {
                    agent.Status = AgentStatus.Idle;
                    OnAgentDestinationReached?.Invoke(this, new AgentDestinationReachedEventArgs;
                    {
                        AgentId = agentId,
                        Destination = agent.CurrentPath.Destination,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update movement for agent {agentId}");
                await _exceptionHandler.HandleExceptionAsync(ex, "AINavigation.UpdateAgentMovementAsync");
            }
        }

        /// <summary>
        /// Adds a dynamic obstacle to the navigation system;
        /// </summary>
        /// <param name="obstacle">The obstacle to add</param>
        public void AddDynamicObstacle(NavigationObstacle obstacle)
        {
            ValidateInitialized();

            lock (_dynamicObstacles)
            {
                _dynamicObstacles.Add(obstacle);
            }

            // Update pathfinding grid with obstacle;
            UpdatePathfindingGridWithObstacle(obstacle);

            _logger.LogDebug($"Added dynamic obstacle: {obstacle.Id}, Type: {obstacle.Type}");
        }

        /// <summary>
        /// Removes a dynamic obstacle from the navigation system;
        /// </summary>
        /// <param name="obstacleId">The obstacle ID</param>
        public void RemoveDynamicObstacle(Guid obstacleId)
        {
            ValidateInitialized();

            lock (_dynamicObstacles)
            {
                var obstacle = _dynamicObstacles.FirstOrDefault(o => o.Id == obstacleId);
                if (obstacle != null)
                {
                    _dynamicObstacles.Remove(obstacle);
                    UpdatePathfindingGridWithoutObstacle(obstacle);
                    _logger.LogDebug($"Removed dynamic obstacle: {obstacleId}");
                }
            }
        }

        /// <summary>
        /// Gets all agents within a specified range of a position;
        /// </summary>
        /// <param name="position">Center position</param>
        /// <param name="range">Search range</param>
        /// <returns>List of agents within range</returns>
        public List<NavigationAgent> GetAgentsInRange(Vector3 position, float range)
        {
            ValidateInitialized();

            var agentsInRange = new List<NavigationAgent>();

            lock (_agentsLock)
            {
                foreach (var agent in _activeAgents.Values)
                {
                    if (Vector3.Distance(position, agent.Position) <= range)
                    {
                        agentsInRange.Add(agent);
                    }
                }
            }

            return agentsInRange;
        }

        /// <summary>
        /// Calculates the nearest point on the navigation mesh;
        /// </summary>
        /// <param name="position">World position</param>
        /// <returns>Nearest point on navmesh, or null if not found</returns>
        public Vector3? FindNearestPointOnNavMesh(Vector3 position)
        {
            if (_navMesh == null)
                return null;

            return _navMesh.FindNearestPoint(position);
        }

        /// <summary>
        /// Checks if a position is on the navigation mesh;
        /// </summary>
        /// <param name="position">Position to check</param>
        /// <param name="tolerance">Tolerance distance</param>
        /// <returns>True if position is on navmesh</returns>
        public bool IsPositionOnNavMesh(Vector3 position, float tolerance = 0.1f)
        {
            if (_navMesh == null)
                return false;

            var nearestPoint = _navMesh.FindNearestPoint(position);
            if (!nearestPoint.HasValue)
                return false;

            return Vector3.Distance(position, nearestPoint.Value) <= tolerance;
        }

        /// <summary>
        /// Gets the navigation mesh polygon at the specified position;
        /// </summary>
        /// <param name="position">World position</param>
        /// <returns>Navmesh polygon, or null if not found</returns>
        public NavMeshPolygon GetPolygonAtPosition(Vector3 position)
        {
            if (_navMesh == null)
                return null;

            return _navMesh.GetPolygonAtPosition(position);
        }

        /// <summary>
        /// Trains the pathfinding ML model with navigation data;
        /// </summary>
        /// <param name="trainingData">Navigation training data</param>
        /// <param name="options">Training options</param>
        public async Task<TrainingResult> TrainPathfindingModelAsync(NavigationTrainingData trainingData, TrainingOptions options)
        {
            ValidateInitialized();

            try
            {
                _logger.LogInformation("Training pathfinding ML model...");

                var result = await _pathfindingModel.TrainAsync(trainingData, options);

                _logger.LogInformation($"Pathfinding model trained: Accuracy = {result.Accuracy:P2}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to train pathfinding model");
                await _exceptionHandler.HandleExceptionAsync(ex, "AINavigation.TrainPathfindingModelAsync");
                throw;
            }
        }

        /// <summary>
        /// Updates the navigation configuration;
        /// </summary>
        /// <param name="config">New configuration</param>
        public void UpdateConfiguration(NavigationConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger.LogInformation("Navigation configuration updated");
        }

        /// <summary>
        /// Stops all active agents;
        /// </summary>
        public void StopAllAgents()
        {
            lock (_agentsLock)
            {
                foreach (var agent in _activeAgents.Values)
                {
                    agent.Status = AgentStatus.Idle;
                    agent.CurrentPath = null;
                }
            }

            _logger.LogInformation("All navigation agents stopped");
        }

        /// <summary>
        /// Removes an agent from the navigation system;
        /// </summary>
        /// <param name="agentId">Agent ID to remove</param>
        public void RemoveAgent(Guid agentId)
        {
            lock (_agentsLock)
            {
                if (_activeAgents.ContainsKey(agentId))
                {
                    _activeAgents.Remove(agentId);
                    _logger.LogDebug($"Removed navigation agent: {agentId}");
                }
            }
        }

        /// <summary>
        /// Gets navigation statistics;
        /// </summary>
        /// <returns>Navigation statistics</returns>
        public NavigationStatistics GetStatistics()
        {
            return new NavigationStatistics;
            {
                TotalAgents = ActiveAgentCount,
                ActiveAgents = _activeAgents.Values.Count(a => a.Status == AgentStatus.Moving),
                DynamicObstacles = DynamicObstacleCount,
                NavMeshPolygons = _navMesh?.Polygons.Count ?? 0,
                NavMeshVertices = _navMesh?.Vertices.Count ?? 0,
                PathfindingGridCells = _pathfindingGrid?.Cells.Count ?? 0,
                LastPathfindingTime = DateTime.UtcNow;
            };
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;

        #region Private Methods;

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("AINavigation system is not initialized. Call InitializeAsync first.");
        }

        private void ValidateNavMeshExists()
        {
            if (_navMesh == null)
                throw new InvalidOperationException("Navigation mesh does not exist. Build a navigation mesh first.");
        }

        private NavigationAgent GetAgent(Guid agentId)
        {
            lock (_agentsLock)
            {
                if (!_activeAgents.TryGetValue(agentId, out var agent))
                    throw new KeyNotFoundException($"Navigation agent with ID {agentId} not found");

                return agent;
            }
        }

        private async Task LoadConfigurationAsync()
        {
            // Load configuration from file or database;
            // This is a simplified implementation;
            await Task.Delay(100); // Simulate async loading;

            _logger.LogDebug("Navigation configuration loaded");
        }

        private NavigationMesh BuildNavigationMeshInternal(NavMeshGeometry geometry, NavMeshBuildOptions options)
        {
            // This is a simplified implementation of navmesh building;
            // In a real implementation, this would use a library like Recast/Detour;

            var navMesh = new NavigationMesh;
            {
                Id = Guid.NewGuid(),
                Bounds = geometry.Bounds,
                BuildTimestamp = DateTime.UtcNow,
                BuildOptions = options;
            };

            // Triangulate the geometry
            var triangles = TriangulateGeometry(geometry.Vertices, geometry.Indices);

            // Create navigation polygons from triangles;
            navMesh.Polygons = CreateNavMeshPolygons(triangles, options);

            // Create vertices list;
            navMesh.Vertices = geometry.Vertices.ToList();

            // Build spatial partitioning structure;
            navMesh.SpatialPartition = BuildSpatialPartition(navMesh.Polygons);

            return navMesh;
        }

        private List<NavMeshTriangle> TriangulateGeometry(List<Vector3> vertices, List<int> indices)
        {
            var triangles = new List<NavMeshTriangle>();

            for (int i = 0; i < indices.Count; i += 3)
            {
                var triangle = new NavMeshTriangle;
                {
                    Vertex1 = vertices[indices[i]],
                    Vertex2 = vertices[indices[i + 1]],
                    Vertex3 = vertices[indices[i + 2]],
                    Normal = CalculateTriangleNormal(
                        vertices[indices[i]],
                        vertices[indices[i + 1]],
                        vertices[indices[i + 2]]
                    )
                };

                triangles.Add(triangle);
            }

            return triangles;
        }

        private Vector3 CalculateTriangleNormal(Vector3 v1, Vector3 v2, Vector3 v3)
        {
            var edge1 = v2 - v1;
            var edge2 = v3 - v1;
            return Vector3.Normalize(Vector3.Cross(edge1, edge2));
        }

        private List<NavMeshPolygon> CreateNavMeshPolygons(List<NavMeshTriangle> triangles, NavMeshBuildOptions options)
        {
            var polygons = new List<NavMeshPolygon>();
            var polygonId = 0;

            foreach (var triangle in triangles)
            {
                // Filter by slope if specified;
                if (options.MaxSlopeAngle > 0)
                {
                    var angle = Math.Acos(Vector3.Dot(triangle.Normal, Vector3.UnitY)) * (180.0 / Math.PI);
                    if (angle > options.MaxSlopeAngle)
                        continue;
                }

                var polygon = new NavMeshPolygon;
                {
                    Id = polygonId++,
                    Vertices = new List<Vector3> { triangle.Vertex1, triangle.Vertex2, triangle.Vertex3 },
                    Normal = triangle.Normal,
                    Center = (triangle.Vertex1 + triangle.Vertex2 + triangle.Vertex3) / 3.0f,
                    Area = CalculateTriangleArea(triangle.Vertex1, triangle.Vertex2, triangle.Vertex3),
                    Walkable = IsTriangleWalkable(triangle, options)
                };

                polygons.Add(polygon);
            }

            return polygons;
        }

        private float CalculateTriangleArea(Vector3 v1, Vector3 v2, Vector3 v3)
        {
            var edge1 = v2 - v1;
            var edge2 = v3 - v1;
            return Vector3.Cross(edge1, edge2).Length() * 0.5f;
        }

        private bool IsTriangleWalkable(NavMeshTriangle triangle, NavMeshBuildOptions options)
        {
            // Check slope;
            var slopeAngle = Math.Acos(Vector3.Dot(triangle.Normal, Vector3.UnitY)) * (180.0 / Math.PI);
            if (slopeAngle > options.MaxSlopeAngle)
                return false;

            // Check minimum area;
            var area = CalculateTriangleArea(triangle.Vertex1, triangle.Vertex2, triangle.Vertex3);
            if (area < options.MinPolygonArea)
                return false;

            return true;
        }

        private SpatialPartition BuildSpatialPartition(List<NavMeshPolygon> polygons)
        {
            // Build a simple grid-based spatial partition;
            // In a real implementation, this would be more sophisticated (quadtree, octree, etc.)

            var bounds = CalculateBounds(polygons);
            var partition = new GridSpatialPartition(bounds, 10.0f); // 10m grid cells;

            foreach (var polygon in polygons)
            {
                partition.Add(polygon);
            }

            return partition;
        }

        private BoundingBox CalculateBounds(List<NavMeshPolygon> polygons)
        {
            if (polygons.Count == 0)
                return new BoundingBox(Vector3.Zero, Vector3.Zero);

            var min = polygons[0].Center;
            var max = polygons[0].Center;

            foreach (var polygon in polygons)
            {
                foreach (var vertex in polygon.Vertices)
                {
                    min = Vector3.Min(min, vertex);
                    max = Vector3.Max(max, vertex);
                }
            }

            return new BoundingBox(min, max);
        }

        private PathfindingGrid CreatePathfindingGrid(NavigationMesh navMesh)
        {
            var grid = new PathfindingGrid;
            {
                Id = Guid.NewGuid(),
                CellSize = _config.CellSize,
                CellHeight = _config.CellHeight,
                Bounds = navMesh.Bounds;
            };

            // Create grid cells;
            var width = (int)Math.Ceiling((navMesh.Bounds.Max.X - navMesh.Bounds.Min.X) / _config.CellSize);
            var depth = (int)Math.Ceiling((navMesh.Bounds.Max.Z - navMesh.Bounds.Min.Z) / _config.CellSize);

            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    var cell = new PathfindingCell;
                    {
                        Id = Guid.NewGuid(),
                        GridPosition = new Vector2(x, z),
                        WorldPosition = new Vector3(
                            navMesh.Bounds.Min.X + x * _config.CellSize + _config.CellSize / 2,
                            0,
                            navMesh.Bounds.Min.Z + z * _config.CellSize + _config.CellSize / 2;
                        ),
                        IsWalkable = true,
                        Cost = 1.0f;
                    };

                    // Check if cell is on walkable polygon;
                    cell.WorldPosition = new Vector3(
                        cell.WorldPosition.X,
                        GetHeightAtPosition(navMesh, cell.WorldPosition),
                        cell.WorldPosition.Z;
                    );

                    cell.IsWalkable = IsPositionOnNavMesh(cell.WorldPosition);
                    grid.Cells.Add(cell);
                }
            }

            // Connect cells;
            ConnectGridCells(grid);

            return grid;
        }

        private float GetHeightAtPosition(NavigationMesh navMesh, Vector3 position)
        {
            var polygon = navMesh.GetPolygonAtPosition(new Vector3(position.X, navMesh.Bounds.Max.Y, position.Z));
            if (polygon == null)
                return 0.0f;

            // Simple height calculation - in reality, this would do proper raycasting;
            return polygon.Center.Y;
        }

        private void ConnectGridCells(PathfindingGrid grid)
        {
            foreach (var cell in grid.Cells)
            {
                cell.Neighbors.Clear();

                // Find neighboring cells (8-directional)
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;

                        var neighborPos = new Vector2(
                            cell.GridPosition.X + dx,
                            cell.GridPosition.Y + dz;
                        );

                        var neighbor = grid.Cells.FirstOrDefault(c =>
                            c.GridPosition.X == neighborPos.X &&
                            c.GridPosition.Y == neighborPos.Y);

                        if (neighbor != null && neighbor.IsWalkable)
                        {
                            // Calculate cost based on distance and height difference;
                            var distanceCost = (dx == 0 || dz == 0) ? 1.0f : 1.414f; // sqrt(2) for diagonals;
                            var heightDiff = Math.Abs(cell.WorldPosition.Y - neighbor.WorldPosition.Y);
                            var heightCost = heightDiff > _config.MaxStepHeight ? float.MaxValue : heightDiff * 2.0f;

                            cell.Neighbors.Add(new CellConnection;
                            {
                                Cell = neighbor,
                                Cost = distanceCost + heightCost;
                            });
                        }
                    }
                }
            }
        }

        private NavigationPath CalculatePathWithAStar(Vector3 start, Vector3 end, NavigationAgent agent, PathOptions options)
        {
            // Convert world positions to grid cells;
            var startCell = FindCellAtPosition(start);
            var endCell = FindCellAtPosition(end);

            if (startCell == null || endCell == null || !startCell.IsWalkable || !endCell.IsWalkable)
                throw new InvalidOperationException("Start or end position is not walkable");

            // A* pathfinding algorithm;
            var openSet = new PriorityQueue<PathfindingCell, float>();
            var cameFrom = new Dictionary<PathfindingCell, PathfindingCell>();
            var gScore = new Dictionary<PathfindingCell, float>();
            var fScore = new Dictionary<PathfindingCell, float>();

            gScore[startCell] = 0;
            fScore[startCell] = Heuristic(startCell, endCell);
            openSet.Enqueue(startCell, fScore[startCell]);

            while (openSet.Count > 0)
            {
                var current = openSet.Dequeue();

                if (current == endCell)
                {
                    // Path found;
                    return ReconstructPath(cameFrom, current, start, end, agent);
                }

                foreach (var connection in current.Neighbors)
                {
                    var neighbor = connection.Cell;
                    var tentativeGScore = gScore[current] + connection.Cost;

                    if (!gScore.ContainsKey(neighbor) || tentativeGScore < gScore[neighbor])
                    {
                        cameFrom[neighbor] = current;
                        gScore[neighbor] = tentativeGScore;
                        fScore[neighbor] = tentativeGScore + Heuristic(neighbor, endCell);

                        if (!openSet.UnorderedItems.Any(item => item.Element == neighbor))
                        {
                            openSet.Enqueue(neighbor, fScore[neighbor]);
                        }
                    }
                }

                // Limit search for performance;
                if (openSet.Count > _config.MaxSearchNodes)
                    break;
            }

            throw new InvalidOperationException("Path not found");
        }

        private async Task<NavigationPath> CalculatePathWithMLAsync(Vector3 start, Vector3 end, NavigationAgent agent, PathOptions options)
        {
            // Prepare input for ML model;
            var input = new NavigationInput;
            {
                StartPosition = start,
                EndPosition = end,
                AgentRadius = agent.Config.Radius,
                AgentHeight = agent.Config.Height,
                TerrainType = GetTerrainTypeAtPosition(start),
                TimeOfDay = DateTime.UtcNow.Hour / 24.0f,
                ObstacleDensity = CalculateObstacleDensity(start, end)
            };

            // Get prediction from ML model;
            var prediction = await _pathfindingModel.PredictAsync(input);

            // Convert prediction to path;
            var path = ConvertPredictionToPath(prediction, start, end, agent);

            return path;
        }

        private NavigationPath ReconstructPath(
            Dictionary<PathfindingCell, PathfindingCell> cameFrom,
            PathfindingCell current,
            Vector3 start,
            Vector3 end,
            NavigationAgent agent)
        {
            var path = new NavigationPath;
            {
                Id = Guid.NewGuid(),
                Start = start,
                Destination = end,
                CreationTime = DateTime.UtcNow;
            };

            // Reconstruct path in reverse;
            var cellPath = new List<PathfindingCell> { current };
            while (cameFrom.ContainsKey(current))
            {
                current = cameFrom[current];
                cellPath.Insert(0, current);
            }

            // Convert cell path to waypoints;
            var waypoints = new List<Vector3> { start };
            foreach (var cell in cellPath.Skip(1)) // Skip start cell;
            {
                waypoints.Add(cell.WorldPosition);
            }
            waypoints.Add(end);

            // Simplify path;
            waypoints = SimplifyPath(waypoints, agent.Config.Radius);

            path.Waypoints = waypoints;
            path.TotalDistance = CalculatePathDistance(waypoints);

            return path;
        }

        private List<Vector3> SimplifyPath(List<Vector3> waypoints, float agentRadius)
        {
            if (waypoints.Count <= 2)
                return waypoints;

            var simplified = new List<Vector3> { waypoints[0] };
            var lastAdded = 0;

            for (int i = 1; i < waypoints.Count - 1; i++)
            {
                // Check if we can skip this waypoint;
                var canSee = CanSeePoint(simplified.Last(), waypoints[i + 1], agentRadius);
                if (!canSee)
                {
                    simplified.Add(waypoints[i]);
                    lastAdded = i;
                }
            }

            simplified.Add(waypoints.Last());
            return simplified;
        }

        private bool CanSeePoint(Vector3 from, Vector3 to, float agentRadius)
        {
            // Simple line-of-sight check;
            // In reality, this would check against the navmesh;
            var direction = to - from;
            var distance = direction.Length();
            direction = Vector3.Normalize(direction);

            // Check for obstacles along the line;
            for (float t = 0; t < distance; t += 0.5f)
            {
                var point = from + direction * t;
                if (!IsPositionOnNavMesh(point, agentRadius * 2))
                    return false;
            }

            return true;
        }

        private float CalculatePathDistance(List<Vector3> waypoints)
        {
            float distance = 0;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                distance += Vector3.Distance(waypoints[i], waypoints[i + 1]);
            }
            return distance;
        }

        private float Heuristic(PathfindingCell a, PathfindingCell b)
        {
            // Euclidean distance heuristic;
            var dx = a.GridPosition.X - b.GridPosition.X;
            var dy = a.GridPosition.Y - b.GridPosition.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private PathfindingCell FindCellAtPosition(Vector3 position)
        {
            if (_pathfindingGrid == null)
                return null;

            var gridPos = new Vector2(
                (position.X - _pathfindingGrid.Bounds.Min.X) / _config.CellSize,
                (position.Z - _pathfindingGrid.Bounds.Min.Z) / _config.CellSize;
            );

            var x = (int)Math.Floor(gridPos.X);
            var z = (int)Math.Floor(gridPos.Y);

            return _pathfindingGrid.Cells.FirstOrDefault(c =>
                c.GridPosition.X == x && c.GridPosition.Y == z);
        }

        private async Task<NavigationObstacle> CheckForObstaclesAsync(NavigationAgent agent, Vector3 direction)
        {
            // Raycast forward to check for obstacles;
            var rayLength = agent.Config.Radius * 2.0f;
            var rayEnd = agent.Position + direction * rayLength;

            // Check dynamic obstacles;
            lock (_dynamicObstacles)
            {
                foreach (var obstacle in _dynamicObstacles)
                {
                    if (obstacle.IsActive && Vector3.Distance(agent.Position, obstacle.Position) < rayLength + obstacle.Radius)
                    {
                        // Check if obstacle is in movement direction;
                        var toObstacle = Vector3.Normalize(obstacle.Position - agent.Position);
                        var dot = Vector3.Dot(direction, toObstacle);

                        if (dot > 0.7f) // Obstacle is roughly in front;
                        {
                            return obstacle;
                        }
                    }
                }
            }

            // Check static obstacles (navmesh boundaries)
            if (!IsPositionOnNavMesh(rayEnd, agent.Config.Radius))
            {
                return new NavigationObstacle;
                {
                    Id = Guid.NewGuid(),
                    Type = ObstacleType.Static,
                    Position = rayEnd,
                    Radius = agent.Config.Radius * 2.0f;
                };
            }

            return null;
        }

        private async Task HandleObstacleAsync(NavigationAgent agent, NavigationObstacle obstacle)
        {
            _logger.LogDebug($"Agent {agent.Id} encountered obstacle {obstacle.Id}");

            // Raise event;
            OnAgentObstacleEncountered?.Invoke(this, new AgentObstacleEncounteredEventArgs;
            {
                AgentId = agent.Id,
                ObstacleId = obstacle.Id,
                ObstacleType = obstacle.Type,
                Position = obstacle.Position,
                Timestamp = DateTime.UtcNow;
            });

            // Make decision on how to handle obstacle;
            var decision = await _decisionEngine.MakeDecisionAsync(new DecisionRequest;
            {
                DecisionType = "ObstacleAvoidance",
                Options = new List<object> { "GoLeft", "GoRight", "Stop", "Jump" },
                Criteria = new Dictionary<string, object>
                {
                    ["agent"] = agent,
                    ["obstacle"] = obstacle,
                    ["navMesh"] = _navMesh;
                }
            });

            if (decision.Success && decision.SelectedOption is string action)
            {
                switch (action)
                {
                    case "GoLeft":
                    case "GoRight":
                        // Calculate avoidance path;
                        var avoidanceDirection = action == "GoLeft" ?
                            Vector3.Cross(agent.Velocity, Vector3.UnitY) :
                            Vector3.Cross(Vector3.UnitY, agent.Velocity);

                        agent.Position += Vector3.Normalize(avoidanceDirection) * agent.Config.Radius * 2.0f;
                        break;

                    case "Stop":
                        agent.Status = AgentStatus.Idle;
                        agent.Velocity = Vector3.Zero;
                        break;

                    case "Jump":
                        // Simple jump - in reality, this would check if jump is possible;
                        if (obstacle.Type == ObstacleType.Static && obstacle.Radius < 1.0f)
                        {
                            agent.Position += Vector3.UnitY * 0.5f;
                        }
                        break;
                }
            }

            // Store obstacle encounter in memory;
            await _memorySystem.StoreAsync(new ObstacleEncounterMemory;
            {
                AgentId = agent.Id,
                ObstacleId = obstacle.Id,
                Position = obstacle.Position,
                Time = DateTime.UtcNow,
                ActionTaken = decision.SelectedOption?.ToString()
            });
        }

        private NavigationPath OptimizePath(NavigationPath path, NavigationAgent agent)
        {
            // Apply various optimization techniques;
            var optimizedWaypoints = new List<Vector3>();

            // 1. Remove redundant waypoints;
            optimizedWaypoints = RemoveRedundantWaypoints(path.Waypoints, agent.Config.Radius);

            // 2. Smooth path;
            optimizedWaypoints = SmoothPath(optimizedWaypoints, agent.Config.Radius);

            // 3. Add intermediate points for better movement;
            optimizedWaypoints = AddIntermediatePoints(optimizedWaypoints, agent.Config.MaxSpeed);

            return new NavigationPath;
            {
                Id = Guid.NewGuid(),
                Start = path.Start,
                Destination = path.Destination,
                Waypoints = optimizedWaypoints,
                TotalDistance = CalculatePathDistance(optimizedWaypoints),
                CreationTime = DateTime.UtcNow;
            };
        }

        private List<Vector3> RemoveRedundantWaypoints(List<Vector3> waypoints, float agentRadius)
        {
            // Implementation of path simplification (Ramer-Douglas-Peucker algorithm)
            if (waypoints.Count < 3)
                return waypoints;

            var simplified = new List<Vector3>();
            SimplifyPathSegment(waypoints, 0, waypoints.Count - 1, agentRadius * 0.5f, simplified);
            simplified.Add(waypoints.Last());

            return simplified;
        }

        private void SimplifyPathSegment(List<Vector3> points, int start, int end, float epsilon, List<Vector3> result)
        {
            // Find the point with the maximum distance;
            float maxDistance = 0;
            int maxIndex = 0;

            for (int i = start + 1; i < end; i++)
            {
                float distance = PointToLineDistance(points[i], points[start], points[end]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    maxIndex = i;
                }
            }

            // If max distance is greater than epsilon, recursively simplify;
            if (maxDistance > epsilon)
            {
                SimplifyPathSegment(points, start, maxIndex, epsilon, result);
                SimplifyPathSegment(points, maxIndex, end, epsilon, result);
            }
            else;
            {
                result.Add(points[start]);
            }
        }

        private float PointToLineDistance(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
        {
            var line = lineEnd - lineStart;
            var lineLength = line.Length();
            line = Vector3.Normalize(line);

            var pointVector = point - lineStart;
            var t = Vector3.Dot(pointVector, line);
            t = Math.Clamp(t, 0, lineLength);

            var projection = lineStart + line * t;
            return Vector3.Distance(point, projection);
        }

        private List<Vector3> SmoothPath(List<Vector3> waypoints, float agentRadius)
        {
            if (waypoints.Count < 3)
                return waypoints;

            var smoothed = new List<Vector3> { waypoints[0] };

            for (int i = 1; i < waypoints.Count - 1; i++)
            {
                // Apply simple averaging filter;
                var smoothedPoint = (waypoints[i - 1] + waypoints[i] + waypoints[i + 1]) / 3.0f;

                // Ensure point is on navmesh;
                var navmeshPoint = FindNearestPointOnNavMesh(smoothedPoint);
                if (navmeshPoint.HasValue)
                {
                    smoothed.Add(navmeshPoint.Value);
                }
                else;
                {
                    smoothed.Add(waypoints[i]);
                }
            }

            smoothed.Add(waypoints.Last());
            return smoothed;
        }

        private List<Vector3> AddIntermediatePoints(List<Vector3> waypoints, float maxSpeed)
        {
            var result = new List<Vector3>();
            var stepDistance = maxSpeed * 0.5f; // Add point every half-second of movement;

            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                result.Add(waypoints[i]);

                var segment = waypoints[i + 1] - waypoints[i];
                var segmentLength = segment.Length();
                var direction = Vector3.Normalize(segment);

                // Add intermediate points if segment is long;
                if (segmentLength > stepDistance * 2)
                {
                    var numPoints = (int)Math.Floor(segmentLength / stepDistance);
                    for (int j = 1; j < numPoints; j++)
                    {
                        var intermediatePoint = waypoints[i] + direction * (stepDistance * j);
                        result.Add(intermediatePoint);
                    }
                }
            }

            result.Add(waypoints.Last());
            return result;
        }

        private async Task StorePathInMemoryAsync(Guid agentId, NavigationPath path)
        {
            var pathMemory = new PathMemory;
            {
                AgentId = agentId,
                PathId = path.Id,
                Start = path.Start,
                Destination = path.Destination,
                WaypointCount = path.Waypoints.Count,
                Distance = path.TotalDistance,
                CreationTime = DateTime.UtcNow;
            };

            await _memorySystem.StoreAsync(pathMemory);
        }

        private void UpdatePathfindingGridWithObstacle(NavigationObstacle obstacle)
        {
            if (_pathfindingGrid == null)
                return;

            // Mark cells occupied by obstacle as unwalkable;
            foreach (var cell in _pathfindingGrid.Cells)
            {
                var distance = Vector3.Distance(
                    new Vector3(cell.WorldPosition.X, 0, cell.WorldPosition.Z),
                    new Vector3(obstacle.Position.X, 0, obstacle.Position.Z)
                );

                if (distance < obstacle.Radius + _config.CellSize / 2)
                {
                    cell.IsWalkable = false;
                    cell.Cost = float.MaxValue;
                }
            }

            OnPathfindingGridUpdated?.Invoke(this, new PathfindingGridUpdatedEventArgs;
            {
                Grid = _pathfindingGrid,
                UpdateType = "ObstacleAdded",
                Timestamp = DateTime.UtcNow;
            });
        }

        private void UpdatePathfindingGridWithoutObstacle(NavigationObstacle obstacle)
        {
            if (_pathfindingGrid == null)
                return;

            // Recalculate walkability for cells previously blocked by obstacle;
            foreach (var cell in _pathfindingGrid.Cells)
            {
                var distance = Vector3.Distance(
                    new Vector3(cell.WorldPosition.X, 0, cell.WorldPosition.Z),
                    new Vector3(obstacle.Position.X, 0, obstacle.Position.Z)
                );

                if (distance < obstacle.Radius + _config.CellSize / 2)
                {
                    // Check if cell should be walkable now;
                    cell.IsWalkable = IsPositionOnNavMesh(cell.WorldPosition);
                    cell.Cost = 1.0f;
                }
            }

            OnPathfindingGridUpdated?.Invoke(this, new PathfindingGridUpdatedEventArgs;
            {
                Grid = _pathfindingGrid,
                UpdateType = "ObstacleRemoved",
                Timestamp = DateTime.UtcNow;
            });
        }

        private string GetTerrainTypeAtPosition(Vector3 position)
        {
            // Simplified terrain type detection;
            var polygon = GetPolygonAtPosition(position);
            if (polygon == null)
                return "Unknown";

            // Determine terrain type based on polygon normal and position;
            var slope = Math.Acos(Vector3.Dot(polygon.Normal, Vector3.UnitY)) * (180.0 / Math.PI);

            if (slope < 10) return "Flat";
            if (slope < 30) return "Slope";
            if (slope < 45) return "Steep";
            return "Cliff";
        }

        private float CalculateObstacleDensity(Vector3 start, Vector3 end)
        {
            if (_dynamicObstacles.Count == 0)
                return 0.0f;

            var bounds = new BoundingBox(
                Vector3.Min(start, end),
                Vector3.Max(start, end)
            );

            var obstaclesInArea = _dynamicObstacles.Count(o =>
                o.IsActive && bounds.Contains(o.Position) == ContainmentType.Contains);

            var area = (bounds.Max.X - bounds.Min.X) * (bounds.Max.Z - bounds.Min.Z);
            return obstaclesInArea / Math.Max(area, 1.0f);
        }

        private NavigationPath ConvertPredictionToPath(object prediction, Vector3 start, Vector3 end, NavigationAgent agent)
        {
            // Convert ML model prediction to navigation path;
            // This is a simplified implementation;

            var waypoints = new List<Vector3> { start };

            if (prediction is List<Vector3> predictedWaypoints)
            {
                waypoints.AddRange(predictedWaypoints);
            }
            else;
            {
                // Fallback to straight line with intermediate points;
                var direction = end - start;
                var distance = direction.Length();
                direction = Vector3.Normalize(direction);

                var numPoints = (int)Math.Ceiling(distance / (agent.Config.MaxSpeed * 2.0f));
                for (int i = 1; i < numPoints; i++)
                {
                    var t = (float)i / numPoints;
                    waypoints.Add(start + direction * distance * t);
                }
            }

            waypoints.Add(end);

            return new NavigationPath;
            {
                Id = Guid.NewGuid(),
                Start = start,
                Destination = end,
                Waypoints = waypoints,
                TotalDistance = CalculatePathDistance(waypoints),
                CreationTime = DateTime.UtcNow;
            };
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    StopAllAgents();

                    lock (_agentsLock)
                    {
                        _activeAgents.Clear();
                    }

                    lock (_dynamicObstacles)
                    {
                        _dynamicObstacles.Clear();
                    }

                    (_pathfindingModel as IDisposable)?.Dispose();
                    (_decisionEngine as IDisposable)?.Dispose();
                    (_memorySystem as IDisposable)?.Dispose();
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Nested Classes;

        /// <summary>
        /// Configuration for the navigation system;
        /// </summary>
        public class NavigationConfig;
        {
            public float AgentRadius { get; set; } = DEFAULT_AGENT_RADIUS;
            public float AgentHeight { get; set; } = DEFAULT_AGENT_HEIGHT;
            public float MaxSlope { get; set; } = DEFAULT_MAX_SLOPE;
            public float StepHeight { get; set; } = DEFAULT_STEP_HEIGHT;
            public int MaxSearchNodes { get; set; } = DEFAULT_MAX_NODES;
            public float CellSize { get; set; } = DEFAULT_CELL_SIZE;
            public float CellHeight { get; set; } = DEFAULT_CELL_HEIGHT;
            public bool EnableMLPathfinding { get; set; } = true;
            public bool EnableDynamicObstacles { get; set; } = true;
            public float ObstacleUpdateFrequency { get; set; } = 0.5f; // Hz;
            public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Navigation mesh data structure;
        /// </summary>
        public class NavigationMesh;
        {
            public Guid Id { get; set; }
            public BoundingBox Bounds { get; set; }
            public List<NavMeshPolygon> Polygons { get; set; } = new List<NavMeshPolygon>();
            public List<Vector3> Vertices { get; set; } = new List<Vector3>();
            public SpatialPartition SpatialPartition { get; set; }
            public DateTime BuildTimestamp { get; set; }
            public NavMeshBuildOptions BuildOptions { get; set; }

            public Vector3? FindNearestPoint(Vector3 position)
            {
                if (Polygons.Count == 0)
                    return null;

                // Simple implementation - find nearest polygon center;
                var nearestPolygon = Polygons.OrderBy(p => Vector3.Distance(p.Center, position)).FirstOrDefault();
                return nearestPolygon?.Center;
            }

            public NavMeshPolygon GetPolygonAtPosition(Vector3 position)
            {
                // Use spatial partition to find polygon;
                if (SpatialPartition != null)
                {
                    return SpatialPartition.Query(position) as NavMeshPolygon;
                }

                // Fallback: linear search;
                return Polygons.FirstOrDefault(p => IsPointInPolygon(position, p));
            }

            private bool IsPointInPolygon(Vector3 point, NavMeshPolygon polygon)
            {
                // Simple point-in-polygon test for triangles;
                if (polygon.Vertices.Count != 3)
                    return false;

                // Barycentric coordinate test;
                var v0 = polygon.Vertices[2] - polygon.Vertices[0];
                var v1 = polygon.Vertices[1] - polygon.Vertices[0];
                var v2 = point - polygon.Vertices[0];

                var dot00 = Vector3.Dot(v0, v0);
                var dot01 = Vector3.Dot(v0, v1);
                var dot02 = Vector3.Dot(v0, v2);
                var dot11 = Vector3.Dot(v1, v1);
                var dot12 = Vector3.Dot(v1, v2);

                var invDenom = 1.0f / (dot00 * dot11 - dot01 * dot01);
                var u = (dot11 * dot02 - dot01 * dot12) * invDenom;
                var v = (dot00 * dot12 - dot01 * dot02) * invDenom;

                return (u >= 0) && (v >= 0) && (u + v < 1);
            }
        }

        /// <summary>
        /// Navigation mesh polygon;
        /// </summary>
        public class NavMeshPolygon;
        {
            public int Id { get; set; }
            public List<Vector3> Vertices { get; set; } = new List<Vector3>();
            public Vector3 Normal { get; set; }
            public Vector3 Center { get; set; }
            public float Area { get; set; }
            public bool Walkable { get; set; }
            public List<int> NeighborPolygonIds { get; set; } = new List<int>();
            public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Navigation mesh build options;
        /// </summary>
        public class NavMeshBuildOptions;
        {
            public float CellSize { get; set; } = 0.3f;
            public float CellHeight { get; set; } = 0.2f;
            public float AgentHeight { get; set; } = 2.0f;
            public float AgentRadius { get; set; } = 0.5f;
            public float AgentMaxClimb { get; set; } = 0.9f;
            public float AgentMaxSlope { get; set; } = 45.0f;
            public float RegionMinSize { get; set; } = 8.0f;
            public float RegionMergeSize { get; set; } = 20.0f;
            public float EdgeMaxLen { get; set; } = 12.0f;
            public float EdgeMaxError { get; set; } = 1.3f;
            public float DetailSampleDist { get; set; } = 6.0f;
            public float DetailSampleMaxError { get; set; } = 1.0f;
            public float MinPolygonArea { get; set; } = 0.1f;
            public float MaxPolygonArea { get; set; } = 100.0f;
        }

        /// <summary>
        /// Navigation mesh geometry data;
        /// </summary>
        public class NavMeshGeometry
        {
            public List<Vector3> Vertices { get; set; } = new List<Vector3>();
            public List<int> Indices { get; set; } = new List<int>();
            public BoundingBox Bounds { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Navigation agent;
        /// </summary>
        public class NavigationAgent;
        {
            public Guid Id { get; set; }
            public Vector3 Position { get; set; }
            public Vector3 Velocity { get; set; }
            public Quaternion Rotation { get; set; }
            public float CurrentSpeed { get; set; }
            public AgentStatus Status { get; set; }
            public NavigationAgentConfig Config { get; }
            public NavigationPath CurrentPath { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

            public NavigationAgent(NavigationAgentConfig config)
            {
                Config = config ?? throw new ArgumentNullException(nameof(config));
            }
        }

        /// <summary>
        /// Navigation agent configuration;
        /// </summary>
        public class NavigationAgentConfig;
        {
            public float MaxSpeed { get; set; } = 5.0f;
            public float Acceleration { get; set; } = 10.0f;
            public float Deceleration { get; set; } = 15.0f;
            public float RotationSpeed { get; set; } = 360.0f; // Degrees per second;
            public float Radius { get; set; } = 0.5f;
            public float Height { get; set; } = 2.0f;
            public float StepHeight { get; set; } = 0.4f;
            public float MaxSlope { get; set; } = 45.0f;
            public float WaypointThreshold { get; set; } = 0.1f;
            public bool AvoidObstacles { get; set; } = true;
            public float ObstacleDetectionRange { get; set; } = 2.0f;
            public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Agent status;
        /// </summary>
        public enum AgentStatus;
        {
            Idle,
            Moving,
            Stopped,
            Avoiding,
            Jumping,
            Falling,
            Dead;
        }

        /// <summary>
        /// Navigation path;
        /// </summary>
        public class NavigationPath;
        {
            public Guid Id { get; set; }
            public Vector3 Start { get; set; }
            public Vector3 Destination { get; set; }
            public List<Vector3> Waypoints { get; set; } = new List<Vector3>();
            public int CurrentWaypointIndex { get; set; } = 0;
            public float TotalDistance { get; set; }
            public DateTime CreationTime { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

            public bool IsComplete => CurrentWaypointIndex >= Waypoints.Count;

            public Vector3? GetCurrentWaypoint()
            {
                if (CurrentWaypointIndex < Waypoints.Count)
                    return Waypoints[CurrentWaypointIndex];
                return null;
            }

            public void AdvanceToNextWaypoint()
            {
                if (CurrentWaypointIndex < Waypoints.Count)
                    CurrentWaypointIndex++;
            }
        }

        /// <summary>
        /// Path options;
        /// </summary>
        public class PathOptions;
        {
            public bool OptimizePath { get; set; } = true;
            public bool UseAdvancedPathfinding { get; set; } = true;
            public float MaxSearchTime { get; set; } = 1.0f; // seconds;
            public float PathSmoothing { get; set; } = 0.5f;
            public bool AllowPartialPath { get; set; } = true;
            public Dictionary<string, object> Constraints { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Navigation obstacle;
        /// </summary>
        public class NavigationObstacle;
        {
            public Guid Id { get; set; } = Guid.NewGuid();
            public ObstacleType Type { get; set; }
            public Vector3 Position { get; set; }
            public Vector3 Velocity { get; set; }
            public float Radius { get; set; }
            public float Height { get; set; }
            public bool IsActive { get; set; } = true;
            public DateTime SpawnTime { get; set; } = DateTime.UtcNow;
            public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Obstacle type;
        /// </summary>
        public enum ObstacleType;
        {
            Static,
            Dynamic,
            Temporary,
            Environmental,
            Player,
            Vehicle;
        }

        /// <summary>
        /// Pathfinding grid;
        /// </summary>
        public class PathfindingGrid;
        {
            public Guid Id { get; set; }
            public float CellSize { get; set; }
            public float CellHeight { get; set; }
            public BoundingBox Bounds { get; set; }
            public List<PathfindingCell> Cells { get; set; } = new List<PathfindingCell>();
            public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Pathfinding cell;
        /// </summary>
        public class PathfindingCell;
        {
            public Guid Id { get; set; }
            public Vector2 GridPosition { get; set; }
            public Vector3 WorldPosition { get; set; }
            public bool IsWalkable { get; set; }
            public float Cost { get; set; } = 1.0f;
            public List<CellConnection> Neighbors { get; set; } = new List<CellConnection>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Cell connection;
        /// </summary>
        public class CellConnection;
        {
            public PathfindingCell Cell { get; set; }
            public float Cost { get; set; }
            public ConnectionType Type { get; set; } = ConnectionType.Walk;
        }

        /// <summary>
        /// Connection type;
        /// </summary>
        public enum ConnectionType;
        {
            Walk,
            Jump,
            Drop,
            Climb;
        }

        /// <summary>
        /// Spatial partition interface;
        /// </summary>
        public interface SpatialPartition;
        {
            void Add(object item);
            void Remove(object item);
            object Query(Vector3 position);
            List<object> QueryRange(BoundingBox bounds);
        }

        /// <summary>
        /// Grid-based spatial partition;
        /// </summary>
        public class GridSpatialPartition : SpatialPartition;
        {
            private readonly BoundingBox _bounds;
            private readonly float _cellSize;
            private readonly Dictionary<Vector2, List<object>> _cells;

            public GridSpatialPartition(BoundingBox bounds, float cellSize)
            {
                _bounds = bounds;
                _cellSize = cellSize;
                _cells = new Dictionary<Vector2, List<object>>();
            }

            public void Add(object item)
            {
                if (item is NavMeshPolygon polygon)
                {
                    var cellKey = GetCellKey(polygon.Center);
                    if (!_cells.ContainsKey(cellKey))
                        _cells[cellKey] = new List<object>();

                    _cells[cellKey].Add(item);
                }
            }

            public void Remove(object item)
            {
                if (item is NavMeshPolygon polygon)
                {
                    var cellKey = GetCellKey(polygon.Center);
                    if (_cells.ContainsKey(cellKey))
                        _cells[cellKey].Remove(item);
                }
            }

            public object Query(Vector3 position)
            {
                var cellKey = GetCellKey(position);
                if (_cells.ContainsKey(cellKey))
                {
                    // Return first item in cell for simplicity;
                    return _cells[cellKey].FirstOrDefault();
                }
                return null;
            }

            public List<object> QueryRange(BoundingBox bounds)
            {
                var result = new List<object>();

                var minCell = GetCellKey(bounds.Min);
                var maxCell = GetCellKey(bounds.Max);

                for (float x = minCell.X; x <= maxCell.X; x++)
                {
                    for (float z = minCell.Y; z <= maxCell.Y; z++)
                    {
                        var cellKey = new Vector2(x, z);
                        if (_cells.ContainsKey(cellKey))
                        {
                            result.AddRange(_cells[cellKey]);
                        }
                    }
                }

                return result;
            }

            private Vector2 GetCellKey(Vector3 position)
            {
                var x = (int)Math.Floor((position.X - _bounds.Min.X) / _cellSize);
                var z = (int)Math.Floor((position.Z - _bounds.Min.Z) / _cellSize);
                return new Vector2(x, z);
            }
        }

        /// <summary>
        /// Navigation triangle for mesh building;
        /// </summary>
        public class NavMeshTriangle;
        {
            public Vector3 Vertex1 { get; set; }
            public Vector3 Vertex2 { get; set; }
            public Vector3 Vertex3 { get; set; }
            public Vector3 Normal { get; set; }
        }

        /// <summary>
        /// Navigation training data;
        /// </summary>
        public class NavigationTrainingData : TrainingDataSet;
        {
            public List<NavigationPath> SuccessfulPaths { get; set; } = new List<NavigationPath>();
            public List<NavigationPath> FailedPaths { get; set; } = new List<NavigationPath>();
            public List<ObstacleEncounterMemory> ObstacleEncounters { get; set; } = new List<ObstacleEncounterMemory>();
        }

        /// <summary>
        /// Navigation input for ML model;
        /// </summary>
        public class NavigationInput;
        {
            public Vector3 StartPosition { get; set; }
            public Vector3 EndPosition { get; set; }
            public float AgentRadius { get; set; }
            public float AgentHeight { get; set; }
            public string TerrainType { get; set; }
            public float TimeOfDay { get; set; }
            public float ObstacleDensity { get; set; }
            public Dictionary<string, object> AdditionalFeatures { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Path memory for learning;
        /// </summary>
        public class PathMemory;
        {
            public Guid AgentId { get; set; }
            public Guid PathId { get; set; }
            public Vector3 Start { get; set; }
            public Vector3 Destination { get; set; }
            public int WaypointCount { get; set; }
            public float Distance { get; set; }
            public DateTime CreationTime { get; set; }
            public bool Success { get; set; }
            public float TravelTime { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Obstacle encounter memory;
        /// </summary>
        public class ObstacleEncounterMemory;
        {
            public Guid AgentId { get; set; }
            public Guid ObstacleId { get; set; }
            public Vector3 Position { get; set; }
            public DateTime Time { get; set; }
            public string ActionTaken { get; set; }
            public bool Successful { get; set; }
            public float AvoidanceTime { get; set; }
            public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Navigation statistics;
        /// </summary>
        public class NavigationStatistics;
        {
            public int TotalAgents { get; set; }
            public int ActiveAgents { get; set; }
            public int DynamicObstacles { get; set; }
            public int NavMeshPolygons { get; set; }
            public int NavMeshVertices { get; set; }
            public int PathfindingGridCells { get; set; }
            public DateTime LastPathfindingTime { get; set; }
            public float AveragePathfindingTime { get; set; }
            public int PathfindingRequests { get; set; }
            public int SuccessfulPaths { get; set; }
            public int FailedPaths { get; set; }
        }

        #endregion;

        #region Event Args Classes;

        /// <summary>
        /// Event args for navigation mesh updates;
        /// </summary>
        public class NavigationMeshUpdatedEventArgs : EventArgs;
        {
            public NavigationMesh NavMesh { get; set; }
            public DateTime Timestamp { get; set; }
            public string UpdateType { get; set; } = "Built";
        }

        /// <summary>
        /// Event args for agent destination reached;
        /// </summary>
        public class AgentDestinationReachedEventArgs : EventArgs;
        {
            public Guid AgentId { get; set; }
            public Vector3 Destination { get; set; }
            public DateTime Timestamp { get; set; }
            public float TravelTime { get; set; }
            public float DistanceTraveled { get; set; }
        }

        /// <summary>
        /// Event args for agent obstacle encountered;
        /// </summary>
        public class AgentObstacleEncounteredEventArgs : EventArgs;
        {
            public Guid AgentId { get; set; }
            public Guid ObstacleId { get; set; }
            public ObstacleType ObstacleType { get; set; }
            public Vector3 Position { get; set; }
            public DateTime Timestamp { get; set; }
            public float Distance { get; set; }
        }

        /// <summary>
        /// Event args for pathfinding grid updates;
        /// </summary>
        public class PathfindingGridUpdatedEventArgs : EventArgs;
        {
            public PathfindingGrid Grid { get; set; }
            public DateTime Timestamp { get; set; }
            public string UpdateType { get; set; }
            public int UpdatedCells { get; set; }
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Bounding box structure;
    /// </summary>
    public struct BoundingBox;
    {
        public Vector3 Min { get; set; }
        public Vector3 Max { get; set; }

        public BoundingBox(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        public bool Contains(Vector3 point)
        {
            return point.X >= Min.X && point.X <= Max.X &&
                   point.Y >= Min.Y && point.Y <= Max.Y &&
                   point.Z >= Min.Z && point.Z <= Max.Z;
        }

        public ContainmentType Contains(Vector3 point, float tolerance = 0)
        {
            if (point.X < Min.X - tolerance || point.X > Max.X + tolerance ||
                point.Y < Min.Y - tolerance || point.Y > Max.Y + tolerance ||
                point.Z < Min.Z - tolerance || point.Z > Max.Z + tolerance)
                return ContainmentType.Disjoint;

            if (point.X > Min.X + tolerance && point.X < Max.X - tolerance &&
                point.Y > Min.Y + tolerance && point.Y < Max.Y - tolerance &&
                point.Z > Min.Z + tolerance && point.Z < Max.Z - tolerance)
                return ContainmentType.Contains;

            return ContainmentType.Intersects;
        }
    }

    /// <summary>
    /// Containment type;
    /// </summary>
    public enum ContainmentType;
    {
        Disjoint,
        Contains,
        Intersects;
    }

    /// <summary>
    /// Priority queue implementation for A* algorithm;
    /// </summary>
    public class PriorityQueue<TElement, TPriority> where TPriority : IComparable<TPriority>
    {
        private readonly List<(TElement Element, TPriority Priority)> _elements = new List<(TElement, TPriority)>();

        public int Count => _elements.Count;

        public void Enqueue(TElement element, TPriority priority)
        {
            _elements.Add((element, priority));
            int i = _elements.Count - 1;

            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_elements[parent].Priority.CompareTo(priority) <= 0)
                    break;

                (_elements[i], _elements[parent]) = (_elements[parent], _elements[i]);
                i = parent;
            }
        }

        public TElement Dequeue()
        {
            if (_elements.Count == 0)
                throw new InvalidOperationException("Queue is empty");

            var result = _elements[0].Element;
            _elements[0] = _elements[^1];
            _elements.RemoveAt(_elements.Count - 1);

            int i = 0;
            while (true)
            {
                int left = 2 * i + 1;
                int right = 2 * i + 2;
                int smallest = i;

                if (left < _elements.Count && _elements[left].Priority.CompareTo(_elements[smallest].Priority) < 0)
                    smallest = left;

                if (right < _elements.Count && _elements[right].Priority.CompareTo(_elements[smallest].Priority) < 0)
                    smallest = right;

                if (smallest == i)
                    break;

                (_elements[i], _elements[smallest]) = (_elements[smallest], _elements[i]);
                i = smallest;
            }

            return result;
        }

        public IEnumerable<(TElement Element, TPriority Priority)> UnorderedItems => _elements;
    }

    #endregion;
}
