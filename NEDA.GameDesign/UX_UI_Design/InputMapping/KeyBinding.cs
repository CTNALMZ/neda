using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;

namespace NEDA.GameDesign.UX_UI_Design.InputMapping;
{
    /// <summary>
    /// Represents a configurable key binding with support for multiple input devices,
    /// modifiers, and contextual overrides;
    /// </summary>
    public class KeyBinding : ICloneable, IEquatable<KeyBinding>
    {
        private readonly ILogger _logger;
        private bool _isEnabled = true;
        private float _sensitivity = 1.0f;
        private float _deadZone = 0.1f;

        /// <summary>
        /// Unique identifier for this key binding;
        /// </summary>
        [JsonPropertyName("id")]
        [XmlAttribute("id")]
        public string Id { get; set; }

        /// <summary>
        /// Display name for the binding;
        /// </summary>
        [JsonPropertyName("name")]
        [XmlAttribute("name")]
        public string Name { get; set; }

        /// <summary>
        /// Description of the binding's function;
        /// </summary>
        [JsonPropertyName("description")]
        [XmlElement("description")]
        public string Description { get; set; }

        /// <summary>
        /// Primary key or button for this binding;
        /// </summary>
        [JsonPropertyName("primaryKey")]
        [XmlElement("primaryKey")]
        public InputKey PrimaryKey { get; set; }

        /// <summary>
        /// Alternative key or button for this binding;
        /// </summary>
        [JsonPropertyName("alternateKey")]
        [XmlElement("alternateKey")]
        public InputKey AlternateKey { get; set; }

        /// <summary>
        /// Gamepad button for this binding;
        /// </summary>
        [JsonPropertyName("gamepadButton")]
        [XmlElement("gamepadButton")]
        public GamepadButton GamepadButton { get; set; }

        /// <summary>
        /// Mouse button for this binding;
        /// </summary>
        [JsonPropertyName("mouseButton")]
        [XmlElement("mouseButton")]
        public MouseButton MouseButton { get; set; }

        /// <summary>
        /// Mouse wheel direction for this binding;
        /// </summary>
        [JsonPropertyName("mouseWheelDirection")]
        [XmlElement("mouseWheelDirection")]
        public MouseWheelDirection MouseWheelDirection { get; set; }

        /// <summary>
        /// Required modifier keys (Ctrl, Alt, Shift, etc.)
        /// </summary>
        [JsonPropertyName("modifiers")]
        [XmlArray("modifiers")]
        [XmlArrayItem("modifier")]
        public List<ModifierKey> Modifiers { get; set; } = new List<ModifierKey>();

        /// <summary>
        /// Input device type for this binding;
        /// </summary>
        [JsonPropertyName("deviceType")]
        [XmlElement("deviceType")]
        public InputDeviceType DeviceType { get; set; } = InputDeviceType.Keyboard;

        /// <summary>
        /// Binding category for organization;
        /// </summary>
        [JsonPropertyName("category")]
        [XmlElement("category")]
        public string Category { get; set; } = "General";

        /// <summary>
        /// Binding group for related bindings;
        /// </summary>
        [JsonPropertyName("group")]
        [XmlElement("group")]
        public string Group { get; set; }

        /// <summary>
        /// Context in which this binding is active;
        /// </summary>
        [JsonPropertyName("context")]
        [XmlElement("context")]
        public string Context { get; set; } = "Global";

        /// <summary>
        /// Priority for conflict resolution;
        /// </summary>
        [JsonPropertyName("priority")]
        [XmlElement("priority")]
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Whether the binding is currently enabled;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public bool IsEnabled;
        {
            get => _isEnabled;
            set;
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnEnabledChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Input sensitivity (for analog inputs)
        /// </summary>
        [JsonPropertyName("sensitivity")]
        [XmlElement("sensitivity")]
        public float Sensitivity;
        {
            get => _sensitivity;
            set => _sensitivity = Math.Clamp(value, 0.1f, 5.0f);
        }

        /// <summary>
        /// Dead zone for analog inputs;
        /// </summary>
        [JsonPropertyName("deadZone")]
        [XmlElement("deadZone")]
        public float DeadZone;
        {
            get => _deadZone;
            set => _deadZone = Math.Clamp(value, 0.0f, 0.9f);
        }

        /// <summary>
        /// Invert axis for analog inputs;
        /// </summary>
        [JsonPropertyName("invertAxis")]
        [XmlElement("invertAxis")]
        public bool InvertAxis { get; set; }

        /// <summary>
        /// Input value curve for analog inputs;
        /// </summary>
        [JsonPropertyName("inputCurve")]
        [XmlElement("inputCurve")]
        public InputCurve InputCurve { get; set; } = InputCurve.Linear;

        /// <summary>
        /// Binding type (press, hold, toggle, axis)
        /// </summary>
        [JsonPropertyName("bindingType")]
        [XmlElement("bindingType")]
        public BindingType BindingType { get; set; } = BindingType.Press;

        /// <summary>
        /// Hold duration required for hold-type bindings (seconds)
        /// </summary>
        [JsonPropertyName("holdDuration")]
        [XmlElement("holdDuration")]
        public float HoldDuration { get; set; } = 0.5f;

        /// <summary>
        /// Cooldown between activations (seconds)
        /// </summary>
        [JsonPropertyName("cooldown")]
        [XmlElement("cooldown")]
        public float Cooldown { get; set; } = 0.1f;

        /// <summary>
        /// Time when this binding was last activated;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public DateTime LastActivationTime { get; private set; }

        /// <summary>
        /// Current activation state;
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public bool IsActive { get; private set; }

        /// <summary>
        /// Current analog value (for axis bindings)
        /// </summary>
        [JsonIgnore]
        [XmlIgnore]
        public float AnalogValue { get; private set; }

        /// <summary>
        /// Event triggered when binding is activated;
        /// </summary>
        public event EventHandler<KeyBindingEventArgs> Activated;

        /// <summary>
        /// Event triggered when binding is deactivated;
        /// </summary>
        public event EventHandler<KeyBindingEventArgs> Deactivated;

        /// <summary>
        /// Event triggered when binding value changes (for analog)
        /// </summary>
        public event EventHandler<KeyBindingValueEventArgs> ValueChanged;

        /// <summary>
        /// Event triggered when binding is enabled/disabled;
        /// </summary>
        public event EventHandler EnabledChanged;

        public KeyBinding()
        {
            Id = Guid.NewGuid().ToString();
            Name = "New Binding";
        }

        public KeyBinding(string id, string name, InputKey primaryKey) : this()
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            PrimaryKey = primaryKey;
        }

        /// <summary>
        /// Checks if the binding matches the given input;
        /// </summary>
        public bool MatchesInput(InputState inputState, string currentContext = "Global")
        {
            if (!IsEnabled || !IsContextActive(currentContext))
                return false;

            // Check cooldown;
            if ((DateTime.UtcNow - LastActivationTime).TotalSeconds < Cooldown)
                return false;

            // Check modifiers;
            if (!CheckModifiers(inputState))
                return false;

            // Check based on device type;
            return DeviceType switch;
            {
                InputDeviceType.Keyboard => CheckKeyboardInput(inputState),
                InputDeviceType.Mouse => CheckMouseInput(inputState),
                InputDeviceType.Gamepad => CheckGamepadInput(inputState),
                InputDeviceType.Touch => CheckTouchInput(inputState),
                _ => false;
            };
        }

        /// <summary>
        /// Updates the binding state with current input;
        /// </summary>
        public void Update(InputState inputState, string currentContext = "Global", float deltaTime = 0.016f)
        {
            if (!IsEnabled || !IsContextActive(currentContext))
                return;

            bool wasActive = IsActive;
            float previousValue = AnalogValue;

            switch (BindingType)
            {
                case BindingType.Press:
                    UpdatePressBinding(inputState, deltaTime);
                    break;
                case BindingType.Hold:
                    UpdateHoldBinding(inputState, deltaTime);
                    break;
                case BindingType.Toggle:
                    UpdateToggleBinding(inputState, deltaTime);
                    break;
                case BindingType.Axis:
                    UpdateAxisBinding(inputState, deltaTime);
                    break;
                case BindingType.Charge:
                    UpdateChargeBinding(inputState, deltaTime);
                    break;
            }

            // Trigger events if state changed;
            if (wasActive != IsActive)
            {
                if (IsActive)
                {
                    LastActivationTime = DateTime.UtcNow;
                    OnActivated(new KeyBindingEventArgs(this, inputState));
                }
                else;
                {
                    OnDeactivated(new KeyBindingEventArgs(this, inputState));
                }
            }

            // Trigger value change event for analog bindings;
            if (BindingType == BindingType.Axis && Math.Abs(previousValue - AnalogValue) > 0.001f)
            {
                OnValueChanged(new KeyBindingValueEventArgs(this, AnalogValue, previousValue));
            }
        }

        /// <summary>
        /// Resets the binding to its default state;
        /// </summary>
        public void Reset()
        {
            IsActive = false;
            AnalogValue = 0f;
            LastActivationTime = DateTime.MinValue;
        }

        /// <summary>
        /// Gets the display string for this binding;
        /// </summary>
        public string GetDisplayString(bool includeModifiers = true)
        {
            var parts = new List<string>();

            if (includeModifiers && Modifiers.Count > 0)
            {
                parts.AddRange(Modifiers.Select(m => m.GetDisplayName()));
            }

            switch (DeviceType)
            {
                case InputDeviceType.Keyboard:
                    if (PrimaryKey != InputKey.None)
                        parts.Add(PrimaryKey.GetDisplayName());
                    break;
                case InputDeviceType.Mouse:
                    if (MouseButton != MouseButton.None)
                        parts.Add(MouseButton.GetDisplayName());
                    else if (MouseWheelDirection != MouseWheelDirection.None)
                        parts.Add(MouseWheelDirection.GetDisplayName());
                    break;
                case InputDeviceType.Gamepad:
                    if (GamepadButton != GamepadButton.None)
                        parts.Add(GamepadButton.GetDisplayName());
                    break;
            }

            return string.Join(" + ", parts);
        }

        /// <summary>
        /// Checks if this binding conflicts with another binding;
        /// </summary>
        public bool ConflictsWith(KeyBinding other, bool checkContext = true)
        {
            if (other == null) return false;
            if (checkContext && Context != other.Context) return false;

            // Check if they share the same primary input;
            if (DeviceType == other.DeviceType)
            {
                return DeviceType switch;
                {
                    InputDeviceType.Keyboard => PrimaryKey == other.PrimaryKey &&
                                                Modifiers.SequenceEqual(other.Modifiers),
                    InputDeviceType.Mouse => MouseButton == other.MouseButton ||
                                            MouseWheelDirection == other.MouseWheelDirection,
                    InputDeviceType.Gamepad => GamepadButton == other.GamepadButton,
                    _ => false;
                };
            }

            return false;
        }

        /// <summary>
        /// Creates a deep copy of this key binding;
        /// </summary>
        public KeyBinding Clone()
        {
            var clone = (KeyBinding)MemberwiseClone();
            clone.Modifiers = new List<ModifierKey>(Modifiers);
            clone.Id = Guid.NewGuid().ToString();

            // Clear event subscriptions;
            clone.Activated = null;
            clone.Deactivated = null;
            clone.ValueChanged = null;
            clone.EnabledChanged = null;

            return clone;
        }

        object ICloneable.Clone() => Clone();

        public bool Equals(KeyBinding other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return Id == other.Id &&
                   Name == other.Name &&
                   PrimaryKey == other.PrimaryKey &&
                   DeviceType == other.DeviceType &&
                   Modifiers.SequenceEqual(other.Modifiers) &&
                   Context == other.Context;
        }

        public override bool Equals(object obj) => Equals(obj as KeyBinding);

        public override int GetHashCode()
        {
            unchecked;
            {
                int hash = 17;
                hash = hash * 23 + Id.GetHashCode();
                hash = hash * 23 + Name.GetHashCode();
                hash = hash * 23 + PrimaryKey.GetHashCode();
                hash = hash * 23 + DeviceType.GetHashCode();
                hash = hash * 23 + Context.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"{Name} ({GetDisplayString()})";

        #region Private Methods;

        private bool IsContextActive(string currentContext)
        {
            return Context == "Global" || Context == currentContext;
        }

        private bool CheckModifiers(InputState inputState)
        {
            foreach (var modifier in Modifiers)
            {
                if (!inputState.IsModifierPressed(modifier))
                    return false;
            }
            return true;
        }

        private bool CheckKeyboardInput(InputState inputState)
        {
            if (PrimaryKey != InputKey.None && inputState.IsKeyPressed(PrimaryKey))
                return true;

            if (AlternateKey != InputKey.None && inputState.IsKeyPressed(AlternateKey))
                return true;

            return false;
        }

        private bool CheckMouseInput(InputState inputState)
        {
            if (MouseButton != MouseButton.None && inputState.IsMouseButtonPressed(MouseButton))
                return true;

            if (MouseWheelDirection != MouseWheelDirection.None)
            {
                var wheelDelta = inputState.MouseWheelDelta;
                return MouseWheelDirection switch;
                {
                    MouseWheelDirection.Up => wheelDelta > 0,
                    MouseWheelDirection.Down => wheelDelta < 0,
                    MouseWheelDirection.Left => inputState.MouseHorizontalWheelDelta < 0,
                    MouseWheelDirection.Right => inputState.MouseHorizontalWheelDelta > 0,
                    _ => false;
                };
            }

            return false;
        }

        private bool CheckGamepadInput(InputState inputState)
        {
            if (GamepadButton != GamepadButton.None && inputState.IsGamepadButtonPressed(GamepadButton))
                return true;

            return false;
        }

        private bool CheckTouchInput(InputState inputState)
        {
            // Touch input implementation would go here;
            return false;
        }

        private void UpdatePressBinding(InputState inputState, float deltaTime)
        {
            bool isPressed = MatchesInput(inputState);
            IsActive = isPressed;
        }

        private void UpdateHoldBinding(InputState inputState, float deltaTime)
        {
            bool isPressed = MatchesInput(inputState);

            if (isPressed)
            {
                _holdTimer += deltaTime;
                IsActive = _holdTimer >= HoldDuration;
            }
            else;
            {
                _holdTimer = 0f;
                IsActive = false;
            }
        }

        private void UpdateToggleBinding(InputState inputState, float deltaTime)
        {
            bool isPressed = MatchesInput(inputState);

            if (isPressed && !_wasPressedLastFrame)
            {
                IsActive = !IsActive;
            }

            _wasPressedLastFrame = isPressed;
        }

        private void UpdateAxisBinding(InputState inputState, float deltaTime)
        {
            float rawValue = 0f;

            switch (DeviceType)
            {
                case InputDeviceType.Gamepad:
                    rawValue = GetGamepadAxisValue(inputState);
                    break;
                case InputDeviceType.Mouse:
                    rawValue = GetMouseAxisValue(inputState);
                    break;
            }

            // Apply dead zone;
            if (Math.Abs(rawValue) < DeadZone)
                rawValue = 0f;

            // Apply sensitivity and curve;
            rawValue *= Sensitivity;
            rawValue = ApplyInputCurve(rawValue);

            // Invert if needed;
            if (InvertAxis)
                rawValue = -rawValue;

            AnalogValue = Math.Clamp(rawValue, -1f, 1f);
            IsActive = Math.Abs(AnalogValue) > 0.001f;
        }

        private void UpdateChargeBinding(InputState inputState, float deltaTime)
        {
            bool isPressed = MatchesInput(inputState);

            if (isPressed)
            {
                _chargeLevel = Math.Min(_chargeLevel + (deltaTime / HoldDuration), 1f);
                IsActive = true;
                AnalogValue = _chargeLevel;
            }
            else if (_chargeLevel > 0f)
            {
                // Trigger release;
                IsActive = false;
                _chargeLevel = 0f;
            }
        }

        private float GetGamepadAxisValue(InputState inputState)
        {
            // Map gamepad buttons/axes to values;
            // This is a simplified implementation;
            return 0f;
        }

        private float GetMouseAxisValue(InputState inputState)
        {
            // Map mouse movements to axis values;
            // This is a simplified implementation;
            return 0f;
        }

        private float ApplyInputCurve(float value)
        {
            return InputCurve switch;
            {
                InputCurve.Linear => value,
                InputCurve.Quadratic => Math.Sign(value) * value * value,
                InputCurve.Cubic => value * value * value,
                InputCurve.Exponential => Math.Sign(value) * (float)Math.Pow(Math.Abs(value), 1.5f),
                InputCurve.Logarithmic => Math.Sign(value) * (float)Math.Log(Math.Abs(value) + 1f),
                _ => value;
            };
        }

        private void OnActivated(KeyBindingEventArgs e)
        {
            Activated?.Invoke(this, e);
        }

        private void OnDeactivated(KeyBindingEventArgs e)
        {
            Deactivated?.Invoke(this, e);
        }

        private void OnValueChanged(KeyBindingValueEventArgs e)
        {
            ValueChanged?.Invoke(this, e);
        }

        #endregion;

        #region Private Fields;

        private float _holdTimer = 0f;
        private bool _wasPressedLastFrame = false;
        private float _chargeLevel = 0f;

        #endregion;
    }

    /// <summary>
    /// Key binding event arguments;
    /// </summary>
    public class KeyBindingEventArgs : EventArgs;
    {
        public KeyBinding Binding { get; }
        public InputState InputState { get; }
        public DateTime ActivationTime { get; }

        public KeyBindingEventArgs(KeyBinding binding, InputState inputState)
        {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            InputState = inputState ?? throw new ArgumentNullException(nameof(inputState));
            ActivationTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Key binding value event arguments (for analog bindings)
    /// </summary>
    public class KeyBindingValueEventArgs : EventArgs;
    {
        public KeyBinding Binding { get; }
        public float CurrentValue { get; }
        public float PreviousValue { get; }
        public float Delta { get; }

        public KeyBindingValueEventArgs(KeyBinding binding, float currentValue, float previousValue)
        {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            CurrentValue = currentValue;
            PreviousValue = previousValue;
            Delta = currentValue - previousValue;
        }
    }

    /// <summary>
    /// Input device types;
    /// </summary>
    public enum InputDeviceType;
    {
        Keyboard,
        Mouse,
        Gamepad,
        Touch,
        VRController,
        MotionSensor;
    }

    /// <summary>
    /// Binding types;
    /// </summary>
    public enum BindingType;
    {
        Press,      // Instant activation on press;
        Hold,       // Activation after holding for duration;
        Toggle,     // Toggle state on/off;
        Axis,       // Analog value input;
        Charge      // Charge and release;
    }

    /// <summary>
    /// Input curve types for analog inputs;
    /// </summary>
    public enum InputCurve;
    {
        Linear,
        Quadratic,
        Cubic,
        Exponential,
        Logarithmic;
    }

    /// <summary>
    /// Mouse wheel directions;
    /// </summary>
    public enum MouseWheelDirection;
    {
        None,
        Up,
        Down,
        Left,
        Right;
    }
}
