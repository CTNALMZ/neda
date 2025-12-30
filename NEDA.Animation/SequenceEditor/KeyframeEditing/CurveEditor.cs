using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Animation.SequenceEditor.TimelineManagement;

namespace NEDA.Animation.SequenceEditor.KeyframeEditing;
{
    /// <summary>
    /// Advanced curve editor for creating, editing, and manipulating animation curves;
    /// with professional-grade tools for bezier curves, easing functions, and real-time;
    /// curve analysis and optimization.
    /// </summary>
    public class CurveEditor : ICurveEditor, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly IKeyframeManager _keyframeManager;
        private readonly IInterpolationEngine _interpolationEngine;
        private readonly IRecoveryEngine _recoveryEngine;
        private readonly ISettingsManager _settingsManager;

        private readonly Dictionary<string, AnimationCurve> _curves;
        private readonly Dictionary<string, CurvePreset> _curvePresets;
        private readonly Dictionary<string, CurveManipulator> _activeManipulators;
        private readonly Dictionary<string, CurveSelection> _selections;
        private readonly List<CurveEditOperation> _editHistory;
        private readonly Queue<CurveCommand> _commandQueue;
        private readonly object _curveLock = new object();

        private CurveEditorState _currentState;
        private CurveEditorSettings _settings;
        private CurveEditorMetrics _metrics;
        private CurveTool _activeTool;
        private CurveSnapSettings _snapSettings;
        private CurveVisualSettings _visualSettings;
        private CurveAnalysisEngine _analysisEngine;
        private CurveOptimizationEngine _optimizationEngine;
        private CurvePresetManager _presetManager;
        private bool _isInitialized;
        private bool _isDisposed;
        private DateTime _sessionStartTime;
        #endregion;

        #region Public Properties;
        /// <summary>
        /// Gets the current curve editor state;
        /// </summary>
        public CurveEditorState CurrentState;
        {
            get;
            {
                lock (_curveLock)
                {
                    return _currentState;
                }
            }
            private set;
            {
                lock (_curveLock)
                {
                    if (_currentState != value)
                    {
                        var previousState = _currentState;
                        _currentState = value;
                        RaiseStateChanged(previousState, value);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the active editing tool;
        /// </summary>
        public CurveTool ActiveTool => _activeTool;

        /// <summary>
        /// Gets the current snap settings;
        /// </summary>
        public CurveSnapSettings SnapSettings => _snapSettings;

        /// <summary>
        /// Gets the visual settings;
        /// </summary>
        public CurveVisualSettings VisualSettings => _visualSettings;

        /// <summary>
        /// Gets the number of loaded curves;
        /// </summary>
        public int LoadedCurvesCount;
        {
            get;
            {
                lock (_curveLock)
                {
                    return _curves.Count;
                }
            }
        }

        /// <summary>
        /// Gets the number of available presets;
        /// </summary>
        public int PresetCount;
        {
            get;
            {
                lock (_curveLock)
                {
                    return _curvePresets.Count;
                }
            }
        }

        /// <summary>
        /// Gets the curve editor performance metrics;
        /// </summary>
        public CurveEditorMetrics Metrics => _metrics;

        /// <summary>
        /// Gets whether the curve editor is initialized;
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets the current selection information;
        /// </summary>
        public CurveSelectionInfo SelectionInfo;
        {
            get;
            {
                lock (_curveLock)
                {
                    return new CurveSelectionInfo;
                    {
                        TotalSelections = _selections.Count,
                        SelectedKeyframes = _selections.Values.Sum(s => s.Keyframes.Count),
                        SelectedCurves = _selections.Values.Select(s => s.CurveId).Distinct().Count()
                    };
                }
            }
        }
        #endregion;

        #region Events;
        /// <summary>
        /// Raised when a curve is created;
        /// </summary>
        public event EventHandler<CurveCreatedEventArgs> CurveCreated;

        /// <summary>
        /// Raised when a curve is modified;
        /// </summary>
        public event EventHandler<CurveModifiedEventArgs> CurveModified;

        /// <summary>
        /// Raised when keyframes are added;
        /// </summary>
        public event EventHandler<KeyframesAddedEventArgs> KeyframesAdded;

        /// <summary>
        /// Raised when keyframes are removed;
        /// </summary>
        public event EventHandler<KeyframesRemovedEventArgs> KeyframesRemoved;

        /// <summary>
        /// Raised when keyframes are modified;
        /// </summary>
        public event EventHandler<KeyframesModifiedEventArgs> KeyframesModified;

        /// <summary>
        /// Raised when selection changes;
        /// </summary>
        public event EventHandler<SelectionChangedEventArgs> SelectionChanged;

        /// <summary>
        /// Raised when editor state changes;
        /// </summary>
        public event EventHandler<CurveEditorStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Raised when a curve operation is performed;
        /// </summary>
        public event EventHandler<CurveOperationEventArgs> CurveOperationPerformed;

        /// <summary>
        /// Raised when curve analysis is completed;
        /// </summary>
        public event EventHandler<CurveAnalysisCompletedEventArgs> CurveAnalysisCompleted;

        /// <summary>
        /// Raised when curve optimization is applied;
        /// </summary>
        public event EventHandler<CurveOptimizationAppliedEventArgs> CurveOptimizationApplied;
        #endregion;

        #region Constructor;
        /// <summary>
        /// Initializes a new instance of the CurveEditor;
        /// </summary>
        public CurveEditor(
            ILogger logger,
            IKeyframeManager keyframeManager,
            IInterpolationEngine interpolationEngine,
            IRecoveryEngine recoveryEngine,
            ISettingsManager settingsManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _keyframeManager = keyframeManager ?? throw new ArgumentNullException(nameof(keyframeManager));
            _interpolationEngine = interpolationEngine ?? throw new ArgumentNullException(nameof(interpolationEngine));
            _recoveryEngine = recoveryEngine ?? throw new ArgumentNullException(nameof(recoveryEngine));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

            _curves = new Dictionary<string, AnimationCurve>();
            _curvePresets = new Dictionary<string, CurvePreset>();
            _activeManipulators = new Dictionary<string, CurveManipulator>();
            _selections = new Dictionary<string, CurveSelection>();
            _editHistory = new List<CurveEditOperation>();
            _commandQueue = new Queue<CurveCommand>();

            _currentState = CurveEditorState.Inactive;
            _metrics = new CurveEditorMetrics();
            _activeTool = CurveTool.Select;
            _snapSettings = new CurveSnapSettings();
            _visualSettings = new CurveVisualSettings();
            _analysisEngine = new CurveAnalysisEngine(logger);
            _optimizationEngine = new CurveOptimizationEngine(logger);
            _presetManager = new CurvePresetManager(logger);

            _sessionStartTime = DateTime.UtcNow;

            _logger.LogInformation("CurveEditor instance created");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Initializes the curve editor system;
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("CurveEditor is already initialized");
                    return;
                }

                await LoadConfigurationAsync();
                await InitializeSubsystemsAsync();
                await LoadDefaultPresetsAsync();
                await InitializeToolsAsync();

                _isInitialized = true;
                CurrentState = CurveEditorState.Ready;

                _logger.LogInformation("CurveEditor initialized successfully");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to initialize CurveEditor", ex);
                throw new CurveEditorException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Updates the curve editor and processes pending operations;
        /// </summary>
        public async Task UpdateAsync(float deltaTime)
        {
            try
            {
                if (!_isInitialized || CurrentState == CurveEditorState.Paused) return;

                var updateTimer = System.Diagnostics.Stopwatch.StartNew();

                await ProcessCurveCommandsAsync();
                await UpdateActiveManipulatorsAsync(deltaTime);
                await UpdateCurveAnalysisAsync(deltaTime);
                await UpdatePerformanceMetricsAsync(deltaTime);

                updateTimer.Stop();
                _metrics.LastUpdateDuration = (float)updateTimer.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Error during curve editor update", ex);
                await _recoveryEngine.ExecuteRecoveryStrategyAsync("CurveEditorUpdate");
            }
        }

        /// <summary>
        /// Creates a new animation curve;
        /// </summary>
        public async Task<string> CreateCurveAsync(CurveCreationParameters creationParams)
        {
            if (creationParams == null)
                throw new ArgumentNullException(nameof(creationParams));

            try
            {
                await ValidateSystemState();
                await ValidateCreationParameters(creationParams);

                var curveId = GenerateCurveId();
                var curve = new AnimationCurve(curveId, creationParams);

                // Add initial keyframes if provided;
                if (creationParams.InitialKeyframes != null && creationParams.InitialKeyframes.Any())
                {
                    foreach (var keyframe in creationParams.InitialKeyframes)
                    {
                        curve.AddKeyframe(keyframe);
                    }
                }

                lock (_curveLock)
                {
                    _curves[curveId] = curve;
                }

                _metrics.CurvesCreated++;
                RaiseCurveCreated(curveId, creationParams);

                _logger.LogDebug($"Curve created: {curveId} ({creationParams.CurveType})");

                return curveId;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to create curve", ex);
                throw new CurveEditorException("Curve creation failed", ex);
            }
        }

        /// <summary>
        /// Loads a curve from data;
        /// </summary>
        public async Task<string> LoadCurveAsync(CurveData curveData)
        {
            if (curveData == null)
                throw new ArgumentNullException(nameof(curveData));

            try
            {
                await ValidateSystemState();
                await ValidateCurveData(curveData);

                var curveId = curveData.CurveId ?? GenerateCurveId();
                var curve = new AnimationCurve(curveId, curveData.CreationParameters);

                // Load keyframes;
                foreach (var keyframeData in curveData.Keyframes)
                {
                    var keyframe = new Keyframe(keyframeData);
                    curve.AddKeyframe(keyframe);
                }

                // Load curve properties;
                curve.CurveType = curveData.CurveType;
                curve.PreWrapMode = curveData.PreWrapMode;
                curve.PostWrapMode = curveData.PostWrapMode;
                curve.Color = curveData.Color;

                lock (_curveLock)
                {
                    _curves[curveId] = curve;
                }

                _metrics.CurvesLoaded++;
                _logger.LogDebug($"Curve loaded: {curveId}");

                return curveId;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to load curve", ex);
                throw new CurveEditorException("Curve load failed", ex);
            }
        }

        /// <summary>
        /// Saves a curve to data;
        /// </summary>
        public async Task<CurveData> SaveCurveAsync(string curveId)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                var curve = await GetCurveAsync(curveId);

                var curveData = new CurveData;
                {
                    CurveId = curve.CurveId,
                    CreationParameters = curve.CreationParameters.Clone(),
                    CurveType = curve.CurveType,
                    PreWrapMode = curve.PreWrapMode,
                    PostWrapMode = curve.PostWrapMode,
                    Color = curve.Color,
                    Keyframes = curve.Keyframes.Select(k => k.ToKeyframeData()).ToList()
                };

                _metrics.CurvesSaved++;
                _logger.LogDebug($"Curve saved: {curveId}");

                return curveData;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to save curve: {curveId}", ex);
                throw new CurveEditorException("Curve save failed", ex);
            }
        }

        /// <summary>
        /// Adds keyframes to a curve;
        /// </summary>
        public async Task AddKeyframesAsync(string curveId, IEnumerable<Keyframe> keyframes)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            if (keyframes == null)
                throw new ArgumentNullException(nameof(keyframes));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var addedKeyframes = new List<Keyframe>();

                foreach (var keyframe in keyframes)
                {
                    // Apply snapping if enabled;
                    var snappedKeyframe = ApplySnapping(keyframe);

                    // Validate keyframe can be added;
                    if (curve.CanAddKeyframe(snappedKeyframe))
                    {
                        curve.AddKeyframe(snappedKeyframe);
                        addedKeyframes.Add(snappedKeyframe);
                    }
                }

                if (addedKeyframes.Any())
                {
                    // Record edit operation;
                    RecordEditOperation(new CurveEditOperation;
                    {
                        OperationType = CurveEditOperationType.AddKeyframes,
                        CurveId = curveId,
                        AffectedKeyframes = addedKeyframes,
                        Timestamp = DateTime.UtcNow;
                    });

                    _metrics.KeyframesAdded += addedKeyframes.Count;
                    RaiseKeyframesAdded(curveId, addedKeyframes);

                    _logger.LogDebug($"Keyframes added to curve {curveId}: {addedKeyframes.Count} keyframes");
                }
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to add keyframes to curve: {curveId}", ex);
                throw new CurveEditorException("Keyframe addition failed", ex);
            }
        }

        /// <summary>
        /// Removes keyframes from a curve;
        /// </summary>
        public async Task RemoveKeyframesAsync(string curveId, IEnumerable<string> keyframeIds)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            if (keyframeIds == null)
                throw new ArgumentNullException(nameof(keyframeIds));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var removedKeyframes = new List<Keyframe>();

                foreach (var keyframeId in keyframeIds)
                {
                    var keyframe = curve.GetKeyframe(keyframeId);
                    if (keyframe != null)
                    {
                        curve.RemoveKeyframe(keyframeId);
                        removedKeyframes.Add(keyframe);
                    }
                }

                if (removedKeyframes.Any())
                {
                    // Record edit operation;
                    RecordEditOperation(new CurveEditOperation;
                    {
                        OperationType = CurveEditOperationType.RemoveKeyframes,
                        CurveId = curveId,
                        AffectedKeyframes = removedKeyframes,
                        Timestamp = DateTime.UtcNow;
                    });

                    _metrics.KeyframesRemoved += removedKeyframes.Count;
                    RaiseKeyframesRemoved(curveId, removedKeyframes);

                    _logger.LogDebug($"Keyframes removed from curve {curveId}: {removedKeyframes.Count} keyframes");
                }
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to remove keyframes from curve: {curveId}", ex);
                throw new CurveEditorException("Keyframe removal failed", ex);
            }
        }

        /// <summary>
        /// Modifies keyframes in a curve;
        /// </summary>
        public async Task ModifyKeyframesAsync(string curveId, IEnumerable<KeyframeModification> modifications)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            if (modifications == null)
                throw new ArgumentNullException(nameof(modifications));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var modifiedKeyframes = new List<Keyframe>();

                foreach (var modification in modifications)
                {
                    var keyframe = curve.GetKeyframe(modification.KeyframeId);
                    if (keyframe != null)
                    {
                        var originalKeyframe = keyframe.Clone();

                        // Apply modifications;
                        ApplyKeyframeModification(keyframe, modification);

                        // Apply snapping;
                        var snappedKeyframe = ApplySnapping(keyframe);
                        curve.UpdateKeyframe(modification.KeyframeId, snappedKeyframe);

                        modifiedKeyframes.Add(snappedKeyframe);
                    }
                }

                if (modifiedKeyframes.Any())
                {
                    // Record edit operation;
                    RecordEditOperation(new CurveEditOperation;
                    {
                        OperationType = CurveEditOperationType.ModifyKeyframes,
                        CurveId = curveId,
                        AffectedKeyframes = modifiedKeyframes,
                        Timestamp = DateTime.UtcNow;
                    });

                    _metrics.KeyframesModified += modifiedKeyframes.Count;
                    RaiseKeyframesModified(curveId, modifiedKeyframes);

                    _logger.LogDebug($"Keyframes modified in curve {curveId}: {modifiedKeyframes.Count} keyframes");
                }
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to modify keyframes in curve: {curveId}", ex);
                throw new CurveEditorException("Keyframe modification failed", ex);
            }
        }

        /// <summary>
        /// Sets the selection for a curve;
        /// </summary>
        public async Task SetSelectionAsync(string curveId, CurveSelection selection)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            if (selection == null)
                throw new ArgumentNullException(nameof(selection));

            try
            {
                await ValidateSystemState();

                var previousSelection = await GetSelectionAsync(curveId);

                lock (_curveLock)
                {
                    _selections[curveId] = selection;
                }

                _metrics.SelectionsChanged++;
                RaiseSelectionChanged(curveId, previousSelection, selection);

                _logger.LogDebug($"Selection set for curve {curveId}: {selection.Keyframes.Count} keyframes selected");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to set selection for curve: {curveId}", ex);
                throw new CurveEditorException("Selection setting failed", ex);
            }
        }

        /// <summary>
        /// Clears the selection for a curve;
        /// </summary>
        public async Task ClearSelectionAsync(string curveId)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                await ValidateSystemState();

                var previousSelection = await GetSelectionAsync(curveId);

                lock (_curveLock)
                {
                    _selections.Remove(curveId);
                }

                _metrics.SelectionsChanged++;
                RaiseSelectionChanged(curveId, previousSelection, null);

                _logger.LogDebug($"Selection cleared for curve {curveId}");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to clear selection for curve: {curveId}", ex);
                throw new CurveEditorException("Selection clearing failed", ex);
            }
        }

        /// <summary>
        /// Applies a curve preset;
        /// </summary>
        public async Task ApplyPresetAsync(string curveId, string presetId, PresetApplicationParameters parameters)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            if (string.IsNullOrEmpty(presetId))
                throw new ArgumentException("Preset ID cannot be null or empty", nameof(presetId));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var preset = await GetPresetAsync(presetId);

                // Apply preset to curve;
                await _presetManager.ApplyPresetAsync(curve, preset, parameters);

                // Record edit operation;
                RecordEditOperation(new CurveEditOperation;
                {
                    OperationType = CurveEditOperationType.ApplyPreset,
                    CurveId = curveId,
                    PresetId = presetId,
                    Timestamp = DateTime.UtcNow;
                });

                _metrics.PresetsApplied++;
                RaiseCurveModified(curveId, CurveModificationType.PresetApplied);

                _logger.LogDebug($"Preset applied to curve {curveId}: {presetId}");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to apply preset to curve: {curveId}", ex);
                throw new CurveEditorException("Preset application failed", ex);
            }
        }

        /// <summary>
        /// Analyzes a curve for optimization opportunities;
        /// </summary>
        public async Task<CurveAnalysisResult> AnalyzeCurveAsync(string curveId, AnalysisParameters parameters)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var analysisResult = await _analysisEngine.AnalyzeCurveAsync(curve, parameters);

                _metrics.CurvesAnalyzed++;
                RaiseCurveAnalysisCompleted(curveId, analysisResult);

                _logger.LogDebug($"Curve analyzed: {curveId}");

                return analysisResult;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to analyze curve: {curveId}", ex);
                throw new CurveEditorException("Curve analysis failed", ex);
            }
        }

        /// <summary>
        /// Optimizes a curve based on analysis results;
        /// </summary>
        public async Task OptimizeCurveAsync(string curveId, OptimizationParameters parameters)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var optimizationResult = await _optimizationEngine.OptimizeCurveAsync(curve, parameters);

                // Record edit operation;
                RecordEditOperation(new CurveEditOperation;
                {
                    OperationType = CurveEditOperationType.Optimize,
                    CurveId = curveId,
                    OptimizationResult = optimizationResult,
                    Timestamp = DateTime.UtcNow;
                });

                _metrics.CurvesOptimized++;
                RaiseCurveOptimizationApplied(curveId, optimizationResult);

                _logger.LogDebug($"Curve optimized: {curveId}");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to optimize curve: {curveId}", ex);
                throw new CurveEditorException("Curve optimization failed", ex);
            }
        }

        /// <summary>
        /// Sets the active editing tool;
        /// </summary>
        public async Task SetActiveToolAsync(CurveTool tool)
        {
            try
            {
                await ValidateSystemState();

                if (_activeTool != tool)
                {
                    var previousTool = _activeTool;
                    _activeTool = tool;

                    // Initialize tool-specific settings;
                    await InitializeToolAsync(tool);

                    _metrics.ToolChanges++;
                    _logger.LogDebug($"Active tool changed: {previousTool} -> {tool}");
                }
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to set active tool", ex);
                throw new CurveEditorException("Tool setting failed", ex);
            }
        }

        /// <summary>
        /// Updates snap settings;
        /// </summary>
        public async Task UpdateSnapSettingsAsync(CurveSnapSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            try
            {
                await ValidateSystemState();

                _snapSettings = settings.Clone();
                _metrics.SnapSettingsUpdated++;

                _logger.LogDebug("Snap settings updated");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to update snap settings", ex);
                throw new CurveEditorException("Snap settings update failed", ex);
            }
        }

        /// <summary>
        /// Updates visual settings;
        /// </summary>
        public async Task UpdateVisualSettingsAsync(CurveVisualSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            try
            {
                await ValidateSystemState();

                _visualSettings = settings.Clone();
                _metrics.VisualSettingsUpdated++;

                _logger.LogDebug("Visual settings updated");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to update visual settings", ex);
                throw new CurveEditorException("Visual settings update failed", ex);
            }
        }

        /// <summary>
        /// Performs an undo operation;
        /// </summary>
        public async Task UndoAsync()
        {
            try
            {
                await ValidateSystemState();

                if (_editHistory.Count > 0)
                {
                    var lastOperation = _editHistory.Last();
                    await UndoOperationAsync(lastOperation);
                    _editHistory.RemoveAt(_editHistory.Count - 1);

                    _metrics.UndoOperations++;
                    _logger.LogDebug($"Undo operation performed: {lastOperation.OperationType}");
                }
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to perform undo operation", ex);
                throw new CurveEditorException("Undo operation failed", ex);
            }
        }

        /// <summary>
        /// Performs a redo operation (if supported)
        /// </summary>
        public async Task RedoAsync()
        {
            try
            {
                await ValidateSystemState();
                // Redo implementation would require a separate redo stack;
                _metrics.RedoOperations++;
                _logger.LogDebug("Redo operation performed");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to perform redo operation", ex);
                throw new CurveEditorException("Redo operation failed", ex);
            }
        }

        /// <summary>
        /// Queues a curve command for execution;
        /// </summary>
        public async Task QueueCommandAsync(CurveCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            try
            {
                await ValidateSystemState();

                lock (_curveLock)
                {
                    _commandQueue.Enqueue(command);
                }

                _metrics.CommandsQueued++;
                _logger.LogDebug($"Curve command queued: {command.CommandType}");
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync("Failed to queue curve command", ex);
                throw new CurveEditorException("Command queuing failed", ex);
            }
        }

        /// <summary>
        /// Exports curve data for external use;
        /// </summary>
        public async Task<CurveExportData> ExportCurveAsync(string curveId, ExportParameters parameters)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var curveData = await SaveCurveAsync(curveId);

                var exportData = new CurveExportData;
                {
                    CurveData = curveData,
                    ExportFormat = parameters.ExportFormat,
                    SampleRate = parameters.SampleRate,
                    IncludeMetadata = parameters.IncludeMetadata,
                    ExportTime = DateTime.UtcNow;
                };

                _metrics.CurvesExported++;
                _logger.LogDebug($"Curve exported: {curveId}");

                return exportData;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to export curve: {curveId}", ex);
                throw new CurveEditorException("Curve export failed", ex);
            }
        }

        /// <summary>
        /// Gets curve evaluation at a specific time;
        /// </summary>
        public async Task<float> EvaluateCurveAsync(string curveId, float time)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                var curve = await GetCurveAsync(curveId);
                var value = curve.Evaluate(time);

                _metrics.CurveEvaluations++;
                return value;
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to evaluate curve: {curveId}", ex);
                throw new CurveEditorException("Curve evaluation failed", ex);
            }
        }

        /// <summary>
        /// Gets curve information and statistics;
        /// </summary>
        public async Task<CurveInfo> GetCurveInfoAsync(string curveId)
        {
            if (string.IsNullOrEmpty(curveId))
                throw new ArgumentException("Curve ID cannot be null or empty", nameof(curveId));

            try
            {
                var curve = await GetCurveAsync(curveId);

                return new CurveInfo;
                {
                    CurveId = curve.CurveId,
                    CurveType = curve.CurveType,
                    KeyframeCount = curve.Keyframes.Count,
                    TimeRange = curve.GetTimeRange(),
                    ValueRange = curve.GetValueRange(),
                    IsConstant = curve.IsConstant(),
                    IsLinear = curve.IsLinear(),
                    CreationTime = curve.CreationTime;
                };
            }
            catch (Exception ex)
            {
                await HandleCurveExceptionAsync($"Failed to get curve info: {curveId}", ex);
                throw new CurveEditorException("Curve info retrieval failed", ex);
            }
        }
        #endregion;

        #region Private Methods;
        private async Task LoadConfigurationAsync()
        {
            try
            {
                _settings = _settingsManager.GetSection<CurveEditorSettings>("CurveEditor") ?? new CurveEditorSettings();
                _logger.LogInformation("Curve editor configuration loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load curve editor configuration: {ex.Message}");
                _settings = new CurveEditorSettings();
            }

            await Task.CompletedTask;
        }

        private async Task InitializeSubsystemsAsync()
        {
            _analysisEngine.Initialize(_settings);
            _optimizationEngine.Initialize(_settings);
            _presetManager.Initialize(_settings);

            _logger.LogDebug("Curve editor subsystems initialized");
            await Task.CompletedTask;
        }

        private async Task LoadDefaultPresetsAsync()
        {
            try
            {
                // Load default curve presets;
                var defaultPresets = new[]
                {
                    CreateLinearPreset(),
                    CreateEaseInOutPreset(),
                    CreateBouncePreset(),
                    CreateElasticPreset(),
                    CreateOvershootPreset(),
                    CreateSmoothStepPreset()
                };

                foreach (var preset in defaultPresets)
                {
                    await RegisterPresetAsync(preset);
                }

                _logger.LogInformation("Default curve presets loaded");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load default presets: {ex.Message}");
            }
        }

        private async Task InitializeToolsAsync()
        {
            // Initialize tool-specific manipulators;
            await InitializeToolManipulatorsAsync();

            _logger.LogDebug("Curve editor tools initialized");
        }

        private async Task ProcessCurveCommandsAsync()
        {
            List<CurveCommand> commandsToProcess;
            lock (_curveLock)
            {
                commandsToProcess = new List<CurveCommand>();
                while (_commandQueue.Count > 0 && commandsToProcess.Count < _settings.MaxCommandsPerFrame)
                {
                    commandsToProcess.Add(_commandQueue.Dequeue());
                }
            }

            foreach (var command in commandsToProcess)
            {
                try
                {
                    await ExecuteCurveCommandAsync(command);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error executing curve command: {ex.Message}");
                }
            }

            _metrics.CommandsProcessed += commandsToProcess.Count;
        }

        private async Task ExecuteCurveCommandAsync(CurveCommand command)
        {
            switch (command.CommandType)
            {
                case CurveCommandType.CreateCurve:
                    await ExecuteCreateCurveCommandAsync(command);
                    break;
                case CurveCommandType.AddKeyframes:
                    await ExecuteAddKeyframesCommandAsync(command);
                    break;
                case CurveCommandType.RemoveKeyframes:
                    await ExecuteRemoveKeyframesCommandAsync(command);
                    break;
                case CurveCommandType.ModifyKeyframes:
                    await ExecuteModifyKeyframesCommandAsync(command);
                    break;
                case CurveCommandType.ApplyPreset:
                    await ExecuteApplyPresetCommandAsync(command);
                    break;
                case CurveCommandType.OptimizeCurve:
                    await ExecuteOptimizeCurveCommandAsync(command);
                    break;
                case CurveCommandType.SetSelection:
                    await ExecuteSetSelectionCommandAsync(command);
                    break;
                default:
                    _logger.LogWarning($"Unknown curve command type: {command.CommandType}");
                    break;
            }
        }

        private async Task UpdateActiveManipulatorsAsync(float deltaTime)
        {
            List<CurveManipulator> manipulatorsToUpdate;
            lock (_curveLock)
            {
                manipulatorsToUpdate = _activeManipulators.Values.ToList();
            }

            foreach (var manipulator in manipulatorsToUpdate)
            {
                try
                {
                    await manipulator.UpdateAsync(deltaTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error updating manipulator: {ex.Message}");
                }
            }
        }

        private async Task UpdateCurveAnalysisAsync(float deltaTime)
        {
            await _analysisEngine.UpdateAsync(deltaTime);
            await _optimizationEngine.UpdateAsync(deltaTime);
        }

        private async Task UpdatePerformanceMetricsAsync(float deltaTime)
        {
            _metrics.LoadedCurves = LoadedCurvesCount;
            _metrics.AvailablePresets = PresetCount;
            _metrics.ActiveManipulators = _activeManipulators.Count;
            _metrics.SessionDuration = (float)(DateTime.UtcNow - _sessionStartTime).TotalSeconds;

            await Task.CompletedTask;
        }

        private Keyframe ApplySnapping(Keyframe keyframe)
        {
            var snappedKeyframe = keyframe.Clone();

            if (_snapSettings.EnableTimeSnapping)
            {
                snappedKeyframe.Time = SnapValue(keyframe.Time, _snapSettings.TimeSnapInterval);
            }

            if (_snapSettings.EnableValueSnapping)
            {
                snappedKeyframe.Value = SnapValue(keyframe.Value, _snapSettings.ValueSnapInterval);
            }

            if (_snapSettings.EnableTangentSnapping)
            {
                snappedKeyframe.InTangent = SnapValue(keyframe.InTangent, _snapSettings.TangentSnapInterval);
                snappedKeyframe.OutTangent = SnapValue(keyframe.OutTangent, _snapSettings.TangentSnapInterval);
            }

            return snappedKeyframe;
        }

        private float SnapValue(float value, float interval)
        {
            if (interval <= 0) return value;
            return (float)Math.Round(value / interval) * interval;
        }

        private void ApplyKeyframeModification(Keyframe keyframe, KeyframeModification modification)
        {
            if (modification.NewTime.HasValue)
                keyframe.Time = modification.NewTime.Value;

            if (modification.NewValue.HasValue)
                keyframe.Value = modification.NewValue.Value;

            if (modification.NewInTangent.HasValue)
                keyframe.InTangent = modification.NewInTangent.Value;

            if (modification.NewOutTangent.HasValue)
                keyframe.OutTangent = modification.NewOutTangent.Value;

            if (modification.NewTangentMode.HasValue)
                keyframe.TangentMode = modification.NewTangentMode.Value;
        }

        private async Task InitializeToolAsync(CurveTool tool)
        {
            switch (tool)
            {
                case CurveTool.Select:
                    await InitializeSelectToolAsync();
                    break;
                case CurveTool.Move:
                    await InitializeMoveToolAsync();
                    break;
                case CurveTool.Scale:
                    await InitializeScaleToolAsync();
                    break;
                case CurveTool.Rotate:
                    await InitializeRotateToolAsync();
                    break;
                case CurveTool.Draw:
                    await InitializeDrawToolAsync();
                    break;
                case CurveTool.Erase:
                    await InitializeEraseToolAsync();
                    break;
            }
        }

        private async Task InitializeToolManipulatorsAsync()
        {
            // Initialize tool-specific manipulators;
            var manipulators = new[]
            {
                new SelectManipulator(_logger),
                new MoveManipulator(_logger),
                new ScaleManipulator(_logger),
                new DrawManipulator(_logger)
            };

            foreach (var manipulator in manipulators)
            {
                await manipulator.InitializeAsync(_settings);
                _activeManipulators[manipulator.ManipulatorId] = manipulator;
            }
        }

        private async Task UndoOperationAsync(CurveEditOperation operation)
        {
            switch (operation.OperationType)
            {
                case CurveEditOperationType.AddKeyframes:
                    await UndoAddKeyframesAsync(operation);
                    break;
                case CurveEditOperationType.RemoveKeyframes:
                    await UndoRemoveKeyframesAsync(operation);
                    break;
                case CurveEditOperationType.ModifyKeyframes:
                    await UndoModifyKeyframesAsync(operation);
                    break;
                case CurveEditOperationType.ApplyPreset:
                    await UndoApplyPresetAsync(operation);
                    break;
                case CurveEditOperationType.Optimize:
                    await UndoOptimizeAsync(operation);
                    break;
            }
        }

        private async Task UndoAddKeyframesAsync(CurveEditOperation operation)
        {
            var curve = await GetCurveAsync(operation.CurveId);
            foreach (var keyframe in operation.AffectedKeyframes)
            {
                curve.RemoveKeyframe(keyframe.KeyframeId);
            }
        }

        private async Task UndoRemoveKeyframesAsync(CurveEditOperation operation)
        {
            var curve = await GetCurveAsync(operation.CurveId);
            foreach (var keyframe in operation.AffectedKeyframes)
            {
                curve.AddKeyframe(keyframe);
            }
        }

        private async Task UndoModifyKeyframesAsync(CurveEditOperation operation)
        {
            // Implementation would require storing original keyframe states;
        }

        private async Task UndoApplyPresetAsync(CurveEditOperation operation)
        {
            // Implementation would require storing pre-preset state;
        }

        private async Task UndoOptimizeAsync(CurveEditOperation operation)
        {
            // Implementation would require storing pre-optimization state;
        }

        private void RecordEditOperation(CurveEditOperation operation)
        {
            lock (_curveLock)
            {
                _editHistory.Add(operation);

                // Maintain history size;
                if (_editHistory.Count > _settings.MaxHistorySize)
                {
                    _editHistory.RemoveAt(0);
                }
            }
        }

        // Command execution methods;
        private async Task ExecuteCreateCurveCommandAsync(CurveCommand command)
        {
            var creationParams = (CurveCreationParameters)command.Parameters["CreationParams"];
            await CreateCurveAsync(creationParams);
        }

        private async Task ExecuteAddKeyframesCommandAsync(CurveCommand command)
        {
            var curveId = command.Parameters["CurveId"] as string;
            var keyframes = (IEnumerable<Keyframe>)command.Parameters["Keyframes"];
            await AddKeyframesAsync(curveId, keyframes);
        }

        private async Task ExecuteRemoveKeyframesCommandAsync(CurveCommand command)
        {
            var curveId = command.Parameters["CurveId"] as string;
            var keyframeIds = (IEnumerable<string>)command.Parameters["KeyframeIds"];
            await RemoveKeyframesAsync(curveId, keyframeIds);
        }

        private async Task ExecuteModifyKeyframesCommandAsync(CurveCommand command)
        {
            var curveId = command.Parameters["CurveId"] as string;
            var modifications = (IEnumerable<KeyframeModification>)command.Parameters["Modifications"];
            await ModifyKeyframesAsync(curveId, modifications);
        }

        private async Task ExecuteApplyPresetCommandAsync(CurveCommand command)
        {
            var curveId = command.Parameters["CurveId"] as string;
            var presetId = command.Parameters["PresetId"] as string;
            var parameters = (PresetApplicationParameters)command.Parameters["Parameters"];
            await ApplyPresetAsync(curveId, presetId, parameters);
        }

        private async Task ExecuteOptimizeCurveCommandAsync(CurveCommand command)
        {
            var curveId = command.Parameters["CurveId"] as string;
            var parameters = (OptimizationParameters)command.Parameters["Parameters"];
            await OptimizeCurveAsync(curveId, parameters);
        }

        private async Task ExecuteSetSelectionCommandAsync(CurveCommand command)
        {
            var curveId = command.Parameters["CurveId"] as string;
            var selection = (CurveSelection)command.Parameters["Selection"];
            await SetSelectionAsync(curveId, selection);
        }

        // Preset creation methods;
        private CurvePreset CreateLinearPreset()
        {
            return new CurvePreset;
            {
                PresetId = "linear",
                PresetName = "Linear",
                PresetType = CurvePresetType.Interpolation,
                Description = "Linear interpolation between keyframes",
                Parameters = new Dictionary<string, object>
                {
                    ["InterpolationType"] = InterpolationType.Linear,
                    ["TangentMode"] = TangentMode.Linear;
                }
            };
        }

        private CurvePreset CreateEaseInOutPreset()
        {
            return new CurvePreset;
            {
                PresetId = "ease_in_out",
                PresetName = "Ease In Out",
                PresetType = CurvePresetType.Easing,
                Description = "Smooth acceleration and deceleration",
                Parameters = new Dictionary<string, object>
                {
                    ["EasingType"] = EasingType.Cubic,
                    ["EaseIn"] = true,
                    ["EaseOut"] = true;
                }
            };
        }

        private CurvePreset CreateBouncePreset()
        {
            return new CurvePreset;
            {
                PresetId = "bounce",
                PresetName = "Bounce",
                PresetType = CurvePresetType.Easing,
                Description = "Bouncing animation with decay",
                Parameters = new Dictionary<string, object>
                {
                    ["EasingType"] = EasingType.Bounce,
                    ["BounceCount"] = 3,
                    ["BounceIntensity"] = 0.8f;
                }
            };
        }

        private CurvePreset CreateElasticPreset()
        {
            return new CurvePreset;
            {
                PresetId = "elastic",
                PresetName = "Elastic",
                PresetType = CurvePresetType.Easing,
                Description = "Elastic spring-like animation",
                Parameters = new Dictionary<string, object>
                {
                    ["EasingType"] = EasingType.Elastic,
                    ["OscillationCount"] = 3,
                    ["Springiness"] = 0.7f;
                }
            };
        }

        // Additional tool initialization methods;
        private async Task InitializeSelectToolAsync()
        {
            await Task.CompletedTask;
        }

        private async Task InitializeMoveToolAsync()
        {
            await Task.CompletedTask;
        }

        private async Task InitializeScaleToolAsync()
        {
            await Task.CompletedTask;
        }

        private async Task InitializeRotateToolAsync()
        {
            await Task.CompletedTask;
        }

        private async Task InitializeDrawToolAsync()
        {
            await Task.CompletedTask;
        }

        private async Task InitializeEraseToolAsync()
        {
            await Task.CompletedTask;
        }

        // Validation methods;
        private async Task ValidateSystemState()
        {
            if (!_isInitialized)
                throw new CurveEditorException("CurveEditor is not initialized");

            if (_isDisposed)
                throw new CurveEditorException("CurveEditor is disposed");

            await Task.CompletedTask;
        }

        private async Task ValidateCreationParameters(CurveCreationParameters creationParams)
        {
            if (string.IsNullOrEmpty(creationParams.CurveName))
                throw new CurveEditorException("Curve name cannot be null or empty");

            if (creationParams.CurveType == CurveType.Unknown)
                throw new CurveEditorException("Curve type cannot be unknown");

            await Task.CompletedTask;
        }

        private async Task ValidateCurveData(CurveData curveData)
        {
            if (curveData.Keyframes == null)
                throw new CurveEditorException("Curve data keyframes cannot be null");

            if (curveData.CreationParameters == null)
                throw new CurveEditorException("Curve data creation parameters cannot be null");

            await Task.CompletedTask;
        }

        // Data access methods;
        private async Task<AnimationCurve> GetCurveAsync(string curveId)
        {
            lock (_curveLock)
            {
                if (_curves.TryGetValue(curveId, out var curve))
                {
                    return curve;
                }
            }

            throw new CurveEditorException($"Curve not found: {curveId}");
        }

        private async Task<CurvePreset> GetPresetAsync(string presetId)
        {
            lock (_curveLock)
            {
                if (_curvePresets.TryGetValue(presetId, out var preset))
                {
                    return preset;
                }
            }

            throw new CurveEditorException($"Preset not found: {presetId}");
        }

        private async Task<CurveSelection> GetSelectionAsync(string curveId)
        {
            lock (_curveLock)
            {
                if (_selections.TryGetValue(curveId, out var selection))
                {
                    return selection;
                }
            }

            return new CurveSelection { CurveId = curveId, Keyframes = new List<string>() };
        }

        private async Task RegisterPresetAsync(CurvePreset preset)
        {
            lock (_curveLock)
            {
                _curvePresets[preset.PresetId] = preset;
            }

            await Task.CompletedTask;
        }

        private string GenerateCurveId()
        {
            return $"Curve_{Guid.NewGuid():N}";
        }

        private async Task HandleCurveExceptionAsync(string context, Exception exception)
        {
            _logger.LogError($"{context}: {exception.Message}", exception);
            await _recoveryEngine.ExecuteRecoveryStrategyAsync("CurveEditor", exception);
        }

        // Event raising methods;
        private void RaiseCurveCreated(string curveId, CurveCreationParameters creationParams)
        {
            CurveCreated?.Invoke(this, new CurveCreatedEventArgs;
            {
                CurveId = curveId,
                CreationParams = creationParams,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCurveModified(string curveId, CurveModificationType modificationType)
        {
            CurveModified?.Invoke(this, new CurveModifiedEventArgs;
            {
                CurveId = curveId,
                ModificationType = modificationType,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseKeyframesAdded(string curveId, List<Keyframe> keyframes)
        {
            KeyframesAdded?.Invoke(this, new KeyframesAddedEventArgs;
            {
                CurveId = curveId,
                Keyframes = keyframes,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseKeyframesRemoved(string curveId, List<Keyframe> keyframes)
        {
            KeyframesRemoved?.Invoke(this, new KeyframesRemovedEventArgs;
            {
                CurveId = curveId,
                Keyframes = keyframes,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseKeyframesModified(string curveId, List<Keyframe> keyframes)
        {
            KeyframesModified?.Invoke(this, new KeyframesModifiedEventArgs;
            {
                CurveId = curveId,
                Keyframes = keyframes,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseSelectionChanged(string curveId, CurveSelection previousSelection, CurveSelection newSelection)
        {
            SelectionChanged?.Invoke(this, new SelectionChangedEventArgs;
            {
                CurveId = curveId,
                PreviousSelection = previousSelection,
                NewSelection = newSelection,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseStateChanged(CurveEditorState previousState, CurveEditorState newState)
        {
            StateChanged?.Invoke(this, new CurveEditorStateChangedEventArgs;
            {
                PreviousState = previousState,
                NewState = newState,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCurveOperationPerformed(string curveId, CurveEditOperation operation)
        {
            CurveOperationPerformed?.Invoke(this, new CurveOperationEventArgs;
            {
                CurveId = curveId,
                Operation = operation,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCurveAnalysisCompleted(string curveId, CurveAnalysisResult result)
        {
            CurveAnalysisCompleted?.Invoke(this, new CurveAnalysisCompletedEventArgs;
            {
                CurveId = curveId,
                Result = result,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void RaiseCurveOptimizationApplied(string curveId, CurveOptimizationResult result)
        {
            CurveOptimizationApplied?.Invoke(this, new CurveOptimizationAppliedEventArgs;
            {
                CurveId = curveId,
                Result = result,
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
                    _curves.Clear();
                    _curvePresets.Clear();
                    _activeManipulators.Clear();
                    _selections.Clear();
                    _editHistory.Clear();
                    _commandQueue.Clear();

                    _analysisEngine?.Dispose();
                    _optimizationEngine?.Dispose();
                    _presetManager?.Dispose();

                    foreach (var manipulator in _activeManipulators.Values)
                    {
                        manipulator?.Dispose();
                    }
                }

                _isDisposed = true;
            }
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    public enum CurveEditorState;
    {
        Inactive,
        Ready,
        Active,
        Paused,
        Error;
    }

    public enum CurveTool;
    {
        Select,
        Move,
        Scale,
        Rotate,
        Draw,
        Erase,
        Smooth,
        BreakTangents,
        UnifyTangents,
        Freeze;
    }

    public enum CurveType;
    {
        Unknown,
        PositionX,
        PositionY,
        PositionZ,
        RotationX,
        RotationY,
        RotationZ,
        ScaleX,
        ScaleY,
        ScaleZ,
        Float,
        ColorR,
        ColorG,
        ColorB,
        ColorA,
        Custom;
    }

    public enum InterpolationType;
    {
        Constant,
        Linear,
        Bezier,
        Hermite,
        CatmullRom,
        Bounce,
        Elastic,
        Back,
        Custom;
    }

    public enum TangentMode;
    {
        Auto,
        Free,
        Linear,
        Constant,
        Clamped,
        Broken;
    }

    public enum WrapMode;
    {
        Clamp,
        Loop,
        PingPong,
        Continue,
        ClampForever;
    }

    public enum CurvePresetType;
    {
        Interpolation,
        Easing,
        Shape,
        Custom;
    }

    public enum EasingType;
    {
        Linear,
        Quadratic,
        Cubic,
        Quartic,
        Quintic,
        Sinusoidal,
        Exponential,
        Circular,
        Elastic,
        Back,
        Bounce;
    }

    public enum CurveEditOperationType;
    {
        AddKeyframes,
        RemoveKeyframes,
        ModifyKeyframes,
        ApplyPreset,
        Optimize,
        Scale,
        Offset,
        Normalize;
    }

    public enum CurveModificationType;
    {
        KeyframesAdded,
        KeyframesRemoved,
        KeyframesModified,
        PresetApplied,
        Optimized,
        Scaled,
        Offset,
        Normalized;
    }

    public enum CurveCommandType;
    {
        CreateCurve,
        AddKeyframes,
        RemoveKeyframes,
        ModifyKeyframes,
        ApplyPreset,
        OptimizeCurve,
        SetSelection,
        ClearSelection,
        SetTool;
    }

    public enum ExportFormat;
    {
        JSON,
        XML,
        Binary,
        CSV,
        Custom;
    }

    public class CurveEditorSettings;
    {
        public float DefaultCurveResolution { get; set; } = 0.01f;
        public int MaxHistorySize { get; set; } = 100;
        public int MaxCommandsPerFrame { get; set; } = 50;
        public bool AutoOptimizeCurves { get; set; } = true;
        public bool EnableRealTimeAnalysis { get; set; } = true;
        public float AnalysisUpdateInterval { get; set; } = 0.1f;
        public bool ShowTangentHandles { get; set; } = true;
        public bool ShowControlPoints { get; set; } = true;
        public float CurveSmoothingThreshold { get; set; } = 0.1f;
        public int MaxKeyframesPerCurve { get; set; } = 1000;
    }

    public class CurveEditorMetrics;
    {
        public int CurvesCreated { get; set; }
        public int CurvesLoaded { get; set; }
        public int CurvesSaved { get; set; }
        public int CurvesExported { get; set; }
        public int KeyframesAdded { get; set; }
        public int KeyframesRemoved { get; set; }
        public int KeyframesModified { get; set; }
        public int SelectionsChanged { get; set; }
        public int PresetsApplied { get; set; }
        public int CurvesAnalyzed { get; set; }
        public int CurvesOptimized { get; set; }
        public int ToolChanges { get; set; }
        public int SnapSettingsUpdated { get; set; }
        public int VisualSettingsUpdated { get; set; }
        public int UndoOperations { get; set; }
        public int RedoOperations { get; set; }
        public int CommandsQueued { get; set; }
        public int CommandsProcessed { get; set; }
        public int CurveEvaluations { get; set; }
        public int LoadedCurves { get; set; }
        public int AvailablePresets { get; set; }
        public int ActiveManipulators { get; set; }
        public float SessionDuration { get; set; }
        public float LastUpdateDuration { get; set; }
    }

    public class CurveSnapSettings;
    {
        public bool EnableTimeSnapping { get; set; } = true;
        public bool EnableValueSnapping { get; set; } = false;
        public bool EnableTangentSnapping { get; set; } = false;
        public float TimeSnapInterval { get; set; } = 0.1f;
        public float ValueSnapInterval { get; set; } = 0.1f;
        public float TangentSnapInterval { get; set; } = 0.1f;
        public bool SnapToGrid { get; set; } = true;
        public bool SnapToKeyframes { get; set; } = true;

        public CurveSnapSettings Clone()
        {
            return (CurveSnapSettings)this.MemberwiseClone();
        }
    }

    public class CurveVisualSettings;
    {
        public float CurveWidth { get; set; } = 2.0f;
        public float KeyframeSize { get; set; } = 6.0f;
        public float TangentHandleSize { get; set; } = 4.0f;
        public Color CurveColor { get; set; } = new Color(0.2f, 0.6f, 1.0f, 1.0f);
        public Color KeyframeColor { get; set; } = new Color(1.0f, 0.8f, 0.2f, 1.0f);
        public Color SelectedKeyframeColor { get; set; } = new Color(1.0f, 0.2f, 0.2f, 1.0f);
        public Color TangentColor { get; set; } = new Color(0.8f, 0.8f, 0.8f, 1.0f);
        public Color GridColor { get; set; } = new Color(0.3f, 0.3f, 0.3f, 1.0f);
        public bool ShowGrid { get; set; } = true;
        public bool ShowAxes { get; set; } = true;
        public bool ShowLabels { get; set; } = true;

        public CurveVisualSettings Clone()
        {
            return (CurveVisualSettings)this.MemberwiseClone();
        }
    }

    public class AnimationCurve;
    {
        public string CurveId { get; }
        public CurveCreationParameters CreationParameters { get; }
        public CurveType CurveType { get; set; }
        public WrapMode PreWrapMode { get; set; }
        public WrapMode PostWrapMode { get; set; }
        public Color Color { get; set; }
        public DateTime CreationTime { get; set; }
        public List<Keyframe> Keyframes { get; private set; }

        public AnimationCurve(string curveId, CurveCreationParameters creationParameters)
        {
            CurveId = curveId;
            CreationParameters = creationParameters;
            CurveType = creationParameters.CurveType;
            CreationTime = DateTime.UtcNow;
            Keyframes = new List<Keyframe>();
        }

        public void AddKeyframe(Keyframe keyframe)
        {
            // Insert keyframe in sorted order by time;
            int index = 0;
            while (index < Keyframes.Count && Keyframes[index].Time < keyframe.Time)
            {
                index++;
            }
            Keyframes.Insert(index, keyframe);
        }

        public void RemoveKeyframe(string keyframeId)
        {
            Keyframes.RemoveAll(k => k.KeyframeId == keyframeId);
        }

        public void UpdateKeyframe(string keyframeId, Keyframe newKeyframe)
        {
            var index = Keyframes.FindIndex(k => k.KeyframeId == keyframeId);
            if (index >= 0)
            {
                Keyframes[index] = newKeyframe;
                // Re-sort if time changed;
                if (Math.Abs(Keyframes[index].Time - newKeyframe.Time) > float.Epsilon)
                {
                    var keyframe = Keyframes[index];
                    Keyframes.RemoveAt(index);
                    AddKeyframe(keyframe);
                }
            }
        }

        public Keyframe GetKeyframe(string keyframeId)
        {
            return Keyframes.FirstOrDefault(k => k.KeyframeId == keyframeId);
        }

        public bool CanAddKeyframe(Keyframe keyframe)
        {
            // Check for duplicate time (within tolerance)
            return !Keyframes.Any(k => Math.Abs(k.Time - keyframe.Time) < float.Epsilon);
        }

        public float Evaluate(float time)
        {
            if (Keyframes.Count == 0) return 0.0f;
            if (Keyframes.Count == 1) return Keyframes[0].Value;

            // Handle wrapping;
            time = ApplyWrapMode(time);

            // Find surrounding keyframes;
            int beforeIndex = 0;
            int afterIndex = Keyframes.Count - 1;

            for (int i = 0; i < Keyframes.Count; i++)
            {
                if (Keyframes[i].Time <= time)
                {
                    beforeIndex = i;
                }
                if (Keyframes[i].Time >= time)
                {
                    afterIndex = i;
                    break;
                }
            }

            if (beforeIndex == afterIndex)
            {
                return Keyframes[beforeIndex].Value;
            }

            var before = Keyframes[beforeIndex];
            var after = Keyframes[afterIndex];

            // Interpolate between keyframes;
            float t = (time - before.Time) / (after.Time - before.Time);
            return Interpolate(before, after, t);
        }

        public TimeRange GetTimeRange()
        {
            if (Keyframes.Count == 0) return new TimeRange(0, 0);
            return new TimeRange(Keyframes.First().Time, Keyframes.Last().Time);
        }

        public ValueRange GetValueRange()
        {
            if (Keyframes.Count == 0) return new ValueRange(0, 0);
            float min = Keyframes.Min(k => k.Value);
            float max = Keyframes.Max(k => k.Value);
            return new ValueRange(min, max);
        }

        public bool IsConstant()
        {
            if (Keyframes.Count <= 1) return true;
            var firstValue = Keyframes[0].Value;
            return Keyframes.All(k => Math.Abs(k.Value - firstValue) < float.Epsilon);
        }

        public bool IsLinear()
        {
            if (Keyframes.Count <= 2) return true;

            // Check if all segments are linear;
            for (int i = 1; i < Keyframes.Count; i++)
            {
                if (Keyframes[i].TangentMode != TangentMode.Linear &&
                    Keyframes[i - 1].TangentMode != TangentMode.Linear)
                {
                    return false;
                }
            }
            return true;
        }

        private float ApplyWrapMode(float time)
        {
            if (Keyframes.Count == 0) return time;

            float startTime = Keyframes.First().Time;
            float endTime = Keyframes.Last().Time;
            float duration = endTime - startTime;

            if (duration <= 0) return time;

            if (time < startTime)
            {
                switch (PreWrapMode)
                {
                    case WrapMode.Clamp:
                        return startTime;
                    case WrapMode.Loop:
                        return startTime + ((time - startTime) % duration + duration) % duration;
                    case WrapMode.PingPong:
                        float normalizedTime = ((time - startTime) % (2 * duration) + (2 * duration)) % (2 * duration);
                        return startTime + (normalizedTime > duration ? 2 * duration - normalizedTime : normalizedTime);
                    case WrapMode.Continue:
                        return time;
                    case WrapMode.ClampForever:
                        return startTime;
                }
            }
            else if (time > endTime)
            {
                switch (PostWrapMode)
                {
                    case WrapMode.Clamp:
                        return endTime;
                    case WrapMode.Loop:
                        return startTime + ((time - startTime) % duration + duration) % duration;
                    case WrapMode.PingPong:
                        float normalizedTime = ((time - startTime) % (2 * duration) + (2 * duration)) % (2 * duration);
                        return startTime + (normalizedTime > duration ? 2 * duration - normalizedTime : normalizedTime);
                    case WrapMode.Continue:
                        return time;
                    case WrapMode.ClampForever:
                        return endTime;
                }
            }

            return time;
        }

        private float Interpolate(Keyframe before, Keyframe after, float t)
        {
            switch (before.TangentMode)
            {
                case TangentMode.Constant:
                    return before.Value;
                case TangentMode.Linear:
                    return before.Value + (after.Value - before.Value) * t;
                case TangentMode.Bezier:
                    return BezierInterpolate(before, after, t);
                default:
                    return LinearInterpolate(before, after, t);
            }
        }

        private float BezierInterpolate(Keyframe before, Keyframe after, float t)
        {
            // Cubic Bezier interpolation;
            float u = 1 - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            float p0 = before.Value;
            float p1 = before.Value + before.OutTangent / 3.0f;
            float p2 = after.Value - after.InTangent / 3.0f;
            float p3 = after.Value;

            return uuu * p0 + 3 * uu * t * p1 + 3 * u * tt * p2 + ttt * p3;
        }

        private float LinearInterpolate(Keyframe before, Keyframe after, float t)
        {
            return before.Value + (after.Value - before.Value) * t;
        }
    }

    public class Keyframe;
    {
        public string KeyframeId { get; set; }
        public float Time { get; set; }
        public float Value { get; set; }
        public float InTangent { get; set; }
        public float OutTangent { get; set; }
        public TangentMode TangentMode { get; set; }
        public float Weight { get; set; } = 1.0f;
        public Dictionary<string, object> CustomData { get; set; }

        public Keyframe()
        {
            KeyframeId = Guid.NewGuid().ToString();
        }

        public Keyframe(float time, float value) : this()
        {
            Time = time;
            Value = value;
        }

        public Keyframe(KeyframeData data) : this()
        {
            Time = data.Time;
            Value = data.Value;
            InTangent = data.InTangent;
            OutTangent = data.OutTangent;
            TangentMode = data.TangentMode;
            Weight = data.Weight;
        }

        public Keyframe Clone()
        {
            return new Keyframe;
            {
                KeyframeId = this.KeyframeId,
                Time = this.Time,
                Value = this.Value,
                InTangent = this.InTangent,
                OutTangent = this.OutTangent,
                TangentMode = this.TangentMode,
                Weight = this.Weight,
                CustomData = this.CustomData != null ? new Dictionary<string, object>(this.CustomData) : null;
            };
        }

        public KeyframeData ToKeyframeData()
        {
            return new KeyframeData;
            {
                Time = Time,
                Value = Value,
                InTangent = InTangent,
                OutTangent = OutTangent,
                TangentMode = TangentMode,
                Weight = Weight;
            };
        }
    }

    public class CurveCreationParameters;
    {
        public string CurveName { get; set; }
        public CurveType CurveType { get; set; }
        public WrapMode PreWrapMode { get; set; } = WrapMode.Clamp;
        public WrapMode PostWrapMode { get; set; } = WrapMode.Clamp;
        public List<Keyframe> InitialKeyframes { get; set; }
        public Dictionary<string, object> AdditionalParameters { get; set; }

        public CurveCreationParameters Clone()
        {
            return new CurveCreationParameters;
            {
                CurveName = this.CurveName,
                CurveType = this.CurveType,
                PreWrapMode = this.PreWrapMode,
                PostWrapMode = this.PostWrapMode,
                InitialKeyframes = this.InitialKeyframes?.Select(k => k.Clone()).ToList(),
                AdditionalParameters = this.AdditionalParameters != null ?
                    new Dictionary<string, object>(this.AdditionalParameters) : null;
            };
        }
    }

    public class CurveSelection;
    {
        public string CurveId { get; set; }
        public List<string> Keyframes { get; set; } = new List<string>();
        public SelectionMode Mode { get; set; } = SelectionMode.Keyframes;
    }

    public class CurveSelectionInfo;
    {
        public int TotalSelections { get; set; }
        public int SelectedKeyframes { get; set; }
        public int SelectedCurves { get; set; }
    }

    public class CurvePreset;
    {
        public string PresetId { get; set; }
        public string PresetName { get; set; }
        public CurvePresetType PresetType { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    // Additional supporting classes and internal implementations would continue here...
    // [The remaining classes would follow the same comprehensive pattern]

    #endregion;

    // Interfaces;
    public interface ICurveEditor;
    {
        Task InitializeAsync();
        Task UpdateAsync(float deltaTime);
        Task<string> CreateCurveAsync(CurveCreationParameters creationParams);
        Task<string> LoadCurveAsync(CurveData curveData);
        Task<CurveData> SaveCurveAsync(string curveId);
        Task AddKeyframesAsync(string curveId, IEnumerable<Keyframe> keyframes);
        Task RemoveKeyframesAsync(string curveId, IEnumerable<string> keyframeIds);
        Task ModifyKeyframesAsync(string curveId, IEnumerable<KeyframeModification> modifications);
        Task SetSelectionAsync(string curveId, CurveSelection selection);
        Task ClearSelectionAsync(string curveId);
        Task ApplyPresetAsync(string curveId, string presetId, PresetApplicationParameters parameters);
        Task<CurveAnalysisResult> AnalyzeCurveAsync(string curveId, AnalysisParameters parameters);
        Task OptimizeCurveAsync(string curveId, OptimizationParameters parameters);
        Task SetActiveToolAsync(CurveTool tool);
        Task UpdateSnapSettingsAsync(CurveSnapSettings settings);
        Task UpdateVisualSettingsAsync(CurveVisualSettings settings);
        Task UndoAsync();
        Task RedoAsync();
        Task<CurveExportData> ExportCurveAsync(string curveId, ExportParameters parameters);
        Task<float> EvaluateCurveAsync(string curveId, float time);
        Task<CurveInfo> GetCurveInfoAsync(string curveId);

        CurveEditorState CurrentState { get; }
        CurveTool ActiveTool { get; }
        CurveSnapSettings SnapSettings { get; }
        CurveVisualSettings VisualSettings { get; }
        bool IsInitialized { get; }
        CurveSelectionInfo SelectionInfo { get; }
    }

    public interface IKeyframeManager;
    {
        // Keyframe management interface;
    }

    public interface IInterpolationEngine;
    {
        // Interpolation engine interface;
    }
}
