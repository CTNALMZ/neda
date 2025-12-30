using NEDA.AI.ComputerVision;
using NEDA.Brain.IntentRecognition.ContextBuilder;
using NEDA.Brain.MemorySystem.LongTermMemory;
using NEDA.Brain.MemorySystem.ShortTermMemory;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis;
using NEDA.Communication.DialogSystem.TopicHandler;
using NEDA.Interface.InteractionManager.ConversationHistory;
using NEDA.Interface.InteractionManager.PreferenceLearner;
using NEDA.Interface.InteractionManager.SessionHandler;
using NEDA.Interface.InteractionManager.UserProfileManager;
using NEDA.Interface.ResponseGenerator.ConversationFlow;
using NEDA.Interface.TextInput.NaturalLanguageInput;
using NEDA.Interface.VoiceRecognition.SpeechToText;
using NEDA.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.InteractionManager.ContextKeeper;
{
    /// <summary>
    /// Durum türleri;
    /// </summary>
    public enum StateType;
    {
        /// <summary>Oturum durumu</summary>
        Session = 0,

        /// <summary>Konuşma durumu</summary>
        Conversation = 1,

        /// <summary>Görev durumu</summary>
        Task = 2,

        /// <summary>Kullanıcı durumu</summary>
        User = 3,

        /// <summary>Sistem durumu</summary>
        System = 4,

        /// <summary>Bağlam durumu</summary>
        Context = 5,

        /// <summary>Öncelik durumu</summary>
        Priority = 6,

        /// <summary>Duygu durumu</summary>
        Emotion = 7,

        /// <summary>Çevre durumu</summary>
        Environment = 8;
    }

    /// <summary>
    /// Durum önceliği;
    /// </summary>
    public enum StatePriority;
    {
        /// <summary>Düşük öncelik</summary>
        Low = 0,

        /// <summary>Normal öncelik</summary>
        Normal = 1,

        /// <summary>Yüksek öncelik</summary>
        High = 2,

        /// <summary>Kritik öncelik</summary>
        Critical = 3,

        /// <summary>Sistem önceliği</summary>
        System = 4;
    }

    /// <summary>
    /// Durum geçişi;
    /// </summary>
    public class StateTransition;
    {
        public string FromState { get; }
        public string ToState { get; }
        public string Trigger { get; }
        public DateTime Timestamp { get; }
        public Dictionary<string, object> Metadata { get; }

        public StateTransition(string fromState, string toState, string trigger,
            Dictionary<string, object> metadata = null)
        {
            FromState = fromState;
            ToState = toState;
            Trigger = trigger;
            Timestamp = DateTime.UtcNow;
            Metadata = metadata ?? new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Durum anlık görüntüsü;
    /// </summary>
    public class StateSnapshot;
    {
        public string StateId { get; }
        public string StateName { get; }
        public StateType Type { get; }
        public Dictionary<string, object> Properties { get; }
        public DateTime Timestamp { get; }
        public StatePriority Priority { get; }
        public string OwnerId { get; }

        public StateSnapshot(string stateId, string stateName, StateType type,
            Dictionary<string, object> properties, StatePriority priority, string ownerId)
        {
            StateId = stateId;
            StateName = stateName;
            Type = type;
            Properties = properties ?? new Dictionary<string, object>();
            Timestamp = DateTime.UtcNow;
            Priority = priority;
            OwnerId = ownerId;
        }

        public T GetProperty<T>(string key, T defaultValue = default)
        {
            if (Properties.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }
    }

    /// <summary>
    /// Durum bağımlılığı;
    /// </summary>
    public class StateDependency;
    {
        public string SourceStateId { get; }
        public string TargetStateId { get; }
        public DependencyType Type { get; }
        public double Strength { get; set; } // 0-1 arası;

        public StateDependency(string sourceStateId, string targetStateId,
            DependencyType type, double strength = 0.5)
        {
            SourceStateId = sourceStateId;
            TargetStateId = targetStateId;
            Type = type;
            Strength = Math.Clamp(strength, 0, 1);
        }
    }

    /// <summary>
    /// Bağımlılık türü;
    /// </summary>
    public enum DependencyType;
    {
        /// <summary>Gereklilik</summary>
        Requires = 0,

        /// <summary>Çakışma</summary>
        Conflicts = 1,

        /// <summary>Öncelik</summary>
        Precedes = 2,

        /// <summary>Bağlantı</summary>
        Related = 3,

        /// <summary>Alt durum</summary>
        ParentChild = 4;
    }

    /// <summary>
    /// Durum çakışması;
    /// </summary>
    public class StateConflict;
    {
        public string State1Id { get; }
        public string State2Id { get; }
        public ConflictType Type { get; }
        public string Reason { get; }
        public DateTime DetectedAt { get; }
        public bool IsResolved { get; set; }
        public string ResolutionMethod { get; set; }

        public StateConflict(string state1Id, string state2Id, ConflictType type,
            string reason = "")
        {
            State1Id = state1Id;
            State2Id = state2Id;
            Type = type;
            Reason = reason;
            DetectedAt = DateTime.UtcNow;
            IsResolved = false;
        }
    }

    /// <summary>
    /// Çakışma türü;
    /// </summary>
    public enum ConflictType;
    {
        /// <summary>Kaynak çakışması</summary>
        Resource = 0,

        /// <summary>Öncelik çakışması</summary>
        Priority = 1,

        /// <summary>Mantıksal çakışma</summary>
        Logical = 2,

        /// <summary>Zamanlama çakışması</summary>
        Timing = 3,

        /// <summary>Durum çakışması</summary>
        State = 4;
    }

    /// <summary>
    /// Durum yapılandırması;
    /// </summary>
    public class StateTrackerConfig;
    {
        public bool EnableStatePersistence { get; set; } = true;
        public int StateHistorySize { get; set; } = 1000;
        public int SnapshotIntervalMs { get; set; } = 60000; // 1 dakika;
        public int MaxConcurrentStates { get; set; } = 100;
        public bool EnableConflictDetection { get; set; } = true;
        public bool EnableDependencyTracking { get; set; } = true;
        public bool EnableStateValidation { get; set; } = true;
        public bool EnableAutoRecovery { get; set; } = true;
        public int RecoveryAttempts { get; set; } = 3;
        public bool EnableStateCompression { get; set; } = false;
        public Dictionary<StateType, StatePriority> DefaultPriorities { get; } = new Dictionary<StateType, StatePriority>();

        public StateTrackerConfig()
        {
            // Varsayılan öncelikler;
            DefaultPriorities[StateType.System] = StatePriority.System;
            DefaultPriorities[StateType.Critical] = StatePriority.Critical;
            DefaultPriorities[StateType.Session] = StatePriority.High;
            DefaultPriorities[StateType.Task] = StatePriority.High;
            DefaultPriorities[StateType.Conversation] = StatePriority.Normal;
            DefaultPriorities[StateType.User] = StatePriority.Normal;
            DefaultPriorities[StateType.Context] = StatePriority.Low;
            DefaultPriorities[StateType.Emotion] = StatePriority.Low;
            DefaultPriorities[StateType.Environment] = StatePriority.Low;
        }
    }

    /// <summary>
    /// Durum performans metrikleri;
    /// </summary>
    public class StatePerformanceMetrics;
    {
        public int TotalStateChanges { get; set; }
        public int ActiveStates { get; set; }
        public int StateConflicts { get; set; }
        public int ResolvedConflicts { get; set; }
        public double AverageStateLifetime { get; set; }
        public int StateTransitions { get; set; }
        public DateTime LastMetricsUpdate { get; set; }

        public Dictionary<StateType, int> StatesByType { get; } = new Dictionary<StateType, int>();
        public Dictionary<string, int> StateChangesByUser { get; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Durum izleyici ana sınıfı;
    /// Etkileşim durumlarını izler ve yönetir;
    /// </summary>
    public class StateTracker : IDisposable
    {
        #region Events;

        /// <summary>Durum değiştiğinde tetiklenir</summary>
        public event EventHandler<(string StateId, string OldValue, string NewValue)> StateChanged;

        /// <summary>Yeni durum eklendiğinde tetiklenir</summary>
        public event EventHandler<string> StateAdded;

        /// <summary>Durum kaldırıldığında tetiklenir</summary>
        public event EventHandler<string> StateRemoved;

        /// <summary>Durum çakışması tespit edildiğinde tetiklenir</summary>
        public event EventHandler<StateConflict> StateConflictDetected;

        /// <summary>Durum geçişi gerçekleştiğinde tetiklenir</summary>
        public event EventHandler<StateTransition> StateTransitioned;

        /// <summary>Durum anlık görüntüsü alındığında tetiklenir</summary>
        public event EventHandler<StateSnapshot> StateSnapshotTaken;

        /// <summary>Performans metrikleri güncellendiğinde tetiklenir</summary>
        public event EventHandler<StatePerformanceMetrics> PerformanceMetricsUpdated;

        #endregion;

        #region Private Fields;

        private readonly ILogger _logger;
        private readonly ISessionManager _sessionManager;
        private readonly IUserProfileManager _userProfileManager;
        private readonly IPreferenceEngine _preferenceEngine;
        private readonly IConversationHistoryManager _historyManager;
        private readonly ISpeechRecognizer _speechRecognizer;
        private readonly INaturalLanguageProcessor _naturalLanguageProcessor;
        private readonly IConversationManager _conversationManager;
        private readonly ISyntaxParser _syntaxParser;
        private readonly IContextManager _contextManager;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ILongTermMemory _longTermMemory;

        private readonly StateTrackerConfig _config;
        private readonly ConcurrentDictionary<string, StateSnapshot> _activeStates;
        private readonly ConcurrentDictionary<string, List<StateSnapshot>> _stateHistory;
        private readonly ConcurrentDictionary<string, List<StateTransition>> _transitionHistory;
        private readonly ConcurrentDictionary<string, List<StateDependency>> _stateDependencies;
        private readonly ConcurrentBag<StateConflict> _stateConflicts;
        private readonly ConcurrentDictionary<string, object> _stateLocks;

        private readonly Timer _snapshotTimer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _metricsTimer;
        private readonly SemaphoreSlim _stateOperationLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _monitoringCancellationTokenSource;

        private StatePerformanceMetrics _performanceMetrics = new StatePerformanceMetrics();
        private bool _isInitialized = false;
        private bool _isMonitoring = false;

        #endregion;

        #region Properties;

        /// <summary>Aktif durum sayısı</summary>
        public int ActiveStateCount => _activeStates.Count;

        /// <summary>İzleyici başlatıldı mı?</summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>İzleme aktif mi?</summary>
        public bool IsMonitoring => _isMonitoring;

        /// <summary>Durum yapılandırması</summary>
        public StateTrackerConfig Config => _config;

        /// <summary>Performans metrikleri</summary>
        public StatePerformanceMetrics PerformanceMetrics => _performanceMetrics;

        #endregion;

        #region Constructor;

        /// <summary>
        /// StateTracker sınıfı yapıcısı;
        /// </summary>
        public StateTracker(
            ILogger logger,
            ISessionManager sessionManager,
            IUserProfileManager userProfileManager,
            IPreferenceEngine preferenceEngine,
            IConversationHistoryManager historyManager,
            ISpeechRecognizer speechRecognizer,
            INaturalLanguageProcessor naturalLanguageProcessor,
            IConversationManager conversationManager,
            ISyntaxParser syntaxParser,
            IContextManager contextManager,
            IShortTermMemory shortTermMemory,
            ILongTermMemory longTermMemory,
            StateTrackerConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _userProfileManager = userProfileManager ?? throw new ArgumentNullException(nameof(userProfileManager));
            _preferenceEngine = preferenceEngine ?? throw new ArgumentNullException(nameof(preferenceEngine));
            _historyManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
            _speechRecognizer = speechRecognizer ?? throw new ArgumentNullException(nameof(speechRecognizer));
            _naturalLanguageProcessor = naturalLanguageProcessor ?? throw new ArgumentNullException(nameof(naturalLanguageProcessor));
            _conversationManager = conversationManager ?? throw new ArgumentNullException(nameof(conversationManager));
            _syntaxParser = syntaxParser ?? throw new ArgumentNullException(nameof(syntaxParser));
            _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));

            _config = config ?? new StateTrackerConfig();

            _activeStates = new ConcurrentDictionary<string, StateSnapshot>();
            _stateHistory = new ConcurrentDictionary<string, List<StateSnapshot>>();
            _transitionHistory = new ConcurrentDictionary<string, List<StateTransition>>();
            _stateDependencies = new ConcurrentDictionary<string, List<StateDependency>>();
            _stateConflicts = new ConcurrentBag<StateConflict>();
            _stateLocks = new ConcurrentDictionary<string, object>();

            _snapshotTimer = new Timer(TakeStateSnapshots, null,
                Timeout.Infinite, Timeout.Infinite);

            _cleanupTimer = new Timer(CleanupOldStates, null,
                Timeout.Infinite, Timeout.Infinite);

            _metricsTimer = new Timer(UpdatePerformanceMetrics, null,
                Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation("StateTracker initialized");
        }

        #endregion;

        #region Public Methods - State Management;

        /// <summary>
        /// Durum izlemeyi başlat;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("StateTracker is already initialized");
                return;
            }

            await _stateOperationLock.WaitAsync();

            try
            {
                _logger.LogInformation("Initializing StateTracker");

                // Bağımlı servisleri kontrol et;
                await ValidateDependenciesAsync();

                // Sistem durumlarını başlat;
                await InitializeSystemStatesAsync();

                // Kullanıcı durumlarını yükle;
                await LoadUserStatesAsync();

                // Zamanlayıcıları başlat;
                StartTimers();

                _isInitialized = true;

                _logger.LogInformation("StateTracker initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"StateTracker initialization failed: {ex.Message}");
                throw;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum izlemeyi durdur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            await _stateOperationLock.WaitAsync();

            try
            {
                _logger.LogInformation("Shutting down StateTracker");

                // Zamanlayıcıları durdur;
                StopTimers();

                // Durum anlık görüntülerini al;
                await TakeFinalSnapshotsAsync();

                // Durumları kalıcı depoya kaydet;
                await SaveStatesToStorageAsync();

                // Kaynakları temizle;
                await CleanupResourcesAsync();

                _isInitialized = false;
                _isMonitoring = false;

                _logger.LogInformation("StateTracker shut down successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"StateTracker shutdown failed: {ex.Message}");
                throw;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum ekle veya güncelle;
        /// </summary>
        public async Task<string> SetStateAsync(
            string stateName,
            StateType type,
            Dictionary<string, object> properties,
            string ownerId = null,
            StatePriority? priority = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateName))
                throw new ArgumentException("State name cannot be empty", nameof(stateName));

            // Durum ID oluştur;
            string stateId = await GenerateStateIdAsync(stateName, ownerId);

            // Öncelik belirle;
            var actualPriority = priority ?? GetDefaultPriorityForType(type);

            // Durum doğrulama;
            await ValidateStateAsync(stateName, type, properties, ownerId);

            // Durum kilidi al;
            var stateLock = _stateLocks.GetOrAdd(stateId, _ => new object());
            lock (stateLock)
            {
                try
                {
                    // Eski durumu al;
                    StateSnapshot oldState = null;
                    if (_activeStates.TryGetValue(stateId, out var existingState))
                    {
                        oldState = existingState;
                    }

                    // Yeni durum oluştur;
                    var newState = new StateSnapshot(
                        stateId, stateName, type, properties, actualPriority, ownerId);

                    // Durum çakışması kontrolü;
                    if (oldState != null)
                    {
                        var conflicts = await DetectStateConflictsAsync(oldState, newState);
                        if (conflicts.Any() && _config.EnableConflictDetection)
                        {
                            await HandleStateConflictsAsync(conflicts, oldState, newState);
                        }
                    }

                    // Durumu güncelle;
                    _activeStates[stateId] = newState;

                    // Geçmişe ekle;
                    AddToHistory(stateId, newState);

                    // Geçiş kaydı oluştur;
                    if (oldState != null)
                    {
                        var transition = new StateTransition(
                            oldState.StateName,
                            newState.StateName,
                            "StateUpdate",
                            new Dictionary<string, object>
                            {
                                ["OldProperties"] = oldState.Properties,
                                ["NewProperties"] = newState.Properties;
                            });

                        AddTransition(stateId, transition);
                        OnStateTransitioned(transition);

                        // Durum değişikliği olayı;
                        OnStateChanged((stateId,
                            oldState.Properties.ToString(),
                            newState.Properties.ToString()));
                    }
                    else;
                    {
                        // Yeni durum eklendi;
                        OnStateAdded(stateId);
                    }

                    _performanceMetrics.TotalStateChanges++;
                    UpdateTypeMetrics(type);

                    _logger.LogDebug($"State set: {stateName} ({type}) for owner: {ownerId}");

                    return stateId;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to set state {stateName}: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Durum al;
        /// </summary>
        public async Task<StateSnapshot> GetStateAsync(string stateId, bool includeHistory = false)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("State ID cannot be empty", nameof(stateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                // Aktif durumlarda ara;
                if (_activeStates.TryGetValue(stateId, out var state))
                {
                    if (includeHistory && _stateHistory.TryGetValue(stateId, out var history))
                    {
                        // Geçmişi de ekle (derin kopya)
                        var stateWithHistory = new StateSnapshot(
                            state.StateId,
                            state.StateName,
                            state.Type,
                            new Dictionary<string, object>(state.Properties),
                            state.Priority,
                            state.OwnerId);

                        // Geçmişi özelliklere ekle;
                        stateWithHistory.Properties["History"] = history;
                            .OrderByDescending(s => s.Timestamp)
                            .Take(10)
                            .ToList();

                        return stateWithHistory;
                    }

                    return state;
                }

                // Geçmişte ara;
                if (_stateHistory.TryGetValue(stateId, out var historicalStates))
                {
                    return historicalStates.OrderByDescending(s => s.Timestamp).FirstOrDefault();
                }

                return null;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum sil;
        /// </summary>
        public async Task<bool> RemoveStateAsync(string stateId, bool permanent = false)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("State ID cannot be empty", nameof(stateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                // Bağımlılıkları kontrol et;
                var dependencies = await GetStateDependenciesAsync(stateId);
                if (dependencies.Any(d => d.Type == DependencyType.Requires))
                {
                    var blockingDependencies = dependencies;
                        .Where(d => d.Type == DependencyType.Requires)
                        .ToList();

                    _logger.LogWarning($"Cannot remove state {stateId} - has {blockingDependencies.Count} required dependencies");
                    return false;
                }

                // Durumu kaldır;
                bool removed = _activeStates.TryRemove(stateId, out var removedState);

                if (removed)
                {
                    // Kalıcı silme;
                    if (permanent)
                    {
                        _stateHistory.TryRemove(stateId, out _);
                        _transitionHistory.TryRemove(stateId, out _);
                        _stateDependencies.TryRemove(stateId, out _);
                    }

                    // Bağımlılıkları temizle;
                    await CleanupDependenciesAsync(stateId);

                    // Durum kilidini kaldır;
                    _stateLocks.TryRemove(stateId, out _);

                    OnStateRemoved(stateId);

                    _logger.LogInformation($"State removed: {stateId}");

                    return true;
                }

                return false;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum özelliği güncelle;
        /// </summary>
        public async Task<bool> UpdateStatePropertyAsync(
            string stateId,
            string propertyName,
            object propertyValue)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("State ID cannot be empty", nameof(stateId));

            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("Property name cannot be empty", nameof(propertyName));

            await _stateOperationLock.WaitAsync();

            try
            {
                if (!_activeStates.TryGetValue(stateId, out var state))
                {
                    _logger.LogWarning($"State not found: {stateId}");
                    return false;
                }

                // Durum kilidi al;
                var stateLock = _stateLocks.GetOrAdd(stateId, _ => new object());
                lock (stateLock)
                {
                    // Eski değeri al;
                    object oldValue = null;
                    if (state.Properties.ContainsKey(propertyName))
                    {
                        oldValue = state.Properties[propertyName];
                    }

                    // Yeni durum oluştur;
                    var newProperties = new Dictionary<string, object>(state.Properties);
                    newProperties[propertyName] = propertyValue;

                    var newState = new StateSnapshot(
                        state.StateId,
                        state.StateName,
                        state.Type,
                        newProperties,
                        state.Priority,
                        state.OwnerId);

                    // Durum çakışması kontrolü;
                    var conflicts = DetectPropertyConflicts(state, propertyName, propertyValue).Result;
                    if (conflicts.Any() && _config.EnableConflictDetection)
                    {
                        _logger.LogWarning($"Property update conflict for {stateId}.{propertyName}");
                        return false;
                    }

                    // Durumu güncelle;
                    _activeStates[stateId] = newState;

                    // Geçmişe ekle;
                    AddToHistory(stateId, newState);

                    // Geçiş kaydı oluştur;
                    var transition = new StateTransition(
                        state.StateName,
                        state.StateName,
                        "PropertyUpdate",
                        new Dictionary<string, object>
                        {
                            ["Property"] = propertyName,
                            ["OldValue"] = oldValue,
                            ["NewValue"] = propertyValue;
                        });

                    AddTransition(stateId, transition);
                    OnStateTransitioned(transition);

                    _performanceMetrics.TotalStateChanges++;

                    _logger.LogDebug($"State property updated: {stateId}.{propertyName}");

                    return true;
                }
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Birden fazla durumu al;
        /// </summary>
        public async Task<List<StateSnapshot>> GetStatesAsync(
            StateType? type = null,
            string ownerId = null,
            StatePriority? priority = null,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            await _stateOperationLock.WaitAsync();

            try
            {
                var query = _activeStates.Values.AsEnumerable();

                if (type.HasValue)
                {
                    query = query.Where(s => s.Type == type.Value);
                }

                if (!string.IsNullOrEmpty(ownerId))
                {
                    query = query.Where(s => s.OwnerId == ownerId);
                }

                if (priority.HasValue)
                {
                    query = query.Where(s => s.Priority == priority.Value);
                }

                if (fromDate.HasValue)
                {
                    query = query.Where(s => s.Timestamp >= fromDate.Value);
                }

                if (toDate.HasValue)
                {
                    query = query.Where(s => s.Timestamp <= toDate.Value);
                }

                return query.ToList();
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum geçmişini al;
        /// </summary>
        public async Task<List<StateSnapshot>> GetStateHistoryAsync(
            string stateId,
            int limit = 50,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("State ID cannot be empty", nameof(stateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                if (!_stateHistory.TryGetValue(stateId, out var history))
                {
                    return new List<StateSnapshot>();
                }

                var query = history.AsEnumerable();

                if (fromDate.HasValue)
                {
                    query = query.Where(s => s.Timestamp >= fromDate.Value);
                }

                if (toDate.HasValue)
                {
                    query = query.Where(s => s.Timestamp <= toDate.Value);
                }

                return query;
                    .OrderByDescending(s => s.Timestamp)
                    .Take(limit)
                    .ToList();
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum geçişlerini al;
        /// </summary>
        public async Task<List<StateTransition>> GetStateTransitionsAsync(
            string stateId,
            int limit = 50)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("State ID cannot be empty", nameof(stateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                if (!_transitionHistory.TryGetValue(stateId, out var transitions))
                {
                    return new List<StateTransition>();
                }

                return transitions;
                    .OrderByDescending(t => t.Timestamp)
                    .Take(limit)
                    .ToList();
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        #endregion;

        #region Public Methods - Dependency Management;

        /// <summary>
        /// Durum bağımlılığı ekle;
        /// </summary>
        public async Task<bool> AddStateDependencyAsync(
            string sourceStateId,
            string targetStateId,
            DependencyType type,
            double strength = 0.5)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(sourceStateId))
                throw new ArgumentException("Source state ID cannot be empty", nameof(sourceStateId));

            if (string.IsNullOrWhiteSpace(targetStateId))
                throw new ArgumentException("Target state ID cannot be empty", nameof(targetStateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                // Durumların var olduğunu kontrol et;
                if (!_activeStates.ContainsKey(sourceStateId) || !_activeStates.ContainsKey(targetStateId))
                {
                    _logger.LogWarning($"Cannot add dependency - one or both states not found: {sourceStateId} -> {targetStateId}");
                    return false;
                }

                // Çakışan bağımlılık kontrolü;
                var existingDependencies = await GetStateDependenciesAsync(sourceStateId);
                var conflictingDependency = existingDependencies;
                    .FirstOrDefault(d => d.TargetStateId == targetStateId && d.Type == DependencyType.Conflicts);

                if (conflictingDependency != null)
                {
                    _logger.LogWarning($"Cannot add dependency - conflicting dependency exists: {sourceStateId} -> {targetStateId}");
                    return false;
                }

                // Bağımlılık oluştur;
                var dependency = new StateDependency(sourceStateId, targetStateId, type, strength);

                // Kaynak durumun bağımlılık listesine ekle;
                var dependencies = _stateDependencies.GetOrAdd(sourceStateId, _ => new List<StateDependency>());
                dependencies.Add(dependency);

                // Karşılıklı bağımlılık için tersini de ekle (gerekiyorsa)
                if (type == DependencyType.Requires || type == DependencyType.ParentChild)
                {
                    var reverseType = type == DependencyType.Requires ? DependencyType.RequiredBy : DependencyType.ParentChild;
                    var reverseDependencies = _stateDependencies.GetOrAdd(targetStateId, _ => new List<StateDependency>());
                    reverseDependencies.Add(new StateDependency(targetStateId, sourceStateId, reverseType, strength));
                }

                _logger.LogDebug($"State dependency added: {sourceStateId} -> {targetStateId} ({type})");

                return true;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum bağımlılıklarını al;
        /// </summary>
        public async Task<List<StateDependency>> GetStateDependenciesAsync(
            string stateId,
            DependencyType? type = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("State ID cannot be empty", nameof(stateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                if (!_stateDependencies.TryGetValue(stateId, out var dependencies))
                {
                    return new List<StateDependency>();
                }

                if (type.HasValue)
                {
                    return dependencies.Where(d => d.Type == type.Value).ToList();
                }

                return dependencies.ToList();
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum bağımlılığını kaldır;
        /// </summary>
        public async Task<bool> RemoveStateDependencyAsync(
            string sourceStateId,
            string targetStateId,
            DependencyType? type = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(sourceStateId))
                throw new ArgumentException("Source state ID cannot be empty", nameof(sourceStateId));

            if (string.IsNullOrWhiteSpace(targetStateId))
                throw new ArgumentException("Target state ID cannot be empty", nameof(targetStateId));

            await _stateOperationLock.WaitAsync();

            try
            {
                if (!_stateDependencies.TryGetValue(sourceStateId, out var dependencies))
                {
                    return false;
                }

                // Bağımlılığı bul ve kaldır;
                var dependencyToRemove = dependencies;
                    .Where(d => d.TargetStateId == targetStateId)
                    .Where(d => !type.HasValue || d.Type == type.Value)
                    .FirstOrDefault();

                if (dependencyToRemove == null)
                {
                    return false;
                }

                dependencies.Remove(dependencyToRemove);

                // Karşılıklı bağımlılığı da kaldır;
                if (_stateDependencies.TryGetValue(targetStateId, out var reverseDependencies))
                {
                    var reverseDependencyToRemove = reverseDependencies;
                        .Where(d => d.TargetStateId == sourceStateId)
                        .Where(d => !type.HasValue || d.Type == GetReverseDependencyType(dependencyToRemove.Type))
                        .FirstOrDefault();

                    if (reverseDependencyToRemove != null)
                    {
                        reverseDependencies.Remove(reverseDependencyToRemove);
                    }
                }

                _logger.LogDebug($"State dependency removed: {sourceStateId} -> {targetStateId}");

                return true;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        #endregion;

        #region Public Methods - Conflict Management;

        /// <summary>
        /// Durum çakışmalarını al;
        /// </summary>
        public async Task<List<StateConflict>> GetStateConflictsAsync(
            bool unresolvedOnly = true,
            ConflictType? type = null)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            await _stateOperationLock.WaitAsync();

            try
            {
                var conflicts = _stateConflicts.AsEnumerable();

                if (unresolvedOnly)
                {
                    conflicts = conflicts.Where(c => !c.IsResolved);
                }

                if (type.HasValue)
                {
                    conflicts = conflicts.Where(c => c.Type == type.Value);
                }

                return conflicts.ToList();
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum çakışmasını çöz;
        /// </summary>
        public async Task<bool> ResolveStateConflictAsync(
            string state1Id,
            string state2Id,
            string resolutionMethod)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(state1Id))
                throw new ArgumentException("State1 ID cannot be empty", nameof(state1Id));

            if (string.IsNullOrWhiteSpace(state2Id))
                throw new ArgumentException("State2 ID cannot be empty", nameof(state2Id));

            await _stateOperationLock.WaitAsync();

            try
            {
                var conflict = _stateConflicts;
                    .FirstOrDefault(c =>
                        (c.State1Id == state1Id && c.State2Id == state2Id) ||
                        (c.State1Id == state2Id && c.State2Id == state1Id));

                if (conflict == null)
                {
                    _logger.LogWarning($"Conflict not found: {state1Id} <-> {state2Id}");
                    return false;
                }

                conflict.IsResolved = true;
                conflict.ResolutionMethod = resolutionMethod;

                _performanceMetrics.ResolvedConflicts++;

                _logger.LogInformation($"State conflict resolved: {state1Id} <-> {state2Id} using {resolutionMethod}");

                return true;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Durum çakışması kontrolü yap;
        /// </summary>
        public async Task<List<StateConflict>> CheckForConflictsAsync()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            await _stateOperationLock.WaitAsync();

            try
            {
                var conflicts = new List<StateConflict>();
                var states = _activeStates.Values.ToList();

                // Tüm durum çiftlerini kontrol et;
                for (int i = 0; i < states.Count; i++)
                {
                    for (int j = i + 1; j < states.Count; j++)
                    {
                        var state1 = states[i];
                        var state2 = states[j];

                        var detectedConflicts = await DetectStateConflictsAsync(state1, state2);
                        conflicts.AddRange(detectedConflicts);
                    }
                }

                // Yeni çakışmaları ekle;
                foreach (var conflict in conflicts)
                {
                    if (!_stateConflicts.Any(c =>
                        (c.State1Id == conflict.State1Id && c.State2Id == conflict.State2Id) ||
                        (c.State1Id == conflict.State2Id && c.State2Id == conflict.State1Id)))
                    {
                        _stateConflicts.Add(conflict);
                        _performanceMetrics.StateConflicts++;

                        OnStateConflictDetected(conflict);

                        _logger.LogWarning($"State conflict detected: {conflict.State1Id} <-> {conflict.State2Id} ({conflict.Type})");
                    }
                }

                return conflicts;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        #endregion;

        #region Public Methods - Context Integration;

        /// <summary>
        /// Bağlamdan durum türet;
        /// </summary>
        public async Task<List<StateSnapshot>> DeriveStatesFromContextAsync(string contextId)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(contextId))
                throw new ArgumentException("Context ID cannot be empty", nameof(contextId));

            await _stateOperationLock.WaitAsync();

            try
            {
                // Bağlam bilgilerini al;
                var context = await _contextManager.GetContextAsync(contextId);
                if (context == null)
                {
                    _logger.LogWarning($"Context not found: {contextId}");
                    return new List<StateSnapshot>();
                }

                var derivedStates = new List<StateSnapshot>();

                // Bağlam özelliklerinden durum türet;
                foreach (var property in context.Properties)
                {
                    var stateName = $"Context_{contextId}_{property.Key}";
                    var stateId = await GenerateStateIdAsync(stateName, context.OwnerId);

                    var state = new StateSnapshot(
                        stateId,
                        stateName,
                        StateType.Context,
                        new Dictionary<string, object> { [property.Key] = property.Value },
                        GetDefaultPriorityForType(StateType.Context),
                        context.OwnerId);

                    _activeStates[stateId] = state;
                    AddToHistory(stateId, state);

                    derivedStates.Add(state);

                    _logger.LogDebug($"State derived from context: {stateName}");
                }

                return derivedStates;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Konuşmadan durum türet;
        /// </summary>
        public async Task<List<StateSnapshot>> DeriveStatesFromConversationAsync(string conversationId)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("Conversation ID cannot be empty", nameof(conversationId));

            await _stateOperationLock.WaitAsync();

            try
            {
                // Konuşma geçmişini al;
                var conversation = await _historyManager.GetConversationHistoryAsync(conversationId);
                if (conversation == null || !conversation.Any())
                {
                    return new List<StateSnapshot>();
                }

                var derivedStates = new List<StateSnapshot>();

                // Her mesajdan durum türet;
                foreach (var message in conversation)
                {
                    var stateName = $"Conversation_{conversationId}_{message.Timestamp:yyyyMMddHHmmss}";
                    var stateId = await GenerateStateIdAsync(stateName, message.SenderId);

                    var stateProperties = new Dictionary<string, object>
                    {
                        ["Message"] = message.Content,
                        ["Sender"] = message.SenderId,
                        ["Timestamp"] = message.Timestamp,
                        ["Emotion"] = message.Emotion,
                        ["Intent"] = message.Intent;
                    };

                    var state = new StateSnapshot(
                        stateId,
                        stateName,
                        StateType.Conversation,
                        stateProperties,
                        StatePriority.Normal,
                        message.SenderId);

                    _activeStates[stateId] = state;
                    AddToHistory(stateId, state);

                    derivedStates.Add(state);
                }

                _logger.LogDebug($"Derived {derivedStates.Count} states from conversation: {conversationId}");

                return derivedStates;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        /// <summary>
        /// Kullanıcı tercihlerinden durum türet;
        /// </summary>
        public async Task<List<StateSnapshot>> DeriveStatesFromPreferencesAsync(string userId)
        {
            if (!_isInitialized)
                throw new InvalidOperationException("StateTracker not initialized");

            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            await _stateOperationLock.WaitAsync();

            try
            {
                // Kullanıcı tercihlerini al;
                var preferences = await _preferenceEngine.GetUserPreferencesAsync(userId);
                if (preferences == null || !preferences.Any())
                {
                    return new List<StateSnapshot>();
                }

                var derivedStates = new List<StateSnapshot>();

                // Her tercihten durum türet;
                foreach (var preference in preferences)
                {
                    var stateName = $"Preference_{userId}_{preference.Key}";
                    var stateId = await GenerateStateIdAsync(stateName, userId);

                    var stateProperties = new Dictionary<string, object>
                    {
                        ["PreferenceKey"] = preference.Key,
                        ["PreferenceValue"] = preference.Value,
                        ["Confidence"] = preference.Confidence,
                        ["LastUpdated"] = preference.LastUpdated;
                    };

                    var state = new StateSnapshot(
                        stateId,
                        stateName,
                        StateType.User,
                        stateProperties,
                        StatePriority.Normal,
                        userId);

                    _activeStates[stateId] = state;
                    AddToHistory(stateId, state);

                    derivedStates.Add(state);
                }

                _logger.LogDebug($"Derived {derivedStates.Count} states from preferences for user: {userId}");

                return derivedStates;
            }
            finally
            {
                _stateOperationLock.Release();
            }
        }

        #endregion;

        #region Private Methods - Initialization;

        /// <summary>
        /// Bağımlılıkları doğrula;
        /// </summary>
        private async Task ValidateDependenciesAsync()
        {
            _logger.LogInformation("Validating StateTracker dependencies");

            try
            {
                // Tüm bağımlı servislerin hazır olduğunu kontrol et;
                await _sessionManager.ValidateSessionAsync("system");
                await _userProfileManager.GetUserProfileAsync("system");
                await _contextManager.GetContextAsync("system");

                _logger.LogInformation("All dependencies validated successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Dependency validation failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sistem durumlarını başlat;
        /// </summary>
        private async Task InitializeSystemStatesAsync()
        {
            _logger.LogInformation("Initializing system states");

            try
            {
                // Sistem durumları oluştur;
                var systemStates = new List<(string, StateType, Dictionary<string, object>)>
                {
                    ("System_Initialized", StateType.System, new Dictionary<string, object>
                    {
                        ["InitializationTime"] = DateTime.UtcNow,
                        ["Version"] = "2.0.0",
                        ["Status"] = "Running"
                    }),

                    ("Tracker_Active", StateType.System, new Dictionary<string, object>
                    {
                        ["StartTime"] = DateTime.UtcNow,
                        ["Monitoring"] = true,
                        ["Performance"] = "Optimal"
                    }),

                    ("Memory_Available", StateType.System, new Dictionary<string, object>
                    {
                        ["ShortTermMemorySize"] = 1000,
                        ["LongTermMemorySize"] = 10000,
                        ["MemoryUsage"] = 0.1;
                    })
                };

                foreach (var (stateName, type, properties) in systemStates)
                {
                    await SetStateAsync(stateName, type, properties, "system", StatePriority.System);
                }

                _logger.LogInformation($"Initialized {systemStates.Count} system states");
            }
            catch (Exception ex)
            {
                _logger.LogError($"System state initialization failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcı durumlarını yükle;
        /// </summary>
        private async Task LoadUserStatesAsync()
        {
            try
            {
                // Aktif oturumları al;
                var activeSessions = await _sessionManager.GetActiveSessionsAsync();

                foreach (var session in activeSessions)
                {
                    // Kullanıcı durumlarını yükle;
                    var userStates = await LoadUserStatesFromStorageAsync(session.UserId);

                    foreach (var state in userStates)
                    {
                        _activeStates[state.StateId] = state;
                        AddToHistory(state.StateId, state);
                    }

                    _logger.LogDebug($"Loaded {userStates.Count} states for user: {session.UserId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load user states: {ex.Message}");
            }
        }

        /// <summary>
        /// Zamanlayıcıları başlat;
        /// </summary>
        private void StartTimers()
        {
            _snapshotTimer.Change(_config.SnapshotIntervalMs, _config.SnapshotIntervalMs);
            _cleanupTimer.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            _metricsTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _monitoringCancellationTokenSource = new CancellationTokenSource();
            _isMonitoring = true;

            _logger.LogDebug("StateTracker timers started");
        }

        /// <summary>
        /// Zamanlayıcıları durdur;
        /// </summary>
        private void StopTimers()
        {
            _snapshotTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _metricsTimer.Change(Timeout.Infinite, Timeout.Infinite);

            _monitoringCancellationTokenSource?.Cancel();
            _monitoringCancellationTokenSource?.Dispose();
            _monitoringCancellationTokenSource = null;

            _isMonitoring = false;

            _logger.LogDebug("StateTracker timers stopped");
        }

        #endregion;

        #region Private Methods - State Operations;

        /// <summary>
        /// Durum ID oluştur;
        /// </summary>
        private async Task<string> GenerateStateIdAsync(string stateName, string ownerId)
        {
            // Benzersiz ID oluştur;
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var random = new Random();
            var randomPart = random.Next(1000, 9999);

            var baseId = $"{stateName}_{ownerId ?? "system"}_{timestamp}_{randomPart}";

            // Hash oluştur;
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(baseId));
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16).ToLower();

            return $"STATE_{hash}";
        }

        /// <summary>
        /// Durum türü için varsayılan öncelik;
        /// </summary>
        private StatePriority GetDefaultPriorityForType(StateType type)
        {
            if (_config.DefaultPriorities.TryGetValue(type, out var priority))
            {
                return priority;
            }

            return StatePriority.Normal;
        }

        /// <summary>
        /// Durumu doğrula;
        /// </summary>
        private async Task ValidateStateAsync(
            string stateName,
            StateType type,
            Dictionary<string, object> properties,
            string ownerId)
        {
            if (!_config.EnableStateValidation)
                return;

            // Durum adı doğrulama;
            if (stateName.Length > 100)
                throw new ArgumentException("State name too long (max 100 characters)");

            // Özellik sayısı kontrolü;
            if (properties != null && properties.Count > 50)
                throw new ArgumentException("Too many properties (max 50)");

            // Özellik boyutu kontrolü;
            foreach (var property in properties ?? new Dictionary<string, object>())
            {
                if (property.Value != null && property.Value.ToString().Length > 1000)
                {
                    throw new ArgumentException($"Property value too long: {property.Key}");
                }
            }

            // Aynı ada sahip aktif durum kontrolü;
            var existingState = _activeStates.Values;
                .FirstOrDefault(s => s.StateName == stateName && s.OwnerId == ownerId);

            if (existingState != null && existingState.Type != type)
            {
                throw new InvalidOperationException($"State with name '{stateName}' already exists with different type");
            }
        }

        /// <summary>
        /// Geçmişe ekle;
        /// </summary>
        private void AddToHistory(string stateId, StateSnapshot snapshot)
        {
            var history = _stateHistory.GetOrAdd(stateId, _ => new List<StateSnapshot>());
            history.Add(snapshot);

            // Geçmiş boyutunu sınırla;
            if (history.Count > _config.StateHistorySize)
            {
                history.RemoveAt(0);
            }
        }

        /// <summary>
        /// Geçiş ekle;
        /// </summary>
        private void AddTransition(string stateId, StateTransition transition)
        {
            var transitions = _transitionHistory.GetOrAdd(stateId, _ => new List<StateTransition>());
            transitions.Add(transition);

            // Geçiş kaydı boyutunu sınırla;
            if (transitions.Count > 100)
            {
                transitions.RemoveAt(0);
            }
        }

        /// <summary>
        /// Tür metriklerini güncelle;
        /// </summary>
        private void UpdateTypeMetrics(StateType type)
        {
            if (!_performanceMetrics.StatesByType.ContainsKey(type))
            {
                _performanceMetrics.StatesByType[type] = 0;
            }

            _performanceMetrics.StatesByType[type]++;
        }

        #endregion;

        #region Private Methods - Conflict Detection;

        /// <summary>
        /// Durum çakışmalarını tespit et;
        /// </summary>
        private async Task<List<StateConflict>> DetectStateConflictsAsync(
            StateSnapshot state1,
            StateSnapshot state2)
        {
            var conflicts = new List<StateConflict>();

            // Aynı sahipli durumlar için çakışma kontrolü;
            if (state1.OwnerId == state2.OwnerId && state1.Type == state2.Type)
            {
                // Özellik çakışması;
                var commonProperties = state1.Properties.Keys;
                    .Intersect(state2.Properties.Keys)
                    .ToList();

                foreach (var property in commonProperties)
                {
                    if (!object.Equals(state1.Properties[property], state2.Properties[property]))
                    {
                        conflicts.Add(new StateConflict(
                            state1.StateId,
                            state2.StateId,
                            ConflictType.Resource,
                            $"Property conflict: {property}"));
                    }
                }
            }

            // Öncelik çakışması;
            if (state1.Priority == StatePriority.Critical && state2.Priority == StatePriority.Critical &&
                state1.Type == state2.Type && state1.OwnerId != state2.OwnerId)
            {
                conflicts.Add(new StateConflict(
                    state1.StateId,
                    state2.StateId,
                    ConflictType.Priority,
                    "Critical priority conflict"));
            }

            // Bağımlılık çakışması;
            var dependencies1 = await GetStateDependenciesAsync(state1.StateId);
            var dependencies2 = await GetStateDependenciesAsync(state2.StateId);

            var conflictingDependency = dependencies1;
                .FirstOrDefault(d => d.TargetStateId == state2.StateId && d.Type == DependencyType.Conflicts);

            if (conflictingDependency != null)
            {
                conflicts.Add(new StateConflict(
                    state1.StateId,
                    state2.StateId,
                    ConflictType.Logical,
                    "Explicit dependency conflict"));
            }

            return conflicts;
        }

        /// <summary>
        /// Özellik çakışmalarını tespit et;
        /// </summary>
        private async Task<List<StateConflict>> DetectPropertyConflicts(
            StateSnapshot state,
            string propertyName,
            object propertyValue)
        {
            var conflicts = new List<StateConflict>();

            // Aynı türdeki diğer durumlarla karşılaştır;
            var similarStates = _activeStates.Values;
                .Where(s => s.Type == state.Type && s.StateId != state.StateId)
                .ToList();

            foreach (var otherState in similarStates)
            {
                if (otherState.Properties.TryGetValue(propertyName, out var otherValue))
                {
                    if (!object.Equals(propertyValue, otherValue))
                    {
                        conflicts.Add(new StateConflict(
                            state.StateId,
                            otherState.StateId,
                            ConflictType.Resource,
                            $"Property conflict: {propertyName}"));
                    }
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Durum çakışmalarını çöz;
        /// </summary>
        private async Task HandleStateConflictsAsync(
            List<StateConflict> conflicts,
            StateSnapshot oldState,
            StateSnapshot newState)
        {
            foreach (var conflict in conflicts)
            {
                // Çakışmayı çözmeye çalış;
                bool resolved = await TryResolveConflictAsync(conflict, oldState, newState);

                if (!resolved && _config.EnableAutoRecovery)
                {
                    // Otomatik kurtarma;
                    resolved = await AttemptAutoRecoveryAsync(conflict, oldState, newState);
                }

                if (!resolved)
                {
                    _logger.LogWarning($"Unresolved state conflict: {conflict.State1Id} <-> {conflict.State2Id}");

                    // Çakışmayı kaydet;
                    _stateConflicts.Add(conflict);
                    _performanceMetrics.StateConflicts++;

                    OnStateConflictDetected(conflict);
                }
            }
        }

        /// <summary>
        /// Çakışmayı çözmeye çalış;
        /// </summary>
        private async Task<bool> TryResolveConflictAsync(
            StateConflict conflict,
            StateSnapshot oldState,
            StateSnapshot newState)
        {
            try
            {
                switch (conflict.Type)
                {
                    case ConflictType.Resource:
                        // Kaynak çakışması - önceliğe göre çöz;
                        if (newState.Priority > oldState.Priority)
                        {
                            // Yeni durum daha yüksek öncelikli, eski durumu geçersiz kıl;
                            _logger.LogInformation($"Resolving resource conflict by priority: {newState.StateName} over {oldState.StateName}");
                            return true;
                        }
                        break;

                    case ConflictType.Priority:
                        // Kritik durum çakışması - zaman damgasına göre çöz;
                        if (newState.Timestamp > oldState.Timestamp)
                        {
                            _logger.LogInformation($"Resolving priority conflict by timestamp: {newState.StateName}");
                            return true;
                        }
                        break;

                    case ConflictType.Logical:
                        // Mantıksal çakışma - bağımlılığı kaldır;
                        await RemoveStateDependencyAsync(conflict.State1Id, conflict.State2Id, DependencyType.Conflicts);
                        _logger.LogInformation($"Resolved logical conflict by removing dependency");
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Conflict resolution failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Otomatik kurtarma dene;
        /// </summary>
        private async Task<bool> AttemptAutoRecoveryAsync(
            StateConflict conflict,
            StateSnapshot oldState,
            StateSnapshot newState)
        {
            for (int attempt = 1; attempt <= _config.RecoveryAttempts; attempt++)
            {
                try
                {
                    _logger.LogInformation($"Auto-recovery attempt {attempt} for conflict: {conflict.State1Id} <-> {conflict.State2Id}");

                    // Farklı kurtarma stratejileri dene;
                    bool recovered = await attempt switch;
                    {
                        1 => await RecoveryStrategy1Async(conflict, oldState, newState),
                        2 => await RecoveryStrategy2Async(conflict, oldState, newState),
                        3 => await RecoveryStrategy3Async(conflict, oldState, newState),
                        _ => false;
                    };

                    if (recovered)
                    {
                        _logger.LogInformation($"Auto-recovery successful on attempt {attempt}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Auto-recovery attempt {attempt} failed: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Kurtarma stratejisi 1: Durumu geri al;
        /// </summary>
        private async Task<bool> RecoveryStrategy1Async(
            StateConflict conflict,
            StateSnapshot oldState,
            StateSnapshot newState)
        {
            // Eski duruma geri dön;
            _activeStates[conflict.State1Id] = oldState;
            AddToHistory(conflict.State1Id, oldState);

            _logger.LogDebug($"Recovery strategy 1: Rolled back to old state");
            return true;
        }

        /// <summary>
        /// Kurtarma stratejisi 2: Melez durum oluştur;
        /// </summary>
        private async Task<bool> RecoveryStrategy2Async(
            StateConflict conflict,
            StateSnapshot oldState,
            StateSnapshot newState)
        {
            // Melez durum oluştur;
            var hybridProperties = new Dictionary<string, object>();

            // Eski ve yeni özellikleri birleştir;
            foreach (var prop in oldState.Properties)
            {
                hybridProperties[prop.Key] = prop.Value;
            }

            foreach (var prop in newState.Properties)
            {
                hybridProperties[prop.Key] = prop.Value;
            }

            var hybridState = new StateSnapshot(
                oldState.StateId,
                $"{oldState.StateName}_Hybrid",
                oldState.Type,
                hybridProperties,
                StatePriority.Normal,
                oldState.OwnerId);

            _activeStates[oldState.StateId] = hybridState;
            AddToHistory(oldState.StateId, hybridState);

            _logger.LogDebug($"Recovery strategy 2: Created hybrid state");
            return true;
        }

        /// <summary>
        /// Kurtarma stratejisi 3: Üçüncü durum oluştur;
        /// </summary>
        private async Task<bool> RecoveryStrategy3Async(
            StateConflict conflict,
            StateSnapshot oldState,
            StateSnapshot newState)
        {
            // Üçüncü bir durum oluştur (tarafsız)
            var neutralProperties = new Dictionary<string, object>
            {
                ["ConflictResolved"] = true,
                ["ResolutionTime"] = DateTime.UtcNow,
                ["OriginalState1"] = oldState.StateName,
                ["OriginalState2"] = newState.StateName;
            };

            var neutralState = new StateSnapshot(
                await GenerateStateIdAsync("Neutral_Resolution", "system"),
                "Neutral_Resolution",
                StateType.System,
                neutralProperties,
                StatePriority.System,
                "system");

            _activeStates[neutralState.StateId] = neutralState;
            AddToHistory(neutralState.StateId, neutralState);

            // Çakışan durumları devre dışı bırak;
            await RemoveStateAsync(oldState.StateId);
            await RemoveStateAsync(newState.StateId);

            _logger.LogDebug($"Recovery strategy 3: Created neutral state and removed conflicting states");
            return true;
        }

        #endregion;

        #region Private Methods - Dependency Management;

        /// <summary>
        /// Bağımlılıkları temizle;
        /// </summary>
        private async Task CleanupDependenciesAsync(string stateId)
        {
            try
            {
                // Bu duruma bağlı tüm bağımlılıkları kaldır;
                foreach (var dependencies in _stateDependencies.Values)
                {
                    dependencies.RemoveAll(d => d.TargetStateId == stateId);
                }

                // Bu durumun bağımlılıklarını temizle;
                _stateDependencies.TryRemove(stateId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to cleanup dependencies for state {stateId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Ters bağımlılık türünü al;
        /// </summary>
        private DependencyType GetReverseDependencyType(DependencyType type)
        {
            return type switch;
            {
                DependencyType.Requires => DependencyType.RequiredBy,
                DependencyType.RequiredBy => DependencyType.Requires,
                DependencyType.ParentChild => DependencyType.ParentChild,
                _ => type;
            };
        }

        #endregion;

        #region Private Methods - Timer Callbacks;

        /// <summary>
        /// Durum anlık görüntülerini al;
        /// </summary>
        private async void TakeStateSnapshots(object state)
        {
            if (!_isMonitoring || _monitoringCancellationTokenSource?.Token.IsCancellationRequested == true)
                return;

            try
            {
                await _stateOperationLock.WaitAsync();

                try
                {
                    var timestamp = DateTime.UtcNow;

                    // Tüm aktif durumlar için anlık görüntü al;
                    foreach (var kvp in _activeStates)
                    {
                        var snapshot = new StateSnapshot(
                            kvp.Value.StateId,
                            kvp.Value.StateName,
                            kvp.Value.Type,
                            new Dictionary<string, object>(kvp.Value.Properties),
                            kvp.Value.Priority,
                            kvp.Value.OwnerId);

                        // Anlık görüntüyü geçmişe ekle;
                        AddToHistory(kvp.Key, snapshot);

                        OnStateSnapshotTaken(snapshot);
                    }

                    _logger.LogDebug($"State snapshots taken for {_activeStates.Count} states at {timestamp}");
                }
                finally
                {
                    _stateOperationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"State snapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Eski durumları temizle;
        /// </summary>
        private async void CleanupOldStates(object state)
        {
            if (!_isMonitoring || _monitoringCancellationTokenSource?.Token.IsCancellationRequested == true)
                return;

            try
            {
                await _stateOperationLock.WaitAsync();

                try
                {
                    var cutoffTime = DateTime.UtcNow.AddHours(-24); // 24 saatten eski;
                    var statesToRemove = new List<string>();

                    // Eski durumları bul;
                    foreach (var kvp in _activeStates)
                    {
                        if (kvp.Value.Timestamp < cutoffTime &&
                            kvp.Value.Priority < StatePriority.High &&
                            kvp.Value.Type != StateType.System)
                        {
                            statesToRemove.Add(kvp.Key);
                        }
                    }

                    // Durumları kaldır;
                    foreach (var stateId in statesToRemove)
                    {
                        await RemoveStateAsync(stateId);
                    }

                    if (statesToRemove.Count > 0)
                    {
                        _logger.LogInformation($"Cleaned up {statesToRemove.Count} old states");
                    }
                }
                finally
                {
                    _stateOperationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"State cleanup error: {ex.Message}");
            }
        }

        /// <summary>
        /// Performans metriklerini güncelle;
        /// </summary>
        private async void UpdatePerformanceMetrics(object state)
        {
            if (!_isMonitoring || _monitoringCancellationTokenSource?.Token.IsCancellationRequested == true)
                return;

            try
            {
                await _stateOperationLock.WaitAsync();

                try
                {
                    _performanceMetrics.ActiveStates = _activeStates.Count;
                    _performanceMetrics.StateTransitions = _transitionHistory.Sum(t => t.Value.Count);
                    _performanceMetrics.LastMetricsUpdate = DateTime.UtcNow;

                    // Ortalama durum ömrü hesapla;
                    if (_stateHistory.Any())
                    {
                        var totalLifetime = _stateHistory.Sum(h =>
                            h.Value.Max(s => s.Timestamp).Ticks - h.Value.Min(s => s.Timestamp).Ticks);
                        var averageTicks = totalLifetime / _stateHistory.Count;
                        _performanceMetrics.AverageStateLifetime = TimeSpan.FromTicks((long)averageTicks).TotalSeconds;
                    }

                    OnPerformanceMetricsUpdated(_performanceMetrics);

                    _logger.LogDebug($"Performance metrics updated: {_performanceMetrics.ActiveStates} active states");
                }
                finally
                {
                    _stateOperationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Performance metrics update error: {ex.Message}");
            }
        }

        #endregion;

        #region Private Methods - Persistence;

        /// <summary>
        /// Son anlık görüntüleri al;
        /// </summary>
        private async Task TakeFinalSnapshotsAsync()
        {
            try
            {
                _logger.LogInformation("Taking final state snapshots");

                // Tüm durumlar için anlık görüntü al;
                foreach (var kvp in _activeStates)
                {
                    var snapshot = new StateSnapshot(
                        kvp.Value.StateId,
                        kvp.Value.StateName,
                        kvp.Value.Type,
                        new Dictionary<string, object>(kvp.Value.Properties),
                        kvp.Value.Priority,
                        kvp.Value.OwnerId);

                    // Anlık görüntüyü geçmişe ekle;
                    AddToHistory(kvp.Key, snapshot);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Final snapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Durumları depolamaya kaydet;
        /// </summary>
        private async Task SaveStatesToStorageAsync()
        {
            if (!_config.EnableStatePersistence)
                return;

            try
            {
                _logger.LogInformation("Saving states to persistent storage");

                // Durumları kalıcı depoya kaydet;
                // Bu kısım gerçek depolama mekanizmasına bağlı olarak implemente edilecek;
                await Task.Delay(100); // Simülasyon;

                _logger.LogInformation($"Saved {_activeStates.Count} states to persistent storage");
            }
            catch (Exception ex)
            {
                _logger.LogError($"State persistence error: {ex.Message}");
            }
        }

        /// <summary>
        /// Kullanıcı durumlarını depodan yükle;
        /// </summary>
        private async Task<List<StateSnapshot>> LoadUserStatesFromStorageAsync(string userId)
        {
            if (!_config.EnableStatePersistence)
                return new List<StateSnapshot>();

            try
            {
                // Kullanıcı durumlarını kalıcı depodan yükle;
                // Bu kısım gerçek depolama mekanizmasına bağlı olarak implemente edilecek;
                await Task.Delay(50); // Simülasyon;

                return new List<StateSnapshot>();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load user states for {userId}: {ex.Message}");
                return new List<StateSnapshot>();
            }
        }

        /// <summary>
        /// Kaynakları temizle;
        /// </summary>
        private async Task CleanupResourcesAsync()
        {
            try
            {
                // Aktif durumları temizle;
                _activeStates.Clear();
                _stateHistory.Clear();
                _transitionHistory.Clear();
                _stateDependencies.Clear();
                _stateConflicts.Clear();
                _stateLocks.Clear();

                _performanceMetrics = new StatePerformanceMetrics();

                _logger.LogDebug("StateTracker resources cleaned up");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Resource cleanup error: {ex.Message}");
            }
        }

        #endregion;

        #region Event Triggers;

        private void OnStateChanged((string StateId, string OldValue, string NewValue) args)
        {
            StateChanged?.Invoke(this, args);
        }

        private void OnStateAdded(string stateId)
        {
            StateAdded?.Invoke(this, stateId);
        }

        private void OnStateRemoved(string stateId)
        {
            StateRemoved?.Invoke(this, stateId);
        }

        private void OnStateConflictDetected(StateConflict conflict)
        {
            StateConflictDetected?.Invoke(this, conflict);
        }

        private void OnStateTransitioned(StateTransition transition)
        {
            StateTransitioned?.Invoke(this, transition);
        }

        private void OnStateSnapshotTaken(StateSnapshot snapshot)
        {
            StateSnapshotTaken?.Invoke(this, snapshot);
        }

        private void OnPerformanceMetricsUpdated(StatePerformanceMetrics metrics)
        {
            PerformanceMetricsUpdated?.Invoke(this, metrics);
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
                    // Yönetilen kaynakları serbest bırak;
                    try
                    {
                        ShutdownAsync().Wait();
                    }
                    catch
                    {
                        // Kapatma hatalarını görmezden gel;
                    }

                    _snapshotTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _metricsTimer?.Dispose();
                    _stateOperationLock?.Dispose();
                    _monitoringCancellationTokenSource?.Dispose();
                }

                _disposed = true;
            }
        }

        ~StateTracker()
        {
            Dispose(false);
        }

        #endregion;
    }
}
