using NEDA.Animation.Common.Enums;
using NEDA.Animation.Common.Models;
using NEDA.Animation.Exceptions;
using NEDA.Animation.Interfaces;
using NEDA.Animation.SequenceEditor.CameraAnimation;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NEDA.Animation.SequenceEditor.KeyframeEditing;
{
    /// <summary>
    /// Professional keyframe editor with visual curve editing, bezier controls,
    /// and real-time animation preview capabilities;
    /// </summary>
    public class KeyframeEditor : IKeyframeEditor, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly IKeyframeDataService _dataService;
        private readonly IAnimationCurveManager _curveManager;
        private readonly List<Keyframe> _selectedKeyframes;
        private readonly object _lockObject = new object();
        private bool _disposed = false;
        private bool _isEditMode = false;
        #endregion;

        #region Public Properties;
        public string EditorName { get; private set; }
        public KeyframeEditorState CurrentState { get; private set; }
        public EditorVisualSettings VisualSettings { get; private set; }
        public KeyframeSelection Selection => new KeyframeSelection(_selectedKeyframes);
        public bool IsEditModeActive => _isEditMode;
        public event EventHandler<KeyframeEditorEventArgs> EditorStateChanged;
        public event EventHandler<KeyframeSelectionChangedEventArgs> SelectionChanged;
        public event EventHandler<AnimationCurveUpdatedEventArgs> CurveUpdated;
        #endregion;

        #region Constructors;
        public KeyframeEditor(ILogger logger, IKeyframeDataService dataService, IAnimationCurveManager curveManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
            _curveManager = curveManager ?? throw new ArgumentNullException(nameof(curveManager));
            _selectedKeyframes = new List<Keyframe>();

            InitializeEditor();
        }

        public KeyframeEditor(ILogger logger)
            : this(logger, new DefaultKeyframeDataService(), new DefaultAnimationCurveManager())
        {
        }
        #endregion;

        #region Initialization;
        private void InitializeEditor()
        {
            EditorName = "Professional Keyframe Editor";
            CurrentState = KeyframeEditorState.Ready;

            VisualSettings = new EditorVisualSettings;
            {
                ShowTangentHandles = true,
                ShowCurvePreview = true,
                SnapToGrid = true,
                GridSize = 0.1f,
                HandleSize = 6.0f,
                CurveQuality = CurveQuality.High,
                TimeFormat = TimeFormat.Seconds,
                ValuePrecision = 3;
            };

            _logger.LogInformation($"KeyframeEditor initialized: {EditorName}");
        }
        #endregion;

        #region Keyframe Selection Management;
        public void SelectKeyframe(Keyframe keyframe, bool addToSelection = false)
        {
            ValidateNotDisposed();
            if (keyframe == null) throw new ArgumentNullException(nameof(keyframe));

            lock (_lockObject)
            {
                if (!addToSelection)
                {
                    ClearSelection();
                }

                if (!_selectedKeyframes.Contains(keyframe))
                {
                    _selectedKeyframes.Add(keyframe);
                    _logger.LogDebug($"Keyframe selected: {keyframe.Id}");

                    OnSelectionChanged(new KeyframeSelectionChangedEventArgs;
                    {
                        Action = SelectionAction.Add,
                        Keyframes = new[] { keyframe },
                        TotalSelected = _selectedKeyframes.Count;
                    });
                }
            }
        }

        public void SelectKeyframes(IEnumerable<Keyframe> keyframes, bool addToSelection = false)
        {
            ValidateNotDisposed();
            if (keyframes == null) throw new ArgumentNullException(nameof(keyframes));

            var keyframeList = keyframes.ToList();
            if (!keyframeList.Any()) return;

            lock (_lockObject)
            {
                if (!addToSelection)
                {
                    ClearSelection();
                }

                var newlyAdded = new List<Keyframe>();
                foreach (var keyframe in keyframeList)
                {
                    if (keyframe != null && !_selectedKeyframes.Contains(keyframe))
                    {
                        _selectedKeyframes.Add(keyframe);
                        newlyAdded.Add(keyframe);
                    }
                }

                if (newlyAdded.Any())
                {
                    _logger.LogDebug($"{newlyAdded.Count} keyframes selected");

                    OnSelectionChanged(new KeyframeSelectionChangedEventArgs;
                    {
                        Action = SelectionAction.Add,
                        Keyframes = newlyAdded,
                        TotalSelected = _selectedKeyframes.Count;
                    });
                }
            }
        }

        public void ClearSelection()
        {
            ValidateNotDisposed();

            lock (_lockObject)
            {
                if (_selectedKeyframes.Any())
                {
                    var previousSelection = _selectedKeyframes.ToList();
                    _selectedKeyframes.Clear();

                    _logger.LogDebug("Selection cleared");

                    OnSelectionChanged(new KeyframeSelectionChangedEventArgs;
                    {
                        Action = SelectionAction.Clear,
                        PreviousKeyframes = previousSelection,
                        TotalSelected = 0;
                    });
                }
            }
        }

        public void DeselectKeyframe(Keyframe keyframe)
        {
            ValidateNotDisposed();
            if (keyframe == null) throw new ArgumentNullException(nameof(keyframe));

            lock (_lockObject)
            {
                if (_selectedKeyframes.Remove(keyframe))
                {
                    _logger.LogDebug($"Keyframe deselected: {keyframe.Id}");

                    OnSelectionChanged(new KeyframeSelectionChangedEventArgs;
                    {
                        Action = SelectionAction.Remove,
                        Keyframes = new[] { keyframe },
                        TotalSelected = _selectedKeyframes.Count;
                    });
                }
            }
        }

        public bool IsKeyframeSelected(Keyframe keyframe)
        {
            ValidateNotDisposed();
            return _selectedKeyframes.Contains(keyframe);
        }
        #endregion;

        #region Keyframe Editing Operations;
        public KeyframeEditResult MoveSelectedKeyframes(double timeDelta, float valueDelta)
        {
            ValidateNotDisposed();
            ValidateEditMode();

            lock (_lockObject)
            {
                if (!_selectedKeyframes.Any())
                {
                    return KeyframeEditResult.CreateFailure("No keyframes selected");
                }

                try
                {
                    var movedKeyframes = new List<Keyframe>();
                    var originalStates = new Dictionary<Guid, Keyframe>();

                    // Store original states;
                    foreach (var keyframe in _selectedKeyframes)
                    {
                        originalStates[keyframe.Id] = keyframe.Clone();
                    }

                    // Apply movement;
                    foreach (var keyframe in _selectedKeyframes)
                    {
                        var newTime = keyframe.Time + timeDelta;
                        var newValue = keyframe.Value + valueDelta;

                        // Apply grid snapping if enabled;
                        if (VisualSettings.SnapToGrid)
                        {
                            newTime = Math.Round(newTime / VisualSettings.GridSize) * VisualSettings.GridSize;
                            newValue = (float)(Math.Round(newValue / VisualSettings.GridSize) * VisualSettings.GridSize);
                        }

                        var updateParams = new KeyframeUpdateParameters;
                        {
                            Time = newTime,
                            Value = newValue;
                        };

                        var updated = _dataService.UpdateKeyframe(keyframe.Id, updateParams);
                        if (updated != null)
                        {
                            movedKeyframes.Add(updated);
                        }
                    }

                    if (movedKeyframes.Any())
                    {
                        _logger.LogInformation($"Moved {movedKeyframes.Count} keyframes - TimeΔ: {timeDelta}, ValueΔ: {valueDelta}");

                        OnCurveUpdated(new AnimationCurveUpdatedEventArgs;
                        {
                            UpdatedKeyframes = movedKeyframes,
                            UpdateType = CurveUpdateType.KeyframesMoved,
                            AffectedProperties = movedKeyframes.Select(k => k.PropertyName).Distinct().ToList()
                        });

                        return KeyframeEditResult.CreateSuccess(movedKeyframes);
                    }

                    return KeyframeEditResult.CreateFailure("Failed to move keyframes");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move selected keyframes");
                    return KeyframeEditResult.CreateFailure($"Move operation failed: {ex.Message}");
                }
            }
        }

        public KeyframeEditResult ScaleSelectedKeyframes(double timeScale, float valueScale, double pivotTime, float pivotValue)
        {
            ValidateNotDisposed();
            ValidateEditMode();

            lock (_lockObject)
            {
                if (!_selectedKeyframes.Any())
                {
                    return KeyframeEditResult.CreateFailure("No keyframes selected");
                }

                try
                {
                    var scaledKeyframes = new List<Keyframe>();

                    foreach (var keyframe in _selectedKeyframes)
                    {
                        var newTime = pivotTime + (keyframe.Time - pivotTime) * timeScale;
                        var newValue = pivotValue + (keyframe.Value - pivotValue) * valueScale;

                        var updateParams = new KeyframeUpdateParameters;
                        {
                            Time = newTime,
                            Value = newValue;
                        };

                        var updated = _dataService.UpdateKeyframe(keyframe.Id, updateParams);
                        if (updated != null)
                        {
                            scaledKeyframes.Add(updated);
                        }
                    }

                    if (scaledKeyframes.Any())
                    {
                        _logger.LogInformation($"Scaled {scaledKeyframes.Count} keyframes - TimeScale: {timeScale}, ValueScale: {valueScale}");

                        OnCurveUpdated(new AnimationCurveUpdatedEventArgs;
                        {
                            UpdatedKeyframes = scaledKeyframes,
                            UpdateType = CurveUpdateType.KeyframesScaled,
                            AffectedProperties = scaledKeyframes.Select(k => k.PropertyName).Distinct().ToList()
                        });

                        return KeyframeEditResult.CreateSuccess(scaledKeyframes);
                    }

                    return KeyframeEditResult.CreateFailure("Failed to scale keyframes");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scale selected keyframes");
                    return KeyframeEditResult.CreateFailure($"Scale operation failed: {ex.Message}");
                }
            }
        }

        public KeyframeEditResult SetTangentsForSelection(TangentType tangentInType, TangentType tangentOutType)
        {
            ValidateNotDisposed();
            ValidateEditMode();

            lock (_lockObject)
            {
                if (!_selectedKeyframes.Any())
                {
                    return KeyframeEditResult.CreateFailure("No keyframes selected");
                }

                try
                {
                    var updatedKeyframes = new List<Keyframe>();

                    foreach (var keyframe in _selectedKeyframes)
                    {
                        var tangentIn = keyframe.TangentIn;
                        var tangentOut = keyframe.TangentOut;

                        tangentIn.Type = tangentInType;
                        tangentOut.Type = tangentOutType;

                        var updateParams = new KeyframeUpdateParameters;
                        {
                            TangentIn = tangentIn,
                            TangentOut = tangentOut;
                        };

                        var updated = _dataService.UpdateKeyframe(keyframe.Id, updateParams);
                        if (updated != null)
                        {
                            updatedKeyframes.Add(updated);
                        }
                    }

                    if (updatedKeyframes.Any())
                    {
                        _logger.LogInformation($"Updated tangents for {updatedKeyframes.Count} keyframes");

                        OnCurveUpdated(new AnimationCurveUpdatedEventArgs;
                        {
                            UpdatedKeyframes = updatedKeyframes,
                            UpdateType = CurveUpdateType.TangentsModified,
                            AffectedProperties = updatedKeyframes.Select(k => k.PropertyName).Distinct().ToList()
                        });

                        return KeyframeEditResult.CreateSuccess(updatedKeyframes);
                    }

                    return KeyframeEditResult.CreateFailure("Failed to update tangents");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to set tangents for selection");
                    return KeyframeEditResult.CreateFailure($"Tangent operation failed: {ex.Message}");
                }
            }
        }

        public KeyframeEditResult ApplyEasingToSelection(EasingFunction easingFunction)
        {
            ValidateNotDisposed();
            ValidateEditMode();

            lock (_lockObject)
            {
                if (!_selectedKeyframes.Any())
                {
                    return KeyframeEditResult.CreateFailure("No keyframes selected");
                }

                try
                {
                    var updatedKeyframes = new List<Keyframe>();

                    foreach (var keyframe in _selectedKeyframes)
                    {
                        var updateParams = new KeyframeUpdateParameters;
                        {
                            EasingFunction = easingFunction;
                        };

                        var updated = _dataService.UpdateKeyframe(keyframe.Id, updateParams);
                        if (updated != null)
                        {
                            updatedKeyframes.Add(updated);
                        }
                    }

                    if (updatedKeyframes.Any())
                    {
                        _logger.LogInformation($"Applied {easingFunction} easing to {updatedKeyframes.Count} keyframes");

                        OnCurveUpdated(new AnimationCurveUpdatedEventArgs;
                        {
                            UpdatedKeyframes = updatedKeyframes,
                            UpdateType = CurveUpdateType.EasingModified,
                            AffectedProperties = updatedKeyframes.Select(k => k.PropertyName).Distinct().ToList()
                        });

                        return KeyframeEditResult.CreateSuccess(updatedKeyframes);
                    }

                    return KeyframeEditResult.CreateFailure("Failed to apply easing");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply easing to selection");
                    return KeyframeEditResult.CreateFailure($"Easing operation failed: {ex.Message}");
                }
            }
        }
        #endregion;

        #region Editor State Management;
        public void BeginEditSession(string sessionName = null)
        {
            ValidateNotDisposed();

            if (_isEditMode)
            {
                throw new KeyframeEditorException("Edit session already active");
            }

            _isEditMode = true;
            var previousState = CurrentState;
            CurrentState = KeyframeEditorState.Editing;

            _logger.LogInformation($"Edit session started: {sessionName ?? "Unnamed"}");

            OnEditorStateChanged(new KeyframeEditorEventArgs;
            {
                EventType = EditorEventType.EditSessionStarted,
                PreviousState = previousState,
                CurrentState = CurrentState,
                SessionName = sessionName;
            });
        }

        public void EndEditSession(bool commitChanges = true)
        {
            ValidateNotDisposed();

            if (!_isEditMode)
            {
                throw new KeyframeEditorException("No active edit session");
            }

            var previousState = CurrentState;
            _isEditMode = false;
            CurrentState = KeyframeEditorState.Ready;

            _logger.LogInformation($"Edit session ended - Commit: {commitChanges}");

            OnEditorStateChanged(new KeyframeEditorEventArgs;
            {
                EventType = EditorEventType.EditSessionEnded,
                PreviousState = previousState,
                CurrentState = CurrentState,
                CommitChanges = commitChanges;
            });
        }

        public void SetEditorState(KeyframeEditorState newState)
        {
            ValidateNotDisposed();

            var previousState = CurrentState;
            CurrentState = newState;

            _logger.LogDebug($"Editor state changed: {previousState} -> {newState}");

            OnEditorStateChanged(new KeyframeEditorEventArgs;
            {
                EventType = EditorEventType.StateChanged,
                PreviousState = previousState,
                CurrentState = CurrentState;
            });
        }

        public void UpdateVisualSettings(EditorVisualSettings newSettings)
        {
            ValidateNotDisposed();
            if (newSettings == null) throw new ArgumentNullException(nameof(newSettings));

            var oldSettings = VisualSettings;
            VisualSettings = newSettings;

            _logger.LogDebug("Visual settings updated");

            OnEditorStateChanged(new KeyframeEditorEventArgs;
            {
                EventType = EditorEventType.VisualSettingsChanged,
                PreviousVisualSettings = oldSettings,
                CurrentVisualSettings = VisualSettings;
            });
        }
        #endregion;

        #region Curve Analysis and Visualization;
        public CurveAnalysisResult AnalyzeCurve(string propertyName, double startTime, double endTime)
        {
            ValidateNotDisposed();
            ValidatePropertyName(propertyName);
            ValidateTimeRange(startTime, endTime);

            try
            {
                var keyframes = _dataService.GetKeyframesInRange(startTime, endTime, propertyName);
                var curvePoints = _curveManager.GenerateCurvePoints(propertyName, startTime, endTime, 1000);

                if (!curvePoints.Any())
                {
                    return CurveAnalysisResult.CreateEmpty(propertyName);
                }

                var analysis = new CurveAnalysisResult;
                {
                    PropertyName = propertyName,
                    KeyframeCount = keyframes.Count,
                    SampleCount = curvePoints.Count,
                    TimeRange = new TimeRange(startTime, endTime),
                    ValueRange = CalculateValueRange(curvePoints),
                    AverageVelocity = CalculateAverageVelocity(curvePoints),
                    MaxVelocity = CalculateMaxVelocity(curvePoints),
                    IsConstant = CheckIfConstant(curvePoints),
                    HasDiscontinuities = CheckForDiscontinuities(curvePoints)
                };

                _logger.LogDebug($"Curve analysis completed for {propertyName}");
                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to analyze curve for property: {propertyName}");
                throw new KeyframeEditorException($"Curve analysis failed: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<CurvePoint> GenerateCurvePreview(string propertyName, double startTime, double endTime, int resolution = 500)
        {
            ValidateNotDisposed();
            ValidatePropertyName(propertyName);
            ValidateTimeRange(startTime, endTime);

            if (resolution < 10 || resolution > 10000)
            {
                throw new ArgumentException("Resolution must be between 10 and 10000", nameof(resolution));
            }

            try
            {
                var points = _curveManager.GenerateCurvePoints(propertyName, startTime, endTime, resolution);
                return points.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate curve preview for {propertyName}");
                throw new KeyframeEditorException($"Curve preview generation failed: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Utility Methods;
        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KeyframeEditor));
        }

        private void ValidateEditMode()
        {
            if (!_isEditMode)
                throw new KeyframeEditorException("Edit mode is not active. Call BeginEditSession first.");
        }

        private void ValidatePropertyName(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("Property name cannot be null or empty", nameof(propertyName));
        }

        private void ValidateTimeRange(double startTime, double endTime)
        {
            if (startTime < 0 || endTime < 0)
                throw new ArgumentException("Time values cannot be negative");

            if (startTime >= endTime)
                throw new ArgumentException("Start time must be less than end time");
        }

        private ValueRange CalculateValueRange(IEnumerable<CurvePoint> points)
        {
            var values = points.Select(p => p.Value).ToList();
            return new ValueRange(values.Min(), values.Max());
        }

        private float CalculateAverageVelocity(IList<CurvePoint> points)
        {
            if (points.Count < 2) return 0f;

            float totalVelocity = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                var deltaValue = points[i].Value - points[i - 1].Value;
                var deltaTime = points[i].Time - points[i - 1].Time;
                if (deltaTime > 0)
                {
                    totalVelocity += Math.Abs(deltaValue / (float)deltaTime);
                }
            }

            return totalVelocity / (points.Count - 1);
        }

        private float CalculateMaxVelocity(IList<CurvePoint> points)
        {
            if (points.Count < 2) return 0f;

            float maxVelocity = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                var deltaValue = points[i].Value - points[i - 1].Value;
                var deltaTime = points[i].Time - points[i - 1].Time;
                if (deltaTime > 0)
                {
                    var velocity = Math.Abs(deltaValue / (float)deltaTime);
                    if (velocity > maxVelocity)
                    {
                        maxVelocity = velocity;
                    }
                }
            }

            return maxVelocity;
        }

        private bool CheckIfConstant(IList<CurvePoint> points)
        {
            if (points.Count < 2) return true;

            var firstValue = points[0].Value;
            return points.All(p => Math.Abs(p.Value - firstValue) < float.Epsilon);
        }

        private bool CheckForDiscontinuities(IList<CurvePoint> points)
        {
            if (points.Count < 2) return false;

            for (int i = 1; i < points.Count; i++)
            {
                var deltaValue = Math.Abs(points[i].Value - points[i - 1].Value);
                var deltaTime = points[i].Time - points[i - 1].Time;

                // If there's a large value change in a small time period, consider it a discontinuity;
                if (deltaTime > 0 && deltaValue / deltaTime > 1000f) // Threshold can be adjusted;
                {
                    return true;
                }
            }

            return false;
        }
        #endregion;

        #region Event Handling;
        protected virtual void OnEditorStateChanged(KeyframeEditorEventArgs e)
        {
            EditorStateChanged?.Invoke(this, e);
        }

        protected virtual void OnSelectionChanged(KeyframeSelectionChangedEventArgs e)
        {
            SelectionChanged?.Invoke(this, e);
        }

        protected virtual void OnCurveUpdated(AnimationCurveUpdatedEventArgs e)
        {
            CurveUpdated?.Invoke(this, e);
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
                    if (_isEditMode)
                    {
                        EndEditSession(false);
                    }

                    ClearSelection();

                    if (_dataService is IDisposable disposableDataService)
                    {
                        disposableDataService.Dispose();
                    }

                    if (_curveManager is IDisposable disposableCurveManager)
                    {
                        disposableCurveManager.Dispose();
                    }

                    _logger.LogInformation("KeyframeEditor disposed");
                }

                _disposed = true;
            }
        }

        ~KeyframeEditor()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes;
    public class KeyframeSelection;
    {
        private readonly IReadOnlyList<Keyframe> _keyframes;

        public int Count => _keyframes.Count;
        public IReadOnlyList<Keyframe> Keyframes => _keyframes;
        public bool IsEmpty => !_keyframes.Any();

        public KeyframeSelection(IEnumerable<Keyframe> keyframes)
        {
            _keyframes = keyframes?.ToList().AsReadOnly() ?? new List<Keyframe>().AsReadOnly();
        }

        public bool Contains(Keyframe keyframe) => _keyframes.Contains(keyframe);
    }

    public class KeyframeEditResult;
    {
        public bool Success { get; }
        public string Message { get; }
        public IReadOnlyList<Keyframe> AffectedKeyframes { get; }
        public int KeyframesModified => AffectedKeyframes?.Count ?? 0;

        private KeyframeEditResult(bool success, string message, IEnumerable<Keyframe> affectedKeyframes)
        {
            Success = success;
            Message = message;
            AffectedKeyframes = affectedKeyframes?.ToList().AsReadOnly() ?? new List<Keyframe>().AsReadOnly();
        }

        public static KeyframeEditResult CreateSuccess(IEnumerable<Keyframe> affectedKeyframes)
        {
            return new KeyframeEditResult(true, "Operation completed successfully", affectedKeyframes);
        }

        public static KeyframeEditResult CreateFailure(string message)
        {
            return new KeyframeEditResult(false, message, null);
        }
    }

    public class CurveAnalysisResult;
    {
        public string PropertyName { get; set; }
        public int KeyframeCount { get; set; }
        public int SampleCount { get; set; }
        public TimeRange TimeRange { get; set; }
        public ValueRange ValueRange { get; set; }
        public float AverageVelocity { get; set; }
        public float MaxVelocity { get; set; }
        public bool IsConstant { get; set; }
        public bool HasDiscontinuities { get; set; }

        public static CurveAnalysisResult CreateEmpty(string propertyName)
        {
            return new CurveAnalysisResult;
            {
                PropertyName = propertyName,
                KeyframeCount = 0,
                SampleCount = 0,
                TimeRange = new TimeRange(0, 0),
                ValueRange = new ValueRange(0, 0),
                AverageVelocity = 0,
                MaxVelocity = 0,
                IsConstant = true,
                HasDiscontinuities = false;
            };
        }
    }

    public struct TimeRange;
    {
        public double Start { get; }
        public double End { get; }
        public double Duration => End - Start;

        public TimeRange(double start, double end)
        {
            Start = start;
            End = end;
        }
    }

    public struct ValueRange;
    {
        public float Min { get; }
        public float Max { get; }
        public float Span => Max - Min;

        public ValueRange(float min, float max)
        {
            Min = min;
            Max = max;
        }
    }
    #endregion;
}
