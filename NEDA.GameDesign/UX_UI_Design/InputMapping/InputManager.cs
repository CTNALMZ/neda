using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.GameDesign.UX_UI_Design.InputMapping.Configuration;
using NEDA.GameDesign.UX_UI_Design.InputMapping.DataModels;
using NEDA.GameDesign.UX_UI_Design.InputMapping.Devices;
using NEDA.GameDesign.UX_UI_Design.InputMapping.Events;
using NEDA.GameDesign.UX_UI_Design.InputMapping.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NEDA.GameDesign.UX_UI_Design.InputMapping;
{
    /// <summary>
    /// Kullanıcı girişlerini (klavye, fare, gamepad, dokunmatik) yöneten ana sınıf.
    /// Gelişmiş giriş eşleme, olay işleme ve durum yönetimi sağlar.
    /// </summary>
    public class InputManager : IInputManager, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IInputDeviceManager _deviceManager;
        private readonly IInputProfileManager _profileManager;
        private readonly IInputEventProcessor _eventProcessor;
        private readonly InputConfiguration _configuration;

        private readonly Dictionary<string, InputAction> _inputActions;
        private readonly Dictionary<string, InputAxis> _inputAxes;
        private readonly Dictionary<string, ControlScheme> _controlSchemes;
        private readonly Dictionary<InputDeviceType, IInputDevice> _activeDevices;

        private InputState _currentState;
        private InputContext _currentContext;
        private bool _isInitialized;
        private bool _isProcessing;
        private bool _isEnabled;
        private readonly object _processLock = new object();
        private DateTime _lastUpdateTime;

        /// <summary>
        /// Kayıtlı giriş aksiyonları;
        /// </summary>
        public IReadOnlyDictionary<string, InputAction> InputActions => _inputActions;

        /// <summary>
        /// Kayıtlı giriş eksenleri;
        /// </summary>
        public IReadOnlyDictionary<string, InputAxis> InputAxes => _inputAxes;

        /// <summary>
        /// Kontrol şemaları;
        /// </summary>
        public IReadOnlyDictionary<string, ControlScheme> ControlSchemes => _controlSchemes;

        /// <summary>
        /// Aktif giriş cihazları;
        /// </summary>
        public IReadOnlyDictionary<InputDeviceType, IInputDevice> ActiveDevices => _activeDevices;

        /// <summary>
        /// Mevcut giriş durumu;
        /// </summary>
        public InputState CurrentState => _currentState;

        /// <summary>
        /// Mevcut giriş bağlamı;
        /// </summary>
        public InputContext CurrentContext;
        {
            get => _currentContext;
            set;
            {
                if (_currentContext != value)
                {
                    var oldContext = _currentContext;
                    _currentContext = value;
                    OnInputContextChanged(new InputContextChangedEventArgs(oldContext, value));
                }
            }
        }

        /// <summary>
        /// Giriş istatistikleri;
        /// </summary>
        public InputStatistics Statistics { get; private set; }

        /// <summary>
        /// Giriş yönetimi etkin mi;
        /// </summary>
        public bool IsEnabled;
        {
            get => _isEnabled;
            set;
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnInputEnabledChanged(new InputEnabledEventArgs(value));
                }
            }
        }

        #endregion;

        #region Events;

        /// <summary>
        /// Giriş yöneticisi başlatıldığında tetiklenir;
        /// </summary>
        public event EventHandler<InputInitializedEventArgs> InputInitialized;

        /// <summary>
        /// Giriş bağlamı değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<InputContextChangedEventArgs> InputContextChanged;

        /// <summary>
        /// Giriş eylem tetiklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<InputActionEventArgs> ActionTriggered;

        /// <summary>
        /// Giriş eksen değeri değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<InputAxisEventArgs> AxisValueChanged;

        /// <summary>
        /// Kontrol şeması değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ControlSchemeChangedEventArgs> ControlSchemeChanged;

        /// <summary>
        /// Giriş cihazı bağlandığında tetiklenir;
        /// </summary>
        public event EventHandler<InputDeviceEventArgs> DeviceConnected;

        /// <summary>
        /// Giriş cihazı bağlantısı kesildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<InputDeviceEventArgs> DeviceDisconnected;

        /// <summary>
        /// Giriş etkin durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<InputEnabledEventArgs> InputEnabledChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// InputManager sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="deviceManager">Giriş cihazı yöneticisi</param>
        /// <param name="profileManager">Giriş profil yöneticisi</param>
        /// <param name="eventProcessor">Giriş olay işlemcisi</param>
        /// <param name="configuration">Giriş konfigürasyonu</param>
        public InputManager(
            ILogger logger,
            IInputDeviceManager deviceManager,
            IInputProfileManager profileManager,
            IInputEventProcessor eventProcessor,
            InputConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
            _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _inputActions = new Dictionary<string, InputAction>();
            _inputAxes = new Dictionary<string, InputAxis>();
            _controlSchemes = new Dictionary<string, ControlScheme>();
            _activeDevices = new Dictionary<InputDeviceType, IInputDevice>();

            Initialize();
        }

        #endregion;

        #region Initialization;

        private void Initialize()
        {
            try
            {
                _logger.LogInformation("InputManager başlatılıyor...");

                _currentState = new InputState();
                _currentContext = InputContext.Default;
                Statistics = new InputStatistics();
                _lastUpdateTime = DateTime.UtcNow;

                // Konfigürasyon validasyonu;
                ValidateConfiguration(_configuration);

                // Cihaz yöneticisini başlat;
                InitializeDeviceManager();

                // Profil yöneticisini başlat;
                InitializeProfileManager();

                // Olay işlemcisini başlat;
                InitializeEventProcessor();

                // Varsayılan kontrol şemalarını yükle;
                LoadDefaultControlSchemes();

                // Varsayılan giriş aksiyonlarını yükle;
                LoadDefaultInputActions();

                _isInitialized = true;
                IsEnabled = true;

                OnInputInitialized(new InputInitializedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    DeviceCount = _activeDevices.Count,
                    ActionCount = _inputActions.Count,
                    SchemeCount = _controlSchemes.Count;
                });

                _logger.LogInformation("InputManager başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InputManager başlatma sırasında hata oluştu.");
                throw new InputManagerException("InputManager başlatılamadı.", ex);
            }
        }

        private void ValidateConfiguration(InputConfiguration config)
        {
            if (config.PollingInterval <= 0)
                throw new ArgumentException("Polling interval pozitif olmalıdır.");

            if (config.DeadZone < 0 || config.DeadZone > 1)
                throw new ArgumentException("Dead zone değeri 0-1 arasında olmalıdır.");

            if (config.Sensitivity <= 0)
                throw new ArgumentException("Sensitivity pozitif olmalıdır.");
        }

        private void InitializeDeviceManager()
        {
            _deviceManager.Initialize(_configuration.DeviceSettings);

            // Cihaz olaylarını dinle;
            _deviceManager.DeviceConnected += OnDeviceConnected;
            _deviceManager.DeviceDisconnected += OnDeviceDisconnected;

            // Aktif cihazları al;
            foreach (var device in _deviceManager.GetConnectedDevices())
            {
                _activeDevices[device.DeviceType] = device;
            }
        }

        private void InitializeProfileManager()
        {
            _profileManager.Initialize(_configuration.ProfileSettings);

            // Aktif profili yükle;
            var activeProfile = _profileManager.LoadActiveProfile();
            if (activeProfile != null)
            {
                ApplyProfile(activeProfile);
            }
        }

        private void InitializeEventProcessor()
        {
            _eventProcessor.Initialize(_configuration.EventProcessingSettings);

            // Olay işlemcisini başlat;
            _eventProcessor.StartProcessing();
        }

        private void LoadDefaultControlSchemes()
        {
            // Varsayılan kontrol şemaları;
            var schemes = new[]
            {
                CreateDefaultKeyboardMouseScheme(),
                CreateDefaultGamepadScheme(),
                CreateDefaultTouchScheme()
            };

            foreach (var scheme in schemes)
            {
                _controlSchemes.Add(scheme.Id, scheme);
            }
        }

        private ControlScheme CreateDefaultKeyboardMouseScheme()
        {
            return new ControlScheme;
            {
                Id = "KeyboardMouse_Default",
                Name = "Keyboard & Mouse",
                Description = "Varsayılan klavye ve fare kontrol şeması",
                DeviceType = InputDeviceType.Keyboard | InputDeviceType.Mouse,
                Bindings = new Dictionary<string, InputBinding>
                {
                    // Hareket;
                    { "MoveForward", new InputBinding(Key.W, KeyBindingType.Hold) },
                    { "MoveBackward", new InputBinding(Key.S, KeyBindingType.Hold) },
                    { "MoveLeft", new InputBinding(Key.A, KeyBindingType.Hold) },
                    { "MoveRight", new InputBinding(Key.D, KeyBindingType.Hold) },
                    { "Jump", new InputBinding(Key.Space, KeyBindingType.Press) },
                    { "Crouch", new InputBinding(Key.C, KeyBindingType.Toggle) },
                    
                    // Bakış;
                    { "LookX", new InputBinding(MouseAxis.MouseX, AxisBindingType.Relative) },
                    { "LookY", new InputBinding(MouseAxis.MouseY, AxisBindingType.Relative) },
                    
                    // Eylemler;
                    { "PrimaryAction", new InputBinding(MouseButton.Left, KeyBindingType.Press) },
                    { "SecondaryAction", new InputBinding(MouseButton.Right, KeyBindingType.Press) },
                    { "Interact", new InputBinding(Key.E, KeyBindingType.Press) },
                    
                    // UI;
                    { "Pause", new InputBinding(Key.Escape, KeyBindingType.Press) },
                    { "Inventory", new InputBinding(Key.Tab, KeyBindingType.Toggle) }
                }
            };
        }

        private ControlScheme CreateDefaultGamepadScheme()
        {
            return new ControlScheme;
            {
                Id = "Gamepad_Xbox",
                Name = "Xbox Controller",
                Description = "Varsayılan Xbox kontrolcü şeması",
                DeviceType = InputDeviceType.Gamepad,
                Bindings = new Dictionary<string, InputBinding>
                {
                    // Hareket;
                    { "MoveForward", new InputBinding(GamepadAxis.LeftStickY, AxisBindingType.Absolute) },
                    { "MoveBackward", new InputBinding(GamepadAxis.LeftStickY, AxisBindingType.Absolute) },
                    { "MoveLeft", new InputBinding(GamepadAxis.LeftStickX, AxisBindingType.Absolute) },
                    { "MoveRight", new InputBinding(GamepadAxis.LeftStickX, AxisBindingType.Absolute) },
                    
                    // Bakış;
                    { "LookX", new InputBinding(GamepadAxis.RightStickX, AxisBindingType.Absolute) },
                    { "LookY", new InputBinding(GamepadAxis.RightStickY, AxisBindingType.Absolute) },
                    
                    // Eylemler;
                    { "Jump", new InputBinding(GamepadButton.A, KeyBindingType.Press) },
                    { "Crouch", new InputBinding(GamepadButton.B, KeyBindingType.Toggle) },
                    { "PrimaryAction", new InputBinding(GamepadButton.RightTrigger, KeyBindingType.Analog) },
                    { "SecondaryAction", new InputBinding(GamepadButton.LeftTrigger, KeyBindingType.Analog) },
                    { "Interact", new InputBinding(GamepadButton.X, KeyBindingType.Press) },
                    
                    // UI;
                    { "Pause", new InputBinding(GamepadButton.Start, KeyBindingType.Press) },
                    { "Inventory", new InputBinding(GamepadButton.Y, KeyBindingType.Toggle) }
                }
            };
        }

        private ControlScheme CreateDefaultTouchScheme()
        {
            return new ControlScheme;
            {
                Id = "Touch_Default",
                Name = "Touch Controls",
                Description = "Varsayılan dokunmatik kontrol şeması",
                DeviceType = InputDeviceType.Touch,
                Bindings = new Dictionary<string, InputBinding>
                {
                    // Hareket;
                    { "MoveJoystick", new InputBinding(TouchZone.LeftHalf, AxisBindingType.VirtualJoystick) },
                    
                    // Bakış;
                    { "LookJoystick", new InputBinding(TouchZone.RightHalf, AxisBindingType.VirtualJoystick) },
                    
                    // Eylemler;
                    { "PrimaryAction", new InputBinding(TouchButton.VirtualButton1, KeyBindingType.Press) },
                    { "SecondaryAction", new InputBinding(TouchButton.VirtualButton2, KeyBindingType.Press) },
                    { "Jump", new InputBinding(TouchButton.VirtualButton3, KeyBindingType.Press) }
                }
            };
        }

        private void LoadDefaultInputActions()
        {
            // Varsayılan giriş aksiyonları;
            var actions = new[]
            {
                new InputAction;
                {
                    Id = "Move",
                    Name = "Movement",
                    Description = "Karakter hareketi",
                    ActionType = InputActionType.Vector2,
                    DefaultValue = new Vector2(0, 0)
                },
                new InputAction;
                {
                    Id = "Look",
                    Name = "Camera Look",
                    Description = "Kamera bakışı",
                    ActionType = InputActionType.Vector2,
                    DefaultValue = new Vector2(0, 0)
                },
                new InputAction;
                {
                    Id = "Jump",
                    Name = "Jump",
                    Description = "Zıplama",
                    ActionType = InputActionType.Button,
                    DefaultValue = 0.0f;
                },
                new InputAction;
                {
                    Id = "PrimaryAction",
                    Name = "Primary Action",
                    Description = "Birincil eylem",
                    ActionType = InputActionType.Button,
                    DefaultValue = 0.0f;
                },
                new InputAction;
                {
                    Id = "SecondaryAction",
                    Name = "Secondary Action",
                    Description = "İkincil eylem",
                    ActionType = InputActionType.Button,
                    DefaultValue = 0.0f;
                },
                new InputAction;
                {
                    Id = "Interact",
                    Name = "Interact",
                    Description = "Etkileşim",
                    ActionType = InputActionType.Button,
                    DefaultValue = 0.0f;
                },
                new InputAction;
                {
                    Id = "Pause",
                    Name = "Pause Game",
                    Description = "Oyunu duraklat",
                    ActionType = InputActionType.Button,
                    DefaultValue = 0.0f;
                }
            };

            foreach (var action in actions)
            {
                _inputActions.Add(action.Id, action);
            }

            // Varsayılan giriş eksenleri;
            var axes = new[]
            {
                new InputAxis;
                {
                    Id = "Horizontal",
                    Name = "Horizontal Axis",
                    Description = "Yatay eksen",
                    AxisType = AxisType.OneDimensional,
                    MinValue = -1.0f,
                    MaxValue = 1.0f,
                    DefaultValue = 0.0f,
                    DeadZone = 0.1f,
                    Sensitivity = 1.0f;
                },
                new InputAxis;
                {
                    Id = "Vertical",
                    Name = "Vertical Axis",
                    Description = "Dikey eksen",
                    AxisType = AxisType.OneDimensional,
                    MinValue = -1.0f,
                    MaxValue = 1.0f,
                    DefaultValue = 0.0f,
                    DeadZone = 0.1f,
                    Sensitivity = 1.0f;
                },
                new InputAxis;
                {
                    Id = "MouseX",
                    Name = "Mouse X",
                    Description = "Fare X ekseni",
                    AxisType = AxisType.OneDimensional,
                    MinValue = float.MinValue,
                    MaxValue = float.MaxValue,
                    DefaultValue = 0.0f,
                    DeadZone = 0.0f,
                    Sensitivity = 1.0f;
                },
                new InputAxis;
                {
                    Id = "MouseY",
                    Name = "Mouse Y",
                    Description = "Fare Y ekseni",
                    AxisType = AxisType.OneDimensional,
                    MinValue = float.MinValue,
                    MaxValue = float.MaxValue,
                    DefaultValue = 0.0f,
                    DeadZone = 0.0f,
                    Sensitivity = 1.0f;
                }
            };

            foreach (var axis in axes)
            {
                _inputAxes.Add(axis.Id, axis);
            }
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Girişleri günceller;
        /// </summary>
        public void Update()
        {
            if (!_isInitialized || !_isEnabled)
                return;

            if (_isProcessing)
                return;

            lock (_processLock)
            {
                if (_isProcessing)
                    return;

                _isProcessing = true;
            }

            try
            {
                var currentTime = DateTime.UtcNow;
                var deltaTime = (float)(currentTime - _lastUpdateTime).TotalSeconds;

                // Cihaz durumlarını güncelle;
                UpdateDeviceStates(deltaTime);

                // Giriş durumunu güncelle;
                UpdateInputState();

                // Giriş aksiyonlarını işle;
                ProcessInputActions();

                // Giriş eksenlerini işle;
                ProcessInputAxes();

                // Olayları işle;
                ProcessInputEvents();

                // İstatistikleri güncelle;
                UpdateStatistics(deltaTime);

                _lastUpdateTime = currentTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Giriş güncelleme sırasında hata oluştu.");
            }
            finally
            {
                lock (_processLock)
                {
                    _isProcessing = false;
                }
            }
        }

        /// <summary>
        /// Giriş aksiyonu kaydeder;
        /// </summary>
        /// <param name="action">Giriş aksiyonu</param>
        public void RegisterInputAction(InputAction action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            if (_inputActions.ContainsKey(action.Id))
                throw new InputManagerException($"Giriş aksiyonu zaten kayıtlı: {action.Id}");

            _inputActions.Add(action.Id, action);
            _logger.LogInformation($"Giriş aksiyonu kaydedildi: {action.Id}");
        }

        /// <summary>
        /// Giriş ekseni kaydeder;
        /// </summary>
        /// <param name="axis">Giriş ekseni</param>
        public void RegisterInputAxis(InputAxis axis)
        {
            if (axis == null)
                throw new ArgumentNullException(nameof(axis));

            if (_inputAxes.ContainsKey(axis.Id))
                throw new InputManagerException($"Giriş ekseni zaten kayıtlı: {axis.Id}");

            _inputAxes.Add(axis.Id, axis);
            _logger.LogInformation($"Giriş ekseni kaydedildi: {axis.Id}");
        }

        /// <summary>
        /// Kontrol şeması ekler;
        /// </summary>
        /// <param name="scheme">Kontrol şeması</param>
        public void AddControlScheme(ControlScheme scheme)
        {
            if (scheme == null)
                throw new ArgumentNullException(nameof(scheme));

            if (_controlSchemes.ContainsKey(scheme.Id))
                throw new InputManagerException($"Kontrol şeması zaten mevcut: {scheme.Id}");

            _controlSchemes.Add(scheme.Id, scheme);
            _logger.LogInformation($"Kontrol şeması eklendi: {scheme.Id}");
        }

        /// <summary>
        /// Aktif kontrol şemasını değiştirir;
        /// </summary>
        /// <param name="schemeId">Kontrol şeması ID</param>
        public void SetActiveControlScheme(string schemeId)
        {
            if (!_controlSchemes.ContainsKey(schemeId))
                throw new InputManagerException($"Kontrol şeması bulunamadı: {schemeId}");

            var oldScheme = _currentState.ActiveControlScheme;
            var newScheme = _controlSchemes[schemeId];

            _currentState.ActiveControlScheme = newScheme;

            OnControlSchemeChanged(new ControlSchemeChangedEventArgs(oldScheme, newScheme));

            _logger.LogInformation($"Aktif kontrol şeması değiştirildi: {schemeId}");
        }

        /// <summary>
        /// Giriş bağlamını değiştirir;
        /// </summary>
        /// <param name="contextId">Giriş bağlamı ID</param>
        public void ChangeInputContext(string contextId)
        {
            var context = InputContext.GetContext(contextId);
            if (context == null)
                throw new InputManagerException($"Giriş bağlamı bulunamadı: {contextId}");

            CurrentContext = context;

            _logger.LogInformation($"Giriş bağlamı değiştirildi: {contextId}");
        }

        /// <summary>
        /// Giriş aksiyonu tetiklenip tetiklenmediğini kontrol eder;
        /// </summary>
        /// <param name="actionId">Giriş aksiyonu ID</param>
        /// <returns>Aksiyon durumu</returns>
        public InputActionState GetActionState(string actionId)
        {
            if (!_inputActions.TryGetValue(actionId, out var action))
                return InputActionState.Released;

            return _currentState.GetActionState(actionId);
        }

        /// <summary>
        /// Giriş aksiyonunun değerini alır;
        /// </summary>
        /// <param name="actionId">Giriş aksiyonu ID</param>
        /// <returns>Aksiyon değeri</returns>
        public float GetActionValue(string actionId)
        {
            if (!_inputActions.TryGetValue(actionId, out var action))
                return 0.0f;

            return _currentState.GetActionValue(actionId);
        }

        /// <summary>
        /// Giriş ekseninin değerini alır;
        /// </summary>
        /// <param name="axisId">Giriş ekseni ID</param>
        /// <returns>Eksen değeri</returns>
        public float GetAxisValue(string axisId)
        {
            if (!_inputAxes.TryGetValue(axisId, out var axis))
                return 0.0f;

            return _currentState.GetAxisValue(axisId);
        }

        /// <summary>
        /// Giriş bağlamasını yapılandırır;
        /// </summary>
        /// <param name="actionId">Giriş aksiyonu ID</param>
        /// <param name="binding">Giriş bağlama</param>
        /// <param name="schemeId">Kontrol şeması ID (opsiyonel)</param>
        public void ConfigureBinding(string actionId, InputBinding binding, string schemeId = null)
        {
            if (!_inputActions.ContainsKey(actionId))
                throw new InputManagerException($"Giriş aksiyonu bulunamadı: {actionId}");

            var targetScheme = schemeId != null && _controlSchemes.ContainsKey(schemeId)
                ? _controlSchemes[schemeId]
                : _currentState.ActiveControlScheme;

            if (targetScheme == null)
                throw new InputManagerException("Aktif kontrol şeması bulunamadı.");

            targetScheme.Bindings[actionId] = binding;

            _logger.LogInformation($"Giriş bağlaması yapılandırıldı: {actionId} -> {binding}");
        }

        /// <summary>
        /// Giriş profilini yükler;
        /// </summary>
        /// <param name="profileName">Profil adı</param>
        public void LoadProfile(string profileName)
        {
            try
            {
                var profile = _profileManager.LoadProfile(profileName);
                if (profile == null)
                    throw new InputManagerException($"Profil bulunamadı: {profileName}");

                ApplyProfile(profile);

                _logger.LogInformation($"Giriş profili yüklendi: {profileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Profil yükleme sırasında hata oluştu: {profileName}");
                throw new InputManagerException($"Profil yüklenemedi: {profileName}", ex);
            }
        }

        /// <summary>
        /// Giriş profilini kaydeder;
        /// </summary>
        /// <param name="profileName">Profil adı</param>
        public void SaveProfile(string profileName)
        {
            try
            {
                var profile = CreateProfileFromCurrentState(profileName);
                _profileManager.SaveProfile(profile);

                _logger.LogInformation($"Giriş profili kaydedildi: {profileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Profil kaydetme sırasında hata oluştu: {profileName}");
                throw new InputManagerException($"Profil kaydedilemedi: {profileName}", ex);
            }
        }

        /// <summary>
        /// Girişi sıfırlar;
        /// </summary>
        public void ResetInput()
        {
            _currentState.Reset();
            _logger.LogDebug("Giriş durumu sıfırlandı.");
        }

        /// <summary>
        /// Giriş titreşimi uygular (gamepad için)
        /// </summary>
        /// <param name="vibrationParams">Titreşim parametreleri</param>
        public void ApplyVibration(VibrationParameters vibrationParams)
        {
            if (!_isEnabled)
                return;

            try
            {
                foreach (var device in _activeDevices.Values)
                {
                    if (device is IGamepadDevice gamepad)
                    {
                        gamepad.SetVibration(vibrationParams);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Titreşim uygulama sırasında hata oluştu.");
            }
        }

        /// <summary>
        /// Giriş titreşimini durdurur;
        /// </summary>
        public void StopVibration()
        {
            try
            {
                foreach (var device in _activeDevices.Values)
                {
                    if (device is IGamepadDevice gamepad)
                    {
                        gamepad.StopVibration();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Titreşim durdurma sırasında hata oluştu.");
            }
        }

        /// <summary>
        /// Giriş verilerini dışa aktarır;
        /// </summary>
        /// <param name="exportType">Dışa aktarma türü</param>
        /// <returns>Dışa aktarılan veriler</returns>
        public async Task<InputExportData> ExportInputDataAsync(InputExportType exportType)
        {
            try
            {
                _logger.LogInformation($"Giriş verileri dışa aktarılıyor. Tip: {exportType}");

                var exportData = new InputExportData;
                {
                    ExportId = Guid.NewGuid().ToString(),
                    ExportType = exportType,
                    ExportTime = DateTime.UtcNow;
                };

                switch (exportType)
                {
                    case InputExportType.JSON:
                        exportData.Data = await ExportToJSONAsync();
                        break;
                    case InputExportType.XML:
                        exportData.Data = await ExportToXMLAsync();
                        break;
                    case InputExportType.Binary:
                        exportData.Data = await ExportToBinaryAsync();
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen export tipi: {exportType}");
                }

                exportData.Success = true;
                exportData.Message = "Giriş verileri başarıyla dışa aktarıldı.";

                _logger.LogInformation($"Giriş verileri dışa aktarıldı. ID: {exportData.ExportId}");

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Giriş verileri dışa aktarma sırasında hata oluştu.");
                throw new InputManagerException("Giriş verileri dışa aktarılamadı.", ex);
            }
        }

        /// <summary>
        /// Giriş cihazlarını tarar;
        /// </summary>
        public void ScanDevices()
        {
            try
            {
                _deviceManager.ScanDevices();
                _logger.LogDebug("Giriş cihazları taranıyor...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cihaz tarama sırasında hata oluştu.");
            }
        }

        #endregion;

        #region Private Methods;

        private void UpdateDeviceStates(float deltaTime)
        {
            foreach (var device in _activeDevices.Values)
            {
                device.Update(deltaTime);
            }
        }

        private void UpdateInputState()
        {
            // Aktif cihazlardan giriş durumunu al;
            var deviceStates = new Dictionary<InputDeviceType, DeviceInputState>();

            foreach (var device in _activeDevices.Values)
            {
                var state = device.GetState();
                deviceStates[device.DeviceType] = state;
            }

            // Giriş durumunu güncelle;
            _currentState.Update(deviceStates, _currentContext);
        }

        private void ProcessInputActions()
        {
            foreach (var action in _inputActions.Values)
            {
                var oldState = _currentState.GetActionState(action.Id);
                var newState = _currentState.GetActionState(action.Id);
                var value = _currentState.GetActionValue(action.Id);

                // Durum değişikliklerini kontrol et;
                if (oldState != newState)
                {
                    var eventArgs = new InputActionEventArgs;
                    {
                        ActionId = action.Id,
                        ActionType = action.ActionType,
                        State = newState,
                        Value = value,
                        Timestamp = DateTime.UtcNow,
                        Context = _currentContext;
                    };

                    OnActionTriggered(eventArgs);

                    // Logging;
                    if (_configuration.LogInputEvents)
                    {
                        _logger.LogDebug($"Giriş aksiyonu: {action.Id} -> {newState} (Value: {value})");
                    }
                }
            }
        }

        private void ProcessInputAxes()
        {
            foreach (var axis in _inputAxes.Values)
            {
                var oldValue = _currentState.GetPreviousAxisValue(axis.Id);
                var newValue = _currentState.GetAxisValue(axis.Id);

                // Ölü bölge uygula;
                newValue = ApplyDeadZone(newValue, axis.DeadZone);

                // Hassasiyet uygula;
                newValue *= axis.Sensitivity;

                // Değer değişikliklerini kontrol et;
                if (Math.Abs(oldValue - newValue) > 0.001f)
                {
                    var eventArgs = new InputAxisEventArgs;
                    {
                        AxisId = axis.Id,
                        AxisType = axis.AxisType,
                        Value = newValue,
                        Delta = newValue - oldValue,
                        Timestamp = DateTime.UtcNow,
                        Context = _currentContext;
                    };

                    OnAxisValueChanged(eventArgs);

                    // Logging;
                    if (_configuration.LogInputEvents)
                    {
                        _logger.LogDebug($"Giriş ekseni: {axis.Id} -> {newValue:F3}");
                    }
                }
            }
        }

        private float ApplyDeadZone(float value, float deadZone)
        {
            if (Math.Abs(value) < deadZone)
                return 0.0f;

            return value;
        }

        private void ProcessInputEvents()
        {
            // Cihazlardan olayları topla;
            var inputEvents = new List<InputEvent>();

            foreach (var device in _activeDevices.Values)
            {
                var events = device.GetEvents();
                inputEvents.AddRange(events);
            }

            // Olayları işle;
            foreach (var inputEvent in inputEvents)
            {
                _eventProcessor.ProcessEvent(inputEvent);
            }
        }

        private void ApplyProfile(InputProfile profile)
        {
            // Kontrol şemalarını uygula;
            foreach (var scheme in profile.ControlSchemes)
            {
                if (_controlSchemes.ContainsKey(scheme.Id))
                {
                    _controlSchemes[scheme.Id] = scheme;
                }
                else;
                {
                    _controlSchemes.Add(scheme.Id, scheme);
                }
            }

            // Aktif kontrol şemasını ayarla;
            if (!string.IsNullOrEmpty(profile.ActiveSchemeId) &&
                _controlSchemes.ContainsKey(profile.ActiveSchemeId))
            {
                SetActiveControlScheme(profile.ActiveSchemeId);
            }

            // Giriş bağlamını ayarla;
            if (!string.IsNullOrEmpty(profile.DefaultContextId))
            {
                ChangeInputContext(profile.DefaultContextId);
            }
        }

        private InputProfile CreateProfileFromCurrentState(string profileName)
        {
            return new InputProfile;
            {
                Id = Guid.NewGuid().ToString(),
                Name = profileName,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ActiveSchemeId = _currentState.ActiveControlScheme?.Id,
                DefaultContextId = _currentContext.Id,
                ControlSchemes = _controlSchemes.Values.ToList(),
                UserSettings = new InputUserSettings;
                {
                    MouseSensitivity = _configuration.Sensitivity,
                    InvertMouseY = _configuration.InvertYAxis,
                    VibrationEnabled = _configuration.EnableVibration;
                }
            };
        }

        private void UpdateStatistics(float deltaTime)
        {
            Statistics = new InputStatistics;
            {
                UpdateCount = Statistics.UpdateCount + 1,
                EventCount = _eventProcessor.ProcessedEventCount,
                ActiveDeviceCount = _activeDevices.Count,
                ActiveActionCount = _inputActions.Count(a =>
                    _currentState.GetActionState(a.Key) != InputActionState.Released),
                FrameRate = 1.0f / deltaTime,
                LastUpdate = DateTime.UtcNow;
            };
        }

        private async Task<byte[]> ExportToJSONAsync()
        {
            return await Task.Run(() =>
            {
                var exportModel = new InputExportModel;
                {
                    Profile = CreateProfileFromCurrentState("Export"),
                    Actions = _inputActions.Values.ToList(),
                    Axes = _inputAxes.Values.ToList(),
                    Statistics = Statistics,
                    ExportTime = DateTime.UtcNow;
                };

                var json = System.Text.Json.JsonSerializer.Serialize(exportModel, new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = true;
                });

                return System.Text.Encoding.UTF8.GetBytes(json);
            });
        }

        private async Task<byte[]> ExportToXMLAsync()
        {
            return await Task.Run(() =>
            {
                // XML formatında dışa aktar;
                var xml = new System.Xml.Serialization.XmlSerializer(typeof(InputExportModel));
                using var stream = new System.IO.MemoryStream();
                xml.Serialize(stream, new InputExportModel;
                {
                    Profile = CreateProfileFromCurrentState("Export"),
                    Actions = _inputActions.Values.ToList(),
                    Axes = _inputAxes.Values.ToList(),
                    ExportTime = DateTime.UtcNow;
                });

                return stream.ToArray();
            });
        }

        private async Task<byte[]> ExportToBinaryAsync()
        {
            return await Task.Run(() =>
            {
                // Binary formatında dışa aktar;
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(stream);

                writer.Write(_inputActions.Count);
                foreach (var action in _inputActions.Values)
                {
                    writer.Write(action.Id);
                    writer.Write((int)action.ActionType);
                }

                writer.Write(_inputAxes.Count);
                foreach (var axis in _inputAxes.Values)
                {
                    writer.Write(axis.Id);
                    writer.Write(axis.MinValue);
                    writer.Write(axis.MaxValue);
                }

                return stream.ToArray();
            });
        }

        private void OnDeviceConnected(object sender, InputDeviceEventArgs e)
        {
            _activeDevices[e.Device.DeviceType] = e.Device;

            // DeviceConnected event'ini tetikle;
            DeviceConnected?.Invoke(this, e);

            _logger.LogInformation($"Giriş cihazı bağlandı: {e.Device.DeviceType} - {e.Device.Name}");
        }

        private void OnDeviceDisconnected(object sender, InputDeviceEventArgs e)
        {
            _activeDevices.Remove(e.Device.DeviceType);

            // DeviceDisconnected event'ini tetikle;
            DeviceDisconnected?.Invoke(this, e);

            _logger.LogInformation($"Giriş cihazı bağlantısı kesildi: {e.Device.DeviceType} - {e.Device.Name}");
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnInputInitialized(InputInitializedEventArgs e)
        {
            InputInitialized?.Invoke(this, e);
        }

        protected virtual void OnInputContextChanged(InputContextChangedEventArgs e)
        {
            InputContextChanged?.Invoke(this, e);
        }

        protected virtual void OnActionTriggered(InputActionEventArgs e)
        {
            ActionTriggered?.Invoke(this, e);
        }

        protected virtual void OnAxisValueChanged(InputAxisEventArgs e)
        {
            AxisValueChanged?.Invoke(this, e);
        }

        protected virtual void OnControlSchemeChanged(ControlSchemeChangedEventArgs e)
        {
            ControlSchemeChanged?.Invoke(this, e);
        }

        protected virtual void OnInputEnabledChanged(InputEnabledEventArgs e)
        {
            InputEnabledChanged?.Invoke(this, e);
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
                    // Yönetilen kaynakları serbest bırak;
                    StopVibration();

                    // Olay bağlantılarını kopar;
                    _deviceManager.DeviceConnected -= OnDeviceConnected;
                    _deviceManager.DeviceDisconnected -= OnDeviceDisconnected;

                    // Alt sistemleri dispose et;
                    (_deviceManager as IDisposable)?.Dispose();
                    (_profileManager as IDisposable)?.Dispose();
                    (_eventProcessor as IDisposable)?.Dispose();

                    // Aktif cihazları temizle;
                    foreach (var device in _activeDevices.Values)
                    {
                        (device as IDisposable)?.Dispose();
                    }
                    _activeDevices.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~InputManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IInputManager : IDisposable
    {
        void Update();
        void RegisterInputAction(InputAction action);
        void RegisterInputAxis(InputAxis axis);
        void AddControlScheme(ControlScheme scheme);
        void SetActiveControlScheme(string schemeId);
        void ChangeInputContext(string contextId);
        void ConfigureBinding(string actionId, InputBinding binding, string schemeId = null);
        void LoadProfile(string profileName);
        void SaveProfile(string profileName);
        void ResetInput();
        void ApplyVibration(VibrationParameters vibrationParams);
        void StopVibration();
        Task<InputExportData> ExportInputDataAsync(InputExportType exportType);
        void ScanDevices();

        InputActionState GetActionState(string actionId);
        float GetActionValue(string actionId);
        float GetAxisValue(string axisId);

        IReadOnlyDictionary<string, InputAction> InputActions { get; }
        IReadOnlyDictionary<string, InputAxis> InputAxes { get; }
        IReadOnlyDictionary<string, ControlScheme> ControlSchemes { get; }
        IReadOnlyDictionary<InputDeviceType, IInputDevice> ActiveDevices { get; }
        InputState CurrentState { get; }
        InputContext CurrentContext { get; }
        InputStatistics Statistics { get; }
        bool IsEnabled { get; set; }

        event EventHandler<InputInitializedEventArgs> InputInitialized;
        event EventHandler<InputContextChangedEventArgs> InputContextChanged;
        event EventHandler<InputActionEventArgs> ActionTriggered;
        event EventHandler<InputAxisEventArgs> AxisValueChanged;
        event EventHandler<ControlSchemeChangedEventArgs> ControlSchemeChanged;
        event EventHandler<InputDeviceEventArgs> DeviceConnected;
        event EventHandler<InputDeviceEventArgs> DeviceDisconnected;
        event EventHandler<InputEnabledEventArgs> InputEnabledChanged;
    }

    public interface IGamepadDevice : IInputDevice;
    {
        void SetVibration(VibrationParameters parameters);
        void StopVibration();
    }

    [Flags]
    public enum InputDeviceType;
    {
        None = 0,
        Keyboard = 1,
        Mouse = 2,
        Gamepad = 4,
        Touch = 8,
        VR = 16,
        Motion = 32,
        All = Keyboard | Mouse | Gamepad | Touch | VR | Motion;
    }

    public enum InputActionType;
    {
        Button,
        Vector2,
        Vector3;
    }

    public enum InputActionState;
    {
        Released,
        Pressed,
        Held,
        Repeated,
        Toggled;
    }

    public enum AxisType;
    {
        OneDimensional,
        TwoDimensional,
        ThreeDimensional;
    }

    public enum InputExportType;
    {
        JSON,
        XML,
        Binary;
    }

    public class InputInitializedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public int DeviceCount { get; set; }
        public int ActionCount { get; set; }
        public int SchemeCount { get; set; }
    }

    public class InputContextChangedEventArgs : EventArgs;
    {
        public InputContext PreviousContext { get; }
        public InputContext NewContext { get; }

        public InputContextChangedEventArgs(InputContext previousContext, InputContext newContext)
        {
            PreviousContext = previousContext;
            NewContext = newContext;
        }
    }

    public class InputActionEventArgs : EventArgs;
    {
        public string ActionId { get; set; }
        public InputActionType ActionType { get; set; }
        public InputActionState State { get; set; }
        public float Value { get; set; }
        public DateTime Timestamp { get; set; }
        public InputContext Context { get; set; }
    }

    public class InputAxisEventArgs : EventArgs;
    {
        public string AxisId { get; set; }
        public AxisType AxisType { get; set; }
        public float Value { get; set; }
        public float Delta { get; set; }
        public DateTime Timestamp { get; set; }
        public InputContext Context { get; set; }
    }

    public class ControlSchemeChangedEventArgs : EventArgs;
    {
        public ControlScheme PreviousScheme { get; }
        public ControlScheme NewScheme { get; }

        public ControlSchemeChangedEventArgs(ControlScheme previousScheme, ControlScheme newScheme)
        {
            PreviousScheme = previousScheme;
            NewScheme = newScheme;
        }
    }

    public class InputDeviceEventArgs : EventArgs;
    {
        public IInputDevice Device { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class InputEnabledEventArgs : EventArgs;
    {
        public bool IsEnabled { get; }

        public InputEnabledEventArgs(bool isEnabled)
        {
            IsEnabled = isEnabled;
        }
    }

    public class InputExportData;
    {
        public string ExportId { get; set; }
        public InputExportType ExportType { get; set; }
        public byte[] Data { get; set; }
        public DateTime ExportTime { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class InputExportModel;
    {
        public InputProfile Profile { get; set; }
        public List<InputAction> Actions { get; set; }
        public List<InputAxis> Axes { get; set; }
        public InputStatistics Statistics { get; set; }
        public DateTime ExportTime { get; set; }
    }

    public class InputManagerException : Exception
    {
        public InputManagerException(string message) : base(message) { }
        public InputManagerException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    // Basit vektör sınıfı;
    public struct Vector2;
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static Vector2 Zero => new Vector2(0, 0);
        public static Vector2 One => new Vector2(1, 1);

        public float Magnitude => (float)Math.Sqrt(X * X + Y * Y);

        public Vector2 Normalized()
        {
            var mag = Magnitude;
            return mag > 0 ? new Vector2(X / mag, Y / mag) : this;
        }
    }

    #endregion;
}
