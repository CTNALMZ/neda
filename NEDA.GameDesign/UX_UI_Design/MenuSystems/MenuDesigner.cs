using NEDA.AI.ComputerVision;
using NEDA.Animation.SequenceEditor.KeyframeEditing;
using NEDA.Animation.SequenceEditor.TimelineManagement;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.GameDesign.UX_UI_Design.HUD_Elements;
using NEDA.GameDesign.UX_UI_Design.InputMapping;
using NEDA.GameDesign.UX_UI_Design.InterfaceDesign;
using NEDA.Services.ProjectService;
using NEDA.UI.Controls;
using NEDA.UI.Themes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NEDA.GameDesign.UX_UI_Design.MenuSystems;
{
    /// <summary>
    /// Menu Designer - Manages menu creation, navigation, and interaction systems;
    /// </summary>
    public class MenuDesigner : IDisposable, INotifyPropertyChanged;
    {
        #region Properties and Fields;

        private readonly ILogger _logger;
        private readonly ThemeManager _themeManager;
        private readonly ProjectManager _projectManager;
        private readonly LayoutManager _layoutManager;
        private readonly DisplaySystem _displaySystem;
        private readonly ControlScheme _controlScheme;
        private readonly Timeline _timeline;
        private readonly CurveEditor _curveEditor;

        private readonly Dictionary<string, Menu> _menus;
        private readonly Dictionary<string, MenuItem> _menuItems;
        private readonly Dictionary<string, MenuTemplate> _templates;
        private readonly Dictionary<string, MenuAnimation> _animations;

        private MenuConfiguration _configuration;
        private MenuMetrics _metrics;
        private MenuState _currentState;
        private NavigationMode _navigationMode;
        private string _activeMenuId;
        private string _focusedItemId;
        private bool _isInitialized;
        private readonly object _syncLock = new object();
        private readonly Queue<MenuCommand> _commandQueue;

        private Menu _activeMenu;
        private DateTime _lastUpdateTime;
        private TimeSpan _totalUptime;
        private MenuStatistics _currentStatistics;
        private readonly Stack<MenuHistoryEntry> _navigationHistory;

        // Property change notifications;
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Current menu state;
        /// </summary>
        public MenuState CurrentState;
        {
            get => _currentState;
            private set;
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    OnPropertyChanged();
                    OnMenuStateChanged(new MenuStateEventArgs;
                    {
                        PreviousState = _currentState,
                        NewState = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Current navigation mode;
        /// </summary>
        public NavigationMode NavigationMode;
        {
            get => _navigationMode;
            set;
            {
                if (_navigationMode != value)
                {
                    _navigationMode = value;
                    OnPropertyChanged();
                    OnNavigationModeChanged(new NavigationModeEventArgs;
                    {
                        NavigationMode = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Active menu;
        /// </summary>
        public Menu ActiveMenu;
        {
            get => _activeMenu;
            set;
            {
                if (_activeMenu != value)
                {
                    _activeMenu = value;
                    OnPropertyChanged();
                    OnActiveMenuChanged(new MenuEventArgs;
                    {
                        Menu = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Currently focused menu item;
        /// </summary>
        public MenuItem FocusedItem;
        {
            get => _focusedItemId != null && _menuItems.TryGetValue(_focusedItemId, out var item) ? item : null;
            private set;
            {
                var newId = value?.Id;
                if (_focusedItemId != newId)
                {
                    var previousItem = FocusedItem;
                    _focusedItemId = newId;
                    OnPropertyChanged();
                    OnFocusChanged(new FocusChangedEventArgs;
                    {
                        PreviousItem = previousItem,
                        CurrentItem = FocusedItem,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Collection of available menus;
        /// </summary>
        public ReadOnlyDictionary<string, Menu> Menus => new ReadOnlyDictionary<string, Menu>(_menus);

        /// <summary>
        /// Collection of menu items;
        /// </summary>
        public ReadOnlyDictionary<string, MenuItem> MenuItems => new ReadOnlyDictionary<string, MenuItem>(_menuItems);

        /// <summary>
        /// Collection of menu templates;
        /// </summary>
        public ReadOnlyDictionary<string, MenuTemplate> Templates => new ReadOnlyDictionary<string, MenuTemplate>(_templates);

        /// <summary>
        /// Navigation history stack;
        /// </summary>
        public ReadOnlyCollection<MenuHistoryEntry> NavigationHistory => new ReadOnlyCollection<MenuHistoryEntry>(_navigationHistory.ToList());

        /// <summary>
        /// Total system uptime;
        /// </summary>
        public TimeSpan TotalUptime => _totalUptime;

        /// <summary>
        /// Gets the total number of registered menus;
        /// </summary>
        public int TotalMenuCount => _menus.Count;

        /// <summary>
        /// Gets the total number of menu items;
        /// </summary>
        public int TotalMenuItemCount => _menuItems.Count;

        /// <summary>
        /// Gets the total number of active templates;
        /// </summary>
        public int TotalTemplateCount => _templates.Count;

        /// <summary>
        /// Menu metrics and statistics;
        /// </summary>
        public MenuMetrics Metrics => _metrics;

        /// <summary>
        /// Current menu statistics;
        /// </summary>
        public MenuStatistics CurrentStatistics => _currentStatistics;

        /// <summary>
        /// Gets the current navigation depth;
        /// </summary>
        public int NavigationDepth => _navigationHistory.Count;

        #endregion;

        #region Events;

        public event EventHandler<MenuStateEventArgs> MenuStateChanged;
        public event EventHandler<NavigationModeEventArgs> NavigationModeChanged;
        public event EventHandler<MenuEventArgs> ActiveMenuChanged;
        public event EventHandler<FocusChangedEventArgs> FocusChanged;
        public event EventHandler<MenuEventArgs> MenuCreated;
        public event EventHandler<MenuEventArgs> MenuRemoved;
        public event EventHandler<MenuItemEventArgs> MenuItemAdded;
        public event EventHandler<MenuItemEventArgs> MenuItemRemoved;
        public event EventHandler<MenuItemEventArgs> MenuItemSelected;
        public event EventHandler<NavigationEventArgs> NavigationPerformed;
        public event EventHandler<MenuErrorEventArgs> MenuError;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of MenuDesigner;
        /// </summary>
        public MenuDesigner(
            ILogger logger,
            ThemeManager themeManager,
            ProjectManager projectManager,
            LayoutManager layoutManager,
            DisplaySystem displaySystem,
            ControlScheme controlScheme,
            Timeline timeline,
            CurveEditor curveEditor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            _layoutManager = layoutManager ?? throw new ArgumentNullException(nameof(layoutManager));
            _displaySystem = displaySystem ?? throw new ArgumentNullException(nameof(displaySystem));
            _controlScheme = controlScheme ?? throw new ArgumentNullException(nameof(controlScheme));
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _curveEditor = curveEditor ?? throw new ArgumentNullException(nameof(curveEditor));

            _menus = new Dictionary<string, Menu>();
            _menuItems = new Dictionary<string, MenuItem>();
            _templates = new Dictionary<string, MenuTemplate>();
            _animations = new Dictionary<string, MenuAnimation>();
            _navigationHistory = new Stack<MenuHistoryEntry>();

            _commandQueue = new Queue<MenuCommand>();
            _configuration = new MenuConfiguration();
            _metrics = new MenuMetrics();
            _currentState = MenuState.Uninitialized;
            _navigationMode = NavigationMode.Keyboard;
            _activeMenuId = null;
            _focusedItemId = null;
            _isInitialized = false;

            _activeMenu = null;
            _lastUpdateTime = DateTime.UtcNow;
            _totalUptime = TimeSpan.Zero;
            _currentStatistics = new MenuStatistics();

            InitializeEventHandlers();
            _logger.LogInformation("MenuDesigner initialized successfully.");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the menu designer with configuration;
        /// </summary>
        public async Task InitializeAsync(MenuConfiguration configuration)
        {
            ValidateConfiguration(configuration);

            try
            {
                CurrentState = MenuState.Initializing;

                _configuration = configuration;
                _navigationMode = configuration.DefaultNavigationMode;

                // Initialize default menus;
                await InitializeDefaultMenusAsync();

                // Load user menus if available;
                await LoadUserMenusAsync();

                // Initialize default templates;
                await InitializeDefaultTemplatesAsync();

                // Register input actions for menu navigation;
                await RegisterNavigationActionsAsync();

                // Start update loop;
                StartUpdateLoop();

                _isInitialized = true;
                CurrentState = MenuState.Ready;

                _logger.LogInformation($"MenuDesigner initialized with navigation mode: {_navigationMode}");
            }
            catch (Exception ex)
            {
                CurrentState = MenuState.Error;
                _logger.LogError(ex, "Failed to initialize MenuDesigner");
                throw new MenuDesignerException("Failed to initialize MenuDesigner", ex);
            }
        }

        /// <summary>
        /// Creates a new menu;
        /// </summary>
        public async Task<Menu> CreateMenuAsync(string menuId, MenuType type, MenuConfiguration config)
        {
            ValidateMenuId(menuId);
            ValidateMenuConfiguration(config);

            try
            {
                if (_menus.ContainsKey(menuId))
                    throw new InvalidOperationException($"Menu with ID '{menuId}' already exists");

                var menu = new Menu;
                {
                    Id = menuId,
                    Name = config.Name,
                    Type = type,
                    Configuration = config,
                    LayoutType = config.LayoutType,
                    NavigationStyle = config.NavigationStyle,
                    IsVisible = config.IsVisible,
                    IsModal = config.IsModal,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                    Items = new Dictionary<string, MenuItem>(),
                    Animations = new Dictionary<string, MenuAnimation>()
                };

                // Apply theme to menu;
                await ApplyThemeToMenuAsync(menu);

                lock (_syncLock)
                {
                    _menus.Add(menuId, menu);
                }

                UpdateMetrics();

                OnMenuCreated(new MenuEventArgs;
                {
                    Menu = menu,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Menu created: {menuId} ({type})");
                return menu;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create menu: {menuId}");
                throw new MenuDesignerException($"Failed to create menu: {menuId}", ex);
            }
        }

        /// <summary>
        /// Removes a menu;
        /// </summary>
        public async Task<bool> RemoveMenuAsync(string menuId)
        {
            ValidateMenuId(menuId);

            try
            {
                if (!_menus.TryGetValue(menuId, out var menu))
                    return false;

                // Remove all menu items;
                var itemsToRemove = menu.Items.Keys.ToList();
                foreach (var itemId in itemsToRemove)
                {
                    await RemoveMenuItemAsync(menuId, itemId);
                }

                // If this is the active menu, deactivate it;
                if (_activeMenuId == menuId)
                {
                    await DeactivateMenuAsync();
                }

                lock (_syncLock)
                {
                    _menus.Remove(menuId);
                }

                UpdateMetrics();

                OnMenuRemoved(new MenuEventArgs;
                {
                    Menu = menu,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Menu removed: {menuId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove menu: {menuId}");
                throw new MenuDesignerException($"Failed to remove menu: {menuId}", ex);
            }
        }

        /// <summary>
        /// Activates a menu;
        /// </summary>
        public async Task ActivateMenuAsync(string menuId, ActivationMode mode = ActivationMode.Standard)
        {
            ValidateMenuId(menuId);

            try
            {
                if (!_menus.TryGetValue(menuId, out var menu))
                    throw new KeyNotFoundException($"Menu not found: {menuId}");

                // Deactivate current menu if any;
                if (_activeMenu != null)
                {
                    await DeactivateMenuAsync();
                }

                // Add to navigation history if not already there;
                if (mode != ActivationMode.Replace)
                {
                    _navigationHistory.Push(new MenuHistoryEntry
                    {
                        MenuId = menuId,
                        Timestamp = DateTime.UtcNow,
                        ActivationMode = mode;
                    });
                }

                // Set as active;
                _activeMenuId = menuId;
                ActiveMenu = menu;
                menu.IsActive = true;
                menu.LastActivated = DateTime.UtcNow;

                // Show menu;
                await ShowMenuAsync(menu);

                // Focus first item if auto-focus is enabled;
                if (menu.Configuration.AutoFocus && menu.Items.Any())
                {
                    var firstItem = menu.Items.Values.OrderBy(i => i.Order).First();
                    await FocusItemAsync(firstItem.Id);
                }

                _logger.LogInformation($"Menu activated: {menuId} (Mode: {mode})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to activate menu: {menuId}");
                throw new MenuDesignerException($"Failed to activate menu: {menuId}", ex);
            }
        }

        /// <summary>
        /// Deactivates the current menu;
        /// </summary>
        public async Task DeactivateMenuAsync()
        {
            if (_activeMenu == null)
                return;

            try
            {
                var menu = _activeMenu;

                // Hide menu;
                await HideMenuAsync(menu);

                // Reset active state;
                menu.IsActive = false;
                menu.LastDeactivated = DateTime.UtcNow;

                _activeMenuId = null;
                ActiveMenu = null;
                FocusedItem = null;

                _logger.LogInformation($"Menu deactivated: {menu.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate menu");
                throw new MenuDesignerException("Failed to deactivate menu", ex);
            }
        }

        /// <summary>
        /// Navigates back in menu history;
        /// </summary>
        public async Task<bool> NavigateBackAsync()
        {
            if (_navigationHistory.Count <= 1)
                return false;

            try
            {
                // Remove current menu from history;
                _navigationHistory.Pop();

                // Get previous menu;
                if (_navigationHistory.TryPeek(out var previousEntry))
                {
                    await ActivateMenuAsync(previousEntry.MenuId, ActivationMode.History);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to navigate back");
                throw new MenuDesignerException("Failed to navigate back", ex);
            }
        }

        /// <summary>
        /// Clears navigation history;
        /// </summary>
        public void ClearNavigationHistory()
        {
            _navigationHistory.Clear();
            _logger.LogInformation("Navigation history cleared");
        }

        /// <summary>
        /// Adds a menu item to a menu;
        /// </summary>
        public async Task<MenuItem> AddMenuItemAsync(string menuId, MenuItem item)
        {
            ValidateMenuId(menuId);
            ValidateMenuItem(item);

            try
            {
                if (!_menus.TryGetValue(menuId, out var menu))
                    throw new KeyNotFoundException($"Menu not found: {menuId}");

                if (menu.Items.ContainsKey(item.Id))
                    throw new InvalidOperationException($"Menu item with ID '{item.Id}' already exists in menu '{menuId}'");

                if (_menuItems.ContainsKey(item.Id))
                    throw new InvalidOperationException($"Menu item with ID '{item.Id}' already exists");

                // Set menu reference;
                item.MenuId = menuId;
                item.CreatedTime = DateTime.UtcNow;
                item.LastModified = DateTime.UtcNow;

                // Apply theme to item;
                await ApplyThemeToMenuItemAsync(item);

                lock (_syncLock)
                {
                    _menuItems.Add(item.Id, item);
                    menu.Items.Add(item.Id, item);
                    menu.ModifiedDate = DateTime.UtcNow;
                }

                // Create display element for the item;
                await CreateMenuItemDisplayAsync(item);

                UpdateMetrics();

                OnMenuItemAdded(new MenuItemEventArgs;
                {
                    MenuItem = item,
                    Menu = menu,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Menu item added: {item.Id} to menu {menuId}");
                return item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add menu item: {item.Id}");
                throw new MenuDesignerException($"Failed to add menu item: {item.Id}", ex);
            }
        }

        /// <summary>
        /// Removes a menu item from a menu;
        /// </summary>
        public async Task<bool> RemoveMenuItemAsync(string menuId, string itemId)
        {
            ValidateMenuId(menuId);
            ValidateMenuItemId(itemId);

            try
            {
                if (!_menus.TryGetValue(menuId, out var menu))
                    return false;

                if (!menu.Items.TryGetValue(itemId, out var item))
                    return false;

                // Remove from focus if focused;
                if (FocusedItem?.Id == itemId)
                {
                    await MoveFocusAsync(FocusDirection.Next);
                }

                // Remove display element;
                await RemoveMenuItemDisplayAsync(item);

                lock (_syncLock)
                {
                    menu.Items.Remove(itemId);
                    _menuItems.Remove(itemId);
                    menu.ModifiedDate = DateTime.UtcNow;
                }

                UpdateMetrics();

                OnMenuItemRemoved(new MenuItemEventArgs;
                {
                    MenuItem = item,
                    Menu = menu,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Menu item removed: {itemId} from menu {menuId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove menu item: {itemId}");
                throw new MenuDesignerException($"Failed to remove menu item: {itemId}", ex);
            }
        }

        /// <summary>
        /// Updates a menu item;
        /// </summary>
        public async Task UpdateMenuItemAsync(string itemId, MenuItemUpdate update)
        {
            ValidateMenuItemId(itemId);
            ValidateMenuItemUpdate(update);

            try
            {
                if (!_menuItems.TryGetValue(itemId, out var item))
                    throw new KeyNotFoundException($"Menu item not found: {itemId}");

                // Apply updates;
                if (update.Label != null)
                    item.Label = update.Label;

                if (update.Description != null)
                    item.Description = update.Description;

                if (update.IsEnabled.HasValue)
                    item.IsEnabled = update.IsEnabled.Value;

                if (update.IsVisible.HasValue)
                    item.IsVisible = update.IsVisible.Value;

                if (update.Order.HasValue)
                    item.Order = update.Order.Value;

                if (update.Style != null)
                    item.Style = update.Style;

                if (update.Action != null)
                    item.Action = update.Action;

                item.LastModified = DateTime.UtcNow;

                // Update display element;
                await UpdateMenuItemDisplayAsync(item);

                OnMenuItemSelected(new MenuItemEventArgs;
                {
                    MenuItem = item,
                    Menu = _menus.TryGetValue(item.MenuId, out var menu) ? menu : null,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Menu item updated: {itemId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update menu item: {itemId}");
                throw new MenuDesignerException($"Failed to update menu item: {itemId}", ex);
            }
        }

        /// <summary>
        /// Focuses a menu item;
        /// </summary>
        public async Task FocusItemAsync(string itemId)
        {
            ValidateMenuItemId(itemId);

            try
            {
                if (!_menuItems.TryGetValue(itemId, out var item))
                    throw new KeyNotFoundException($"Menu item not found: {itemId}");

                if (!item.IsEnabled || !item.IsVisible)
                    throw new InvalidOperationException($"Menu item is not focusable: {itemId}");

                // Remove focus from current item;
                var previousItem = FocusedItem;
                if (previousItem != null)
                {
                    await ApplyItemFocusStyleAsync(previousItem, false);
                }

                // Set focus to new item;
                FocusedItem = item;
                await ApplyItemFocusStyleAsync(item, true);

                _logger.LogDebug($"Focus moved to item: {itemId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to focus item: {itemId}");
                throw new MenuDesignerException($"Failed to focus item: {itemId}", ex);
            }
        }

        /// <summary>
        /// Moves focus in a direction;
        /// </summary>
        public async Task<bool> MoveFocusAsync(FocusDirection direction)
        {
            if (_activeMenu == null || !_activeMenu.Items.Any())
                return false;

            try
            {
                var currentItem = FocusedItem;
                var menuItems = _activeMenu.Items.Values;
                    .Where(i => i.IsEnabled && i.IsVisible)
                    .OrderBy(i => i.Order)
                    .ToList();

                if (!menuItems.Any())
                    return false;

                // If no item is focused, focus the first one;
                if (currentItem == null)
                {
                    await FocusItemAsync(menuItems[0].Id);
                    return true;
                }

                // Find current index;
                int currentIndex = menuItems.FindIndex(i => i.Id == currentItem.Id);
                if (currentIndex == -1)
                {
                    await FocusItemAsync(menuItems[0].Id);
                    return true;
                }

                // Calculate new index based on direction and layout;
                int newIndex = CalculateNewFocusIndex(currentIndex, direction, menuItems);

                if (newIndex != currentIndex && newIndex >= 0 && newIndex < menuItems.Count)
                {
                    await FocusItemAsync(menuItems[newIndex].Id);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to move focus: {direction}");
                throw new MenuDesignerException($"Failed to move focus: {direction}", ex);
            }
        }

        /// <summary>
        /// Selects the currently focused menu item;
        /// </summary>
        public async Task<bool> SelectFocusedItemAsync()
        {
            if (FocusedItem == null)
                return false;

            try
            {
                var item = FocusedItem;

                // Execute item action;
                await ExecuteMenuItemActionAsync(item);

                // Update statistics;
                _currentStatistics.ItemsSelected++;

                OnMenuItemSelected(new MenuItemEventArgs;
                {
                    MenuItem = item,
                    Menu = _menus.TryGetValue(item.MenuId, out var menu) ? menu : null,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Menu item selected: {item.Id}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to select focused item");
                throw new MenuDesignerException("Failed to select focused item", ex);
            }
        }

        /// <summary>
        /// Creates a menu template;
        /// </summary>
        public async Task<MenuTemplate> CreateTemplateAsync(string templateName, TemplateType type)
        {
            ValidateTemplateName(templateName);

            try
            {
                if (_templates.ContainsKey(templateName))
                    throw new InvalidOperationException($"Template with name '{templateName}' already exists");

                var template = new MenuTemplate;
                {
                    Name = templateName,
                    Type = type,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                    Menus = new Dictionary<string, Menu>(),
                    MenuItems = new Dictionary<string, MenuItem>(),
                    Animations = new Dictionary<string, MenuAnimation>()
                };

                // Copy current menu structure if creating from current state;
                if (type == TemplateType.FromCurrent)
                {
                    foreach (var menu in _menus)
                    {
                        template.Menus.Add(menu.Key, menu.Value.Clone());
                    }

                    foreach (var menuItem in _menuItems)
                    {
                        template.MenuItems.Add(menuItem.Key, menuItem.Value.Clone());
                    }
                }

                lock (_syncLock)
                {
                    _templates.Add(templateName, template);
                }

                _logger.LogInformation($"Template created: {templateName} ({type})");
                return template;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create template: {templateName}");
                throw new MenuDesignerException($"Failed to create template: {templateName}", ex);
            }
        }

        /// <summary>
        /// Applies a menu template;
        /// </summary>
        public async Task ApplyTemplateAsync(string templateName)
        {
            ValidateTemplateName(templateName);

            try
            {
                if (!_templates.TryGetValue(templateName, out var template))
                    throw new KeyNotFoundException($"Template not found: {templateName}");

                // Clear current menus;
                foreach (var menuId in _menus.Keys.ToList())
                {
                    await RemoveMenuAsync(menuId);
                }

                // Apply template menus;
                foreach (var menu in template.Menus.Values)
                {
                    await CreateMenuAsync(menu.Id, menu.Type, menu.Configuration);

                    // Add menu items;
                    var templateItems = template.MenuItems.Values;
                        .Where(i => i.MenuId == menu.Id);

                    foreach (var item in templateItems)
                    {
                        await AddMenuItemAsync(menu.Id, item.Clone());
                    }
                }

                _logger.LogInformation($"Template applied: {templateName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply template: {templateName}");
                throw new MenuDesignerException($"Failed to apply template: {templateName}", ex);
            }
        }

        /// <summary>
        /// Shows or hides a menu;
        /// </summary>
        public async Task SetMenuVisibilityAsync(string menuId, bool isVisible)
        {
            ValidateMenuId(menuId);

            try
            {
                if (!_menus.TryGetValue(menuId, out var menu))
                    throw new KeyNotFoundException($"Menu not found: {menuId}");

                if (menu.IsVisible == isVisible)
                    return;

                menu.IsVisible = isVisible;

                if (isVisible)
                {
                    await ShowMenuAsync(menu);
                }
                else;
                {
                    await HideMenuAsync(menu);
                }

                _logger.LogInformation($"Menu visibility changed: {menuId} -> {isVisible}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set menu visibility: {menuId}");
                throw new MenuDesignerException($"Failed to set menu visibility: {menuId}", ex);
            }
        }

        /// <summary>
        /// Gets menu items by type;
        /// </summary>
        public IEnumerable<MenuItem> GetMenuItemsByType(string menuId, MenuItemType type)
        {
            ValidateMenuId(menuId);

            if (!_menus.TryGetValue(menuId, out var menu))
                throw new KeyNotFoundException($"Menu not found: {menuId}");

            return menu.Items.Values.Where(i => i.Type == type);
        }

        /// <summary>
        /// Gets menu items by state;
        /// </summary>
        public IEnumerable<MenuItem> GetMenuItemsByState(string menuId, bool isEnabled, bool isVisible)
        {
            ValidateMenuId(menuId);

            if (!_menus.TryGetValue(menuId, out var menu))
                throw new KeyNotFoundException($"Menu not found: {menuId}");

            return menu.Items.Values.Where(i => i.IsEnabled == isEnabled && i.IsVisible == isVisible);
        }

        /// <summary>
        /// Updates the menu designer (to be called each frame)
        /// </summary>
        public async Task UpdateAsync(TimeSpan deltaTime)
        {
            if (!_isInitialized || CurrentState == MenuState.Error)
                return;

            try
            {
                _totalUptime += deltaTime;

                // Process command queue;
                await ProcessCommandQueueAsync();

                // Update menu animations;
                await UpdateAnimationsAsync(deltaTime);

                // Update input handling;
                await UpdateInputHandlingAsync(deltaTime);

                // Update statistics;
                UpdateStatistics(deltaTime);

                // Update metrics;
                UpdateMetrics();

                _lastUpdateTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during menu designer update");
                OnMenuError(new MenuErrorEventArgs;
                {
                    ErrorMessage = "Update error",
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Renders the active menu;
        /// </summary>
        public async Task RenderAsync()
        {
            if (!_isInitialized || CurrentState != MenuState.Active)
                return;

            try
            {
                if (_activeMenu != null && _activeMenu.IsVisible)
                {
                    // Render menu background;
                    await RenderMenuBackgroundAsync(_activeMenu);

                    // Render menu items;
                    foreach (var item in _activeMenu.Items.Values.Where(i => i.IsVisible))
                    {
                        await RenderMenuItemAsync(item);
                    }

                    // Render focus indicator;
                    if (FocusedItem != null)
                    {
                        await RenderFocusIndicatorAsync(FocusedItem);
                    }

                    _currentStatistics.FramesRendered++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during menu render");
                OnMenuError(new MenuErrorEventArgs;
                {
                    ErrorMessage = "Render error",
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Gets menu designer statistics;
        /// </summary>
        public MenuDesignerStatistics GetStatistics()
        {
            return new MenuDesignerStatistics;
            {
                TotalMenus = _menus.Count,
                TotalMenuItems = _menuItems.Count,
                TotalTemplates = _templates.Count,
                Uptime = _totalUptime,
                State = CurrentState,
                NavigationMode = NavigationMode,
                NavigationDepth = NavigationDepth,
                ActiveMenuId = _activeMenuId,
                FocusedItemId = _focusedItemId,
                Metrics = _metrics,
                MenuStatistics = _currentStatistics,
                LastUpdate = _lastUpdateTime;
            };
        }

        /// <summary>
        /// Resets menu designer to default state;
        /// </summary>
        public async Task ResetToDefaultsAsync()
        {
            try
            {
                CurrentState = MenuState.Resetting;

                // Clear all data;
                foreach (var menuId in _menus.Keys.ToList())
                {
                    await RemoveMenuAsync(menuId);
                }

                // Clear navigation history;
                ClearNavigationHistory();

                // Re-initialize defaults;
                await InitializeDefaultMenusAsync();

                CurrentState = MenuState.Ready;

                _logger.LogInformation("Menu designer reset to defaults");
            }
            catch (Exception ex)
            {
                CurrentState = MenuState.Error;
                _logger.LogError(ex, "Failed to reset menu designer");
                throw new MenuDesignerException("Failed to reset menu designer", ex);
            }
        }

        #endregion;

        #region Private Methods;

        private async Task InitializeDefaultMenusAsync()
        {
            // Create default main menu;
            var mainMenu = await CreateMenuAsync("main_menu", MenuType.Main, new MenuConfiguration;
            {
                Name = "Main Menu",
                LayoutType = MenuLayoutType.VerticalList,
                NavigationStyle = NavigationStyle.Linear,
                IsVisible = true,
                IsModal = false,
                AutoFocus = true,
                BackgroundStyle = new MenuStyle;
                {
                    BackgroundColor = Color.FromHex("#1a1a1a"),
                    BorderColor = Color.FromHex("#333333"),
                    BorderWidth = 2;
                }
            });

            // Add default menu items;
            var menuItems = new[]
            {
                new MenuItem;
                {
                    Id = "start_game",
                    Label = "Start Game",
                    Type = MenuItemType.Button,
                    Order = 1,
                    IsEnabled = true,
                    IsVisible = true,
                    Action = new MenuItemAction;
                    {
                        Type = ActionType.Navigate,
                        Target = "game_menu",
                        Parameters = new Dictionary<string, object>()
                    }
                },
                new MenuItem;
                {
                    Id = "options",
                    Label = "Options",
                    Type = MenuItemType.Button,
                    Order = 2,
                    IsEnabled = true,
                    IsVisible = true,
                    Action = new MenuItemAction;
                    {
                        Type = ActionType.Navigate,
                        Target = "options_menu",
                        Parameters = new Dictionary<string, object>()
                    }
                },
                new MenuItem;
                {
                    Id = "credits",
                    Label = "Credits",
                    Type = MenuItemType.Button,
                    Order = 3,
                    IsEnabled = true,
                    IsVisible = true,
                    Action = new MenuItemAction;
                    {
                        Type = ActionType.Navigate,
                        Target = "credits_menu",
                        Parameters = new Dictionary<string, object>()
                    }
                },
                new MenuItem;
                {
                    Id = "exit",
                    Label = "Exit",
                    Type = MenuItemType.Button,
                    Order = 4,
                    IsEnabled = true,
                    IsVisible = true,
                    Action = new MenuItemAction;
                    {
                        Type = ActionType.Quit,
                        Parameters = new Dictionary<string, object>()
                    }
                }
            };

            foreach (var item in menuItems)
            {
                await AddMenuItemAsync("main_menu", item);
            }

            _logger.LogInformation($"Initialized {_menus.Count} default menus");
        }

        private async Task LoadUserMenusAsync()
        {
            try
            {
                var userMenus = await _projectManager.LoadMenuStructuresAsync();

                foreach (var menu in userMenus)
                {
                    if (!_menus.ContainsKey(menu.Id))
                    {
                        _menus.Add(menu.Id, menu);

                        // Load menu items;
                        var menuItems = await _projectManager.LoadMenuItemsAsync(menu.Id);
                        foreach (var item in menuItems)
                        {
                            _menuItems.Add(item.Id, item);
                            menu.Items.Add(item.Id, item);
                        }
                    }
                }

                _logger.LogInformation($"Loaded {userMenus.Count} user menus");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load user menus");
            }
        }

        private async Task InitializeDefaultTemplatesAsync()
        {
            // Create default templates;
            var verticalTemplate = new MenuTemplate;
            {
                Name = "VerticalList",
                Type = TemplateType.Vertical,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Menus = new Dictionary<string, Menu>(),
                MenuItems = new Dictionary<string, MenuItem>(),
                Animations = new Dictionary<string, MenuAnimation>()
            };

            var horizontalTemplate = new MenuTemplate;
            {
                Name = "HorizontalList",
                Type = TemplateType.Horizontal,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Menus = new Dictionary<string, Menu>(),
                MenuItems = new Dictionary<string, MenuItem>(),
                Animations = new Dictionary<string, MenuAnimation>()
            };

            lock (_syncLock)
            {
                _templates.Add("VerticalList", verticalTemplate);
                _templates.Add("HorizontalList", horizontalTemplate);
            }

            _logger.LogInformation($"Initialized {_templates.Count} default templates");
        }

        private async Task RegisterNavigationActionsAsync()
        {
            // Register navigation actions with control scheme;
            await _controlScheme.RegisterActionAsync("MenuUp", ActionType.Button, new ActionConfiguration;
            {
                Category = "Menu",
                Priority = ActionPriority.High,
                IsEnabled = true;
            });

            await _controlScheme.RegisterActionAsync("MenuDown", ActionType.Button, new ActionConfiguration;
            {
                Category = "Menu",
                Priority = ActionPriority.High,
                IsEnabled = true;
            });

            await _controlScheme.RegisterActionAsync("MenuLeft", ActionType.Button, new ActionConfiguration;
            {
                Category = "Menu",
                Priority = ActionPriority.High,
                IsEnabled = true;
            });

            await _controlScheme.RegisterActionAsync("MenuRight", ActionType.Button, new ActionConfiguration;
            {
                Category = "Menu",
                Priority = ActionPriority.High,
                IsEnabled = true;
            });

            await _controlScheme.RegisterActionAsync("MenuSelect", ActionType.Button, new ActionConfiguration;
            {
                Category = "Menu",
                Priority = ActionPriority.Highest,
                IsEnabled = true;
            });

            await _controlScheme.RegisterActionAsync("MenuBack", ActionType.Button, new ActionConfiguration;
            {
                Category = "Menu",
                Priority = ActionPriority.High,
                IsEnabled = true;
            });

            _logger.LogInformation("Registered menu navigation actions");
        }

        private void StartUpdateLoop()
        {
            // This would typically be integrated with the game/application main loop;
            _logger.LogInformation("MenuDesigner update loop started");
        }

        private async Task ApplyThemeToMenuAsync(Menu menu)
        {
            try
            {
                var theme = await _themeManager.GetCurrentThemeAsync();
                if (theme != null && theme.MenuStyles.TryGetValue(menu.Type.ToString(), out var style))
                {
                    menu.Style = style;
                    menu.ModifiedDate = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to apply theme to menu: {menu.Id}");
            }
        }

        private async Task ApplyThemeToMenuItemAsync(MenuItem item)
        {
            try
            {
                var theme = await _themeManager.GetCurrentThemeAsync();
                if (theme != null && theme.MenuItemStyles.TryGetValue(item.Type.ToString(), out var style))
                {
                    item.Style = style;
                    item.LastModified = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to apply theme to menu item: {item.Id}");
            }
        }

        private async Task CreateMenuItemDisplayAsync(MenuItem item)
        {
            // Create UI element for the menu item;
            var elementConfig = new ElementConfiguration;
            {
                Position = CalculateMenuItemPosition(item),
                Size = CalculateMenuItemSize(item),
                IsVisible = item.IsVisible,
                ZIndex = item.Order * 10,
                Properties = new Dictionary<string, object>
                {
                    { "MenuItemId", item.Id },
                    { "Label", item.Label },
                    { "Description", item.Description },
                    { "IsEnabled", item.IsEnabled }
                }
            };

            await _layoutManager.AddElementAsync($"menu_item_{item.Id}", ElementType.Button, elementConfig);
        }

        private async Task UpdateMenuItemDisplayAsync(MenuItem item)
        {
            // Update UI element for the menu item;
            var update = new ElementUpdate;
            {
                IsVisible = item.IsVisible,
                Properties = new Dictionary<string, object>
                {
                    { "Label", item.Label },
                    { "Description", item.Description },
                    { "IsEnabled", item.IsEnabled }
                }
            };

            await _layoutManager.UpdateElementAsync($"menu_item_{item.Id}", update);
        }

        private async Task RemoveMenuItemDisplayAsync(MenuItem item)
        {
            // Remove UI element for the menu item;
            await _layoutManager.RemoveElementAsync($"menu_item_{item.Id}");
        }

        private Point CalculateMenuItemPosition(MenuItem item)
        {
            if (!_menus.TryGetValue(item.MenuId, out var menu))
                return Point.Zero;

            // Calculate position based on menu layout and item order;
            var basePosition = menu.Configuration.Position;
            var spacing = menu.Configuration.ItemSpacing;

            switch (menu.LayoutType)
            {
                case MenuLayoutType.VerticalList:
                    return new Point(
                        basePosition.X,
                        basePosition.Y + (item.Order - 1) * (menu.Configuration.ItemSize.Height + spacing));

                case MenuLayoutType.HorizontalList:
                    return new Point(
                        basePosition.X + (item.Order - 1) * (menu.Configuration.ItemSize.Width + spacing),
                        basePosition.Y);

                case MenuLayoutType.Grid:
                    int columns = menu.Configuration.GridColumns;
                    int row = (item.Order - 1) / columns;
                    int column = (item.Order - 1) % columns;
                    return new Point(
                        basePosition.X + column * (menu.Configuration.ItemSize.Width + spacing),
                        basePosition.Y + row * (menu.Configuration.ItemSize.Height + spacing));

                default:
                    return basePosition;
            }
        }

        private Size CalculateMenuItemSize(MenuItem item)
        {
            if (!_menus.TryGetValue(item.MenuId, out var menu))
                return menu?.Configuration.ItemSize ?? new Size(200, 50);

            return menu.Configuration.ItemSize;
        }

        private async Task ApplyItemFocusStyleAsync(MenuItem item, bool isFocused)
        {
            // Apply focus style to the menu item;
            var update = new ElementUpdate;
            {
                Properties = new Dictionary<string, object>
                {
                    { "IsFocused", isFocused }
                }
            };

            await _layoutManager.UpdateElementAsync($"menu_item_{item.Id}", update);
        }

        private async Task ShowMenuAsync(Menu menu)
        {
            // Show menu background;
            var backgroundConfig = new ElementConfiguration;
            {
                Position = menu.Configuration.Position,
                Size = menu.Configuration.Size,
                IsVisible = true,
                ZIndex = 0,
                Properties = new Dictionary<string, object>
                {
                    { "MenuId", menu.Id },
                    { "IsModal", menu.IsModal }
                }
            };

            await _layoutManager.AddElementAsync($"menu_background_{menu.Id}", ElementType.Panel, backgroundConfig);

            // Show menu items;
            foreach (var item in menu.Items.Values)
            {
                await UpdateMenuItemDisplayAsync(item);
            }

            // Play show animation if defined;
            if (menu.Configuration.ShowAnimation != null)
            {
                await PlayMenuAnimationAsync(menu, menu.Configuration.ShowAnimation);
            }

            _logger.LogDebug($"Menu shown: {menu.Id}");
        }

        private async Task HideMenuAsync(Menu menu)
        {
            // Hide menu background;
            await _layoutManager.RemoveElementAsync($"menu_background_{menu.Id}");

            // Hide menu items;
            foreach (var item in menu.Items.Values)
            {
                await UpdateMenuItemDisplayAsync(item);
            }

            // Play hide animation if defined;
            if (menu.Configuration.HideAnimation != null)
            {
                await PlayMenuAnimationAsync(menu, menu.Configuration.HideAnimation);
            }

            _logger.LogDebug($"Menu hidden: {menu.Id}");
        }

        private async Task PlayMenuAnimationAsync(Menu menu, MenuAnimation animation)
        {
            var menuAnimation = new MenuAnimation;
            {
                Id = $"{menu.Id}_{Guid.NewGuid():N}",
                MenuId = menu.Id,
                Animation = animation,
                StartTime = DateTime.UtcNow,
                IsActive = true;
            };

            _animations[menuAnimation.Id] = menuAnimation;

            await _timeline.AddAnimationAsync(menuAnimation.Id, animation.ToTimelineAnimation());

            _logger.LogDebug($"Menu animation started: {animation.Type} for menu {menu.Id}");
        }

        private int CalculateNewFocusIndex(int currentIndex, FocusDirection direction, List<MenuItem> items)
        {
            if (!items.Any())
                return -1;

            switch (direction)
            {
                case FocusDirection.Up:
                    return (currentIndex - 1 + items.Count) % items.Count;

                case FocusDirection.Down:
                    return (currentIndex + 1) % items.Count;

                case FocusDirection.Left:
                    // For grid layouts, might need special handling;
                    return (currentIndex - 1 + items.Count) % items.Count;

                case FocusDirection.Right:
                    // For grid layouts, might need special handling;
                    return (currentIndex + 1) % items.Count;

                case FocusDirection.Next:
                    return (currentIndex + 1) % items.Count;

                case FocusDirection.Previous:
                    return (currentIndex - 1 + items.Count) % items.Count;

                default:
                    return currentIndex;
            }
        }

        private async Task ExecuteMenuItemActionAsync(MenuItem item)
        {
            if (item.Action == null)
                return;

            try
            {
                switch (item.Action.Type)
                {
                    case ActionType.Navigate:
                        if (item.Action.Parameters.TryGetValue("target", out var target))
                        {
                            await ActivateMenuAsync(target.ToString());
                        }
                        break;

                    case ActionType.Execute:
                        if (item.Action.Parameters.TryGetValue("command", out var command))
                        {
                            // Execute the command;
                            await ExecuteCommandAsync(command.ToString(), item.Action.Parameters);
                        }
                        break;

                    case ActionType.Toggle:
                        if (item.Action.Parameters.TryGetValue("property", out var property))
                        {
                            // Toggle the property;
                            await TogglePropertyAsync(property.ToString(), item.Action.Parameters);
                        }
                        break;

                    case ActionType.Quit:
                        // Handle quit action;
                        await HandleQuitActionAsync();
                        break;

                    case ActionType.Custom:
                        // Handle custom action;
                        await HandleCustomActionAsync(item.Action);
                        break;
                }

                _logger.LogDebug($"Menu item action executed: {item.Action.Type} for item {item.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to execute menu item action: {item.Id}");
                throw;
            }
        }

        private async Task ExecuteCommandAsync(string command, Dictionary<string, object> parameters)
        {
            // Execute the specified command;
            var menuCommand = new MenuCommand;
            {
                Type = MenuCommandType.Custom,
                Parameters = new Dictionary<string, object>(parameters)
                {
                    { "command", command }
                },
                CreatedTime = DateTime.UtcNow;
            };

            _commandQueue.Enqueue(menuCommand);

            await Task.CompletedTask;
        }

        private async Task TogglePropertyAsync(string property, Dictionary<string, object> parameters)
        {
            // Toggle the specified property;
            _logger.LogInformation($"Toggling property: {property}");

            await Task.CompletedTask;
        }

        private async Task HandleQuitActionAsync()
        {
            // Handle quit action;
            CurrentState = MenuState.ShuttingDown;
            _logger.LogInformation("Quit action triggered");

            await Task.CompletedTask;
        }

        private async Task HandleCustomActionAsync(MenuItemAction action)
        {
            // Handle custom action;
            _logger.LogInformation($"Custom action triggered: {action.Type}");

            await Task.CompletedTask;
        }

        private async Task RenderMenuBackgroundAsync(Menu menu)
        {
            // Render menu background;
            // This would integrate with the rendering system;
            // For now, we'll just update statistics;

            _currentStatistics.BackgroundsRendered++;

            await Task.CompletedTask;
        }

        private async Task RenderMenuItemAsync(MenuItem item)
        {
            // Render menu item;
            // This would integrate with the rendering system;
            // For now, we'll just update statistics;

            _currentStatistics.ItemsRendered++;

            await Task.CompletedTask;
        }

        private async Task RenderFocusIndicatorAsync(MenuItem item)
        {
            // Render focus indicator;
            // This would integrate with the rendering system;
            // For now, we'll just update statistics;

            _currentStatistics.FocusIndicatorsRendered++;

            await Task.CompletedTask;
        }

        private async Task ProcessCommandQueueAsync()
        {
            lock (_syncLock)
            {
                while (_commandQueue.Count > 0)
                {
                    var command = _commandQueue.Dequeue();
                    ProcessCommand(command);
                }
            }

            await Task.CompletedTask;
        }

        private void ProcessCommand(MenuCommand command)
        {
            switch (command.Type)
            {
                case MenuCommandType.CreateMenu:
                    _ = CreateMenuAsync(
                        command.Parameters["menuId"] as string,
                        (MenuType)command.Parameters["type"],
                        (MenuConfiguration)command.Parameters["config"]);
                    break;

                case MenuCommandType.RemoveMenu:
                    _ = RemoveMenuAsync(command.Parameters["menuId"] as string);
                    break;

                case MenuCommandType.ActivateMenu:
                    _ = ActivateMenuAsync(
                        command.Parameters["menuId"] as string,
                        (ActivationMode)command.Parameters["mode"]);
                    break;

                case MenuCommandType.DeactivateMenu:
                    _ = DeactivateMenuAsync();
                    break;

                case MenuCommandType.AddMenuItem:
                    _ = AddMenuItemAsync(
                        command.Parameters["menuId"] as string,
                        (MenuItem)command.Parameters["item"]);
                    break;

                case MenuCommandType.RemoveMenuItem:
                    _ = RemoveMenuItemAsync(
                        command.Parameters["menuId"] as string,
                        command.Parameters["itemId"] as string);
                    break;

                case MenuCommandType.SelectItem:
                    _ = SelectFocusedItemAsync();
                    break;

                default:
                    _logger.LogWarning($"Unknown command type: {command.Type}");
                    break;
            }
        }

        private async Task UpdateAnimationsAsync(TimeSpan deltaTime)
        {
            var completedAnimations = new List<string>();

            foreach (var animation in _animations.Values.Where(a => a.IsActive))
            {
                animation.UpdateTime += deltaTime;

                // Check if animation is complete;
                if (animation.UpdateTime >= animation.Animation.Duration)
                {
                    completedAnimations.Add(animation.Id);
                    animation.IsActive = false;
                }
            }

            // Remove completed animations;
            foreach (var animationId in completedAnimations)
            {
                _animations.Remove(animationId);
            }

            await Task.CompletedTask;
        }

        private async Task UpdateInputHandlingAsync(TimeSpan deltaTime)
        {
            if (_activeMenu == null || !_activeMenu.IsVisible)
                return;

            // Process input for menu navigation;
            // This would typically check input state and trigger navigation events;

            await Task.CompletedTask;
        }

        private void UpdateStatistics(TimeSpan deltaTime)
        {
            _currentStatistics.UpdateTime += deltaTime;
            _currentStatistics.FrameCount++;

            // Calculate items per second;
            if (_currentStatistics.UpdateTime.TotalSeconds >= 1.0)
            {
                _currentStatistics.ItemsPerSecond = _currentStatistics.ItemsRendered / (float)_currentStatistics.UpdateTime.TotalSeconds;
                _currentStatistics.BackgroundsPerSecond = _currentStatistics.BackgroundsRendered / (float)_currentStatistics.UpdateTime.TotalSeconds;

                // Reset for next second;
                _currentStatistics.ItemsRendered = 0;
                _currentStatistics.BackgroundsRendered = 0;
                _currentStatistics.FocusIndicatorsRendered = 0;
                _currentStatistics.UpdateTime = TimeSpan.Zero;
                _currentStatistics.FrameCount = 0;
            }
        }

        private void UpdateMetrics()
        {
            _metrics.TotalMenus = _menus.Count;
            _metrics.TotalMenuItems = _menuItems.Count;
            _metrics.VisibleMenus = _menus.Count(m => m.Value.IsVisible);
            _metrics.ActiveMenus = _menus.Count(m => m.Value.IsActive);
            _metrics.EnabledItems = _menuItems.Count(i => i.Value.IsEnabled);
            _metrics.VisibleItems = _menuItems.Count(i => i.Value.IsVisible);
            _metrics.LastUpdate = DateTime.UtcNow;
        }

        #endregion;

        #region Validation Methods;

        private void ValidateConfiguration(MenuConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
        }

        private void ValidateMenuId(string menuId)
        {
            if (string.IsNullOrWhiteSpace(menuId))
                throw new ArgumentException("Menu ID cannot be null or empty", nameof(menuId));
        }

        private void ValidateMenuConfiguration(MenuConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
        }

        private void ValidateMenuItem(MenuItem item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrWhiteSpace(item.Id))
                throw new ArgumentException("Menu item ID cannot be null or empty", nameof(item.Id));

            if (string.IsNullOrWhiteSpace(item.Label))
                throw new ArgumentException("Menu item label cannot be null or empty", nameof(item.Label));
        }

        private void ValidateMenuItemId(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Menu item ID cannot be null or empty", nameof(itemId));
        }

        private void ValidateMenuItemUpdate(MenuItemUpdate update)
        {
            if (update == null)
                throw new ArgumentNullException(nameof(update));
        }

        private void ValidateTemplateName(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Template name cannot be null or empty", nameof(templateName));
        }

        #endregion;

        #region Event Methods;

        private void InitializeEventHandlers()
        {
            MenuStateChanged += OnMenuStateChangedInternal;
            NavigationModeChanged += OnNavigationModeChangedInternal;
            ActiveMenuChanged += OnActiveMenuChangedInternal;
            FocusChanged += OnFocusChangedInternal;
            MenuCreated += OnMenuCreatedInternal;
            MenuRemoved += OnMenuRemovedInternal;
            MenuItemAdded += OnMenuItemAddedInternal;
            MenuItemRemoved += OnMenuItemRemovedInternal;
            MenuItemSelected += OnMenuItemSelectedInternal;
            NavigationPerformed += OnNavigationPerformedInternal;
            MenuError += OnMenuErrorInternal;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected virtual void OnMenuStateChanged(MenuStateEventArgs e)
        {
            MenuStateChanged?.Invoke(this, e);
        }

        protected virtual void OnNavigationModeChanged(NavigationModeEventArgs e)
        {
            NavigationModeChanged?.Invoke(this, e);
        }

        protected virtual void OnActiveMenuChanged(MenuEventArgs e)
        {
            ActiveMenuChanged?.Invoke(this, e);
        }

        protected virtual void OnFocusChanged(FocusChangedEventArgs e)
        {
            FocusChanged?.Invoke(this, e);
        }

        protected virtual void OnMenuCreated(MenuEventArgs e)
        {
            MenuCreated?.Invoke(this, e);
        }

        protected virtual void OnMenuRemoved(MenuEventArgs e)
        {
            MenuRemoved?.Invoke(this, e);
        }

        protected virtual void OnMenuItemAdded(MenuItemEventArgs e)
        {
            MenuItemAdded?.Invoke(this, e);
        }

        protected virtual void OnMenuItemRemoved(MenuItemEventArgs e)
        {
            MenuItemRemoved?.Invoke(this, e);
        }

        protected virtual void OnMenuItemSelected(MenuItemEventArgs e)
        {
            MenuItemSelected?.Invoke(this, e);
        }

        protected virtual void OnNavigationPerformed(NavigationEventArgs e)
        {
            NavigationPerformed?.Invoke(this, e);
        }

        protected virtual void OnMenuError(MenuErrorEventArgs e)
        {
            MenuError?.Invoke(this, e);
        }

        private void OnMenuStateChangedInternal(object sender, MenuStateEventArgs e)
        {
            _logger.LogInformation($"Menu state changed: {e.PreviousState} -> {e.NewState}");
        }

        private void OnNavigationModeChangedInternal(object sender, NavigationModeEventArgs e)
        {
            _logger.LogInformation($"Navigation mode changed: {e.NavigationMode}");
        }

        private void OnActiveMenuChangedInternal(object sender, MenuEventArgs e)
        {
            _logger.LogInformation($"Active menu changed: {e.Menu?.Name ?? "None"}");
        }

        private void OnFocusChangedInternal(object sender, FocusChangedEventArgs e)
        {
            _logger.LogDebug($"Focus changed: {e.PreviousItem?.Label ?? "None"} -> {e.CurrentItem?.Label ?? "None"}");
        }

        private void OnMenuCreatedInternal(object sender, MenuEventArgs e)
        {
            _logger.LogDebug($"Menu created: {e.Menu.Id}");
        }

        private void OnMenuRemovedInternal(object sender, MenuEventArgs e)
        {
            _logger.LogDebug($"Menu removed: {e.Menu.Id}");
        }

        private void OnMenuItemAddedInternal(object sender, MenuItemEventArgs e)
        {
            _logger.LogDebug($"Menu item added: {e.MenuItem.Id}");
        }

        private void OnMenuItemRemovedInternal(object sender, MenuItemEventArgs e)
        {
            _logger.LogDebug($"Menu item removed: {e.MenuItem.Id}");
        }

        private void OnMenuItemSelectedInternal(object sender, MenuItemEventArgs e)
        {
            _logger.LogInformation($"Menu item selected: {e.MenuItem.Label}");
        }

        private void OnNavigationPerformedInternal(object sender, NavigationEventArgs e)
        {
            _logger.LogDebug($"Navigation performed: {e.FromMenuId} -> {e.ToMenuId}");
        }

        private void OnMenuErrorInternal(object sender, MenuErrorEventArgs e)
        {
            _logger.LogError(e.Exception, $"Menu error: {e.ErrorMessage}");
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
                    // Dispose managed resources;
                    _menus.Clear();
                    _menuItems.Clear();
                    _templates.Clear();
                    _animations.Clear();
                    _navigationHistory.Clear();
                    _commandQueue.Clear();

                    // Unsubscribe events;
                    MenuStateChanged -= OnMenuStateChangedInternal;
                    NavigationModeChanged -= OnNavigationModeChangedInternal;
                    ActiveMenuChanged -= OnActiveMenuChangedInternal;
                    FocusChanged -= OnFocusChangedInternal;
                    MenuCreated -= OnMenuCreatedInternal;
                    MenuRemoved -= OnMenuRemovedInternal;
                    MenuItemAdded -= OnMenuItemAddedInternal;
                    MenuItemRemoved -= OnMenuItemRemovedInternal;
                    MenuItemSelected -= OnMenuItemSelectedInternal;
                    NavigationPerformed -= OnNavigationPerformedInternal;
                    MenuError -= OnMenuErrorInternal;
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
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Menu state;
    /// </summary>
    public enum MenuState;
    {
        Uninitialized,
        Initializing,
        Ready,
        Active,
        Navigating,
        Animating,
        Resetting,
        Error,
        ShuttingDown;
    }

    /// <summary>
    /// Menu types;
    /// </summary>
    public enum MenuType;
    {
        Main,
        Pause,
        Options,
        Inventory,
        Character,
        System,
        Debug,
        Custom;
    }

    /// <summary>
    /// Menu layout types;
    /// </summary>
    public enum MenuLayoutType;
    {
        VerticalList,
        HorizontalList,
        Grid,
        Radial,
        Freeform,
        Tabbed;
    }

    /// <summary>
    /// Navigation modes;
    /// </summary>
    public enum NavigationMode;
    {
        Keyboard,
        Gamepad,
        Mouse,
        Touch,
        Hybrid,
        Automatic;
    }

    /// <summary>
    /// Navigation styles;
    /// </summary>
    public enum NavigationStyle;
    {
        Linear,
        Circular,
        Grid,
        Free,
        Hierarchical;
    }

    /// <summary>
    /// Menu item types;
    /// </summary>
    public enum MenuItemType;
    {
        Button,
        Toggle,
        Slider,
        Dropdown,
        InputField,
        Label,
        Separator,
        Submenu,
        Custom;
    }

    /// <summary>
    /// Action types for menu items;
    /// </summary>
    public enum ActionType;
    {
        Navigate,
        Execute,
        Toggle,
        Quit,
        Back,
        Custom;
    }

    /// <summary>
    /// Focus directions;
    /// </summary>
    public enum FocusDirection;
    {
        Up,
        Down,
        Left,
        Right,
        Next,
        Previous,
        First,
        Last;
    }

    /// <summary>
    /// Activation modes;
    /// </summary>
    public enum ActivationMode;
    {
        Standard,
        Replace,
        History,
        Modal,
        Overlay;
    }

    /// <summary>
    /// Template types;
    /// </summary>
    public enum TemplateType;
    {
        Vertical,
        Horizontal,
        Grid,
        Radial,
        Tabbed,
        FromCurrent,
        Custom;
    }

    /// <summary>
    /// Animation types for menus;
    /// </summary>
    public enum AnimationType;
    {
        Fade,
        Slide,
        Scale,
        Rotate,
        Flip,
        Bounce,
        Custom;
    }

    /// <summary>
    /// Menu;
    /// </summary>
    public class Menu;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MenuType Type { get; set; }
        public MenuConfiguration Configuration { get; set; }
        public MenuLayoutType LayoutType { get; set; }
        public NavigationStyle NavigationStyle { get; set; }
        public MenuStyle Style { get; set; }
        public bool IsVisible { get; set; }
        public bool IsActive { get; set; }
        public bool IsModal { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public DateTime? LastActivated { get; set; }
        public DateTime? LastDeactivated { get; set; }
        public Dictionary<string, MenuItem> Items { get; set; }
        public Dictionary<string, MenuAnimation> Animations { get; set; }

        public Menu Clone()
        {
            return new Menu;
            {
                Id = Guid.NewGuid().ToString(),
                Name = this.Name,
                Type = this.Type,
                Configuration = this.Configuration?.Clone(),
                LayoutType = this.LayoutType,
                NavigationStyle = this.NavigationStyle,
                Style = this.Style?.Clone(),
                IsVisible = this.IsVisible,
                IsActive = false,
                IsModal = this.IsModal,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Items = new Dictionary<string, MenuItem>(),
                Animations = new Dictionary<string, MenuAnimation>()
            };
        }
    }

    /// <summary>
    /// Menu configuration;
    /// </summary>
    public class MenuConfiguration;
    {
        public string Name { get; set; }
        public MenuLayoutType LayoutType { get; set; } = MenuLayoutType.VerticalList;
        public NavigationStyle NavigationStyle { get; set; } = NavigationStyle.Linear;
        public Point Position { get; set; } = Point.Zero;
        public Size Size { get; set; } = new Size(800, 600);
        public Size ItemSize { get; set; } = new Size(200, 50);
        public float ItemSpacing { get; set; } = 10.0f;
        public int GridColumns { get; set; } = 3;
        public bool IsVisible { get; set; } = true;
        public bool IsModal { get; set; } = false;
        public bool AutoFocus { get; set; } = true;
        public NavigationMode DefaultNavigationMode { get; set; } = NavigationMode.Keyboard;
        public MenuStyle BackgroundStyle { get; set; }
        public MenuAnimation ShowAnimation { get; set; }
        public MenuAnimation HideAnimation { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public MenuConfiguration Clone()
        {
            return new MenuConfiguration;
            {
                Name = this.Name,
                LayoutType = this.LayoutType,
                NavigationStyle = this.NavigationStyle,
                Position = this.Position,
                Size = this.Size,
                ItemSize = this.ItemSize,
                ItemSpacing = this.ItemSpacing,
                GridColumns = this.GridColumns,
                IsVisible = this.IsVisible,
                IsModal = this.IsModal,
                AutoFocus = this.AutoFocus,
                DefaultNavigationMode = this.DefaultNavigationMode,
                BackgroundStyle = this.BackgroundStyle?.Clone(),
                ShowAnimation = this.ShowAnimation?.Clone(),
                HideAnimation = this.HideAnimation?.Clone(),
                Properties = new Dictionary<string, object>(this.Properties)
            };
        }
    }

    /// <summary>
    /// Menu item;
    /// </summary>
    public class MenuItem;
    {
        public string Id { get; set; }
        public string MenuId { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public MenuItemType Type { get; set; }
        public int Order { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible { get; set; } = true;
        public MenuItemStyle Style { get; set; }
        public MenuItemAction Action { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastModified { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public MenuItem Clone()
        {
            return new MenuItem;
            {
                Id = Guid.NewGuid().ToString(),
                MenuId = this.MenuId,
                Label = this.Label,
                Description = this.Description,
                Type = this.Type,
                Order = this.Order,
                IsEnabled = this.IsEnabled,
                IsVisible = this.IsVisible,
                Style = this.Style?.Clone(),
                Action = this.Action?.Clone(),
                CreatedTime = DateTime.UtcNow,
                LastModified = this.LastModified,
                Properties = new Dictionary<string, object>(this.Properties)
            };
        }
    }

    /// <summary>
    /// Menu item update parameters;
    /// </summary>
    public class MenuItemUpdate;
    {
        public string Label { get; set; }
        public string Description { get; set; }
        public bool? IsEnabled { get; set; }
        public bool? IsVisible { get; set; }
        public int? Order { get; set; }
        public MenuItemStyle Style { get; set; }
        public MenuItemAction Action { get; set; }
        public Dictionary<string, object> Properties { get; set; }
    }

    /// <summary>
    /// Menu item action;
    /// </summary>
    public class MenuItemAction;
    {
        public ActionType Type { get; set; }
        public string Target { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public MenuItemAction Clone()
        {
            return new MenuItemAction;
            {
                Type = this.Type,
                Target = this.Target,
                Parameters = new Dictionary<string, object>(this.Parameters)
            };
        }
    }

    /// <summary>
    /// Menu style;
    /// </summary>
    public class MenuStyle;
    {
        public Color BackgroundColor { get; set; }
        public Color BorderColor { get; set; }
        public float BorderWidth { get; set; }
        public float CornerRadius { get; set; }
        public float Opacity { get; set; } = 1.0f;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public MenuStyle Clone()
        {
            return new MenuStyle;
            {
                BackgroundColor = this.BackgroundColor,
                BorderColor = this.BorderColor,
                BorderWidth = this.BorderWidth,
                CornerRadius = this.CornerRadius,
                Opacity = this.Opacity,
                Properties = new Dictionary<string, object>(this.Properties)
            };
        }
    }

    /// <summary>
    /// Menu item style;
    /// </summary>
    public class MenuItemStyle;
    {
        public Color NormalColor { get; set; }
        public Color HoverColor { get; set; }
        public Color PressedColor { get; set; }
        public Color DisabledColor { get; set; }
        public Color TextColor { get; set; }
        public Color FocusColor { get; set; }
        public FontSettings Font { get; set; }
        public float Padding { get; set; } = 5.0f;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public MenuItemStyle Clone()
        {
            return new MenuItemStyle;
            {
                NormalColor = this.NormalColor,
                HoverColor = this.HoverColor,
                PressedColor = this.PressedColor,
                DisabledColor = this.DisabledColor,
                TextColor = this.TextColor,
                FocusColor = this.FocusColor,
                Font = this.Font?.Clone(),
                Padding = this.Padding,
                Properties = new Dictionary<string, object>(this.Properties)
            };
        }
    }

    /// <summary>
    /// Menu template;
    /// </summary>
    public class MenuTemplate;
    {
        public string Name { get; set; }
        public TemplateType Type { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public Dictionary<string, Menu> Menus { get; set; }
        public Dictionary<string, MenuItem> MenuItems { get; set; }
        public Dictionary<string, MenuAnimation> Animations { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Menu animation;
    /// </summary>
    public class MenuAnimation;
    {
        public string Id { get; set; }
        public string MenuId { get; set; }
        public MenuAnimationConfig Animation { get; set; }
        public DateTime StartTime { get; set; }
        public bool IsActive { get; set; }
        public TimeSpan UpdateTime { get; set; }

        public TimelineAnimation ToTimelineAnimation()
        {
            return new TimelineAnimation;
            {
                Type = Animation.Type.ToString(),
                Duration = Animation.Duration,
                Easing = Animation.Easing,
                Parameters = new Dictionary<string, object>(Animation.Parameters)
            };
        }
    }

    /// <summary>
    /// Menu animation configuration;
    /// </summary>
    public class MenuAnimationConfig;
    {
        public AnimationType Type { get; set; }
        public TimeSpan Duration { get; set; }
        public AnimationEasing Easing { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public MenuAnimationConfig Clone()
        {
            return new MenuAnimationConfig;
            {
                Type = this.Type,
                Duration = this.Duration,
                Easing = this.Easing,
                Parameters = new Dictionary<string, object>(this.Parameters)
            };
        }
    }

    /// <summary>
    /// Menu history entry
    /// </summary>
    public class MenuHistoryEntry
    {
        public string MenuId { get; set; }
        public DateTime Timestamp { get; set; }
        public ActivationMode ActivationMode { get; set; }
        public Dictionary<string, object> State { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Menu metrics;
    /// </summary>
    public class MenuMetrics;
    {
        public int TotalMenus { get; set; }
        public int TotalMenuItems { get; set; }
        public int VisibleMenus { get; set; }
        public int ActiveMenus { get; set; }
        public int EnabledItems { get; set; }
        public int VisibleItems { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Menu statistics;
    /// </summary>
    public class MenuStatistics;
    {
        public long ItemsRendered { get; set; }
        public long BackgroundsRendered { get; set; }
        public long FocusIndicatorsRendered { get; set; }
        public long FramesRendered { get; set; }
        public long ItemsSelected { get; set; }
        public float ItemsPerSecond { get; set; }
        public float BackgroundsPerSecond { get; set; }
        public TimeSpan UpdateTime { get; set; }
        public long FrameCount { get; set; }
    }

    /// <summary>
    /// Menu designer statistics;
    /// </summary>
    public class MenuDesignerStatistics;
    {
        public int TotalMenus { get; set; }
        public int TotalMenuItems { get; set; }
        public int TotalTemplates { get; set; }
        public TimeSpan Uptime { get; set; }
        public MenuState State { get; set; }
        public NavigationMode NavigationMode { get; set; }
        public int NavigationDepth { get; set; }
        public string ActiveMenuId { get; set; }
        public string FocusedItemId { get; set; }
        public MenuMetrics Metrics { get; set; }
        public MenuStatistics MenuStatistics { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Menu command;
    /// </summary>
    public class MenuCommand;
    {
        public MenuCommandType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedTime { get; set; }
    }

    /// <summary>
    /// Menu command types;
    /// </summary>
    public enum MenuCommandType;
    {
        CreateMenu,
        RemoveMenu,
        ActivateMenu,
        DeactivateMenu,
        AddMenuItem,
        RemoveMenuItem,
        UpdateMenuItem,
        FocusItem,
        SelectItem,
        NavigateBack,
        ApplyTemplate,
        SetVisibility,
        Custom;
    }

    #region Geometry and Color Structures;

    /// <summary>
    /// Point structure;
    /// </summary>
    public struct Point;
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Point(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static Point Zero => new Point(0, 0);
    }

    /// <summary>
    /// Size structure;
    /// </summary>
    public struct Size;
    {
        public float Width { get; set; }
        public float Height { get; set; }

        public Size(float width, float height)
        {
            Width = width;
            Height = height;
        }

        public static Size Zero => new Size(0, 0);
    }

    /// <summary>
    /// Color structure;
    /// </summary>
    public struct Color;
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }

        public Color(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static Color FromHex(string hex)
        {
            if (hex.StartsWith("#"))
                hex = hex.Substring(1);

            if (hex.Length == 6)
            {
                return new Color(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            else if (hex.Length == 8)
            {
                return new Color(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    Convert.ToByte(hex.Substring(6, 2), 16));
            }

            throw new ArgumentException("Invalid hex color format");
        }

        public static Color Transparent => new Color(0, 0, 0, 0);
        public static Color White => new Color(255, 255, 255);
        public static Color Black => new Color(0, 0, 0);
        public static Color Red => new Color(255, 0, 0);
        public static Color Green => new Color(0, 255, 0);
        public static Color Blue => new Color(0, 0, 255);
    }

    /// <summary>
    /// Font settings;
    /// </summary>
    public class FontSettings;
    {
        public string Family { get; set; } = "Arial";
        public float Size { get; set; } = 14.0f;
        public FontWeight Weight { get; set; } = FontWeight.Normal;
        public FontStyle Style { get; set; } = FontStyle.Normal;

        public FontSettings Clone()
        {
            return new FontSettings;
            {
                Family = this.Family,
                Size = this.Size,
                Weight = this.Weight,
                Style = this.Style;
            };
        }
    }

    /// <summary>
    /// Font weight;
    /// </summary>
    public enum FontWeight;
    {
        Normal,
        Bold,
        Light,
        ExtraBold;
    }

    /// <summary>
    /// Font style;
    /// </summary>
    public enum FontStyle;
    {
        Normal,
        Italic,
        Oblique;
    }

    /// <summary>
    /// Animation easing;
    /// </summary>
    public enum AnimationEasing;
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        Bounce,
        Elastic;
    }

    #endregion;

    #region Event Arguments;

    /// <summary>
    /// Menu state event arguments;
    /// </summary>
    public class MenuStateEventArgs : EventArgs;
    {
        public MenuState PreviousState { get; set; }
        public MenuState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Navigation mode event arguments;
    /// </summary>
    public class NavigationModeEventArgs : EventArgs;
    {
        public NavigationMode NavigationMode { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Menu event arguments;
    /// </summary>
    public class MenuEventArgs : EventArgs;
    {
        public Menu Menu { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Focus changed event arguments;
    /// </summary>
    public class FocusChangedEventArgs : EventArgs;
    {
        public MenuItem PreviousItem { get; set; }
        public MenuItem CurrentItem { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Menu item event arguments;
    /// </summary>
    public class MenuItemEventArgs : EventArgs;
    {
        public MenuItem MenuItem { get; set; }
        public Menu Menu { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Navigation event arguments;
    /// </summary>
    public class NavigationEventArgs : EventArgs;
    {
        public string FromMenuId { get; set; }
        public string ToMenuId { get; set; }
        public NavigationMode NavigationMode { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Menu error event arguments;
    /// </summary>
    public class MenuErrorEventArgs : EventArgs;
    {
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Custom exception for menu designer errors;
    /// </summary>
    public class MenuDesignerException : Exception
    {
        public MenuDesignerException() { }
        public MenuDesignerException(string message) : base(message) { }
        public MenuDesignerException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #endregion;
}
