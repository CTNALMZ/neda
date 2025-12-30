using NEDA.AI.MachineLearning;
using NEDA.API.Versioning;
using NEDA.Communication.DialogSystem;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.ContentCreation.AssetPipeline.QualityOptimizers;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign;
using NEDA.Monitoring.HealthChecks;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.UI.Controls;
using NEDA.UI.Themes;
using NEDA.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NEDA.UI.Controls;
{
    /// <summary>
    /// Advanced widget management system for HUD elements with dynamic layout, theming, and AI-driven adaptation;
    /// Supports docking, grouping, resizing, and real-time performance optimization;
    /// </summary>
    public class WidgetManager : IWidgetManager, IDisposable;
    {
        private readonly ILogger<WidgetManager> _logger;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ISystemHealth _systemHealth;
        private readonly IThemeManager _themeManager;
        private readonly IMLModel _mlModel;
        private readonly IEmotionDetector _emotionDetector;

        private Dictionary<string, WidgetInstance> _widgets;
        private Dictionary<string, WidgetGroup> _widgetGroups;
        private Dictionary<string, WidgetLayout> _widgetLayouts;
        private Dictionary<string, WidgetTemplate> _widgetTemplates;

        private Canvas _mainCanvas;
        private Grid _layoutGrid;
        private WidgetDockPanel _dockPanel;

        private bool _isInitialized;
        private bool _isDisposed;
        private object _syncLock = new object();

        /// <summary>
        /// Event triggered when widget is added;
        /// </summary>
        public event EventHandler<WidgetAddedEventArgs> WidgetAdded;

        /// <summary>
        /// Event triggered when widget is removed;
        /// </summary>
        public event EventHandler<WidgetRemovedEventArgs> WidgetRemoved;

        /// <summary>
        /// Event triggered when widget layout changes;
        /// </summary>
        public event EventHandler<WidgetLayoutChangedEventArgs> LayoutChanged;

        /// <summary>
        /// Event triggered when widget performance metrics are updated;
        /// </summary>
        public event EventHandler<WidgetPerformanceEventArgs> PerformanceMetricsUpdated;

        /// <summary>
        /// Event triggered when widget visibility changes;
        /// </summary>
        public event EventHandler<WidgetVisibilityChangedEventArgs> VisibilityChanged;

        /// <summary>
        /// Event triggered when widget state changes;
        /// </summary>
        public event EventHandler<WidgetStateChangedEventArgs> StateChanged;

        public WidgetManager(
            ILogger<WidgetManager> logger,
            IPerformanceMonitor performanceMonitor,
            ISystemHealth systemHealth,
            IThemeManager themeManager,
            IMLModel mlModel,
            IEmotionDetector emotionDetector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _systemHealth = systemHealth ?? throw new ArgumentNullException(nameof(systemHealth));
            _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
            _mlModel = mlModel ?? throw new ArgumentNullException(nameof(mlModel));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));

            _widgets = new Dictionary<string, WidgetInstance>();
            _widgetGroups = new Dictionary<string, WidgetGroup>();
            _widgetLayouts = new Dictionary<string, WidgetLayout>();
            _widgetTemplates = new Dictionary<string, WidgetTemplate>();

            InitializeTemplates();
            SubscribeToEvents();

            _logger.LogInformation("WidgetManager initialized");
        }

        /// <summary>
        /// Initializes the widget manager with UI containers;
        /// </summary>
        /// <param name="mainCanvas">Main canvas for widget placement</param>
        /// <param name="layoutGrid">Layout grid for structured placement</param>
        public void Initialize(Canvas mainCanvas, Grid layoutGrid)
        {
            if (mainCanvas == null)
                throw new ArgumentNullException(nameof(mainCanvas));
            if (layoutGrid == null)
                throw new ArgumentNullException(nameof(layoutGrid));

            lock (_syncLock)
            {
                _mainCanvas = mainCanvas;
                _layoutGrid = layoutGrid;

                // Create dock panel for widget management;
                _dockPanel = new WidgetDockPanel;
                {
                    Name = "MainWidgetDockPanel",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    LastChildFill = true;
                };

                layoutGrid.Children.Add(_dockPanel);
                Grid.SetColumnSpan(_dockPanel, layoutGrid.ColumnDefinitions.Count);
                Grid.SetRowSpan(_dockPanel, layoutGrid.RowDefinitions.Count);

                // Load saved layout;
                LoadSavedLayout();

                // Start performance monitoring;
                StartPerformanceMonitoring();

                _isInitialized = true;
                _logger.LogInformation("WidgetManager UI containers initialized");
            }
        }

        /// <summary>
        /// Creates and adds a new widget to the interface;
        /// </summary>
        /// <param name="widgetType">Type of widget to create</param>
        /// <param name="parameters">Widget creation parameters</param>
        /// <param name="position">Initial position (optional)</param>
        public async Task<WidgetInstance> CreateWidgetAsync(WidgetType widgetType, WidgetCreationParameters parameters, Point? position = null)
        {
            if (!_isInitialized)
                throw new WidgetManagerException("WidgetManager not initialized. Call Initialize() first.");

            _logger.LogInformation("Creating widget of type: {WidgetType}", widgetType);

            try
            {
                // Check if widget already exists with same ID;
                if (_widgets.ContainsKey(parameters.WidgetId))
                {
                    throw new WidgetCreationException($"Widget with ID '{parameters.WidgetId}' already exists.");
                }

                // Get widget template;
                var template = GetWidgetTemplate(widgetType);

                // Create widget instance;
                var widget = await CreateWidgetInstanceAsync(template, parameters);

                // Apply theme;
                ApplyThemeToWidget(widget);

                // Set initial position;
                if (position.HasValue)
                {
                    widget.Position = position.Value;
                }
                else;
                {
                    widget.Position = CalculateOptimalPosition(widget);
                }

                // Add to collections;
                lock (_syncLock)
                {
                    _widgets[widget.Id] = widget;

                    // Add to appropriate group;
                    var group = GetOrCreateGroup(widget.GroupName);
                    group.Widgets.Add(widget);
                }

                // Add to UI;
                await AddWidgetToUIAsync(widget);

                // Register for performance monitoring;
                RegisterWidgetForMonitoring(widget);

                // Trigger event;
                OnWidgetAdded(widget);

                _logger.LogDebug("Widget created successfully: {WidgetId}", widget.Id);
                return widget;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create widget of type: {WidgetType}", widgetType);
                throw new WidgetCreationException($"Failed to create widget of type {widgetType}", ex);
            }
        }

        /// <summary>
        /// Removes a widget from the interface;
        /// </summary>
        /// <param name="widgetId">ID of widget to remove</param>
        /// <param name="saveState">Whether to save widget state before removal</param>
        public async Task RemoveWidgetAsync(string widgetId, bool saveState = true)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget))
            {
                _logger.LogWarning("Attempted to remove non-existent widget: {WidgetId}", widgetId);
                return;
            }

            _logger.LogInformation("Removing widget: {WidgetId}", widgetId);

            try
            {
                // Save state if requested;
                if (saveState)
                {
                    await SaveWidgetStateAsync(widget);
                }

                // Unregister from monitoring;
                UnregisterWidgetFromMonitoring(widget);

                // Remove from UI;
                await RemoveWidgetFromUIAsync(widget);

                // Remove from collections;
                lock (_syncLock)
                {
                    // Remove from group;
                    if (_widgetGroups.TryGetValue(widget.GroupName, out var group))
                    {
                        group.Widgets.Remove(widget);

                        // Remove group if empty;
                        if (group.Widgets.Count == 0)
                        {
                            _widgetGroups.Remove(widget.GroupName);
                        }
                    }

                    // Remove from main collection;
                    _widgets.Remove(widgetId);
                }

                // Clean up resources;
                widget.Dispose();

                // Trigger event;
                OnWidgetRemoved(widget);

                _logger.LogDebug("Widget removed successfully: {WidgetId}", widgetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove widget: {WidgetId}", widgetId);
                throw;
            }
        }

        /// <summary>
        /// Updates widget position and size;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <param name="newPosition">New position</param>
        /// <param name="newSize">New size</param>
        public async Task UpdateWidgetLayoutAsync(string widgetId, Point newPosition, Size newSize)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget))
            {
                throw new WidgetNotFoundException($"Widget not found: {widgetId}");
            }

            _logger.LogDebug("Updating layout for widget: {WidgetId}", widgetId);

            try
            {
                // Validate new size;
                if (newSize.Width < widget.MinWidth || newSize.Height < widget.MinHeight)
                {
                    throw new WidgetLayoutException($"Widget size below minimum: {newSize}");
                }

                if (newSize.Width > widget.MaxWidth || newSize.Height > widget.MaxHeight)
                {
                    throw new WidgetLayoutException($"Widget size exceeds maximum: {newSize}");
                }

                // Check for collisions;
                if (CheckWidgetCollision(widgetId, newPosition, newSize))
                {
                    // Auto-resolve collision;
                    newPosition = ResolveCollision(widgetId, newPosition, newSize);
                }

                // Update widget properties;
                widget.Position = newPosition;
                widget.Size = newSize;

                // Update UI element;
                await UpdateWidgetUIAsync(widget);

                // Save layout;
                await SaveWidgetLayoutAsync(widget);

                // Trigger event;
                OnLayoutChanged(widget, WidgetLayoutChangeType.PositionAndSize);

                _logger.LogDebug("Widget layout updated: {WidgetId}", widgetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update widget layout: {WidgetId}", widgetId);
                throw;
            }
        }

        /// <summary>
        /// Shows or hides a widget;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <param name="visible">True to show, false to hide</param>
        public async Task SetWidgetVisibilityAsync(string widgetId, bool visible)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget))
            {
                throw new WidgetNotFoundException($"Widget not found: {widgetId}");
            }

            _logger.LogDebug("Setting visibility for widget {WidgetId} to {Visibility}", widgetId, visible);

            try
            {
                widget.IsVisible = visible;

                // Update UI visibility;
                await UpdateWidgetVisibilityUIAsync(widget);

                // Trigger event;
                OnVisibilityChanged(widget, visible);

                // Update performance monitoring;
                if (!visible)
                {
                    SuspendWidgetMonitoring(widget);
                }
                else;
                {
                    ResumeWidgetMonitoring(widget);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set widget visibility: {WidgetId}", widgetId);
                throw;
            }
        }

        /// <summary>
        /// Docks a widget to specified dock area;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <param name="dockArea">Dock area</param>
        public async Task DockWidgetAsync(string widgetId, DockArea dockArea)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget))
            {
                throw new WidgetNotFoundException($"Widget not found: {widgetId}");
            }

            _logger.LogInformation("Docking widget {WidgetId} to {DockArea}", widgetId, dockArea);

            try
            {
                widget.DockArea = dockArea;
                widget.IsDocked = true;

                // Remove from canvas and add to dock panel;
                await MoveWidgetToDockPanelAsync(widget, dockArea);

                // Adjust dock panel layout;
                await UpdateDockPanelLayoutAsync();

                // Save dock state;
                await SaveWidgetDockStateAsync(widget);

                // Trigger event;
                OnStateChanged(widget, WidgetStateChangeType.Docked);

                _logger.LogDebug("Widget docked successfully: {WidgetId}", widgetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dock widget: {WidgetId}", widgetId);
                throw;
            }
        }

        /// <summary>
        /// Undocks a widget, making it float;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        public async Task UndockWidgetAsync(string widgetId)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget))
            {
                throw new WidgetNotFoundException($"Widget not found: {widgetId}");
            }

            _logger.LogInformation("Undocking widget: {WidgetId}", widgetId);

            try
            {
                widget.IsDocked = false;

                // Remove from dock panel and add to canvas;
                await MoveWidgetToCanvasAsync(widget);

                // Adjust dock panel layout;
                await UpdateDockPanelLayoutAsync();

                // Save dock state;
                await SaveWidgetDockStateAsync(widget);

                // Trigger event;
                OnStateChanged(widget, WidgetStateChangeType.Undocked);

                _logger.LogDebug("Widget undocked successfully: {WidgetId}", widgetId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to undock widget: {WidgetId}", widgetId);
                throw;
            }
        }

        /// <summary>
        /// Groups multiple widgets together;
        /// </summary>
        /// <param name="widgetIds">Widget IDs to group</param>
        /// <param name="groupName">Name of the group</param>
        public async Task GroupWidgetsAsync(IEnumerable<string> widgetIds, string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                throw new ArgumentException("Group name cannot be empty", nameof(groupName));

            var widgets = new List<WidgetInstance>();

            foreach (var widgetId in widgetIds)
            {
                if (!_widgets.TryGetValue(widgetId, out var widget))
                {
                    throw new WidgetNotFoundException($"Widget not found: {widgetId}");
                }
                widgets.Add(widget);
            }

            _logger.LogInformation("Grouping {Count} widgets into group: {GroupName}", widgets.Count, groupName);

            try
            {
                // Create or get group;
                var group = GetOrCreateGroup(groupName);

                // Add widgets to group;
                foreach (var widget in widgets)
                {
                    // Remove from old group if any;
                    if (!string.IsNullOrEmpty(widget.GroupName))
                    {
                        if (_widgetGroups.TryGetValue(widget.GroupName, out var oldGroup))
                        {
                            oldGroup.Widgets.Remove(widget);

                            // Remove empty old group;
                            if (oldGroup.Widgets.Count == 0)
                            {
                                _widgetGroups.Remove(widget.GroupName);
                            }
                        }
                    }

                    // Add to new group;
                    widget.GroupName = groupName;
                    group.Widgets.Add(widget);
                }

                // Apply group layout;
                await ApplyGroupLayoutAsync(group);

                // Save group configuration;
                await SaveGroupConfigurationAsync(group);

                _logger.LogDebug("Widgets grouped successfully into: {GroupName}", groupName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to group widgets");
                throw;
            }
        }

        /// <summary>
        /// Ungroups widgets, making them independent;
        /// </summary>
        /// <param name="widgetIds">Widget IDs to ungroup</param>
        public async Task UngroupWidgetsAsync(IEnumerable<string> widgetIds)
        {
            var widgets = new List<WidgetInstance>();

            foreach (var widgetId in widgetIds)
            {
                if (!_widgets.TryGetValue(widgetId, out var widget))
                {
                    throw new WidgetNotFoundException($"Widget not found: {widgetId}");
                }
                widgets.Add(widget);
            }

            _logger.LogInformation("Ungrouping {Count} widgets", widgets.Count);

            try
            {
                foreach (var widget in widgets)
                {
                    if (!string.IsNullOrEmpty(widget.GroupName))
                    {
                        if (_widgetGroups.TryGetValue(widget.GroupName, out var group))
                        {
                            group.Widgets.Remove(widget);

                            // Remove empty group;
                            if (group.Widgets.Count == 0)
                            {
                                _widgetGroups.Remove(widget.GroupName);
                            }
                        }

                        widget.GroupName = null;
                    }
                }

                _logger.LogDebug("Widgets ungrouped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ungroup widgets");
                throw;
            }
        }

        /// <summary>
        /// Applies a theme to all widgets;
        /// </summary>
        /// <param name="theme">Theme to apply</param>
        public async Task ApplyThemeAsync(UITheme theme)
        {
            _logger.LogInformation("Applying theme: {ThemeName}", theme.Name);

            try
            {
                lock (_syncLock)
                {
                    foreach (var widget in _widgets.Values)
                    {
                        ApplyThemeToWidget(widget, theme);
                    }
                }

                // Update theme manager;
                await _themeManager.ApplyThemeAsync(theme);

                // Save theme preference;
                await SaveThemePreferenceAsync(theme);

                _logger.LogDebug("Theme applied successfully: {ThemeName}", theme.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply theme: {ThemeName}", theme.Name);
                throw;
            }
        }

        /// <summary>
        /// Saves current widget layout;
        /// </summary>
        /// <param name="layoutName">Name for the layout</param>
        public async Task SaveLayoutAsync(string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
                throw new ArgumentException("Layout name cannot be empty", nameof(layoutName));

            _logger.LogInformation("Saving widget layout: {LayoutName}", layoutName);

            try
            {
                var layout = new WidgetLayout;
                {
                    Name = layoutName,
                    CreatedAt = DateTime.UtcNow,
                    WidgetStates = new Dictionary<string, WidgetState>()
                };

                lock (_syncLock)
                {
                    foreach (var widget in _widgets.Values)
                    {
                        layout.WidgetStates[widget.Id] = new WidgetState;
                        {
                            Position = widget.Position,
                            Size = widget.Size,
                            IsVisible = widget.IsVisible,
                            IsDocked = widget.IsDocked,
                            DockArea = widget.DockArea,
                            ZIndex = widget.ZIndex;
                        };
                    }

                    _widgetLayouts[layoutName] = layout;
                }

                // Serialize and save to storage;
                await SerializeAndSaveLayoutAsync(layout);

                _logger.LogDebug("Layout saved successfully: {LayoutName}", layoutName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save layout: {LayoutName}", layoutName);
                throw new WidgetLayoutException($"Failed to save layout: {layoutName}", ex);
            }
        }

        /// <summary>
        /// Loads a saved widget layout;
        /// </summary>
        /// <param name="layoutName">Name of layout to load</param>
        public async Task LoadLayoutAsync(string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
                throw new ArgumentException("Layout name cannot be empty", nameof(layoutName));

            _logger.LogInformation("Loading widget layout: {LayoutName}", layoutName);

            try
            {
                // Load layout from storage;
                var layout = await LoadAndDeserializeLayoutAsync(layoutName);

                if (layout == null)
                {
                    throw new WidgetLayoutNotFoundException($"Layout not found: {layoutName}");
                }

                lock (_syncLock)
                {
                    // Clear current layout;
                    ClearCurrentLayout();

                    // Apply loaded layout;
                    foreach (var kvp in layout.WidgetStates)
                    {
                        if (_widgets.TryGetValue(kvp.Key, out var widget))
                        {
                            ApplyWidgetState(widget, kvp.Value);
                        }
                    }

                    // Update collections;
                    _widgetLayouts[layoutName] = layout;
                }

                // Update UI;
                await UpdateUIForLoadedLayoutAsync();

                _logger.LogDebug("Layout loaded successfully: {LayoutName}", layoutName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load layout: {LayoutName}", layoutName);
                throw;
            }
        }

        /// <summary>
        /// Optimizes widget performance based on current system metrics;
        /// </summary>
        public async Task OptimizePerformanceAsync()
        {
            _logger.LogInformation("Optimizing widget performance");

            try
            {
                // Get system performance metrics;
                var metrics = await _performanceMonitor.GetCurrentMetricsAsync();
                var health = await _systemHealth.GetSystemHealthAsync();

                // Determine optimization strategy;
                var strategy = DetermineOptimizationStrategy(metrics, health);

                // Apply optimizations;
                await ApplyOptimizationsAsync(strategy);

                // Update widget LODs;
                await UpdateWidgetLODsAsync();

                // Disable non-essential widgets if needed;
                if (strategy.DisableNonEssentialWidgets)
                {
                    await DisableNonEssentialWidgetsAsync();
                }

                _logger.LogDebug("Performance optimization completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Performance optimization failed");
                throw;
            }
        }

        /// <summary>
        /// Gets all widgets of specified type;
        /// </summary>
        /// <param name="widgetType">Type of widget</param>
        public IEnumerable<WidgetInstance> GetWidgetsByType(WidgetType widgetType)
        {
            lock (_syncLock)
            {
                return _widgets.Values.Where(w => w.Type == widgetType).ToList();
            }
        }

        /// <summary>
        /// Gets widgets in specified group;
        /// </summary>
        /// <param name="groupName">Group name</param>
        public IEnumerable<WidgetInstance> GetWidgetsByGroup(string groupName)
        {
            if (_widgetGroups.TryGetValue(groupName, out var group))
            {
                return group.Widgets.ToList();
            }

            return Enumerable.Empty<WidgetInstance>();
        }

        /// <summary>
        /// Gets widget by ID;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        public WidgetInstance GetWidget(string widgetId)
        {
            lock (_syncLock)
            {
                _widgets.TryGetValue(widgetId, out var widget);
                return widget;
            }
        }

        /// <summary>
        /// Gets all available widget layouts;
        /// </summary>
        public IEnumerable<string> GetAvailableLayouts()
        {
            lock (_syncLock)
            {
                return _widgetLayouts.Keys.ToList();
            }
        }

        /// <summary>
        /// Gets performance statistics for all widgets;
        /// </summary>
        public async Task<WidgetPerformanceReport> GetPerformanceReportAsync()
        {
            var report = new WidgetPerformanceReport;
            {
                GeneratedAt = DateTime.UtcNow,
                WidgetMetrics = new List<WidgetMetric>()
            };

            lock (_syncLock)
            {
                foreach (var widget in _widgets.Values)
                {
                    var metric = new WidgetMetric;
                    {
                        WidgetId = widget.Id,
                        WidgetType = widget.Type,
                        RenderTime = widget.RenderTime,
                        UpdateTime = widget.UpdateTime,
                        MemoryUsage = widget.MemoryUsage,
                        IsVisible = widget.IsVisible,
                        IsDocked = widget.IsDocked;
                    };

                    report.WidgetMetrics.Add(metric);
                }

                report.TotalWidgets = _widgets.Count;
                report.VisibleWidgets = _widgets.Values.Count(w => w.IsVisible);
                report.DockedWidgets = _widgets.Values.Count(w => w.IsDocked);
            }

            // Add system metrics;
            report.SystemMetrics = await _performanceMonitor.GetCurrentMetricsAsync();

            return report;
        }

        /// <summary>
        /// AI-driven widget arrangement based on user behavior and preferences;
        /// </summary>
        public async Task ArrangeWidgetsIntelligentlyAsync()
        {
            _logger.LogInformation("Performing intelligent widget arrangement");

            try
            {
                // Analyze user behavior patterns;
                var userBehavior = await AnalyzeUserBehaviorAsync();

                // Get user preferences from ML model;
                var preferences = await _mlModel.PredictWidgetPreferencesAsync(userBehavior);

                // Generate optimal layout;
                var optimalLayout = GenerateOptimalLayout(preferences);

                // Apply the layout;
                await ApplyIntelligentLayoutAsync(optimalLayout);

                // Learn from user adjustments;
                await LearnFromUserAdjustmentsAsync();

                _logger.LogDebug("Intelligent widget arrangement completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Intelligent widget arrangement failed");
                throw;
            }
        }

        /// <summary>
        /// Cleans up resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private async Task<WidgetInstance> CreateWidgetInstanceAsync(WidgetTemplate template, WidgetCreationParameters parameters)
        {
            var widget = new WidgetInstance;
            {
                Id = parameters.WidgetId,
                Name = parameters.Name,
                Type = template.Type,
                GroupName = parameters.GroupName,
                MinWidth = template.MinWidth,
                MinHeight = template.MinHeight,
                MaxWidth = template.MaxWidth,
                MaxHeight = template.MaxHeight,
                DefaultWidth = template.DefaultWidth,
                DefaultHeight = template.DefaultHeight,
                CanResize = template.CanResize,
                CanDrag = template.CanDrag,
                CanDock = template.CanDock,
                CreatedAt = DateTime.UtcNow,
                ZIndex = CalculateNextZIndex()
            };

            // Create UI element;
            widget.UIElement = await CreateWidgetUIElementAsync(template, parameters);

            // Initialize data context if provided;
            if (parameters.DataContext != null)
            {
                widget.UIElement.DataContext = parameters.DataContext;
            }

            // Set event handlers;
            AttachEventHandlers(widget);

            return widget;
        }

        private async Task<FrameworkElement> CreateWidgetUIElementAsync(WidgetTemplate template, WidgetCreationParameters parameters)
        {
            // Load XAML template;
            var xamlContent = await LoadXamlTemplateAsync(template.XamlPath);

            // Parse XAML;
            var element = XamlParser.Parse(xamlContent);

            // Apply parameters;
            ApplyCreationParameters(element, parameters);

            // Set visual properties;
            element.Width = template.DefaultWidth;
            element.Height = template.DefaultHeight;
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.VerticalAlignment = VerticalAlignment.Top;

            return element;
        }

        private async Task AddWidgetToUIAsync(WidgetInstance widget)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (widget.IsDocked && _dockPanel != null)
                {
                    // Add to dock panel;
                    DockPanel.SetDock(widget.UIElement, widget.DockArea.ToWindowsDock());
                    _dockPanel.Children.Add(widget.UIElement);
                }
                else;
                {
                    // Add to canvas;
                    Canvas.SetLeft(widget.UIElement, widget.Position.X);
                    Canvas.SetTop(widget.UIElement, widget.Position.Y);
                    _mainCanvas.Children.Add(widget.UIElement);
                }

                // Set z-index;
                Panel.SetZIndex(widget.UIElement, widget.ZIndex);
            });
        }

        private async Task RemoveWidgetFromUIAsync(WidgetInstance widget)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (widget.IsDocked && _dockPanel != null)
                {
                    _dockPanel.Children.Remove(widget.UIElement);
                }
                else;
                {
                    _mainCanvas.Children.Remove(widget.UIElement);
                }
            });
        }

        private async Task UpdateWidgetUIAsync(WidgetInstance widget)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!widget.IsDocked)
                {
                    Canvas.SetLeft(widget.UIElement, widget.Position.X);
                    Canvas.SetTop(widget.UIElement, widget.Position.Y);
                }

                widget.UIElement.Width = widget.Size.Width;
                widget.UIElement.Height = widget.Size.Height;
                Panel.SetZIndex(widget.UIElement, widget.ZIndex);
            });
        }

        private async Task UpdateWidgetVisibilityUIAsync(WidgetInstance widget)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                widget.UIElement.Visibility = widget.IsVisible;
                    ? Visibility.Visible;
                    : Visibility.Collapsed;
            });
        }

        private async Task MoveWidgetToDockPanelAsync(WidgetInstance widget, DockArea dockArea)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                // Remove from current parent;
                var currentParent = widget.UIElement.Parent as Panel;
                currentParent?.Children.Remove(widget.UIElement);

                // Add to dock panel;
                DockPanel.SetDock(widget.UIElement, dockArea.ToWindowsDock());
                _dockPanel.Children.Add(widget.UIElement);

                // Reorder dock panel children;
                UpdateDockPanelChildOrder();
            });
        }

        private async Task MoveWidgetToCanvasAsync(WidgetInstance widget)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                // Remove from dock panel;
                _dockPanel.Children.Remove(widget.UIElement);

                // Add to canvas;
                Canvas.SetLeft(widget.UIElement, widget.Position.X);
                Canvas.SetTop(widget.UIElement, widget.Position.Y);
                _mainCanvas.Children.Add(widget.UIElement);
            });
        }

        private WidgetGroup GetOrCreateGroup(string groupName)
        {
            if (!_widgetGroups.TryGetValue(groupName, out var group))
            {
                group = new WidgetGroup;
                {
                    Name = groupName,
                    CreatedAt = DateTime.UtcNow,
                    Widgets = new List<WidgetInstance>()
                };
                _widgetGroups[groupName] = group;
            }

            return group;
        }

        private void InitializeTemplates()
        {
            // Load built-in widget templates;
            _widgetTemplates = WidgetTemplateRegistry.GetAllTemplates()
                .ToDictionary(t => t.Type.ToString(), t => t);

            // Load custom templates from configuration;
            LoadCustomTemplates();

            _logger.LogInformation("Loaded {Count} widget templates", _widgetTemplates.Count);
        }

        private void SubscribeToEvents()
        {
            _themeManager.ThemeChanged += OnThemeChanged;
            _performanceMonitor.PerformanceAlert += OnPerformanceAlert;
            _systemHealth.HealthStatusChanged += OnHealthStatusChanged;
        }

        private void UnsubscribeFromEvents()
        {
            _themeManager.ThemeChanged -= OnThemeChanged;
            _performanceMonitor.PerformanceAlert -= OnPerformanceAlert;
            _systemHealth.HealthStatusChanged -= OnHealthStatusChanged;
        }

        private void StartPerformanceMonitoring()
        {
            _ = Task.Run(async () =>
            {
                while (!_isDisposed)
                {
                    try
                    {
                        await MonitorWidgetPerformanceAsync();
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Performance monitoring error");
                    }
                }
            });
        }

        private async Task MonitorWidgetPerformanceAsync()
        {
            var metrics = new List<WidgetPerformanceMetric>();

            lock (_syncLock)
            {
                foreach (var widget in _widgets.Values.Where(w => w.IsVisible))
                {
                    var metric = new WidgetPerformanceMetric;
                    {
                        WidgetId = widget.Id,
                        Timestamp = DateTime.UtcNow,
                        RenderTime = widget.RenderTime,
                        UpdateTime = widget.UpdateTime,
                        MemoryUsage = widget.MemoryUsage,
                        IsDocked = widget.IsDocked;
                    };

                    metrics.Add(metric);
                }
            }

            // Analyze metrics;
            var analysis = await AnalyzePerformanceMetricsAsync(metrics);

            // Trigger event;
            OnPerformanceMetricsUpdated(analysis);

            // Take corrective actions if needed;
            if (analysis.RequiresOptimization)
            {
                await OptimizePerformanceAsync();
            }
        }

        private void RegisterWidgetForMonitoring(WidgetInstance widget)
        {
            widget.PerformanceMetricsUpdated += OnWidgetPerformanceMetricsUpdated;
        }

        private void UnregisterWidgetFromMonitoring(WidgetInstance widget)
        {
            widget.PerformanceMetricsUpdated -= OnWidgetPerformanceMetricsUpdated;
        }

        private void SuspendWidgetMonitoring(WidgetInstance widget)
        {
            widget.IsMonitoringSuspended = true;
        }

        private void ResumeWidgetMonitoring(WidgetInstance widget)
        {
            widget.IsMonitoringSuspended = false;
        }

        private Point CalculateOptimalPosition(WidgetInstance widget)
        {
            // Use AI model to determine optimal position based on:
            // 1. Existing widget positions;
            // 2. Screen resolution;
            // 3. User preferences;
            // 4. Widget type and frequency of use;

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // Start with default position based on widget type;
            var defaultPosition = GetDefaultPositionForType(widget.Type);

            // Adjust for existing widgets;
            return AdjustPositionForCollisions(defaultPosition, widget.Size);
        }

        private bool CheckWidgetCollision(string widgetId, Point position, Size size)
        {
            var widgetRect = new Rect(position, size);

            lock (_syncLock)
            {
                foreach (var widget in _widgets.Values)
                {
                    if (widget.Id == widgetId) continue;

                    var otherRect = new Rect(widget.Position, widget.Size);
                    if (widgetRect.IntersectsWith(otherRect))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private Point ResolveCollision(string widgetId, Point position, Size size)
        {
            // Simple collision resolution - move right and down;
            var newPosition = position;
            var attempts = 0;
            const int maxAttempts = 10;

            while (CheckWidgetCollision(widgetId, newPosition, size) && attempts < maxAttempts)
            {
                newPosition = new Point(
                    newPosition.X + 20,
                    newPosition.Y + 20;
                );
                attempts++;
            }

            return newPosition;
        }

        private void ApplyThemeToWidget(WidgetInstance widget, UITheme theme = null)
        {
            theme ??= _themeManager.CurrentTheme;

            if (theme == null) return;

            // Apply theme colors;
            if (theme.Colors.TryGetValue("WidgetBackground", out var background))
            {
                widget.UIElement.SetValue(Control.BackgroundProperty, background);
            }

            if (theme.Colors.TryGetValue("WidgetForeground", out var foreground))
            {
                widget.UIElement.SetValue(Control.ForegroundProperty, foreground);
            }

            // Apply theme styles;
            var style = theme.GetStyleForWidgetType(widget.Type);
            if (style != null)
            {
                widget.UIElement.Style = style;
            }
        }

        private async Task SaveWidgetStateAsync(WidgetInstance widget)
        {
            var state = new WidgetState;
            {
                Position = widget.Position,
                Size = widget.Size,
                IsVisible = widget.IsVisible,
                IsDocked = widget.IsDocked,
                DockArea = widget.DockArea,
                ZIndex = widget.ZIndex,
                DataContext = widget.UIElement.DataContext;
            };

            await WidgetStatePersister.SaveStateAsync(widget.Id, state);
        }

        private async Task LoadSavedLayout()
        {
            try
            {
                var layoutName = await WidgetLayoutPersister.GetSavedLayoutNameAsync();
                if (!string.IsNullOrEmpty(layoutName))
                {
                    await LoadLayoutAsync(layoutName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load saved layout");
            }
        }

        private void OnWidgetAdded(WidgetInstance widget)
        {
            WidgetAdded?.Invoke(this, new WidgetAddedEventArgs;
            {
                Widget = widget,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnWidgetRemoved(WidgetInstance widget)
        {
            WidgetRemoved?.Invoke(this, new WidgetRemovedEventArgs;
            {
                WidgetId = widget.Id,
                WidgetType = widget.Type,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnLayoutChanged(WidgetInstance widget, WidgetLayoutChangeType changeType)
        {
            LayoutChanged?.Invoke(this, new WidgetLayoutChangedEventArgs;
            {
                WidgetId = widget.Id,
                ChangeType = changeType,
                NewPosition = widget.Position,
                NewSize = widget.Size,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnPerformanceMetricsUpdated(WidgetPerformanceAnalysis analysis)
        {
            PerformanceMetricsUpdated?.Invoke(this, new WidgetPerformanceEventArgs;
            {
                Analysis = analysis,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnVisibilityChanged(WidgetInstance widget, bool visible)
        {
            VisibilityChanged?.Invoke(this, new WidgetVisibilityChangedEventArgs;
            {
                WidgetId = widget.Id,
                IsVisible = visible,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnStateChanged(WidgetInstance widget, WidgetStateChangeType changeType)
        {
            StateChanged?.Invoke(this, new WidgetStateChangedEventArgs;
            {
                WidgetId = widget.Id,
                ChangeType = changeType,
                IsDocked = widget.IsDocked,
                DockArea = widget.DockArea,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnThemeChanged(object sender, ThemeChangedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                await ApplyThemeAsync(e.NewTheme);
            });
        }

        private void OnPerformanceAlert(object sender, PerformanceAlertEventArgs e)
        {
            if (e.AlertLevel >= AlertLevel.Warning)
            {
                _ = Task.Run(async () =>
                {
                    await OptimizePerformanceAsync();
                });
            }
        }

        private void OnHealthStatusChanged(object sender, HealthStatusChangedEventArgs e)
        {
            if (e.NewStatus == HealthStatus.Degraded || e.NewStatus == HealthStatus.Critical)
            {
                _ = Task.Run(async () =>
                {
                    await OptimizePerformanceAsync();
                });
            }
        }

        private void OnWidgetPerformanceMetricsUpdated(object sender, WidgetPerformanceMetricEventArgs e)
        {
            // Update widget metrics;
            if (sender is WidgetInstance widget)
            {
                widget.RenderTime = e.Metric.RenderTime;
                widget.UpdateTime = e.Metric.UpdateTime;
                widget.MemoryUsage = e.Metric.MemoryUsage;
            }
        }

        private void ClearCurrentLayout()
        {
            // Remove all widgets from UI;
            foreach (var widget in _widgets.Values)
            {
                Dispatcher.Invoke(() =>
                {
                    var parent = widget.UIElement.Parent as Panel;
                    parent?.Children.Remove(widget.UIElement);
                });
            }
        }

        private void ApplyWidgetState(WidgetInstance widget, WidgetState state)
        {
            widget.Position = state.Position;
            widget.Size = state.Size;
            widget.IsVisible = state.IsVisible;
            widget.IsDocked = state.IsDocked;
            widget.DockArea = state.DockArea;
            widget.ZIndex = state.ZIndex;

            if (state.DataContext != null)
            {
                widget.UIElement.DataContext = state.DataContext;
            }
        }

        private async Task UpdateUIForLoadedLayoutAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var widget in _widgets.Values)
                {
                    if (widget.IsDocked)
                    {
                        MoveWidgetToDockPanelAsync(widget, widget.DockArea).Wait();
                    }
                    else;
                    {
                        MoveWidgetToCanvasAsync(widget).Wait();
                    }

                    UpdateWidgetVisibilityUIAsync(widget).Wait();
                    UpdateWidgetUIAsync(widget).Wait();
                }

                UpdateDockPanelLayoutAsync().Wait();
            });
        }

        private void AttachEventHandlers(WidgetInstance widget)
        {
            widget.UIElement.MouseLeftButtonDown += (s, e) => OnWidgetMouseDown(widget, e);
            widget.UIElement.MouseMove += (s, e) => OnWidgetMouseMove(widget, e);
            widget.UIElement.MouseLeftButtonUp += (s, e) => OnWidgetMouseUp(widget, e);
            widget.UIElement.SizeChanged += (s, e) => OnWidgetSizeChanged(widget, e);
        }

        private void OnWidgetMouseDown(WidgetInstance widget, MouseButtonEventArgs e)
        {
            if (widget.CanDrag)
            {
                widget.IsDragging = true;
                widget.DragStartPosition = e.GetPosition(_mainCanvas);
                widget.UIElement.CaptureMouse();
            }
        }

        private void OnWidgetMouseMove(WidgetInstance widget, MouseEventArgs e)
        {
            if (widget.IsDragging)
            {
                var currentPosition = e.GetPosition(_mainCanvas);
                var delta = currentPosition - widget.DragStartPosition;

                var newPosition = new Point(
                    widget.Position.X + delta.X,
                    widget.Position.Y + delta.Y;
                );

                UpdateWidgetLayoutAsync(widget.Id, newPosition, widget.Size).Wait();
            }
        }

        private void OnWidgetMouseUp(WidgetInstance widget, MouseButtonEventArgs e)
        {
            if (widget.IsDragging)
            {
                widget.IsDragging = false;
                widget.UIElement.ReleaseMouseCapture();
            }
        }

        private void OnWidgetSizeChanged(WidgetInstance widget, SizeChangedEventArgs e)
        {
            widget.Size = e.NewSize;

            // Save new size;
            SaveWidgetStateAsync(widget).Wait();
        }

        private int CalculateNextZIndex()
        {
            lock (_syncLock)
            {
                if (_widgets.Count == 0) return 0;
                return _widgets.Values.Max(w => w.ZIndex) + 1;
            }
        }

        private WidgetTemplate GetWidgetTemplate(WidgetType widgetType)
        {
            var key = widgetType.ToString();
            if (_widgetTemplates.TryGetValue(key, out var template))
            {
                return template;
            }

            throw new WidgetTemplateNotFoundException($"Template for widget type {widgetType} not found");
        }

        private Point GetDefaultPositionForType(WidgetType widgetType)
        {
            return widgetType switch;
            {
                WidgetType.PerformanceMonitor => new Point(10, 10),
                WidgetType.ResourceMonitor => new Point(10, 120),
                WidgetType.TaskList => new Point(10, 230),
                WidgetType.Chat => new Point(SystemParameters.PrimaryScreenWidth - 410, 10),
                WidgetType.NotificationPanel => new Point(SystemParameters.PrimaryScreenWidth - 210, 10),
                _ => new Point(100, 100)
            };
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    UnsubscribeFromEvents();

                    foreach (var widget in _widgets.Values)
                    {
                        widget.Dispose();
                    }

                    _widgets.Clear();
                    _widgetGroups.Clear();
                    _widgetLayouts.Clear();
                    _widgetTemplates.Clear();
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Helper Classes;

        private class WidgetGroup;
        {
            public string Name { get; set; }
            public List<WidgetInstance> Widgets { get; set; }
            public DateTime CreatedAt { get; set; }
            public WidgetLayout GroupLayout { get; set; }
        }

        private class WidgetInstance : IDisposable
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public WidgetType Type { get; set; }
            public string GroupName { get; set; }
            public FrameworkElement UIElement { get; set; }
            public Point Position { get; set; }
            public Size Size { get; set; }
            public double MinWidth { get; set; }
            public double MinHeight { get; set; }
            public double MaxWidth { get; set; }
            public double MaxHeight { get; set; }
            public bool CanResize { get; set; }
            public bool CanDrag { get; set; }
            public bool CanDock { get; set; }
            public bool IsVisible { get; set; } = true;
            public bool IsDocked { get; set; }
            public DockArea DockArea { get; set; }
            public int ZIndex { get; set; }
            public DateTime CreatedAt { get; set; }

            // Performance metrics;
            public TimeSpan RenderTime { get; set; }
            public TimeSpan UpdateTime { get; set; }
            public long MemoryUsage { get; set; }

            // Interaction state;
            public bool IsDragging { get; set; }
            public Point DragStartPosition { get; set; }
            public bool IsMonitoringSuspended { get; set; }

            public event EventHandler<WidgetPerformanceMetricEventArgs> PerformanceMetricsUpdated;

            public void Dispose()
            {
                UIElement?.DataContext = null;
                UIElement = null;
            }
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Widget creation parameters;
    /// </summary>
    public class WidgetCreationParameters;
    {
        public string WidgetId { get; set; }
        public string Name { get; set; }
        public string GroupName { get; set; }
        public object DataContext { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    /// <summary>
    /// Widget layout definition;
    /// </summary>
    public class WidgetLayout;
    {
        public string Name { get; set; }
        public Dictionary<string, WidgetState> WidgetStates { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Widget state for serialization;
    /// </summary>
    public class WidgetState;
    {
        public Point Position { get; set; }
        public Size Size { get; set; }
        public bool IsVisible { get; set; }
        public bool IsDocked { get; set; }
        public DockArea DockArea { get; set; }
        public int ZIndex { get; set; }
        public object DataContext { get; set; }
    }

    /// <summary>
    /// Widget performance report;
    /// </summary>
    public class WidgetPerformanceReport;
    {
        public DateTime GeneratedAt { get; set; }
        public int TotalWidgets { get; set; }
        public int VisibleWidgets { get; set; }
        public int DockedWidgets { get; set; }
        public List<WidgetMetric> WidgetMetrics { get; set; }
        public PerformanceMetrics SystemMetrics { get; set; }
    }

    /// <summary>
    /// Widget performance analysis;
    /// </summary>
    public class WidgetPerformanceAnalysis;
    {
        public bool RequiresOptimization { get; set; }
        public List<string> ProblematicWidgets { get; set; }
        public OptimizationRecommendation[] Recommendations { get; set; }
        public PerformanceScore OverallScore { get; set; }
    }

    /// <summary>
    /// Widget type enumeration;
    /// </summary>
    public enum WidgetType;
    {
        PerformanceMonitor,
        ResourceMonitor,
        TaskList,
        Chat,
        NotificationPanel,
        SystemStatus,
        AIAssistant,
        MediaPlayer,
        FileBrowser,
        Custom;
    }

    /// <summary>
    /// Dock area enumeration;
    /// </summary>
    public enum DockArea;
    {
        Left,
        Right,
        Top,
        Bottom;
    }

    /// <summary>
    /// Widget layout change type;
    /// </summary>
    public enum WidgetLayoutChangeType;
    {
        Position,
        Size,
        PositionAndSize,
        ZIndex;
    }

    /// <summary>
    /// Widget state change type;
    /// </summary>
    public enum WidgetStateChangeType;
    {
        Docked,
        Undocked,
        Minimized,
        Maximized,
        Restored;
    }

    /// <summary>
    /// Widget added event arguments;
    /// </summary>
    public class WidgetAddedEventArgs : EventArgs;
    {
        public WidgetInstance Widget { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget removed event arguments;
    /// </summary>
    public class WidgetRemovedEventArgs : EventArgs;
    {
        public string WidgetId { get; set; }
        public WidgetType WidgetType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget layout changed event arguments;
    /// </summary>
    public class WidgetLayoutChangedEventArgs : EventArgs;
    {
        public string WidgetId { get; set; }
        public WidgetLayoutChangeType ChangeType { get; set; }
        public Point NewPosition { get; set; }
        public Size NewSize { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget performance event arguments;
    /// </summary>
    public class WidgetPerformanceEventArgs : EventArgs;
    {
        public WidgetPerformanceAnalysis Analysis { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget visibility changed event arguments;
    /// </summary>
    public class WidgetVisibilityChangedEventArgs : EventArgs;
    {
        public string WidgetId { get; set; }
        public bool IsVisible { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget state changed event arguments;
    /// </summary>
    public class WidgetStateChangedEventArgs : EventArgs;
    {
        public string WidgetId { get; set; }
        public WidgetStateChangeType ChangeType { get; set; }
        public bool IsDocked { get; set; }
        public DockArea DockArea { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget manager specific exception;
    /// </summary>
    public class WidgetManagerException : Exception
    {
        public WidgetManagerException(string message) : base(message) { }
        public WidgetManagerException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Widget creation specific exception;
    /// </summary>
    public class WidgetCreationException : WidgetManagerException;
    {
        public WidgetCreationException(string message) : base(message) { }
        public WidgetCreationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Widget layout specific exception;
    /// </summary>
    public class WidgetLayoutException : WidgetManagerException;
    {
        public WidgetLayoutException(string message) : base(message) { }
        public WidgetLayoutException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
