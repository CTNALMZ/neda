using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Interface.TextInput.QuickCommands;
using NEDA.Commands.CommandExecutor;
using NEDA.Automation.Executors;

namespace NEDA.Interface.TextInput.MacroSupport;
{
    /// <summary>
    /// Advanced Macro System for recording, editing, and playing back sequences of commands and actions;
    /// Supports scripting, variables, conditions, loops, and integration with other NEDA systems;
    /// </summary>
    public class MacroSystem : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly CommandExecutor _commandExecutor;
        private readonly ScriptRunner _scriptRunner;
        private readonly MacroConfiguration _configuration;
        private readonly MacroStorage _storage;
        private readonly MacroRecorder _recorder;
        private readonly MacroPlayer _player;
        private readonly MacroEditor _editor;
        private readonly MacroCompiler _compiler;
        private readonly Dictionary<string, Macro> _loadedMacros;
        private bool _isInitialized;
        private bool _isRecording;
        private bool _isPlaying;
        private int _currentMacroId;
        private CancellationTokenSource _playbackCancellationToken;

        public event EventHandler<MacroRecordingStartedEventArgs> RecordingStarted;
        public event EventHandler<MacroRecordingStoppedEventArgs> RecordingStopped;
        public event EventHandler<MacroPlaybackStartedEventArgs> PlaybackStarted;
        public event EventHandler<MacroPlaybackCompletedEventArgs> PlaybackCompleted;
        public event EventHandler<MacroPlaybackErrorEventArgs> PlaybackError;
        public event EventHandler<MacroCompiledEventArgs> MacroCompiled;
        public event EventHandler<MacroSavedEventArgs> MacroSaved;
        public event EventHandler<MacroLoadedEventArgs> MacroLoaded;

        public IReadOnlyDictionary<string, Macro> LoadedMacros => _loadedMacros;
        public bool IsRecording => _isRecording;
        public bool IsPlaying => _isPlaying;
        public int TotalMacros => _loadedMacros.Count;
        public Macro CurrentMacro => _recorder?.CurrentMacro;

        #endregion;

        #region Configuration and Data Structures;

        public class MacroConfiguration;
        {
            public string StoragePath { get; set; } = "Macros";
            public string DefaultExtension { get; set; } = ".nedamacro";
            public bool AutoSaveOnStop { get; set; } = true;
            public bool AutoCompile { get; set; } = true;
            public bool EnableCompression { get; set; } = true;
            public bool EnableEncryption { get; set; } = false;
            public string EncryptionKey { get; set; }
            public int MaxRecordingTime { get; set; } = 3600; // seconds;
            public int MaxMacroSize { get; set; } = 10 * 1024 * 1024; // 10 MB;
            public bool EnableVersioning { get; set; } = true;
            public bool EnableUndoRedo { get; set; } = true;
            public bool EnableRealTimePreview { get; set; } = true;
            public Dictionary<string, object> DefaultVariables { get; set; } = new();
            public PlaybackSpeed DefaultPlaybackSpeed { get; set; } = PlaybackSpeed.Normal;
        }

        public enum PlaybackSpeed;
        {
            VerySlow = 25,
            Slow = 50,
            Normal = 100,
            Fast = 200,
            VeryFast = 400,
            Instant = 0;
        }

        public enum MacroType;
        {
            CommandSequence,
            Script,
            Hybrid,
            Template,
            Dynamic;
        }

        public enum ActionType;
        {
            Command,
            KeyboardInput,
            MouseAction,
            Delay,
            Condition,
            Loop,
            VariableAssignment,
            FunctionCall,
            SystemCall,
            UserInput,
            FileOperation,
            NetworkOperation,
            UIInteraction;
        }

        [Flags]
        public enum MacroFlags;
        {
            None = 0,
            Compressed = 1,
            Encrypted = 2,
            Compiled = 4,
            ReadOnly = 8,
            System = 16,
            Template = 32,
            Reversible = 64,
            Looping = 128,
            Conditional = 256;
        }

        #endregion;

        #region Event Args Classes;

        public class MacroRecordingStartedEventArgs : EventArgs;
        {
            public string MacroName { get; }
            public DateTime StartTime { get; }
            public MacroType MacroType { get; }

            public MacroRecordingStartedEventArgs(string macroName, MacroType macroType)
            {
                MacroName = macroName;
                StartTime = DateTime.UtcNow;
                MacroType = macroType;
            }
        }

        public class MacroRecordingStoppedEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public DateTime StartTime { get; }
            public DateTime EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public int ActionCount { get; }
            public bool WasSaved { get; }

            public MacroRecordingStoppedEventArgs(Macro macro, DateTime startTime,
                DateTime endTime, int actionCount, bool wasSaved)
            {
                Macro = macro;
                StartTime = startTime;
                EndTime = endTime;
                ActionCount = actionCount;
                WasSaved = wasSaved;
            }
        }

        public class MacroPlaybackStartedEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public DateTime StartTime { get; }
            public PlaybackSpeed Speed { get; }
            public Dictionary<string, object> Variables { get; }

            public MacroPlaybackStartedEventArgs(Macro macro, PlaybackSpeed speed,
                Dictionary<string, object> variables)
            {
                Macro = macro;
                StartTime = DateTime.UtcNow;
                Speed = speed;
                Variables = variables;
            }
        }

        public class MacroPlaybackCompletedEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public DateTime StartTime { get; }
            public DateTime EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public int ActionsExecuted { get; }
            public object Result { get; }
            public Dictionary<string, object> OutputVariables { get; }

            public MacroPlaybackCompletedEventArgs(Macro macro, DateTime startTime,
                DateTime endTime, int actionsExecuted, object result,
                Dictionary<string, object> outputVariables)
            {
                Macro = macro;
                StartTime = startTime;
                EndTime = endTime;
                ActionsExecuted = actionsExecuted;
                Result = result;
                OutputVariables = outputVariables;
            }
        }

        public class MacroPlaybackErrorEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public int ActionIndex { get; }
            public MacroAction FailedAction { get; }
            public Exception Error { get; }
            public DateTime ErrorTime { get; }

            public MacroPlaybackErrorEventArgs(Macro macro, int actionIndex,
                MacroAction failedAction, Exception error)
            {
                Macro = macro;
                ActionIndex = actionIndex;
                FailedAction = failedAction;
                Error = error;
                ErrorTime = DateTime.UtcNow;
            }
        }

        public class MacroCompiledEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public DateTime CompileTime { get; }
            public TimeSpan CompileDuration { get; }
            public CompilationResult Result { get; }

            public MacroCompiledEventArgs(Macro macro, DateTime compileTime,
                TimeSpan compileDuration, CompilationResult result)
            {
                Macro = macro;
                CompileTime = compileTime;
                CompileDuration = compileDuration;
                Result = result;
            }
        }

        public class MacroSavedEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public string FilePath { get; }
            public DateTime SaveTime { get; }

            public MacroSavedEventArgs(Macro macro, string filePath, DateTime saveTime)
            {
                Macro = macro;
                FilePath = filePath;
                SaveTime = saveTime;
            }
        }

        public class MacroLoadedEventArgs : EventArgs;
        {
            public Macro Macro { get; }
            public string FilePath { get; }
            public DateTime LoadTime { get; }
            public LoadResult Result { get; }

            public MacroLoadedEventArgs(Macro macro, string filePath,
                DateTime loadTime, LoadResult result)
            {
                Macro = macro;
                FilePath = filePath;
                LoadTime = loadTime;
                Result = result;
            }
        }

        #endregion;

        #region Core Data Classes;

        public class Macro;
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public MacroType Type { get; set; }
            public MacroFlags Flags { get; set; }
            public Version Version { get; set; } = new Version(1, 0, 0);
            public DateTime Created { get; set; }
            public DateTime Modified { get; set; }
            public string Author { get; set; }
            public List<MacroAction> Actions { get; set; } = new();
            public Dictionary<string, object> Variables { get; set; } = new();
            public Dictionary<string, string> Metadata { get; set; } = new();
            public List<string> Tags { get; set; } = new();
            public MacroRequirements Requirements { get; set; } = new();
            public MacroStatistics Statistics { get; set; } = new();

            [JsonIgnore]
            public bool IsCompiled => Flags.HasFlag(MacroFlags.Compiled);

            [JsonIgnore]
            public bool IsEncrypted => Flags.HasFlag(MacroFlags.Encrypted);

            [JsonIgnore]
            public bool IsTemplate => Flags.HasFlag(MacroFlags.Template);

            public override string ToString() => $"{Name} ({Id})";
        }

        public class MacroAction;
        {
            public string Id { get; set; }
            public ActionType Type { get; set; }
            public string Name { get; set; }
            public string Content { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new();
            public int DelayBefore { get; set; } // milliseconds;
            public int DelayAfter { get; set; } // milliseconds;
            public List<string> Conditions { get; set; } = new();
            public List<string> OnSuccess { get; set; } = new();
            public List<string> OnError { get; set; } = new();
            public bool Enabled { get; set; } = true;
            public DateTime Timestamp { get; set; }
            public object Result { get; set; }
            public string Error { get; set; }

            public override string ToString() => $"{Type}: {Name}";
        }

        public class MacroRequirements;
        {
            public string MinimumNedaVersion { get; set; }
            public List<string> RequiredModules { get; set; } = new();
            public List<string> RequiredPermissions { get; set; } = new();
            public Dictionary<string, string> SystemRequirements { get; set; } = new();
            public List<string> Dependencies { get; set; } = new();
        }

        public class MacroStatistics;
        {
            public int TotalExecutions { get; set; }
            public int SuccessCount { get; set; }
            public int ErrorCount { get; set; }
            public TimeSpan TotalExecutionTime { get; set; }
            public DateTime LastExecution { get; set; }
            public double AverageExecutionTime =>
                TotalExecutions > 0 ? TotalExecutionTime.TotalMilliseconds / TotalExecutions : 0;
            public double SuccessRate =>
                TotalExecutions > 0 ? (SuccessCount * 100.0) / TotalExecutions : 0;
        }

        public class CompilationResult;
        {
            public bool Success { get; set; }
            public string Output { get; set; }
            public List<CompilationError> Errors { get; set; } = new();
            public List<CompilationWarning> Warnings { get; set; } = new();
            public byte[] CompiledBytes { get; set; }
            public TimeSpan CompileTime { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new();
        }

        public class CompilationError;
        {
            public int Line { get; set; }
            public int Column { get; set; }
            public string ErrorCode { get; set; }
            public string Message { get; set; }
            public string File { get; set; }
        }

        public class CompilationWarning;
        {
            public int Line { get; set; }
            public int Column { get; set; }
            public string WarningCode { get; set; }
            public string Message { get; set; }
        }

        public class LoadResult;
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public List<string> Warnings { get; set; } = new();
            public List<string> Errors { get; set; } = new();
            public TimeSpan LoadTime { get; set; }
        }

        #endregion;

        #region Constructor and Initialization;

        public MacroSystem(ILogger logger = null,
            MacroConfiguration configuration = null,
            CommandExecutor commandExecutor = null,
            ScriptRunner scriptRunner = null)
        {
            _logger = logger ?? new DefaultLogger();
            _configuration = configuration ?? new MacroConfiguration();
            _commandExecutor = commandExecutor ?? new CommandExecutor();
            _scriptRunner = scriptRunner ?? new ScriptRunner();

            try
            {
                _storage = new MacroStorage(_configuration, _logger);
                _recorder = new MacroRecorder(_logger);
                _player = new MacroPlayer(_commandExecutor, _scriptRunner, _logger);
                _editor = new MacroEditor(_logger);
                _compiler = new MacroCompiler(_logger);

                _loadedMacros = new Dictionary<string, Macro>(StringComparer.OrdinalIgnoreCase);
                _playbackCancellationToken = new CancellationTokenSource();

                InitializeSystem();
                LoadMacrosFromStorage();

                _isInitialized = true;
                _logger.LogInformation("MacroSystem initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize MacroSystem: {ex.Message}", ex);
                throw new MacroSystemInitializationException("Failed to initialize MacroSystem", ex);
            }
        }

        private void InitializeSystem()
        {
            // Ensure storage directory exists;
            if (!Directory.Exists(_configuration.StoragePath))
            {
                Directory.CreateDirectory(_configuration.StoragePath);
                _logger.LogDebug($"Created macro storage directory: {_configuration.StoragePath}");
            }

            // Initialize default variables;
            InitializeDefaultVariables();

            // Subscribe to events;
            _recorder.RecordingStarted += OnRecorderRecordingStarted;
            _recorder.RecordingStopped += OnRecorderRecordingStopped;
            _recorder.ActionRecorded += OnRecorderActionRecorded;

            _player.PlaybackStarted += OnPlayerPlaybackStarted;
            _player.PlaybackCompleted += OnPlayerPlaybackCompleted;
            _player.PlaybackError += OnPlayerPlaybackError;
            _player.ActionExecuted += OnPlayerActionExecuted;

            _logger.LogDebug("MacroSystem components initialized");
        }

        private void InitializeDefaultVariables()
        {
            // System variables;
            _configuration.DefaultVariables["DATETIME"] = DateTime.Now;
            _configuration.DefaultVariables["USERNAME"] = Environment.UserName;
            _configuration.DefaultVariables["MACHINENAME"] = Environment.MachineName;
            _configuration.DefaultVariables["OS"] = Environment.OSVersion.Platform.ToString();
            _configuration.DefaultVariables["CURRENT_DIR"] = Environment.CurrentDirectory;

            // Macro system variables;
            _configuration.DefaultVariables["MACRO_COUNT"] = 0;
            _configuration.DefaultVariables["IS_RECORDING"] = false;
            _configuration.DefaultVariables["IS_PLAYING"] = false;
        }

        private void LoadMacrosFromStorage()
        {
            try
            {
                var macros = _storage.LoadAllMacros();
                foreach (var macro in macros)
                {
                    _loadedMacros[macro.Id] = macro;
                }
                _logger.LogInformation($"Loaded {macros.Count} macros from storage");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load macros from storage: {ex.Message}");
            }
        }

        #endregion;

        #region Public Methods - Recording;

        public void StartRecording(string macroName, MacroType type = MacroType.CommandSequence)
        {
            ValidateSystemState();

            if (string.IsNullOrWhiteSpace(macroName))
                throw new ArgumentException("Macro name cannot be null or empty", nameof(macroName));

            if (_isRecording)
                throw new InvalidOperationException("Already recording a macro");

            if (_isPlaying)
                throw new InvalidOperationException("Cannot record while playing back");

            try
            {
                var macroId = GenerateMacroId(macroName);
                var macro = new Macro;
                {
                    Id = macroId,
                    Name = macroName,
                    Type = type,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    Author = Environment.UserName,
                    Variables = new Dictionary<string, object>(_configuration.DefaultVariables)
                };

                _recorder.StartRecording(macro);
                _isRecording = true;

                RecordingStarted?.Invoke(this, new MacroRecordingStartedEventArgs(macroName, type));
                _logger.LogInformation($"Started recording macro: {macroName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start recording: {ex.Message}", ex);
                throw new MacroRecordingException($"Failed to start recording macro '{macroName}'", ex);
            }
        }

        public Macro StopRecording(bool save = true)
        {
            ValidateSystemState();

            if (!_isRecording)
                throw new InvalidOperationException("Not currently recording");

            try
            {
                var macro = _recorder.StopRecording();
                _isRecording = false;

                // Update macro statistics;
                macro.Modified = DateTime.UtcNow;

                // Save if requested;
                bool wasSaved = false;
                if (save && _configuration.AutoSaveOnStop)
                {
                    SaveMacro(macro);
                    wasSaved = true;
                }

                // Add to loaded macros;
                _loadedMacros[macro.Id] = macro;

                RecordingStopped?.Invoke(this, new MacroRecordingStoppedEventArgs(
                    macro, macro.Created, DateTime.UtcNow, macro.Actions.Count, wasSaved));

                _logger.LogInformation($"Stopped recording macro: {macro.Name} ({macro.Actions.Count} actions)");

                return macro;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to stop recording: {ex.Message}", ex);
                throw new MacroRecordingException("Failed to stop recording", ex);
            }
        }

        public void PauseRecording()
        {
            if (_isRecording && _recorder.IsRecording)
            {
                _recorder.PauseRecording();
                _logger.LogDebug("Recording paused");
            }
        }

        public void ResumeRecording()
        {
            if (_isRecording && !_recorder.IsRecording)
            {
                _recorder.ResumeRecording();
                _logger.LogDebug("Recording resumed");
            }
        }

        public void RecordAction(ActionType type, string content,
            Dictionary<string, object> parameters = null)
        {
            if (!_isRecording)
                throw new InvalidOperationException("Not currently recording");

            _recorder.RecordAction(type, content, parameters);
        }

        #endregion;

        #region Public Methods - Playback;

        public async Task<object> PlayMacroAsync(string macroIdOrName,
            PlaybackSpeed speed = PlaybackSpeed.Normal,
            Dictionary<string, object> variables = null,
            CancellationToken cancellationToken = default)
        {
            ValidateSystemState();

            if (_isPlaying)
                throw new InvalidOperationException("Already playing a macro");

            if (_isRecording)
                throw new InvalidOperationException("Cannot play while recording");

            var macro = GetMacro(macroIdOrName);
            if (macro == null)
                throw new MacroNotFoundException($"Macro not found: {macroIdOrName}");

            try
            {
                _isPlaying = true;

                // Merge variables;
                var playbackVariables = MergeVariables(macro.Variables, variables);

                // Start playback;
                var result = await _player.PlayMacroAsync(macro, speed, playbackVariables,
                    cancellationToken);

                // Update statistics;
                UpdateMacroStatistics(macro, true);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Macro playback cancelled: {macro.Name}");
                throw;
            }
            catch (Exception ex)
            {
                UpdateMacroStatistics(macro, false);
                _logger.LogError($"Macro playback failed: {ex.Message}", ex);
                throw new MacroPlaybackException($"Failed to play macro '{macro.Name}'", ex);
            }
            finally
            {
                _isPlaying = false;
            }
        }

        public void PlayMacro(string macroIdOrName,
            PlaybackSpeed speed = PlaybackSpeed.Normal,
            Dictionary<string, object> variables = null)
        {
            PlayMacroAsync(macroIdOrName, speed, variables).Wait();
        }

        public void StopPlayback()
        {
            if (_isPlaying)
            {
                _playbackCancellationToken.Cancel();
                _logger.LogDebug("Playback stopped");
            }
        }

        public async Task<object> PlayMacroStepByStepAsync(string macroIdOrName,
            Action<MacroAction, object> onStep = null,
            CancellationToken cancellationToken = default)
        {
            var macro = GetMacro(macroIdOrName);
            if (macro == null)
                throw new MacroNotFoundException($"Macro not found: {macroIdOrName}");

            return await _player.PlayMacroStepByStepAsync(macro, onStep, cancellationToken);
        }

        #endregion;

        #region Public Methods - Management;

        public Macro CreateMacro(string name, MacroType type = MacroType.CommandSequence,
            string description = null)
        {
            ValidateSystemState();

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Macro name cannot be null or empty", nameof(name));

            var macroId = GenerateMacroId(name);

            if (_loadedMacros.ContainsKey(macroId))
                throw new MacroAlreadyExistsException($"Macro already exists: {name}");

            var macro = new Macro;
            {
                Id = macroId,
                Name = name,
                Description = description ?? $"Macro created on {DateTime.Now:yyyy-MM-dd}",
                Type = type,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Author = Environment.UserName,
                Variables = new Dictionary<string, object>(_configuration.DefaultVariables),
                Statistics = new MacroStatistics()
            };

            _loadedMacros[macro.Id] = macro;

            _logger.LogDebug($"Created new macro: {name} ({type})");

            return macro;
        }

        public void SaveMacro(Macro macro)
        {
            ValidateSystemState();

            if (macro == null)
                throw new ArgumentNullException(nameof(macro));

            try
            {
                // Update modified timestamp;
                macro.Modified = DateTime.UtcNow;

                // Compile if enabled;
                if (_configuration.AutoCompile && !macro.IsCompiled)
                {
                    var compileResult = CompileMacro(macro);
                    if (compileResult.Success)
                    {
                        macro.Flags |= MacroFlags.Compiled;
                    }
                }

                // Save to storage;
                var filePath = _storage.SaveMacro(macro);

                // Update in memory;
                _loadedMacros[macro.Id] = macro;

                MacroSaved?.Invoke(this, new MacroSavedEventArgs(macro, filePath, DateTime.UtcNow));

                _logger.LogInformation($"Saved macro: {macro.Name} to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save macro '{macro.Name}': {ex.Message}", ex);
                throw new MacroSaveException($"Failed to save macro '{macro.Name}'", ex);
            }
        }

        public Macro LoadMacro(string filePath)
        {
            ValidateSystemState();

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Macro file not found: {filePath}");

            try
            {
                var loadResult = _storage.LoadMacro(filePath);
                if (!loadResult.Success)
                    throw new MacroLoadException($"Failed to load macro: {loadResult.Message}");

                var macro = loadResult.Macro;

                // Add to loaded macros;
                _loadedMacros[macro.Id] = macro;

                MacroLoaded?.Invoke(this, new MacroLoadedEventArgs(
                    macro, filePath, DateTime.UtcNow, loadResult));

                _logger.LogInformation($"Loaded macro: {macro.Name} from {filePath}");

                return macro;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load macro from '{filePath}': {ex.Message}", ex);
                throw new MacroLoadException($"Failed to load macro from '{filePath}'", ex);
            }
        }

        public void DeleteMacro(string macroIdOrName, bool deleteFile = true)
        {
            ValidateSystemState();

            var macro = GetMacro(macroIdOrName);
            if (macro == null)
                throw new MacroNotFoundException($"Macro not found: {macroIdOrName}");

            try
            {
                // Remove from memory;
                _loadedMacros.Remove(macro.Id);

                // Delete file if requested;
                if (deleteFile)
                {
                    _storage.DeleteMacro(macro.Id);
                }

                _logger.LogInformation($"Deleted macro: {macro.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete macro '{macro.Name}': {ex.Message}", ex);
                throw new MacroDeleteException($"Failed to delete macro '{macro.Name}'", ex);
            }
        }

        public Macro GetMacro(string macroIdOrName)
        {
            if (string.IsNullOrWhiteSpace(macroIdOrName))
                return null;

            // Try by ID;
            if (_loadedMacros.TryGetValue(macroIdOrName, out var macro))
                return macro;

            // Try by name (case-insensitive)
            macro = _loadedMacros.Values.FirstOrDefault(m =>
                m.Name.Equals(macroIdOrName, StringComparison.OrdinalIgnoreCase));

            return macro;
        }

        public List<Macro> FindMacros(string searchTerm, bool searchContent = false)
        {
            var results = new List<Macro>();

            foreach (var macro in _loadedMacros.Values)
            {
                if (macro.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    macro.Description?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true ||
                    macro.Tags.Any(tag => tag.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(macro);
                }
                else if (searchContent)
                {
                    // Search in action content;
                    if (macro.Actions.Any(action =>
                        action.Content?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true))
                    {
                        results.Add(macro);
                    }
                }
            }

            return results;
        }

        public Macro DuplicateMacro(string sourceMacroIdOrName, string newName)
        {
            var sourceMacro = GetMacro(sourceMacroIdOrName);
            if (sourceMacro == null)
                throw new MacroNotFoundException($"Source macro not found: {sourceMacroIdOrName}");

            var newMacro = new Macro;
            {
                Id = GenerateMacroId(newName),
                Name = newName,
                Description = $"Copy of {sourceMacro.Name}",
                Type = sourceMacro.Type,
                Flags = sourceMacro.Flags,
                Version = sourceMacro.Version,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                Author = Environment.UserName,
                Actions = sourceMacro.Actions.Select(a => CloneAction(a)).ToList(),
                Variables = new Dictionary<string, object>(sourceMacro.Variables),
                Metadata = new Dictionary<string, string>(sourceMacro.Metadata),
                Tags = new List<string>(sourceMacro.Tags),
                Requirements = new MacroRequirements;
                {
                    MinimumNedaVersion = sourceMacro.Requirements.MinimumNedaVersion,
                    RequiredModules = new List<string>(sourceMacro.Requirements.RequiredModules),
                    RequiredPermissions = new List<string>(sourceMacro.Requirements.RequiredPermissions),
                    SystemRequirements = new Dictionary<string, string>(sourceMacro.Requirements.SystemRequirements),
                    Dependencies = new List<string>(sourceMacro.Requirements.Dependencies)
                },
                Statistics = new MacroStatistics()
            };

            _loadedMacros[newMacro.Id] = newMacro;

            _logger.LogDebug($"Duplicated macro: {sourceMacro.Name} -> {newName}");

            return newMacro;
        }

        #endregion;

        #region Public Methods - Editing and Compilation;

        public Macro EditMacro(string macroIdOrName, Action<Macro> editAction)
        {
            var macro = GetMacro(macroIdOrName);
            if (macro == null)
                throw new MacroNotFoundException($"Macro not found: {macroIdOrName}");

            try
            {
                editAction?.Invoke(macro);
                macro.Modified = DateTime.UtcNow;

                // Clear compiled flag if edited;
                if (macro.IsCompiled)
                {
                    macro.Flags &= ~MacroFlags.Compiled;
                }

                _logger.LogDebug($"Edited macro: {macro.Name}");

                return macro;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to edit macro '{macro.Name}': {ex.Message}", ex);
                throw new MacroEditException($"Failed to edit macro '{macro.Name}'", ex);
            }
        }

        public CompilationResult CompileMacro(Macro macro)
        {
            ValidateSystemState();

            if (macro == null)
                throw new ArgumentNullException(nameof(macro));

            try
            {
                var startTime = DateTime.UtcNow;
                var result = _compiler.Compile(macro);
                var compileTime = DateTime.UtcNow - startTime;

                if (result.Success)
                {
                    macro.Flags |= MacroFlags.Compiled;
                    _logger.LogInformation($"Compiled macro: {macro.Name} ({compileTime.TotalMilliseconds}ms)");
                }
                else;
                {
                    _logger.LogWarning($"Compilation failed for macro '{macro.Name}': {result.Errors.FirstOrDefault()?.Message}");
                }

                MacroCompiled?.Invoke(this, new MacroCompiledEventArgs(
                    macro, DateTime.UtcNow, compileTime, result));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to compile macro '{macro.Name}': {ex.Message}", ex);
                throw new MacroCompilationException($"Failed to compile macro '{macro.Name}'", ex);
            }
        }

        public Macro ImportFromScript(string scriptPath, string macroName = null)
        {
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Script file not found: {scriptPath}");

            try
            {
                var scriptContent = File.ReadAllText(scriptPath);
                var macroNameToUse = macroName ?? Path.GetFileNameWithoutExtension(scriptPath);

                var macro = new Macro;
                {
                    Id = GenerateMacroId(macroNameToUse),
                    Name = macroNameToUse,
                    Description = $"Imported from script: {Path.GetFileName(scriptPath)}",
                    Type = MacroType.Script,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    Author = Environment.UserName,
                    Actions = new List<MacroAction>
                    {
                        new MacroAction;
                        {
                            Id = Guid.NewGuid().ToString(),
                            Type = ActionType.Script,
                            Name = "Script Execution",
                            Content = scriptContent,
                            Timestamp = DateTime.UtcNow;
                        }
                    }
                };

                _loadedMacros[macro.Id] = macro;

                _logger.LogInformation($"Imported script as macro: {macro.Name} from {scriptPath}");

                return macro;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to import script '{scriptPath}': {ex.Message}", ex);
                throw new MacroImportException($"Failed to import script '{scriptPath}'", ex);
            }
        }

        public string ExportToScript(Macro macro, string outputPath = null)
        {
            if (macro == null)
                throw new ArgumentNullException(nameof(macro));

            try
            {
                var scriptContent = _compiler.ExportToScript(macro);
                var exportPath = outputPath ?? Path.Combine(
                    _configuration.StoragePath,
                    $"{macro.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.nedascript");

                File.WriteAllText(exportPath, scriptContent);

                _logger.LogInformation($"Exported macro to script: {macro.Name} -> {exportPath}");

                return exportPath;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to export macro '{macro.Name}': {ex.Message}", ex);
                throw new MacroExportException($"Failed to export macro '{macro.Name}'", ex);
            }
        }

        #endregion;

        #region Public Methods - Variables and Templates;

        public void SetVariable(string macroIdOrName, string variableName, object value)
        {
            var macro = GetMacro(macroIdOrName);
            if (macro == null)
                throw new MacroNotFoundException($"Macro not found: {macroIdOrName}");

            macro.Variables[variableName] = value;
            macro.Modified = DateTime.UtcNow;
        }

        public object GetVariable(string macroIdOrName, string variableName)
        {
            var macro = GetMacro(macroIdOrName);
            if (macro == null)
                throw new MacroNotFoundException($"Macro not found: {macroIdOrName}");

            return macro.Variables.TryGetValue(variableName, out var value) ? value : null;
        }

        public Macro CreateTemplate(string templateName, Macro baseMacro = null)
        {
            var template = baseMacro != null;
                ? DuplicateMacro(baseMacro.Id, templateName)
                : CreateMacro(templateName, MacroType.Template);

            template.Flags |= MacroFlags.Template;
            template.Description = $"Template: {template.Description}";

            _logger.LogDebug($"Created template: {template.Name}");

            return template;
        }

        public Macro CreateFromTemplate(string templateName, string newMacroName)
        {
            var template = GetMacro(templateName);
            if (template == null || !template.IsTemplate)
                throw new InvalidOperationException($"Template not found: {templateName}");

            var newMacro = DuplicateMacro(template.Id, newMacroName);
            newMacro.Flags &= ~MacroFlags.Template; // Remove template flag;
            newMacro.Description = newMacro.Description?.Replace("Template: ", "");

            _logger.LogDebug($"Created macro from template: {template.Name} -> {newMacro.Name}");

            return newMacro;
        }

        #endregion;

        #region Private Methods;

        private void ValidateSystemState()
        {
            if (!_isInitialized)
                throw new MacroSystemNotInitializedException("MacroSystem is not initialized");
        }

        private string GenerateMacroId(string macroName)
        {
            var sanitizedName = Regex.Replace(macroName, @"[^\w\s-]", "");
            var baseId = Regex.Replace(sanitizedName, @"\s+", "-").ToLowerInvariant();
            var id = $"{baseId}-{Guid.NewGuid():N}";

            return id.Length > 100 ? id.Substring(0, 100) : id;
        }

        private Dictionary<string, object> MergeVariables(
            Dictionary<string, object> macroVariables,
            Dictionary<string, object> overrideVariables)
        {
            var result = new Dictionary<string, object>(macroVariables);

            if (overrideVariables != null)
            {
                foreach (var kvp in overrideVariables)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            // Add system variables;
            result["MACRO_PLAYBACK_TIME"] = DateTime.UtcNow;
            result["PLAYBACK_SPEED"] = _configuration.DefaultPlaybackSpeed;
            result["RANDOM_ID"] = Guid.NewGuid().ToString("N");

            return result;
        }

        private void UpdateMacroStatistics(Macro macro, bool success)
        {
            macro.Statistics.TotalExecutions++;

            if (success)
            {
                macro.Statistics.SuccessCount++;
            }
            else;
            {
                macro.Statistics.ErrorCount++;
            }

            macro.Statistics.LastExecution = DateTime.UtcNow;
            macro.Modified = DateTime.UtcNow;
        }

        private MacroAction CloneAction(MacroAction source)
        {
            return new MacroAction;
            {
                Id = Guid.NewGuid().ToString(),
                Type = source.Type,
                Name = source.Name,
                Content = source.Content,
                Parameters = new Dictionary<string, object>(source.Parameters),
                DelayBefore = source.DelayBefore,
                DelayAfter = source.DelayAfter,
                Conditions = new List<string>(source.Conditions),
                OnSuccess = new List<string>(source.OnSuccess),
                OnError = new List<string>(source.OnError),
                Enabled = source.Enabled,
                Timestamp = DateTime.UtcNow;
            };
        }

        #endregion;

        #region Event Handlers;

        private void OnRecorderRecordingStarted(object sender, EventArgs e)
        {
            // Update system variable;
            _configuration.DefaultVariables["IS_RECORDING"] = true;
        }

        private void OnRecorderRecordingStopped(object sender, EventArgs e)
        {
            // Update system variable;
            _configuration.DefaultVariables["IS_RECORDING"] = false;
            _configuration.DefaultVariables["MACRO_COUNT"] = _loadedMacros.Count;
        }

        private void OnRecorderActionRecorded(object sender, MacroActionEventArgs e)
        {
            _logger.LogDebug($"Action recorded: {e.Action.Type} - {e.Action.Name}");
        }

        private void OnPlayerPlaybackStarted(object sender, EventArgs e)
        {
            // Update system variable;
            _configuration.DefaultVariables["IS_PLAYING"] = true;
        }

        private void OnPlayerPlaybackCompleted(object sender, EventArgs e)
        {
            // Update system variable;
            _configuration.DefaultVariables["IS_PLAYING"] = false;
        }

        private void OnPlayerPlaybackError(object sender, MacroPlaybackErrorEventArgs e)
        {
            PlaybackError?.Invoke(this, e);
        }

        private void OnPlayerActionExecuted(object sender, MacroActionEventArgs e)
        {
            _logger.LogDebug($"Action executed: {e.Action.Type} - {e.Action.Name}");
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _playbackCancellationToken?.Cancel();
                    _playbackCancellationToken?.Dispose();

                    _recorder?.Dispose();
                    _player?.Dispose();
                    _storage?.Dispose();
                    _compiler?.Dispose();

                    // Save all macros;
                    foreach (var macro in _loadedMacros.Values)
                    {
                        try
                        {
                            SaveMacro(macro);
                        }
                        catch
                        {
                            // Ignore save errors during disposal;
                        }
                    }
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;

        #region Helper Classes;

        public class MacroActionEventArgs : EventArgs;
        {
            public MacroAction Action { get; }
            public int Index { get; }

            public MacroActionEventArgs(MacroAction action, int index)
            {
                Action = action;
                Index = index;
            }
        }

        #endregion;

        #region System Status;

        public MacroSystemStatus GetStatus()
        {
            return new MacroSystemStatus;
            {
                IsInitialized = _isInitialized,
                IsRecording = _isRecording,
                IsPlaying = _isPlaying,
                TotalMacros = TotalMacros,
                StoragePath = _configuration.StoragePath,
                LoadedMacroNames = _loadedMacros.Values.Select(m => m.Name).ToList(),
                Configuration = _configuration;
            };
        }

        public class MacroSystemStatus;
        {
            public bool IsInitialized { get; set; }
            public bool IsRecording { get; set; }
            public bool IsPlaying { get; set; }
            public int TotalMacros { get; set; }
            public string StoragePath { get; set; }
            public List<string> LoadedMacroNames { get; set; }
            public MacroConfiguration Configuration { get; set; }
        }

        #endregion;
    }

    #region Custom Exceptions;

    public class MacroSystemInitializationException : Exception
    {
        public MacroSystemInitializationException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroSystemNotInitializedException : InvalidOperationException;
    {
        public MacroSystemNotInitializedException(string message)
            : base(message) { }
    }

    public class MacroRecordingException : Exception
    {
        public MacroRecordingException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroPlaybackException : Exception
    {
        public MacroPlaybackException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroNotFoundException : Exception
    {
        public MacroNotFoundException(string message)
            : base(message) { }
    }

    public class MacroAlreadyExistsException : Exception
    {
        public MacroAlreadyExistsException(string message)
            : base(message) { }
    }

    public class MacroSaveException : Exception
    {
        public MacroSaveException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroLoadException : Exception
    {
        public MacroLoadException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroDeleteException : Exception
    {
        public MacroDeleteException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroEditException : Exception
    {
        public MacroEditException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroCompilationException : Exception
    {
        public MacroCompilationException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroImportException : Exception
    {
        public MacroImportException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MacroExportException : Exception
    {
        public MacroExportException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    #endregion;

    #region Default Implementations;

    internal class DefaultLogger : ILogger;
    {
        public void LogInformation(string message) => Console.WriteLine($"[INFO] {message}");
        public void LogDebug(string message) => Console.WriteLine($"[DEBUG] {message}");
        public void LogWarning(string message) => Console.WriteLine($"[WARNING] {message}");
        public void LogError(string message, Exception ex = null) =>
            Console.WriteLine($"[ERROR] {message}: {ex?.Message}");
    }

    #endregion;
}
