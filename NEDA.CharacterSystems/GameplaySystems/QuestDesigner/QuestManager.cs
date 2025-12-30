using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.ProjectService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.GameplaySystems.QuestDesigner.QuestManager;

namespace NEDA.CharacterSystems.GameplaySystems.QuestDesigner;
{
    /// <summary>
    /// Comprehensive quest management system for handling quest lifecycle, tracking, and progression.
    /// Supports main quests, side quests, daily quests, and event-based quests.
    /// </summary>
    public class QuestManager : IDisposable
    {
        #region Constants;

        private const int MAX_ACTIVE_QUESTS = 25;
        private const int MAX_COMPLETED_QUESTS_HISTORY = 100;
        private const int QUEST_UPDATE_INTERVAL_MS = 1000;
        private const string DEFAULT_QUEST_CATEGORY = "General";

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IProjectManager _projectManager;
        private readonly ConcurrentDictionary<string, Quest> _activeQuests;
        private readonly ConcurrentDictionary<string, Quest> _completedQuests;
        private readonly ConcurrentDictionary<string, QuestTemplate> _questTemplates;
        private readonly ConcurrentDictionary<string, QuestChain> _questChains;
        private readonly ConcurrentDictionary<string, QuestObjective> _trackedObjectives;
        private readonly object _syncLock = new object();
        private bool _disposed = false;
        private System.Timers.Timer _updateTimer;
        private bool _isInitialized = false;

        #endregion;

        #region Properties;

        /// <summary>
        /// Total number of active quests;
        /// </summary>
        public int ActiveQuestCount => _activeQuests.Count;

        /// <summary>
        /// Total number of completed quests;
        /// </summary>
        public int CompletedQuestCount => _completedQuests.Count;

        /// <summary>
        /// Number of available quest templates;
        /// </summary>
        public int TemplateCount => _questTemplates.Count;

        /// <summary>
        /// Current player's quest log capacity;
        /// </summary>
        public int QuestLogCapacity { get; private set; } = MAX_ACTIVE_QUESTS;

        /// <summary>
        /// Quest manager configuration;
        /// </summary>
        public QuestManagerConfiguration Configuration { get; private set; }

        /// <summary>
        /// Player's current quest statistics;
        /// </summary>
        public QuestStatistics Statistics { get; private set; }

        /// <summary>
        /// Current quest filters and settings;
        /// </summary>
        public QuestFilters ActiveFilters { get; private set; }

        /// <summary>
        /// Available quest categories;
        /// </summary>
        public List<string> QuestCategories { get; private set; }

        /// <summary>
        /// Current session quest data;
        /// </summary>
        public QuestSessionData SessionData { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Raised when a new quest is accepted;
        /// </summary>
        public event EventHandler<QuestAcceptedEventArgs> QuestAccepted;

        /// <summary>
        /// Raised when a quest is completed;
        /// </summary>
        public event EventHandler<QuestCompletedEventArgs> QuestCompleted;

        /// <summary>
        /// Raised when a quest is failed;
        /// </summary>
        public event EventHandler<QuestFailedEventArgs> QuestFailed;

        /// <summary>
        /// Raised when quest progress is updated;
        /// </summary>
        public event EventHandler<QuestProgressEventArgs> QuestProgressUpdated;

        /// <summary>
        /// Raised when a quest objective is completed;
        /// </summary>
        public event EventHandler<ObjectiveCompletedEventArgs> ObjectiveCompleted;

        /// <summary>
        /// Raised when a quest is abandoned;
        /// </summary>
        public event EventHandler<QuestAbandonedEventArgs> QuestAbandoned;

        /// <summary>
        /// Raised when quest rewards are claimed;
        /// </summary>
        public event EventHandler<RewardsClaimedEventArgs> RewardsClaimed;

        /// <summary>
        /// Raised when quest chain progresses;
        /// </summary>
        public event EventHandler<QuestChainUpdatedEventArgs> QuestChainUpdated;

        /// <summary>
        /// Raised when daily quests reset;
        /// </summary>
        public event EventHandler<DailyQuestsResetEventArgs> DailyQuestsReset;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new QuestManager instance;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="projectManager">Project manager for saving/loading</param>
        public QuestManager(ILogger logger = null, IProjectManager projectManager = null)
        {
            _logger = logger ?? LoggerFactory.CreateLogger(nameof(QuestManager));
            _projectManager = projectManager;

            _activeQuests = new ConcurrentDictionary<string, Quest>();
            _completedQuests = new ConcurrentDictionary<string, Quest>();
            _questTemplates = new ConcurrentDictionary<string, QuestTemplate>();
            _questChains = new ConcurrentDictionary<string, QuestChain>();
            _trackedObjectives = new ConcurrentDictionary<string, QuestObjective>();

            InitializeDefaults();
            InitializeTimer();
        }

        /// <summary>
        /// Initializes QuestManager with custom configuration;
        /// </summary>
        public QuestManager(QuestManagerConfiguration configuration, ILogger logger = null,
                          IProjectManager projectManager = null)
            : this(logger, projectManager)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            ApplyConfiguration();
        }

        #endregion;

        #region Public Methods - Quest Management;

        /// <summary>
        /// Initializes the quest manager with templates and data;
        /// </summary>
        /// <param name="templates">Quest templates to load</param>
        /// <param name="chains">Quest chains to load</param>
        /// <param name="saveData">Saved quest data (optional)</param>
        public async Task InitializeAsync(IEnumerable<QuestTemplate> templates = null,
                                        IEnumerable<QuestChain> chains = null,
                                        QuestSaveData saveData = null)
        {
            ValidateNotDisposed();

            try
            {
                _logger.LogInformation("Initializing QuestManager...");

                // Load templates;
                if (templates != null)
                {
                    foreach (var template in templates)
                    {
                        await RegisterQuestTemplateAsync(template);
                    }
                }

                // Load chains;
                if (chains != null)
                {
                    foreach (var chain in chains)
                    {
                        await RegisterQuestChainAsync(chain);
                    }
                }

                // Load saved data;
                if (saveData != null)
                {
                    await LoadSaveDataAsync(saveData);
                }

                // Load default templates if none provided;
                if (_questTemplates.IsEmpty)
                {
                    await LoadDefaultTemplatesAsync();
                }

                _isInitialized = true;
                _logger.LogInformation($"QuestManager initialized. Templates: {TemplateCount}, Chains: {_questChains.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize QuestManager");
                throw new QuestInitializationException("Failed to initialize QuestManager", ex);
            }
        }

        /// <summary>
        /// Accepts a new quest;
        /// </summary>
        /// <param name="questId">Quest identifier</param>
        /// <param name="source">Source of the quest</param>
        /// <returns>Accepted quest instance</returns>
        public async Task<Quest> AcceptQuestAsync(string questId, string source = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(questId, nameof(questId));

            try
            {
                _logger.LogInformation($"Attempting to accept quest: {questId}");

                // Check if already has quest;
                if (_activeQuests.ContainsKey(questId))
                {
                    throw new QuestOperationException($"Quest '{questId}' is already active");
                }

                if (_completedQuests.ContainsKey(questId))
                {
                    throw new QuestOperationException($"Quest '{questId}' has already been completed");
                }

                // Check quest log capacity;
                if (_activeQuests.Count >= QuestLogCapacity)
                {
                    throw new QuestLogFullException($"Quest log is full ({_activeQuests.Count}/{QuestLogCapacity})");
                }

                // Get or create quest from template;
                var quest = await CreateQuestFromTemplateAsync(questId);
                if (quest == null)
                {
                    throw new QuestNotFoundException($"Quest template not found: {questId}");
                }

                // Check prerequisites;
                if (!await CheckPrerequisitesAsync(quest))
                {
                    throw new QuestPrerequisiteException($"Prerequisites not met for quest: {questId}");
                }

                // Start the quest;
                quest.Status = QuestStatus.InProgress;
                quest.AcceptedTime = DateTime.UtcNow;
                quest.Source = source;

                // Add to active quests;
                if (!_activeQuests.TryAdd(quest.Id, quest))
                {
                    throw new QuestOperationException($"Failed to add quest '{questId}' to active quests");
                }

                // Update statistics;
                UpdateStatistics(quest, StatisticUpdateType.QuestAccepted);

                // Start tracking objectives;
                await StartTrackingObjectivesAsync(quest);

                // Check if part of a chain;
                await ProcessQuestChainOnAcceptAsync(quest);

                // Raise event;
                OnQuestAccepted(quest, source);

                _logger.LogInformation($"Quest accepted: {quest.Name} (ID: {quest.Id})");

                return quest;
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error accepting quest: {questId}");
                throw new QuestOperationException($"Failed to accept quest: {questId}", ex);
            }
        }

        /// <summary>
        /// Completes a quest;
        /// </summary>
        /// <param name="questId">Quest identifier</param>
        /// <param name="completionData">Additional completion data</param>
        /// <returns>Completion result with rewards</returns>
        public async Task<QuestCompletionResult> CompleteQuestAsync(string questId,
                                                                  QuestCompletionData completionData = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(questId, nameof(questId));

            try
            {
                _logger.LogInformation($"Attempting to complete quest: {questId}");

                // Get quest;
                if (!_activeQuests.TryGetValue(questId, out var quest))
                {
                    throw new QuestNotFoundException($"Active quest not found: {questId}");
                }

                // Validate completion;
                if (!await CanCompleteQuestAsync(quest))
                {
                    throw new QuestCompletionException($"Cannot complete quest '{questId}'. Objectives not met.");
                }

                // Update quest status;
                quest.Status = QuestStatus.Completed;
                quest.CompletedTime = DateTime.UtcNow;
                quest.CompletionData = completionData;

                // Calculate rewards;
                var rewards = await CalculateRewardsAsync(quest, completionData);

                // Move from active to completed;
                if (!_activeQuests.TryRemove(questId, out _))
                {
                    throw new QuestOperationException($"Failed to remove quest '{questId}' from active quests");
                }

                // Add to completed quests (with limit)
                await AddToCompletedQuestsAsync(quest);

                // Stop tracking objectives;
                await StopTrackingObjectivesAsync(quest);

                // Update statistics;
                UpdateStatistics(quest, StatisticUpdateType.QuestCompleted);

                // Process quest chain;
                await ProcessQuestChainOnCompleteAsync(quest);

                // Create completion result;
                var result = new QuestCompletionResult;
                {
                    Quest = quest,
                    Rewards = rewards,
                    CompletionTime = DateTime.UtcNow,
                    BonusMultipliers = completionData?.BonusMultipliers;
                };

                // Raise events;
                OnQuestCompleted(quest, result);
                if (reards?.Count > 0)
                {
                    OnRewardsClaimed(quest, rewards);
                }

                _logger.LogInformation($"Quest completed: {quest.Name} (ID: {quest.Id})");

                return result;
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error completing quest: {questId}");
                throw new QuestOperationException($"Failed to complete quest: {questId}", ex);
            }
        }

        /// <summary>
        /// Updates quest progress for specific objective;
        /// </summary>
        /// <param name="objectiveId">Objective identifier</param>
        /// <param name="progressAmount">Progress amount to add</param>
        /// <param name="progressData">Additional progress data</param>
        /// <returns>Updated objective progress</returns>
        public async Task<ObjectiveProgressResult> UpdateObjectiveProgressAsync(string objectiveId,
                                                                              int progressAmount = 1,
                                                                              Dictionary<string, object> progressData = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(objectiveId, nameof(objectiveId));

            try
            {
                // Get tracked objective;
                if (!_trackedObjectives.TryGetValue(objectiveId, out var objective))
                {
                    _logger.LogDebug($"Objective not tracked: {objectiveId}");
                    return null;
                }

                // Get parent quest;
                if (!_activeQuests.TryGetValue(objective.QuestId, out var quest))
                {
                    _logger.LogWarning($"Parent quest not found for objective: {objectiveId}");
                    _trackedObjectives.TryRemove(objectiveId, out _);
                    return null;
                }

                // Update objective progress;
                var previousProgress = objective.CurrentProgress;
                objective.CurrentProgress = Math.Min(objective.TargetProgress,
                                                   objective.CurrentProgress + progressAmount);

                // Update progress data;
                objective.ProgressData = progressData ?? objective.ProgressData;
                objective.LastUpdated = DateTime.UtcNow;

                // Check if objective completed;
                bool objectiveCompleted = false;
                if (objective.CurrentProgress >= objective.TargetProgress && !objective.IsCompleted)
                {
                    objective.IsCompleted = true;
                    objective.CompletedTime = DateTime.UtcNow;
                    objectiveCompleted = true;

                    _logger.LogDebug($"Objective completed: {objective.Name} ({objective.Id})");

                    // Raise objective completed event;
                    OnObjectiveCompleted(objective, quest);
                }

                // Check if all objectives completed;
                bool questCompletable = false;
                if (quest.Objectives.All(o => o.IsCompleted) && quest.Status == QuestStatus.InProgress)
                {
                    questCompletable = true;
                    quest.Status = QuestStatus.ReadyForCompletion;

                    _logger.LogDebug($"Quest ready for completion: {quest.Name}");
                }

                // Create result;
                var result = new ObjectiveProgressResult;
                {
                    Objective = objective,
                    Quest = quest,
                    PreviousProgress = previousProgress,
                    NewProgress = objective.CurrentProgress,
                    ObjectiveCompleted = objectiveCompleted,
                    QuestCompletable = questCompletable,
                    ProgressData = progressData;
                };

                // Raise progress updated event;
                OnQuestProgressUpdated(quest, objective, result);

                _logger.LogTrace($"Objective progress updated: {objectiveId} ({previousProgress} -> {objective.CurrentProgress})");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating objective progress: {objectiveId}");
                throw new QuestOperationException($"Failed to update objective progress: {objectiveId}", ex);
            }
        }

        /// <summary>
        /// Abandons an active quest;
        /// </summary>
        /// <param name="questId">Quest identifier</param>
        /// <param name="reason">Abandon reason</param>
        /// <returns>True if successfully abandoned</returns>
        public async Task<bool> AbandonQuestAsync(string questId, string reason = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(questId, nameof(questId));

            try
            {
                _logger.LogInformation($"Attempting to abandon quest: {questId}");

                // Get quest;
                if (!_activeQuests.TryGetValue(questId, out var quest))
                {
                    throw new QuestNotFoundException($"Active quest not found: {questId}");
                }

                // Check if can be abandoned;
                if (!quest.CanAbandon)
                {
                    throw new QuestOperationException($"Quest '{questId}' cannot be abandoned");
                }

                // Update quest status;
                quest.Status = QuestStatus.Abandoned;
                quest.AbandonedTime = DateTime.UtcNow;
                quest.AbandonReason = reason;

                // Remove from active quests;
                if (!_activeQuests.TryRemove(questId, out _))
                {
                    throw new QuestOperationException($"Failed to remove quest '{questId}' from active quests");
                }

                // Stop tracking objectives;
                await StopTrackingObjectivesAsync(quest);

                // Update statistics;
                UpdateStatistics(quest, StatisticUpdateType.QuestAbandoned);

                // Raise event;
                OnQuestAbandoned(quest, reason);

                _logger.LogInformation($"Quest abandoned: {quest.Name} (ID: {quest.Id}) - Reason: {reason}");

                return true;
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error abandoning quest: {questId}");
                throw new QuestOperationException($"Failed to abandon quest: {questId}", ex);
            }
        }

        /// <summary>
        /// Fails a quest;
        /// </summary>
        /// <param name="questId">Quest identifier</param>
        /// <param name="failureReason">Failure reason</param>
        /// <returns>True if successfully failed</returns>
        public async Task<bool> FailQuestAsync(string questId, string failureReason)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ArgumentValidator.ThrowIfNullOrEmpty(questId, nameof(questId));
            ArgumentValidator.ThrowIfNullOrEmpty(failureReason, nameof(failureReason));

            try
            {
                _logger.LogInformation($"Failing quest: {questId} - Reason: {failureReason}");

                // Get quest;
                if (!_activeQuests.TryGetValue(questId, out var quest))
                {
                    throw new QuestNotFoundException($"Active quest not found: {questId}");
                }

                // Update quest status;
                quest.Status = QuestStatus.Failed;
                quest.FailedTime = DateTime.UtcNow;
                quest.FailureReason = failureReason;

                // Remove from active quests;
                if (!_activeQuests.TryRemove(questId, out _))
                {
                    throw new QuestOperationException($"Failed to remove quest '{questId}' from active quests");
                }

                // Stop tracking objectives;
                await StopTrackingObjectivesAsync(quest);

                // Update statistics;
                UpdateStatistics(quest, StatisticUpdateType.QuestFailed);

                // Raise event;
                OnQuestFailed(quest, failureReason);

                _logger.LogInformation($"Quest failed: {quest.Name} (ID: {quest.Id}) - Reason: {failureReason}");

                return true;
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error failing quest: {questId}");
                throw new QuestOperationException($"Failed to fail quest: {questId}", ex);
            }
        }

        #endregion;

        #region Public Methods - Quest Querying;

        /// <summary>
        /// Gets all active quests;
        /// </summary>
        /// <param name="filters">Optional filters to apply</param>
        /// <returns>List of active quests</returns>
        public List<Quest> GetActiveQuests(QuestFilters filters = null)
        {
            ValidateNotDisposed();

            var quests = _activeQuests.Values.ToList();

            if (filters != null)
            {
                quests = ApplyFilters(quests, filters);
            }

            return quests;
        }

        /// <summary>
        /// Gets all completed quests;
        /// </summary>
        /// <param name="limit">Maximum number to return</param>
        /// <returns>List of completed quests</returns>
        public List<Quest> GetCompletedQuests(int limit = 50)
        {
            ValidateNotDisposed();

            return _completedQuests.Values;
                .OrderByDescending(q => q.CompletedTime)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Gets quest by ID;
        /// </summary>
        /// <param name="questId">Quest identifier</param>
        /// <returns>Quest instance or null</returns>
        public Quest GetQuest(string questId)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNullOrEmpty(questId, nameof(questId));

            if (_activeQuests.TryGetValue(questId, out var activeQuest))
            {
                return activeQuest;
            }

            if (_completedQuests.TryGetValue(questId, out var completedQuest))
            {
                return completedQuest;
            }

            return null;
        }

        /// <summary>
        /// Gets available quests (not yet accepted)
        /// </summary>
        /// <param name="playerLevel">Current player level</param>
        /// <returns>List of available quests</returns>
        public async Task<List<QuestTemplate>> GetAvailableQuestsAsync(int playerLevel)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var availableQuests = new List<QuestTemplate>();

            foreach (var template in _questTemplates.Values)
            {
                // Check if already completed;
                if (_completedQuests.ContainsKey(template.Id))
                    continue;

                // Check if already active;
                if (_activeQuests.ContainsKey(template.Id))
                    continue;

                // Check level requirement;
                if (playerLevel < template.MinLevel ||
                    (template.MaxLevel > 0 && playerLevel > template.MaxLevel))
                    continue;

                // Check prerequisites;
                if (await CheckTemplatePrerequisitesAsync(template))
                {
                    availableQuests.Add(template);
                }
            }

            return availableQuests;
        }

        /// <summary>
        /// Gets quests by category;
        /// </summary>
        /// <param name="category">Category name</param>
        /// <returns>Quests in category</returns>
        public List<Quest> GetQuestsByCategory(string category)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNullOrEmpty(category, nameof(category));

            return _activeQuests.Values;
                .Where(q => q.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Gets quests by type;
        /// </summary>
        /// <param name="type">Quest type</param>
        /// <returns>Quests of specified type</returns>
        public List<Quest> GetQuestsByType(QuestType type)
        {
            ValidateNotDisposed();

            return _activeQuests.Values;
                .Where(q => q.Type == type)
                .ToList();
        }

        /// <summary>
        /// Gets quest chain progress;
        /// </summary>
        /// <param name="chainId">Chain identifier</param>
        /// <returns>Chain progress information</returns>
        public QuestChainProgress GetQuestChainProgress(string chainId)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNullOrEmpty(chainId, nameof(chainId));

            if (!_questChains.TryGetValue(chainId, out var chain))
            {
                return null;
            }

            var completedInChain = _completedQuests.Values;
                .Where(q => q.ChainId == chainId)
                .ToList();

            var currentQuestInChain = _activeQuests.Values;
                .FirstOrDefault(q => q.ChainId == chainId);

            return new QuestChainProgress;
            {
                Chain = chain,
                CompletedQuests = completedInChain,
                CurrentQuest = currentQuestInChain,
                ProgressPercentage = completedInChain.Count * 100 / Math.Max(1, chain.QuestIds.Count),
                IsChainComplete = completedInChain.Count >= chain.QuestIds.Count;
            };
        }

        /// <summary>
        /// Gets quest statistics;
        /// </summary>
        /// <returns>Current statistics</returns>
        public QuestStatistics GetStatistics()
        {
            ValidateNotDisposed();

            return Statistics.Clone();
        }

        #endregion;

        #region Public Methods - Template Management;

        /// <summary>
        /// Registers a new quest template;
        /// </summary>
        /// <param name="template">Quest template</param>
        public async Task RegisterQuestTemplateAsync(QuestTemplate template)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNull(template, nameof(template));

            try
            {
                if (_questTemplates.ContainsKey(template.Id))
                {
                    throw new QuestOperationException($"Quest template already registered: {template.Id}");
                }

                // Validate template;
                await ValidateQuestTemplateAsync(template);

                if (!_questTemplates.TryAdd(template.Id, template))
                {
                    throw new QuestOperationException($"Failed to register quest template: {template.Id}");
                }

                // Add category if new;
                if (!string.IsNullOrEmpty(template.Category) &&
                    !QuestCategories.Contains(template.Category))
                {
                    QuestCategories.Add(template.Category);
                }

                _logger.LogInformation($"Quest template registered: {template.Name} (ID: {template.Id})");
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error registering quest template: {template.Id}");
                throw new QuestOperationException($"Failed to register quest template: {template.Id}", ex);
            }
        }

        /// <summary>
        /// Unregisters a quest template;
        /// </summary>
        /// <param name="templateId">Template identifier</param>
        public async Task<bool> UnregisterQuestTemplateAsync(string templateId)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNullOrEmpty(templateId, nameof(templateId));

            try
            {
                if (!_questTemplates.ContainsKey(templateId))
                {
                    _logger.LogWarning($"Quest template not found: {templateId}");
                    return false;
                }

                // Check if any active quests use this template;
                if (_activeQuests.Values.Any(q => q.TemplateId == templateId))
                {
                    throw new QuestOperationException($"Cannot unregister template '{templateId}' with active quests");
                }

                return _questTemplates.TryRemove(templateId, out _);
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error unregistering quest template: {templateId}");
                throw new QuestOperationException($"Failed to unregister quest template: {templateId}", ex);
            }
        }

        /// <summary>
        /// Gets quest template by ID;
        /// </summary>
        /// <param name="templateId">Template identifier</param>
        /// <returns>Quest template or null</returns>
        public QuestTemplate GetQuestTemplate(string templateId)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNullOrEmpty(templateId, nameof(templateId));

            _questTemplates.TryGetValue(templateId, out var template);
            return template;
        }

        /// <summary>
        /// Registers a quest chain;
        /// </summary>
        /// <param name="chain">Quest chain</param>
        public async Task RegisterQuestChainAsync(QuestChain chain)
        {
            ValidateNotDisposed();
            ArgumentValidator.ThrowIfNull(chain, nameof(chain));

            try
            {
                if (_questChains.ContainsKey(chain.Id))
                {
                    throw new QuestOperationException($"Quest chain already registered: {chain.Id}");
                }

                // Validate chain;
                await ValidateQuestChainAsync(chain);

                if (!_questChains.TryAdd(chain.Id, chain))
                {
                    throw new QuestOperationException($"Failed to register quest chain: {chain.Id}");
                }

                _logger.LogInformation($"Quest chain registered: {chain.Name} (ID: {chain.Id})");
            }
            catch (Exception ex) when (!(ex is QuestOperationException))
            {
                _logger.LogError(ex, $"Error registering quest chain: {chain.Id}");
                throw new QuestOperationException($"Failed to register quest chain: {chain.Id}", ex);
            }
        }

        #endregion;

        #region Public Methods - Utility;

        /// <summary>
        /// Saves current quest state;
        /// </summary>
        /// <returns>Save data</returns>
        public async Task<QuestSaveData> SaveAsync()
        {
            ValidateNotDisposed();

            try
            {
                var saveData = new QuestSaveData;
                {
                    ActiveQuests = _activeQuests.Values.ToList(),
                    CompletedQuests = _completedQuests.Values.ToList(),
                    Statistics = Statistics.Clone(),
                    SessionData = SessionData.Clone(),
                    Configuration = Configuration.Clone(),
                    SavedTime = DateTime.UtcNow;
                };

                // Save to project manager if available;
                if (_projectManager != null)
                {
                    await _projectManager.SaveDataAsync("quest_system", saveData);
                }

                _logger.LogDebug("Quest state saved");

                return saveData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving quest state");
                throw new QuestOperationException("Failed to save quest state", ex);
            }
        }

        /// <summary>
        /// Resets daily quests;
        /// </summary>
        public async Task ResetDailyQuestsAsync()
        {
            ValidateNotDisposed();

            try
            {
                _logger.LogInformation("Resetting daily quests...");

                var dailyQuests = _activeQuests.Values;
                    .Where(q => q.Type == QuestType.Daily)
                    .ToList();

                foreach (var quest in dailyQuests)
                {
                    // Fail or abandon daily quests that aren't completed;
                    if (quest.Status != QuestStatus.Completed)
                    {
                        await FailQuestAsync(quest.Id, "Daily reset");
                    }
                }

                // Update session data;
                SessionData.LastDailyReset = DateTime.UtcNow;
                SessionData.DailyResetCount++;

                // Raise event;
                OnDailyQuestsReset(SessionData.DailyResetCount);

                _logger.LogInformation($"Daily quests reset. Count: {SessionData.DailyResetCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting daily quests");
                throw new QuestOperationException("Failed to reset daily quests", ex);
            }
        }

        /// <summary>
        /// Validates quest progression state;
        /// </summary>
        public async Task<ValidationResult> ValidateQuestStateAsync()
        {
            ValidateNotDisposed();

            var result = new ValidationResult();

            try
            {
                // Validate active quests;
                foreach (var quest in _activeQuests.Values)
                {
                    var questValidation = await ValidateQuestAsync(quest);
                    if (!questValidation.IsValid)
                    {
                        result.Errors.AddRange(questValidation.Errors);
                    }
                }

                // Check for expired quests;
                var expiredQuests = _activeQuests.Values;
                    .Where(q => q.ExpirationTime.HasValue && q.ExpirationTime.Value < DateTime.UtcNow)
                    .ToList();

                foreach (var quest in expiredQuests)
                {
                    result.Warnings.Add($"Quest '{quest.Name}' has expired");
                    await FailQuestAsync(quest.Id, "Quest expired");
                }

                result.IsValid = result.Errors.Count == 0;

                _logger.LogDebug($"Quest state validation completed. Valid: {result.IsValid}, Errors: {result.Errors.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating quest state");
                result.Errors.Add($"Validation error: {ex.Message}");
                result.IsValid = false;
            }

            return result;
        }

        /// <summary>
        /// Clears all quest data (for testing/reset)
        /// </summary>
        public async Task ClearAllQuestDataAsync()
        {
            ValidateNotDisposed();

            try
            {
                _logger.LogWarning("Clearing all quest data...");

                // Fail all active quests;
                var activeQuestIds = _activeQuests.Keys.ToList();
                foreach (var questId in activeQuestIds)
                {
                    await FailQuestAsync(questId, "System reset");
                }

                // Clear collections;
                _activeQuests.Clear();
                _completedQuests.Clear();
                _trackedObjectives.Clear();

                // Reset statistics;
                Statistics = new QuestStatistics();
                SessionData = new QuestSessionData();

                _logger.LogInformation("All quest data cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing quest data");
                throw new QuestOperationException("Failed to clear quest data", ex);
            }
        }

        #endregion;

        #region Private Methods;

        private void InitializeDefaults()
        {
            Configuration = new QuestManagerConfiguration;
            {
                MaxActiveQuests = MAX_ACTIVE_QUESTS,
                MaxCompletedQuests = MAX_COMPLETED_QUESTS_HISTORY,
                UpdateIntervalMs = QUEST_UPDATE_INTERVAL_MS,
                AutoSaveIntervalMinutes = 5,
                EnableDailyReset = true,
                EnableQuestChains = true,
                EnableStatistics = true;
            };

            Statistics = new QuestStatistics();
            ActiveFilters = new QuestFilters();
            QuestCategories = new List<string> { DEFAULT_QUEST_CATEGORY };
            SessionData = new QuestSessionData();
        }

        private void ApplyConfiguration()
        {
            QuestLogCapacity = Configuration.MaxActiveQuests;

            if (Configuration.UpdateIntervalMs > 0)
            {
                _updateTimer.Interval = Configuration.UpdateIntervalMs;
            }
        }

        private void InitializeTimer()
        {
            _updateTimer = new System.Timers.Timer(Configuration.UpdateIntervalMs);
            _updateTimer.Elapsed += async (sender, e) => await OnUpdateTimerElapsedAsync();
            _updateTimer.AutoReset = true;
            _updateTimer.Start();

            _logger.LogDebug($"Quest update timer initialized ({Configuration.UpdateIntervalMs}ms interval)");
        }

        private async Task OnUpdateTimerElapsedAsync()
        {
            try
            {
                if (_disposed || !_isInitialized)
                    return;

                // Update quest timers;
                await UpdateQuestTimersAsync();

                // Auto-save if configured;
                if (Configuration.AutoSaveIntervalMinutes > 0)
                {
                    var timeSinceLastSave = DateTime.UtcNow - SessionData.LastSaveTime;
                    if (timeSinceLastSave.TotalMinutes >= Configuration.AutoSaveIntervalMinutes)
                    {
                        await SaveAsync();
                        SessionData.LastSaveTime = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in quest update timer");
            }
        }

        private async Task UpdateQuestTimersAsync()
        {
            var now = DateTime.UtcNow;

            foreach (var quest in _activeQuests.Values)
            {
                // Update duration;
                quest.ElapsedTime = now - quest.AcceptedTime;

                // Check for time-based failures;
                if (quest.TimeLimit.HasValue && quest.ElapsedTime > quest.TimeLimit.Value)
                {
                    await FailQuestAsync(quest.Id, "Time limit exceeded");
                }
            }
        }

        private async Task<Quest> CreateQuestFromTemplateAsync(string templateId)
        {
            if (!_questTemplates.TryGetValue(templateId, out var template))
            {
                return null;
            }

            var quest = new Quest;
            {
                Id = GenerateQuestInstanceId(templateId),
                TemplateId = templateId,
                Name = template.Name,
                Description = template.Description,
                Category = template.Category,
                Type = template.Type,
                Difficulty = template.Difficulty,
                MinLevel = template.MinLevel,
                MaxLevel = template.MaxLevel,
                Objectives = template.Objectives.Select(o => o.Clone()).ToList(),
                Rewards = template.Rewards.Clone(),
                Prerequisites = template.Prerequisites.ToList(),
                ChainId = template.ChainId,
                CanAbandon = template.CanAbandon,
                CanShare = template.CanShare,
                TimeLimit = template.TimeLimit,
                CreatedTime = DateTime.UtcNow,
                Status = QuestStatus.NotStarted;
            };

            // Initialize objectives;
            foreach (var objective in quest.Objectives)
            {
                objective.QuestId = quest.Id;
                objective.IsCompleted = false;
                objective.CurrentProgress = 0;
            }

            return await Task.FromResult(quest);
        }

        private string GenerateQuestInstanceId(string templateId)
        {
            var timestamp = DateTime.UtcNow.Ticks;
            var random = new Random().Next(1000, 9999);
            return $"{templateId}_{timestamp}_{random}";
        }

        private async Task<bool> CheckPrerequisitesAsync(Quest quest)
        {
            if (quest.Prerequisites == null || !quest.Prerequisites.Any())
                return true;

            foreach (var prerequisite in quest.Prerequisites)
            {
                switch (prerequisite.Type)
                {
                    case PrerequisiteType.QuestCompleted:
                        if (!_completedQuests.ContainsKey(prerequisite.TargetId))
                            return false;
                        break;

                    case PrerequisiteType.Level:
                        // This would require player level service;
                        break;

                    case PrerequisiteType.Item:
                        // This would require inventory service;
                        break;

                    case PrerequisiteType.Skill:
                        // This would require skill system;
                        break;
                }
            }

            return await Task.FromResult(true);
        }

        private async Task<bool> CheckTemplatePrerequisitesAsync(QuestTemplate template)
        {
            // Simplified check - in real implementation would check actual prerequisites;
            return await Task.FromResult(true);
        }

        private async Task StartTrackingObjectivesAsync(Quest quest)
        {
            foreach (var objective in quest.Objectives)
            {
                if (!_trackedObjectives.TryAdd(objective.Id, objective))
                {
                    _logger.LogWarning($"Failed to track objective: {objective.Id}");
                }
            }

            await Task.CompletedTask;
        }

        private async Task StopTrackingObjectivesAsync(Quest quest)
        {
            foreach (var objective in quest.Objectives)
            {
                _trackedObjectives.TryRemove(objective.Id, out _);
            }

            await Task.CompletedTask;
        }

        private async Task<bool> CanCompleteQuestAsync(Quest quest)
        {
            if (quest.Status != QuestStatus.ReadyForCompletion)
                return false;

            if (!quest.Objectives.All(o => o.IsCompleted))
                return false;

            return await Task.FromResult(true);
        }

        private async Task<QuestRewards> CalculateRewardsAsync(Quest quest, QuestCompletionData completionData)
        {
            var rewards = quest.Rewards.Clone();

            // Apply completion bonuses;
            if (completionData != null)
            {
                // Time bonus;
                if (completionData.CompletionTimeBonus > 0 && quest.TimeLimit.HasValue)
                {
                    var timeRatio = 1.0 - (quest.ElapsedTime.TotalSeconds / quest.TimeLimit.Value.TotalSeconds);
                    if (timeRatio > 0)
                    {
                        rewards.Experience = (int)(rewards.Experience * (1 + timeRatio * completionData.CompletionTimeBonus));
                    }
                }

                // Perfect objective bonus;
                if (completionData.PerfectObjectiveBonus > 0)
                {
                    if (quest.Objectives.All(o => o.CurrentProgress >= o.TargetProgress))
                    {
                        rewards.Experience = (int)(rewards.Experience * (1 + completionData.PerfectObjectiveBonus));
                    }
                }
            }

            return await Task.FromResult(rewards);
        }

        private async Task AddToCompletedQuestsAsync(Quest quest)
        {
            // Check limit;
            if (_completedQuests.Count >= Configuration.MaxCompletedQuests)
            {
                // Remove oldest completed quest;
                var oldest = _completedQuests.Values;
                    .OrderBy(q => q.CompletedTime)
                    .FirstOrDefault();

                if (oldest != null)
                {
                    _completedQuests.TryRemove(oldest.Id, out _);
                }
            }

            if (!_completedQuests.TryAdd(quest.Id, quest))
            {
                _logger.LogWarning($"Failed to add quest '{quest.Id}' to completed quests");
            }

            await Task.CompletedTask;
        }

        private async Task ProcessQuestChainOnAcceptAsync(Quest quest)
        {
            if (string.IsNullOrEmpty(quest.ChainId))
                return;

            if (!_questChains.TryGetValue(quest.ChainId, out var chain))
                return;

            // Update chain progress;
            chain.CurrentQuestIndex = chain.QuestIds.IndexOf(quest.TemplateId);

            // Raise event;
            OnQuestChainUpdated(chain, QuestChainUpdateType.QuestAccepted);

            await Task.CompletedTask;
        }

        private async Task ProcessQuestChainOnCompleteAsync(Quest quest)
        {
            if (string.IsNullOrEmpty(quest.ChainId))
                return;

            if (!_questChains.TryGetValue(quest.ChainId, out var chain))
                return;

            // Check if chain is complete;
            var completedInChain = _completedQuests.Values;
                .Count(q => q.ChainId == quest.ChainId);

            chain.CurrentQuestIndex = -1; // Reset for next quest;

            if (completedInChain >= chain.QuestIds.Count)
            {
                // Chain completed;
                chain.IsComplete = true;
                chain.CompletedTime = DateTime.UtcNow;

                OnQuestChainUpdated(chain, QuestChainUpdateType.ChainCompleted);

                _logger.LogInformation($"Quest chain completed: {chain.Name} (ID: {chain.Id})");
            }
            else;
            {
                // Next quest in chain;
                OnQuestChainUpdated(chain, QuestChainUpdateType.QuestCompleted);
            }

            await Task.CompletedTask;
        }

        private async Task LoadSaveDataAsync(QuestSaveData saveData)
        {
            if (saveData == null)
                return;

            // Load active quests;
            foreach (var quest in saveData.ActiveQuests)
            {
                _activeQuests.TryAdd(quest.Id, quest);

                // Restart tracking objectives;
                await StartTrackingObjectivesAsync(quest);
            }

            // Load completed quests;
            foreach (var quest in saveData.CompletedQuests)
            {
                _completedQuests.TryAdd(quest.Id, quest);
            }

            // Load statistics;
            if (saveData.Statistics != null)
            {
                Statistics = saveData.Statistics;
            }

            // Load session data;
            if (saveData.SessionData != null)
            {
                SessionData = saveData.SessionData;
            }

            // Load configuration;
            if (saveData.Configuration != null)
            {
                Configuration = saveData.Configuration;
                ApplyConfiguration();
            }

            _logger.LogInformation($"Loaded save data. Active: {saveData.ActiveQuests.Count}, Completed: {saveData.CompletedQuests.Count}");
        }

        private async Task LoadDefaultTemplatesAsync()
        {
            // Create some default templates for testing/demo;
            var defaultTemplates = new[]
            {
                new QuestTemplate;
                {
                    Id = "quest_tutorial_01",
                    Name = "First Steps",
                    Description = "Learn the basics of the game",
                    Category = "Tutorial",
                    Type = QuestType.Main,
                    Difficulty = QuestDifficulty.Easy,
                    MinLevel = 1,
                    Objectives = new List<QuestObjective>
                    {
                        new QuestObjective;
                        {
                            Id = "obj_move",
                            Name = "Move Around",
                            Description = "Move to the designated area",
                            Type = ObjectiveType.Movement,
                            TargetProgress = 1;
                        }
                    },
                    Rewards = new QuestRewards;
                    {
                        Experience = 100,
                        Gold = 50;
                    }
                }
            };

            foreach (var template in defaultTemplates)
            {
                await RegisterQuestTemplateAsync(template);
            }
        }

        private List<Quest> ApplyFilters(List<Quest> quests, QuestFilters filters)
        {
            var filtered = quests.AsEnumerable();

            if (filters.Categories != null && filters.Categories.Any())
            {
                filtered = filtered.Where(q => filters.Categories.Contains(q.Category));
            }

            if (filters.Types != null && filters.Types.Any())
            {
                filtered = filtered.Where(q => filters.Types.Contains(q.Type));
            }

            if (filters.Difficulties != null && filters.Difficulties.Any())
            {
                filtered = filtered.Where(q => filters.Difficulties.Contains(q.Difficulty));
            }

            if (filters.MinLevel.HasValue)
            {
                filtered = filtered.Where(q => q.MinLevel >= filters.MinLevel.Value);
            }

            if (filters.MaxLevel.HasValue)
            {
                filtered = filtered.Where(q => q.MaxLevel <= filters.MaxLevel.Value || q.MaxLevel == 0);
            }

            if (!string.IsNullOrEmpty(filters.SearchText))
            {
                filtered = filtered.Where(q =>
                    q.Name.Contains(filters.SearchText, StringComparison.OrdinalIgnoreCase) ||
                    q.Description.Contains(filters.SearchText, StringComparison.OrdinalIgnoreCase));
            }

            return filtered.ToList();
        }

        private void UpdateStatistics(Quest quest, StatisticUpdateType updateType)
        {
            Statistics.TotalQuests++;

            switch (updateType)
            {
                case StatisticUpdateType.QuestAccepted:
                    Statistics.QuestsAccepted++;
                    break;

                case StatisticUpdateType.QuestCompleted:
                    Statistics.QuestsCompleted++;
                    Statistics.TotalExperienceEarned += quest.Rewards.Experience;
                    Statistics.TotalGoldEarned += quest.Rewards.Gold;
                    break;

                case StatisticUpdateType.QuestFailed:
                    Statistics.QuestsFailed++;
                    break;

                case StatisticUpdateType.QuestAbandoned:
                    Statistics.QuestsAbandoned++;
                    break;
            }

            // Update category statistics;
            if (!Statistics.CategoryStats.ContainsKey(quest.Category))
            {
                Statistics.CategoryStats[quest.Category] = new CategoryStatistics();
            }

            var categoryStats = Statistics.CategoryStats[quest.Category];
            categoryStats.TotalQuests++;

            switch (updateType)
            {
                case StatisticUpdateType.QuestCompleted:
                    categoryStats.Completed++;
                    break;
                case StatisticUpdateType.QuestFailed:
                    categoryStats.Failed++;
                    break;
                case StatisticUpdateType.QuestAbandoned:
                    categoryStats.Abandoned++;
                    break;
            }

            Statistics.LastUpdated = DateTime.UtcNow;
        }

        private async Task ValidateQuestTemplateAsync(QuestTemplate template)
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(template.Id))
                errors.Add("Template ID is required");

            if (string.IsNullOrEmpty(template.Name))
                errors.Add("Template name is required");

            if (template.Objectives == null || !template.Objectives.Any())
                errors.Add("At least one objective is required");

            if (template.MinLevel < 0)
                errors.Add("Minimum level cannot be negative");

            if (template.MaxLevel < 0)
                errors.Add("Maximum level cannot be negative");

            if (template.MaxLevel > 0 && template.MaxLevel < template.MinLevel)
                errors.Add("Maximum level cannot be less than minimum level");

            if (errors.Any())
            {
                throw new QuestValidationException($"Invalid quest template: {string.Join("; ", errors)}");
            }

            await Task.CompletedTask;
        }

        private async Task ValidateQuestChainAsync(QuestChain chain)
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(chain.Id))
                errors.Add("Chain ID is required");

            if (string.IsNullOrEmpty(chain.Name))
                errors.Add("Chain name is required");

            if (chain.QuestIds == null || !chain.QuestIds.Any())
                errors.Add("At least one quest ID is required");

            // Verify all quest templates exist;
            foreach (var questId in chain.QuestIds)
            {
                if (!_questTemplates.ContainsKey(questId))
                {
                    errors.Add($"Quest template not found: {questId}");
                }
            }

            if (errors.Any())
            {
                throw new QuestValidationException($"Invalid quest chain: {string.Join("; ", errors)}");
            }

            await Task.CompletedTask;
        }

        private async Task<ValidationResult> ValidateQuestAsync(Quest quest)
        {
            var result = new ValidationResult();

            if (quest == null)
            {
                result.Errors.Add("Quest is null");
                return result;
            }

            // Check if template still exists;
            if (!_questTemplates.ContainsKey(quest.TemplateId))
            {
                result.Errors.Add($"Template not found: {quest.TemplateId}");
            }

            // Validate objectives;
            foreach (var objective in quest.Objectives)
            {
                if (objective.CurrentProgress < 0)
                {
                    result.Errors.Add($"Objective '{objective.Id}' has negative progress");
                }

                if (objective.CurrentProgress > objective.TargetProgress && !objective.AllowOverCompletion)
                {
                    result.Errors.Add($"Objective '{objective.Id}' has exceeded target progress");
                }
            }

            result.IsValid = !result.Errors.Any();

            return await Task.FromResult(result);
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QuestManager));
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new QuestNotInitializedException("QuestManager must be initialized before use");
            }
        }

        #endregion;

        #region Event Invokers;

        private void OnQuestAccepted(Quest quest, string source)
        {
            QuestAccepted?.Invoke(this, new QuestAcceptedEventArgs(quest, source));
        }

        private void OnQuestCompleted(Quest quest, QuestCompletionResult result)
        {
            QuestCompleted?.Invoke(this, new QuestCompletedEventArgs(quest, result));
        }

        private void OnQuestFailed(Quest quest, string reason)
        {
            QuestFailed?.Invoke(this, new QuestFailedEventArgs(quest, reason));
        }

        private void OnQuestProgressUpdated(Quest quest, QuestObjective objective, ObjectiveProgressResult result)
        {
            QuestProgressUpdated?.Invoke(this, new QuestProgressEventArgs(quest, objective, result));
        }

        private void OnObjectiveCompleted(QuestObjective objective, Quest quest)
        {
            ObjectiveCompleted?.Invoke(this, new ObjectiveCompletedEventArgs(objective, quest));
        }

        private void OnQuestAbandoned(Quest quest, string reason)
        {
            QuestAbandoned?.Invoke(this, new QuestAbandonedEventArgs(quest, reason));
        }

        private void OnRewardsClaimed(Quest quest, QuestRewards rewards)
        {
            RewardsClaimed?.Invoke(this, new RewardsClaimedEventArgs(quest, rewards));
        }

        private void OnQuestChainUpdated(QuestChain chain, QuestChainUpdateType updateType)
        {
            QuestChainUpdated?.Invoke(this, new QuestChainUpdatedEventArgs(chain, updateType));
        }

        private void OnDailyQuestsReset(int resetCount)
        {
            DailyQuestsReset?.Invoke(this, new DailyQuestsResetEventArgs(resetCount));
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
            if (!_disposed)
            {
                if (disposing)
                {
                    // Stop and dispose timer;
                    if (_updateTimer != null)
                    {
                        _updateTimer.Stop();
                        _updateTimer.Dispose();
                        _updateTimer = null;
                    }

                    // Clear collections;
                    _activeQuests.Clear();
                    _completedQuests.Clear();
                    _questTemplates.Clear();
                    _questChains.Clear();
                    _trackedObjectives.Clear();
                    QuestCategories.Clear();

                    // Unsubscribe from events;
                    QuestAccepted = null;
                    QuestCompleted = null;
                    QuestFailed = null;
                    QuestProgressUpdated = null;
                    ObjectiveCompleted = null;
                    QuestAbandoned = null;
                    RewardsClaimed = null;
                    QuestChainUpdated = null;
                    DailyQuestsReset = null;
                }

                _disposed = true;
                _logger.LogInformation("QuestManager disposed");
            }
        }

        ~QuestManager()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes and Enums;

        /// <summary>
        /// Quest manager configuration;
        /// </summary>
        public class QuestManagerConfiguration : ICloneable;
        {
            public int MaxActiveQuests { get; set; } = MAX_ACTIVE_QUESTS;
            public int MaxCompletedQuests { get; set; } = MAX_COMPLETED_QUESTS_HISTORY;
            public int UpdateIntervalMs { get; set; } = QUEST_UPDATE_INTERVAL_MS;
            public int AutoSaveIntervalMinutes { get; set; } = 5;
            public bool EnableDailyReset { get; set; } = true;
            public bool EnableQuestChains { get; set; } = true;
            public bool EnableStatistics { get; set; } = true;
            public bool EnableAutoTracking { get; set; } = true;
            public bool EnableNotifications { get; set; } = true;
            public string DefaultCategory { get; set; } = DEFAULT_QUEST_CATEGORY;

            public object Clone()
            {
                return new QuestManagerConfiguration;
                {
                    MaxActiveQuests = this.MaxActiveQuests,
                    MaxCompletedQuests = this.MaxCompletedQuests,
                    UpdateIntervalMs = this.UpdateIntervalMs,
                    AutoSaveIntervalMinutes = this.AutoSaveIntervalMinutes,
                    EnableDailyReset = this.EnableDailyReset,
                    EnableQuestChains = this.EnableQuestChains,
                    EnableStatistics = this.EnableStatistics,
                    EnableAutoTracking = this.EnableAutoTracking,
                    EnableNotifications = this.EnableNotifications,
                    DefaultCategory = this.DefaultCategory;
                };
            }
        }

        /// <summary>
        /// Quest statistics;
        /// </summary>
        public class QuestStatistics : ICloneable;
        {
            public int TotalQuests { get; set; }
            public int QuestsAccepted { get; set; }
            public int QuestsCompleted { get; set; }
            public int QuestsFailed { get; set; }
            public int QuestsAbandoned { get; set; }
            public int TotalExperienceEarned { get; set; }
            public int TotalGoldEarned { get; set; }
            public int TotalItemsEarned { get; set; }
            public DateTime FirstQuestTime { get; set; }
            public DateTime LastUpdated { get; set; }
            public Dictionary<string, CategoryStatistics> CategoryStats { get; set; } = new Dictionary<string, CategoryStatistics>();

            public object Clone()
            {
                return new QuestStatistics;
                {
                    TotalQuests = this.TotalQuests,
                    QuestsAccepted = this.QuestsAccepted,
                    QuestsCompleted = this.QuestsCompleted,
                    QuestsFailed = this.QuestsFailed,
                    QuestsAbandoned = this.QuestsAbandoned,
                    TotalExperienceEarned = this.TotalExperienceEarned,
                    TotalGoldEarned = this.TotalGoldEarned,
                    TotalItemsEarned = this.TotalItemsEarned,
                    FirstQuestTime = this.FirstQuestTime,
                    LastUpdated = this.LastUpdated,
                    CategoryStats = new Dictionary<string, CategoryStatistics>(this.CategoryStats)
                };
            }
        }

        /// <summary>
        /// Category statistics;
        /// </summary>
        public class CategoryStatistics;
        {
            public int TotalQuests { get; set; }
            public int Completed { get; set; }
            public int Failed { get; set; }
            public int Abandoned { get; set; }
        }

        /// <summary>
        /// Quest session data;
        /// </summary>
        public class QuestSessionData : ICloneable;
        {
            public DateTime SessionStartTime { get; set; } = DateTime.UtcNow;
            public DateTime LastSaveTime { get; set; } = DateTime.UtcNow;
            public DateTime LastDailyReset { get; set; } = DateTime.UtcNow;
            public int DailyResetCount { get; set; }
            public int SessionQuestsCompleted { get; set; }
            public int SessionExperienceEarned { get; set; }

            public object Clone()
            {
                return new QuestSessionData;
                {
                    SessionStartTime = this.SessionStartTime,
                    LastSaveTime = this.LastSaveTime,
                    LastDailyReset = this.LastDailyReset,
                    DailyResetCount = this.DailyResetCount,
                    SessionQuestsCompleted = this.SessionQuestsCompleted,
                    SessionExperienceEarned = this.SessionExperienceEarned;
                };
            }
        }

        /// <summary>
        /// Quest filters;
        /// </summary>
        public class QuestFilters;
        {
            public List<string> Categories { get; set; }
            public List<QuestType> Types { get; set; }
            public List<QuestDifficulty> Difficulties { get; set; }
            public int? MinLevel { get; set; }
            public int? MaxLevel { get; set; }
            public string SearchText { get; set; }
            public bool ShowCompleted { get; set; }
            public bool ShowFailed { get; set; }
            public bool ShowActiveOnly { get; set; } = true;
        }

        /// <summary>
        /// Quest save data;
        /// </summary>
        public class QuestSaveData;
        {
            public List<Quest> ActiveQuests { get; set; } = new List<Quest>();
            public List<Quest> CompletedQuests { get; set; } = new List<Quest>();
            public QuestStatistics Statistics { get; set; }
            public QuestSessionData SessionData { get; set; }
            public QuestManagerConfiguration Configuration { get; set; }
            public DateTime SavedTime { get; set; }
        }

        /// <summary>
        /// Quest completion data;
        /// </summary>
        public class QuestCompletionData;
        {
            public double CompletionTimeBonus { get; set; }
            public double PerfectObjectiveBonus { get; set; }
            public Dictionary<string, object> BonusMultipliers { get; set; }
            public Dictionary<string, object> CompletionMetadata { get; set; }
        }

        /// <summary>
        /// Quest completion result;
        /// </summary>
        public class QuestCompletionResult;
        {
            public Quest Quest { get; set; }
            public QuestRewards Rewards { get; set; }
            public DateTime CompletionTime { get; set; }
            public Dictionary<string, object> BonusMultipliers { get; set; }
        }

        /// <summary>
        /// Objective progress result;
        /// </summary>
        public class ObjectiveProgressResult;
        {
            public QuestObjective Objective { get; set; }
            public Quest Quest { get; set; }
            public int PreviousProgress { get; set; }
            public int NewProgress { get; set; }
            public bool ObjectiveCompleted { get; set; }
            public bool QuestCompletable { get; set; }
            public Dictionary<string, object> ProgressData { get; set; }
        }

        /// <summary>
        /// Quest chain progress;
        /// </summary>
        public class QuestChainProgress;
        {
            public QuestChain Chain { get; set; }
            public List<Quest> CompletedQuests { get; set; }
            public Quest CurrentQuest { get; set; }
            public int ProgressPercentage { get; set; }
            public bool IsChainComplete { get; set; }
        }

        /// <summary>
        /// Validation result;
        /// </summary>
        public class ValidationResult;
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
        }

        /// <summary>
        /// Quest status;
        /// </summary>
        public enum QuestStatus;
        {
            NotStarted,
            InProgress,
            ReadyForCompletion,
            Completed,
            Failed,
            Abandoned;
        }

        /// <summary>
        /// Quest type;
        /// </summary>
        public enum QuestType;
        {
            Main,
            Side,
            Daily,
            Weekly,
            Event,
            Tutorial,
            Hidden,
            Epic;
        }

        /// <summary>
        /// Quest difficulty;
        /// </summary>
        public enum QuestDifficulty;
        {
            VeryEasy,
            Easy,
            Medium,
            Hard,
            VeryHard,
            Impossible;
        }

        /// <summary>
        /// Objective type;
        /// </summary>
        public enum ObjectiveType;
        {
            Kill,
            Collect,
            Deliver,
            Escort,
            Defend,
            Explore,
            Talk,
            Use,
            Craft,
            Custom;
        }

        /// <summary>
        /// Prerequisite type;
        /// </summary>
        public enum PrerequisiteType;
        {
            QuestCompleted,
            Level,
            Item,
            Skill,
            Faction,
            Reputation;
        }

        /// <summary>
        /// Quest chain update type;
        /// </summary>
        public enum QuestChainUpdateType;
        {
            QuestAccepted,
            QuestCompleted,
            ChainCompleted;
        }

        /// <summary>
        /// Statistic update type;
        /// </summary>
        public enum StatisticUpdateType;
        {
            QuestAccepted,
            QuestCompleted,
            QuestFailed,
            QuestAbandoned;
        }

        #endregion;

        #region Event Args Classes;

        public class QuestAcceptedEventArgs : EventArgs;
        {
            public Quest Quest { get; }
            public string Source { get; }

            public QuestAcceptedEventArgs(Quest quest, string source)
            {
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
                Source = source;
            }
        }

        public class QuestCompletedEventArgs : EventArgs;
        {
            public Quest Quest { get; }
            public QuestCompletionResult Result { get; }

            public QuestCompletedEventArgs(Quest quest, QuestCompletionResult result)
            {
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
                Result = result ?? throw new ArgumentNullException(nameof(result));
            }
        }

        public class QuestFailedEventArgs : EventArgs;
        {
            public Quest Quest { get; }
            public string Reason { get; }

            public QuestFailedEventArgs(Quest quest, string reason)
            {
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
                Reason = reason ?? throw new ArgumentNullException(nameof(reason));
            }
        }

        public class QuestProgressEventArgs : EventArgs;
        {
            public Quest Quest { get; }
            public QuestObjective Objective { get; }
            public ObjectiveProgressResult ProgressResult { get; }

            public QuestProgressEventArgs(Quest quest, QuestObjective objective, ObjectiveProgressResult progressResult)
            {
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
                Objective = objective ?? throw new ArgumentNullException(nameof(objective));
                ProgressResult = progressResult ?? throw new ArgumentNullException(nameof(progressResult));
            }
        }

        public class ObjectiveCompletedEventArgs : EventArgs;
        {
            public QuestObjective Objective { get; }
            public Quest Quest { get; }

            public ObjectiveCompletedEventArgs(QuestObjective objective, Quest quest)
            {
                Objective = objective ?? throw new ArgumentNullException(nameof(objective));
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
            }
        }

        public class QuestAbandonedEventArgs : EventArgs;
        {
            public Quest Quest { get; }
            public string Reason { get; }

            public QuestAbandonedEventArgs(Quest quest, string reason)
            {
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
                Reason = reason;
            }
        }

        public class RewardsClaimedEventArgs : EventArgs;
        {
            public Quest Quest { get; }
            public QuestRewards Rewards { get; }

            public RewardsClaimedEventArgs(Quest quest, QuestRewards rewards)
            {
                Quest = quest ?? throw new ArgumentNullException(nameof(quest));
                Rewards = rewards ?? throw new ArgumentNullException(nameof(rewards));
            }
        }

        public class QuestChainUpdatedEventArgs : EventArgs;
        {
            public QuestChain Chain { get; }
            public QuestChainUpdateType UpdateType { get; }

            public QuestChainUpdatedEventArgs(QuestChain chain, QuestChainUpdateType updateType)
            {
                Chain = chain ?? throw new ArgumentNullException(nameof(chain));
                UpdateType = updateType;
            }
        }

        public class DailyQuestsResetEventArgs : EventArgs;
        {
            public int ResetCount { get; }

            public DailyQuestsResetEventArgs(int resetCount)
            {
                ResetCount = resetCount;
            }
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Quest template;
    /// </summary>
    public class QuestTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public QuestType Type { get; set; }
        public QuestDifficulty Difficulty { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public List<QuestObjective> Objectives { get; set; } = new List<QuestObjective>();
        public QuestRewards Rewards { get; set; } = new QuestRewards();
        public List<QuestPrerequisite> Prerequisites { get; set; } = new List<QuestPrerequisite>();
        public string ChainId { get; set; }
        public bool CanAbandon { get; set; } = true;
        public bool CanShare { get; set; } = false;
        public TimeSpan? TimeLimit { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Quest instance;
    /// </summary>
    public class Quest;
    {
        public string Id { get; set; }
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public QuestType Type { get; set; }
        public QuestDifficulty Difficulty { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public List<QuestObjective> Objectives { get; set; } = new List<QuestObjective>();
        public QuestRewards Rewards { get; set; } = new QuestRewards();
        public List<QuestPrerequisite> Prerequisites { get; set; } = new List<QuestPrerequisite>();
        public string ChainId { get; set; }
        public bool CanAbandon { get; set; }
        public bool CanShare { get; set; }
        public TimeSpan? TimeLimit { get; set; }
        public QuestStatus Status { get; set; }
        public string Source { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime AcceptedTime { get; set; }
        public DateTime? CompletedTime { get; set; }
        public DateTime? FailedTime { get; set; }
        public DateTime? AbandonedTime { get; set; }
        public string FailureReason { get; set; }
        public string AbandonReason { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public DateTime? ExpirationTime { get; set; }
        public QuestCompletionData CompletionData { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Quest objective;
    /// </summary>
    public class QuestObjective : ICloneable;
    {
        public string Id { get; set; }
        public string QuestId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ObjectiveType Type { get; set; }
        public int TargetProgress { get; set; }
        public int CurrentProgress { get; set; }
        public bool IsCompleted { get; set; }
        public bool AllowOverCompletion { get; set; }
        public DateTime? CompletedTime { get; set; }
        public DateTime LastUpdated { get; set; }
        public Dictionary<string, object> ProgressData { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new QuestObjective;
            {
                Id = this.Id,
                Name = this.Name,
                Description = this.Description,
                Type = this.Type,
                TargetProgress = this.TargetProgress,
                CurrentProgress = this.CurrentProgress,
                IsCompleted = this.IsCompleted,
                AllowOverCompletion = this.AllowOverCompletion,
                ProgressData = new Dictionary<string, object>(this.ProgressData),
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Quest rewards;
    /// </summary>
    public class QuestRewards : ICloneable;
    {
        public int Experience { get; set; }
        public int Gold { get; set; }
        public List<ItemReward> Items { get; set; } = new List<ItemReward>();
        public List<string> Unlocks { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new QuestRewards;
            {
                Experience = this.Experience,
                Gold = this.Gold,
                Items = this.Items.Select(i => i.Clone()).ToList(),
                Unlocks = new List<string>(this.Unlocks),
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Item reward;
    /// </summary>
    public class ItemReward : ICloneable;
    {
        public string ItemId { get; set; }
        public int Quantity { get; set; }
        public int Quality { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new ItemReward;
            {
                ItemId = this.ItemId,
                Quantity = this.Quantity,
                Quality = this.Quality,
                Properties = new Dictionary<string, object>(this.Properties)
            };
        }
    }

    /// <summary>
    /// Quest prerequisite;
    /// </summary>
    public class QuestPrerequisite;
    {
        public PrerequisiteType Type { get; set; }
        public string TargetId { get; set; }
        public int RequiredValue { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Quest chain;
    /// </summary>
    public class QuestChain;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> QuestIds { get; set; } = new List<string>();
        public int CurrentQuestIndex { get; set; } = -1;
        public bool IsComplete { get; set; }
        public DateTime? CompletedTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    #endregion;

    #region Custom Exceptions;

    [Serializable]
    public class QuestOperationException : Exception
    {
        public QuestOperationException() { }
        public QuestOperationException(string message) : base(message) { }
        public QuestOperationException(string message, Exception inner) : base(message, inner) { }
        protected QuestOperationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestNotFoundException : QuestOperationException;
    {
        public QuestNotFoundException() { }
        public QuestNotFoundException(string message) : base(message) { }
        public QuestNotFoundException(string message, Exception inner) : base(message, inner) { }
        protected QuestNotFoundException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestInitializationException : QuestOperationException;
    {
        public QuestInitializationException() { }
        public QuestInitializationException(string message) : base(message) { }
        public QuestInitializationException(string message, Exception inner) : base(message, inner) { }
        protected QuestInitializationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestNotInitializedException : QuestOperationException;
    {
        public QuestNotInitializedException() { }
        public QuestNotInitializedException(string message) : base(message) { }
        public QuestNotInitializedException(string message, Exception inner) : base(message, inner) { }
        protected QuestNotInitializedException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestCompletionException : QuestOperationException;
    {
        public QuestCompletionException() { }
        public QuestCompletionException(string message) : base(message) { }
        public QuestCompletionException(string message, Exception inner) : base(message, inner) { }
        protected QuestCompletionException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestPrerequisiteException : QuestOperationException;
    {
        public QuestPrerequisiteException() { }
        public QuestPrerequisiteException(string message) : base(message) { }
        public QuestPrerequisiteException(string message, Exception inner) : base(message, inner) { }
        protected QuestPrerequisiteException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestLogFullException : QuestOperationException;
    {
        public QuestLogFullException() { }
        public QuestLogFullException(string message) : base(message) { }
        public QuestLogFullException(string message, Exception inner) : base(message, inner) { }
        protected QuestLogFullException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    [Serializable]
    public class QuestValidationException : QuestOperationException;
    {
        public QuestValidationException() { }
        public QuestValidationException(string message) : base(message) { }
        public QuestValidationException(string message, Exception inner) : base(message, inner) { }
        protected QuestValidationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    #endregion;
}
