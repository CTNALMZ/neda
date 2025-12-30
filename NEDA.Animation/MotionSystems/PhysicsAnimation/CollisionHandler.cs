using NEDA.Animation.MotionSystems.RagdollSystems;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.Unreal.Physics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace NEDA.Animation.MotionSystems.PhysicsAnimation;
{
    /// <summary>
    /// Advanced collision handling system for physics-based animations and interactions.
    /// Supports multiple collision types, response strategies, and real-time impact processing.
    /// </summary>
    public class CollisionHandler : ICollisionHandler, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly IPhysicsEngine _physicsEngine;
        private readonly ISettingsManager _settingsManager;
        private readonly IRecoveryEngine _recoveryEngine;

        private readonly Dictionary<int, CollisionObject> _collisionObjects;
        private readonly Dictionary<string, CollisionMaterial> _materials;
        private readonly List<CollisionEvent> _pendingCollisions;
        private readonly List<ImpactEvent> _pendingImpacts;
        private readonly object _collisionLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private CollisionSettings _settings;
        private CollisionMetrics _metrics;
        private CollisionSolver _solver;
        private ContactCache _contactCache;
        #endregion;

        #region Public Properties;
        /// <summary>
        /// Gets the number of active collision objects;
        /// </summary>
        public int ActiveObjectCount;
        {
            get;
            {
                lock (_collisionLock)
                {
                    return _collisionObjects.Count;
                }
            }
        }

        /// <summary>
        /// Gets the system performance metrics;
        /// </summary>
        public CollisionMetrics Metrics => _metrics;

        /// <summary>
        /// Gets the current system settings;
        /// </summary>
        public CollisionSettings Settings => _settings;

        /// <summary>
        /// Gets whether the system is initialized and ready;
        /// </summary>
        public bool IsInitialized => _isInitialized;
        #endregion;

        #region Events;
        /// <summary>
        /// Raised when a collision is detected between objects;
        /// </summary>
        public event EventHandler<CollisionDetectedEventArgs> CollisionDetected;

        /// <summary>
        /// Raised when a collision is resolved;
        /// </summary>
        public event EventHandler<CollisionResolvedEventArgs> CollisionResolved;

        /// <summary>
        /// Raised when a significant impact occurs;
        /// </summary>
        public event EventHandler<ImpactDetectedEventArgs> ImpactDetected;

        /// <summary>
        /// Raised when collision metrics are updated;
        /// </summary>
        public event EventHandler<CollisionMetricsUpdatedEventArgs> MetricsUpdated;

        /// <summary>
        /// Raised when a collision object is added or removed;
        /// </summary>
        public event EventHandler<CollisionObjectChangedEventArgs> CollisionObjectChanged;
        #endregion;

        #region Constructor;
        /// <summary>
        /// Initializes a new instance of the CollisionHandler;
        /// </summary>
        public CollisionHandler(
            ILogger logger,
            IPhysicsEngine physicsEngine,
            ISettingsManager settingsManager,
            IRecoveryEngine recoveryEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _physicsEngine = physicsEngine ?? throw new ArgumentNullException(nameof(physicsEngine));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _recoveryEngine = recoveryEngine ?? throw new ArgumentNullException(nameof(recoveryEngine));

            _collisionObjects = new Dictionary<int, CollisionObject>();
            _materials = new Dictionary<string, CollisionMaterial>();
            _pendingCollisions = new List<CollisionEvent>();
            _pendingImpacts = new List<ImpactEvent>();
            _metrics = new CollisionMetrics();
            _solver = new CollisionSolver(logger);
            _contactCache = new ContactCache();

            // Subscribe to physics engine events;
            _physicsEngine.CollisionDetected += OnPhysicsCollisionDetected;

            _logger.LogInformation("CollisionHandler instance created");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Initializes the collision handler system;
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("CollisionHandler is already initialized");
                    return;
                }

                await LoadConfigurationAsync();
                await PreloadMaterialLibraryAsync();
                await InitializeSolverAsync();

                _isInitialized = true;
                _logger.LogInformation("CollisionHandler initialized successfully");
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync("Failed to initialize CollisionHandler", ex);
                throw new CollisionHandlerException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Registers a collision object for handling;
        /// </summary>
        public async Task<int> RegisterCollisionObjectAsync(CollisionObject collisionObject)
        {
            if (collisionObject == null)
                throw new ArgumentNullException(nameof(collisionObject));

            try
            {
                await ValidateSystemState();
                await ValidateCollisionObject(collisionObject);

                var objectId = GenerateObjectId();
                collisionObject.Id = objectId;

                lock (_collisionLock)
                {
                    _collisionObjects[objectId] = collisionObject;
                }

                await InitializeObjectPhysicsAsync(collisionObject);
                _metrics.ObjectsRegistered++;

                RaiseCollisionObjectChanged(objectId, CollisionObjectChangeType.Added);
                _logger.LogDebug($"Collision object registered: {objectId} ({collisionObject.ObjectType})");

                return objectId;
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync("Failed to register collision object", ex);
                throw new CollisionHandlerException("Object registration failed", ex);
            }
        }

        /// <summary>
        /// Unregisters a collision object;
        /// </summary>
        public async Task<bool> UnregisterCollisionObjectAsync(int objectId)
        {
            try
            {
                CollisionObject collisionObject;
                lock (_collisionLock)
                {
                    if (!_collisionObjects.TryGetValue(objectId, out collisionObject))
                    {
                        _logger.LogWarning($"Collision object not found: {objectId}");
                        return false;
                    }

                    _collisionObjects.Remove(objectId);
                }

                await CleanupObjectPhysicsAsync(collisionObject);
                _metrics.ObjectsUnregistered++;

                RaiseCollisionObjectChanged(objectId, CollisionObjectChangeType.Removed);
                _logger.LogDebug($"Collision object unregistered: {objectId}");

                return true;
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to unregister collision object: {objectId}", ex);
                throw new CollisionHandlerException("Object unregistration failed", ex);
            }
        }

        /// <summary>
        /// Processes all pending collisions and impacts;
        /// </summary>
        public async Task ProcessCollisionsAsync(float deltaTime)
        {
            try
            {
                if (!_isInitialized) return;

                var processTimer = System.Diagnostics.Stopwatch.StartNew();
                _metrics.LastProcessTime = deltaTime;

                await ProcessPendingCollisionsAsync();
                await ProcessPendingImpactsAsync();
                await UpdateContactCacheAsync(deltaTime);
                await UpdateMetricsAsync();

                processTimer.Stop();
                _metrics.LastProcessDuration = (float)processTimer.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync("Error during collision processing", ex);
                await _recoveryEngine.ExecuteRecoveryStrategyAsync("CollisionProcessing");
            }
        }

        /// <summary>
        /// Performs a collision check between two objects;
        /// </summary>
        public async Task<CollisionTestResult> CheckCollisionAsync(int objectIdA, int objectIdB)
        {
            if (objectIdA == objectIdB)
                throw new ArgumentException("Cannot check collision between the same object");

            try
            {
                var objA = await GetCollisionObjectAsync(objectIdA);
                var objB = await GetCollisionObjectAsync(objectIdB);

                if (objA.PhysicsBodyId.HasValue && objB.PhysicsBodyId.HasValue)
                {
                    var collision = await PerformCollisionTestAsync(objA, objB);
                    return new CollisionTestResult;
                    {
                        IsColliding = collision != null,
                        Collision = collision,
                        PenetrationDepth = collision?.PenetrationDepth ?? 0f,
                        ContactPoint = collision?.ContactPoint ?? Vector3.Zero,
                        Normal = collision?.Normal ?? Vector3.Zero;
                    };
                }

                return CollisionTestResult.NoCollision;
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to check collision between {objectIdA} and {objectIdB}", ex);
                throw new CollisionHandlerException("Collision check failed", ex);
            }
        }

        /// <summary>
        /// Performs a sweep test for moving objects;
        /// </summary>
        public async Task<SweepTestResult> SweepTestAsync(int objectId, Vector3 direction, float distance)
        {
            if (direction == Vector3.Zero)
                throw new ArgumentException("Direction cannot be zero", nameof(direction));

            try
            {
                var collisionObject = await GetCollisionObjectAsync(objectId);
                var result = new SweepTestResult();

                if (collisionObject.PhysicsBodyId.HasValue)
                {
                    var raycastResult = await _physicsEngine.RaycastAsync(
                        collisionObject.Position,
                        direction,
                        distance + collisionObject.BoundingRadius;
                    );

                    if (raycastResult.Hit)
                    {
                        result.Hit = true;
                        result.HitObjectId = raycastResult.BodyId;
                        result.HitDistance = raycastResult.Distance - collisionObject.BoundingRadius;
                        result.HitPoint = raycastResult.Point;
                        result.HitNormal = raycastResult.Normal;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to perform sweep test for object {objectId}", ex);
                throw new CollisionHandlerException("Sweep test failed", ex);
            }
        }

        /// <summary>
        /// Applies collision response to objects;
        /// </summary>
        public async Task ApplyCollisionResponseAsync(CollisionEvent collisionEvent)
        {
            if (collisionEvent == null)
                throw new ArgumentNullException(nameof(collisionEvent));

            try
            {
                var objA = await GetCollisionObjectAsync(collisionEvent.ObjectIdA);
                var objB = await GetCollisionObjectAsync(collisionEvent.ObjectIdB);

                var materialA = GetMaterial(objA.MaterialId);
                var materialB = GetMaterial(objB.MaterialId);

                var response = await CalculateCollisionResponseAsync(collisionEvent, materialA, materialB);
                await ApplyResponseToObjectsAsync(objA, objB, response);

                _metrics.CollisionsResolved++;
                RaiseCollisionResolved(collisionEvent, response);

                _logger.LogDebug($"Collision response applied between {collisionEvent.ObjectIdA} and {collisionEvent.ObjectIdB}");
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync("Failed to apply collision response", ex);
                throw new CollisionHandlerException("Collision response failed", ex);
            }
        }

        /// <summary>
        /// Registers a collision material;
        /// </summary>
        public async Task RegisterMaterialAsync(CollisionMaterial material)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            try
            {
                await ValidateMaterial(material);

                lock (_collisionLock)
                {
                    _materials[material.MaterialId] = material;
                }

                _logger.LogInformation($"Collision material registered: {material.MaterialId}");
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to register material: {material.MaterialId}", ex);
                throw new CollisionHandlerException("Material registration failed", ex);
            }
        }

        /// <summary>
        /// Gets the contact points between two objects;
        /// </summary>
        public async Task<List<ContactPoint>> GetContactPointsAsync(int objectIdA, int objectIdB)
        {
            try
            {
                var cacheKey = ContactCache.GenerateKey(objectIdA, objectIdB);
                return _contactCache.GetContacts(cacheKey);
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to get contact points between {objectIdA} and {objectIdB}", ex);
                throw new CollisionHandlerException("Contact points retrieval failed", ex);
            }

            await Task.CompletedTask;
            return new List<ContactPoint>();
        }

        /// <summary>
        /// Sets collision ignore relationship between two objects;
        /// </summary>
        public async Task SetCollisionIgnoreAsync(int objectIdA, int objectIdB, bool ignore)
        {
            try
            {
                var objA = await GetCollisionObjectAsync(objectIdA);
                var objB = await GetCollisionObjectAsync(objectIdB);

                if (ignore)
                {
                    objA.IgnoredObjects.Add(objectIdB);
                    objB.IgnoredObjects.Add(objectIdA);
                }
                else;
                {
                    objA.IgnoredObjects.Remove(objectIdB);
                    objB.IgnoredObjects.Remove(objectIdA);
                }

                _logger.LogDebug($"Collision ignore set between {objectIdA} and {objectIdB}: {ignore}");
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to set collision ignore between {objectIdA} and {objectIdB}", ex);
                throw new CollisionHandlerException("Collision ignore setting failed", ex);
            }
        }

        /// <summary>
        /// Applies impact damage to objects based on collision;
        /// </summary>
        public async Task ApplyImpactDamageAsync(ImpactEvent impactEvent)
        {
            if (impactEvent == null)
                throw new ArgumentNullException(nameof(impactEvent));

            try
            {
                var collisionObject = await GetCollisionObjectAsync(impactEvent.ObjectId);
                var material = GetMaterial(collisionObject.MaterialId);

                var damage = CalculateImpactDamage(impactEvent, material);
                await ApplyDamageToObjectAsync(collisionObject, damage, impactEvent);

                _metrics.ImpactsProcessed++;
                _logger.LogDebug($"Impact damage applied to object {impactEvent.ObjectId}: {damage}");
            }
            catch (Exception ex)
            {
                await HandleCollisionExceptionAsync($"Failed to apply impact damage to object {impactEvent.ObjectId}", ex);
                throw new CollisionHandlerException("Impact damage application failed", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private async Task LoadConfigurationAsync()
        {
            try
            {
                _settings = _settingsManager.GetSection<CollisionSettings>("CollisionHandler") ?? new CollisionSettings();
                _logger.LogInformation("Collision configuration loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load collision configuration: {ex.Message}");
                _settings = new CollisionSettings();
            }

            await Task.CompletedTask;
        }

        private async Task PreloadMaterialLibraryAsync()
        {
            try
            {
                // Preload common materials;
                var commonMaterials = new[]
                {
                    new CollisionMaterial { MaterialId = "Metal", Density = 7.8f, Restitution = 0.3f, Friction = 0.4f, Hardness = 0.8f },
                    new CollisionMaterial { MaterialId = "Wood", Density = 0.7f, Restitution = 0.5f, Friction = 0.6f, Hardness = 0.4f },
                    new CollisionMaterial { MaterialId = "Concrete", Density = 2.4f, Restitution = 0.1f, Friction = 0.8f, Hardness = 0.9f },
                    new CollisionMaterial { MaterialId = "Rubber", Density = 1.2f, Restitution = 0.8f, Friction = 1.0f, Hardness = 0.2f },
                    new CollisionMaterial { MaterialId = "Glass", Density = 2.5f, Restitution = 0.2f, Friction = 0.1f, Hardness = 0.7f }
                };

                foreach (var material in commonMaterials)
                {
                    await RegisterMaterialAsync(material);
                }

                _logger.LogInformation("Common collision materials preloaded");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to preload material library: {ex.Message}");
            }
        }

        private async Task InitializeSolverAsync()
        {
            _solver.Initialize(_settings);
            _logger.LogDebug("Collision solver initialized");
            await Task.CompletedTask;
        }

        private async Task InitializeObjectPhysicsAsync(CollisionObject collisionObject)
        {
            if (collisionObject.PhysicsBodyId.HasValue) return;

            var rigidBody = new RigidBody;
            {
                Position = collisionObject.Position,
                Rotation = collisionObject.Rotation,
                Mass = collisionObject.Mass,
                Restitution = GetMaterial(collisionObject.MaterialId).Restitution,
                Friction = GetMaterial(collisionObject.MaterialId).Friction,
                CollisionShape = CreateCollisionShape(collisionObject)
            };

            var bodyId = await _physicsEngine.AddRigidBodyAsync(rigidBody);
            collisionObject.PhysicsBodyId = bodyId;
        }

        private async Task CleanupObjectPhysicsAsync(CollisionObject collisionObject)
        {
            if (collisionObject.PhysicsBodyId.HasValue)
            {
                await _physicsEngine.RemoveRigidBodyAsync(collisionObject.PhysicsBodyId.Value);
                collisionObject.PhysicsBodyId = null;
            }
        }

        private CollisionShape CreateCollisionShape(CollisionObject collisionObject)
        {
            switch (collisionObject.CollisionType)
            {
                case CollisionType.Sphere:
                    return new SphereShape { Radius = collisionObject.BoundingRadius };
                case CollisionType.Box:
                    return new BoxShape;
                    {
                        HalfExtents = new Vector3(
                            collisionObject.BoundingRadius,
                            collisionObject.BoundingRadius,
                            collisionObject.BoundingRadius;
                        )
                    };
                case CollisionType.Capsule:
                    return new CapsuleShape;
                    {
                        Radius = collisionObject.BoundingRadius * 0.5f,
                        Height = collisionObject.BoundingRadius * 2f;
                    };
                case CollisionType.Mesh:
                    return new MeshShape;
                    {
                        Vertices = collisionObject.MeshVertices,
                        Indices = collisionObject.MeshIndices;
                    };
                default:
                    return new SphereShape { Radius = collisionObject.BoundingRadius };
            }
        }

        private async Task ProcessPendingCollisionsAsync()
        {
            List<CollisionEvent> collisionsToProcess;
            lock (_collisionLock)
            {
                collisionsToProcess = new List<CollisionEvent>(_pendingCollisions);
                _pendingCollisions.Clear();
            }

            foreach (var collision in collisionsToProcess)
            {
                try
                {
                    await ProcessCollisionAsync(collision);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing collision: {ex.Message}");
                }
            }

            await Task.CompletedTask;
        }

        private async Task ProcessPendingImpactsAsync()
        {
            List<ImpactEvent> impactsToProcess;
            lock (_collisionLock)
            {
                impactsToProcess = new List<ImpactEvent>(_pendingImpacts);
                _pendingImpacts.Clear();
            }

            foreach (var impact in impactsToProcess)
            {
                try
                {
                    await ProcessImpactAsync(impact);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing impact: {ex.Message}");
                }
            }

            await Task.CompletedTask;
        }

        private async Task ProcessCollisionAsync(CollisionEvent collision)
        {
            // Check if objects should ignore this collision;
            if (ShouldIgnoreCollision(collision.ObjectIdA, collision.ObjectIdB))
            {
                _metrics.CollisionsIgnored++;
                return;
            }

            // Update contact cache;
            var cacheKey = ContactCache.GenerateKey(collision.ObjectIdA, collision.ObjectIdB);
            _contactCache.AddContact(cacheKey, new ContactPoint;
            {
                Point = collision.ContactPoint,
                Normal = collision.Normal,
                Depth = collision.PenetrationDepth,
                Timestamp = DateTime.UtcNow;
            });

            // Apply collision response;
            await ApplyCollisionResponseAsync(collision);

            // Raise collision detected event;
            RaiseCollisionDetected(collision);

            _metrics.CollisionsProcessed++;
        }

        private async Task ProcessImpactAsync(ImpactEvent impact)
        {
            // Check if impact is significant enough to process;
            if (impact.ImpactForce < _settings.MinImpactForce)
            {
                _metrics.ImpactsIgnored++;
                return;
            }

            // Apply impact damage;
            await ApplyImpactDamageAsync(impact);

            // Raise impact detected event;
            RaiseImpactDetected(impact);

            _metrics.ImpactsProcessed++;
        }

        private async Task UpdateContactCacheAsync(float deltaTime)
        {
            _contactCache.Update(deltaTime, _settings.ContactPersistenceTime);
            await Task.CompletedTask;
        }

        private async Task UpdateMetricsAsync()
        {
            _metrics.ActiveObjects = ActiveObjectCount;
            _metrics.ActiveContacts = _contactCache.ContactCount;
            _metrics.ActiveMaterials = _materials.Count;

            RaiseMetricsUpdated();
            await Task.CompletedTask;
        }

        private async Task<Collision> PerformCollisionTestAsync(CollisionObject objA, CollisionObject objB)
        {
            // This would typically use the physics engine's collision detection;
            // For now, we'll simulate a basic collision test;

            var distance = Vector3.Distance(objA.Position, objB.Position);
            var minDistance = objA.BoundingRadius + objB.BoundingRadius;

            if (distance < minDistance)
            {
                var direction = Vector3.Normalize(objB.Position - objA.Position);
                var penetration = minDistance - distance;

                return new Collision;
                {
                    BodyAId = objA.PhysicsBodyId.Value,
                    BodyBId = objB.PhysicsBodyId.Value,
                    ContactPoint = objA.Position + direction * objA.BoundingRadius,
                    Normal = direction,
                    PenetrationDepth = penetration;
                };
            }

            return null;
        }

        private async Task<CollisionResponse> CalculateCollisionResponseAsync(
            CollisionEvent collision,
            CollisionMaterial materialA,
            CollisionMaterial materialB)
        {
            return await _solver.SolveCollisionAsync(collision, materialA, materialB);
        }

        private async Task ApplyResponseToObjectsAsync(
            CollisionObject objA,
            CollisionObject objB,
            CollisionResponse response)
        {
            if (objA.PhysicsBodyId.HasValue && objB.PhysicsBodyId.HasValue)
            {
                // Apply impulses to physics bodies;
                await _physicsEngine.ApplyImpulseAsync(objA.PhysicsBodyId.Value, -response.ImpulseA, response.ContactPoint);
                await _physicsEngine.ApplyImpulseAsync(objB.PhysicsBodyId.Value, response.ImpulseB, response.ContactPoint);

                // Apply friction;
                if (response.FrictionImpulse != Vector3.Zero)
                {
                    await _physicsEngine.ApplyImpulseAsync(objA.PhysicsBodyId.Value, -response.FrictionImpulse, response.ContactPoint);
                    await _physicsEngine.ApplyImpulseAsync(objB.PhysicsBodyId.Value, response.FrictionImpulse, response.ContactPoint);
                }
            }
        }

        private float CalculateImpactDamage(ImpactEvent impact, CollisionMaterial material)
        {
            var baseDamage = impact.ImpactForce * _settings.ImpactDamageMultiplier;
            var materialFactor = material.Hardness * _settings.MaterialDamageFactor;
            return baseDamage * materialFactor;
        }

        private async Task ApplyDamageToObjectAsync(CollisionObject obj, float damage, ImpactEvent impact)
        {
            obj.Health -= damage;

            if (obj.Health <= 0 && obj.ObjectType == CollisionObjectType.Destructible)
            {
                await HandleObjectDestructionAsync(obj, impact);
            }

            await Task.CompletedTask;
        }

        private async Task HandleObjectDestructionAsync(CollisionObject obj, ImpactEvent impact)
        {
            // Handle object destruction - spawn particles, play sounds, etc.
            _metrics.ObjectsDestroyed++;
            _logger.LogInformation($"Object destroyed: {obj.Id} with damage: {impact.ImpactForce}");

            await UnregisterCollisionObjectAsync(obj.Id);
        }

        private bool ShouldIgnoreCollision(int objectIdA, int objectIdB)
        {
            lock (_collisionLock)
            {
                if (_collisionObjects.TryGetValue(objectIdA, out var objA) &&
                    _collisionObjects.TryGetValue(objectIdB, out var objB))
                {
                    return objA.IgnoredObjects.Contains(objectIdB) ||
                           objB.IgnoredObjects.Contains(objectIdA) ||
                           (objA.CollisionLayer & objB.CollisionMask) == 0;
                }
            }
            return false;
        }

        private CollisionMaterial GetMaterial(string materialId)
        {
            lock (_collisionLock)
            {
                if (_materials.TryGetValue(materialId, out var material))
                {
                    return material;
                }
            }

            // Return default material if not found;
            return new CollisionMaterial { MaterialId = "Default", Density = 1.0f, Restitution = 0.5f, Friction = 0.5f };
        }

        private async Task<CollisionObject> GetCollisionObjectAsync(int objectId)
        {
            lock (_collisionLock)
            {
                if (_collisionObjects.TryGetValue(objectId, out var collisionObject))
                {
                    return collisionObject;
                }
            }

            throw new CollisionHandlerException($"Collision object not found: {objectId}");
        }

        private async Task ValidateSystemState()
        {
            if (!_isInitialized)
                throw new CollisionHandlerException("CollisionHandler is not initialized");

            if (!_physicsEngine.CurrentState == SimulationState.Running)
                throw new CollisionHandlerException("Physics engine is not running");

            await Task.CompletedTask;
        }

        private async Task ValidateCollisionObject(CollisionObject collisionObject)
        {
            if (string.IsNullOrEmpty(collisionObject.MaterialId))
                throw new CollisionHandlerException("Collision object must have a material ID");

            if (collisionObject.BoundingRadius <= 0)
                throw new CollisionHandlerException("Bounding radius must be greater than zero");

            if (collisionObject.Mass < 0)
                throw new CollisionHandlerException("Mass cannot be negative");

            await Task.CompletedTask;
        }

        private async Task ValidateMaterial(CollisionMaterial material)
        {
            if (string.IsNullOrEmpty(material.MaterialId))
                throw new CollisionHandlerException("Material ID cannot be null or empty");

            if (material.Density <= 0)
                throw new CollisionHandlerException("Material density must be greater than zero");

            if (material.Restitution < 0 || material.Restitution > 1)
                throw new CollisionHandlerException("Restitution must be between 0 and 1");

            if (material.Friction < 0)
                throw new CollisionHandlerException("Friction cannot be negative");

            await Task.CompletedTask;
        }

        private int GenerateObjectId()
        {
            return Math.Abs(Guid.NewGuid().GetHashCode());
        }

        private async Task HandleCollisionExceptionAsync(string context, Exception exception)
        {
            _logger.LogError($"{context}: {exception.Message}", exception);
            await _recoveryEngine.ExecuteRecoveryStrategyAsync("CollisionHandler", exception);
        }

        private void OnPhysicsCollisionDetected(object sender, CollisionEventArgs e)
        {
            try
            {
                var collisionEvent = new CollisionEvent;
                {
                    ObjectIdA = e.Collision.BodyAId,
                    ObjectIdB = e.Collision.BodyBId,
                    ContactPoint = e.Collision.ContactPoint,
                    Normal = e.Collision.Normal,
                    PenetrationDepth = e.Collision.PenetrationDepth,
                    RelativeVelocity = Vector3.Zero, // Would be calculated from physics bodies;
                    Timestamp = e.Timestamp;
                };

                lock (_collisionLock)
                {
                    _pendingCollisions.Add(collisionEvent);
                }

                _metrics.CollisionsDetected++;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling physics collision: {ex.Message}");
            }
        }

        private void RaiseCollisionDetected(CollisionEvent collision)
        {
            CollisionDetected?.Invoke(this, new CollisionDetectedEventArgs;
            {
                CollisionEvent = collision,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCollisionResolved(CollisionEvent collision, CollisionResponse response)
        {
            CollisionResolved?.Invoke(this, new CollisionResolvedEventArgs;
            {
                CollisionEvent = collision,
                Response = response,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseImpactDetected(ImpactEvent impact)
        {
            ImpactDetected?.Invoke(this, new ImpactDetectedEventArgs;
            {
                ImpactEvent = impact,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseMetricsUpdated()
        {
            MetricsUpdated?.Invoke(this, new CollisionMetricsUpdatedEventArgs;
            {
                Metrics = _metrics,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCollisionObjectChanged(int objectId, CollisionObjectChangeType changeType)
        {
            CollisionObjectChanged?.Invoke(this, new CollisionObjectChangedEventArgs;
            {
                ObjectId = objectId,
                ChangeType = changeType,
                Timestamp = DateTime.UtcNow;
            });
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
                    // Unsubscribe from physics engine;
                    _physicsEngine.CollisionDetected -= OnPhysicsCollisionDetected;

                    // Clean up collision objects;
                    var objectIds = _collisionObjects.Keys.ToList();
                    foreach (var objectId in objectIds)
                    {
                        try
                        {
                            UnregisterCollisionObjectAsync(objectId).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error unregistering collision object during disposal: {ex.Message}");
                        }
                    }

                    _collisionObjects.Clear();
                    _materials.Clear();
                    _pendingCollisions.Clear();
                    _pendingImpacts.Clear();
                    _contactCache.Clear();
                }

                _isDisposed = true;
            }
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    public enum CollisionType;
    {
        Sphere,
        Box,
        Capsule,
        Mesh,
        Compound;
    }

    public enum CollisionObjectType;
    {
        Static,
        Dynamic,
        Kinematic,
        Destructible,
        Trigger;
    }

    public enum CollisionObjectChangeType;
    {
        Added,
        Removed,
        Updated;
    }

    public class CollisionSettings;
    {
        public float MinImpactForce { get; set; } = 10.0f;
        public float ImpactDamageMultiplier { get; set; } = 0.1f;
        public float MaterialDamageFactor { get; set; } = 1.0f;
        public float ContactPersistenceTime { get; set; } = 2.0f;
        public float SolverIterations { get; set; } = 10;
        public float Baumgarte { get; set; } = 0.2f;
        public float Slop { get; set; } = 0.01f;
        public float RestingThreshold { get; set; } = 0.1f;
    }

    public class CollisionMetrics;
    {
        public int ObjectsRegistered { get; set; }
        public int ObjectsUnregistered { get; set; }
        public int ObjectsDestroyed { get; set; }
        public int ActiveObjects { get; set; }
        public int CollisionsDetected { get; set; }
        public int CollisionsProcessed { get; set; }
        public int CollisionsResolved { get; set; }
        public int CollisionsIgnored { get; set; }
        public int ImpactsProcessed { get; set; }
        public int ImpactsIgnored { get; set; }
        public int ActiveContacts { get; set; }
        public int ActiveMaterials { get; set; }
        public float LastProcessTime { get; set; }
        public float LastProcessDuration { get; set; }
    }

    public class CollisionObject;
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public CollisionObjectType ObjectType { get; set; }
        public CollisionType CollisionType { get; set; }
        public string MaterialId { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public float BoundingRadius { get; set; }
        public float Mass { get; set; }
        public float Health { get; set; } = 100.0f;
        public int? PhysicsBodyId { get; set; }
        public int CollisionLayer { get; set; } = 1;
        public int CollisionMask { get; set; } = -1; // All layers;
        public HashSet<int> IgnoredObjects { get; set; } = new HashSet<int>();
        public List<Vector3> MeshVertices { get; set; }
        public List<int> MeshIndices { get; set; }
    }

    public class CollisionMaterial;
    {
        public string MaterialId { get; set; }
        public float Density { get; set; } = 1.0f;
        public float Restitution { get; set; } = 0.5f;
        public float Friction { get; set; } = 0.5f;
        public float Hardness { get; set; } = 0.5f;
        public float Durability { get; set; } = 1.0f;
    }

    public class CollisionEvent;
    {
        public int ObjectIdA { get; set; }
        public int ObjectIdB { get; set; }
        public Vector3 ContactPoint { get; set; }
        public Vector3 Normal { get; set; }
        public float PenetrationDepth { get; set; }
        public Vector3 RelativeVelocity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ImpactEvent;
    {
        public int ObjectId { get; set; }
        public Vector3 ImpactPoint { get; set; }
        public Vector3 ImpactNormal { get; set; }
        public float ImpactForce { get; set; }
        public Vector3 ImpactDirection { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CollisionResponse;
    {
        public Vector3 ImpulseA { get; set; }
        public Vector3 ImpulseB { get; set; }
        public Vector3 FrictionImpulse { get; set; }
        public Vector3 ContactPoint { get; set; }
        public float EnergyLoss { get; set; }
    }

    public class ContactPoint;
    {
        public Vector3 Point { get; set; }
        public Vector3 Normal { get; set; }
        public float Depth { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CollisionTestResult;
    {
        public bool IsColliding { get; set; }
        public Collision Collision { get; set; }
        public float PenetrationDepth { get; set; }
        public Vector3 ContactPoint { get; set; }
        public Vector3 Normal { get; set; }

        public static CollisionTestResult NoCollision => new CollisionTestResult { IsColliding = false };
    }

    public class SweepTestResult;
    {
        public bool Hit { get; set; }
        public int HitObjectId { get; set; }
        public float HitDistance { get; set; }
        public Vector3 HitPoint { get; set; }
        public Vector3 HitNormal { get; set; }
    }

    public class CollisionDetectedEventArgs : EventArgs;
    {
        public CollisionEvent CollisionEvent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CollisionResolvedEventArgs : EventArgs;
    {
        public CollisionEvent CollisionEvent { get; set; }
        public CollisionResponse Response { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ImpactDetectedEventArgs : EventArgs;
    {
        public ImpactEvent ImpactEvent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CollisionMetricsUpdatedEventArgs : EventArgs;
    {
        public CollisionMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CollisionObjectChangedEventArgs : EventArgs;
    {
        public int ObjectId { get; set; }
        public CollisionObjectChangeType ChangeType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class CollisionHandlerException : Exception
    {
        public CollisionHandlerException(string message) : base(message) { }
        public CollisionHandlerException(string message, Exception innerException) : base(message, innerException) { }
    }

    // Internal helper classes;
    internal class CollisionSolver;
    {
        private readonly ILogger _logger;
        private CollisionSettings _settings;

        public CollisionSolver(ILogger logger)
        {
            _logger = logger;
        }

        public void Initialize(CollisionSettings settings)
        {
            _settings = settings;
        }

        public async Task<CollisionResponse> SolveCollisionAsync(
            CollisionEvent collision,
            CollisionMaterial materialA,
            CollisionMaterial materialB)
        {
            return await Task.Run(() =>
            {
                var response = new CollisionResponse;
                {
                    ContactPoint = collision.ContactPoint;
                };

                // Calculate normal impulse;
                var restitution = (materialA.Restitution + materialB.Restitution) * 0.5f;
                var impulse = CalculateImpulse(collision, restitution);

                response.ImpulseA = impulse;
                response.ImpulseB = -impulse;

                // Calculate friction impulse;
                response.FrictionImpulse = CalculateFrictionImpulse(collision, materialA, materialB);

                return response;
            });
        }

        private Vector3 CalculateImpulse(CollisionEvent collision, float restitution)
        {
            var impulseMagnitude = collision.RelativeVelocity.Length() * (1 + restitution);
            return collision.Normal * impulseMagnitude;
        }

        private Vector3 CalculateFrictionImpulse(CollisionEvent collision, CollisionMaterial materialA, CollisionMaterial materialB)
        {
            var friction = (materialA.Friction + materialB.Friction) * 0.5f;
            var tangent = collision.RelativeVelocity - Vector3.Dot(collision.RelativeVelocity, collision.Normal) * collision.Normal;

            if (tangent.Length() > 0.001f)
            {
                tangent = Vector3.Normalize(tangent);
            }

            return tangent * friction * collision.RelativeVelocity.Length();
        }
    }

    internal class ContactCache;
    {
        private readonly Dictionary<string, List<ContactPoint>> _contacts;
        private readonly object _cacheLock = new object();

        public int ContactCount;
        {
            get;
            {
                lock (_cacheLock)
                {
                    return _contacts.Values.Sum(list => list.Count);
                }
            }
        }

        public ContactCache()
        {
            _contacts = new Dictionary<string, List<ContactPoint>>();
        }

        public static string GenerateKey(int objectIdA, int objectIdB)
        {
            return objectIdA < objectIdB ? $"{objectIdA}_{objectIdB}" : $"{objectIdB}_{objectIdA}";
        }

        public void AddContact(string cacheKey, ContactPoint contact)
        {
            lock (_cacheLock)
            {
                if (!_contacts.ContainsKey(cacheKey))
                {
                    _contacts[cacheKey] = new List<ContactPoint>();
                }

                _contacts[cacheKey].Add(contact);
            }
        }

        public List<ContactPoint> GetContacts(string cacheKey)
        {
            lock (_cacheLock)
            {
                if (_contacts.TryGetValue(cacheKey, out var contacts))
                {
                    return new List<ContactPoint>(contacts);
                }
            }
            return new List<ContactPoint>();
        }

        public void Update(float deltaTime, float persistenceTime)
        {
            lock (_cacheLock)
            {
                var now = DateTime.UtcNow;
                var keysToRemove = new List<string>();

                foreach (var kvp in _contacts)
                {
                    kvp.Value.RemoveAll(contact =>
                        (now - contact.Timestamp).TotalSeconds > persistenceTime);

                    if (kvp.Value.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _contacts.Remove(key);
                }
            }
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _contacts.Clear();
            }
        }
    }
    #endregion;
}
