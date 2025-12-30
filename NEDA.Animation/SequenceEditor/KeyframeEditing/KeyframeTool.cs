using NEDA.Animation.SequenceEditor.CameraAnimation;
using NEDA.Animation.SequenceEditor.KeyframeEditing.Interfaces;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NEDA.Animation.SequenceEditor.KeyframeEditing;
{
    /// <summary>
    /// Advanced keyframe manipulation tool with support for bezier curves, easing functions,
    /// and real-time interpolation for professional animation workflows;
    /// </summary>
    public class KeyframeTool : IKeyframeTool, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IKeyframeStorage _keyframeStorage;
        private readonly IInterpolationEngine _interpolationEngine;
        private bool _disposed = false;

        #region Properties;
        public string ToolName { get; private set; }
        public KeyframeToolMode CurrentMode { get; private set; }
        public KeyframeManipulationSettings Settings { get; private set; }
        public bool IsActive { get; private set; }
        public event EventHandler<KeyframeToolEventArgs> ToolStateChanged;
        public event EventHandler<KeyframeCollectionChangedEventArgs> KeyframesModified;
        #endregion;

        #region Constructors;
        public KeyframeTool(ILogger logger, IKeyframeStorage keyframeStorage, IInterpolationEngine interpolationEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _keyframeStorage = keyframeStorage ?? throw new ArgumentNullException(nameof(keyframeStorage));
            _interpolationEngine = interpolationEngine ?? throw new ArgumentNullException(nameof(interpolationEngine));

            InitializeTool();
        }

        public KeyframeTool(ILogger logger)
            : this(logger, new DefaultKeyframeStorage(), new BezierInterpolationEngine())
        {
        }
        #endregion;

        #region Initialization;
        private void InitializeTool()
        {
            ToolName = "Advanced Keyframe Tool";
            CurrentMode = KeyframeToolMode.Selection;
            Settings = new KeyframeManipulationSettings;
            {
                SnapToGrid = true,
                GridSize = 1.0f,
                AutoTangent = true,
                DefaultEasing = EasingFunction.EaseInOutCubic,
                Precision = 0.001f;
            };

            _logger.LogInformation($"KeyframeTool initialized: {ToolName}");
        }
        #endregion;

        #region Keyframe Management;
        public Keyframe AddKeyframe(double time, float value, string propertyName, object owner)
        {
            ValidateNotDisposed();
            ValidateParameters(time, propertyName, owner);

            try
            {
                var keyframe = new Keyframe;
                {
                    Id = Guid.NewGuid(),
                    Time = time,
                    Value = value,
                    PropertyName = propertyName,
                    Owner = owner,
                    TangentIn = new TangentPoint { Weight = 0.5f, Type = TangentType.Smooth },
                    TangentOut = new TangentPoint { Weight = 0.5f, Type = TangentType.Smooth },
                    EasingFunction = Settings.DefaultEasing,
                    CreatedAt = DateTime.UtcNow;
                };

                _keyframeStorage.AddKeyframe(keyframe);
                _logger.LogDebug($"Keyframe added: {keyframe.Id} at time {time} for property {propertyName}");

                OnKeyframesModified(new KeyframeCollectionChangedEventArgs;
                {
                    Action = KeyframeCollectionAction.Add,
                    Keyframes = new[] { keyframe },
                    Timestamp = DateTime.UtcNow;
                });

                return keyframe;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add keyframe at time {time} for property {propertyName}");
                throw new KeyframeToolException(ErrorCodes.KeyframeAddFailed,
                    $"Failed to add keyframe: {ex.Message}", ex);
            }
        }

        public bool RemoveKeyframe(Guid keyframeId)
        {
            ValidateNotDisposed();

            try
            {
                var keyframe = _keyframeStorage.GetKeyframe(keyframeId);
                if (keyframe == null)
                {
                    _logger.LogWarning($"Keyframe not found for removal: {keyframeId}");
                    return false;
                }

                var success = _keyframeStorage.RemoveKeyframe(keyframeId);
                if (success)
                {
                    _logger.LogDebug($"Keyframe removed: {keyframeId}");

                    OnKeyframesModified(new KeyframeCollectionChangedEventArgs;
                    {
                        Action = KeyframeCollectionAction.Remove,
                        Keyframes = new[] { keyframe },
                        Timestamp = DateTime.UtcNow;
                    });
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove keyframe: {keyframeId}");
                throw new KeyframeToolException(ErrorCodes.KeyframeRemoveFailed,
                    $"Failed to remove keyframe: {ex.Message}", ex);
            }
        }

        public Keyframe UpdateKeyframe(Guid keyframeId, KeyframeUpdateParameters parameters)
        {
            ValidateNotDisposed();
            ValidateUpdateParameters(parameters);

            try
            {
                var existingKeyframe = _keyframeStorage.GetKeyframe(keyframeId);
                if (existingKeyframe == null)
                {
                    throw new KeyframeNotFoundException($"Keyframe with ID {keyframeId} not found");
                }

                var updatedKeyframe = existingKeyframe.Clone();

                if (parameters.Time.HasValue)
                    updatedKeyframe.Time = parameters.Time.Value;

                if (parameters.Value.HasValue)
                    updatedKeyframe.Value = parameters.Value.Value;

                if (parameters.TangentIn.HasValue)
                    updatedKeyframe.TangentIn = parameters.TangentIn.Value;

                if (parameters.TangentOut.HasValue)
                    updatedKeyframe.TangentOut = parameters.TangentOut.Value;

                if (parameters.EasingFunction.HasValue)
                    updatedKeyframe.EasingFunction = parameters.EasingFunction.Value;

                updatedKeyframe.ModifiedAt = DateTime.UtcNow;

                _keyframeStorage.UpdateKeyframe(updatedKeyframe);
                _logger.LogDebug($"Keyframe updated: {keyframeId}");

                OnKeyframesModified(new KeyframeCollectionChangedEventArgs;
                {
                    Action = KeyframeCollectionAction.Update,
                    Keyframes = new[] { updatedKeyframe },
                    Timestamp = DateTime.UtcNow;
                });

                return updatedKeyframe;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update keyframe: {keyframeId}");
                throw new KeyframeToolException(ErrorCodes.KeyframeUpdateFailed,
                    $"Failed to update keyframe: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<Keyframe> GetKeyframesInRange(double startTime, double endTime, string propertyName = null)
        {
            ValidateNotDisposed();
            ValidateTimeRange(startTime, endTime);

            try
            {
                var allKeyframes = _keyframeStorage.GetAllKeyframes();
                var filteredKeyframes = allKeyframes.Where(k =>
                    k.Time >= startTime && k.Time <= endTime &&
                    (propertyName == null || k.PropertyName == propertyName))
                    .OrderBy(k => k.Time)
                    .ToList();

                return filteredKeyframes.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get keyframes in range {startTime}-{endTime} for property {propertyName}");
                throw new KeyframeToolException(ErrorCodes.KeyframeQueryFailed,
                    $"Failed to query keyframes: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Keyframe Manipulation;
        public void MoveKeyframes(IEnumerable<Guid> keyframeIds, double timeOffset, float valueOffset)
        {
            ValidateNotDisposed();
            ValidateKeyframeIds(keyframeIds);

            try
            {
                var affectedKeyframes = new List<Keyframe>();
                var updatedKeyframes = new List<Keyframe>();

                foreach (var keyframeId in keyframeIds)
                {
                    var keyframe = _keyframeStorage.GetKeyframe(keyframeId);
                    if (keyframe != null)
                    {
                        affectedKeyframes.Add(keyframe);

                        var updatedKeyframe = keyframe.Clone();
                        updatedKeyframe.Time += timeOffset;
                        updatedKeyframe.Value += valueOffset;
                        updatedKeyframe.ModifiedAt = DateTime.UtcNow;

                        _keyframeStorage.UpdateKeyframe(updatedKeyframe);
                        updatedKeyframes.Add(updatedKeyframe);
                    }
                }

                if (updatedKeyframes.Any())
                {
                    _logger.LogDebug($"Moved {updatedKeyframes.Count} keyframes by time offset {timeOffset}, value offset {valueOffset}");

                    OnKeyframesModified(new KeyframeCollectionChangedEventArgs;
                    {
                        Action = KeyframeCollectionAction.Move,
                        Keyframes = updatedKeyframes,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to move keyframes");
                throw new KeyframeToolException(ErrorCodes.KeyframeMoveFailed,
                    $"Failed to move keyframes: {ex.Message}", ex);
            }
        }

        public void ScaleKeyframes(IEnumerable<Guid> keyframeIds, double timeScale, float valueScale, double pivotTime = 0, float pivotValue = 0)
        {
            ValidateNotDisposed();
            ValidateKeyframeIds(keyframeIds);

            try
            {
                var affectedKeyframes = new List<Keyframe>();
                var updatedKeyframes = new List<Keyframe>();

                foreach (var keyframeId in keyframeIds)
                {
                    var keyframe = _keyframeStorage.GetKeyframe(keyframeId);
                    if (keyframe != null)
                    {
                        affectedKeyframes.Add(keyframe);

                        var updatedKeyframe = keyframe.Clone();
                        updatedKeyframe.Time = pivotTime + (updatedKeyframe.Time - pivotTime) * timeScale;
                        updatedKeyframe.Value = pivotValue + (updatedKeyframe.Value - pivotValue) * valueScale;
                        updatedKeyframe.ModifiedAt = DateTime.UtcNow;

                        _keyframeStorage.UpdateKeyframe(updatedKeyframe);
                        updatedKeyframes.Add(updatedKeyframe);
                    }
                }

                if (updatedKeyframes.Any())
                {
                    _logger.LogDebug($"Scaled {updatedKeyframes.Count} keyframes with time scale {timeScale}, value scale {valueScale}");

                    OnKeyframesModified(new KeyframeCollectionChangedEventArgs;
                    {
                        Action = KeyframeCollectionAction.Scale,
                        Keyframes = updatedKeyframes,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scale keyframes");
                throw new KeyframeToolException(ErrorCodes.KeyframeScaleFailed,
                    $"Failed to scale keyframes: {ex.Message}", ex);
            }
        }

        public void ApplyEasingToSelection(IEnumerable<Guid> keyframeIds, EasingFunction easingFunction)
        {
            ValidateNotDisposed();
            ValidateKeyframeIds(keyframeIds);

            try
            {
                var updatedKeyframes = new List<Keyframe>();

                foreach (var keyframeId in keyframeIds)
                {
                    var keyframe = _keyframeStorage.GetKeyframe(keyframeId);
                    if (keyframe != null)
                    {
                        var updatedKeyframe = keyframe.Clone();
                        updatedKeyframe.EasingFunction = easingFunction;
                        updatedKeyframe.ModifiedAt = DateTime.UtcNow;

                        _keyframeStorage.UpdateKeyframe(updatedKeyframe);
                        updatedKeyframes.Add(updatedKeyframe);
                    }
                }

                if (updatedKeyframes.Any())
                {
                    _logger.LogDebug($"Applied easing {easingFunction} to {updatedKeyframes.Count} keyframes");

                    OnKeyframesModified(new KeyframeCollectionChangedEventArgs;
                    {
                        Action = KeyframeCollectionAction.EasingChanged,
                        Keyframes = updatedKeyframes,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply easing to keyframes");
                throw new KeyframeToolException(ErrorCodes.KeyframeEasingFailed,
                    $"Failed to apply easing: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Interpolation and Evaluation;
        public float EvaluateAtTime(double time, string propertyName)
        {
            ValidateNotDisposed();
            ValidateTime(time);

            try
            {
                var keyframes = _keyframeStorage.GetKeyframesForProperty(propertyName);
                if (!keyframes.Any())
                {
                    throw new NoKeyframesFoundException($"No keyframes found for property: {propertyName}");
                }

                var sortedKeyframes = keyframes.OrderBy(k => k.Time).ToList();

                // Find surrounding keyframes;
                var previousKeyframe = sortedKeyframes.LastOrDefault(k => k.Time <= time);
                var nextKeyframe = sortedKeyframes.FirstOrDefault(k => k.Time >= time);

                if (previousKeyframe == null && nextKeyframe == null)
                    return 0f;

                if (previousKeyframe == null)
                    return nextKeyframe.Value;

                if (nextKeyframe == null)
                    return previousKeyframe.Value;

                if (Math.Abs(previousKeyframe.Time - nextKeyframe.Time) < Settings.Precision)
                    return previousKeyframe.Value;

                // Calculate interpolation factor;
                var t = (time - previousKeyframe.Time) / (nextKeyframe.Time - previousKeyframe.Time);

                // Apply easing function;
                var easedT = ApplyEasingFunction(t, previousKeyframe.EasingFunction);

                // Perform interpolation;
                var interpolatedValue = _interpolationEngine.Interpolate(
                    previousKeyframe.Value, nextKeyframe.Value, easedT,
                    previousKeyframe.TangentOut, nextKeyframe.TangentIn);

                return interpolatedValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to evaluate property {propertyName} at time {time}");
                throw new KeyframeToolException(ErrorCodes.KeyframeEvaluationFailed,
                    $"Failed to evaluate at time: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<AnimationCurvePoint> GenerateCurvePoints(string propertyName, double startTime, double endTime, int sampleCount = 100)
        {
            ValidateNotDisposed();
            ValidateTimeRange(startTime, endTime);

            if (sampleCount <= 0)
                throw new ArgumentException("Sample count must be positive", nameof(sampleCount));

            try
            {
                var points = new List<AnimationCurvePoint>();
                var timeStep = (endTime - startTime) / (sampleCount - 1);

                for (int i = 0; i < sampleCount; i++)
                {
                    var time = startTime + i * timeStep;
                    var value = EvaluateAtTime(time, propertyName);

                    points.Add(new AnimationCurvePoint;
                    {
                        Time = time,
                        Value = value,
                        SampleIndex = i;
                    });
                }

                return points.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate curve points for property {propertyName}");
                throw new KeyframeToolException(ErrorCodes.CurveGenerationFailed,
                    $"Failed to generate curve: {ex.Message}", ex);
            }
        }
        #endregion;

        #region Tool Operations;
        public void SetToolMode(KeyframeToolMode mode)
        {
            ValidateNotDisposed();

            var previousMode = CurrentMode;
            CurrentMode = mode;

            _logger.LogDebug($"Keyframe tool mode changed from {previousMode} to {mode}");

            OnToolStateChanged(new KeyframeToolEventArgs;
            {
                EventType = KeyframeToolEventType.ModeChanged,
                PreviousMode = previousMode,
                CurrentMode = mode,
                Timestamp = DateTime.UtcNow;
            });
        }

        public void Activate()
        {
            ValidateNotDisposed();

            if (!IsActive)
            {
                IsActive = true;
                _logger.LogInformation("Keyframe tool activated");

                OnToolStateChanged(new KeyframeToolEventArgs;
                {
                    EventType = KeyframeToolEventType.Activated,
                    CurrentMode = CurrentMode,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        public void Deactivate()
        {
            ValidateNotDisposed();

            if (IsActive)
            {
                IsActive = false;
                _logger.LogInformation("Keyframe tool deactivated");

                OnToolStateChanged(new KeyframeToolEventArgs;
                {
                    EventType = KeyframeToolEventType.Deactivated,
                    CurrentMode = CurrentMode,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        public void UpdateSettings(KeyframeManipulationSettings newSettings)
        {
            ValidateNotDisposed();
            ValidateSettings(newSettings);

            var oldSettings = Settings;
            Settings = newSettings;

            _logger.LogDebug("Keyframe tool settings updated");

            OnToolStateChanged(new KeyframeToolEventArgs;
            {
                EventType = KeyframeToolEventType.SettingsChanged,
                PreviousSettings = oldSettings,
                CurrentSettings = Settings,
                Timestamp = DateTime.UtcNow;
            });
        }
        #endregion;

        #region Utility Methods;
        private float ApplyEasingFunction(float t, EasingFunction easing)
        {
            return EasingUtilities.ApplyEasing(t, easing);
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KeyframeTool));
        }

        private void ValidateParameters(double time, string propertyName, object owner)
        {
            if (time < 0)
                throw new ArgumentException("Time cannot be negative", nameof(time));

            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("Property name cannot be null or empty", nameof(propertyName));

            if (owner == null)
                throw new ArgumentNullException(nameof(owner));
        }

        private void ValidateUpdateParameters(KeyframeUpdateParameters parameters)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            if (parameters.Time.HasValue && parameters.Time.Value < 0)
                throw new ArgumentException("Time cannot be negative", nameof(parameters));
        }

        private void ValidateTimeRange(double startTime, double endTime)
        {
            if (startTime < 0 || endTime < 0)
                throw new ArgumentException("Time values cannot be negative");

            if (startTime > endTime)
                throw new ArgumentException("Start time cannot be greater than end time");
        }

        private void ValidateTime(double time)
        {
            if (time < 0)
                throw new ArgumentException("Time cannot be negative", nameof(time));
        }

        private void ValidateKeyframeIds(IEnumerable<Guid> keyframeIds)
        {
            if (keyframeIds == null)
                throw new ArgumentNullException(nameof(keyframeIds));

            if (!keyframeIds.Any())
                throw new ArgumentException("Keyframe IDs collection cannot be empty", nameof(keyframeIds));
        }

        private void ValidateSettings(KeyframeManipulationSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (settings.GridSize <= 0)
                throw new ArgumentException("Grid size must be positive", nameof(settings));

            if (settings.Precision <= 0)
                throw new ArgumentException("Precision must be positive", nameof(settings));
        }
        #endregion;

        #region Event Handling;
        protected virtual void OnToolStateChanged(KeyframeToolEventArgs e)
        {
            ToolStateChanged?.Invoke(this, e);
        }

        protected virtual void OnKeyframesModified(KeyframeCollectionChangedEventArgs e)
        {
            KeyframesModified?.Invoke(this, e);
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
                    Deactivate();

                    if (_keyframeStorage is IDisposable disposableStorage)
                        disposableStorage.Dispose();

                    if (_interpolationEngine is IDisposable disposableInterpolation)
                        disposableInterpolation.Dispose();

                    _logger.LogInformation("Keyframe tool disposed");
                }

                _disposed = true;
            }
        }

        ~KeyframeTool()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Enums and Classes;
    public enum KeyframeToolMode;
    {
        Selection,
        Add,
        Move,
        Scale,
        TangentEdit,
        EasingEdit;
    }

    public enum KeyframeCollectionAction;
    {
        Add,
        Remove,
        Update,
        Move,
        Scale,
        EasingChanged;
    }

    public enum KeyframeToolEventType;
    {
        Activated,
        Deactivated,
        ModeChanged,
        SettingsChanged;
    }

    public class Keyframe;
    {
        public Guid Id { get; set; }
        public double Time { get; set; }
        public float Value { get; set; }
        public string PropertyName { get; set; }
        public object Owner { get; set; }
        public TangentPoint TangentIn { get; set; }
        public TangentPoint TangentOut { get; set; }
        public EasingFunction EasingFunction { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }

        public Keyframe Clone()
        {
            return new Keyframe;
            {
                Id = Id,
                Time = Time,
                Value = Value,
                PropertyName = PropertyName,
                Owner = Owner,
                TangentIn = TangentIn,
                TangentOut = TangentOut,
                EasingFunction = EasingFunction,
                CreatedAt = CreatedAt,
                ModifiedAt = ModifiedAt;
            };
        }
    }

    public struct TangentPoint;
    {
        public float Weight { get; set; }
        public TangentType Type { get; set; }
    }

    public enum TangentType;
    {
        Linear,
        Smooth,
        Stepped,
        Flat;
    }

    public class KeyframeUpdateParameters;
    {
        public double? Time { get; set; }
        public float? Value { get; set; }
        public TangentPoint? TangentIn { get; set; }
        public TangentPoint? TangentOut { get; set; }
        public EasingFunction? EasingFunction { get; set; }
    }

    public class KeyframeManipulationSettings;
    {
        public bool SnapToGrid { get; set; }
        public float GridSize { get; set; }
        public bool AutoTangent { get; set; }
        public EasingFunction DefaultEasing { get; set; }
        public float Precision { get; set; }
    }

    public class KeyframeToolEventArgs : EventArgs;
    {
        public KeyframeToolEventType EventType { get; set; }
        public KeyframeToolMode PreviousMode { get; set; }
        public KeyframeToolMode CurrentMode { get; set; }
        public KeyframeManipulationSettings PreviousSettings { get; set; }
        public KeyframeManipulationSettings CurrentSettings { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class KeyframeCollectionChangedEventArgs : EventArgs;
    {
        public KeyframeCollectionAction Action { get; set; }
        public IEnumerable<Keyframe> Keyframes { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class AnimationCurvePoint;
    {
        public double Time { get; set; }
        public float Value { get; set; }
        public int SampleIndex { get; set; }
    }
    #endregion;
}
