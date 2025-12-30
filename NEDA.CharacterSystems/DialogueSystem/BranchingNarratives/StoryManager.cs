// NEDA.CharacterSystems/DialogueSystem/BranchingNarratives/StoryManager.cs;

using NEDA.AI.MachineLearning;
using NEDA.AI.NaturalLanguage;
using NEDA.Brain.KnowledgeBase.CreativePatterns;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NLP_Engine;
using NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
using NEDA.Communication.DialogSystem;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.FileService;
using NEDA.Services.NotificationService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.DialogueSystem.BranchingNarratives.NarrativeEngine;

namespace NEDA.CharacterSystems.DialogueSystem.BranchingNarratives;
{
    /// <summary>
    /// Hikaye yönetim sistemi - Dallanmış hikayeler, diyalog ağaçları, hikaye durumu ve oyun içi olayları yönetir;
    /// </summary>
    public class StoryManager : IStoryManager, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IFileManager _fileManager;
        private readonly IAppConfig _appConfig;
        private readonly INLPEngine _nlpEngine;
        private readonly IMemorySystem _memorySystem;
        private readonly IEmotionRecognition _emotionRecognition;
        private readonly IConversationManager _conversationManager;
        private readonly INotificationManager _notificationManager;

        private readonly Dictionary<string, Story> _stories;
        private readonly Dictionary<string, StoryState> _storyStates;
        private readonly Dictionary<string, List<StoryEvent>> _storyEvents;
        private readonly StoryAnalyticsEngine _analyticsEngine;
        private readonly StoryGenerator _storyGenerator;
        private readonly StoryValidator _storyValidator;

        private bool _isInitialized;
        private readonly string _storyDatabasePath;
        private readonly string _storyTemplatesPath;
        private readonly string _saveGamePath;
        private readonly SemaphoreSlim _storyAccessSemaphore;
        private readonly Timer _autoSaveTimer;
        private readonly JsonSerializerOptions _jsonOptions;

        private const int AUTO_SAVE_INTERVAL = 60000; // 1 dakika;
        private const int MAX_UNDO_STEPS = 50;

        // Eventler;
        public event EventHandler<StoryStartedEventArgs> StoryStarted;
        public event EventHandler<StoryProgressedEventArgs> StoryProgressed;
        public event EventHandler<BranchSelectedEventArgs> BranchSelected;
        public event EventHandler<StoryEndedEventArgs> StoryEnded;
        public event EventHandler<StoryEventTriggeredEventArgs> StoryEventTriggered;
        public event EventHandler<PlayerChoiceMadeEventArgs> PlayerChoiceMade;

        /// <summary>
        /// StoryManager constructor;
        /// </summary>
        public StoryManager(
            ILogger<StoryManager> logger,
            IFileManager fileManager,
            IAppConfig appConfig,
            INLPEngine nlpEngine,
            IMemorySystem memorySystem,
            IEmotionRecognition emotionRecognition,
            IConversationManager conversationManager,
            INotificationManager notificationManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _nlpEngine = nlpEngine ?? throw new ArgumentNullException(nameof(nlpEngine));
            _memorySystem = memorySystem ?? throw new ArgumentNullException(nameof(memorySystem));
            _emotionRecognition = emotionRecognition ?? throw new ArgumentNullException(nameof(emotionRecognition));
            _conversationManager = conversationManager ?? throw new ArgumentNullException(nameof(conversationManager));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));

            _stories = new Dictionary<string, Story>();
            _storyStates = new Dictionary<string, StoryState>();
            _storyEvents = new Dictionary<string, List<StoryEvent>>();
            _analyticsEngine = new StoryAnalyticsEngine();
            _storyGenerator = new StoryGenerator();
            _storyValidator = new StoryValidator();

            _storyDatabasePath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:StoryDatabasePath") ?? "Data/Stories",
                "Stories.db");
            _storyTemplatesPath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:StoryTemplatesPath") ?? "Data/Stories/Templates",
                "Templates");
            _saveGamePath = Path.Combine(
                _appConfig.GetValue<string>("CharacterSystems:SaveGamePath") ?? "Data/Saves",
                "Stories");

            _storyAccessSemaphore = new SemaphoreSlim(1, 1);

            // JSON serialization ayarları;
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters =
                {
                    new JsonStringEnumConverter(),
                    new StoryNodeConverter(),
                    new StoryConditionConverter()
                }
            };

            // Otomatik kaydetme timer'ı;
            _autoSaveTimer = new Timer(AutoSaveCallback, null, AUTO_SAVE_INTERVAL, AUTO_SAVE_INTERVAL);

            _logger.LogInformation("StoryManager initialized");
        }

        /// <summary>
        /// StoryManager'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing StoryManager...");

                // Storage dizinlerini oluştur;
                await EnsureDirectoriesExistAsync(cancellationToken);

                // Story database'ini yükle;
                await LoadStoryDatabaseAsync(cancellationToken);

                // Kayıtlı oyunları yükle;
                await LoadSavedGamesAsync(cancellationToken);

                // Story generator'ı başlat;
                await _storyGenerator.InitializeAsync(_storyTemplatesPath, cancellationToken);

                // Analytics engine'i başlat;
                await _analyticsEngine.InitializeAsync(cancellationToken);

                _isInitialized = true;
                _logger.LogInformation($"StoryManager initialized successfully with {_stories.Count} stories");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize StoryManager");
                throw new StoryManagerException("Failed to initialize StoryManager", ex);
            }
        }

        /// <summary>
        /// Yeni hikaye başlatır;
        /// </summary>
        public async Task<StoryState> StartStoryAsync(
            string storyId,
            StartStoryOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                options ??= StartStoryOptions.Default;

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {storyId} not found");
                }

                // Story validasyonu;
                _storyValidator.ValidateStory(story);

                // Başlangıç state'i oluştur;
                var initialState = CreateInitialStoryState(story, options);

                // Player profili oluştur;
                if (options.PlayerProfile != null)
                {
                    await ApplyPlayerProfileAsync(initialState, options.PlayerProfile, cancellationToken);
                }

                // Story events'leri başlat;
                await InitializeStoryEventsAsync(storyId, story, cancellationToken);

                // State'i kaydet;
                _storyStates[storyId] = initialState;
                await SaveStoryStateAsync(initialState, cancellationToken);

                // Analytics kaydı başlat;
                _analyticsEngine.StartStoryTracking(storyId, initialState.PlayerId);

                _logger.LogInformation($"Story started: {story.Title} ({storyId}) for player {initialState.PlayerId}");

                // Event tetikle;
                StoryStarted?.Invoke(this, new StoryStartedEventArgs;
                {
                    StoryId = storyId,
                    Story = story,
                    StoryState = initialState,
                    StartTime = DateTime.UtcNow,
                    Options = options;
                });

                // Bildirim gönder;
                await SendNotificationAsync(
                    $"Story started: {story.Title}",
                    NotificationType.Info,
                    cancellationToken);

                return initialState;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start story {storyId}");
                throw new StoryStartException($"Failed to start story: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Hikayede ilerler (diyalog seçimi, aksiyon, vs.)
        /// </summary>
        public async Task<StoryProgressResult> ProgressStoryAsync(
            string storyId,
            StoryChoice choice,
            ProgressOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            if (choice == null)
                throw new ArgumentNullException(nameof(choice));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                options ??= ProgressOptions.Default;

                // Story state'i yükle;
                if (!_storyStates.TryGetValue(storyId, out var currentState))
                {
                    currentState = await LoadStoryStateAsync(storyId, cancellationToken);
                    if (currentState == null)
                    {
                        throw new StoryStateNotFoundException($"Story state for {storyId} not found");
                    }
                    _storyStates[storyId] = currentState;
                }

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {storyId} not found");
                }

                // Mevcut node'u bul;
                var currentNode = story.GetNode(currentState.CurrentNodeId);
                if (currentNode == null)
                {
                    throw new StoryNodeNotFoundException($"Current node {currentState.CurrentNodeId} not found");
                }

                // Seçimi validate et;
                ValidateChoice(currentNode, choice, currentState);

                // Seçimin sonuçlarını hesapla;
                var choiceResult = await ProcessChoiceAsync(
                    currentNode,
                    choice,
                    currentState,
                    options,
                    cancellationToken);

                // Yeni node'a geç;
                var nextNode = story.GetNode(choiceResult.NextNodeId);
                if (nextNode == null)
                {
                    throw new StoryNodeNotFoundException($"Next node {choiceResult.NextNodeId} not found");
                }

                // Story state'ini güncelle;
                var previousState = currentState.Clone();
                UpdateStoryState(currentState, choice, choiceResult, nextNode);

                // Event'leri kontrol et;
                await CheckAndTriggerEventsAsync(storyId, currentState, cancellationToken);

                // Achievement'leri kontrol et;
                await CheckAchievementsAsync(storyId, currentState, cancellationToken);

                // State'i kaydet;
                await SaveStoryStateAsync(currentState, cancellationToken);

                // Undo stack'e ekle;
                AddToUndoStack(storyId, previousState);

                // Analytics kaydı;
                _analyticsEngine.RecordChoice(
                    storyId,
                    currentState.PlayerId,
                    currentNode.Id,
                    choice.Id,
                    choiceResult);

                _logger.LogInformation($"Story progressed: {storyId}, node {currentNode.Id} -> {nextNode.Id}");

                // Progress result oluştur;
                var result = new StoryProgressResult;
                {
                    Success = true,
                    PreviousNode = currentNode,
                    NextNode = nextNode,
                    Choice = choice,
                    ChoiceResult = choiceResult,
                    NewStoryState = currentState,
                    Timestamp = DateTime.UtcNow,
                    EventsTriggered = currentState.RecentEvents;
                };

                // Event tetikle;
                StoryProgressed?.Invoke(this, new StoryProgressedEventArgs;
                {
                    StoryId = storyId,
                    Story = story,
                    PreviousNode = currentNode,
                    NextNode = nextNode,
                    Choice = choice,
                    Result = result,
                    StoryState = currentState;
                });

                PlayerChoiceMade?.Invoke(this, new PlayerChoiceMadeEventArgs;
                {
                    StoryId = storyId,
                    PlayerId = currentState.PlayerId,
                    Choice = choice,
                    Result = choiceResult,
                    Timestamp = DateTime.UtcNow;
                });

                // Eğer story bittiyse;
                if (nextNode.Type == StoryNodeType.Ending)
                {
                    await HandleStoryEndingAsync(storyId, currentState, nextNode, cancellationToken);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to progress story {storyId}");
                throw new StoryProgressException($"Failed to progress story: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Dallanma noktasında seçim yapar;
        /// </summary>
        public async Task<BranchResult> SelectBranchAsync(
            string storyId,
            string branchId,
            BranchSelectionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            if (string.IsNullOrEmpty(branchId))
                throw new ArgumentException("Branch ID cannot be null or empty", nameof(branchId));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                options ??= BranchSelectionOptions.Default;

                // Story state'i yükle;
                if (!_storyStates.TryGetValue(storyId, out var currentState))
                {
                    currentState = await LoadStoryStateAsync(storyId, cancellationToken);
                    if (currentState == null)
                    {
                        throw new StoryStateNotFoundException($"Story state for {storyId} not found");
                    }
                    _storyStates[storyId] = currentState;
                }

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {storyId} not found");
                }

                // Mevcut node'u bul (branching node olmalı)
                var currentNode = story.GetNode(currentState.CurrentNodeId);
                if (currentNode == null || currentNode.Type != StoryNodeType.Branching)
                {
                    throw new InvalidStoryNodeException($"Current node is not a branching node");
                }

                // Branch'i bul;
                var branch = currentNode.Branches?.FirstOrDefault(b => b.Id == branchId);
                if (branch == null)
                {
                    throw new BranchNotFoundException($"Branch {branchId} not found in node {currentNode.Id}");
                }

                // Branch koşullarını kontrol et;
                if (!CheckBranchConditions(branch, currentState))
                {
                    if (!options.ForceSelect)
                    {
                        throw new BranchConditionException($"Branch conditions not met for {branchId}");
                    }
                    _logger.LogWarning($"Forcing branch selection {branchId} despite unmet conditions");
                }

                // Branch seçimini işle;
                var branchResult = await ProcessBranchSelectionAsync(
                    branch,
                    currentState,
                    options,
                    cancellationToken);

                // Yeni node'a geç;
                var nextNode = story.GetNode(branchResult.NextNodeId);
                if (nextNode == null)
                {
                    throw new StoryNodeNotFoundException($"Next node {branchResult.NextNodeId} not found");
                }

                // Story state'ini güncelle;
                var previousState = currentState.Clone();
                UpdateStoryStateForBranch(currentState, branch, branchResult, nextNode);

                // Event'leri kontrol et;
                await CheckAndTriggerEventsAsync(storyId, currentState, cancellationToken);

                // State'i kaydet;
                await SaveStoryStateAsync(currentState, cancellationToken);

                // Undo stack'e ekle;
                AddToUndoStack(storyId, previousState);

                // Analytics kaydı;
                _analyticsEngine.RecordBranchSelection(
                    storyId,
                    currentState.PlayerId,
                    currentNode.Id,
                    branchId,
                    branchResult);

                _logger.LogInformation($"Branch selected: {branchId} in story {storyId}");

                // Result oluştur;
                var result = new BranchResult;
                {
                    Success = true,
                    Branch = branch,
                    BranchResult = branchResult,
                    NextNode = nextNode,
                    NewStoryState = currentState,
                    Timestamp = DateTime.UtcNow;
                };

                // Event tetikle;
                BranchSelected?.Invoke(this, new BranchSelectedEventArgs;
                {
                    StoryId = storyId,
                    Story = story,
                    Branch = branch,
                    Result = result,
                    StoryState = currentState;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to select branch {branchId} in story {storyId}");
                throw new BranchSelectionException($"Failed to select branch: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Hikayeyi kaydeder;
        /// </summary>
        public async Task<SaveGameResult> SaveGameAsync(
            string storyId,
            string saveName = null,
            SaveOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                options ??= SaveOptions.Default;

                // Story state'i yükle;
                if (!_storyStates.TryGetValue(storyId, out var storyState))
                {
                    storyState = await LoadStoryStateAsync(storyId, cancellationToken);
                    if (storyState == null)
                    {
                        throw new StoryStateNotFoundException($"Story state for {storyId} not found");
                    }
                }

                // Save name oluştur;
                if (string.IsNullOrEmpty(saveName))
                {
                    saveName = $"Save_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                }

                // Save game oluştur;
                var saveGame = new SaveGame;
                {
                    Id = Guid.NewGuid().ToString(),
                    StoryId = storyId,
                    SaveName = saveName,
                    StoryState = storyState.Clone(),
                    CreatedAt = DateTime.UtcNow,
                    PlayTime = storyState.PlayTime,
                    PlayerId = storyState.PlayerId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["version"] = "1.0",
                        ["platform"] = Environment.OSVersion.Platform.ToString(),
                        ["saveType"] = options.SaveType.ToString()
                    }
                };

                // Screenshot ekle;
                if (options.IncludeScreenshot)
                {
                    saveGame.Screenshot = await CaptureGameScreenshotAsync(cancellationToken);
                }

                // Save game'i kaydet;
                var savePath = await SaveGameToFileAsync(saveGame, cancellationToken);

                // Quick save slot'u güncelle;
                if (options.SaveType == SaveType.QuickSave)
                {
                    await UpdateQuickSaveSlotAsync(storyId, saveGame, cancellationToken);
                }

                // Auto save slot'u güncelle;
                if (options.SaveType == SaveType.AutoSave)
                {
                    await UpdateAutoSaveSlotAsync(storyId, saveGame, cancellationToken);
                }

                _logger.LogInformation($"Game saved: {saveName} for story {storyId}");

                return new SaveGameResult;
                {
                    Success = true,
                    SaveGame = saveGame,
                    SavePath = savePath,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save game for story {storyId}");
                throw new SaveGameException($"Failed to save game: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Kayıtlı oyunu yükler;
        /// </summary>
        public async Task<StoryState> LoadGameAsync(
            string saveGameId,
            LoadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(saveGameId))
                throw new ArgumentException("Save game ID cannot be null or empty", nameof(saveGameId));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                options ??= LoadOptions.Default;

                // Save game'i yükle;
                var saveGame = await LoadSaveGameAsync(saveGameId, cancellationToken);
                if (saveGame == null)
                {
                    throw new SaveGameNotFoundException($"Save game {saveGameId} not found");
                }

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(saveGame.StoryId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {saveGame.StoryId} not found for save game");
                }

                // Story state'i restore et;
                var storyState = saveGame.StoryState;

                // Validasyon;
                if (!ValidateStoryState(story, storyState))
                {
                    if (!options.ForceLoad)
                    {
                        throw new InvalidSaveGameException($"Save game {saveGameId} is invalid or corrupted");
                    }
                    _logger.LogWarning($"Forcing load of potentially invalid save game {saveGameId}");
                }

                // State'i memory'ye yükle;
                _storyStates[saveGame.StoryId] = storyState;

                // Events'leri restore et;
                await RestoreStoryEventsAsync(saveGame.StoryId, storyState, cancellationToken);

                // Analytics kaydı;
                _analyticsEngine.RecordGameLoad(
                    saveGame.StoryId,
                    storyState.PlayerId,
                    saveGameId,
                    saveGame.CreatedAt);

                _logger.LogInformation($"Game loaded: {saveGame.SaveName} for story {saveGame.StoryId}");

                return storyState;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load game {saveGameId}");
                throw new LoadGameException($"Failed to load game: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Bir adım geri alır (undo)
        /// </summary>
        public async Task<UndoResult> UndoLastStepAsync(
            string storyId,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                // Undo stack'ini kontrol et;
                if (!_storyStates.TryGetValue(storyId, out var currentState))
                {
                    throw new StoryStateNotFoundException($"Story state for {storyId} not found");
                }

                var undoStack = GetUndoStack(storyId);
                if (!undoStack.Any())
                {
                    return new UndoResult;
                    {
                        Success = false,
                        Message = "No steps to undo",
                        CanUndo = false;
                    };
                }

                // Önceki state'i al;
                var previousState = undoStack.Pop();

                // Mevcut state'i redo stack'ine ekle;
                AddToRedoStack(storyId, currentState);

                // State'i geri yükle;
                _storyStates[storyId] = previousState;
                await SaveStoryStateAsync(previousState, cancellationToken);

                _logger.LogInformation($"Undo performed for story {storyId}");

                return new UndoResult;
                {
                    Success = true,
                    PreviousState = previousState,
                    CurrentState = currentState,
                    RemainingUndoSteps = undoStack.Count,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to undo step for story {storyId}");
                throw new UndoException($"Failed to undo: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Hikaye event'ini tetikler;
        /// </summary>
        public async Task<StoryEventResult> TriggerStoryEventAsync(
            string storyId,
            string eventId,
            EventTriggerOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            if (string.IsNullOrEmpty(eventId))
                throw new ArgumentException("Event ID cannot be null or empty", nameof(eventId));

            try
            {
                await _storyAccessSemaphore.WaitAsync(cancellationToken);

                options ??= EventTriggerOptions.Default;

                // Story state'i yükle;
                if (!_storyStates.TryGetValue(storyId, out var storyState))
                {
                    storyState = await LoadStoryStateAsync(storyId, cancellationToken);
                    if (storyState == null)
                    {
                        throw new StoryStateNotFoundException($"Story state for {storyId} not found");
                    }
                }

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {storyId} not found");
                }

                // Event'i bul;
                var storyEvent = story.Events?.FirstOrDefault(e => e.Id == eventId);
                if (storyEvent == null)
                {
                    // Dynamic event oluştur;
                    storyEvent = await GenerateDynamicEventAsync(eventId, storyState, cancellationToken);
                    if (storyEvent == null)
                    {
                        throw new StoryEventNotFoundException($"Event {eventId} not found");
                    }
                }

                // Event koşullarını kontrol et;
                if (!CheckEventConditions(storyEvent, storyState))
                {
                    if (!options.ForceTrigger)
                    {
                        throw new EventConditionException($"Event conditions not met for {eventId}");
                    }
                    _logger.LogWarning($"Forcing event {eventId} despite unmet conditions");
                }

                // Event'i tetikle;
                var eventResult = await ExecuteStoryEventAsync(
                    storyEvent,
                    storyState,
                    options,
                    cancellationToken);

                // Story state'ini güncelle;
                UpdateStoryStateForEvent(storyState, storyEvent, eventResult);

                // Event'i kaydet;
                await RecordStoryEventAsync(storyId, storyEvent, eventResult, cancellationToken);

                // Analytics kaydı;
                _analyticsEngine.RecordEventTrigger(
                    storyId,
                    storyState.PlayerId,
                    eventId,
                    eventResult);

                _logger.LogInformation($"Story event triggered: {eventId} in story {storyId}");

                // Result oluştur;
                var result = new StoryEventResult;
                {
                    Success = true,
                    StoryEvent = storyEvent,
                    EventResult = eventResult,
                    StoryState = storyState,
                    Timestamp = DateTime.UtcNow;
                };

                // Event tetikle;
                StoryEventTriggered?.Invoke(this, new StoryEventTriggeredEventArgs;
                {
                    StoryId = storyId,
                    Story = story,
                    StoryEvent = storyEvent,
                    Result = result,
                    StoryState = storyState;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to trigger event {eventId} in story {storyId}");
                throw new StoryEventException($"Failed to trigger event: {ex.Message}", ex);
            }
            finally
            {
                _storyAccessSemaphore.Release();
            }
        }

        /// <summary>
        /// Dinamik hikaye içeriği oluşturur;
        /// </summary>
        public async Task<DynamicContentResult> GenerateDynamicContentAsync(
            string storyId,
            ContentRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                // Story state'i yükle;
                if (!_storyStates.TryGetValue(storyId, out var storyState))
                {
                    storyState = await LoadStoryStateAsync(storyId, cancellationToken);
                    if (storyState == null)
                    {
                        throw new StoryStateNotFoundException($"Story state for {storyId} not found");
                    }
                }

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {storyId} not found");
                }

                // Context oluştur;
                var context = BuildGenerationContext(story, storyState, request);

                // Dynamic content oluştur;
                var generatedContent = await _storyGenerator.GenerateContentAsync(
                    request.ContentType,
                    context,
                    cancellationToken);

                // Content'i validate et;
                var validationResult = ValidateGeneratedContent(generatedContent, request);

                if (!validationResult.IsValid && !request.AllowInvalidContent)
                {
                    throw new ContentGenerationException(
                        $"Generated content failed validation: {validationResult.Errors.First()}");
                }

                // Content'i optimize et;
                if (request.OptimizeContent)
                {
                    generatedContent = await OptimizeGeneratedContentAsync(
                        generatedContent,
                        request,
                        cancellationToken);
                }

                _logger.LogInformation($"Dynamic content generated for story {storyId}: {request.ContentType}");

                return new DynamicContentResult;
                {
                    Success = true,
                    GeneratedContent = generatedContent,
                    Context = context,
                    ValidationResult = validationResult,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate dynamic content for story {storyId}");
                throw new ContentGenerationException($"Failed to generate content: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Hikaye analitiği getirir;
        /// </summary>
        public async Task<StoryAnalytics> GetStoryAnalyticsAsync(
            string storyId,
            AnalyticsOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            try
            {
                options ??= AnalyticsOptions.Default;

                // Analytics verilerini topla;
                var analytics = await _analyticsEngine.GetStoryAnalyticsAsync(
                    storyId,
                    options,
                    cancellationToken);

                // Story state'ini yükle;
                if (_storyStates.TryGetValue(storyId, out var storyState))
                {
                    analytics.CurrentState = storyState;
                }

                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story != null)
                {
                    analytics.StoryInfo = new StoryInfo;
                    {
                        Id = story.Id,
                        Title = story.Title,
                        NodeCount = story.Nodes.Count,
                        BranchCount = story.Nodes.Sum(n => n.Branches?.Count ?? 0),
                        EventCount = story.Events?.Count ?? 0;
                    };
                }

                // Player istatistikleri;
                if (storyState != null)
                {
                    analytics.PlayerStats = CalculatePlayerStats(storyId, storyState);
                }

                return analytics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get analytics for story {storyId}");
                throw new AnalyticsException($"Failed to get analytics: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Hikaye puanlaması ve değerlendirmesi yapar;
        /// </summary>
        public async Task<StoryRating> RateStoryAsync(
            string storyId,
            RatingRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrEmpty(storyId))
                throw new ArgumentException("Story ID cannot be null or empty", nameof(storyId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                // Story'i yükle;
                var story = await GetOrLoadStoryAsync(storyId, cancellationToken);
                if (story == null)
                {
                    throw new StoryNotFoundException($"Story {storyId} not found");
                }

                // Rating oluştur;
                var rating = new StoryRating;
                {
                    StoryId = storyId,
                    PlayerId = request.PlayerId,
                    OverallRating = request.OverallRating,
                    CategoryRatings = request.CategoryRatings,
                    Comments = request.Comments,
                    RatedAt = DateTime.UtcNow,
                    PlayTime = request.PlayTime,
                    CompletionStatus = request.CompletionStatus;
                };

                // NLP analizi;
                if (!string.IsNullOrEmpty(request.Comments))
                {
                    var sentiment = await _nlpEngine.AnalyzeSentimentAsync(request.Comments, cancellationToken);
                    rating.SentimentScore = sentiment.Score;
                    rating.Keywords = await _nlpEngine.ExtractKeywordsAsync(request.Comments, cancellationToken);
                }

                // Rating'i kaydet;
                await SaveStoryRatingAsync(rating, cancellationToken);

                // Story rating'ini güncelle;
                await UpdateStoryAggregateRatingAsync(storyId, rating, cancellationToken);

                _logger.LogInformation($"Story rated: {storyId} by player {request.PlayerId}");

                return rating;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to rate story {storyId}");
                throw new RatingException($"Failed to rate story: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sistem durumunu raporlar;
        /// </summary>
        public StorySystemStatus GetSystemStatus()
        {
            ValidateInitialized();

            return new StorySystemStatus;
            {
                IsInitialized = _isInitialized,
                ActiveStoriesCount = _storyStates.Count,
                TotalStoriesCount = _stories.Count,
                TotalSaveGames = CountSaveGames(),
                AnalyticsEngineStatus = _analyticsEngine.GetStatus(),
                GeneratorStatus = _storyGenerator.GetStatus(),
                MemoryUsage = CalculateMemoryUsage(),
                LastOperationTime = DateTime.UtcNow;
            };
        }

        #region Private Methods;

        private async Task EnsureDirectoriesExistAsync(CancellationToken cancellationToken)
        {
            var directories = new[]
            {
                Path.GetDirectoryName(_storyDatabasePath),
                _storyTemplatesPath,
                _saveGamePath,
                Path.Combine(_saveGamePath, "AutoSaves"),
                Path.Combine(_saveGamePath, "QuickSaves"),
                Path.Combine(_saveGamePath, "ManualSaves")
            };

            foreach (var directory in directories)
            {
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation($"Created directory: {directory}");
                }
            }

            await Task.CompletedTask;
        }

        private async Task LoadStoryDatabaseAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_storyDatabasePath))
            {
                _logger.LogWarning($"Story database not found at {_storyDatabasePath}. Creating new database.");
                await SaveStoryDatabaseAsync(cancellationToken);
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_storyDatabasePath);
                var database = JsonSerializer.Deserialize<StoryDatabase>(json, _jsonOptions);

                if (database?.Stories != null)
                {
                    foreach (var storyEntry in database.Stories)
                    {
                        try
                        {
                            var story = await LoadStoryFromFileAsync(storyEntry.FilePath, cancellationToken);
                            if (story != null)
                            {
                                _stories[story.Id] = story;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to load story {storyEntry.Id} from database");
                        }
                    }

                    _logger.LogInformation($"Loaded {_stories.Count} stories from database");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load story database");
                throw;
            }
        }

        private async Task LoadSavedGamesAsync(CancellationToken cancellationToken)
        {
            var saveFiles = Directory.GetFiles(_saveGamePath, "*.save", SearchOption.AllDirectories);

            int loadedCount = 0;
            foreach (var saveFile in saveFiles.Take(100)) // Son 100 kayıt;
            {
                try
                {
                    var saveGame = await LoadSaveGameFromFileAsync(saveFile, cancellationToken);
                    if (saveGame != null)
                    {
                        // En son kayıtları tut;
                        loadedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to load save game from {saveFile}");
                }
            }

            _logger.LogInformation($"Loaded {loadedCount} save games");
        }

        private async Task<Story> GetOrLoadStoryAsync(string storyId, CancellationToken cancellationToken)
        {
            if (_stories.TryGetValue(storyId, out var story))
                return story;

            // Database'den yükle;
            var storyPath = Path.Combine(
                _storyTemplatesPath,
                $"{storyId}.story");

            story = await LoadStoryFromFileAsync(storyPath, cancellationToken);
            if (story != null)
            {
                _stories[storyId] = story;
            }

            return story;
        }

        private async Task<Story> LoadStoryFromFileAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var story = JsonSerializer.Deserialize<Story>(json, _jsonOptions);

                if (story != null)
                {
                    // Story'yi normalize et;
                    NormalizeStory(story);
                    return story;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load story from {filePath}");
                return null;
            }
        }

        private async Task<StoryState> LoadStoryStateAsync(string storyId, CancellationToken cancellationToken)
        {
            var statePath = Path.Combine(
                _saveGamePath,
                "Current",
                $"{storyId}.state");

            if (!File.Exists(statePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(statePath);
                var state = JsonSerializer.Deserialize<StoryState>(json, _jsonOptions);
                return state;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load story state from {statePath}");
                return null;
            }
        }

        private async Task SaveStoryStateAsync(StoryState state, CancellationToken cancellationToken)
        {
            var statePath = Path.Combine(
                _saveGamePath,
                "Current",
                $"{state.StoryId}.state");

            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(statePath, json);
        }

        private async Task SaveStoryDatabaseAsync(CancellationToken cancellationToken)
        {
            var database = new StoryDatabase;
            {
                Version = "1.0",
                LastUpdated = DateTime.UtcNow,
                Stories = _stories.Values.Select(s => new StoryDatabaseEntry
                {
                    Id = s.Id,
                    Title = s.Title,
                    FilePath = Path.Combine(_storyTemplatesPath, $"{s.Id}.story"),
                    CreatedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow;
                }).ToList()
            };

            var json = JsonSerializer.Serialize(database, _jsonOptions);
            await File.WriteAllTextAsync(_storyDatabasePath, json);
        }

        private StoryState CreateInitialStoryState(Story story, StartStoryOptions options)
        {
            var startNode = story.Nodes.FirstOrDefault(n => n.Type == StoryNodeType.Start);
            if (startNode == null)
            {
                throw new StoryValidationException("Story must have a start node");
            }

            return new StoryState;
            {
                StoryId = story.Id,
                PlayerId = options.PlayerId ?? Guid.NewGuid().ToString(),
                CurrentNodeId = startNode.Id,
                StartTime = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Variables = new Dictionary<string, object>(),
                Inventory = new List<StoryItem>(),
                Relationships = new Dictionary<string, Relationship>(),
                Achievements = new List<Achievement>(),
                ChoicesHistory = new List<ChoiceRecord>(),
                EventsHistory = new List<EventRecord>(),
                PlayTime = TimeSpan.Zero,
                Metadata = new Dictionary<string, object>
                {
                    ["difficulty"] = options.Difficulty.ToString(),
                    ["language"] = options.Language,
                    ["version"] = story.Version;
                }
            };
        }

        private async Task ApplyPlayerProfileAsync(
            StoryState state,
            PlayerProfile profile,
            CancellationToken cancellationToken)
        {
            // Player özelliklerini uygula;
            state.PlayerName = profile.Name;
            state.PlayerTraits = profile.Traits;

            // Initial variables;
            if (profile.InitialVariables != null)
            {
                foreach (var variable in profile.InitialVariables)
                {
                    state.Variables[variable.Key] = variable.Value;
                }
            }

            // Initial inventory;
            if (profile.InitialInventory != null)
            {
                state.Inventory.AddRange(profile.InitialInventory);
            }

            // Memory'ye kaydet;
            await _memorySystem.StoreAsync(
                $"player_profile_{state.PlayerId}",
                profile,
                cancellationToken);
        }

        private async Task InitializeStoryEventsAsync(
            string storyId,
            Story story,
            CancellationToken cancellationToken)
        {
            if (story.Events == null)
                return;

            _storyEvents[storyId] = new List<StoryEvent>();

            foreach (var storyEvent in story.Events)
            {
                if (storyEvent.TriggerType == EventTriggerType.OnStart)
                {
                    // Başlangıç event'lerini tetikle;
                    await TriggerStoryEventAsync(storyId, storyEvent.Id, cancellationToken: cancellationToken);
                }
                else;
                {
                    // Diğer event'leri kaydet;
                    _storyEvents[storyId].Add(storyEvent);
                }
            }
        }

        private async Task<ChoiceResult> ProcessChoiceAsync(
            StoryNode currentNode,
            StoryChoice choice,
            StoryState currentState,
            ProgressOptions options,
            CancellationToken cancellationToken)
        {
            var result = new ChoiceResult;
            {
                ChoiceId = choice.Id,
                Success = true,
                Timestamp = DateTime.UtcNow;
            };

            // Choice effects'lerini uygula;
            if (choice.Effects != null)
            {
                foreach (var effect in choice.Effects)
                {
                    await ApplyEffectAsync(effect, currentState, cancellationToken);
                }
            }

            // Next node'u belirle;
            if (!string.IsNullOrEmpty(choice.NextNodeId))
            {
                result.NextNodeId = choice.NextNodeId;
            }
            else if (currentNode.Connections != null && currentNode.Connections.Any())
            {
                // Default connection;
                result.NextNodeId = currentNode.Connections.First().TargetNodeId;
            }
            else;
            {
                throw new StoryProgressException($"No next node defined for choice {choice.Id}");
            }

            // Skill check'leri yap;
            if (choice.RequiredSkills != null && choice.RequiredSkills.Any())
            {
                result.SkillCheckResult = await CheckSkillsAsync(
                    choice.RequiredSkills,
                    currentState,
                    cancellationToken);

                if (!result.SkillCheckResult.Success && !options.IgnoreSkillChecks)
                {
                    result.Success = false;
                    result.NextNodeId = choice.FailureNodeId ?? result.NextNodeId;
                }
            }

            // RNG check'leri;
            if (choice.SuccessChance < 1.0f)
            {
                var random = new Random();
                if (random.NextDouble() > choice.SuccessChance)
                {
                    result.Success = false;
                    result.NextNodeId = choice.FailureNodeId ?? result.NextNodeId;
                    result.RandomCheckFailed = true;
                }
            }

            return result;
        }

        private void UpdateStoryState(
            StoryState state,
            StoryChoice choice,
            ChoiceResult result,
            StoryNode nextNode)
        {
            // Current node'u güncelle;
            state.CurrentNodeId = nextNode.Id;
            state.LastUpdated = DateTime.UtcNow;

            // Choice'ı history'e ekle;
            state.ChoicesHistory.Add(new ChoiceRecord;
            {
                ChoiceId = choice.Id,
                NodeId = state.CurrentNodeId,
                Timestamp = DateTime.UtcNow,
                Success = result.Success,
                SkillCheckResult = result.SkillCheckResult;
            });

            // Play time'ı güncelle;
            state.PlayTime = state.PlayTime.Add(DateTime.UtcNow - state.LastUpdated);

            // Visited nodes;
            if (!state.VisitedNodeIds.Contains(nextNode.Id))
            {
                state.VisitedNodeIds.Add(nextNode.Id);
            }

            // Recent events'i temizle;
            state.RecentEvents.Clear();
        }

        private async Task CheckAndTriggerEventsAsync(
            string storyId,
            StoryState state,
            CancellationToken cancellationToken)
        {
            if (!_storyEvents.TryGetValue(storyId, out var events))
                return;

            var triggeredEvents = new List<StoryEvent>();

            foreach (var storyEvent in events)
            {
                if (CheckEventConditions(storyEvent, state))
                {
                    await TriggerStoryEventAsync(
                        storyId,
                        storyEvent.Id,
                        new EventTriggerOptions { ForceTrigger = false },
                        cancellationToken);

                    triggeredEvents.Add(storyEvent);

                    // One-time event'leri kaldır;
                    if (storyEvent.MaxTriggerCount == 1)
                    {
                        events.Remove(storyEvent);
                    }
                }
            }

            // Triggered event'leri state'e kaydet;
            foreach (var triggeredEvent in triggeredEvents)
            {
                state.RecentEvents.Add(triggeredEvent.Id);
            }
        }

        private async Task CheckAchievementsAsync(
            string storyId,
            StoryState state,
            CancellationToken cancellationToken)
        {
            // Achievement kontrolü;
            // Gerçek implementasyonda achievement sistemine bağlanır;
            await Task.CompletedTask;
        }

        private async Task HandleStoryEndingAsync(
            string storyId,
            StoryState state,
            StoryNode endingNode,
            CancellationToken cancellationToken)
        {
            // Ending işlemleri;
            state.IsCompleted = true;
            state.CompletionTime = DateTime.UtcNow;
            state.EndingId = endingNode.Id;

            // Analytics kaydı;
            _analyticsEngine.RecordStoryCompletion(
                storyId,
                state.PlayerId,
                endingNode.Id,
                state.PlayTime);

            // Final save;
            await SaveGameAsync(storyId, $"Ending_{endingNode.Id}", cancellationToken: cancellationToken);

            // Event tetikle;
            StoryEnded?.Invoke(this, new StoryEndedEventArgs;
            {
                StoryId = storyId,
                StoryState = state,
                EndingNode = endingNode,
                CompletionTime = DateTime.UtcNow,
                PlayTime = state.PlayTime;
            });

            _logger.LogInformation($"Story completed: {storyId} with ending {endingNode.Id}");
        }

        private async Task<SaveGame> LoadSaveGameAsync(string saveGameId, CancellationToken cancellationToken)
        {
            var saveFiles = Directory.GetFiles(_saveGamePath, "*.save", SearchOption.AllDirectories);

            foreach (var saveFile in saveFiles)
            {
                try
                {
                    var saveGame = await LoadSaveGameFromFileAsync(saveFile, cancellationToken);
                    if (saveGame != null && saveGame.Id == saveGameId)
                    {
                        return saveGame;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to check save game file {saveFile}");
                }
            }

            return null;
        }

        private async Task<SaveGame> LoadSaveGameFromFileAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var saveGame = JsonSerializer.Deserialize<SaveGame>(json, _jsonOptions);
                return saveGame;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load save game from {filePath}");
                return null;
            }
        }

        private async Task<string> SaveGameToFileAsync(SaveGame saveGame, CancellationToken cancellationToken)
        {
            var saveDir = Path.Combine(_saveGamePath, "ManualSaves");
            var savePath = Path.Combine(saveDir, $"{saveGame.SaveName}_{saveGame.Id}.save");

            var json = JsonSerializer.Serialize(saveGame, _jsonOptions);
            await File.WriteAllTextAsync(savePath, json);

            return savePath;
        }

        private void AddToUndoStack(string storyId, StoryState state)
        {
            var undoStack = GetUndoStack(storyId);
            undoStack.Push(state);

            // Max undo steps kontrolü;
            while (undoStack.Count > MAX_UNDO_STEPS)
            {
                undoStack.Pop();
            }
        }

        private Stack<StoryState> GetUndoStack(string storyId)
        {
            if (!_undoStacks.ContainsKey(storyId))
                _undoStacks[storyId] = new Stack<StoryState>();

            return _undoStacks[storyId];
        }

        private void AddToRedoStack(string storyId, StoryState state)
        {
            var redoStack = GetRedoStack(storyId);
            redoStack.Push(state);
        }

        private Stack<StoryState> GetRedoStack(string storyId)
        {
            if (!_redoStacks.ContainsKey(storyId))
                _redoStacks[storyId] = new Stack<StoryState>();

            return _redoStacks[storyId];
        }

        private async Task<byte[]> CaptureGameScreenshotAsync(CancellationToken cancellationToken)
        {
            // Screenshot alma işlemi;
            // Gerçek implementasyonda grafik motoruna bağlanır;
            await Task.Delay(100, cancellationToken);
            return new byte[0];
        }

        private void AutoSaveCallback(object state)
        {
            try
            {
                if (_isInitialized && _storyStates.Any())
                {
                    foreach (var storyState in _storyStates.Values)
                    {
                        _ = SaveGameAsync(
                            storyState.StoryId,
                            $"AutoSave_{DateTime.UtcNow:HHmm}",
                            new SaveOptions { SaveType = SaveType.AutoSave },
                            CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-save failed");
            }
        }

        private long CalculateMemoryUsage()
        {
            long total = 0;

            // Stories memory;
            foreach (var story in _stories.Values)
            {
                total += story.EstimatedMemoryUsage;
            }

            // States memory;
            foreach (var state in _storyStates.Values)
            {
                total += state.EstimatedMemoryUsage;
            }

            // Events memory;
            foreach (var eventList in _storyEvents.Values)
            {
                total += eventList.Sum(e => e.EstimatedMemoryUsage);
            }

            return total;
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StoryManager is not initialized. Call InitializeAsync first.");
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;
        private readonly Dictionary<string, Stack<StoryState>> _undoStacks = new Dictionary<string, Stack<StoryState>>();
        private readonly Dictionary<string, Stack<StoryState>> _redoStacks = new Dictionary<string, Stack<StoryState>>();

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Auto-save timer'ı durdur;
                    _autoSaveTimer?.Dispose();

                    // Son kayıt işlemleri;
                    if (_isInitialized)
                    {
                        try
                        {
                            // Auto-save yap;
                            AutoSaveCallback(null);

                            // Database'i kaydet;
                            SaveStoryDatabaseAsync(CancellationToken.None).Wait(5000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to perform final save operations");
                        }
                    }

                    // Semaphore'ı serbest bırak;
                    _storyAccessSemaphore?.Dispose();

                    // Analytics engine'i temizle;
                    _analyticsEngine?.Dispose();

                    // Generator'ı temizle;
                    _storyGenerator?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~StoryManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface IStoryManager;
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<StoryState> StartStoryAsync(
            string storyId,
            StartStoryOptions options = null,
            CancellationToken cancellationToken = default);
        Task<StoryProgressResult> ProgressStoryAsync(
            string storyId,
            StoryChoice choice,
            ProgressOptions options = null,
            CancellationToken cancellationToken = default);
        Task<BranchResult> SelectBranchAsync(
            string storyId,
            string branchId,
            BranchSelectionOptions options = null,
            CancellationToken cancellationToken = default);
        Task<SaveGameResult> SaveGameAsync(
            string storyId,
            string saveName = null,
            SaveOptions options = null,
            CancellationToken cancellationToken = default);
        Task<StoryState> LoadGameAsync(
            string saveGameId,
            LoadOptions options = null,
            CancellationToken cancellationToken = default);
        Task<UndoResult> UndoLastStepAsync(
            string storyId,
            CancellationToken cancellationToken = default);
        Task<StoryEventResult> TriggerStoryEventAsync(
            string storyId,
            string eventId,
            EventTriggerOptions options = null,
            CancellationToken cancellationToken = default);
        Task<DynamicContentResult> GenerateDynamicContentAsync(
            string storyId,
            ContentRequest request,
            CancellationToken cancellationToken = default);
        Task<StoryAnalytics> GetStoryAnalyticsAsync(
            string storyId,
            AnalyticsOptions options = null,
            CancellationToken cancellationToken = default);
        Task<StoryRating> RateStoryAsync(
            string storyId,
            RatingRequest request,
            CancellationToken cancellationToken = default);
        StorySystemStatus GetSystemStatus();

        // Events;
        event EventHandler<StoryStartedEventArgs> StoryStarted;
        event EventHandler<StoryProgressedEventArgs> StoryProgressed;
        event EventHandler<BranchSelectedEventArgs> BranchSelected;
        event EventHandler<StoryEndedEventArgs> StoryEnded;
        event EventHandler<StoryEventTriggeredEventArgs> StoryEventTriggered;
        event EventHandler<PlayerChoiceMadeEventArgs> PlayerChoiceMade;
    }

    public class Story;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string Version { get; set; } = "1.0.0";
        public List<string> Tags { get; set; } = new List<string>();
        public List<StoryNode> Nodes { get; set; } = new List<StoryNode>();
        public List<StoryEvent> Events { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        public StoryNode GetNode(string nodeId)
        {
            return Nodes.FirstOrDefault(n => n.Id == nodeId);
        }

        public long EstimatedMemoryUsage =>
            Nodes.Sum(n => n.EstimatedMemoryUsage) +
            (Events?.Sum(e => e.EstimatedMemoryUsage) ?? 0);
    }

    public class StoryNode;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public StoryNodeType Type { get; set; }
        public List<StoryChoice> Choices { get; set; } = new List<StoryChoice>();
        public List<StoryBranch> Branches { get; set; }
        public List<NodeConnection> Connections { get; set; }
        public List<StoryCondition> Conditions { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public long EstimatedMemoryUsage =>
            Content?.Length * 2 ?? 0 +
            Choices?.Sum(c => c.EstimatedMemoryUsage) ?? 0 +
            Branches?.Sum(b => b.EstimatedMemoryUsage) ?? 0;
    }

    public class StoryChoice;
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string NextNodeId { get; set; }
        public string FailureNodeId { get; set; }
        public List<StoryEffect> Effects { get; set; }
        public List<SkillRequirement> RequiredSkills { get; set; }
        public float SuccessChance { get; set; } = 1.0f;
        public bool IsHidden { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public long EstimatedMemoryUsage => Text?.Length * 2 ?? 0;
    }

    public class StoryBranch;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string NextNodeId { get; set; }
        public List<StoryCondition> Conditions { get; set; }
        public List<StoryEffect> Effects { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public long EstimatedMemoryUsage =>
            Name?.Length * 2 ?? 0 +
            Description?.Length * 2 ?? 0;
    }

    public class StoryEvent;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public EventTriggerType TriggerType { get; set; }
        public List<StoryCondition> Conditions { get; set; }
        public List<StoryEffect> Effects { get; set; }
        public int MaxTriggerCount { get; set; } = 1;
        public int CurrentTriggerCount { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public long EstimatedMemoryUsage =>
            Name?.Length * 2 ?? 0 +
            Description?.Length * 2 ?? 0;
    }

    public class StoryState : ICloneable;
    {
        public string StoryId { get; set; }
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public string CurrentNodeId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? CompletionTime { get; set; }
        public TimeSpan PlayTime { get; set; }
        public bool IsCompleted { get; set; }
        public string EndingId { get; set; }
        public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();
        public List<StoryItem> Inventory { get; set; } = new List<StoryItem>();
        public Dictionary<string, Relationship> Relationships { get; set; } = new Dictionary<string, Relationship>();
        public List<Achievement> Achievements { get; set; } = new List<Achievement>();
        public List<ChoiceRecord> ChoicesHistory { get; set; } = new List<ChoiceRecord>();
        public List<EventRecord> EventsHistory { get; set; } = new List<EventRecord>();
        public List<string> VisitedNodeIds { get; set; } = new List<string>();
        public List<string> RecentEvents { get; set; } = new List<string>();
        public Dictionary<string, object> PlayerTraits { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public object Clone()
        {
            return new StoryState;
            {
                StoryId = this.StoryId,
                PlayerId = this.PlayerId,
                PlayerName = this.PlayerName,
                CurrentNodeId = this.CurrentNodeId,
                StartTime = this.StartTime,
                LastUpdated = this.LastUpdated,
                CompletionTime = this.CompletionTime,
                PlayTime = this.PlayTime,
                IsCompleted = this.IsCompleted,
                EndingId = this.EndingId,
                Variables = new Dictionary<string, object>(this.Variables),
                Inventory = this.Inventory.Select(i => i.Clone()).ToList(),
                Relationships = new Dictionary<string, Relationship>(this.Relationships),
                Achievements = this.Achievements.Select(a => a.Clone()).ToList(),
                ChoicesHistory = this.ChoicesHistory.Select(c => c.Clone()).ToList(),
                EventsHistory = this.EventsHistory.Select(e => e.Clone()).ToList(),
                VisitedNodeIds = new List<string>(this.VisitedNodeIds),
                RecentEvents = new List<string>(this.RecentEvents),
                PlayerTraits = new Dictionary<string, object>(this.PlayerTraits),
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }

        public long EstimatedMemoryUsage;
        {
            get;
            {
                long total = 0;

                total += Variables.Sum(kvp =>
                    kvp.Key.Length * 2 + GetObjectSize(kvp.Value));

                total += Inventory.Sum(i => i.EstimatedMemoryUsage);
                total += Relationships.Sum(kvp =>
                    kvp.Key.Length * 2 + kvp.Value.EstimatedMemoryUsage);

                total += ChoicesHistory.Sum(c => c.EstimatedMemoryUsage);
                total += EventsHistory.Sum(e => e.EstimatedMemoryUsage);

                return total;
            }
        }

        private long GetObjectSize(object obj)
        {
            if (obj == null) return 0;
            if (obj is string str) return str.Length * 2;
            if (obj is int) return 4;
            if (obj is long) return 8;
            if (obj is double) return 8;
            if (obj is bool) return 1;
            return 16; // Default estimate;
        }
    }

    // Result Classes;
    public class StoryProgressResult;
    {
        public bool Success { get; set; }
        public StoryNode PreviousNode { get; set; }
        public StoryNode NextNode { get; set; }
        public StoryChoice Choice { get; set; }
        public ChoiceResult ChoiceResult { get; set; }
        public StoryState NewStoryState { get; set; }
        public DateTime Timestamp { get; set; }
        public List<string> EventsTriggered { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
    }

    public class BranchResult;
    {
        public bool Success { get; set; }
        public StoryBranch Branch { get; set; }
        public BranchSelectionResult BranchResult { get; set; }
        public StoryNode NextNode { get; set; }
        public StoryState NewStoryState { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class SaveGameResult;
    {
        public bool Success { get; set; }
        public SaveGame SaveGame { get; set; }
        public string SavePath { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class UndoResult;
    {
        public bool Success { get; set; }
        public StoryState PreviousState { get; set; }
        public StoryState CurrentState { get; set; }
        public int RemainingUndoSteps { get; set; }
        public bool CanUndo { get; set; }
        public bool CanRedo { get; set; }
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
    }

    public class StoryEventResult;
    {
        public bool Success { get; set; }
        public StoryEvent StoryEvent { get; set; }
        public EventExecutionResult EventResult { get; set; }
        public StoryState StoryState { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class DynamicContentResult;
    {
        public bool Success { get; set; }
        public GeneratedContent GeneratedContent { get; set; }
        public GenerationContext Context { get; set; }
        public ContentValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class StoryAnalytics;
    {
        public string StoryId { get; set; }
        public StoryInfo StoryInfo { get; set; }
        public StoryState CurrentState { get; set; }
        public PlayerStats PlayerStats { get; set; }
        public List<AnalyticsDataPoint> ChoiceAnalytics { get; set; } = new List<AnalyticsDataPoint>();
        public List<AnalyticsDataPoint> TimeAnalytics { get; set; } = new List<AnalyticsDataPoint>();
        public List<AnalyticsDataPoint> EventAnalytics { get; set; } = new List<AnalyticsDataPoint>();
        public Dictionary<string, object> Summary { get; set; } = new Dictionary<string, object>();
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }

    public class StoryRating;
    {
        public string StoryId { get; set; }
        public string PlayerId { get; set; }
        public float OverallRating { get; set; }
        public Dictionary<string, float> CategoryRatings { get; set; } = new Dictionary<string, float>();
        public string Comments { get; set; }
        public float SentimentScore { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
        public TimeSpan PlayTime { get; set; }
        public CompletionStatus CompletionStatus { get; set; }
        public DateTime RatedAt { get; set; } = DateTime.UtcNow;
    }

    public class StorySystemStatus;
    {
        public bool IsInitialized { get; set; }
        public int ActiveStoriesCount { get; set; }
        public int TotalStoriesCount { get; set; }
        public int TotalSaveGames { get; set; }
        public EngineStatus AnalyticsEngineStatus { get; set; }
        public EngineStatus GeneratorStatus { get; set; }
        public long MemoryUsage { get; set; }
        public DateTime LastOperationTime { get; set; }
    }

    // Options Classes;
    public class StartStoryOptions;
    {
        public static StartStoryOptions Default => new StartStoryOptions();

        public string PlayerId { get; set; }
        public PlayerProfile PlayerProfile { get; set; }
        public StoryDifficulty Difficulty { get; set; } = StoryDifficulty.Normal;
        public string Language { get; set; } = "en";
        public bool SkipIntro { get; set; } = false;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class ProgressOptions;
    {
        public static ProgressOptions Default => new ProgressOptions();

        public bool IgnoreSkillChecks { get; set; } = false;
        public bool AutoSave { get; set; } = true;
        public bool RecordAnalytics { get; set; } = true;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class BranchSelectionOptions;
    {
        public static BranchSelectionOptions Default => new BranchSelectionOptions();

        public bool ForceSelect { get; set; } = false;
        public bool ApplyEffects { get; set; } = true;
        public bool RecordAnalytics { get; set; } = true;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class SaveOptions;
    {
        public static SaveOptions Default => new SaveOptions();

        public SaveType SaveType { get; set; } = SaveType.Manual;
        public bool IncludeScreenshot { get; set; } = true;
        public bool CompressSave { get; set; } = true;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class LoadOptions;
    {
        public static LoadOptions Default => new LoadOptions();

        public bool ForceLoad { get; set; } = false;
        public bool RestoreEvents { get; set; } = true;
        public bool ValidateSave { get; set; } = true;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class EventTriggerOptions;
    {
        public static EventTriggerOptions Default => new EventTriggerOptions();

        public bool ForceTrigger { get; set; } = false;
        public bool ApplyEffects { get; set; } = true;
        public bool RecordAnalytics { get; set; } = true;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class AnalyticsOptions;
    {
        public static AnalyticsOptions Default => new AnalyticsOptions();

        public TimeRange TimeRange { get; set; } = TimeRange.AllTime;
        public int MaxDataPoints { get; set; } = 1000;
        public List<AnalyticsDimension> Dimensions { get; set; } = new List<AnalyticsDimension>();
        public Dictionary<string, object> Filters { get; set; } = new Dictionary<string, object>();
    }

    // Event Args Classes;
    public class StoryStartedEventArgs : EventArgs;
    {
        public string StoryId { get; set; }
        public Story Story { get; set; }
        public StoryState StoryState { get; set; }
        public DateTime StartTime { get; set; }
        public StartStoryOptions Options { get; set; }
    }

    public class StoryProgressedEventArgs : EventArgs;
    {
        public string StoryId { get; set; }
        public Story Story { get; set; }
        public StoryNode PreviousNode { get; set; }
        public StoryNode NextNode { get; set; }
        public StoryChoice Choice { get; set; }
        public StoryProgressResult Result { get; set; }
        public StoryState StoryState { get; set; }
    }

    public class BranchSelectedEventArgs : EventArgs;
    {
        public string StoryId { get; set; }
        public Story Story { get; set; }
        public StoryBranch Branch { get; set; }
        public BranchResult Result { get; set; }
        public StoryState StoryState { get; set; }
    }

    public class StoryEndedEventArgs : EventArgs;
    {
        public string StoryId { get; set; }
        public StoryState StoryState { get; set; }
        public StoryNode EndingNode { get; set; }
        public DateTime CompletionTime { get; set; }
        public TimeSpan PlayTime { get; set; }
    }

    public class StoryEventTriggeredEventArgs : EventArgs;
    {
        public string StoryId { get; set; }
        public Story Story { get; set; }
        public StoryEvent StoryEvent { get; set; }
        public StoryEventResult Result { get; set; }
        public StoryState StoryState { get; set; }
    }

    public class PlayerChoiceMadeEventArgs : EventArgs;
    {
        public string StoryId { get; set; }
        public string PlayerId { get; set; }
        public StoryChoice Choice { get; set; }
        public ChoiceResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Enums;
    public enum StoryNodeType { Start, Dialog, Choice, Branching, Event, Ending }
    public enum EventTriggerType { OnStart, OnChoice, OnCondition, Manual, Timer }
    public enum StoryDifficulty { VeryEasy, Easy, Normal, Hard, VeryHard }
    public enum SaveType { Manual, QuickSave, AutoSave }
    public enum CompletionStatus { NotStarted, InProgress, Completed, Abandoned }
    public enum TimeRange { LastHour, LastDay, LastWeek, LastMonth, LastYear, AllTime }
    public enum AnalyticsDimension { Player, Choice, Time, Event, Branch }

    // Exceptions;
    public class StoryManagerException : Exception
    {
        public StoryManagerException(string message) : base(message) { }
        public StoryManagerException(string message, Exception inner) : base(message, inner) { }
    }

    public class StoryStartException : StoryManagerException;
    {
        public StoryStartException(string message) : base(message) { }
        public StoryStartException(string message, Exception inner) : base(message, inner) { }
    }

    public class StoryProgressException : StoryManagerException;
    {
        public StoryProgressException(string message) : base(message) { }
        public StoryProgressException(string message, Exception inner) : base(message, inner) { }
    }

    public class BranchSelectionException : StoryManagerException;
    {
        public BranchSelectionException(string message) : base(message) { }
        public BranchSelectionException(string message, Exception inner) : base(message, inner) { }
    }

    public class SaveGameException : StoryManagerException;
    {
        public SaveGameException(string message) : base(message) { }
        public SaveGameException(string message, Exception inner) : base(message, inner) { }
    }

    public class LoadGameException : StoryManagerException;
    {
        public LoadGameException(string message) : base(message) { }
        public LoadGameException(string message, Exception inner) : base(message, inner) { }
    }

    public class UndoException : StoryManagerException;
    {
        public UndoException(string message) : base(message) { }
        public UndoException(string message, Exception inner) : base(message, inner) { }
    }

    public class StoryEventException : StoryManagerException;
    {
        public StoryEventException(string message) : base(message) { }
        public StoryEventException(string message, Exception inner) : base(message, inner) { }
    }

    public class ContentGenerationException : StoryManagerException;
    {
        public ContentGenerationException(string message) : base(message) { }
        public ContentGenerationException(string message, Exception inner) : base(message, inner) { }
    }

    public class AnalyticsException : StoryManagerException;
    {
        public AnalyticsException(string message) : base(message) { }
        public AnalyticsException(string message, Exception inner) : base(message, inner) { }
    }

    public class RatingException : StoryManagerException;
    {
        public RatingException(string message) : base(message) { }
        public RatingException(string message, Exception inner) : base(message, inner) { }
    }

    public class StoryNotFoundException : StoryManagerException;
    {
        public StoryNotFoundException(string message) : base(message) { }
    }

    public class StoryStateNotFoundException : StoryManagerException;
    {
        public StoryStateNotFoundException(string message) : base(message) { }
    }

    public class StoryNodeNotFoundException : StoryManagerException;
    {
        public StoryNodeNotFoundException(string message) : base(message) { }
    }

    public class BranchNotFoundException : StoryManagerException;
    {
        public BranchNotFoundException(string message) : base(message) { }
    }

    public class StoryEventNotFoundException : StoryManagerException;
    {
        public StoryEventNotFoundException(string message) : base(message) { }
    }

    public class SaveGameNotFoundException : StoryManagerException;
    {
        public SaveGameNotFoundException(string message) : base(message) { }
    }

    public class StoryValidationException : StoryManagerException;
    {
        public StoryValidationException(string message) : base(message) { }
    }

    public class InvalidStoryNodeException : StoryManagerException;
    {
        public InvalidStoryNodeException(string message) : base(message) { }
    }

    public class BranchConditionException : StoryManagerException;
    {
        public BranchConditionException(string message) : base(message) { }
    }

    public class EventConditionException : StoryManagerException;
    {
        public EventConditionException(string message) : base(message) { }
    }

    public class InvalidSaveGameException : StoryManagerException;
    {
        public InvalidSaveGameException(string message) : base(message) { }
    }

    #endregion;

    #region Internal Helper Classes;

    internal class StoryAnalyticsEngine : IDisposable
    {
        private Dictionary<string, StoryAnalyticsData> _analyticsData;
        private bool _isInitialized;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            _analyticsData = new Dictionary<string, StoryAnalyticsData>();
            _isInitialized = true;
            return Task.CompletedTask;
        }

        public void StartStoryTracking(string storyId, string playerId)
        {
            if (!_isInitialized) return;

            if (!_analyticsData.ContainsKey(storyId))
                _analyticsData[storyId] = new StoryAnalyticsData();

            _analyticsData[storyId].StartSession(playerId);
        }

        public void RecordChoice(string storyId, string playerId, string nodeId, string choiceId, ChoiceResult result)
        {
            if (!_isInitialized || !_analyticsData.ContainsKey(storyId)) return;

            _analyticsData[storyId].RecordChoice(playerId, nodeId, choiceId, result);
        }

        public void RecordBranchSelection(string storyId, string playerId, string nodeId, string branchId, BranchSelectionResult result)
        {
            if (!_isInitialized || !_analyticsData.ContainsKey(storyId)) return;

            _analyticsData[storyId].RecordBranchSelection(playerId, nodeId, branchId, result);
        }

        public void RecordEventTrigger(string storyId, string playerId, string eventId, EventExecutionResult result)
        {
            if (!_isInitialized || !_analyticsData.ContainsKey(storyId)) return;

            _analyticsData[storyId].RecordEventTrigger(playerId, eventId, result);
        }

        public void RecordStoryCompletion(string storyId, string playerId, string endingId, TimeSpan playTime)
        {
            if (!_isInitialized || !_analyticsData.ContainsKey(storyId)) return;

            _analyticsData[storyId].RecordCompletion(playerId, endingId, playTime);
        }

        public void RecordGameLoad(string storyId, string playerId, string saveGameId, DateTime loadTime)
        {
            if (!_isInitialized || !_analyticsData.ContainsKey(storyId)) return;

            _analyticsData[storyId].RecordGameLoad(playerId, saveGameId, loadTime);
        }

        public Task<StoryAnalytics> GetStoryAnalyticsAsync(string storyId, AnalyticsOptions options, CancellationToken cancellationToken)
        {
            if (!_isInitialized || !_analyticsData.ContainsKey(storyId))
                return Task.FromResult(new StoryAnalytics { StoryId = storyId });

            var analytics = _analyticsData[storyId].GenerateAnalytics(options);
            analytics.StoryId = storyId;

            return Task.FromResult(analytics);
        }

        public EngineStatus GetStatus()
        {
            return new EngineStatus;
            {
                IsInitialized = _isInitialized,
                TrackedStories = _analyticsData?.Count ?? 0,
                MemoryUsage = CalculateMemoryUsage()
            };
        }

        public void Dispose()
        {
            _analyticsData?.Clear();
        }

        private long CalculateMemoryUsage()
        {
            return _analyticsData?.Sum(kvp => kvp.Value.EstimatedMemoryUsage) ?? 0;
        }
    }

    internal class StoryGenerator : IDisposable
    {
        private Dictionary<string, StoryTemplate> _templates;
        private bool _isInitialized;

        public Task InitializeAsync(string templatesPath, CancellationToken cancellationToken)
        {
            _templates = LoadTemplates(templatesPath);
            _isInitialized = true;
            return Task.CompletedTask;
        }

        public Task<GeneratedContent> GenerateContentAsync(
            ContentType contentType,
            GenerationContext context,
            CancellationToken cancellationToken)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StoryGenerator not initialized");

            // Content generation logic;
            var content = new GeneratedContent;
            {
                Type = contentType,
                Content = GenerateBasedOnType(contentType, context),
                Context = context,
                GeneratedAt = DateTime.UtcNow;
            };

            return Task.FromResult(content);
        }

        public EngineStatus GetStatus()
        {
            return new EngineStatus;
            {
                IsInitialized = _isInitialized,
                TemplateCount = _templates?.Count ?? 0,
                MemoryUsage = CalculateMemoryUsage()
            };
        }

        public void Dispose()
        {
            _templates?.Clear();
        }

        private Dictionary<string, StoryTemplate> LoadTemplates(string templatesPath)
        {
            var templates = new Dictionary<string, StoryTemplate>();

            if (!Directory.Exists(templatesPath))
                return templates;

            var templateFiles = Directory.GetFiles(templatesPath, "*.template");

            foreach (var file in templateFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var template = JsonSerializer.Deserialize<StoryTemplate>(json);
                    if (template != null)
                    {
                        templates[template.Id] = template;
                    }
                }
                catch (Exception)
                {
                    // Ignore invalid templates;
                }
            }

            return templates;
        }

        private string GenerateBasedOnType(ContentType type, GenerationContext context)
        {
            switch (type)
            {
                case ContentType.Dialog:
                    return GenerateDialog(context);
                case ContentType.Choice:
                    return GenerateChoice(context);
                case ContentType.Event:
                    return GenerateEvent(context);
                case ContentType.Description:
                    return GenerateDescription(context);
                default:
                    return string.Empty;
            }
        }

        private long CalculateMemoryUsage()
        {
            return _templates?.Sum(kvp => kvp.Value.EstimatedMemoryUsage) ?? 0;
        }
    }

    internal class StoryValidator;
    {
        public void ValidateStory(Story story)
        {
            if (story == null)
                throw new ArgumentNullException(nameof(story));

            if (string.IsNullOrEmpty(story.Id))
                throw new StoryValidationException("Story ID cannot be empty");

            if (string.IsNullOrEmpty(story.Title))
                throw new StoryValidationException("Story title cannot be empty");

            if (story.Nodes == null || !story.Nodes.Any())
                throw new StoryValidationException("Story must have at least one node");

            // Start node kontrolü;
            var startNodes = story.Nodes.Where(n => n.Type == StoryNodeType.Start).ToList();
            if (startNodes.Count != 1)
                throw new StoryValidationException("Story must have exactly one start node");

            // End node kontrolü;
            var endNodes = story.Nodes.Where(n => n.Type == StoryNodeType.Ending).ToList();
            if (!endNodes.Any())
                throw new StoryValidationException("Story must have at least one ending node");

            // Node ID uniqueness;
            var nodeIds = story.Nodes.Select(n => n.Id).ToList();
            if (nodeIds.Distinct().Count() != nodeIds.Count)
                throw new StoryValidationException("Story node IDs must be unique");

            // Connection validation;
            foreach (var node in story.Nodes)
            {
                ValidateNode(node, story);
            }
        }

        private void ValidateNode(StoryNode node, Story story)
        {
            if (node.Connections != null)
            {
                foreach (var connection in node.Connections)
                {
                    if (!story.Nodes.Any(n => n.Id == connection.TargetNodeId))
                    {
                        throw new StoryValidationException(
                            $"Node {node.Id} has connection to non-existent node {connection.TargetNodeId}");
                    }
                }
            }

            if (node.Choices != null)
            {
                foreach (var choice in node.Choices)
                {
                    ValidateChoice(choice, story);
                }
            }
        }

        private void ValidateChoice(StoryChoice choice, Story story)
        {
            if (!string.IsNullOrEmpty(choice.NextNodeId))
            {
                if (!story.Nodes.Any(n => n.Id == choice.NextNodeId))
                {
                    throw new StoryValidationException(
                        $"Choice {choice.Id} references non-existent node {choice.NextNodeId}");
                }
            }

            if (!string.IsNullOrEmpty(choice.FailureNodeId))
            {
                if (!story.Nodes.Any(n => n.Id == choice.FailureNodeId))
                {
                    throw new StoryValidationException(
                        $"Choice {choice.Id} references non-existent failure node {choice.FailureNodeId}");
                }
            }
        }
    }

    #endregion;
}
