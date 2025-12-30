using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Common.Utilities;
using NEDA.Common.Constants;
using NEDA.Services.EventBus;

namespace NEDA.CharacterSystems.GameplaySystems.QuestDesigner;
{
    /// <summary>
    /// Represents the type of objective;
    /// </summary>
    public enum ObjectiveType;
    {
        Collect,
        Kill,
        Interact,
        Explore,
        Deliver,
        Craft,
        Escort,
        Defend,
        TimeLimit,
        Sequence,
        Custom;
    }

    /// <summary>
    /// Represents the state of an objective;
    /// </summary>
    public enum ObjectiveState;
    {
        Inactive,
        Active,
        Completed,
        Failed,
        Hidden;
    }

    /// <summary>
    /// Represents the progress tracking method;
    /// </summary>
    public enum ProgressType;
    {
        Boolean,
        Numeric,
        Percentage,
        Tiered;
    }

    /// <summary>
    /// Represents a single objective within a quest;
    /// </summary>
    public class Objective;
    {
        public string Id { get; private set; }
        public string QuestId { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public ObjectiveType Type { get; private set; }
        public ObjectiveState State { get; private set; }
        public ProgressType ProgressTracking { get; private set; }

        public int CurrentProgress { get; private set; }
        public int RequiredProgress { get; private set; }
        public float ProgressPercentage => RequiredProgress > 0 ?
            (float)CurrentProgress / RequiredProgress * 100 : 0;

        public bool IsOptional { get; private set; }
        public int OrderIndex { get; private set; }
        public string TargetId { get; private set; }
        public string LocationId { get; private set; }

        public Dictionary<string, object> Parameters { get; private set; }
        public List<string> PrerequisiteObjectiveIds { get; private set; }

        public DateTime? ActivationTime { get; private set; }
        public DateTime? CompletionTime { get; private set; }
        public TimeSpan? TimeLimit { get; private set; }
        public DateTime? TimeLimitExpiry { get; private set; }

        public Dictionary<string, object> CompletionData { get; private set; }

        public Objective(
            string id,
            string questId,
            string title,
            ObjectiveType type,
            int requiredProgress = 1,
            bool isOptional = false)
        {
            Id = id ?? Guid.NewGuid().ToString();
            QuestId = questId ?? throw new ArgumentNullException(nameof(questId));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Type = type;
            State = ObjectiveState.Inactive;
            ProgressTracking = DetermineProgressType(type);
            CurrentProgress = 0;
            RequiredProgress = Math.Max(1, requiredProgress);
            IsOptional = isOptional;

            Parameters = new Dictionary<string, object>();
            PrerequisiteObjectiveIds = new List<string>();
            CompletionData = new Dictionary<string, object>();

            OrderIndex = 0;
        }

        private ProgressType DetermineProgressType(ObjectiveType type)
        {
            return type switch;
            {
                ObjectiveType.Collect => ProgressType.Numeric,
                ObjectiveType.Kill => ProgressType.Numeric,
                ObjectiveType.Interact => ProgressType.Boolean,
                ObjectiveType.Explore => ProgressType.Boolean,
                ObjectiveType.Deliver => ProgressType.Boolean,
                ObjectiveType.Craft => ProgressType.Numeric,
                ObjectiveType.Escort => ProgressType.Percentage,
                ObjectiveType.Defend => ProgressType.Percentage,
                ObjectiveType.TimeLimit => ProgressType.Boolean,
                ObjectiveType.Sequence => ProgressType.Tiered,
                _ => ProgressType.Boolean;
            };
        }

        public void Activate()
        {
            if (State != ObjectiveState.Inactive)
                throw new InvalidOperationException($"Objective {Id} cannot be activated from state {State}");

            State = ObjectiveState.Active;
            ActivationTime = DateTime.UtcNow;

            if (TimeLimit.HasValue)
            {
                TimeLimitExpiry = DateTime.UtcNow.Add(TimeLimit.Value);
            }
        }

        public void UpdateProgress(int amount, Dictionary<string, object> additionalData = null)
        {
            if (State != ObjectiveState.Active)
                throw new InvalidOperationException($"Cannot update progress for objective in state {State}");

            if (TimeLimitExpiry.HasValue && DateTime.UtcNow > TimeLimitExpiry.Value)
            {
                Fail("Time limit expired");
                return;
            }

            CurrentProgress = Math.Max(0, Math.Min(RequiredProgress, CurrentProgress + amount));

            if (CurrentProgress >= RequiredProgress)
            {
                Complete(additionalData);
            }
        }

        public void Complete(Dictionary<string, object> completionData = null)
        {
            if (State != ObjectiveState.Active)
                throw new InvalidOperationException($"Objective {Id} cannot be completed from state {State}");

            State = ObjectiveState.Completed;
            CurrentProgress = RequiredProgress;
            CompletionTime = DateTime.UtcNow;

            if (completionData != null)
            {
                CompletionData = completionData;
            }
        }

        public void Fail(string reason = null)
        {
            State = ObjectiveState.Failed;

            if (!string.IsNullOrEmpty(reason))
            {
                Parameters["FailureReason"] = reason;
            }
        }

        public void Reset()
        {
            State = ObjectiveState.Inactive;
            CurrentProgress = 0;
            ActivationTime = null;
            CompletionTime = null;
            CompletionData.Clear();

            if (TimeLimit.HasValue)
            {
                TimeLimitExpiry = null;
            }
        }

        public void SetOptional(bool isOptional) => IsOptional = isOptional;
        public void SetOrderIndex(int index) => OrderIndex = index;
        public void SetTarget(string targetId) => TargetId = targetId;
        public void SetLocation(string locationId) => LocationId = locationId;
        public void SetTimeLimit(TimeSpan timeLimit) => TimeLimit = timeLimit;

        public void AddParameter(string key, object value)
        {
            Parameters[key] = value;
        }

        public void AddPrerequisite(string objectiveId)
        {
            if (!PrerequisiteObjectiveIds.Contains(objectiveId))
            {
                PrerequisiteObjectiveIds.Add(objectiveId);
            }
        }

        public bool ArePrerequisitesMet(IEnumerable<Objective> allObjectives)
        {
            if (PrerequisiteObjectiveIds.Count == 0)
                return true;

            foreach (var prereqId in PrerequisiteObjectiveIds)
            {
                var prereq = allObjectives.FirstOrDefault(o => o.Id == prereqId);
                if (prereq == null || prereq.State != ObjectiveState.Completed)
                    return false;
            }

            return true;
        }

        public ObjectiveData GetData() => new ObjectiveData(this);
    }

    /// <summary>
    /// Data transfer object for objective state;
    /// </summary>
    public class ObjectiveData;
    {
        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public ObjectiveType Type { get; }
        public ObjectiveState State { get; }
        public int CurrentProgress { get; }
        public int RequiredProgress { get; }
        public float ProgressPercentage { get; }
        public bool IsOptional { get; }
        public string TargetId { get; }
        public string LocationId { get; }
        public DateTime? TimeLimitExpiry { get; }
        public bool HasTimeLimit => TimeLimitExpiry.HasValue;
        public TimeSpan? RemainingTime => TimeLimitExpiry.HasValue ?
            TimeLimitExpiry.Value - DateTime.UtcNow : null;

        public ObjectiveData(Objective objective)
        {
            Id = objective.Id;
            Title = objective.Title;
            Description = objective.Description;
            Type = objective.Type;
            State = objective.State;
            CurrentProgress = objective.CurrentProgress;
            RequiredProgress = objective.RequiredProgress;
            ProgressPercentage = objective.ProgressPercentage;
            IsOptional = objective.IsOptional;
            TargetId = objective.TargetId;
            LocationId = objective.LocationId;
            TimeLimitExpiry = objective.TimeLimitExpiry;
        }
    }

    /// <summary>
    /// Event arguments for objective events;
    /// </summary>
    public class ObjectiveEventArgs : EventArgs;
    {
        public string ObjectiveId { get; }
        public string QuestId { get; }
        public ObjectiveState NewState { get; }
        public ObjectiveState PreviousState { get; }
        public Dictionary<string, object> EventData { get; }

        public ObjectiveEventArgs(
            string objectiveId,
            string questId,
            ObjectiveState newState,
            ObjectiveState previousState,
            Dictionary<string, object> eventData = null)
        {
            ObjectiveId = objectiveId;
            QuestId = questId;
            NewState = newState;
            PreviousState = previousState;
            EventData = eventData ?? new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Manages objectives within quests;
    /// </summary>
    public interface IObjectiveSystem;
    {
        Task<Objective> CreateObjectiveAsync(Objective objective);
        Task<Objective> GetObjectiveAsync(string objectiveId);
        Task<IEnumerable<Objective>> GetQuestObjectivesAsync(string questId);
        Task UpdateObjectiveProgressAsync(string objectiveId, int progressDelta, Dictionary<string, object> context = null);
        Task<bool> CompleteObjectiveAsync(string objectiveId, Dictionary<string, object> completionData = null);
        Task<bool> FailObjectiveAsync(string objectiveId, string reason = null);
        Task ResetObjectiveAsync(string objectiveId);
        Task<bool> ArePrerequisitesMetAsync(string objectiveId);
        Task<IEnumerable<ObjectiveData>> GetObjectiveStatusAsync(string questId);

        event EventHandler<ObjectiveEventArgs> ObjectiveActivated;
        event EventHandler<ObjectiveEventArgs> ObjectiveProgressUpdated;
        event EventHandler<ObjectiveEventArgs> ObjectiveCompleted;
        event EventHandler<ObjectiveEventArgs> ObjectiveFailed;
    }

    /// <summary>
    /// Implementation of the objective system;
    /// </summary>
    public class ObjectiveSystem : IObjectiveSystem;
    {
        private readonly ILogger<ObjectiveSystem> _logger;
        private readonly IEventBus _eventBus;
        private readonly Dictionary<string, Objective> _objectives;
        private readonly Dictionary<string, List<string>> _questObjectives;

        public event EventHandler<ObjectiveEventArgs> ObjectiveActivated;
        public event EventHandler<ObjectiveEventArgs> ObjectiveProgressUpdated;
        public event EventHandler<ObjectiveEventArgs> ObjectiveCompleted;
        public event EventHandler<ObjectiveEventArgs> ObjectiveFailed;

        public ObjectiveSystem(ILogger<ObjectiveSystem> logger, IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _objectives = new Dictionary<string, Objective>();
            _questObjectives = new Dictionary<string, List<string>>();
        }

        public async Task<Objective> CreateObjectiveAsync(Objective objective)
        {
            if (objective == null)
                throw new ArgumentNullException(nameof(objective));

            try
            {
                _logger.LogInformation("Creating objective {ObjectiveId} for quest {QuestId}",
                    objective.Id, objective.QuestId);

                // Add to storage;
                _objectives[objective.Id] = objective;

                // Update quest index;
                if (!_questObjectives.ContainsKey(objective.QuestId))
                {
                    _questObjectives[objective.QuestId] = new List<string>();
                }

                if (!_questObjectives[objective.QuestId].Contains(objective.Id))
                {
                    _questObjectives[objective.QuestId].Add(objective.Id);
                }

                _logger.LogDebug("Objective {ObjectiveId} created successfully", objective.Id);

                // Publish event;
                await _eventBus.PublishAsync(new ObjectiveCreatedEvent;
                {
                    ObjectiveId = objective.Id,
                    QuestId = objective.QuestId,
                    Type = objective.Type,
                    Timestamp = DateTime.UtcNow;
                });

                return objective;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create objective {ObjectiveId}", objective.Id);
                throw new ObjectiveSystemException($"Failed to create objective: {objective.Id}", ex);
            }
        }

        public async Task<Objective> GetObjectiveAsync(string objectiveId)
        {
            if (string.IsNullOrEmpty(objectiveId))
                throw new ArgumentException("Objective ID cannot be null or empty", nameof(objectiveId));

            if (!_objectives.TryGetValue(objectiveId, out var objective))
            {
                _logger.LogWarning("Objective not found: {ObjectiveId}", objectiveId);
                return null;
            }

            return objective;
        }

        public async Task<IEnumerable<Objective>> GetQuestObjectivesAsync(string questId)
        {
            if (string.IsNullOrEmpty(questId))
                throw new ArgumentException("Quest ID cannot be null or empty", nameof(questId));

            if (!_questObjectives.TryGetValue(questId, out var objectiveIds))
            {
                return Enumerable.Empty<Objective>();
            }

            return objectiveIds;
                .Select(id => _objectives.GetValueOrDefault(id))
                .Where(obj => obj != null)
                .OrderBy(obj => obj.OrderIndex);
        }

        public async Task UpdateObjectiveProgressAsync(
            string objectiveId,
            int progressDelta,
            Dictionary<string, object> context = null)
        {
            if (string.IsNullOrEmpty(objectiveId))
                throw new ArgumentException("Objective ID cannot be null or empty", nameof(objectiveId));

            try
            {
                var objective = await GetObjectiveAsync(objectiveId);
                if (objective == null)
                {
                    throw new ArgumentException($"Objective not found: {objectiveId}");
                }

                if (objective.State != ObjectiveState.Active)
                {
                    _logger.LogWarning("Cannot update progress for objective in state {State}", objective.State);
                    return;
                }

                var previousProgress = objective.CurrentProgress;
                var previousState = objective.State;

                objective.UpdateProgress(progressDelta, context);

                _logger.LogDebug("Updated progress for objective {ObjectiveId}: {Previous} -> {Current}/{Required}",
                    objectiveId, previousProgress, objective.CurrentProgress, objective.RequiredProgress);

                // Raise event;
                var eventArgs = new ObjectiveEventArgs(
                    objectiveId,
                    objective.QuestId,
                    objective.State,
                    previousState,
                    new Dictionary<string, object>
                    {
                        ["ProgressDelta"] = progressDelta,
                        ["PreviousProgress"] = previousProgress,
                        ["CurrentProgress"] = objective.CurrentProgress,
                        ["Context"] = context;
                    });

                OnObjectiveProgressUpdated(eventArgs);

                // Publish event bus message;
                await _eventBus.PublishAsync(new ObjectiveProgressUpdatedEvent;
                {
                    ObjectiveId = objectiveId,
                    QuestId = objective.QuestId,
                    ProgressDelta = progressDelta,
                    CurrentProgress = objective.CurrentProgress,
                    RequiredProgress = objective.RequiredProgress,
                    ProgressPercentage = objective.ProgressPercentage,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update progress for objective {ObjectiveId}", objectiveId);
                throw new ObjectiveSystemException($"Failed to update objective progress: {objectiveId}", ex);
            }
        }

        public async Task<bool> CompleteObjectiveAsync(string objectiveId, Dictionary<string, object> completionData = null)
        {
            if (string.IsNullOrEmpty(objectiveId))
                throw new ArgumentException("Objective ID cannot be null or empty", nameof(objectiveId));

            try
            {
                var objective = await GetObjectiveAsync(objectiveId);
                if (objective == null)
                {
                    _logger.LogWarning("Objective not found for completion: {ObjectiveId}", objectiveId);
                    return false;
                }

                if (objective.State != ObjectiveState.Active)
                {
                    _logger.LogWarning("Cannot complete objective in state {State}", objective.State);
                    return false;
                }

                var previousState = objective.State;

                objective.Complete(completionData);

                _logger.LogInformation("Objective completed: {ObjectiveId}", objectiveId);

                // Raise event;
                var eventArgs = new ObjectiveEventArgs(
                    objectiveId,
                    objective.QuestId,
                    objective.State,
                    previousState,
                    completionData);

                OnObjectiveCompleted(eventArgs);

                // Publish event bus message;
                await _eventBus.PublishAsync(new ObjectiveCompletedEvent;
                {
                    ObjectiveId = objectiveId,
                    QuestId = objective.QuestId,
                    Type = objective.Type,
                    CompletionData = completionData,
                    Timestamp = DateTime.UtcNow;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete objective {ObjectiveId}", objectiveId);
                throw new ObjectiveSystemException($"Failed to complete objective: {objectiveId}", ex);
            }
        }

        public async Task<bool> FailObjectiveAsync(string objectiveId, string reason = null)
        {
            if (string.IsNullOrEmpty(objectiveId))
                throw new ArgumentException("Objective ID cannot be null or empty", nameof(objectiveId));

            try
            {
                var objective = await GetObjectiveAsync(objectiveId);
                if (objective == null)
                {
                    _logger.LogWarning("Objective not found for failure: {ObjectiveId}", objectiveId);
                    return false;
                }

                if (objective.State != ObjectiveState.Active)
                {
                    _logger.LogWarning("Cannot fail objective in state {State}", objective.State);
                    return false;
                }

                var previousState = objective.State;

                objective.Fail(reason);

                _logger.LogInformation("Objective failed: {ObjectiveId} - Reason: {Reason}",
                    objectiveId, reason ?? "Unknown");

                // Raise event;
                var eventArgs = new ObjectiveEventArgs(
                    objectiveId,
                    objective.QuestId,
                    objective.State,
                    previousState,
                    new Dictionary<string, object>
                    {
                        ["FailureReason"] = reason;
                    });

                OnObjectiveFailed(eventArgs);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fail objective {ObjectiveId}", objectiveId);
                throw new ObjectiveSystemException($"Failed to fail objective: {objectiveId}", ex);
            }
        }

        public async Task ResetObjectiveAsync(string objectiveId)
        {
            if (string.IsNullOrEmpty(objectiveId))
                throw new ArgumentException("Objective ID cannot be null or empty", nameof(objectiveId));

            try
            {
                var objective = await GetObjectiveAsync(objectiveId);
                if (objective == null)
                {
                    throw new ArgumentException($"Objective not found: {objectiveId}");
                }

                objective.Reset();

                _logger.LogDebug("Objective reset: {ObjectiveId}", objectiveId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset objective {ObjectiveId}", objectiveId);
                throw new ObjectiveSystemException($"Failed to reset objective: {objectiveId}", ex);
            }
        }

        public async Task<bool> ArePrerequisitesMetAsync(string objectiveId)
        {
            if (string.IsNullOrEmpty(objectiveId))
                throw new ArgumentException("Objective ID cannot be null or empty", nameof(objectiveId));

            var objective = await GetObjectiveAsync(objectiveId);
            if (objective == null)
            {
                return false;
            }

            if (objective.PrerequisiteObjectiveIds.Count == 0)
            {
                return true;
            }

            var allObjectives = await GetQuestObjectivesAsync(objective.QuestId);
            return objective.ArePrerequisitesMet(allObjectives);
        }

        public async Task<IEnumerable<ObjectiveData>> GetObjectiveStatusAsync(string questId)
        {
            if (string.IsNullOrEmpty(questId))
                throw new ArgumentException("Quest ID cannot be null or empty", nameof(questId));

            var objectives = await GetQuestObjectivesAsync(questId);
            return objectives.Select(obj => obj.GetData());
        }

        protected virtual void OnObjectiveActivated(ObjectiveEventArgs e)
        {
            ObjectiveActivated?.Invoke(this, e);
        }

        protected virtual void OnObjectiveProgressUpdated(ObjectiveEventArgs e)
        {
            ObjectiveProgressUpdated?.Invoke(this, e);
        }

        protected virtual void OnObjectiveCompleted(ObjectiveEventArgs e)
        {
            ObjectiveCompleted?.Invoke(this, e);
        }

        protected virtual void OnObjectiveFailed(ObjectiveEventArgs e)
        {
            ObjectiveFailed?.Invoke(this, e);
        }

        public async Task ActivateObjectiveAsync(string objectiveId)
        {
            var objective = await GetObjectiveAsync(objectiveId);
            if (objective == null || objective.State != ObjectiveState.Inactive)
                return;

            var prerequisitesMet = await ArePrerequisitesMetAsync(objectiveId);
            if (!prerequisitesMet)
            {
                _logger.LogWarning("Prerequisites not met for objective {ObjectiveId}", objectiveId);
                return;
            }

            var previousState = objective.State;
            objective.Activate();

            _logger.LogInformation("Objective activated: {ObjectiveId}", objectiveId);

            // Raise event;
            var eventArgs = new ObjectiveEventArgs(
                objectiveId,
                objective.QuestId,
                objective.State,
                previousState);

            OnObjectiveActivated(eventArgs);

            // Publish event bus message;
            await _eventBus.PublishAsync(new ObjectiveActivatedEvent;
            {
                ObjectiveId = objectiveId,
                QuestId = objective.QuestId,
                Type = objective.Type,
                Timestamp = DateTime.UtcNow;
            });
        }

        public async Task<IEnumerable<Objective>> GetActiveObjectivesAsync(string questId)
        {
            var objectives = await GetQuestObjectivesAsync(questId);
            return objectives.Where(obj => obj.State == ObjectiveState.Active);
        }

        public async Task<IEnumerable<Objective>> GetCompletedObjectivesAsync(string questId)
        {
            var objectives = await GetQuestObjectivesAsync(questId);
            return objectives.Where(obj => obj.State == ObjectiveState.Completed);
        }

        public async Task<Dictionary<string, int>> GetQuestProgressAsync(string questId)
        {
            var objectives = await GetQuestObjectivesAsync(questId);
            var requiredObjectives = objectives.Where(obj => !obj.IsOptional).ToList();

            if (requiredObjectives.Count == 0)
                return new Dictionary<string, int>();

            var completed = requiredObjectives.Count(obj => obj.State == ObjectiveState.Completed);
            var active = requiredObjectives.Count(obj => obj.State == ObjectiveState.Active);
            var failed = requiredObjectives.Count(obj => obj.State == ObjectiveState.Failed);

            return new Dictionary<string, int>
            {
                ["Total"] = requiredObjectives.Count,
                ["Completed"] = completed,
                ["Active"] = active,
                ["Failed"] = failed,
                ["Remaining"] = requiredObjectives.Count - completed - failed;
            };
        }
    }

    /// <summary>
    /// Custom exception for objective system errors;
    /// </summary>
    public class ObjectiveSystemException : Exception
    {
        public string ObjectiveId { get; }

        public ObjectiveSystemException(string message) : base(message) { }

        public ObjectiveSystemException(string message, Exception innerException)
            : base(message, innerException) { }

        public ObjectiveSystemException(string message, string objectiveId, Exception innerException = null)
            : base(message, innerException)
        {
            ObjectiveId = objectiveId;
        }
    }

    /// <summary>
    /// Event for objective creation;
    /// </summary>
    public class ObjectiveCreatedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string ObjectiveId { get; set; }
        public string QuestId { get; set; }
        public ObjectiveType Type { get; set; }
    }

    /// <summary>
    /// Event for objective activation;
    /// </summary>
    public class ObjectiveActivatedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string ObjectiveId { get; set; }
        public string QuestId { get; set; }
        public ObjectiveType Type { get; set; }
    }

    /// <summary>
    /// Event for objective progress update;
    /// </summary>
    public class ObjectiveProgressUpdatedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string ObjectiveId { get; set; }
        public string QuestId { get; set; }
        public int ProgressDelta { get; set; }
        public int CurrentProgress { get; set; }
        public int RequiredProgress { get; set; }
        public float ProgressPercentage { get; set; }
    }

    /// <summary>
    /// Event for objective completion;
    /// </summary>
    public class ObjectiveCompletedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string ObjectiveId { get; set; }
        public string QuestId { get; set; }
        public ObjectiveType Type { get; set; }
        public Dictionary<string, object> CompletionData { get; set; }
    }

    /// <summary>
    /// Factory for creating objectives;
    /// </summary>
    public class ObjectiveFactory;
    {
        private readonly ILogger<ObjectiveFactory> _logger;

        public ObjectiveFactory(ILogger<ObjectiveFactory> logger)
        {
            _logger = logger;
        }

        public Objective CreateCollectObjective(
            string questId,
            string title,
            string itemId,
            int requiredAmount,
            bool isOptional = false)
        {
            var objective = new Objective(
                Guid.NewGuid().ToString(),
                questId,
                title,
                ObjectiveType.Collect,
                requiredAmount,
                isOptional);

            objective.SetTarget(itemId);
            objective.AddParameter("ItemId", itemId);
            objective.AddParameter("RequiredAmount", requiredAmount);

            _logger.LogDebug("Created collect objective for item {ItemId}", itemId);

            return objective;
        }

        public Objective CreateKillObjective(
            string questId,
            string title,
            string enemyType,
            int requiredKills,
            bool isOptional = false)
        {
            var objective = new Objective(
                Guid.NewGuid().ToString(),
                questId,
                title,
                ObjectiveType.Kill,
                requiredKills,
                isOptional);

            objective.SetTarget(enemyType);
            objective.AddParameter("EnemyType", enemyType);
            objective.AddParameter("RequiredKills", requiredKills);

            _logger.LogDebug("Created kill objective for enemy {EnemyType}", enemyType);

            return objective;
        }

        public Objective CreateDeliveryObjective(
            string questId,
            string title,
            string npcId,
            string itemId,
            bool isOptional = false)
        {
            var objective = new Objective(
                Guid.NewGuid().ToString(),
                questId,
                title,
                ObjectiveType.Deliver,
                1,
                isOptional);

            objective.SetTarget(npcId);
            objective.AddParameter("NPCId", npcId);
            objective.AddParameter("ItemId", itemId);

            _logger.LogDebug("Created delivery objective to NPC {NPCId}", npcId);

            return objective;
        }

        public Objective CreateTimeLimitObjective(
            string questId,
            string title,
            TimeSpan timeLimit,
            bool isOptional = false)
        {
            var objective = new Objective(
                Guid.NewGuid().ToString(),
                questId,
                title,
                ObjectiveType.TimeLimit,
                1,
                isOptional);

            objective.SetTimeLimit(timeLimit);
            objective.AddParameter("TimeLimit", timeLimit);

            _logger.LogDebug("Created time-limited objective with limit {TimeLimit}", timeLimit);

            return objective;
        }

        public Objective CreateCustomObjective(
            string questId,
            string title,
            string customType,
            Dictionary<string, object> parameters,
            bool isOptional = false)
        {
            var objective = new Objective(
                Guid.NewGuid().ToString(),
                questId,
                title,
                ObjectiveType.Custom,
                1,
                isOptional);

            foreach (var param in parameters)
            {
                objective.AddParameter(param.Key, param.Value);
            }

            objective.AddParameter("CustomType", customType);

            _logger.LogDebug("Created custom objective of type {CustomType}", customType);

            return objective;
        }
    }

    /// <summary>
    /// Service registration extension;
    /// </summary>
    public static class ObjectiveSystemExtensions;
    {
        public static IServiceCollection AddObjectiveSystem(this IServiceCollection services)
        {
            services.AddScoped<IObjectiveSystem, ObjectiveSystem>();
            services.AddSingleton<ObjectiveFactory>();

            services.AddTransient<IEventBusSubscriber, ObjectiveEventSubscriber>();

            return services;
        }
    }

    /// <summary>
    /// Event subscriber for objective events;
    /// </summary>
    public class ObjectiveEventSubscriber : IEventBusSubscriber;
    {
        private readonly ILogger<ObjectiveEventSubscriber> _logger;
        private readonly IObjectiveSystem _objectiveSystem;

        public ObjectiveEventSubscriber(
            ILogger<ObjectiveEventSubscriber> logger,
            IObjectiveSystem objectiveSystem)
        {
            _logger = logger;
            _objectiveSystem = objectiveSystem;
        }

        public void Subscribe(IEventBus eventBus)
        {
            eventBus.Subscribe<ObjectiveCompletedEvent>(HandleObjectiveCompleted);
            eventBus.Subscribe<ObjectiveProgressUpdatedEvent>(HandleProgressUpdated);
        }

        private async Task HandleObjectiveCompleted(ObjectiveCompletedEvent @event)
        {
            _logger.LogInformation("Objective completed: {@Event}", @event);

            // Additional processing logic can be added here;
            // Example: Update quest progress, trigger rewards, etc.
            await Task.CompletedTask;
        }

        private async Task HandleProgressUpdated(ObjectiveProgressUpdatedEvent @event)
        {
            _logger.LogDebug("Objective progress updated: {@Event}", @event);

            // Real-time progress tracking logic;
            // Example: Update UI, send notifications, etc.
            await Task.CompletedTask;
        }
    }
}
