using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
{
    /// <summary>
    /// Represents a parameter in the animation system with full type support and constraints;
    /// </summary>
    public class AnimationParameter : INotifyPropertyChanged;
    {
        private string _name;
        private ParameterType _type;
        private object _value;
        private object _defaultValue;
        private object _minValue;
        private object _maxValue;
        private bool _isExposed;
        private bool _isReadOnly;
        private string _description;
        private ParameterBinding _binding;
        private float _smoothing;
        private ParameterValueChangedEventHandler _valueChanged;

        public string Name;
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public ParameterType Type;
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValueType)); }
        }

        public object Value;
        {
            get => _value;
            set;
            {
                if (_value?.Equals(value) == true) return;

                var oldValue = _value;
                _value = ValidateAndConvertValue(value);
                OnPropertyChanged();
                OnValueChanged(oldValue, _value);
            }
        }

        public object DefaultValue;
        {
            get => _defaultValue;
            set { _defaultValue = ValidateAndConvertValue(value); OnPropertyChanged(); }
        }

        public object MinValue;
        {
            get => _minValue;
            set { _minValue = ValidateAndConvertValue(value); OnPropertyChanged(); }
        }

        public object MaxValue;
        {
            get => _maxValue;
            set { _maxValue = ValidateAndConvertValue(value); OnPropertyChanged(); }
        }

        public bool IsExposed;
        {
            get => _isExposed;
            set { _isExposed = value; OnPropertyChanged(); }
        }

        public bool IsReadOnly;
        {
            get => _isReadOnly;
            set { _isReadOnly = value; OnPropertyChanged(); }
        }

        public string Description;
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public ParameterBinding Binding;
        {
            get => _binding;
            set { _binding = value; OnPropertyChanged(); }
        }

        public float Smoothing;
        {
            get => _smoothing;
            set { _smoothing = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); }
        }

        public Type ValueType => GetSystemType(_type);

        public event PropertyChangedEventHandler PropertyChanged;
        public event ParameterValueChangedEventHandler ValueChanged;
        {
            add { _valueChanged += value; }
            remove { _valueChanged -= value; }
        }

        public AnimationParameter(string name, ParameterType type, object defaultValue = null)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _type = type;
            _smoothing = 0.1f;
            _isExposed = true;

            // Set default constraints based on type;
            SetDefaultConstraints();

            // Set default value after constraints are established;
            _defaultValue = ValidateAndConvertValue(defaultValue ?? GetDefaultValueForType(type));
            _value = _defaultValue;
        }

        public T GetValue<T>()
        {
            if (_value is T typedValue)
                return typedValue;

            try
            {
                return (T)Convert.ChangeType(_value, typeof(T));
            }
            catch (Exception ex)
            {
                throw new InvalidCastException($"Cannot convert parameter {_name} from {_value?.GetType()} to {typeof(T)}", ex);
            }
        }

        public void SetValue<T>(T value, bool immediate = false)
        {
            if (_isReadOnly)
                throw new InvalidOperationException($"Parameter {_name} is read-only");

            Value = value;
        }

        public void ResetToDefault()
        {
            Value = DefaultValue;
        }

        public bool IsInRange(object value)
        {
            var validatedValue = ValidateAndConvertValue(value);
            return CheckRange(validatedValue);
        }

        public void AddBinding(string sourcePath, BindingMode mode = BindingMode.OneWay, float multiplier = 1.0f)
        {
            Binding = new ParameterBinding;
            {
                SourcePath = sourcePath,
                Mode = mode,
                Multiplier = multiplier;
            };
        }

        public void RemoveBinding()
        {
            Binding = null;
        }

        private object ValidateAndConvertValue(object value)
        {
            if (value == null)
                return GetDefaultValueForType(_type);

            var targetType = ValueType;
            if (value.GetType() == targetType)
                return CheckRange(value) ? value : ClampValue(value);

            try
            {
                var convertedValue = Convert.ChangeType(value, targetType);
                return CheckRange(convertedValue) ? convertedValue : ClampValue(convertedValue);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid value for parameter {_name}. Expected {targetType}, got {value.GetType()}", ex);
            }
        }

        private bool CheckRange(object value)
        {
            if (_minValue == null || _maxValue == null)
                return true;

            return CompareValues(value, _minValue) >= 0 && CompareValues(value, _maxValue) <= 0;
        }

        private object ClampValue(object value)
        {
            if (_minValue == null || _maxValue == null)
                return value;

            if (CompareValues(value, _minValue) < 0)
                return _minValue;
            if (CompareValues(value, _maxValue) > 0)
                return _maxValue;

            return value;
        }

        private int CompareValues(object a, object b)
        {
            return Comparer.Default.Compare(a, b);
        }

        private void SetDefaultConstraints()
        {
            switch (_type)
            {
                case ParameterType.Bool:
                    _minValue = false;
                    _maxValue = true;
                    break;
                case ParameterType.Int:
                    _minValue = int.MinValue;
                    _maxValue = int.MaxValue;
                    break;
                case ParameterType.Float:
                    _minValue = float.MinValue;
                    _maxValue = float.MaxValue;
                    break;
                case ParameterType.Vector2:
                    _minValue = new Vector2(float.MinValue, float.MinValue);
                    _maxValue = new Vector2(float.MaxValue, float.MaxValue);
                    break;
                case ParameterType.Trigger:
                    _minValue = false;
                    _maxValue = true;
                    break;
            }
        }

        private object GetDefaultValueForType(ParameterType type)
        {
            return type switch;
            {
                ParameterType.Bool => false,
                ParameterType.Int => 0,
                ParameterType.Float => 0.0f,
                ParameterType.Vector2 => new Vector2(0, 0),
                ParameterType.Trigger => false,
                ParameterType.String => string.Empty,
                _ => null;
            };
        }

        private Type GetSystemType(ParameterType parameterType)
        {
            return parameterType switch;
            {
                ParameterType.Bool => typeof(bool),
                ParameterType.Int => typeof(int),
                ParameterType.Float => typeof(float),
                ParameterType.Vector2 => typeof(Vector2),
                ParameterType.Trigger => typeof(bool),
                ParameterType.String => typeof(string),
                _ => typeof(object)
            };
        }

        private void OnValueChanged(object oldValue, object newValue)
        {
            _valueChanged?.Invoke(this, new ParameterValueChangedEventArgs;
            {
                Parameter = this,
                OldValue = oldValue,
                NewValue = newValue,
                Timestamp = DateTime.UtcNow;
            });
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Main parameter handler for animation blueprints and state machines;
    /// </summary>
    public class ParameterHandler : INotifyPropertyChanged, IParameterProvider;
    {
        private readonly IParameterBindingResolver _bindingResolver;
        private readonly IParameterSmoother _smoother;
        private readonly ObservableCollection<AnimationParameter> _parameters;
        private readonly Dictionary<string, AnimationParameter> _parameterLookup;
        private readonly Dictionary<string, object> _cachedValues;
        private readonly List<ParameterBinding> _activeBindings;

        private bool _isUpdating;
        private ParameterHandler _parentHandler;
        private ParameterSyncMode _syncMode;

        public ReadOnlyObservableCollection<AnimationParameter> Parameters { get; }
        public IParameterProvider ParentProvider { get; set; }

        public ParameterSyncMode SyncMode;
        {
            get => _syncMode;
            set { _syncMode = value; OnPropertyChanged(); }
        }

        public int Count => _parameters.Count;

        public event EventHandler<ParameterCollectionChangedEventArgs> ParameterCollectionChanged;
        public event ParameterValueChangedEventHandler ParameterValueChanged;
        public event EventHandler<ParameterBindingResolvedEventArgs> ParameterBindingResolved;

        public ParameterHandler(IParameterBindingResolver bindingResolver = null, IParameterSmoother smoother = null)
        {
            _bindingResolver = bindingResolver;
            _smoother = smoother;
            _parameters = new ObservableCollection<AnimationParameter>();
            _parameterLookup = new Dictionary<string, AnimationParameter>(StringComparer.OrdinalIgnoreCase);
            _cachedValues = new Dictionary<string, object>();
            _activeBindings = new List<ParameterBinding>();

            Parameters = new ReadOnlyObservableCollection<AnimationParameter>(_parameters);

            _parameters.CollectionChanged += OnParametersCollectionChanged;
        }

        /// <summary>
        /// Adds a new parameter to the handler;
        /// </summary>
        public AnimationParameter AddParameter(string name, ParameterType type, object defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Parameter name cannot be null or empty", nameof(name));

            if (_parameterLookup.ContainsKey(name))
                throw new ArgumentException($"Parameter '{name}' already exists", nameof(name));

            var parameter = new AnimationParameter(name, type, defaultValue);
            _parameters.Add(parameter);
            _parameterLookup[name] = parameter;

            parameter.ValueChanged += OnParameterValueChanged;

            return parameter;
        }

        /// <summary>
        /// Removes a parameter from the handler;
        /// </summary>
        public bool RemoveParameter(string name)
        {
            if (_parameterLookup.TryGetValue(name, out var parameter))
            {
                parameter.ValueChanged -= OnParameterValueChanged;
                _parameters.Remove(parameter);
                _parameterLookup.Remove(name);
                _cachedValues.Remove(name);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a parameter by name;
        /// </summary>
        public AnimationParameter GetParameter(string name)
        {
            return _parameterLookup.TryGetValue(name, out var parameter) ? parameter : null;
        }

        /// <summary>
        /// Checks if a parameter exists;
        /// </summary>
        public bool HasParameter(string name)
        {
            return _parameterLookup.ContainsKey(name);
        }

        /// <summary>
        /// Gets the value of a parameter;
        /// </summary>
        public T GetValue<T>(string name)
        {
            var parameter = GetParameter(name);
            if (parameter == null)
                throw new ArgumentException($"Parameter '{name}' not found");

            return parameter.GetValue<T>();
        }

        /// <summary>
        /// Sets the value of a parameter with optional smoothing;
        /// </summary>
        public void SetValue<T>(string name, T value, bool immediate = false)
        {
            var parameter = GetParameter(name);
            if (parameter == null)
                throw new ArgumentException($"Parameter '{name}' not found");

            if (parameter.IsReadOnly)
                throw new InvalidOperationException($"Parameter '{name}' is read-only");

            if (!immediate && parameter.Smoothing > 0 && _smoother != null)
            {
                _smoother.SmoothParameter(parameter, value);
            }
            else;
            {
                parameter.SetValue(value, immediate);
            }
        }

        /// <summary>
        /// Sets multiple parameter values at once;
        /// </summary>
        public void SetValues(IEnumerable<KeyValuePair<string, object>> values, bool immediate = false)
        {
            _isUpdating = true;

            try
            {
                foreach (var kvp in values)
                {
                    if (_parameterLookup.TryGetValue(kvp.Key, out var parameter))
                    {
                        if (!parameter.IsReadOnly)
                        {
                            parameter.SetValue(kvp.Value);
                        }
                    }
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        /// <summary>
        /// Resets all parameters to their default values;
        /// </summary>
        public void ResetAllToDefault()
        {
            _isUpdating = true;

            try
            {
                foreach (var parameter in _parameters)
                {
                    parameter.ResetToDefault();
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        /// <summary>
        /// Creates a binding between parameters;
        /// </summary>
        public void CreateBinding(string sourceParameterName, string targetParameterName,
            BindingMode mode = BindingMode.OneWay, float multiplier = 1.0f)
        {
            var sourceParameter = GetParameter(sourceParameterName);
            var targetParameter = GetParameter(targetParameterName);

            if (sourceParameter == null || targetParameter == null)
                throw new ArgumentException("Source or target parameter not found");

            var binding = new ParameterBinding;
            {
                SourcePath = sourceParameterName,
                TargetPath = targetParameterName,
                Mode = mode,
                Multiplier = multiplier;
            };

            _activeBindings.Add(binding);
            sourceParameter.ValueChanged += (s, e) => OnSourceParameterChanged(binding, e);
        }

        /// <summary>
        /// Updates all parameter bindings;
        /// </summary>
        public void UpdateBindings()
        {
            if (_bindingResolver == null) return;

            foreach (var binding in _activeBindings)
            {
                UpdateBinding(binding);
            }
        }

        /// <summary>
        /// Resolves external parameter bindings;
        /// </summary>
        public void ResolveExternalBindings(IParameterProvider externalProvider)
        {
            if (externalProvider == null) return;

            foreach (var parameter in _parameters.Where(p => p.Binding != null && p.IsExposed))
            {
                ResolveExternalBinding(parameter, externalProvider);
            }
        }

        /// <summary>
        /// Gets all exposed parameters that can be controlled externally;
        /// </summary>
        public IEnumerable<AnimationParameter> GetExposedParameters()
        {
            return _parameters.Where(p => p.IsExposed);
        }

        /// <summary>
        /// Gets all parameters of a specific type;
        /// </summary>
        public IEnumerable<AnimationParameter> GetParametersByType(ParameterType type)
        {
            return _parameters.Where(p => p.Type == type);
        }

        /// <summary>
        /// Creates a snapshot of current parameter values;
        /// </summary>
        public ParameterSnapshot CreateSnapshot()
        {
            var snapshot = new ParameterSnapshot();

            foreach (var parameter in _parameters)
            {
                snapshot.Values[parameter.Name] = parameter.Value;
            }

            return snapshot;
        }

        /// <summary>
        /// Applies a parameter snapshot;
        /// </summary>
        public void ApplySnapshot(ParameterSnapshot snapshot, bool immediate = false)
        {
            if (snapshot == null) return;

            _isUpdating = true;

            try
            {
                foreach (var kvp in snapshot.Values)
                {
                    if (_parameterLookup.TryGetValue(kvp.Key, out var parameter))
                    {
                        if (!parameter.IsReadOnly)
                        {
                            parameter.SetValue(kvp.Value, immediate);
                        }
                    }
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        /// <summary>
        /// Performs bulk parameter operations for performance;
        /// </summary>
        public IDisposable BeginBulkUpdate()
        {
            return new BulkParameterUpdate(this);
        }

        /// <summary>
        /// Validates all parameters and their constraints;
        /// </summary>
        public ParameterValidationResult Validate()
        {
            var result = new ParameterValidationResult();

            foreach (var parameter in _parameters)
            {
                if (!parameter.IsInRange(parameter.Value))
                {
                    result.Errors.Add($"Parameter '{parameter.Name}' value is out of range");
                }

                if (string.IsNullOrWhiteSpace(parameter.Name))
                {
                    result.Errors.Add($"Parameter has invalid name");
                }
            }

            // Check for duplicate names;
            var duplicateNames = _parameters;
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var name in duplicateNames)
            {
                result.Errors.Add($"Duplicate parameter name: {name}");
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }

        /// <summary>
        /// Initializes parameters from a configuration;
        /// </summary>
        public void InitializeFromConfig(ParameterConfig config)
        {
            if (config?.Parameters == null) return;

            foreach (var paramConfig in config.Parameters)
            {
                AddParameter(paramConfig.Name, paramConfig.Type, paramConfig.DefaultValue)
                    .WithConstraints(paramConfig.MinValue, paramConfig.MaxValue)
                    .WithMetadata(paramConfig.Description, paramConfig.IsExposed, paramConfig.IsReadOnly);
            }
        }

        private void OnParametersCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            ParameterCollectionChanged?.Invoke(this, new ParameterCollectionChangedEventArgs;
            {
                Action = e.Action,
                NewItems = e.NewItems?.Cast<AnimationParameter>().ToList(),
                OldItems = e.OldItems?.Cast<AnimationParameter>().ToList(),
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnParameterValueChanged(object sender, ParameterValueChangedEventArgs e)
        {
            if (_isUpdating) return;

            ParameterValueChanged?.Invoke(sender, e);

            // Update bindings that depend on this parameter;
            UpdateDependentBindings(e.Parameter.Name);
        }

        private void OnSourceParameterChanged(ParameterBinding binding, ParameterValueChangedEventArgs e)
        {
            if (binding.Mode == BindingMode.OneWay || binding.Mode == BindingMode.TwoWay)
            {
                UpdateBinding(binding);
            }
        }

        private void UpdateBinding(ParameterBinding binding)
        {
            try
            {
                var sourceValue = GetParameterValue(binding.SourcePath);
                if (sourceValue == null) return;

                var transformedValue = ApplyBindingTransform(sourceValue, binding);
                SetParameterValue(binding.TargetPath, transformedValue);

                OnParameterBindingResolved(new ParameterBindingResolvedEventArgs;
                {
                    Binding = binding,
                    SourceValue = sourceValue,
                    TargetValue = transformedValue,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                // Log binding resolution errors;
                System.Diagnostics.Debug.WriteLine($"Binding resolution failed: {ex.Message}");
            }
        }

        private object GetParameterValue(string parameterPath)
        {
            // Simple implementation - in real scenario would handle nested paths;
            return GetParameter(parameterPath)?.Value;
        }

        private void SetParameterValue(string parameterPath, object value)
        {
            // Simple implementation - in real scenario would handle nested paths;
            var parameter = GetParameter(parameterPath);
            parameter?.SetValue(value);
        }

        private object ApplyBindingTransform(object sourceValue, ParameterBinding binding)
        {
            if (binding.Multiplier != 1.0f && sourceValue is IConvertible convertible)
            {
                var doubleValue = Convert.ToDouble(convertible);
                return Convert.ChangeType(doubleValue * binding.Multiplier, sourceValue.GetType());
            }

            return sourceValue;
        }

        private void ResolveExternalBinding(AnimationParameter parameter, IParameterProvider externalProvider)
        {
            if (parameter.Binding == null) return;

            var externalValue = externalProvider.GetValue<object>(parameter.Binding.SourcePath);
            if (externalValue != null)
            {
                parameter.SetValue(externalValue);
            }
        }

        private void UpdateDependentBindings(string parameterName)
        {
            var dependentBindings = _activeBindings.Where(b => b.SourcePath == parameterName);
            foreach (var binding in dependentBindings)
            {
                UpdateBinding(binding);
            }
        }

        private void OnParameterBindingResolved(ParameterBindingResolvedEventArgs e)
        {
            ParameterBindingResolved?.Invoke(this, e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region Supporting Types and Interfaces;

    public enum ParameterType;
    {
        Bool,
        Int,
        Float,
        Vector2,
        Trigger,
        String;
    }

    public enum BindingMode;
    {
        OneWay,
        TwoWay,
        OneTime;
    }

    public enum ParameterSyncMode;
    {
        None,
        WithParent,
        WithChildren,
        Bidirectional;
    }

    public delegate void ParameterValueChangedEventHandler(object sender, ParameterValueChangedEventArgs e);

    public class ParameterValueChangedEventArgs : EventArgs;
    {
        public AnimationParameter Parameter { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ParameterCollectionChangedEventArgs : EventArgs;
    {
        public NotifyCollectionChangedAction Action { get; set; }
        public List<AnimationParameter> NewItems { get; set; }
        public List<AnimationParameter> OldItems { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ParameterBindingResolvedEventArgs : EventArgs;
    {
        public ParameterBinding Binding { get; set; }
        public object SourceValue { get; set; }
        public object TargetValue { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public struct ParameterBinding;
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public BindingMode Mode { get; set; }
        public float Multiplier { get; set; }
        public string TransformExpression { get; set; }
    }

    public class ParameterSnapshot;
    {
        public Dictionary<string, object> Values { get; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; } = DateTime.UtcNow;
    }

    public class ParameterValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ParameterConfig;
    {
        public List<ParameterConfigItem> Parameters { get; set; } = new List<ParameterConfigItem>();
    }

    public class ParameterConfigItem;
    {
        public string Name { get; set; }
        public ParameterType Type { get; set; }
        public object DefaultValue { get; set; }
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public string Description { get; set; }
        public bool IsExposed { get; set; } = true;
        public bool IsReadOnly { get; set; }
    }

    public interface IParameterProvider;
    {
        T GetValue<T>(string name);
        void SetValue<T>(string name, T value, bool immediate = false);
        bool HasParameter(string name);
    }

    public interface IParameterBindingResolver;
    {
        object ResolveBinding(string path, IParameterProvider context);
    }

    public interface IParameterSmoother;
    {
        void SmoothParameter(AnimationParameter parameter, object targetValue);
        void UpdateSmoothing(float deltaTime);
    }

    public struct Vector2;
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    #endregion;

    #region Extension Methods;

    public static class ParameterExtensions;
    {
        public static AnimationParameter WithConstraints(this AnimationParameter parameter, object minValue, object maxValue)
        {
            parameter.MinValue = minValue;
            parameter.MaxValue = maxValue;
            return parameter;
        }

        public static AnimationParameter WithMetadata(this AnimationParameter parameter, string description, bool isExposed = true, bool isReadOnly = false)
        {
            parameter.Description = description;
            parameter.IsExposed = isExposed;
            parameter.IsReadOnly = isReadOnly;
            return parameter;
        }

        public static AnimationParameter WithSmoothing(this AnimationParameter parameter, float smoothing)
        {
            parameter.Smoothing = smoothing;
            return parameter;
        }

        public static AnimationParameter WithBinding(this AnimationParameter parameter, string sourcePath, BindingMode mode = BindingMode.OneWay)
        {
            parameter.AddBinding(sourcePath, mode);
            return parameter;
        }
    }

    #endregion;

    #region Bulk Update Implementation;

    public class BulkParameterUpdate : IDisposable
    {
        private readonly ParameterHandler _handler;
        private readonly bool _originalUpdatingState;

        public BulkParameterUpdate(ParameterHandler handler)
        {
            _handler = handler;
            _originalUpdatingState = handler.GetType().GetField("_isUpdating",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(handler) as bool? ?? false;

            handler.GetType().GetField("_isUpdating",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .SetValue(handler, true);
        }

        public void Dispose()
        {
            _handler.GetType().GetField("_isUpdating",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .SetValue(_handler, _originalUpdatingState);
        }
    }

    #endregion;
}
